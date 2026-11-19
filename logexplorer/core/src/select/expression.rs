//! The expression around an assignment or comparison operator under the pointer.

use super::{delimiters, BytePosition};
use crate::selection::{Point, Selection};
use crate::stage::Stage;
use crate::text::{byte_to_col, col_to_byte};

fn operator_at(line: &[u8], byte: usize) -> Option<(usize, usize)> {
    let operator_byte = |byte| line.get(byte).is_some_and(|ch| b"=!<>".contains(ch));
    for operator in [b"===".as_slice(), b"!==", b"==", b"=>", b"!=", b"<=", b">=", b"="] {
        for start in (0..operator.len()).filter_map(|offset| byte.checked_sub(offset)) {
            let end = start + operator.len();
            if line.get(start..end) == Some(operator) && !start.checked_sub(1).is_some_and(operator_byte) && !operator_byte(end) {
                return Some((start, end));
            }
        }
    }
    None
}

fn left_boundary(line: &[u8]) -> (usize, bool) {
    let mut quote = None;
    let mut depth = 0;
    for (index, &ch) in line.iter().enumerate().rev() {
        if let Some(open) = quote {
            if ch == open && !delimiters::is_escaped(line, index) {
                quote = None;
            }
            continue;
        }
        match ch {
            b'"' | b'\'' | b'`' => quote = Some(ch),
            b')' | b']' | b'}' | b'>' => depth += 1,
            b'(' | b'[' | b'{' | b'<' if depth > 0 => depth -= 1,
            b'(' | b'[' | b'{' | b'<' => return (index + 1, false),
            b',' | b';' if depth == 0 => return (index + 1, false),
            _ => {}
        }
    }
    (0, depth > 0)
}

fn right_boundary(stage: &Stage, (first_row, first_byte): BytePosition) -> BytePosition {
    let mut depth = 0;
    for row in first_row..stage.len() {
        let line = stage.line_bytes(row);
        let from = if row == first_row { first_byte } else { 0 };
        let mut quote = None;
        for (offset, &ch) in line[from..].iter().enumerate() {
            let byte = from + offset;
            if let Some(open) = quote {
                if ch == open && !delimiters::is_escaped(line, byte) {
                    quote = None;
                }
                continue;
            }
            match ch {
                b'"' | b'\'' | b'`' => quote = Some(ch),
                b'(' | b'[' | b'{' | b'<' => depth += 1,
                b')' | b']' | b'}' | b'>' if depth > 0 => depth -= 1,
                b')' | b']' | b'}' | b'>' | b',' | b';' if depth == 0 => return (row, byte),
                _ => {}
            }
        }
        let continues_with_dot = || stage.line(row + 1).trim_start_matches([' ', '\t']).starts_with('.');
        if row + 1 == stage.len() || (quote.is_none() && depth == 0 && !continues_with_dot()) {
            return (row, line.len());
        }
    }
    unreachable!("the operator is in a stage row")
}

pub(super) fn at(stage: &Stage, point: Point) -> Option<Selection> {
    let line = stage.line(point.row);
    let (operator_start, operator_end) = operator_at(line.as_bytes(), col_to_byte(line, point.col))?;
    let (left, unclosed) = left_boundary(&line.as_bytes()[..operator_start]);
    let leading = &line[left..operator_start];
    let mut start = (point.row, left + leading.len() - leading.trim_start().len());
    // A bracket immediately left of the operator may have opened on an earlier row.
    if unclosed || leading.trim().is_empty() {
        let before = line[..operator_start].trim_end();
        let (row, trimmed) = if before.is_empty() {
            (0..point.row)
                .rev()
                .find_map(|row| {
                    let trimmed = stage.line(row).trim_end();
                    (!trimmed.is_empty()).then_some((row, trimmed))
                })
                .unwrap_or((point.row, before))
        } else {
            (point.row, before)
        };
        if trimmed.ends_with([')', ']', '}']) {
            let close = (row, trimmed.len() - 1);
            delimiters::brackets(stage, close, None, |pair| {
                if pair.content_end == close {
                    start = start.min(pair.start);
                }
            });
        }
    }
    let (row, byte) = right_boundary(stage, (point.row, operator_end));
    let end = (point.row..=row).rev().find_map(|row_index| {
        let line = stage.line(row_index);
        let trimmed = if row_index == row { &line[..byte] } else { line }.trim_end();
        (!trimmed.is_empty()).then_some((row_index, trimmed.len()))
    })?;
    let position = |(row, byte)| Point::new(row, byte_to_col(stage.line(row), byte));
    Some(Selection::new(position(start), position(end)))
}
