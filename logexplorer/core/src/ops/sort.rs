//! Sort: rows ordered by a value read from each row. A row without a readable value
//! travels with the row above it, so stack traces stay under their log line.

use super::par_chunks;
use crate::cancel::{Cancel, OpResult};
use crate::collate;
use crate::operator::{AnchorSide, SortMode, SortOp, SortOrder};
use crate::stage::{OutRows, Stage};
use crate::text::cols_to_bytes;
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
    pub side: AnchorSide,
    pub value_type: ValueType,
}

impl<'a> SliceSpec<'a> {
    pub fn positional(pos: u64, len: u64, value_type: ValueType) -> Self {
        SliceSpec { mode: SortMode::Positional, pos: pos as usize, len: len.max(1) as usize, anchor: "", side: AnchorSide::After, value_type }
    }

    /// Byte range of the value in `line`; `None` when the row has none
    pub fn slice(&self, line: &str) -> Option<(usize, usize)> {
        if self.mode == SortMode::Anchor {
            let at = if self.anchor.is_empty() { 0 } else { memmem::find(line.as_bytes(), self.anchor.as_bytes())? };
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
    /// Byte range of the value in the first row of each block
    Slices(Vec<(u32, u32)>),
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
        for row in range {
            let line = stage.line(row);
            let Some((start, end)) = spec.slice(line) else { continue };
            if is_text {
                slices.push((start as u32, end as u32));
            } else {
                match parse_typed(&line[start..end], spec.value_type, invert_date_time) {
                    Some(value) => numbers.push(value),
                    None => continue,
                }
            }
            starts.push(row as u32);
        }
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

pub fn apply(stage: &Stage, op: &SortOp, cancel: &Cancel) -> OpResult<Stage> {
    let spec = SliceSpec { mode: op.mode, pos: op.pos as usize, len: op.len.max(1) as usize, anchor: &op.anchor, side: op.anchor_side, value_type: op.value_type };
    let grouped = group_rows(stage, &spec, false, cancel)?;
    cancel.check()?;
    let descending = op.order == SortOrder::Desc;
    let mut order: Vec<u32> = (0..grouped.blocks.len() as u32).collect();
    let directed = |ordering: Ordering| if descending { ordering.reverse() } else { ordering };
    match &grouped.keys {
        Keys::Numbers(numbers) => {
            order.par_sort_by(|&left, &right| directed(numbers[left as usize].partial_cmp(&numbers[right as usize]).unwrap_or(Ordering::Equal)).then(left.cmp(&right)))
        }
        Keys::Slices(slices) => {
            let text = |block: u32| {
                let (start, end) = slices[block as usize];
                &stage.line(grouped.blocks[block as usize].start as usize)[start as usize..end as usize]
            };
            order.par_sort_by(|&left, &right| directed(collate::compare(text(left), text(right))).then(left.cmp(&right)));
        }
    }
    cancel.check()?;
    let mut origin: Vec<u32> = Vec::with_capacity(stage.len());
    origin.extend(0..grouped.leading as u32);
    for block in order {
        let block = grouped.blocks[block as usize];
        origin.extend(block.start..block.end);
    }
    let spans = origin.par_iter().with_min_len(1 << 14).map(|&row| stage.span(row as usize)).collect();
    Ok(stage.derive("Sort", OutRows { spans, origin: Some(origin), arena: Vec::new() }))
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
}
