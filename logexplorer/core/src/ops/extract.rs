//! Extract: values pulled out of the rows by a regular expression, a fixed column or the
//! text between two delimiters. Every value is a piece of an existing row, so the result
//! needs no text of its own.

use super::{par_chunks, trim_span};
use crate::cancel::{Cancel, OpError, OpResult};
use crate::literal::Needle;
use crate::operator::{ExtractMode, ExtractOp, ExtractScope, RowMode};
use crate::stage::{OutRows, Stage};
use crate::text::{as_str, char_len, cols_to_bytes, Span};
use regex_syntax::hir::{Class, Hir, HirKind};
use std::ops::Range;

/// Collects the values of one chunk of rows.
struct Output<'a> {
    stage: &'a Stage,
    trim: bool,
    skip_empty: bool,
    spans: Vec<Span>,
    origin: Vec<u32>,
    max_cols: u64,
}

impl<'a> Output<'a> {
    fn new(stage: &'a Stage, op: &ExtractOp) -> Self {
        Output { stage, trim: op.trim, skip_empty: op.skip_empty, spans: Vec::new(), origin: Vec::new(), max_cols: 0 }
    }

    #[inline]
    fn push(&mut self, span: Span, row: usize) {
        let cols = if span.is_empty() {
            0
        } else if self.stage.bufs[span.buf() as usize].ascii {
            span.len() as u64
        } else {
            char_len(self.stage.span_bytes(span)) as u64
        };
        self.max_cols = self.max_cols.max(cols);
        self.spans.push(span);
        self.origin.push(row as u32);
    }

    fn add(&mut self, span: Span, row: usize) {
        let span = if self.trim { trim_span(self.stage, span) } else { span };
        if self.skip_empty && span.is_empty() {
            return;
        }
        self.push(span, row);
    }

    /// A value from byte `from.1` of row `from.0` to byte `to.1` of row `to.0`; one that
    /// crosses rows gives one row for each
    fn add_range(&mut self, from: (usize, usize), to: (usize, usize)) {
        if from.0 == to.0 {
            self.add(self.stage.span(from.0).sub(from.1, to.1), from.0);
            return;
        }
        let mut pieces: Vec<(Span, usize)> = (from.0..=to.0)
            .map(|row| {
                let span = self.stage.span(row);
                let start = if row == from.0 { from.1 } else { 0 };
                let end = if row == to.0 { to.1 } else { span.len() as usize };
                (span.sub(start, end), row)
            })
            .collect();
        if self.trim {
            let blank = |stage: &Stage, span: Span| as_str(stage.span_bytes(span)).trim().is_empty();
            while pieces.last().is_some_and(|(span, _)| blank(self.stage, *span)) {
                pieces.pop();
            }
            let leading = pieces.iter().take_while(|(span, _)| blank(self.stage, *span)).count();
            pieces.drain(..leading);
            if let Some((span, _)) = pieces.first_mut() {
                let text = as_str(self.stage.span_bytes(*span));
                *span = span.sub(text.len() - text.trim_start().len(), text.len());
            }
            if let Some((span, _)) = pieces.last_mut() {
                let text = as_str(self.stage.span_bytes(*span));
                *span = span.sub(0, text.trim_end().len());
            }
        }
        for (span, row) in pieces {
            self.push(span, row);
        }
    }

    fn into_out_rows(self) -> OutRows {
        OutRows { spans: self.spans, origin: Some(self.origin), arena: Vec::new(), max_cols: Some(self.max_cols), arena_resolved: true, arena_ascii: None }
    }
}

fn row_bounds(stage: &Stage, op: &ExtractOp) -> Range<usize> {
    let rows = stage.len();
    if rows == 0 {
        return 0..0;
    }
    if op.row_mode != RowMode::Range {
        return 0..rows;
    }
    let start = (op.from_row.max(1) as usize - 1).min(rows - 1);
    let end = if op.to_row == 0 { start + 1 } else { (op.to_row as usize).clamp(start + 1, rows) };
    start..end
}

// --- regular expressions ----------------------------------------------------------------------

enum Engine {
    Fast(regex::Regex),
    /// Look-around and back-references, which the fast engine does not have
    Fancy(Box<fancy_regex::Regex>),
}

pub(crate) struct Pattern {
    engine: Engine,
    group: usize,
    /// A match can contain a line break, so rows cannot be searched one by one
    crosses_rows: bool,
}

