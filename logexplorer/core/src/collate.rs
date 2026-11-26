//! Ordering text the way `String.localeCompare` does in its root locale: punctuation before
//! digits before letters, letters regardless of case and accents first, then accents, then
//! lowercase before uppercase.

use std::cmp::Ordering;
use unicode_normalization::char::is_combining_mark;
use unicode_normalization::UnicodeNormalization;

const PUNCTUATION: &[u8] = b"_-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$";

const fn build_ranks() -> [u8; 128] {
    let mut ranks = [1u8; 128];
    let whitespace = [b'\t', b'\n', 0x0b, 0x0c, b'\r', b' '];
    let mut next = 2;
    let mut index = 0;
    while index < whitespace.len() {
        ranks[whitespace[index] as usize] = next;
        next += 1;
        index += 1;
    }
    index = 0;
    while index < PUNCTUATION.len() {
        ranks[PUNCTUATION[index] as usize] = next;
        next += 1;
        index += 1;
    }
    index = 0;
    while index < 10 {
        ranks[b'0' as usize + index] = next;
        next += 1;
        index += 1;
    }
    index = 0;
    while index < 26 {
        ranks[b'a' as usize + index] = next;
        ranks[b'A' as usize + index] = next;
        next += 1;
        index += 1;
    }
    ranks
}

const RANKS: [u8; 128] = build_ranks();

#[inline]
fn weight(ch: char) -> u32 {
    if ch.is_ascii() {
        RANKS[ch as usize] as u32
    } else {
        0x1000 + ch.to_lowercase().next().unwrap_or(ch) as u32
    }
}

#[inline]
pub fn compare_ascii(left: &[u8], right: &[u8]) -> Ordering {
    let mut case = Ordering::Equal;
    for (&x, &y) in left.iter().zip(right) {
        if x == y {
            continue;
        }
        let (rank_x, rank_y) = (RANKS[x as usize], RANKS[y as usize]);
        if rank_x != rank_y {
            return rank_x.cmp(&rank_y);
        }
        if case == Ordering::Equal {
            case = if x.is_ascii_lowercase() { Ordering::Less } else { Ordering::Greater };
        }
    }
    left.len().cmp(&right.len()).then(case)
}

/// First 8 ASCII primary ranks packed big-endian into a `u64` (0 marks end-of-string).
/// Whenever two keys differ, their `u64` order matches [`compare_ascii`] exactly.
#[inline]
pub fn ascii_prefix_key(bytes: &[u8]) -> u64 {
    let mut key = 0u64;
    let len = bytes.len().min(8);
    let mut i = 0;
    while i < len {
        key |= (RANKS[bytes[i] as usize] as u64) << (56 - i * 8);
        i += 1;
    }
    key
}

/// Base letters of the text, without accents
fn bases(text: &str) -> impl Iterator<Item = char> + '_ {
    text.nfd().filter(|ch| !is_combining_mark(*ch))
}

/// Compares the base letters; digit runs by their value when `numeric`
fn primary(left: &str, right: &str, numeric: bool) -> Ordering {
    let mut left = bases(left).peekable();
    let mut right = bases(right).peekable();
    loop {
        match (left.peek().copied(), right.peek().copied()) {
            (None, None) => return Ordering::Equal,
            (None, Some(_)) => return Ordering::Less,
            (Some(_), None) => return Ordering::Greater,
            (Some(x), Some(y)) => {
                if numeric && x.is_ascii_digit() && y.is_ascii_digit() {
                    let take = |iter: &mut std::iter::Peekable<_>| {
                        let mut run = String::new();
                        while let Some(digit) = iter.next_if(|ch: &char| ch.is_ascii_digit()) {
                            run.push(digit);
                        }
                        run
                    };
                    let (run_x, run_y) = (take(&mut left), take(&mut right));
                    let (value_x, value_y) = (run_x.trim_start_matches('0'), run_y.trim_start_matches('0'));
                    let order = value_x.len().cmp(&value_y.len()).then_with(|| value_x.cmp(value_y));
                    if order != Ordering::Equal {
                        return order;
                    }
                    continue;
                }
                let order = weight(x).cmp(&weight(y));
                if order != Ordering::Equal {
                    return order;
                }
                left.next();
                right.next();
            }
        }
    }
}

