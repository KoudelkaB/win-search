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

/// What separates log fields: blanks, control characters such as the group separator
/// in audit logs, and arrows
fn field_gap(ch: char) -> bool {
    ch.is_whitespace() || ch.is_control() || ch == '→'
}

fn field_key_start(line: &str, equals: usize) -> Option<usize> {
    let prefix = line[..equals].trim_end_matches(field_key_char);
    let key = &line[prefix.len()..equals];
    let first = key.chars().next()?;
    let last = key.chars().next_back()?;
    // A quote before the key closes the previous value when the caller sees it at the top level.
    let delimited = prefix.chars().next_back().is_none_or(|ch| field_gap(ch) || matches!(ch, ',' | '"' | '\'' | '`'));
    (delimited && (first.is_alphabetic() || first == '_') && (last.is_alphanumeric() || last == '_')).then_some(prefix.len())
}

/// A key and its equals sign start `text`, but not a comparison (`offset==0`) or an arrow (`y=>x`)
fn starts_with_field(text: &str) -> bool {
    text.find(|ch| !field_key_char(ch)).is_some_and(|at| text[at..].starts_with('=') && !text[at + 1..].starts_with(['=', '>']) && field_key_start(text, at) == Some(0))
}

/// An unquoted value runs to a gap, a separator or a quote, including balanced brackets
/// such as in `audit(1790879654.546:187)`.
fn unquoted_value_end(value: &str) -> usize {
    let mut depth = 0usize;
    for (at, ch) in value.char_indices() {
        match ch {
            '(' | '[' | '{' => depth += 1,
            ')' | ']' | '}' if depth == 0 => return at,
            ')' | ']' | '}' => depth -= 1,
            _ if depth > 0 => {}
            ',' | ';' | '"' | '\'' | '`' => return at,
            _ if field_gap(ch) => return at,
            _ => {}
        }
    }
    if depth > 0 {
        return value.find(['(', '[', '{']).unwrap_or(value.len());
    }
    value.len()
}

fn field_value_end(value: &str) -> Option<usize> {
    let bytes = value.as_bytes();
    let quoted = matches!(bytes.first()?, b'"' | b'\'' | b'`');
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
        _ => {
            let end = unquoted_value_end(value);
            // A colon after the value starts the message, as in `rows=4668: wait`.
            let rest = &value[end..];
            if value[..end].ends_with(':') && rest.chars().next().is_none_or(char::is_whitespace) {
                end - 1
            } else {
                end
            }
        }
    };
    let tail = &value[end..];
    let message = tail.strip_prefix(':').is_some_and(|rest| rest.is_empty() || rest.starts_with(char::is_whitespace));
    let next = tail.trim_start_matches(field_gap);
    let word_separator = next.len() < tail.len() && !operator_follows(tail);
    let delimited = tail.is_empty() || tail.starts_with([',', ';']) || word_separator || message || (quoted && starts_with_field(tail));
    (end > 0 && delimited).then_some(end)
}

/// Where `text` ends: at the top level, inside a quote opened at the top level (its
/// byte), or nested in brackets
enum Context {
    TopLevel,
    Quote(usize),
    Nested,
}

fn context(line: &str) -> Context {
    let bytes = line.as_bytes();
    let mut quote = None;
    let mut depth = 0usize;
    for (at, &ch) in bytes.iter().enumerate() {
        if let Some((open, _)) = quote {
            if ch == open && !delimiters::is_escaped(bytes, at) {
                quote = None;
            }
            continue;
        }
        match ch {
            b'\'' if line[..at].chars().next_back().is_some_and(char::is_alphanumeric) => {}
            b'"' | b'\'' | b'`' if !delimiters::is_escaped(bytes, at) => quote = Some((ch, at)),
            b'(' | b'[' | b'{' => depth += 1,
            b')' | b']' | b'}' => depth = depth.saturating_sub(1),
            _ => {}
        }
    }
    match (quote, depth) {
        (None, 0) => Context::TopLevel,
        (Some((_, at)), 0) => Context::Quote(at),
        _ => Context::Nested,
    }
}

