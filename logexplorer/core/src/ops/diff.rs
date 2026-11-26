//! Diff: the value at a column becomes its change from the previous row with a value.

use super::par_chunks;
use super::sort::{group_rows, Grouped, Keys, SliceSpec};
use crate::cancel::{Cancel, OpResult};
use crate::operator::DiffOp;
use crate::stage::{DiffMeta, OutRows, Stage};
use crate::text::{cols_to_bytes, Span};
use crate::typed::{align_to_slice, format_diff_value, iso_string, parse_date_time, DiffProfile, ProfileSample, ValueType};
use rayon::prelude::*;
use std::sync::Arc;

fn signature(op: &DiffOp) -> String {
    format!("{}|{}|{}|{}", op.pos, op.len, op.value_type.as_str(), op.output_extend.as_deref().unwrap_or(""))
}

/// Numbers and durations get one more column, for the sign
pub fn uses_expanded_output(value_type: ValueType) -> bool {
    matches!(value_type, ValueType::Number | ValueType::TimeSpan)
}

/// The columns the diff value is written to: `(pos, len)`
fn output_range(op: &DiffOp) -> (usize, usize) {
    let (pos, len) = (op.pos as usize, op.len.max(1) as usize);
    if !uses_expanded_output(op.value_type) {
        (pos, len)
    } else if op.output_extend.as_deref() == Some("left") && pos > 0 {
        (pos - 1, len + 1)
    } else {
        (pos, len + 1)
    }
}

fn diff_strings(current: &str, previous: &str) -> String {
    let mut previous = previous.chars();
    current.chars().map(|ch| if previous.next() == Some(ch) { ' ' } else { ch }).collect()
}

fn invert_string_diff(diff: &str, previous: &str) -> String {
    let mut previous = previous.chars();
    diff.chars()
        .map(|ch| {
            let before = previous.next();
            if ch == ' ' {
                before.unwrap_or(' ')
            } else {
                ch
            }
        })
        .collect()
}

struct Context<'a> {
    stage: &'a Stage,
    op: &'a DiffOp,
    spec: SliceSpec<'a>,
    grouped: &'a Grouped,
    profile: Option<DiffProfile>,
    range: (usize, usize),
}

impl Context<'_> {
    fn raw(&self, block: usize) -> &str {
        let line = self.stage.line(self.grouped.blocks[block].start as usize);
        match &self.grouped.keys {
            Keys::Slices(slices) => &line[slices[block].0 as usize..slices[block].1 as usize],
            Keys::Numbers(_) => self.spec.slice(line).map_or("", |(start, end)| &line[start..end]),
        }
    }

    fn number(&self, block: usize) -> f64 {
        match &self.grouped.keys {
            Keys::Numbers(numbers) => numbers[block],
            Keys::Slices(_) => 0.0,
        }
    }

    /// The text written in place of the value of `block` (not reversing)
    fn forward(&self, block: usize) -> String {
        let raw = self.raw(block);
        match self.op.value_type {
            ValueType::String if block > 0 => diff_strings(raw, self.raw(block - 1)),
            ValueType::String => raw.to_string(),
            ValueType::DateTime if block > 0 => format_diff_value(self.number(block) - self.number(block - 1), ValueType::TimeSpan, raw, self.profile.as_ref()),
            ValueType::DateTime => raw.to_string(),
            value_type => {
                let value = if block > 0 { self.number(block) - self.number(block - 1) } else { self.number(block) };
                format_diff_value(value, value_type, raw, self.profile.as_ref())
            }
        }
    }

    /// Writes the first row of `block` with `replacement` in the output columns
    fn write(&self, block: usize, replacement: &str, arena: &mut Vec<u8>) -> Option<(u32, usize, u32)> {
        let row = self.grouped.blocks[block].start;
        let line = self.stage.line(row as usize);
        let (start, end) = cols_to_bytes(line, self.range.0, self.range.0 + self.range.1);
        if &line[start..end] == replacement {
            return None;
        }
        let offset = arena.len();
        arena.extend_from_slice(&line.as_bytes()[..start]);
        arena.extend_from_slice(replacement.as_bytes());
        arena.extend_from_slice(&line.as_bytes()[end..]);
        Some((row, offset, (arena.len() - offset) as u32))
    }
}

