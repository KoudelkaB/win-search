//! Sort: rows ordered by a value read from each row. A row without a readable value
//! travels with the row above it, so stack traces stay under their log line.

use super::par_chunks;
use crate::cancel::{Cancel, OpResult};
use crate::collate;
use crate::operator::{AnchorSide, SortMode, SortOp, SortOrder};
use crate::stage::Stage;
use crate::text::{as_str, cols_to_bytes, Span};
use crate::typed::{parse_typed, ValueType};
use memchr::memmem;
use rayon::prelude::*;
use std::cmp::Ordering;

/// Where the value of a row is.
pub(crate) struct SliceSpec<'a> {
    pub mode: SortMode,
    pub pos: usize,
    pub len: usize,
    pub anchor: &'a str,
    finder: Option<memmem::Finder<'a>>,
    pub side: AnchorSide,
    pub value_type: ValueType,
}

impl<'a> SliceSpec<'a> {
    pub fn new(mode: SortMode, pos: usize, len: usize, anchor: &'a str, side: AnchorSide, value_type: ValueType) -> Self {
        let finder = (mode == SortMode::Anchor && !anchor.is_empty()).then(|| memmem::Finder::new(anchor.as_bytes()));
        SliceSpec { mode, pos, len, anchor, finder, side, value_type }
    }

    pub fn positional(pos: u64, len: u64, value_type: ValueType) -> Self {
        SliceSpec::new(SortMode::Positional, pos as usize, len.max(1) as usize, "", AnchorSide::After, value_type)
    }

    /// Byte range of the value in `line`; `None` when the row has none
    #[inline]
    pub fn slice(&self, line: &str) -> Option<(usize, usize)> {
        if self.mode == SortMode::Anchor {
            let at = match &self.finder {
                Some(finder) => finder.find(line.as_bytes())?,
                None => 0,
            };
            if self.side == AnchorSide::Before {
                let prefix = line[..at].trim_end();
                let token = prefix.split_whitespace().next_back()?;
                let start = token.as_ptr() as usize - line.as_ptr() as usize;
                return Some((start, start + token.len()));
            }
            let after = at + self.anchor.len();
            let suffix = line[after..].trim_start();
            if suffix.is_empty() {
                return None;
            }
            let start = line.len() - suffix.len();
            if self.value_type == ValueType::String {
                return Some((start, line.len()));
            }
            let token = suffix.split_whitespace().next()?;
            return Some((start, start + token.len()));
        }
        let (start, end) = cols_to_bytes(line, self.pos, self.pos + self.len);
        (end > start).then_some((start, end))
    }
}

/// A row with a value and the rows without one that follow it.
#[derive(Clone, Copy)]
pub(crate) struct Block {
    pub start: u32,
    pub end: u32,
}

pub(crate) enum Keys {
    /// Per block
    Numbers(Vec<f64>),
    /// Span of the value in the first row of each block
    Slices(Vec<Span>),
}

pub(crate) struct Grouped {
    /// Rows before the first row with a value
    pub leading: usize,
    pub blocks: Vec<Block>,
    pub keys: Keys,
}

/// The value of every row, then the blocks they form.
pub(crate) fn group_rows(stage: &Stage, spec: &SliceSpec, invert_date_time: bool, cancel: &Cancel) -> OpResult<Grouped> {
    let rows = stage.len();
    let is_text = spec.value_type == ValueType::String;
    // (row, value) of the rows that have one, chunk by chunk
    let found = par_chunks(rows, cancel, |range| {
        let mut numbers = Vec::new();
        let mut slices = Vec::new();
        let mut starts = Vec::new();
        stage.for_each_row(range, |row, span, line_bytes| {
            let line = as_str(line_bytes);
            let Some((start, end)) = spec.slice(line) else { return };
            if is_text {
                slices.push(span.sub(start, end));
            } else {
                match parse_typed(&line[start..end], spec.value_type, invert_date_time) {
                    Some(value) => numbers.push(value),
                    None => return,
                }
            }
            starts.push(row as u32);
        });
        Ok((starts, numbers, slices))
    })?;
    let count: usize = found.iter().map(|(starts, _, _)| starts.len()).sum();
    let mut blocks = Vec::with_capacity(count);
    let mut numbers = Vec::with_capacity(if is_text { 0 } else { count });
    let mut slices = Vec::with_capacity(if is_text { count } else { 0 });
    for (starts, chunk_numbers, chunk_slices) in found {
        for start in starts {
            if let Some(previous) = blocks.last_mut() {
                let previous: &mut Block = previous;
                previous.end = start;
            }
            blocks.push(Block { start, end: rows as u32 });
        }
        numbers.extend(chunk_numbers);
        slices.extend(chunk_slices);
    }
    let leading = blocks.first().map_or(rows, |block| block.start as usize);
    Ok(Grouped { leading, blocks, keys: if is_text { Keys::Slices(slices) } else { Keys::Numbers(numbers) } })
}

