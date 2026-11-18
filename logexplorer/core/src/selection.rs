//! The selection in the view and the text it stands for.

use crate::operator::Prefill;
use crate::stage::Stage;
use crate::text::{cols_to_bytes, slice_cols};
use rayon::prelude::*;
use std::io::Write;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Default)]
pub struct Point {
    pub row: usize,
    pub col: usize,
}

impl Point {
    pub fn new(row: usize, col: usize) -> Self {
        Point { row, col }
    }
}

/// From where the selection was started to where it was extended (either may come first).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Selection {
    pub start: Point,
    pub end: Point,
}

impl Selection {
    pub fn new(start: Point, end: Point) -> Self {
        Selection { start, end }
    }

    pub fn caret(point: Point) -> Self {
        Selection { start: point, end: point }
    }

    /// With the earlier point first
    pub fn normalized(&self) -> Selection {
        if self.start <= self.end {
            *self
        } else {
            Selection { start: self.end, end: self.start }
        }
    }

    pub fn is_empty(&self) -> bool {
        self.start == self.end
    }

    /// Selected columns of `row` of a normalized selection: `(start, end)`, `usize::MAX` to
    /// the end of the row
    pub fn cols_in_row(&self, row: usize) -> Option<(usize, usize)> {
        if row < self.start.row || row > self.end.row {
            return None;
        }
        Some(match (row == self.start.row, row == self.end.row) {
            (true, true) => (self.start.col, self.end.col),
            (true, false) => (self.start.col, usize::MAX),
            (false, true) => (0, self.end.col),
            (false, false) => (0, usize::MAX),
        })
    }
}

/// The selected text, rows joined by `\n`
pub fn selection_text(stage: &Stage, selection: &Selection) -> String {
    let selection = selection.normalized();
    let last = selection.end.row.min(stage.len().saturating_sub(1));
    let mut out = String::new();
    for row in selection.start.row..=last {
        if row > selection.start.row {
            out.push('\n');
        }
        let (start, end) = selection.cols_in_row(row).unwrap_or((0, 0));
        out.push_str(slice_cols(stage.line(row), start, end));
    }
    out
}

/// Characters of [`selection_text`], without building it
pub fn selection_chars(stage: &Stage, selection: &Selection) -> u64 {
    let selection = selection.normalized();
    if stage.is_empty() {
        return 0;
    }
    let last = selection.end.row.min(stage.len() - 1);
    if selection.start.row > last {
        return 0;
    }
    let row_chars = |row| {
        let (start, end) = selection.cols_in_row(row).unwrap_or((0, 0));
        let cols = stage.line_cols(row);
        (end.min(cols).saturating_sub(start.min(cols))) as u64
    };
    let rows = selection.start.row..last + 1;
    let chars: u64 = if rows.len() < (1 << 14) { rows.map(row_chars).sum() } else { rows.into_par_iter().with_min_len(1 << 14).map(row_chars).sum() };
    chars + (last - selection.start.row) as u64
}

/// The selected columns of every row of the stage
pub fn column_text(stage: &Stage, selection: &Selection) -> String {
    let selection = selection.normalized();
    block_rows(stage, 0, stage.len(), selection.start.col, selection.end.col)
}

/// The selected rectangle
pub fn block_text(stage: &Stage, selection: &Selection) -> String {
    let selection = selection.normalized();
    block_rows(stage, selection.start.row, (selection.end.row + 1).min(stage.len()), selection.start.col, selection.end.col)
}

fn block_rows(stage: &Stage, from_row: usize, to_row: usize, start_col: usize, end_col: usize) -> String {
    if end_col <= start_col {
        return String::new();
    }
    let mut out = String::new();
    for row in from_row..to_row {
        if row > from_row {
            out.push('\n');
        }
        out.push_str(slice_cols(stage.line(row), start_col, end_col));
    }
    out
}

/// All rows joined by `\n`
pub fn stage_text(stage: &Stage) -> String {
    let mut out = String::with_capacity(stage.text_bytes() as usize);
    for row in 0..stage.len() {
        if row > 0 {
            out.push('\n');
        }
        out.push_str(stage.line(row));
    }
    out
}

