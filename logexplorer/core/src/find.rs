//! Find in the current view: the next or previous occurrence of a text, wrapping around.

use crate::literal::Needle;
use crate::selection::Point;
use crate::stage::{scan_line_end, LineIndex, Rows, Stage};
use crate::text::{as_str, byte_to_col, col_to_byte};
use rayon::prelude::*;

const SCAN_BLOCK: usize = 4 << 20;
const NEAR_ROWS: usize = 4096;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Match {
    pub row: usize,
    pub start_col: usize,
    pub end_col: usize,
    /// Found only after wrapping around the end (or start) of the view
    pub wrapped: bool,
}

fn found(stage: &Stage, row: usize, bytes: (usize, usize), wrapped: bool) -> Match {
    let line = as_str(stage.line_bytes(row));
    Match { row, start_col: byte_to_col(line, bytes.0), end_col: byte_to_col(line, bytes.1), wrapped }
}

/// Rounds `target` forward to the start of the next line inside `bytes[..range_end]`.
fn line_boundary_after(bytes: &[u8], target: usize, range_end: usize) -> usize {
    if target >= range_end {
        return range_end;
    }
    scan_line_end(bytes, target, range_end).1
}

/// Rounds `target` backward to a line start inside `bytes[range_start..range_end]`.
fn line_boundary_before(bytes: &[u8], target: usize, range_start: usize, range_end: usize) -> usize {
    if target <= range_start {
        return range_start;
    }
    match memchr::memrchr2(b'\n', b'\r', &bytes[range_start..target]) {
        None => range_start,
        Some(rel) => {
            let pos = range_start + rel;
            if bytes[pos] == b'\r' && pos + 1 < range_end && bytes[pos + 1] == b'\n' {
                pos + 2
            } else {
                pos + 1
            }
        }
    }
}

/// Searches `bytes[range_start..range_end]` forward in line-aligned 4 MiB blocks with immediate early-exit.
fn scan_forward_bytes(bytes: &[u8], needle: &Needle, range_start: usize, range_end: usize) -> Option<(usize, usize)> {
    if range_start >= range_end {
        return None;
    }
    if range_end - range_start <= SCAN_BLOCK {
        return needle.find_at(&bytes[..range_end], range_start);
    }
    // Check the first 4 MiB single-threaded so nearby hits return immediately without waking rayon.
    let first_cut = line_boundary_after(bytes, range_start + SCAN_BLOCK, range_end);
    if let Some(hit) = needle.find_at(&bytes[..first_cut], range_start) {
        return Some(hit);
    }
    let mut segments = Vec::new();
    let mut start = first_cut;
    while start < range_end {
        let cut = line_boundary_after(bytes, (start + SCAN_BLOCK).min(range_end), range_end);
        segments.push((start, cut));
        start = cut;
    }
    segments.par_iter().find_map_first(|&(s, e)| needle.find_at(&bytes[..e], s))
}

/// Searches `bytes[range_start..range_end]` backward in line-aligned 4 MiB blocks with immediate early-exit.
fn scan_backward_bytes(bytes: &[u8], needle: &Needle, range_start: usize, range_end: usize) -> Option<(usize, usize)> {
    if range_start >= range_end {
        return None;
    }
    let rfind_seg = |s: usize, e: usize| needle.rfind_from(&bytes[s..e], e - s).map(|(a, b)| (s + a, s + b));
    if range_end - range_start <= SCAN_BLOCK {
        return rfind_seg(range_start, range_end);
    }
    // Check the nearest 4 MiB single-threaded first.
    let last_cut = line_boundary_before(bytes, range_end - SCAN_BLOCK, range_start, range_end);
    if let Some(hit) = rfind_seg(last_cut, range_end) {
        return Some(hit);
    }
    let mut segments = Vec::new();
    let mut end = last_cut;
    while end > range_start {
        let target = end.saturating_sub(SCAN_BLOCK).max(range_start);
        let cut = line_boundary_before(bytes, target, range_start, end);
        // Guard against a single >4 MiB line where `cut` lands at `end`
        let seg_start = if cut >= end { range_start } else { cut };
        segments.push((seg_start, end));
        end = seg_start;
    }
    segments.reverse();
    segments.par_iter().find_map_last(|&(s, e)| rfind_seg(s, e))
}

fn match_at_offset(stage: &Stage, index: &LineIndex, bytes: &[u8], hit: (usize, usize), wrapped: bool) -> Match {
    let (row, line_start) = index.row_of_offset(hit.0, bytes);
    found(stage, row, (hit.0 - line_start, hit.1 - line_start), wrapped)
}

