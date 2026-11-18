//! Bracket scopes in a JSON document that extends beyond the local selection window.

use crate::selection::{Point, Selection};
use crate::stage::Stage;
use crate::text::{char_len, col_to_byte};
use memchr::memchr2;

type BytePosition = (usize, usize);

fn content(scope: Selection) -> Selection {
    Selection::new(Point::new(scope.start.row, scope.start.col + 1), Point::new(scope.end.row, scope.end.col - 1))
}

pub(super) fn selection_at(scopes: &[Selection], point: Point, current: Option<Selection>, shrink: bool) -> Option<Selection> {
    let delimiter = scopes.iter().copied().find(|scope| point == scope.start || point == Point::new(scope.end.row, scope.end.col - 1));
    let contains = |outer: Selection, inner: Selection| outer.start <= inner.start && outer.end >= inner.end;
    let Some(current) = current.filter(|current| !current.is_empty()) else { return delimiter.map(content) };
    if !shrink && delimiter.is_some_and(|scope| content(scope) == current) {
        return delimiter;
    }
    let mut ranges = scopes.iter().copied().flat_map(|scope| [content(scope), scope]);
    if current.start <= point && point <= current.end {
        if shrink {
            ranges.rev().find(|&range| range != current && range.start <= point && point < range.end && contains(current, range))
        } else {
            ranges.find(|&range| range != current && range.start <= point && point < range.end && contains(range, current))
        }
    } else {
        delimiter.map(content)
    }
}

/// Read only the bracket scopes containing the click, innermost first. Strings, including
/// their escapes, are skipped without building a JSON tree or copying the document.
pub(super) fn scopes(stage: &Stage, point: Point) -> Vec<Selection> {
    bracket_scopes(stage, point).map_or_else(Vec::new, |pairs| {
        // The opening positions are in reverse order. Walk the offsets in text order so
        // computing columns on a long Unicode row visits each byte at most once.
        let mut row = 0;
        let (mut previous, mut column) = (0, 0);
        let mut points: Vec<_> = pairs
            .iter()
            .rev()
            .map(|&(open, _)| open)
            .chain(pairs.iter().map(|&(_, close)| close))
            .map(|(next_row, byte)| {
                if next_row != row {
                    (row, previous, column) = (next_row, 0, 0);
                }
                column += char_len(&stage.line_bytes(row)[previous..byte]);
                previous = byte;
                Point::new(row, column)
            })
            .collect();
        let ends = points.split_off(pairs.len());
        points.into_iter().rev().zip(ends).map(|(start, end)| Selection::new(start, end)).collect()
    })
}

fn bracket_scopes(stage: &Stage, point: Point) -> Option<Vec<(BytePosition, BytePosition)>> {
    let (first_row, first_byte) = (0..stage.len()).find_map(|row| {
        let offset = stage.line_bytes(row).iter().position(|byte| !byte.is_ascii_whitespace())?;
        Some((row, offset))
    })?;
    let closing = match stage.line_bytes(first_row)[first_byte] {
        b'{' => b'}',
        b'[' => b']',
        _ => return None,
    };
    let (last_row, last_byte) = (first_row..stage.len()).rev().find_map(|row| {
        let offset = stage.line_bytes(row).iter().rposition(|byte| !byte.is_ascii_whitespace())?;
        Some((row, offset))
    })?;
    if stage.line_bytes(last_row)[last_byte] != closing {
        return None;
    }
    let clicked = (point.row, col_to_byte(stage.line(point.row), point.col));
    let mut stack = Vec::new();
    let mut scopes = Vec::new();
    for row in first_row..=last_row {
        let line = stage.line_bytes(row);
        let mut byte = if row == first_row { first_byte } else { 0 };
        while let Some(&ch) = line.get(byte) {
            byte += 1;
            match ch {
                b'"' => loop {
                    byte += memchr2(b'"', b'\\', line.get(byte..)?)? + 1;
                    if line[byte - 1] == b'"' {
                        break;
                    }
                    byte += 1;
                },
                b'{' | b'[' => stack.push((ch, (row, byte - 1))),
                b'}' | b']' => {
                    let (opening, start) = stack.pop()?;
                    if !matches!((opening, ch), (b'{', b'}') | (b'[', b']')) {
                        return None;
                    }
                    let end = (row, byte);
                    if start <= clicked && clicked < end {
                        scopes.push((start, end));
                    }
                    if stack.is_empty() {
                        return (row == last_row && byte - 1 == last_byte).then_some(scopes);
                    }
                }
                _ => {}
            }
        }
    }
    None
}
