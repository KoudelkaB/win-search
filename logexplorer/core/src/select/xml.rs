//! Tolerant tag matching for XML-like text, as used by click selection.

use super::{BytePosition, Delimited};
use crate::selection::{Point, Selection};
use crate::stage::Stage;
use crate::text::{byte_to_col, col_to_byte};
use memchr::{memchr, memmem};

pub(super) const CDATA: &[u8] = b"<![CDATA[";
pub(super) const CDATA_END: &[u8] = b"]]>";

pub(super) fn opaque_delimiters(rest: &[u8]) -> Option<(&'static [u8], &'static [u8])> {
    [(b"<!--".as_slice(), b"-->".as_slice()), (CDATA, CDATA_END), (b"<?", b"?>")].into_iter().find(|(prefix, _)| rest.starts_with(prefix))
}

pub(super) fn cdata_end(text: &[char], start: usize) -> Option<usize> {
    let rest = text.get(start..)?;
    let open = ['<', '!', '[', 'C', 'D', 'A', 'T', 'A', '['];
    rest.starts_with(&open).then(|| {
        let body = &rest[open.len()..];
        body.windows(3).position(|window| window == [']', ']', '>']).map_or(text.len(), |end| start + open.len() + end + 3)
    })
}

/// Both the local selection window and the document scan use these same tag rules.
pub(super) fn scan<'a>(
    lines: impl Iterator<Item = &'a [u8]>,
    through: Option<BytePosition>,
    mut emit_element: impl FnMut(Delimited<BytePosition>),
    mut emit_tag: impl FnMut(BytePosition, BytePosition) -> bool,
) {
    let mut rows = lines.enumerate();
    let Some((mut row, mut line)) = rows.next() else { return };
    let mut position = 0;
    let mut stack = Vec::new();
    let mut next_row = |row: &mut usize, line: &mut &'a [u8], position: &mut usize| {
        let Some((next, text)) = rows.next() else { return false };
        (*row, *line, *position) = (next, text, 0);
        true
    };
    'tags: loop {
        if stack.is_empty() && through.is_some_and(|click| (row, position) > click) {
            return;
        }
        let Some(relative) = memchr(b'<', &line[position..]) else {
            if !next_row(&mut row, &mut line, &mut position) {
                return;
            }
            continue;
        };
        position += relative;
        let start = (row, position);
        if stack.is_empty() && through.is_some_and(|click| start > click) {
            return;
        }
        let rest = &line[position..];
        if let Some((prefix, suffix)) = opaque_delimiters(rest) {
            let angle = prefix == b"<!--"
                || (prefix == b"<?"
                    && rest.get(2..5).is_some_and(|name| name.eq_ignore_ascii_case(b"xml"))
                    && !rest.get(5).is_some_and(|&byte| byte.is_ascii_alphanumeric() || byte == b'_'));
            position += prefix.len();
            let content_start = (row, position);
            loop {
                if let Some(relative) = memmem::find(&line[position..], suffix) {
                    let content_end = (row, position + relative);
                    position += relative + suffix.len();
                    let end = (row, position);
                    if prefix == CDATA {
                        emit_element(Delimited { start, end, content_start, content_end });
                    } else if angle && !emit_tag(start, end) {
                        return;
                    }
                    continue 'tags;
                }
                if !next_row(&mut row, &mut line, &mut position) {
                    return;
                }
            }
        }

        position += 1;
        let closing = line.get(position) == Some(&b'/');
        let declaration = line.get(position) == Some(&b'!');
        let name_start = position + usize::from(closing || declaration);
        if !line.get(name_start).is_some_and(|byte| byte.is_ascii_alphabetic() || *byte == b'_') {
            continue;
        }
        let name_end = name_start + line[name_start..].iter().take_while(|byte| byte.is_ascii_alphanumeric() || b"_:.-".contains(byte)).count();
        if line.get(name_end).is_some_and(|byte| !byte.is_ascii_whitespace() && !b"/>".contains(byte)) {
            continue;
        }
        let name = &line[name_start..name_end];
        position = name_end;
        let mut quote = None;
        loop {
            while let Some(&byte) = line.get(position) {
                if let Some(current) = quote {
                    if byte == current {
                        quote = None;
                    }
                } else {
                    match byte {
                        b'"' | b'\'' => quote = Some(byte),
                        b'<' => continue 'tags,
                        b'>' => {
                            let empty = position > 0 && line[position - 1] == b'/';
                            position += 1;
                            let end = (row, position);
                            if !emit_tag(start, end) {
                                return;
                            }
                            if declaration {
                                continue 'tags;
                            }
                            if closing {
                                if let Some(matching) = stack.iter().rposition(|(open, _, _)| *open == name) {
                                    let (_, start_open, content_start) = stack.remove(matching);
                                    emit_element(Delimited { start: start_open, end, content_start, content_end: start });
                                }
                            } else if empty {
                                emit_element(Delimited { start, end, content_start: end, content_end: end });
                            } else {
                                stack.push((name, start, end));
                            }
                            continue 'tags;
                        }
                        _ => {}
                    }
                }
                position += 1;
            }
            if !next_row(&mut row, &mut line, &mut position) {
                return;
            }
        }
    }
}