fn has_adjacent_field(prefix: &str, tail: &str) -> bool {
    let next = tail.trim_start_matches(field_gap);
    if let Some(equals) = next.find(|ch| !field_key_char(ch)).filter(|&at| next.as_bytes()[at] == b'=') {
        if field_key_start(next, equals).is_some() && field_value_end(&next[equals + 1..]).is_some() {
            return true;
        }
    }

    // The preceding value can contain equals signs inside quotes or JSON.
    let previous = prefix.trim_end_matches(field_gap);
    previous.rmatch_indices('=').any(|(equals, _)| {
        let value = &previous[equals + 1..];
        field_key_start(previous, equals).is_some() && field_value_end(value) == Some(value.len())
    })
}

/// An unspaced assignment belongs to a log field on a timestamped row or beside another
/// field. Fields can also be listed in the quoted value of a field, as in audit messages
/// `msg='op=PAM:setcred grantors=pam_permit'`. Leave assignments in other quoted text,
/// nested structures and ordinary code to the expression selector.
/// A log row starts with a timestamp, also as in `2025-11-18 10:43:59, Info` (setupact.log,
/// CBS.log) and `[2025-11-18 10:43:59]`
fn timestamped(line: &str) -> bool {
    static TIMESTAMP: LazyLock<regex::Regex> = LazyLock::new(|| regex::Regex::new(&format!(r"^\[?{DATE_TIME_ISO}\]?[\s,]")).expect("log timestamp"));
    TIMESTAMP.is_match(line.trim_start())
}

fn log_field(line: &str, equals: usize) -> Option<(usize, usize)> {
    let start = field_key_start(line, equals)?;
    match context(&line[..start]) {
        Context::TopLevel => {
            let value_start = equals + 1;
            let value = &line[value_start..];
            let end = value_start + field_value_end(value).or_else(|| operand_end(value))?;
            (timestamped(line) || has_adjacent_field(&line[..start], &line[end..])).then_some((start, end))
        }
        Context::Quote(open) => {
            let outer_equals = open.checked_sub(1).filter(|&at| line.as_bytes()[at] == b'=')?;
            let (_, outer_end) = log_field(line, outer_equals)?;
            let content = &line[open + 1..outer_end - 1];
            let (start, end) = quoted_field(content, equals - open - 1)?;
            Some((open + 1 + start, open + 1 + end))
        }
        Context::Nested => None,
    }
}

/// The end of an unquoted value that an operator follows (`a=b + 1`). Whether the operator
/// continues the value is unclear, so the field comes first and the next click on its operator
/// takes the expression.
fn operand_end(value: &str) -> Option<usize> {
    let end = unquoted_value_end(value);
    (end > 0 && operator_follows(&value[end..])).then_some(end)
}

/// `tail` goes on with an operator after a gap, as in ` + 1` or ` != 0`
fn operator_follows(tail: &str) -> bool {
    let next = tail.trim_start_matches(field_gap);
    next.len() < tail.len() && (next.starts_with(['+', '-', '*', '/', '%', '&', '|', '^', '?', '.', '<', '>', '=']) || next.starts_with("!="))
}

/// Where another field of a log row starts in `text` after a gap and outside brackets and
/// quotes, as `retry=3` in `+ 1 retry=3` but not in `+ f(1, retry=3)`. Returns the end of what
/// precedes it.
fn before_next_field(text: &str) -> Option<usize> {
    let bytes = text.as_bytes();
    let mut quote = None;
    let mut depth = 0usize;
    for (at, ch) in text.char_indices() {
        if let Some(open) = quote {
            if ch == open && !delimiters::is_escaped(bytes, at) {
                quote = None;
            }
            continue;
        }
        match ch {
            '\'' if text[..at].chars().next_back().is_some_and(char::is_alphanumeric) => {}
            '"' | '\'' | '`' => quote = Some(ch),
            '(' | '[' | '{' => depth += 1,
            ')' | ']' | '}' => depth = depth.saturating_sub(1),
            _ if depth == 0 && text[..at].ends_with(field_gap) && starts_with_field(&text[at..]) => return Some(text[..at].trim_end_matches(field_gap).len()),
            _ => {}
        }
    }
    None
}

