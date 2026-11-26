//! Replace: literal replacement in the whole stage; the searched text and its replacement
//! may contain line breaks, so rows can be split and joined.

use super::{merge_chunks, par_chunks, Assembler};
use crate::cancel::{Cancel, OpResult};
use crate::operator::ReplaceOp;
use crate::stage::Stage;
use memchr::memmem;

pub fn apply(stage: &Stage, op: &ReplaceOp, cancel: &Cancel) -> OpResult<Stage> {
    if op.search.is_empty() {
        return Ok(stage.copy_as("Replace"));
    }
    let replacement = op.replacement.as_bytes();
    let splits_rows = op.replacement.contains('\n');
    if !op.search.contains('\n') {
        // A match cannot cross rows: every row on its own, in parallel
        let finder = memmem::Finder::new(op.search.as_bytes());
        let search_length = op.search.len();
        let chunks = par_chunks(stage.len(), cancel, |range| {
            let mut assembler = Assembler::with_origin(stage, splits_rows);
            stage.for_each_row(range, |row, span, line| {
                let mut copied = 0;
                for found in finder.find_iter(line) {
                    assembler.push_span(span.sub(copied, found), row as u32);
                    assembler.push_text(replacement, row as u32);
                    copied = found + search_length;
                }
                assembler.push_span(span.sub(copied, line.len()), row as u32);
                assembler.end_row(row as u32);
            });
            assembler.finish()
        })?;
        return Ok(stage.derive("Replace", merge_chunks(chunks, splits_rows, stage.bufs.len() as u16)?));
    }

    // The search spans rows: its first line ends a row, its last line starts one, and the
    // lines between are whole rows
    let parts: Vec<&[u8]> = op.search.split('\n').map(str::as_bytes).collect();
    let (first, last, middle) = (parts[0], parts[parts.len() - 1], &parts[1..parts.len() - 1]);
    let reach = parts.len() - 1;
    let rows = stage.len();
    let mut assembler = Assembler::with_origin(stage, true);
    let (mut row, mut from) = (0usize, 0usize);
    while row < rows {
        if row % 4096 == 0 {
            cancel.check()?;
        }
        let span = stage.span(row);
        let line = stage.span_bytes(span);
        let matches = row + reach < rows
            && line.len() >= from + first.len()
            && line.ends_with(first)
            && middle.iter().enumerate().all(|(index, part)| stage.line_bytes(row + 1 + index) == *part)
            && stage.line_bytes(row + reach).starts_with(last);
        if matches {
            assembler.push_span(span.sub(from, line.len() - first.len()), row as u32);
            assembler.push_text(replacement, row as u32);
            row += reach;
            from = last.len();
        } else {
            assembler.push_span(span.sub(from, line.len()), row as u32);
            assembler.end_row(row as u32);
            row += 1;
            from = 0;
        }
    }
    Ok(stage.derive("Replace", merge_chunks(vec![assembler.finish()?], true, stage.bufs.len() as u16)?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stage::testing::*;

    fn replace(stage: &Stage, search: &str, replacement: &str) -> Stage {
        apply(stage, &ReplaceOp { search: search.into(), replacement: replacement.into() }, &Cancel::new()).unwrap()
    }

    #[test]
    fn every_occurrence_is_replaced_and_untouched_rows_are_not_copied() {
        let stage = stage_of(&["id=4711 id=4712", "nothing here", "id="]);
        let out = replace(&stage, "id=", "#");
        assert_eq!(lines_of(&out), ["#4711 #4712", "nothing here", "#"]);
        assert_eq!(row_ids_of(&out), [0, 1, 2]);
        assert_eq!(out.bufs.last().unwrap().len(), "#4711 #4712".len() + 1);
        assert_eq!(lines_of(&replace(&stage, "", "x")), lines_of(&stage));
    }

    #[test]
    fn an_empty_replacement_deletes() {
        let stage = stage_of(&["a--b--c", "--"]);
        assert_eq!(lines_of(&replace(&stage, "--", "")), ["abc", ""]);
    }

    #[test]
    fn a_line_break_in_the_replacement_splits_rows() {
        let stage = stage_of(&["ts=1; level=E; user=42", "next"]);
        let out = replace(&stage, "; ", ";\n");
        assert_eq!(lines_of(&out), ["ts=1;", "level=E;", "user=42", "next"]);
        assert_eq!(row_ids_of(&out), [0, 0, 0, 1]);
    }

    #[test]
    fn a_line_break_in_the_search_joins_rows() {
        let stage = stage_of(&["ERROR Checkout failed", "    user=42", "    order=991", "INFO ok"]);
        let out = replace(&stage, "\n    ", " ");
        assert_eq!(lines_of(&out), ["ERROR Checkout failed user=42 order=991", "INFO ok"]);
        assert_eq!(row_ids_of(&out), [0, 3]);
        assert_eq!(lines_of(&replace(&stage, "\n", "|")), ["ERROR Checkout failed|    user=42|    order=991|INFO ok"]);
    }

    #[test]
    fn a_search_over_several_rows_matches_whole_rows_between() {
        let stage = stage_of(&["} ", "x", "{ a", "} ", "y", "{ b"]);
        assert_eq!(lines_of(&replace(&stage, "} \nx\n{", "}{")), ["}{ a", "} ", "y", "{ b"]);
        let json = stage_of(&["{a} {b} {c}"]);
        assert_eq!(lines_of(&replace(&json, "} {", "}\n{")), ["{a}", "{b}", "{c}"]);
    }
}