/// Digit runs without their leading zeros: "id09" and "id9" are the same text to a numeric order
fn without_leading_zeros(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch == '0' && !out.ends_with(|last: char| last.is_ascii_digit()) && chars.peek().is_some_and(char::is_ascii_digit) {
            continue;
        }
        out.push(ch);
    }
    out
}

fn full_compare(left: &str, right: &str, numeric: bool, all_levels: bool) -> Ordering {
    let order = primary(left, right, numeric);
    if order == Ordering::Equal && all_levels && numeric {
        return full_compare(&without_leading_zeros(left), &without_leading_zeros(right), false, true);
    }
    if order != Ordering::Equal || !all_levels {
        return order;
    }
    // Accents, then case: lowercase first
    let lower = |text: &str| text.nfd().flat_map(char::to_lowercase).collect::<Vec<char>>();
    let order = lower(left).cmp(&lower(right));
    if order != Ordering::Equal {
        return order;
    }
    for (x, y) in left.nfd().zip(right.nfd()) {
        if x != y {
            return if x.is_lowercase() { Ordering::Less } else { Ordering::Greater };
        }
    }
    Ordering::Equal
}

/// `left.localeCompare(right)`
#[inline]
pub fn compare(left: &str, right: &str) -> Ordering {
    if left.is_ascii() && right.is_ascii() {
        compare_ascii(left.as_bytes(), right.as_bytes())
    } else {
        full_compare(left, right, false, true)
    }
}

/// `left.localeCompare(right, undefined, { numeric: true, sensitivity })`: numbers inside the
/// text by value; with `case_sensitive` off, case and accents do not count
pub fn compare_natural(left: &str, right: &str, case_sensitive: bool) -> Ordering {
    full_compare(left, right, true, case_sensitive)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sorted(mut items: Vec<&str>) -> Vec<&str> {
        items.sort_by(|left, right| compare(left, right));
        items
    }

    #[test]
    fn letters_sort_regardless_of_case_then_lowercase_first() {
        assert_eq!(sorted(vec!["b", "A", "a", "B", "C"]), ["a", "A", "b", "B", "C"]);
        assert_eq!(sorted(vec!["Zeta", "alpha", "Beta"]), ["alpha", "Beta", "Zeta"]);
        assert_eq!(sorted(vec!["ab", "a", "abc"]), ["a", "ab", "abc"]);
    }

    #[test]
    fn punctuation_comes_before_digits_and_digits_before_letters() {
        assert_eq!(sorted(vec!["a", "1", "_", "-", " "]), [" ", "_", "-", "1", "a"]);
        assert_eq!(sorted(vec!["[4712]", "[4711]", "(1)"]), ["(1)", "[4711]", "[4712]"]);
    }

    #[test]
    fn accents_sort_next_to_their_base_letter() {
        assert_eq!(sorted(vec!["d", "č", "c", "Č", "b"]), ["b", "c", "č", "Č", "d"]);
        assert_eq!(sorted(vec!["zebra", "žába", "auto"]), ["auto", "žába", "zebra"]);
    }

    #[test]
    fn natural_order_reads_numbers_by_value() {
        let mut items = vec!["id10", "id9", "id100", "ID9", "id09"];
        items.sort_by(|left, right| compare_natural(left, right, true));
        assert_eq!(items, ["id9", "id09", "ID9", "id10", "id100"]);
        assert_eq!(compare_natural("Á9", "a09", false), Ordering::Equal);
        assert_eq!(compare_natural("a", "A", true), Ordering::Less);
    }

    #[test]
    fn ascii_prefix_key_agrees_with_compare_whenever_keys_differ() {
        let samples = [
            "", " ", "-", "_", "0", "9", "a", "A", "ab", "aB", "Ab", "abc",
            "abcdefgh", "abcdefgH", "abcdefgh1", "abcdefgh2", "abcdefgi",
            "[4711] START", "[4712] END", "START /api/orders/1", "START /api/orders/2",
        ];
        for &left in &samples {
            for &right in &samples {
                let key_order = ascii_prefix_key(left.as_bytes()).cmp(&ascii_prefix_key(right.as_bytes()));
                if key_order != Ordering::Equal {
                    assert_eq!(key_order, compare(left, right), "left={left:?} right={right:?}");
                }
            }
        }
    }
}
