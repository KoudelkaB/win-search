//! Filter: keeps the rows that contain any Include text and no Exclude text.

use super::par_chunks;
use crate::cancel::{Cancel, OpResult};
use crate::literal::LiteralSet;
use crate::operator::FilterOp;
use crate::stage::{scan_line_end, LineIndex, Rows, SourceDesc, SrcIds, Stage, LINE_INDEX_STRIDE};
use crate::text::char_len;
use rayon::prelude::*;

const CHUNK_FILTER_MIN_ROWS: usize = 16_384;

/// A filter ready to test rows; also colors the rows the next filter would keep.
pub struct FilterMatcher {
    include: LiteralSet,
    exclude: LiteralSet,
    /// Per source of the stage: whether the filter applies to it; `None` for all
    allowed: Option<Vec<bool>>,
}

impl FilterMatcher {
    pub fn new(op: &FilterOp, sources: &[SourceDesc]) -> Self {
        let keys: Vec<&String> = op.sources.iter().filter(|key| !key.is_empty()).collect();
        FilterMatcher {
            include: LiteralSet::new(&op.include),
            exclude: LiteralSet::new(&op.exclude),
            allowed: (!keys.is_empty()).then(|| sources.iter().map(|source| keys.contains(&&source.filter_key())).collect()),
        }
    }

    #[inline]
    fn allows_src(&self, src: u16) -> bool {
        match &self.allowed {
            None => true,
            Some(allowed) => allowed.get(src as usize).copied().unwrap_or(false),
        }
    }

    #[inline]
    pub fn matches_line(&self, stage: &Stage, row: usize, line: &[u8]) -> bool {
        if !self.allows_src(stage.src(row)) {
            return false;
        }
        (!self.include.has_clauses() || self.include.is_match(line)) && !self.exclude.is_match(line)
    }

    #[inline]
    pub fn matches(&self, stage: &Stage, row: usize) -> bool {
        if !self.allows_src(stage.src(row)) {
            return false;
        }
        let line = stage.line_bytes(row);
        (!self.include.has_clauses() || self.include.is_match(line)) && !self.exclude.is_match(line)
    }
}

/// Scans contiguous multi-line byte blocks directly with `memmem`/`AhoCorasick`, then maps hits to rows.
fn apply_chunk_include(stage: &Stage, index: &LineIndex, matcher: &FilterMatcher, cancel: &Cancel) -> OpResult<Stage> {
    let buf = &stage.bufs[index.buf as usize];
    let bytes = buf.bytes();
    let ascii = buf.ascii;
    let region_end = index.end as usize;
    let total_rows = index.len();
    let num_cp = index.checkpoints.len();
    let cp_step = (num_cp / (rayon::current_num_threads() * 16)).clamp(16, 1024);
    let cp_ranges: Vec<(usize, usize)> = (0..num_cp).step_by(cp_step).map(|start| (start, (start + cp_step).min(num_cp))).collect();
    let has_exclude = matcher.exclude.has_clauses();

    let kept: OpResult<Vec<(Vec<u32>, u64)>> = cp_ranges
        .into_par_iter()
        .map(|(cp_start, cp_end)| {
            cancel.check()?;
            let r_start = cp_start * LINE_INDEX_STRIDE;
            let r_end = (cp_end * LINE_INDEX_STRIDE).min(total_rows);
            let byte_start = index.checkpoints.get(cp_start) as usize;
            let byte_end = if cp_end >= num_cp { region_end } else { index.checkpoints.get(cp_end) as usize };
            let hay = &bytes[byte_start..byte_end];
            let mut rows = Vec::new();
            let mut max_cols = 0u64;
            let mut cur_row = r_start;
            let mut cur_pos = byte_start;
            let mut cursor = 0usize;

            while let Some(rel) = matcher.include.find_start_at(hay, cursor) {
                let abs_pos = byte_start + rel;
                let next_cp = cur_row / LINE_INDEX_STRIDE + 1;
                if next_cp < num_cp && (index.checkpoints.get(next_cp) as usize) <= abs_pos {
                    let (row, line_start) = index.row_of_offset(abs_pos, bytes);
                    cur_row = row;
                    cur_pos = line_start;
                } else {
                    while cur_row + 1 < r_end {
                        let (_, next) = scan_line_end(bytes, cur_pos, region_end);
                        if next > abs_pos {
                            break;
                        }
                        cur_pos = next;
                        cur_row += 1;
                    }
                }
                let (line_end, next_start) = scan_line_end(bytes, cur_pos, region_end);
                let line = &bytes[cur_pos..line_end];
                if !has_exclude || !matcher.exclude.is_match(line) {
                    rows.push(cur_row as u32);
                    let cols = if ascii { line.len() as u64 } else { char_len(line) as u64 };
                    max_cols = max_cols.max(cols);
                }
                cur_row += 1;
                cur_pos = next_start;
                if cur_row >= r_end || next_start >= byte_end {
                    break;
                }
                cursor = next_start - byte_start;
            }
            Ok((rows, max_cols))
        })
        .collect();

    let kept = kept?;
    let total: usize = kept.iter().map(|(rows, _)| rows.len()).sum();
    let max_cols = kept.iter().map(|(_, cols)| *cols).max().unwrap_or(0);
    let mut origin = Vec::with_capacity(total);
    for (chunk, _) in kept {
        origin.extend_from_slice(&chunk);
    }
    Ok(stage.derive_rows("Filter", origin, Some(max_cols)))
}