/// The first match at or after `from`, then from the top down to `from`
pub fn find_forward(stage: &Stage, needle: &Needle, from: Point) -> Option<Match> {
    if stage.is_empty() || needle.is_empty() {
        return None;
    }
    let from_byte = col_to_byte(stage.line(from.row), from.col);
    if let Some(bytes) = needle.find_at(stage.line_bytes(from.row), from_byte) {
        return Some(found(stage, from.row, bytes, false));
    }
    if !needle.contains_line_break() {
        if let Rows::Lines(index) = &stage.rows {
            let bytes = stage.bufs[index.buf as usize].bytes();
            let region_end = index.end as usize;
            let from_start = index.row_start(from.row, bytes);
            let (_, after_from) = scan_line_end(bytes, from_start, region_end);
            if from.row + 1 < stage.len() {
                if let Some(hit) = scan_forward_bytes(bytes, needle, after_from, region_end) {
                    return Some(match_at_offset(stage, index, bytes, hit, false));
                }
            }
            if from.row > 0 {
                let first_start = index.row_start(0, bytes);
                if let Some(hit) = scan_forward_bytes(bytes, needle, first_start, from_start) {
                    return Some(match_at_offset(stage, index, bytes, hit, true));
                }
            }
            return needle.find_at(stage.line_bytes(from.row), 0).filter(|bytes| bytes.0 < from_byte).map(|bytes| found(stage, from.row, bytes, true));
        }
    }
    let first_in = |row: usize| needle.find_at(stage.line_bytes(row), 0);
    // Check a small window sequentially first so nearby matches never wake rayon or materialize dense starts.
    let near_end = (from.row + 1 + NEAR_ROWS).min(stage.len());
    for row in from.row + 1..near_end {
        if let Some(bytes) = first_in(row) {
            return Some(found(stage, row, bytes, false));
        }
    }
    if stage.len() >= 16_384 {
        stage.ensure_random_access();
    }
    if let Some(row) = (near_end..stage.len()).into_par_iter().with_min_len(1024).find_first(|&row| first_in(row).is_some()) {
        return Some(found(stage, row, first_in(row)?, false));
    }
    if let Some(row) = (0..from.row).into_par_iter().with_min_len(1024).find_first(|&row| first_in(row).is_some()) {
        return Some(found(stage, row, first_in(row)?, true));
    }
    first_in(from.row).filter(|bytes| bytes.0 < from_byte).map(|bytes| found(stage, from.row, bytes, true))
}

