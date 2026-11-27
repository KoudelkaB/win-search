//! Members of JSON and log objects: a key, a colon and the value after it.

use super::{is_quote, BytePosition};
use crate::stage::Stage;
use crate::text::as_str;
use memchr::memchr2;

/// `"key": value` or `key: value`, from the start of the key to the end of the value
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Member<P> {
    pub start: P,
    pub colon: P,
    pub end: P,
}

impl<P> Member<P> {
    pub fn map<Q>(self, mut point: impl FnMut(P) -> Q) -> Member<Q> {
        Member { start: point(self.start), colon: point(self.colon), end: point(self.end) }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Key {
    Quoted,
    Bare,
}

/// How far a member of an object has got
#[derive(Clone, Copy, PartialEq, Eq)]
enum Part {
    Empty,
    Key(Key),
    Colon(Key),
    Value(Key),
    /// Not a member, such as code or prose in braces
    Other,
}

struct Level<P> {
    open: u8,
    start: Option<P>,
    colon: Option<P>,
    end: Option<P>,
    part: Part,
    /// Bare keys require the surrounding braces to contain a list of members.
    only_members: bool,
    /// The member around the click, kept until the object closes
    found: Option<(Member<P>, Key)>,
}

impl<P: Copy + Ord> Level<P> {
    fn new(open: u8) -> Self {
        Level { open, start: None, colon: None, end: None, part: Part::Empty, only_members: true, found: None }
    }

    fn token(&mut self, start: P, end: P, key: Option<Key>) {
        if self.open != b'{' {
            return;
        }
        self.start.get_or_insert(start);
        self.end = Some(end);
        self.part = match (self.part, key) {
            (Part::Empty, Some(key)) => Part::Key(key),
            (Part::Colon(key) | Part::Value(key), _) => Part::Value(key),
            _ => Part::Other,
        };
    }

    fn colon(&mut self, position: P) -> Option<Key> {
        if self.part == Part::Colon(Key::Bare) {
            // A second colon can be a scope operator, as in `Type::member`.
            self.only_members = false;
        }
        let Part::Key(key) = self.part else { return None };
        self.part = Part::Colon(key);
        self.colon = Some(position);
        Some(key)
    }

    fn finish_member(&mut self, clicked: P) {
        if let (Part::Value(key), Some(start), Some(colon), Some(end)) = (self.part, self.start, self.colon, self.end) {
            if start <= clicked && clicked < end {
                self.found = Some((Member { start, colon, end }, key));
            }
        } else if self.part != Part::Empty {
            self.only_members = false;
        }
        (self.start, self.colon, self.end, self.part) = (None, None, None, Part::Empty);
    }
}

/// What the objects around a click have
pub(super) struct Objects<P> {
    /// Members around the click, innermost first
    pub members: Vec<Member<P>>,
    /// The nearest colons after a quoted key before and from the click, also in objects that do not close
    pub colons: [Option<P>; 2],
    /// The last closing `"`, `'` and `` ` `` before the click
    pub closing_quotes: [Option<P>; 3],
}

impl<P: Copy + Ord> Objects<P> {
    /// Whether a quote before the click closes a string rather than opens one
    pub fn closes_string(&self, quote: P) -> bool {
        self.closing_quotes.contains(&Some(quote))
    }

    /// Whether quotes from `start` to `end` are those of a key and a value rather than a string
    pub fn split_by_member(&self, start: P, end: P) -> bool {
        let crosses = |member: &Member<P>| (start < member.start && member.start < end && end < member.end) || (member.start < start && start < member.end && member.end < end);
        // Quotes around the click hold a colon exactly when they hold the nearest one on its side.
        self.members.iter().any(crosses) || self.colons.iter().flatten().any(|&colon| start < colon && colon < end)
    }
}

/// Follows the objects around a click. Brackets match as tolerantly as elsewhere.
struct Scanner<P> {
    clicked: P,
    levels: Vec<Level<P>>,
    /// Levels open at the click, once it is passed
    enclosing: Option<usize>,
    objects: Objects<P>,
    done: bool,
}

impl<P: Copy + Ord> Scanner<P> {
    fn new(clicked: P) -> Self {
        Scanner { clicked, levels: Vec::new(), enclosing: None, objects: Objects { members: Vec::new(), colons: [None; 2], closing_quotes: [None; 3] }, done: false }
    }

    /// Before the token or character at `position`
    fn reach(&mut self, position: P) {
        if self.enclosing.is_none() && position >= self.clicked {
            self.enclosing = Some(self.levels.len());
            self.done = self.levels.is_empty();
        }
    }

    fn expects_key(&self) -> bool {
        self.levels.last().is_some_and(|level| level.open == b'{' && level.part == Part::Empty)
    }

    fn token(&mut self, start: P, end: P, key: Option<Key>) {
        if let Some(level) = self.levels.last_mut() {
            level.token(start, end, key);
        }
    }

    /// A character outside quotes
    fn char(&mut self, position: P, ch: u8, next: P) {
        self.reach(position);
        match ch {
            b' ' | b'\t' | b'\r' | b'\n' => {}
            b'(' | b'[' | b'{' => {
                self.token(position, next, None);
                self.levels.push(Level::new(ch));
            }
            b')' | b']' | b'}' => {
                let open = match ch {
                    b')' => b'(',
                    b']' => b'[',
                    _ => b'{',
                };
                let Some(at) = self.levels.iter().rposition(|level| level.open == open) else {
                    return self.token(position, next, None);
                };
                let balanced = at + 1 == self.levels.len();
                let mut level = self.levels.swap_remove(at);
                self.levels.truncate(at);
                if level.open == b'{' {
                    level.finish_member(self.clicked);
                    if let Some((member, key)) = level.found {
                        if key == Key::Quoted || (balanced && level.only_members) {
                            self.objects.members.push(member);
                        }
                    }
                }
                if let Some(parent) = self.levels.last_mut() {
                    parent.end = Some(next);
                }
                if let Some(open) = self.enclosing {
                    self.enclosing = Some(self.levels.len().min(open));
                    self.done = self.levels.is_empty();
                }
            }
            b',' => {
                if let Some(level) = self.levels.last_mut().filter(|level| level.open == b'{') {
                    level.finish_member(self.clicked);
                }
            }
            b':' => match self.levels.last_mut().and_then(|level| level.colon(position)) {
                Some(Key::Quoted) => {
                    let [before, after] = &mut self.objects.colons;
                    if position < self.clicked {
                        *before = Some(position);
                    } else {
                        after.get_or_insert(position);
                    }
                }
                Some(Key::Bare) => {}
                None => self.token(position, next, None),
            },
            b';' => {
                // A statement in braces can start with an unquoted label.
                if let Some(level) = self.levels.last_mut() {
                    level.only_members = false;
                }
                self.token(position, next, None);
            }
            _ => self.token(position, next, None),
        }
    }

    /// Quoted text from its opening quote to past its closing one, or to the end of its row
    fn quoted(&mut self, start: P, end: P, closed: bool) {
        self.reach(start);
        self.token(start, end, closed.then_some(Key::Quoted));
    }

    fn closing_quote(&mut self, quote: u8, at: P) {
        if at < self.clicked {
            let kind = match quote {
                b'"' => 0,
                b'\'' => 1,
                _ => 2,
            };
            self.objects.closing_quotes[kind] = Some(at);
        }
    }
}

fn key_start(ch: char) -> bool {
    ch.is_alphabetic() || matches!(ch, '_' | '$')
}

fn key_char(ch: char) -> bool {
    ch.is_alphanumeric() || matches!(ch, '_' | '$')
}

/// Where the key of a member ends right before a colon
#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) enum KeyBefore {
    /// A quoted key, or a bare one that starts its row, can be in an object that opens far before.
    Distant,
    /// A bare key after `{` or `,` on its row, as in `INFO Id: 1001, Status: ready`
    Inline,
}

/// Whether a colon after this text could introduce a member value, and after what key
pub(super) fn key_before(text: &[char]) -> Option<KeyBefore> {
    let end = text.iter().rposition(|ch| !ch.is_whitespace())?;
    if is_quote(text[end]) {
        return Some(KeyBefore::Distant);
    }
    let start = text[..=end].iter().rposition(|&ch| !key_char(ch)).map_or(0, |index| index + 1);
    if start > end || !key_start(text[start]) {
        return None;
    }
    match text[..start].iter().rposition(|ch| !ch.is_whitespace()) {
        None => Some(KeyBefore::Distant),
        Some(at) if matches!(text[at], '{' | ',') => Some(if text[at..start].contains(&'\n') { KeyBefore::Distant } else { KeyBefore::Inline }),
        Some(_) => None,
    }
}

/// The objects around `clicked` in the window text, which can start inside quotes
pub(super) fn objects_in(text: &[char], clicked: usize, leading_quote: Option<char>) -> Objects<usize> {
    let mut scanner = Scanner::new(clicked);
    let mut at = 0;
    let mut leading_quote = leading_quote;
    while at < text.len() && !scanner.done {
        let (quote, from) = match leading_quote.take() {
            Some(quote) => (quote, 0),
            None if matches!(text[at], '"' | '\'' | '`') => (text[at], at + 1),
            None if scanner.expects_key() && key_start(text[at]) => {
                let end = at + text[at..].iter().copied().take_while(|&ch| key_char(ch)).count();
                scanner.reach(at);
                scanner.token(at, end, Some(Key::Bare));
                at = end;
                continue;
            }
            None => {
                let ch = text[at];
                scanner.char(at, if ch.is_ascii() { ch as u8 } else { 0 }, at + 1);
                at += 1;
                continue;
            }
        };
        let mut end = from;
        while end < text.len() && text[end] != quote && text[end] != '\n' {
            end += if text[end] == '\\' && text.get(end + 1) != Some(&'\n') { 2 } else { 1 };
        }
        let closed = end < text.len() && text[end] == quote;
        if closed {
            scanner.closing_quote(quote as u8, end);
        }
        scanner.quoted(at, if closed { end + 1 } else { end.min(text.len()) }, closed);
        at = end + usize::from(closed);
    }
    scanner.objects
}

fn is_bare(ch: u8) -> bool {
    ch.is_ascii_alphanumeric() || ch >= 0x80 || matches!(ch, b'.' | b'-' | b'+' | b'_' | b'$')
}

/// The objects around `clicked` in the stage from `from` to `to`
pub(super) fn objects_on_stage(stage: &Stage, from: BytePosition, to: BytePosition, clicked: BytePosition) -> Objects<BytePosition> {
    let mut scanner = Scanner::new(clicked);
    for row in from.0..=to.0 {
        let line = stage.line_bytes(row);
        let end = if row == to.0 { to.1.min(line.len()) } else { line.len() };
        let line = &line[..end];
        let mut byte = if row == from.0 { from.1 } else { 0 };
        while byte < line.len() && !scanner.done {
            let ch = line[byte];
            if matches!(ch, b'"' | b'\'' | b'`') {
                let mut close = byte + 1;
                let closed = loop {
                    let Some(next) = line.get(close..).and_then(|rest| memchr2(ch, b'\\', rest)) else { break false };
                    close += next;
                    if line[close] == ch {
                        break true;
                    }
                    close += 2;
                };
                let close = close.min(line.len());
                scanner.quoted((row, byte), (row, if closed { close + 1 } else { close }), closed);
                byte = close + usize::from(closed);
            } else if is_bare(ch) {
                let end = byte + line[byte..].iter().take_while(|&&ch| is_bare(ch)).count();
                let key = if scanner.expects_key() {
                    let mut chars = as_str(&line[byte..end]).chars();
                    (chars.next().is_some_and(key_start) && chars.all(key_char)).then_some(Key::Bare)
                } else {
                    None
                };
                scanner.reach((row, byte));
                scanner.token((row, byte), (row, end), key);
                byte = end;
            } else {
                scanner.char((row, byte), ch, (row, byte + 1));
                byte += 1;
            }
        }
        if scanner.done {
            break;
        }
        if row < to.0 {
            scanner.char((row, line.len()), b'\n', (row + 1, 0));
        }
    }
    scanner.objects
}