/// `<` opens angle brackets (`List<String>`, `<tag>`, `HashMap::<` before a line break) unless it
/// compares: with a gap after it (`count < limit`) or as `<=`
fn opens_angle(line: &[u8], at: usize) -> bool {
    match line.get(at + 1) {
        None => at.checked_sub(1).is_some_and(|before| !line[before].is_ascii_whitespace()),
        Some(after) => !after.is_ascii_whitespace() && *after != b'=',
    }
}

/// `>` can close angle brackets unless it is part of `>=`, `=>` or `->`
fn closes_angle(line: &[u8], at: usize) -> bool {
    !at.checked_sub(1).is_some_and(|before| matches!(line[before], b'=' | b'-')) && line.get(at + 1) != Some(&b'=')
}

/// A `>` right after a word, so it can end a tag (`<x a=b>`), not a comparison (`a > b`)
fn ends_angle(line: &[u8], at: usize) -> bool {
    closes_angle(line, at) && at.checked_sub(1).is_some_and(|before| !line[before].is_ascii_whitespace())
}

/// Open angle brackets go on after `row`: it ends in `<` or `,` (`HashMap::<`, `String,`), or the
/// next row closes them or continues their list (`>::new()`, `, usize`). An unclosed comparison
/// such as `x<5` does not take the rows after it.
fn angles_continue(stage: &Stage, row: usize) -> bool {
    let ends_open = stage.line_bytes(row).iter().rev().find(|ch| !ch.is_ascii_whitespace()).is_some_and(|ch| matches!(ch, b'<' | b','));
    ends_open || (row + 1 < stage.len() && stage.line(row + 1).trim_start().starts_with(['>', ',']))
}

/// A field in the content of a quoted field value
fn quoted_field(content: &str, equals: usize) -> Option<(usize, usize)> {
    let start = field_key_start(content, equals)?;
    let value_start = equals + 1;
    let end = value_start + field_value_end(&content[value_start..])?;
    match context(&content[..start]) {
        Context::TopLevel => Some((start, end)),
        Context::Quote(_) | Context::Nested => None,
    }
}

/// Where the expression left of an operator starts in `line`, and whether a bracket closed in it
/// opened on an earlier row. On a log row, `: ` also ends what is before the expression, as in
/// `Host SKU Info: Edition = 'Professional'`.
fn left_boundary(line: &[u8], log_row: bool) -> (usize, bool) {
    let mut quote = None;
    let mut depth = 0;
    for (index, &ch) in line.iter().enumerate().rev() {
        if let Some(open) = quote {
            if ch == open && !delimiters::is_escaped(line, index) {
                quote = None;
            }
            continue;
        }
        let bracket = match ch {
            b'<' => opens_angle(line, index),
            b'>' => ends_angle(line, index),
            _ => true,
        };
        match ch {
            b'"' | b'\'' | b'`' => quote = Some(ch),
            b')' | b']' | b'}' | b'>' if bracket => depth += 1,
            b'(' | b'[' | b'{' | b'<' if bracket && depth > 0 => depth -= 1,
            b'(' | b'[' | b'{' | b'<' if bracket => return (index + 1, false),
            b',' | b';' if depth == 0 => return (index + 1, false),
            b':' if log_row && depth == 0 && line.get(index + 1).is_some_and(u8::is_ascii_whitespace) => return (index + 1, false),
            _ => {}
        }
    }
    (0, depth > 0)
}

fn continues_with_dot(stage: &Stage, row: usize) -> bool {
    row + 1 < stage.len() && stage.line(row + 1).trim_start_matches([' ', '\t']).starts_with('.')
}

