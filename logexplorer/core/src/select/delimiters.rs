//! Delimiter pairs that may start or end outside the click window.

use super::{xml, BytePosition, Delimited};
use crate::stage::Stage;
use crate::text::char_len;
use memchr::{memchr2, memchr_iter, memmem};

pub(super) fn is_escaped(line: &[u8], byte: usize) -> bool {
    line[..byte].iter().rev().take_while(|&&ch| ch == b'\\').count() % 2 == 1
}

pub(super) fn single_delimiters(start: BytePosition, end: BytePosition) -> Delimited<BytePosition> {
    Delimited { start, end, content_start: (start.0, start.1 + 1), content_end: (end.0, end.1 - 1) }
}

/// Like the local quote rule, use the closest unescaped quotes of the same kind on this row.
pub(super) fn quoted(stage: &Stage, (row, byte): BytePosition) -> Option<Delimited<BytePosition>> {
    let line = stage.line_bytes(row);
    let at = line.get(byte).copied().filter(|ch| b"\"'`".contains(ch) && !is_escaped(line, byte));
    let quotes = at.as_ref().map_or(b"\"'`".as_slice(), std::slice::from_ref);
    for &quote in quotes {
        let left = memchr_iter(quote, &line[..byte]).rev().find(|&col| !is_escaped(line, col));
        let from = byte + usize::from(at.is_some());
        let right = memchr_iter(quote, &line[from..]).map(|col| from + col).find(|&col| !is_escaped(line, col));
        let pair = match (at, left, right) {
            (Some(_), Some(left), Some(right)) if char_len(&line[left..byte]) < char_len(&line[byte..right]) => Some((left, byte)),
            (Some(_), _, Some(right)) => Some((byte, right)),
            (Some(_), Some(left), None) => Some((left, byte)),
            (None, Some(left), Some(right)) if byte < right => Some((left, right)),
            _ => None,
        };
        if let Some((left, right)) = pair {
            return Some(single_delimiters((row, left), (row, right + 1)));
        }
    }
    None
}

/// Match brackets using the same tolerant rule as the local window, including in plain logs
/// and JSON fragments. Quoted strings hide brackets on their row and opaque markup can span rows.
pub(super) fn brackets(stage: &Stage, clicked: BytePosition, bounds: Option<(BytePosition, BytePosition)>, mut emit: impl FnMut(Delimited<BytePosition>)) {
    let mut opens = [Vec::new(), Vec::new(), Vec::new()];
    // Beyond the click, we only need the depth, since those opening positions cannot enclose it.
    let mut depths = [0; 3];
    let mut opaque_end = None;
    let last = stage.len() - 1;
    let (first, last) = bounds.unwrap_or(((0, 0), (last, stage.line_bytes(last).len())));
    for row in first.0..=last.0 {
        let line = stage.line_bytes(row);
        let line = if row == last.0 { &line[..last.1] } else { line };
        let mut byte = if row == first.0 { first.1 } else { 0 };
        'line: while byte < line.len() {
            if let Some(suffix) = opaque_end {
                let Some(end) = memmem::find(&line[byte..], suffix) else { break };
                byte += end + suffix.len();
                opaque_end = None;
                continue;
            }
            let ch = line[byte];
            let position = (row, byte);
            byte += 1;
            let kind = match ch {
                b'(' | b')' => 0,
                b'[' | b']' => 1,
                b'{' | b'}' => 2,
                b'"' | b'\'' | b'`' => {
                    loop {
                        let Some(next) = line.get(byte..).and_then(|rest| memchr2(ch, b'\\', rest)) else { break 'line };
                        byte += next + 1;
                        if line[byte - 1] == ch {
                            break;
                        }
                        byte += 1;
                    }
                    continue;
                }
                b'<' => {
                    if let Some((prefix, suffix)) = xml::opaque_delimiters(&line[position.1..]) {
                        opaque_end = Some(suffix);
                        byte = position.1 + prefix.len();
                    }
                    continue;
                }
                _ => continue,
            };
            if matches!(ch, b'(' | b'[' | b'{') {
                if position <= clicked {
                    opens[kind].push(position);
                } else {
                    depths[kind] += 1;
                }
            } else if depths[kind] > 0 {
                depths[kind] -= 1;
            } else if let Some(start) = opens[kind].pop() {
                if position >= clicked {
                    emit(single_delimiters(start, (row, byte)));
                }
            }
        }
        if row >= clicked.0 && opens.iter().all(Vec::is_empty) {
            break;
        }
    }
}
