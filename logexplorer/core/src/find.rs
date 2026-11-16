//! Find in the current view: the next or previous occurrence of a text, wrapping around.

use crate::literal::Needle;
use crate::selection::Point;
use crate::stage::Stage;
use crate::text::{as_str, byte_to_col, col_to_byte};
use rayon::prelude::*;

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

/// The first match at or after `from`, then from the top down to `from`
pub fn find_forward(stage: &Stage, needle: &Needle, from: Point) -> Option<Match> {
    if stage.is_empty() || needle.is_empty() {
        return None;
    }
    let from_byte = col_to_byte(stage.line(from.row), from.col);
    if let Some(bytes) = needle.find_at(stage.line_bytes(from.row), from_byte) {
        return Some(found(stage, from.row, bytes, false));
    }
    let first_in = |row: usize| needle.find_at(stage.line_bytes(row), 0);
    if let Some(row) = (from.row + 1..stage.len()).into_par_iter().with_min_len(1024).find_first(|&row| first_in(row).is_some()) {
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
    let last_in = |row: usize| needle.rfind_from(stage.line_bytes(row), usize::MAX - 64);
    if let Some(row) = (0..from.row).into_par_iter().with_min_len(1024).find_last(|&row| last_in(row).is_some()) {
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
}