/// The last match starting before `from`, then from the bottom up to `from`
pub fn find_backward(stage: &Stage, needle: &Needle, from: Point) -> Option<Match> {
    if stage.is_empty() || needle.is_empty() {
        return None;
    }
    let from_byte = col_to_byte(stage.line(from.row), from.col);
    if from_byte > 0 {
        if let Some(bytes) = needle.rfind_from(stage.line_bytes(from.row), from_byte - 1) {
            return Some(found(stage, from.row, bytes, false));
        }
    }
    if !needle.contains_line_break() {
        if let Rows::Lines(index) = &stage.rows {
            let bytes = stage.bufs[index.buf as usize].bytes();
            let region_end = index.end as usize;
            let from_start = index.row_start(from.row, bytes);
            if from.row > 0 {
                let first_start = index.row_start(0, bytes);
                if let Some(hit) = scan_backward_bytes(bytes, needle, first_start, from_start) {
                    return Some(match_at_offset(stage, index, bytes, hit, false));
                }
            }
            if from.row + 1 < stage.len() {
                let (_, after_from) = scan_line_end(bytes, from_start, region_end);
                if let Some(hit) = scan_backward_bytes(bytes, needle, after_from, region_end) {
                    return Some(match_at_offset(stage, index, bytes, hit, true));
                }
            }
            return needle
                .rfind_from(stage.line_bytes(from.row), usize::MAX - 64)
                .filter(|bytes| bytes.0 >= from_byte)
                .map(|bytes| found(stage, from.row, bytes, true));
        }
    }
    let last_in = |row: usize| needle.rfind_from(stage.line_bytes(row), usize::MAX - 64);
    let near_start = from.row.saturating_sub(NEAR_ROWS);
    for row in (near_start..from.row).rev() {
        if let Some(bytes) = last_in(row) {
            return Some(found(stage, row, bytes, false));
        }
    }
    if stage.len() >= 16_384 {
        stage.ensure_random_access();
    }
    if let Some(row) = (0..near_start).into_par_iter().with_min_len(1024).find_last(|&row| last_in(row).is_some()) {
        return Some(found(stage, row, last_in(row)?, false));
    }
    if let Some(row) = (from.row + 1..stage.len()).into_par_iter().with_min_len(1024).find_last(|&row| last_in(row).is_some()) {
        return Some(found(stage, row, last_in(row)?, true));
    }
    last_in(from.row).filter(|bytes| bytes.0 >= from_byte).map(|bytes| found(stage, from.row, bytes, true))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stage::testing::*;

    #[test]
    fn find_walks_the_matches_and_wraps() {
        let stage = stage_of(&["x ab", "none", "ab ab", "žž AB"]);
        let needle = Needle::new("ab", true);
        let at = |row, col| Point::new(row, col);
        let hit = |row, start_col, wrapped| Some(Match { row, start_col, end_col: start_col + 2, wrapped });
        assert_eq!(find_forward(&stage, &needle, at(0, 0)), hit(0, 2, false));
        assert_eq!(find_forward(&stage, &needle, at(0, 4)), hit(2, 0, false));
        assert_eq!(find_forward(&stage, &needle, at(2, 2)), hit(2, 3, false));
        assert_eq!(find_forward(&stage, &needle, at(2, 5)), hit(0, 2, true));
        assert_eq!(find_backward(&stage, &needle, at(2, 3)), hit(2, 0, false));
        assert_eq!(find_backward(&stage, &needle, at(2, 0)), hit(0, 2, false));
        assert_eq!(find_backward(&stage, &needle, at(0, 2)), hit(2, 3, true));
        // Without case the last row matches too, at its column (not byte) 3
        let loose = Needle::new("ab", false);
        assert_eq!(find_forward(&stage, &loose, at(2, 5)), hit(3, 3, false));
        assert_eq!(find_forward(&stage, &Needle::new("zzz", true), at(0, 0)), None);
        // The only match is found again from itself, wrapped
        let single = stage_of(&["only ab"]);
        assert_eq!(find_forward(&single, &needle, at(0, 7)), hit(0, 5, true));
        assert_eq!(find_backward(&single, &needle, at(0, 5)), hit(0, 5, true));
    }

    #[test]
    fn block_find_handles_large_files_across_4mib_segment_boundaries() {
        let mut bytes = Vec::with_capacity(9 << 20);
        let pad = b"2026-03-13 09:14:02 INFO regular log line with padding bytes 0123456789\r\n";
        let mut target_rows = Vec::new();
        for row in 0..130_000usize {
            if row == 100 || row == 65_000 || row == 129_900 {
                target_rows.push(row);
                bytes.extend_from_slice(b"2026-03-13 09:14:02 WARN TargetMarker here\r\n");
            } else {
                bytes.extend_from_slice(pad);
            }
        }
        let stage = crate::load::source_from_bytes(bytes, "big", "big").unwrap().stage;
        let needle = Needle::new("targetmarker", false);
        assert_eq!(find_forward(&stage, &needle, Point::new(0, 0)), Some(Match { row: target_rows[0], start_col: 25, end_col: 37, wrapped: false }));
        assert_eq!(find_forward(&stage, &needle, Point::new(target_rows[0], 30)), Some(Match { row: target_rows[1], start_col: 25, end_col: 37, wrapped: false }));
        assert_eq!(find_forward(&stage, &needle, Point::new(target_rows[1], 30)), Some(Match { row: target_rows[2], start_col: 25, end_col: 37, wrapped: false }));
        assert_eq!(find_forward(&stage, &needle, Point::new(target_rows[2], 30)), Some(Match { row: target_rows[0], start_col: 25, end_col: 37, wrapped: true }));

        assert_eq!(find_backward(&stage, &needle, Point::new(target_rows[2], 0)), Some(Match { row: target_rows[1], start_col: 25, end_col: 37, wrapped: false }));
        assert_eq!(find_backward(&stage, &needle, Point::new(target_rows[1], 0)), Some(Match { row: target_rows[0], start_col: 25, end_col: 37, wrapped: false }));
        assert_eq!(find_backward(&stage, &needle, Point::new(target_rows[0], 0)), Some(Match { row: target_rows[2], start_col: 25, end_col: 37, wrapped: true }));
    }
}
