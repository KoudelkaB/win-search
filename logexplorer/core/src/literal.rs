//! Searching rows for literal text, case-sensitively or not, without copying the rows.

use crate::operator::Clause;
use aho_corasick::{AhoCorasick, AhoCorasickBuilder, MatchKind};
use memchr::memmem;

/// One text to find.
pub enum Needle {
    Empty,
    Exact(Box<(memmem::Finder<'static>, memmem::FinderRev<'static>)>, usize),
    /// Case-insensitive ASCII literal searched with SIMD byte scans in both directions
    AsciiFold {
        needle: Vec<u8>,
        first_lo: u8,
        first_hi: u8,
    },
    /// Case-insensitive non-ASCII literal, by Unicode simple case folding
    Fold(regex::bytes::Regex, bool),
}

impl Needle {
    pub fn new(text: &str, case_sensitive: bool) -> Needle {
        if text.is_empty() {
            return Needle::Empty;
        }
        if case_sensitive || !text.chars().any(char::is_alphabetic) {
            return Needle::Exact(Box::new((memmem::Finder::new(text).into_owned(), memmem::FinderRev::new(text).into_owned())), text.len());
        }
        if text.is_ascii() {
            let needle = text.as_bytes().to_vec();
            let first_lo = needle[0].to_ascii_lowercase();
            let first_hi = needle[0].to_ascii_uppercase();
            return Needle::AsciiFold { needle, first_lo, first_hi };
        }
        let has_break = text.as_bytes().iter().any(|&b| b == b'\n' || b == b'\r');
        match regex::bytes::RegexBuilder::new(&regex::escape(text)).case_insensitive(true).build() {
            Ok(regex) => Needle::Fold(regex, has_break),
            Err(_) => Needle::Exact(Box::new((memmem::Finder::new(text).into_owned(), memmem::FinderRev::new(text).into_owned())), text.len()),
        }
    }

    pub fn is_empty(&self) -> bool {
        matches!(self, Needle::Empty)
    }

    /// True if the pattern contains `\n` or `\r` (and thus cannot safely scan across raw line breaks)
    pub fn contains_line_break(&self) -> bool {
        match self {
            Needle::Empty => false,
            Needle::Exact(finders, _) => finders.0.needle().iter().any(|&b| b == b'\n' || b == b'\r'),
            Needle::AsciiFold { needle, .. } => needle.iter().any(|&b| b == b'\n' || b == b'\r'),
            Needle::Fold(_, has_break) => *has_break,
        }
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
            Needle::AsciiFold { needle, first_lo, first_hi } => {
                let len = needle.len();
                if from.saturating_add(len) > hay.len() {
                    return None;
                }
                let search_end = hay.len() - len + 1;
                let mut at = from;
                while at < search_end {
                    let rel = if first_lo == first_hi {
                        memchr::memchr(*first_lo, &hay[at..search_end])?
                    } else {
                        memchr::memchr2(*first_lo, *first_hi, &hay[at..search_end])?
                    };
                    let pos = at + rel;
                    if hay[pos..pos + len].eq_ignore_ascii_case(needle) {
                        return Some((pos, pos + len));
                    }
                    at = pos + 1;
                }
                None
            }
            Needle::Fold(regex, _) => regex.find_at(hay, from).map(|found| (found.start(), found.end())),
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
            Needle::AsciiFold { needle, first_lo, first_hi } => {
                let len = needle.len();
                if hay.len() < len {
                    return None;
                }
                let max_start = limit.min(hay.len() - len);
                let mut end = max_start + 1;
                while end > 0 {
                    let pos = if first_lo == first_hi {
                        memchr::memrchr(*first_lo, &hay[..end])?
                    } else {
                        memchr::memrchr2(*first_lo, *first_hi, &hay[..end])?
                    };
                    if hay[pos..pos + len].eq_ignore_ascii_case(needle) {
                        return Some((pos, pos + len));
                    }
                    end = pos;
                }
                None
            }
            Needle::Fold(regex, _) => {
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
    /// True if any alternative contains `\r` (prevents raw chunk scanning across lone `\r` line breaks)
    has_cr: bool,
}

impl LiteralSet {
    pub fn new(clauses: &[Clause]) -> LiteralSet {
        let mut count = 0;
        let mut has_cr = false;
        let (mut exact, mut ascii_fold, mut unicode_fold): (Vec<&str>, Vec<&str>, Vec<String>) = Default::default();
        for clause in clauses.iter().filter(|clause| !clause.text.is_empty()) {
            count += 1;
            // Every line of a clause is one alternative
            for variant in clause.text.split('\n').map(|line| line.strip_suffix('\r').unwrap_or(line)).filter(|line| !line.is_empty()) {
                has_cr |= variant.as_bytes().contains(&b'\r');
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
            has_cr,
        }
    }

    /// Clauses with any text; a filter without them keeps every row
    pub fn has_clauses(&self) -> bool {
        self.clauses > 0
    }

    /// True when the set has clauses but zero searchable alternatives (matches nothing)
    pub fn matches_nothing(&self) -> bool {
        self.clauses > 0 && self.single.is_none() && self.exact.is_none() && self.ascii_fold.is_none() && self.unicode_fold.is_none()
    }

    /// True when this set can scan contiguous multi-line byte slices with a single fast engine
    pub fn can_chunk_scan(&self) -> bool {
        if self.has_cr || self.unicode_fold.is_some() {
            return false;
        }
        let engines = usize::from(self.single.is_some()) + usize::from(self.exact.is_some()) + usize::from(self.ascii_fold.is_some());
        engines == 1
    }

    /// Finds the start offset of the first match at or after `from` (only valid when [`Self::can_chunk_scan`] is true)
    #[inline]
    pub fn find_start_at(&self, hay: &[u8], from: usize) -> Option<usize> {
        if from >= hay.len() {
            return None;
        }
        let slice = &hay[from..];
        if let Some(finder) = &self.single {
            return finder.find(slice).map(|at| from + at);
        }
        if let Some(set) = &self.exact {
            return set.find(slice).map(|found| from + found.start());
        }
        if let Some(set) = &self.ascii_fold {
            return set.find(slice).map(|found| from + found.start());
        }
        None
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
        let ascii_fold = Needle::new("aA", false);
        assert!(matches!(ascii_fold, Needle::AsciiFold { .. }));
        assert_eq!(ascii_fold.find_at(b"xAaA", 0), Some((1, 3)));
        assert_eq!(ascii_fold.find_at(b"xAaA", 2), Some((2, 4)));
        assert_eq!(ascii_fold.rfind_from(b"xAaA", 10), Some((2, 4)));
        assert_eq!(ascii_fold.rfind_from(b"xAaA", 1), Some((1, 3)));
        assert_eq!(ascii_fold.rfind_from(b"xAaA", 0), None);
        let punct = Needle::new("=Err", false);
        assert_eq!(punct.find_at(b"x=err y=ERR", 0), Some((1, 5)));
        assert_eq!(punct.rfind_from(b"x=err y=ERR", 11), Some((7, 11)));
        assert_eq!(punct.rfind_from(b"x=err y=ERR", 6), Some((1, 5)));
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