/// Where the expression right of an operator ends. A `>` that no `<` after the operator opened
/// ends it only in a tag, when a `<` is open left of the operator (`<x a=b>`); otherwise it
/// compares (`count>limit`).
fn right_boundary(stage: &Stage, (first_row, first_byte): BytePosition, in_angle: bool) -> BytePosition {
    let mut depth = 0;
    let mut angles = 0;
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
                b'(' | b'[' | b'{' => depth += 1,
                b'<' if opens_angle(line, byte) => angles += 1,
                b'>' if angles > 0 && closes_angle(line, byte) => angles -= 1,
                b'>' if in_angle && depth == 0 && ends_angle(line, byte) => return (row, byte),
                b')' | b']' | b'}' if depth > 0 => depth -= 1,
                b')' | b']' | b'}' if depth == 0 => return (row, byte),
                b',' | b';' if depth == 0 && angles == 0 => return (row, byte),
                _ => {}
            }
        }
        let open_angles = angles > 0 && angles_continue(stage, row);
        if row + 1 == stage.len() || (quote.is_none() && depth == 0 && !open_angles && !continues_with_dot(stage, row)) {
            return (row, line.len());
        }
    }
    unreachable!("the operator is in a stage row")
}

/// What a click on an operator selects: the log field it belongs to, otherwise the expression
/// around it
pub(super) fn at(stage: &Stage, point: Point) -> Option<Selection> {
    field_at(stage, point).or_else(|| expression_at(stage, point))
}

/// What a second click on the operator of a log field selects: the expression the field starts,
/// as in `a=b + 1` or a call chain continued on the next row, up to the next field of the row
pub(super) fn grown(stage: &Stage, point: Point, current: Selection) -> Option<Selection> {
    let field = field_at(stage, point).filter(|field| *field == current)?;
    let line = stage.line(point.row);
    let from = col_to_byte(line, field.end.col);
    let tail = &line[from..];
    if !operator_follows(tail) && !(tail.trim().is_empty() && continues_with_dot(stage, point.row)) {
        return None;
    }
    let mut end = expression_at(stage, point)?.end;
    if end <= field.end {
        return None;
    }
    if end.row == point.row {
        if let Some(at) = before_next_field(&line[from..col_to_byte(line, end.col)]) {
            end = Point::new(point.row, byte_to_col(line, from + at));
        }
    }
    (end > field.end).then(|| Selection::new(field.start, end))
}

/// Whether `current` is what `grown` made of the field at the operator, so that clicking the
/// operator again keeps it instead of going back to the field
pub(super) fn is_grown(stage: &Stage, point: Point, current: Selection) -> bool {
    field_at(stage, point).and_then(|field| grown(stage, point, field)) == Some(current)
}

fn field_at(stage: &Stage, point: Point) -> Option<Selection> {
    let line = stage.line(point.row);
    let (operator_start, operator_end) = operator_at(line.as_bytes(), col_to_byte(line, point.col))?;
    let (start, end) = log_field(line, operator_start).filter(|_| operator_end == operator_start + 1)?;
    let position = |byte| Point::new(point.row, byte_to_col(line, byte));
    Some(Selection::new(position(start), position(end)))
}

fn expression_at(stage: &Stage, point: Point) -> Option<Selection> {
    let line = stage.line(point.row);
    let (operator_start, operator_end) = operator_at(line.as_bytes(), col_to_byte(line, point.col))?;
    let position = |(row, byte)| Point::new(row, byte_to_col(stage.line(row), byte));
    let (left, unclosed) = left_boundary(&line.as_bytes()[..operator_start], timestamped(line));
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
    // The left side stopped at a `<` it did not close, as in a tag
    let in_angle = left > 0 && line.as_bytes()[left - 1] == b'<';
    let (row, byte) = right_boundary(stage, (point.row, operator_end), in_angle);
    let end = (point.row..=row).rev().find_map(|row_index| {
        let line = stage.line(row_index);
        let trimmed = if row_index == row { &line[..byte] } else { line }.trim_end();
        (!trimmed.is_empty()).then_some((row_index, trimmed.len()))
    })?;
    Some(Selection::new(position(start), position(end)))
}
