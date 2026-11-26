//! Diff: the value at a column becomes its change from the previous row with a value.

use super::par_chunks;
use super::sort::{group_rows, Grouped, Keys, SliceSpec};
use crate::cancel::{Cancel, OpResult};
use crate::operator::DiffOp;
use crate::stage::{DiffMeta, OutRows, Stage};
use crate::text::{as_str, char_len, cols_to_bytes, Span};
use crate::typed::{parse_date_time, write_aligned_to_slice, write_diff_value, write_iso_string, DiffProfile, ProfileSample, ValueType};
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

fn write_diff_strings(out: &mut String, current: &str, previous: &str) {
    let mut previous = previous.chars();
    for ch in current.chars() {
        out.push(if previous.next() == Some(ch) { ' ' } else { ch });
    }
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

struct ChunkOut {
    arena: Vec<u8>,
    changed: Vec<(u32, usize, u32)>,
    max_cols: u64,
    arena_non_ascii: bool,
    any_shrunk: bool,
}

struct Context<'a> {
    stage: &'a Stage,
    spans: &'a [Span],
    op: &'a DiffOp,
    spec: SliceSpec<'a>,
    grouped: &'a Grouped,
    profile: Option<DiffProfile>,
    range: (usize, usize),
}

impl Context<'_> {
    #[inline]
    fn line_of(&self, row: usize) -> &str {
        as_str(self.stage.span_bytes(self.spans[row]))
    }

    fn raw(&self, block: usize) -> &str {
        match &self.grouped.keys {
            Keys::Slices(slices) => as_str(self.stage.span_bytes(slices[block])),
            Keys::Numbers(_) => {
                let line = self.line_of(self.grouped.blocks[block].start as usize);
                self.spec.slice(line).map_or("", |(start, end)| &line[start..end])
            }
        }
    }

    fn number(&self, block: usize) -> f64 {
        match &self.grouped.keys {
            Keys::Numbers(numbers) => numbers[block],
            Keys::Slices(_) => 0.0,
        }
    }

    /// Writes the replacement for `block` into `out`; returns `false` when the row is unchanged.
    fn write_forward(&self, block: usize, out: &mut String, scratch: &mut String) -> bool {
        out.clear();
        match self.op.value_type {
            ValueType::String if block == 0 => return false,
            ValueType::String => write_diff_strings(out, self.raw(block), self.raw(block - 1)),
            ValueType::DateTime if block == 0 => return false,
            ValueType::DateTime => {
                let raw = self.raw(block);
                write_diff_value(out, scratch, self.number(block) - self.number(block - 1), ValueType::TimeSpan, raw, self.profile.as_ref());
            }
            value_type => {
                let raw = self.raw(block);
                let value = if block > 0 { self.number(block) - self.number(block - 1) } else { self.number(block) };
                write_diff_value(out, scratch, value, value_type, raw, self.profile.as_ref());
            }
        }
        true
    }

    /// Writes the first row of `block` with `replacement` in the output columns
    fn write(&self, block: usize, replacement: &str, out: &mut ChunkOut) {
        let row = self.grouped.blocks[block].start;
        let span = self.spans[row as usize];
        let line = as_str(self.stage.span_bytes(span));
        let (start, end) = cols_to_bytes(line, self.range.0, self.range.0 + self.range.1);
        if &line[start..end] == replacement {
            return;
        }
        let offset = out.arena.len();
        out.arena.extend_from_slice(&line.as_bytes()[..start]);
        out.arena.extend_from_slice(replacement.as_bytes());
        out.arena.extend_from_slice(&line.as_bytes()[end..]);
        let new_len = out.arena.len() - offset;
        let orig_ascii = self.stage.bufs[span.buf() as usize].ascii;
        // The buffer's flag covers all of its text: an ASCII row of a non-ASCII file stays ASCII
        let line_ascii = (orig_ascii && replacement.is_ascii()) || out.arena[offset..].is_ascii();
        if !line_ascii {
            out.arena_non_ascii = true;
        }
        let old_cols = if orig_ascii { line.len() as u64 } else { char_len(line.as_bytes()) as u64 };
        let new_cols = if line_ascii { new_len as u64 } else { char_len(&out.arena[offset..]) as u64 };
        if new_cols < old_cols {
            out.any_shrunk = true;
        }
        out.max_cols = out.max_cols.max(new_cols);
        out.changed.push((row, offset, new_len as u32));
    }
}

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
    let span_chunks = par_chunks(stage.len(), cancel, |range| {
        let mut chunk = Vec::with_capacity(range.len());
        stage.for_each_row(range, |_, span, _| chunk.push(span));
        Ok(chunk)
    })?;
    let mut spans = Vec::with_capacity(stage.len());
    for chunk in span_chunks {
        spans.extend_from_slice(&chunk);
    }
    let range = output_range(op);
    let chunks: Vec<ChunkOut> = {
        let mut context = Context { stage, spans: &spans, op, spec, grouped: &grouped, profile: None, range };
        // DateTime inputs never match TimeSpan profiles (and invert DateTime writes ISO dates), so only Number and TimeSpan build a column profile.
        if matches!(op.value_type, ValueType::Number | ValueType::TimeSpan) {
            context.profile = (0..grouped.blocks.len())
                .into_par_iter()
                .with_min_len(1 << 12)
                .map(|block| ProfileSample::of(context.raw(block), op.value_type))
                // Rayon reduces neighbours in order, so "the first value with decimals" stays the first
                .reduce(ProfileSample::default, ProfileSample::merge)
                .profile(op.value_type, range.1);
        }
        cancel.check()?;

        // Changed rows: (row, offset in the chunk's text, length)
        if !op.invert {
            par_chunks(grouped.blocks.len(), cancel, |blocks| {
                let mut out = ChunkOut { arena: Vec::new(), changed: Vec::new(), max_cols: 0, arena_non_ascii: false, any_shrunk: false };
                let mut replacement = String::with_capacity(64);
                let mut scratch = String::with_capacity(64);
                for block in blocks {
                    if context.write_forward(block, &mut replacement, &mut scratch) {
                        context.write(block, &replacement, &mut out);
                    }
                }
                Ok(out)
            })?
        } else {
            // A running total: every row needs the restored value of the one before
            let mut out = ChunkOut { arena: Vec::new(), changed: Vec::new(), max_cols: 0, arena_non_ascii: false, any_shrunk: false };
            let mut replacement = String::with_capacity(64);
            let mut scratch = String::with_capacity(64);
            let mut previous_text: Option<String> = None;
            let mut previous_value: Option<f64> = None;
            for block in 0..grouped.blocks.len() {
                if block % 4096 == 0 {
                    cancel.check()?;
                }
                let raw = context.raw(block);
                let mut has_replacement = false;
                match op.value_type {
                    ValueType::String => {
                        let restored = match &previous_text {
                            Some(previous) => invert_string_diff(raw, previous),
                            None => raw.to_string(),
                        };
                        if previous_text.is_some() {
                            replacement.clear();
                            replacement.push_str(&restored);
                            has_replacement = true;
                        }
                        previous_text = Some(restored);
                    }
                    value_type => match previous_value {
                        Some(previous) => {
                            let restored = context.number(block) + previous;
                            replacement.clear();
                            if value_type == ValueType::DateTime {
                                scratch.clear();
                                write_iso_string(&mut scratch, restored);
                                write_aligned_to_slice(&mut replacement, &scratch, raw, value_type);
                            } else {
                                write_diff_value(&mut replacement, &mut scratch, restored, value_type, raw, context.profile.as_ref());
                            }
                            has_replacement = true;
                            previous_value = Some(restored);
                        }
                        None => previous_value = if value_type == ValueType::DateTime { parse_date_time(raw) } else { Some(context.number(block)) },
                    },
                }
                if has_replacement {
                    context.write(block, &replacement, &mut out);
                }
            }
            vec![out]
        }
    };

    let arena_buf = stage.bufs.len() as u16;
    let total_arena: usize = chunks.iter().map(|chunk| chunk.arena.len()).sum();
    let arena_ascii = !chunks.iter().any(|chunk| chunk.arena_non_ascii);
    let any_shrunk = chunks.iter().any(|chunk| chunk.any_shrunk);
    let changed_max_cols = chunks.iter().map(|chunk| chunk.max_cols).max().unwrap_or(0);
    let max_cols = (!any_shrunk).then(|| stage.max_cols.max(changed_max_cols));
    let mut arena = Vec::new();
    arena.reserve_exact(total_arena);
    for chunk in chunks {
        let base = arena.len();
        arena.extend_from_slice(&chunk.arena);
        for (row, offset, length) in chunk.changed {
            spans[row as usize] = Span::new(arena_buf, (base + offset) as u64, length);
        }
    }
    let mut out = stage.derive(
        "Diff",
        OutRows { spans, origin: None, arena, max_cols, arena_resolved: true, arena_ascii: (total_arena > 0).then_some(arena_ascii) },
    );
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
    fn ascii_rows_written_from_a_file_with_other_letters_stay_ascii() {
        let stage = arc(&["a 1", "b 4", "ř"]);
        assert!(!stage.bufs[0].ascii);
        let out = apply(&stage, &diff(2, 1, ValueType::Number), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["a  1", "b  3", "ř"]);
        assert!(out.bufs.last().unwrap().ascii);
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