/// Order-preserving bijection from finite `f64` (with `-0.0` mapped to `+0.0`) to `u64`.
#[inline]
fn f64_to_sortable_u64(value: f64) -> u64 {
    let bits = if value == 0.0 { 0u64 } else { value.to_bits() };
    if (bits >> 63) != 0 {
        !bits
    } else {
        bits ^ (1u64 << 63)
    }
}

pub fn apply(stage: &Stage, op: &SortOp, cancel: &Cancel) -> OpResult<Stage> {
    let spec = SliceSpec::new(op.mode, op.pos as usize, op.len.max(1) as usize, &op.anchor, op.anchor_side, op.value_type);
    let grouped = group_rows(stage, &spec, false, cancel)?;
    cancel.check()?;
    let descending = op.order == SortOrder::Desc;
    let directed = |ordering: Ordering| if descending { ordering.reverse() } else { ordering };
    let order: Vec<u32> = match grouped.keys {
        Keys::Numbers(numbers) => {
            let mut keyed: Vec<(u64, u32)> = numbers
                .into_par_iter()
                .enumerate()
                .map(|(block, value)| {
                    let key = f64_to_sortable_u64(value);
                    (if descending { !key } else { key }, block as u32)
                })
                .collect();
            keyed.par_sort_unstable();
            keyed.into_iter().map(|(_, block)| block).collect()
        }
        Keys::Slices(slices) => {
            let all_ascii = stage.bufs.iter().all(|buf| buf.ascii);
            if all_ascii {
                let mut keyed: Vec<(u64, u32)> = slices
                    .par_iter()
                    .enumerate()
                    .map(|(block, &span)| {
                        let key = collate::ascii_prefix_key(stage.span_bytes(span));
                        (if descending { !key } else { key }, block as u32)
                    })
                    .collect();
                keyed.par_sort_unstable_by(|&(lk, lb), &(rk, rb)| {
                    lk.cmp(&rk)
                        .then_with(|| directed(collate::compare_ascii(stage.span_bytes(slices[lb as usize]), stage.span_bytes(slices[rb as usize]))))
                        .then(lb.cmp(&rb))
                });
                keyed.into_iter().map(|(_, block)| block).collect()
            } else {
                let mut order: Vec<u32> = (0..slices.len() as u32).collect();
                order.par_sort_unstable_by(|&left, &right| {
                    let left_text = as_str(stage.span_bytes(slices[left as usize]));
                    let right_text = as_str(stage.span_bytes(slices[right as usize]));
                    directed(collate::compare(left_text, right_text)).then(left.cmp(&right))
                });
                order
            }
        }
    };
    cancel.check()?;
    let mut origin: Vec<u32> = Vec::with_capacity(stage.len());
    origin.extend(0..grouped.leading as u32);
    for block in order {
        let block = grouped.blocks[block as usize];
        origin.extend(block.start..block.end);
    }
    Ok(stage.derive_rows("Sort", origin, Some(stage.max_cols)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::load::{merge_append, Bundle};
    use crate::stage::testing::*;

    fn sort(pos: u64, len: u64, value_type: ValueType) -> SortOp {
        SortOp { mode: SortMode::Positional, pos, len, anchor: String::new(), anchor_side: AnchorSide::After, value_type, order: SortOrder::Asc }
    }

    #[test]
    fn number_sorting_keeps_continuation_lines_with_their_blocks_and_sources() {
        let (a, b) = (stage_named(&["2 first", "  detail"], "a"), stage_named(&["1 second"], "b"));
        let stage = merge_append(&[Bundle::of(&a), Bundle::of(&b)]).unwrap();
        let out = apply(&stage, &sort(0, 1, ValueType::Number), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["1 second", "2 first", "  detail"]);
        assert_eq!(srcs_of(&out), [1, 0, 0]);
        assert_eq!(row_ids_of(&out), [2, 0, 1]);
    }

    #[test]
    fn rows_before_the_first_value_stay_on_top_and_equal_values_keep_their_order() {
        let stage = stage_of(&["header", "b 1", "a 1", "b 2", "a 2"]);
        let out = apply(&stage, &sort(0, 1, ValueType::String), &Cancel::new()).unwrap();
        // "header" has a value too ("h") as text - with numbers it would lead
        assert_eq!(lines_of(&out), ["a 1", "a 2", "b 1", "b 2", "header"]);
        let numbers = stage_of(&["header", "2", "1"]);
        assert_eq!(lines_of(&apply(&numbers, &sort(0, 1, ValueType::Number), &Cancel::new()).unwrap()), ["header", "1", "2"]);
    }

    #[test]
    fn descending_reverses_values_but_not_ties() {
        let stage = stage_of(&["1 a", "2 b", "1 c"]);
        let mut op = sort(0, 1, ValueType::Number);
        op.order = SortOrder::Desc;
        assert_eq!(lines_of(&apply(&stage, &op, &Cancel::new()).unwrap()), ["2 b", "1 a", "1 c"]);
    }

    #[test]
    fn the_value_can_stand_next_to_a_text() {
        let stage = stage_of(&["GET status=500 x", "GET status=200 y", "no status"]);
        let mut op = sort(0, 1, ValueType::Number);
        op.mode = SortMode::Anchor;
        op.anchor = "status=".into();
        // "no status" has no value: it stays under the row it followed
        assert_eq!(lines_of(&apply(&stage, &op, &Cancel::new()).unwrap()), ["GET status=200 y", "no status", "GET status=500 x"]);
        let before = stage_of(&["took 30 ms", "took 4 ms"]);
        op.anchor = "ms".into();
        op.anchor_side = AnchorSide::Before;
        assert_eq!(lines_of(&apply(&before, &op, &Cancel::new()).unwrap()), ["took 4 ms", "took 30 ms"]);
    }

    #[test]
    fn durations_and_dates_sort_by_their_value() {
        let stage = stage_of(&["00:00:10.000", "00:00:02.500", "1.00:00:00.000"]);
        assert_eq!(lines_of(&apply(&stage, &sort(0, 14, ValueType::TimeSpan), &Cancel::new()).unwrap()), ["00:00:02.500", "00:00:10.000", "1.00:00:00.000"]);
        let dates = stage_of(&["13.3.2026 10:00", "2.1.2026 09:00", "1.12.2025 23:59"]);
        assert_eq!(lines_of(&apply(&dates, &sort(0, 20, ValueType::DateTime), &Cancel::new()).unwrap()), ["1.12.2025 23:59", "2.1.2026 09:00", "13.3.2026 10:00"]);
    }

    #[test]
    fn sortable_f64_keys_preserve_order_and_long_text_prefixes_resolve_ties() {
        let values = [f64::NEG_INFINITY, -1e100, -10.5, -1.0, -0.0, 0.0, 1e-300, 1.0, 10.5, 1e100, f64::INFINITY];
        for &a in &values {
            for &b in &values {
                assert_eq!(f64_to_sortable_u64(a).cmp(&f64_to_sortable_u64(b)), a.partial_cmp(&b).unwrap(), "a={a} b={b}");
            }
        }
        let stage = stage_of(&["prefix_00_B", "prefix_00_a", "prefix_00_A", "prefix_00_b", "prefix_00"]);
        let asc = apply(&stage, &sort(0, 20, ValueType::String), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&asc), ["prefix_00", "prefix_00_a", "prefix_00_A", "prefix_00_b", "prefix_00_B"]);
        let mut desc_op = sort(0, 20, ValueType::String);
        desc_op.order = SortOrder::Desc;
        let desc = apply(&stage, &desc_op, &Cancel::new()).unwrap();
        assert_eq!(lines_of(&desc), ["prefix_00_B", "prefix_00_b", "prefix_00_A", "prefix_00_a", "prefix_00"]);
    }
}