fn can_match_line_break(hir: &Hir) -> bool {
    match hir.kind() {
        HirKind::Empty | HirKind::Look(_) => false,
        HirKind::Literal(literal) => literal.0.contains(&b'\n'),
        HirKind::Class(Class::Unicode(class)) => class.ranges().iter().any(|range| range.start() <= '\n' && '\n' <= range.end()),
        HirKind::Class(Class::Bytes(class)) => class.ranges().iter().any(|range| range.start() <= b'\n' && b'\n' <= range.end()),
        HirKind::Repetition(repetition) => can_match_line_break(&repetition.sub),
        HirKind::Capture(capture) => can_match_line_break(&capture.sub),
        HirKind::Concat(parts) | HirKind::Alternation(parts) => parts.iter().any(can_match_line_break),
    }
}

fn next_char(text: &str, at: usize) -> usize {
    let mut next = at + 1;
    while next < text.len() && !text.is_char_boundary(next) {
        next += 1;
    }
    next
}

impl Pattern {
    /// Compiles a multi-line pattern with the requested case sensitivity
    pub fn compile(pattern: &str, case_sensitive: bool, group: usize) -> OpResult<Pattern> {
        let invalid = |error: String| OpError::Fmt("Invalid Extract regular expression: {0}", vec![error]);
        let (engine, groups, crosses_rows) = match regex::RegexBuilder::new(pattern).multi_line(true).case_insensitive(!case_sensitive).build() {
            Ok(regex) => {
                let crosses_rows =
                    regex_syntax::ParserBuilder::new().multi_line(true).case_insensitive(!case_sensitive).build().parse(pattern).map_or(true, |hir| can_match_line_break(&hir));
                let groups = regex.captures_len();
                (Engine::Fast(regex), groups, crosses_rows)
            }
            Err(fast_error) => {
                let flags = if case_sensitive { "(?m)" } else { "(?mi)" };
                match fancy_regex::Regex::new(&format!("{flags}{pattern}")) {
                    Ok(regex) => {
                        let groups = regex.captures_len();
                        (Engine::Fancy(Box::new(regex)), groups, true)
                    }
                    Err(_) => return Err(invalid(fast_error.to_string())),
                }
            }
        };
        if group >= groups {
            return Err(OpError::Fmt("Extract capture group {0} does not exist in the pattern.", vec![group.to_string()]));
        }
        Ok(Pattern { engine, group, crosses_rows })
    }

    /// Calls `found` with the byte range of the group of every match (`None` when the group
    /// took no part in it); only the first match unless `all`
    fn each_match(&self, text: &str, all: bool, mut found: impl FnMut(Option<(usize, usize)>)) -> OpResult<()> {
        let mut at = 0;
        match &self.engine {
            Engine::Fast(regex) => {
                let mut locations = regex.capture_locations();
                while at <= text.len() {
                    let whole = if self.group == 0 {
                        let Some(whole) = regex.find_at(text, at) else { break };
                        found(Some((whole.start(), whole.end())));
                        whole
                    } else {
                        let Some(whole) = regex.captures_read_at(&mut locations, text, at) else { break };
                        found(locations.get(self.group));
                        whole
                    };
                    if !all {
                        break;
                    }
                    at = if whole.is_empty() { next_char(text, whole.end()) } else { whole.end() };
                }
            }
            Engine::Fancy(regex) => {
                while at <= text.len() {
                    let captures = regex.captures_from_pos(text, at).map_err(|error| OpError::Fmt("Invalid Extract regular expression: {0}", vec![error.to_string()]))?;
                    let Some(captures) = captures else { break };
                    let Some(whole) = captures.get(0) else { break };
                    found(captures.get(self.group).map(|group| (group.start(), group.end())));
                    if !all {
                        break;
                    }
                    at = if whole.start() == whole.end() { next_char(text, whole.end()) } else { whole.end() };
                }
            }
        }
        Ok(())
    }
}

fn collect_chunks(stage: &Stage, op: &ExtractOp, rows: Range<usize>, cancel: &Cancel, per_row: impl Fn(&mut Output, usize) -> OpResult<()> + Sync) -> OpResult<OutRows> {
    let base = rows.start;
    let chunks = par_chunks(rows.len(), cancel, |range| {
        let mut output = Output::new(stage, op);
        for row in range {
            per_row(&mut output, base + row)?;
        }
        Ok(output)
    })?;
    let total = chunks.iter().map(|chunk| chunk.spans.len()).sum();
    let max_cols = chunks.iter().map(|chunk| chunk.max_cols).max().unwrap_or(0);
    let mut out = OutRows {
        spans: Vec::with_capacity(total),
        origin: Some(Vec::with_capacity(total)),
        arena: Vec::new(),
        max_cols: Some(max_cols),
        arena_resolved: true,
        arena_ascii: None,
    };
    for chunk in chunks {
        out.spans.extend_from_slice(&chunk.spans);
        out.origin.as_mut().unwrap().extend_from_slice(&chunk.origin);
    }
    Ok(out)
}