/// The text of rewritten rows and, for each, `(row, offset in the text, length)`
type Rewritten = (Vec<u8>, Vec<(u32, usize, u32)>);

pub fn apply(stage: &Arc<Stage>, op: &DiffOp, cancel: &Cancel) -> OpResult<Stage> {
    let signature = signature(op);
    if op.invert {
        if let Some(meta) = stage.diff_meta.as_ref().filter(|meta| meta.signature == signature) {
            // Reverse right after the same Diff: its input, exactly
            return Ok(meta.original.copy_as("Diff restore"));
        }
    }
    let invert_date_time = op.invert && op.value_type == ValueType::DateTime;
    let spec = SliceSpec::positional(op.pos, op.len, op.value_type);
    let grouped = group_rows(stage, &spec, invert_date_time, cancel)?;
    let range = output_range(op);
    let profile_type = if op.value_type == ValueType::DateTime { ValueType::TimeSpan } else { op.value_type };
    let mut context = Context { stage, op, spec, grouped: &grouped, profile: None, range };
    if profile_type != ValueType::String {
        context.profile = (0..grouped.blocks.len())
            .into_par_iter()
            .with_min_len(1 << 12)
            .map(|block| ProfileSample::of(context.raw(block), profile_type))
            // Rayon reduces neighbours in order, so "the first value with decimals" stays the first
            .reduce(ProfileSample::default, ProfileSample::merge)
            .profile(profile_type, range.1);
    }
    cancel.check()?;

    // Changed rows: (row, offset in the chunk's text, length)
    let chunks: Vec<Rewritten> = if !op.invert {
        par_chunks(grouped.blocks.len(), cancel, |blocks| {
            let mut arena = Vec::new();
            let mut changed = Vec::new();
            for block in blocks {
                changed.extend(context.write(block, &context.forward(block), &mut arena));
            }
            Ok((arena, changed))
        })?
    } else {
        // A running total: every row needs the restored value of the one before
        let mut arena = Vec::new();
        let mut changed = Vec::new();
        let mut previous_text: Option<String> = None;
        let mut previous_value: Option<f64> = None;
        for block in 0..grouped.blocks.len() {
            if block % 4096 == 0 {
                cancel.check()?;
            }
            let raw = context.raw(block);
            let mut replacement = None;
            match op.value_type {
                ValueType::String => {
                    let restored = match &previous_text {
                        Some(previous) => invert_string_diff(raw, previous),
                        None => raw.to_string(),
                    };
                    replacement = previous_text.is_some().then(|| restored.clone());
                    previous_text = Some(restored);
                }
                value_type => match previous_value {
                    Some(previous) => {
                        let restored = context.number(block) + previous;
                        replacement = Some(if value_type == ValueType::DateTime {
                            align_to_slice(&iso_string(restored), raw, value_type)
                        } else {
                            format_diff_value(restored, value_type, raw, context.profile.as_ref())
                        });
                        previous_value = Some(restored);
                    }
                    None => previous_value = if value_type == ValueType::DateTime { parse_date_time(raw) } else { Some(context.number(block)) },
                },
            }
            if let Some(replacement) = replacement {
                changed.extend(context.write(block, &replacement, &mut arena));
            }
        }
        vec![(arena, changed)]
    };

    let mut spans: Vec<Span> = (0..stage.len()).into_par_iter().with_min_len(1 << 14).map(|row| stage.span(row)).collect();
    let arena_buf = stage.bufs.len() as u16;
    let mut arena = Vec::new();
    arena.reserve_exact(chunks.iter().map(|(text, _)| text.len()).sum());
    for (text, changed) in chunks {
        let base = arena.len();
        arena.extend_from_slice(&text);
        for (row, offset, length) in changed {
            spans[row as usize] = Span::new(arena_buf, (base + offset) as u64, length);
        }
    }
    let mut out = stage.derive("Diff", OutRows { spans, origin: None, arena, max_cols: None, arena_resolved: true, arena_ascii: None });
    out.diff_meta = Some(DiffMeta { signature, original: stage.clone() });
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::load::stage_from_text;
    use crate::stage::testing::*;

    fn diff(pos: u64, len: u64, value_type: ValueType) -> DiffOp {
        DiffOp { pos, len, value_type, invert: false, output_extend: None }
    }

    fn arc(lines: &[&str]) -> Arc<Stage> {
        Arc::new(stage_from_text(lines.join("\n"), "test", "test"))
    }

    #[test]
    fn timestamps_become_the_time_since_the_previous_row() {
        let stage = arc(&[
            "[4711] 2026-03-13T09:14:02.105Z START GET /api/orders",
            "[4711] 2026-03-13T09:14:02.248Z END   200 GET /api/orders",
            "    continuation",
            "[4712] 2026-03-13T09:14:03.031Z END   201 POST /api/payments",
        ]);
        let out = apply(&stage, &diff(7, 24, ValueType::DateTime), &Cancel::new()).unwrap();
        assert_eq!(
            lines_of(&out),
            [
                "[4711] 2026-03-13T09:14:02.105Z START GET /api/orders",
                "[4711]              00:00:00.143 END   200 GET /api/orders",
                "    continuation",
                "[4712]              00:00:00.783 END   201 POST /api/payments",
            ]
        );
        assert_eq!(row_ids_of(&out), [0, 1, 2, 3]);
        // Only the two changed rows were written; the rest is still the file's text
        assert_eq!(out.bufs.last().unwrap().len(), lines_of(&out)[1].len() + lines_of(&out)[3].len());
    }

    #[test]
    fn numbers_keep_their_decimals_and_get_a_column_for_the_sign() {
        let stage = arc(&["afr  95.6%", "afr  95.2%", "amh  48.6%"]);
        let out = apply(&stage, &diff(5, 4, ValueType::Number), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["afr   95.6", "afr   -0.4", "amh  -46.6"]);
        let mut left = diff(5, 4, ValueType::Number);
        left.output_extend = Some("left".into());
        assert_eq!(lines_of(&apply(&stage, &left, &Cancel::new()).unwrap()), ["afr  95.6%", "afr  -0.4%", "amh -46.6%"]);
    }

    #[test]
    fn text_keeps_only_the_changed_characters() {
        let stage = arc(&["id=abc1", "id=abd1", "id=abd2"]);
        let out = apply(&stage, &diff(3, 4, ValueType::String), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["id=abc1", "id=  d ", "id=   2"]);
    }

    #[test]
    fn reverse_right_after_the_same_diff_restores_the_input_exactly() {
        let stage = arc(&["a 1.50", "a 2.25", "note", "a 2.00"]);
        let op = diff(2, 4, ValueType::Number);
        let diffed = Arc::new(apply(&stage, &op, &Cancel::new()).unwrap());
        assert_eq!(lines_of(&diffed), ["a  1.50", "a  0.75", "note", "a -0.25"]);
        let restored = apply(&diffed, &DiffOp { invert: true, ..op.clone() }, &Cancel::new()).unwrap();
        assert_eq!(lines_of(&restored), lines_of(&stage));
        assert_eq!(restored.label, "Diff restore");
    }

    #[test]
    fn reverse_on_its_own_sums_the_differences_up() {
        let stage = arc(&["10", " 5", "-3"]);
        let out = apply(&stage, &DiffOp { invert: true, ..diff(0, 2, ValueType::Number) }, &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["10", " 15", " 12"]);
        let text = arc(&["abcd", "  x ", "y   "]);
        let out = apply(&text, &DiffOp { invert: true, ..diff(0, 4, ValueType::String) }, &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["abcd", "abxd", "ybxd"]);
    }
}