pub fn apply(stage: &Stage, op: &FilterOp, cancel: &Cancel) -> OpResult<Stage> {
    let matcher = FilterMatcher::new(op, &stage.sources);
    if matcher.include.matches_nothing() {
        return Ok(stage.derive_rows("Filter", Vec::new(), Some(0)));
    }
    if let SrcIds::Uniform(src_id) = stage.src {
        if !matcher.allows_src(src_id) {
            return Ok(stage.derive_rows("Filter", Vec::new(), Some(0)));
        }
        if !matcher.include.has_clauses() && !matcher.exclude.has_clauses() {
            return Ok(stage.copy_as("Filter"));
        }
        if let Rows::Lines(index) = &stage.rows {
            if stage.len() >= CHUNK_FILTER_MIN_ROWS && matcher.include.has_clauses() && matcher.include.can_chunk_scan() {
                return apply_chunk_include(stage, index, &matcher, cancel);
            }
        }
    }
    let kept = par_chunks(stage.len(), cancel, |range| {
        let mut rows = Vec::new();
        let mut max_cols = 0u64;
        stage.for_each_row(range, |row, span, line| {
            if matcher.matches_line(stage, row, line) {
                rows.push(row as u32);
                let cols = if span.is_empty() {
                    0
                } else if stage.bufs[span.buf() as usize].ascii {
                    span.len() as u64
                } else {
                    char_len(line) as u64
                };
                max_cols = max_cols.max(cols);
            }
        });
        Ok((rows, max_cols))
    })?;
    let total: usize = kept.iter().map(|(rows, _)| rows.len()).sum();
    let max_cols = kept.iter().map(|(_, cols)| *cols).max().unwrap_or(0);
    let mut origin = Vec::with_capacity(total);
    for (chunk, _) in kept {
        origin.extend_from_slice(&chunk);
    }
    Ok(stage.derive_rows("Filter", origin, Some(max_cols)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::load::{merge_append, Bundle};
    use crate::operator::Clause;
    use crate::stage::testing::*;

    fn filter(include: &[&str], exclude: &[&str]) -> FilterOp {
        FilterOp { include: include.iter().map(|text| Clause::new(text)).collect(), exclude: exclude.iter().map(|text| Clause::new(text)).collect(), sources: vec![] }
    }

    #[test]
    fn include_is_or_and_exclude_removes() {
        let stage = stage_of(&["10 START /api", "11 END /api", "12 START /health", "13 info"]);
        let out = apply(&stage, &filter(&["start\nEND"], &["/health"]), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["10 START /api", "11 END /api"]);
        assert_eq!(row_ids_of(&out), [0, 1]);
        // No text sharing is lost: the result adds no buffer
        assert_eq!(out.bufs.len(), stage.bufs.len());
    }

    #[test]
    fn a_filter_without_text_keeps_everything_and_case_can_matter() {
        let stage = stage_of(&["Error", "error"]);
        assert_eq!(apply(&stage, &filter(&[""], &[]), &Cancel::new()).unwrap().len(), 2);
        let mut exact = filter(&["Error"], &[]);
        exact.include[0].case_sensitive = true;
        assert_eq!(lines_of(&apply(&stage, &exact, &Cancel::new()).unwrap()), ["Error"]);
    }

    #[test]
    fn only_in_files_limits_the_filter_to_chosen_sources() {
        let (a, b) = (stage_named(&["x a", "y a"], "a.log"), stage_named(&["x b"], "b.log"));
        let merged = merge_append(&[Bundle::of(&a), Bundle::of(&b)]).unwrap();
        let mut op = filter(&["x"], &[]);
        op.sources = vec![merged.sources[1].filter_key()];
        let out = apply(&merged, &op, &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["x b"]);
        assert_eq!(srcs_of(&out), [1]);
        assert_eq!(row_ids_of(&out), [2]);
    }

    #[test]
    fn chunk_filter_matches_row_by_row_for_sparse_and_dense_patterns() {
        let mut text = String::new();
        for row in 0..25_000usize {
            if row > 0 {
                text.push_str(if row % 2 == 0 { "\r\n" } else { "\n" });
            }
            if row % 500 == 7 {
                text.push_str(&format!("[{row}] ERROR ERROR failed on /api"));
            } else if row % 2 == 0 {
                text.push_str(&format!("[{row}] info /api/orders ok"));
            } else if row % 17 == 0 {
                text.push_str(&format!("[{row}] WARN /health ignored"));
            } else {
                text.push_str(&format!("[{row}] debug trace"));
            }
        }
        let stage = crate::load::source_from_bytes(text.into_bytes(), "big", "big").unwrap().stage;
        for op in [
            filter(&["error\nWARN"], &["/health"]),
            filter(&["/api"], &[]),
            filter(&["nonexistent"], &[]),
            FilterOp { include: vec![Clause { text: "ERROR".into(), case_sensitive: true }], exclude: vec![], sources: vec![] },
        ] {
            let matcher = FilterMatcher::new(&op, &stage.sources);
            let expected: Vec<u32> = (0..stage.len()).filter(|&row| matcher.matches(&stage, row)).map(|row| row as u32).collect();
            let actual = apply(&stage, &op, &Cancel::new()).unwrap();
            assert_eq!(row_ids_of(&actual), expected);
            assert!(matches!(actual.rows, Rows::Indexed { .. }));
        }
    }
}