fn regex_rows(stage: &Stage, op: &ExtractOp, pattern: &Pattern, rows: Range<usize>, cancel: &Cancel) -> OpResult<OutRows> {
    collect_chunks(stage, op, rows, cancel, |output, row| {
        let span = stage.span(row);
        pattern.each_match(as_str(stage.span_bytes(span)), op.all_matches, |group| {
            if let Some((start, end)) = group {
                output.add(span.sub(start, end), row);
            }
        })
    })
}

/// The pattern over the rows joined by line breaks, for matches that cross rows
fn regex_whole_text(stage: &Stage, op: &ExtractOp, pattern: &Pattern, cancel: &Cancel) -> OpResult<OutRows> {
    let rows = stage.len();
    let mut text = String::new();
    text.try_reserve_exact(stage.text_bytes() as usize).map_err(|_| OpError::msg("Not enough memory to search the whole view."))?;
    let mut starts = Vec::with_capacity(rows);
    for row in 0..rows {
        if row % 8192 == 0 {
            cancel.check()?;
        }
        if row > 0 {
            text.push('\n');
        }
        starts.push(text.len());
        text.push_str(stage.line(row));
    }
    let mut output = Output::new(stage, op);
    // Matches come in order, so the row of an offset is found by walking on
    let mut row = 0;
    let locate = |offset: usize, row: &mut usize| {
        while *row + 1 < rows && starts[*row + 1] <= offset {
            *row += 1;
        }
        (*row, offset - starts[*row])
    };
    pattern.each_match(&text, op.all_matches, |group| {
        if let Some((start, end)) = group {
            let from = locate(start, &mut row);
            let mut end_row = from.0;
            let to = locate(end, &mut end_row);
            // An end right after a line break belongs to the row before it
            let to = if to.1 == 0 && to.0 > from.0 && end > start && text.as_bytes()[end - 1] == b'\n' { (to.0 - 1, stage.span(to.0 - 1).len() as usize) } else { to };
            output.add_range(from, to);
        }
    })?;
    Ok(output.into_out_rows())
}

fn apply_regex(stage: &Stage, op: &ExtractOp, cancel: &Cancel) -> OpResult<OutRows> {
    let pattern = Pattern::compile(&op.pattern, op.case_sensitive, op.group as usize)?;
    if op.scope == ExtractScope::Line {
        return regex_rows(stage, op, &pattern, row_bounds(stage, op), cancel);
    }
    if pattern.crosses_rows {
        return regex_whole_text(stage, op, &pattern, cancel);
    }
    if op.all_matches {
        return regex_rows(stage, op, &pattern, 0..stage.len(), cancel);
    }
    // Only the first match of the whole view
    let mut output = Output::new(stage, op);
    for row in 0..stage.len() {
        if row % 8192 == 0 {
            cancel.check()?;
        }
        let span = stage.span(row);
        let mut matched = false;
        pattern.each_match(as_str(stage.span_bytes(span)), false, |group| {
            matched = true;
            if let Some((start, end)) = group {
                output.add(span.sub(start, end), row);
            }
        })?;
        if matched {
            break;
        }
    }
    Ok(output.into_out_rows())
}

// --- between two texts ------------------------------------------------------------------------

fn between_in_row(output: &mut Output, row: usize, start: &Needle, end: &Needle, all: bool) {
    let span = output.stage.span(row);
    let line = output.stage.span_bytes(span);
    let mut cursor = 0;
    while cursor <= line.len() {
        let anchor = if start.is_empty() {
            if cursor != 0 {
                break;
            }
            (0, 0)
        } else {
            match start.find_at(line, cursor) {
                Some(found) => found,
                None => break,
            }
        };
        let value_start = anchor.1;
        let (value_end, after_end) = if end.is_empty() {
            (line.len(), line.len())
        } else {
            match end.find_at(line, value_start) {
                Some(found) => found,
                None => break,
            }
        };
        output.add(span.sub(value_start, value_end), row);
        if !all || start.is_empty() {
            break;
        }
        cursor = after_end.max(anchor.1.max(anchor.0 + 1));
    }
}

