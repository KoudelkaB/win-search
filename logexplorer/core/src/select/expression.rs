//! Log fields and expressions around an assignment or comparison operator.

use super::{delimiters, BytePosition, DATE_TIME_ISO};
use crate::selection::{Point, Selection};
use crate::stage::Stage;
use crate::text::{byte_to_col, col_to_byte};
use memchr::memchr_iter;
use serde::de::IgnoredAny;
use std::sync::LazyLock;

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

fn field_key_char(ch: char) -> bool {
    ch.is_alphanumeric() || matches!(ch, '_' | '-' | '.')
}

fn field_key_start(line: &str, equals: usize) -> Option<usize> {
    let prefix = line[..equals].trim_end_matches(field_key_char);
    let key = &line[prefix.len()..equals];
    let first = key.chars().next()?;
    let last = key.chars().next_back()?;
    let delimited = prefix.chars().next_back().is_none_or(|ch| ch.is_whitespace() || matches!(ch, ',' | '→'));
    (delimited && (first.is_alphabetic() || first == '_') && (last.is_alphanumeric() || last == '_')).then_some(prefix.len())
}

fn field_value_end(value: &str) -> Option<usize> {
    let bytes = value.as_bytes();
    let end = match *bytes.first()? {
        quote @ (b'"' | b'\'' | b'`') => {
            let close = memchr_iter(quote, &bytes[1..]).map(|at| at + 1).find(|&at| !delimiters::is_escaped(bytes, at))?;
            close + 1
        }
        b'{' | b'[' => {
            let mut json = serde_json::Deserializer::from_str(value).into_iter::<IgnoredAny>();
            json.next()?.ok()?;
            json.byte_offset()
        }
        _ => value.find(|ch| !(field_key_char(ch) || ch == '+')).unwrap_or(value.len()),
    };
    let tail = &value[end..];
    let message = tail.strip_prefix(':').is_some_and(|rest| rest.is_empty() || rest.starts_with(char::is_whitespace));
    let next = tail.trim_start();
    let word_separator = next.len() < tail.len() && !next.starts_with(['+', '-', '*', '/', '%', '&', '|', '^', '?', '.', '<', '>', '=']);
    let delimited = tail.is_empty() || tail.starts_with([',', ';', '→']) || word_separator || message;
    (end > 0 && delimited).then_some(end)
}

fn at_top_level(line: &str) -> bool {
    let bytes = line.as_bytes();
    let mut quote = None;
    let mut depth = 0usize;
    for (at, &ch) in bytes.iter().enumerate() {
        if let Some(open) = quote {
            if ch == open && !delimiters::is_escaped(bytes, at) {
                quote = None;
            }
            continue;
        }
        match ch {
            b'\'' if line[..at].chars().next_back().is_some_and(char::is_alphanumeric) => {}
            b'"' | b'\'' | b'`' if !delimiters::is_escaped(bytes, at) => quote = Some(ch),
            b'(' | b'[' | b'{' => depth += 1,
            b')' | b']' | b'}' => depth = depth.saturating_sub(1),
            _ => {}
        }
    }
    quote.is_none() && depth == 0
}

fn has_adjacent_field(prefix: &str, tail: &str) -> bool {
    let gap = |ch: char| ch.is_whitespace() || ch == '→';
    let next = tail.trim_start_matches(gap);
    if let Some(equals) = next.find(|ch| !field_key_char(ch)).filter(|&at| next.as_bytes()[at] == b'=') {
        if field_key_start(next, equals).is_some() && field_value_end(&next[equals + 1..]).is_some() {
            return true;
        }
    }

    // The preceding value can contain equals signs inside quotes or JSON.
    let previous = prefix.trim_end_matches(gap);
    previous.rmatch_indices('=').any(|(equals, _)| {
        let value = &previous[equals + 1..];
        field_key_start(previous, equals).is_some() && field_value_end(value) == Some(value.len())
    })
}

/// An unspaced assignment belongs to a log field on a timestamped row or beside another
/// field. Leave assignments in quoted text, nested structures and ordinary code to the
/// expression selector.
fn log_field(line: &str, equals: usize) -> Option<(usize, usize)> {
    static TIMESTAMP: LazyLock<regex::Regex> = LazyLock::new(|| regex::Regex::new(&format!(r"^{DATE_TIME_ISO}\s")).expect("log timestamp"));
    let start = field_key_start(line, equals)?;
    let value_start = equals + 1;
    let end = value_start + field_value_end(&line[value_start..])?;
    let prefix = &line[..start];
    (at_top_level(prefix) && (TIMESTAMP.is_match(line.trim_start()) || has_adjacent_field(prefix, &line[end..]))).then_some((start, end))
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

fn continues_with_dot(stage: &Stage, row: usize) -> bool {
    row + 1 < stage.len() && stage.line(row + 1).trim_start_matches([' ', '\t']).starts_with('.')
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
        if row + 1 == stage.len() || (quote.is_none() && depth == 0 && !continues_with_dot(stage, row)) {
            return (row, line.len());
        }
    }
    unreachable!("the operator is in a stage row")
}

pub(super) fn at(stage: &Stage, point: Point) -> Option<Selection> {
    let line = stage.line(point.row);
    let (operator_start, operator_end) = operator_at(line.as_bytes(), col_to_byte(line, point.col))?;
    let position = |(row, byte)| Point::new(row, byte_to_col(stage.line(row), byte));
    if operator_end == operator_start + 1 {
        if let Some((start, end)) = log_field(line, operator_start).filter(|&(_, end)| !line[end..].trim().is_empty() || !continues_with_dot(stage, point.row)) {
            return Some(Selection::new(position((point.row, start)), position((point.row, end))));
        }
    }
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
    Some(Selection::new(position(start), position(end)))
}