/// Writes all rows joined by `\n`, without building the text in memory
pub fn write_stage(stage: &Stage, writer: &mut impl Write) -> std::io::Result<()> {
    let mut writer = std::io::BufWriter::with_capacity(1 << 20, writer);
    for row in 0..stage.len() {
        if row > 0 {
            writer.write_all(b"\n")?;
        }
        writer.write_all(stage.line_bytes(row))?;
    }
    writer.flush()
}

/// What the selection offers a new operation
pub fn prefill(stage: Option<&Stage>, selection: Option<&Selection>) -> Prefill {
    let max_width = stage.map_or(1, |stage| stage.max_cols.max(1) as usize);
    let (Some(stage), Some(selection)) = (stage, selection) else {
        return Prefill { max_width, ..Default::default() };
    };
    let selection = selection.normalized();
    let selected_text = selection_text(stage, &selection);
    let has_selection = !selected_text.is_empty();
    Prefill {
        start_row: selection.start.row,
        end_row: selection.end.row,
        start_col: if has_selection { selection.start.col } else { 0 },
        width: selection.end.col.saturating_sub(selection.start.col),
        max_width,
        has_selection,
        selected_text,
    }
}

/// The selection as byte offsets within one row, when it lies in a single row
pub fn single_row_bytes(stage: &Stage, selection: &Selection) -> Option<(usize, usize, usize)> {
    let selection = selection.normalized();
    (selection.start.row == selection.end.row && selection.start.col != selection.end.col).then(|| {
        let (start, end) = cols_to_bytes(stage.line(selection.start.row), selection.start.col, selection.end.col);
        (selection.start.row, start, end)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stage::testing::*;

    fn select(start: (usize, usize), end: (usize, usize)) -> Selection {
        Selection::new(Point::new(start.0, start.1), Point::new(end.0, end.1))
    }

    #[test]
    fn the_selected_text_spans_rows_in_either_direction() {
        let stage = stage_of(&["first row", "žluťoučký", "last row"]);
        assert_eq!(selection_text(&stage, &select((0, 6), (2, 4))), "row\nžluťoučký\nlast");
        assert_eq!(selection_text(&stage, &select((2, 4), (0, 6))), "row\nžluťoučký\nlast");
        assert_eq!(selection_text(&stage, &select((1, 1), (1, 4))), "luť");
        assert_eq!(selection_chars(&stage, &select((0, 6), (2, 4))), 3 + 1 + 9 + 1 + 4);
        assert_eq!(selection_chars(&stage, &select((1, 1), (1, 4))), 3);
        assert_eq!(selection_chars(&stage, &select((1, 3), (1, 3))), 0);
    }

    #[test]
    fn a_column_and_a_block_take_the_same_columns_of_many_rows() {
        let stage = stage_of(&["a 10 x", "b 20 y", "c", "d 40 z"]);
        let selection = select((1, 2), (3, 4));
        assert_eq!(column_text(&stage, &selection), "10\n20\n\n40");
        assert_eq!(block_text(&stage, &selection), "20\n\n40");
        assert_eq!(stage_text(&stage), "a 10 x\nb 20 y\nc\nd 40 z");
        let mut written = Vec::new();
        write_stage(&stage, &mut written).unwrap();
        assert_eq!(written, stage_text(&stage).into_bytes());
    }

    #[test]
    fn the_selection_prefills_an_operation() {
        let stage = stage_of(&["afr  95.6%", "amh  48.6%"]);
        let filled = prefill(Some(&stage), Some(&select((0, 5), (0, 9))));
        assert_eq!((filled.selected_text.as_str(), filled.start_col, filled.width, filled.has_selection, filled.max_width), ("95.6", 5, 4, true, 10));
        let caret = prefill(Some(&stage), Some(&select((1, 3), (1, 3))));
        assert_eq!((caret.has_selection, caret.start_col, caret.max_width), (false, 0, 10));
        assert_eq!(prefill(None, None).max_width, 1);
    }
}