/// The first match of `needle` at or after `from`, searching on through the rows
fn find_across(stage: &Stage, needle: &Needle, from: (usize, usize), cancel: &Cancel) -> OpResult<Option<(usize, usize, usize)>> {
    for row in from.0..stage.len() {
        if row % 8192 == 0 {
            cancel.check()?;
        }
        if let Some((start, end)) = needle.find_at(stage.line_bytes(row), if row == from.0 { from.1 } else { 0 }) {
            return Ok(Some((row, start, end)));
        }
    }
    Ok(None)
}

fn between_whole(stage: &Stage, op: &ExtractOp, start: &Needle, end: &Needle, cancel: &Cancel) -> OpResult<OutRows> {
    let mut output = Output::new(stage, op);
    let rows = stage.len();
    let mut cursor = (0usize, 0usize);
    if rows == 0 {
        return Ok(output.into_out_rows());
    }
    loop {
        let anchor = if start.is_empty() {
            if cursor != (0, 0) {
                break;
            }
            (0, 0, 0)
        } else {
            match find_across(stage, start, cursor, cancel)? {
                Some(found) => found,
                None => break,
            }
        };
        let value_start = (anchor.0, anchor.2);
        let (value_end, after_end) = if end.is_empty() {
            let last = (rows - 1, stage.span(rows - 1).len() as usize);
            (last, last)
        } else {
            match find_across(stage, end, value_start, cancel)? {
                Some((row, found_start, found_end)) => ((row, found_start), (row, found_end)),
                None => break,
            }
        };
        output.add_range(value_start, value_end);
        if !op.all_matches || start.is_empty() {
            break;
        }
        cursor = after_end.max((anchor.0, anchor.2.max(anchor.1 + 1)));
    }
    Ok(output.into_out_rows())
}

