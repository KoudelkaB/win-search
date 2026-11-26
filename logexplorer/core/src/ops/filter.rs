//! Filter: keeps the rows that contain any Include text and no Exclude text.

use super::par_chunks;
use crate::cancel::{Cancel, OpResult};
use crate::literal::LiteralSet;
use crate::operator::FilterOp;
use crate::stage::{SourceDesc, Stage};
use crate::text::char_len;

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
    pub fn matches_line(&self, stage: &Stage, row: usize, line: &[u8]) -> bool {
        if let Some(allowed) = &self.allowed {
            if !allowed.get(stage.src(row) as usize).copied().unwrap_or(false) {
                return false;
            }
        }
        (!self.include.has_clauses() || self.include.is_match(line)) && !self.exclude.is_match(line)
    }

    #[inline]
    pub fn matches(&self, stage: &Stage, row: usize) -> bool {
        if let Some(allowed) = &self.allowed {
            if !allowed.get(stage.src(row) as usize).copied().unwrap_or(false) {
                return false;
            }
        }
        let line = stage.line_bytes(row);
        (!self.include.has_clauses() || self.include.is_match(line)) && !self.exclude.is_match(line)
    }
}

pub fn apply(stage: &Stage, op: &FilterOp, cancel: &Cancel) -> OpResult<Stage> {
    let matcher = FilterMatcher::new(op, &stage.sources);
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
}
