//! Searching rows for literal text, case-sensitively or not, without copying the rows.

use crate::operator::Clause;
use aho_corasick::{AhoCorasick, AhoCorasickBuilder, MatchKind};
use memchr::memmem;

/// One text to find.
pub enum Needle {
    Empty,
    Exact(Box<(memmem::Finder<'static>, memmem::FinderRev<'static>)>, usize),
    /// Case-insensitive, by Unicode simple case folding
    Fold(regex::bytes::Regex),
}

impl Needle {
    pub fn new(text: &str, case_sensitive: bool) -> Needle {
        if text.is_empty() {
            return Needle::Empty;
        }
        if case_sensitive || !text.chars().any(char::is_alphabetic) {
            return Needle::Exact(Box::new((memmem::Finder::new(text).into_owned(), memmem::FinderRev::new(text).into_owned())), text.len());
        }
        match regex::bytes::RegexBuilder::new(&regex::escape(text)).case_insensitive(true).build() {
            Ok(regex) => Needle::Fold(regex),
            Err(_) => Needle::Exact(Box::new((memmem::Finder::new(text).into_owned(), memmem::FinderRev::new(text).into_owned())), text.len()),
        }
    }

    pub fn is_empty(&self) -> bool {
        matches!(self, Needle::Empty)
    }

    /// The first match starting at byte `from` or later
    #[inline]
    pub fn find_at(&self, hay: &[u8], from: usize) -> Option<(usize, usize)> {
        if from > hay.len() {
            return None;
        }
        match self {
            Needle::Empty => Some((from, from)),
            Needle::Exact(finders, length) => finders.0.find(&hay[from..]).map(|at| (from + at, from + at + length)),
            Needle::Fold(regex) => regex.find_at(hay, from).map(|found| (found.start(), found.end())),
        }
    }

    /// The last match starting at byte `limit` or earlier
    pub fn rfind_from(&self, hay: &[u8], limit: usize) -> Option<(usize, usize)> {
        match self {
            Needle::Empty => Some((limit.min(hay.len()), limit.min(hay.len()))),
            Needle::Exact(finders, length) => {
                let end = (limit.saturating_add(*length)).min(hay.len());
                finders.1.rfind(&hay[..end]).map(|at| (at, at + length))
            }
            Needle::Fold(regex) => {
                let mut last = None;
                let mut from = 0;
                while let Some(found) = regex.find_at(hay, from) {
                    if found.start() > limit {
                        break;
                    }
                    last = Some((found.start(), found.end()));
                    // Overlapping matches count too, like lastIndexOf
                    from = found.start() + 1;
                    while from < hay.len() && (hay[from] & 0xC0) == 0x80 {
                        from += 1;
                    }
                    if from > hay.len() {
                        break;
                    }
                }
                last
            }
        }
    }
}

/// The alternatives of the clauses of a Filter: a row matches when it contains any of them.
pub struct LiteralSet {
    clauses: usize,
    single: Option<memmem::Finder<'static>>,
    exact: Option<AhoCorasick>,
    /// ASCII alternatives matched without regard to ASCII case
    ascii_fold: Option<AhoCorasick>,
    /// Alternatives with other letters, by Unicode case folding
    unicode_fold: Option<regex::bytes::Regex>,
}

impl LiteralSet {
    pub fn new(clauses: &[Clause]) -> LiteralSet {
        let mut count = 0;
        let (mut exact, mut ascii_fold, mut unicode_fold): (Vec<&str>, Vec<&str>, Vec<String>) = Default::default();
        for clause in clauses.iter().filter(|clause| !clause.text.is_empty()) {
            count += 1;
            // Every line of a clause is one alternative
            for variant in clause.text.split('\n').map(|line| line.strip_suffix('\r').unwrap_or(line)).filter(|line| !line.is_empty()) {
                if clause.case_sensitive || !variant.chars().any(char::is_alphabetic) {
                    exact.push(variant);
                } else if variant.is_ascii() {
                    ascii_fold.push(variant);
                } else {
                    unicode_fold.push(regex::escape(variant));
                }
            }
        }
        let build = |patterns: &[&str], fold: bool| {
            (!patterns.is_empty()).then(|| AhoCorasickBuilder::new().match_kind(MatchKind::Standard).ascii_case_insensitive(fold).build(patterns).ok()).flatten()
        };
        let single = (exact.len() == 1).then(|| memmem::Finder::new(exact[0]).into_owned());
        LiteralSet {
            clauses: count,
            exact: if single.is_some() { None } else { build(&exact, false) },
            single,
            ascii_fold: build(&ascii_fold, true),
            unicode_fold: (!unicode_fold.is_empty()).then(|| regex::bytes::RegexBuilder::new(&unicode_fold.join("|")).case_insensitive(true).build().ok()).flatten(),
        }
    }

    /// Clauses with any text; a filter without them keeps every row
    pub fn has_clauses(&self) -> bool {
        self.clauses > 0
    }

    #[inline]
    pub fn is_match(&self, hay: &[u8]) -> bool {
        self.single.as_ref().is_some_and(|finder| finder.find(hay).is_some())
            || self.exact.as_ref().is_some_and(|set| set.is_match(hay))
            || self.ascii_fold.as_ref().is_some_and(|set| set.is_match(hay))
            || self.unicode_fold.as_ref().is_some_and(|regex| regex.is_match(hay))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_needle_finds_forward_and_backward() {
        let exact = Needle::new("ab", true);
        assert_eq!(exact.find_at(b"xxabxab", 0), Some((2, 4)));
        assert_eq!(exact.find_at(b"xxabxab", 3), Some((5, 7)));
        assert_eq!(exact.find_at(b"xxabxab", 6), None);
        assert_eq!(exact.rfind_from(b"xxabxab", 7), Some((5, 7)));
        assert_eq!(exact.rfind_from(b"xxabxab", 4), Some((2, 4)));
        assert_eq!(exact.rfind_from(b"xxabxab", 1), None);
        let fold = Needle::new("Žlu", false);
        assert_eq!(fold.find_at("x žLU y ŽLU".as_bytes(), 0), Some((2, 6)));
        assert_eq!(fold.rfind_from("x žLU y ŽLU".as_bytes(), 99), Some((9, 13)));
        assert_eq!(fold.rfind_from("x žLU y ŽLU".as_bytes(), 8), Some((2, 6)));
    }

    #[test]
    fn a_set_matches_any_alternative_with_its_own_case_rule() {
        let set = LiteralSet::new(&[Clause::new("error\nWARN"), Clause { text: "Fatal".into(), case_sensitive: true }, Clause::new("Příliš")]);
        assert!(set.has_clauses());
        for line in ["an ERROR here", "warn", "Fatal", "PŘÍLIŠ žluťoučký"] {
            assert!(set.is_match(line.as_bytes()), "{line}");
        }
        for line in ["fatal", "info", ""] {
            assert!(!set.is_match(line.as_bytes()), "{line}");
        }
        assert!(!LiteralSet::new(&[Clause::new("")]).has_clauses());
        // A clause of line breaks only has no alternative: it is a clause that matches nothing
        let breaks = LiteralSet::new(&[Clause::new("\n\n")]);
        assert!(breaks.has_clauses() && !breaks.is_match(b"anything"));
    }
}