pub fn apply(stage: &Stage, op: &ExtractOp, cancel: &Cancel) -> OpResult<Stage> {
    let out = match op.mode {
        ExtractMode::Regex => {
            if op.pattern.is_empty() {
                return Ok(stage.copy_as("Extract"));
            }
            apply_regex(stage, op, cancel)?
        }
        ExtractMode::Between => {
            let start = Needle::new(&op.start_anchor, op.case_sensitive);
            let end = Needle::new(&op.end_anchor, op.case_sensitive);
            if op.scope == ExtractScope::Line {
                collect_chunks(stage, op, row_bounds(stage, op), cancel, |output, row| {
                    between_in_row(output, row, &start, &end, op.all_matches);
                    Ok(())
                })?
            } else {
                between_whole(stage, op, &start, &end, cancel)?
            }
        }
        ExtractMode::Column => {
            let (pos, len) = (op.pos as usize, op.len.max(1) as usize);
            collect_chunks(stage, op, row_bounds(stage, op), cancel, |output, row| {
                let span = stage.span(row);
                let (start, end) = cols_to_bytes(as_str(stage.span_bytes(span)), pos, pos + len);
                output.add(span.sub(start, end), row);
                Ok(())
            })?
        }
    };
    if out.spans.len() > u32::MAX as usize - 1 {
        return Err(OpError::msg("The result has too many rows."));
    }
    Ok(stage.derive("Extract", out))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::operator::{create_default, OpKind, OpType};
    use crate::stage::testing::*;

    fn extract() -> ExtractOp {
        let OpKind::Extract(op) = create_default(1, OpType::Extract, None).kind else { unreachable!() };
        op
    }

    fn run(stage: &Stage, op: &ExtractOp) -> Vec<String> {
        lines_of(&apply(stage, op, &Cancel::new()).unwrap())
    }

    #[test]
    fn a_capture_group_is_collected_from_every_match_even_in_one_line_json() {
        let stage = stage_of(&[r#"[{"ConnectionId":"a1","x":1},{"ConnectionId": "b2"}]"#, r#"{"ConnectionId":"c3"}"#]);
        let op = ExtractOp { pattern: r#""ConnectionId"\s*:\s*"([^"]+)""#.into(), ..extract() };
        let out = apply(&stage, &op, &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["a1", "b2", "c3"]);
        assert_eq!(row_ids_of(&out), [0, 0, 1]);
        // Values are pieces of the rows: nothing was copied
        assert_eq!(out.bufs.len(), stage.bufs.len());
        assert_eq!(run(&stage, &ExtractOp { all_matches: false, ..op.clone() }), ["a1"]);
        assert_eq!(run(&stage, &ExtractOp { all_matches: false, scope: ExtractScope::Line, ..op.clone() }), ["a1", "c3"]);
        assert_eq!(run(&stage, &ExtractOp { group: 0, ..op }), [r#""ConnectionId":"a1""#, r#""ConnectionId": "b2""#, r#""ConnectionId":"c3""#]);
    }

    #[test]
    fn the_pattern_is_multi_line_and_case_can_be_ignored() {
        let stage = stage_of(&["ERROR one", "info", "error two"]);
        assert_eq!(run(&stage, &ExtractOp { pattern: "^ERROR (.*)$".into(), ..extract() }), ["one"]);
        assert_eq!(run(&stage, &ExtractOp { pattern: "^ERROR (.*)$".into(), case_sensitive: false, ..extract() }), ["one", "two"]);
    }

    #[test]
    fn a_match_across_rows_gives_a_row_for_each() {
        let stage = stage_of(&["BEGIN a", "b", "c END", "BEGIN d END"]);
        let op = ExtractOp { pattern: r"BEGIN([\s\S]*?)END".into(), ..extract() };
        assert_eq!(run(&stage, &op), ["a", "b", "c", "d"]);
        assert_eq!(row_ids_of(&apply(&stage, &op, &Cancel::new()).unwrap()), [0, 1, 2, 3]);
    }

    #[test]
    fn look_around_patterns_work_and_bad_ones_are_reported() {
        let stage = stage_of(&["id=42 id=7"]);
        assert_eq!(run(&stage, &ExtractOp { pattern: r"(?<=id=)\d+".into(), group: 0, ..extract() }), ["42", "7"]);
        let invalid = apply(&stage, &ExtractOp { pattern: "(".into(), ..extract() }, &Cancel::new()).err().unwrap();
        assert!(invalid.to_string().starts_with("Invalid Extract regular expression"), "{invalid}");
        let missing = apply(&stage, &ExtractOp { pattern: "id".into(), group: 2, ..extract() }, &Cancel::new()).err().unwrap();
        assert_eq!(missing.to_string(), "Extract capture group 2 does not exist in the pattern.");
        assert_eq!(run(&stage, &ExtractOp { pattern: String::new(), ..extract() }), ["id=42 id=7"]);
    }

    #[test]
    fn a_column_is_cut_from_the_chosen_rows() {
        let stage = stage_of(&["afr  95.6%", "amh  48.6%", "x", "ara  78.0%"]);
        let op = ExtractOp { mode: ExtractMode::Column, pos: 5, len: 4, ..extract() };
        assert_eq!(run(&stage, &op), ["95.6", "48.6", "78.0"]);
        assert_eq!(run(&stage, &ExtractOp { skip_empty: false, ..op.clone() }), ["95.6", "48.6", "", "78.0"]);
        assert_eq!(run(&stage, &ExtractOp { row_mode: RowMode::Range, from_row: 2, to_row: 3, ..op }), ["48.6"]);
    }

    #[test]
    fn text_between_two_delimiters_is_found_in_rows_or_across_them() {
        let stage = stage_of(&["<a> one </a><a>two</a>", "<a>three", "four</a>"]);
        let op = ExtractOp { mode: ExtractMode::Between, start_anchor: "<a>".into(), end_anchor: "</a>".into(), ..extract() };
        assert_eq!(run(&stage, &op), ["one", "two", "three", "four"]);
        assert_eq!(run(&stage, &ExtractOp { scope: ExtractScope::Line, ..op.clone() }), ["one", "two"]);
        assert_eq!(run(&stage, &ExtractOp { all_matches: false, ..op.clone() }), ["one"]);
        assert_eq!(run(&stage, &ExtractOp { start_anchor: "<A>".into(), case_sensitive: false, scope: ExtractScope::Line, ..op.clone() }), ["one", "two"]);
        // No closing text: the rest of the row
        assert_eq!(run(&stage, &ExtractOp { end_anchor: String::new(), scope: ExtractScope::Line, ..op.clone() }), ["one </a><a>two</a>", "three"]);
        // No opening text: from the start of the row
        assert_eq!(run(&stage, &ExtractOp { start_anchor: String::new(), end_anchor: ">".into(), scope: ExtractScope::Line, ..op }), ["<a", "<a", "four</a"]);
    }
}
