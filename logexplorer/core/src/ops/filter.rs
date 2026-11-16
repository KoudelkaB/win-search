//! Filter: keeps the rows that contain any Include text and no Exclude text.

use super::par_chunks;
use crate::cancel::{Cancel, OpResult};
use crate::literal::LiteralSet;
use crate::operator::FilterOp;
use crate::stage::{OutRows, SourceDesc, Stage};
use rayon::prelude::*;

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
    pub fn matches(&self, stage: &Stage, row: usize) -> bool {
        let line = stage.line_bytes(row);
        if (self.include.has_clauses() && !self.include.is_match(line)) || self.exclude.is_match(line) {
            return false;
        }
        match &self.allowed {
            None => true,
            Some(allowed) => allowed.get(stage.src(row) as usize).copied().unwrap_or(false),
        }
    }
}

pub fn apply(stage: &Stage, op: &FilterOp, cancel: &Cancel) -> OpResult<Stage> {
    let matcher = FilterMatcher::new(op, &stage.sources);
    let kept = par_chunks(stage.len(), cancel, |range| Ok(range.filter(|&row| matcher.matches(stage, row)).map(|row| row as u32).collect::<Vec<u32>>()))?;
    let total = kept.iter().map(Vec::len).sum();
    let mut origin = Vec::with_capacity(total);
    for chunk in kept {
        origin.extend_from_slice(&chunk);
    }
    let spans = origin.par_iter().with_min_len(1 << 14).map(|&row| stage.span(row as usize)).collect();
    Ok(stage.derive("Filter", OutRows { spans, origin: Some(origin), arena: Vec::new() }))
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