pub(super) fn attribute_at(stage: &Stage, point: Point, local_tag: Option<(Point, Point)>, window_complete: bool) -> Option<Selection> {
    let byte_position = |point: Point| (point.row, col_to_byte(stage.line(point.row), point.col));
    let clicked = byte_position(point);
    let (start, end) = if let Some((start, end)) = local_tag {
        (byte_position(start), byte_position(end))
    } else {
        if window_complete {
            return None;
        }
        let mut tag = None;
        scan(
            (0..stage.len()).map(|row| stage.line_bytes(row)),
            Some(clicked),
            |_| {},
            |start, end| {
                if start <= clicked && clicked < end {
                    tag = Some((start, end));
                }
                end <= clicked
            },
        );
        tag?
    };
    let (start, end) = attribute_bounds(stage, clicked, start, end)?;
    let position = |(row, byte)| Point::new(row, byte_to_col(stage.line(row), byte));
    Some(Selection::new(position(start), position(end)))
}

/// Read just the attribute list of a tag that contains the clicked `=`.
fn attribute_bounds(stage: &Stage, clicked: BytePosition, start: BytePosition, end: BytePosition) -> Option<(BytePosition, BytePosition)> {
    let declaration = stage.line_bytes(start.0)[start.1..].get(..5).is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"<?xml"));
    let mut bytes = (start.0..=end.0)
        .flat_map(|row| {
            let line = stage.line_bytes(row);
            let first = if row == start.0 { start.1 + 1 + usize::from(declaration) } else { 0 };
            let last = if row == end.0 { end.1 - 1 } else { line.len() };
            line[first..last].iter().enumerate().map(move |(index, &byte)| ((row, first + index), byte)).chain((row < end.0).then_some(((row, last), b'\n')))
        })
        .peekable();
    let name_start = |&(_, byte): &(BytePosition, u8)| byte.is_ascii_alphabetic() || byte >= 128 || b"_:".contains(&byte);
    let name_byte = |entry: &(BytePosition, u8)| name_start(entry) || entry.1.is_ascii_digit() || b"-.".contains(&entry.1);
    let space = |&(_, byte): &(BytePosition, u8)| byte.is_ascii_whitespace();
    bytes.next_if(name_start)?;
    while bytes.next_if(name_byte).is_some() {}
    loop {
        while bytes.next_if(space).is_some() {}
        let (name, _) = bytes.next_if(name_start)?;
        while bytes.next_if(name_byte).is_some() {}
        while bytes.next_if(space).is_some() {}
        let Some((equals, _)) = bytes.next_if(|&(_, byte)| byte == b'=') else { continue };
        if equals > clicked {
            return None;
        }
        while bytes.next_if(space).is_some() {}
        let (first, byte) = bytes.next()?;
        let last = if matches!(byte, b'"' | b'\'') {
            bytes.find(|&(_, next)| next == byte)?.0
        } else {
            let unquoted = |&(_, byte): &(BytePosition, u8)| !byte.is_ascii_whitespace() && !b"\"'`=<>".contains(&byte);
            if !unquoted(&(first, byte)) {
                return None;
            }
            let mut last = first;
            while let Some((position, _)) = bytes.next_if(unquoted) {
                last = position;
            }
            last
        };
        if equals == clicked {
            return Some((name, (last.0, last.1 + 1)));
        }
        if clicked <= last {
            return None;
        }
    }
}
