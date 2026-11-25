//! Members of JSON objects: a quoted key, a colon and the value after it.

use super::BytePosition;
use crate::stage::Stage;
use memchr::memchr2;

/// `"key": value`, from the key's opening quote to the end of the value
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

/// How far a member of an object has got
#[derive(Clone, Copy, PartialEq, Eq)]
enum Part {
    Empty,
    Key,
    Colon,
    Value,
    /// Not a member, such as code or prose in braces
    Other,
}

struct Level<P> {
    open: u8,
    start: Option<P>,
    colon: Option<P>,
    end: Option<P>,
    part: Part,
    /// The member around the click, kept until the object closes
    found: Option<Member<P>>,
}

impl<P: Copy + Ord> Level<P> {
    fn new(open: u8) -> Self {
        Level { open, start: None, colon: None, end: None, part: Part::Empty, found: None }
    }

    fn token(&mut self, start: P, end: P, quoted: bool) {
        if self.open != b'{' {
            return;
        }
        self.start.get_or_insert(start);
        self.end = Some(end);
        self.part = match self.part {
            Part::Empty if quoted => Part::Key,
            Part::Colon | Part::Value => Part::Value,
            _ => Part::Other,
        };
    }

    fn finish_member(&mut self, clicked: P) {
        if let (Part::Value, Some(start), Some(colon), Some(end)) = (self.part, self.start, self.colon, self.end) {
            if start <= clicked && clicked < end {
                self.found = Some(Member { start, colon, end });
            }
        }
        (self.start, self.colon, self.end, self.part) = (None, None, None, Part::Empty);
    }
}

/// What the objects around a click have
pub(super) struct Objects<P> {
    /// Members around the click, innermost first
    pub members: Vec<Member<P>>,
    /// The nearest colons after a key before and from the click, also in objects that do not close
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

    fn token(&mut self, start: P, end: P, quoted: bool) {
        if let Some(level) = self.levels.last_mut() {
            level.token(start, end, quoted);
        }
    }

    /// A character outside quotes
    fn char(&mut self, position: P, ch: u8, next: P) {
        self.reach(position);
        match ch {
            b' ' | b'\t' | b'\r' | b'\n' => {}
            b'(' | b'[' | b'{' => {
                self.token(position, next, false);
                self.levels.push(Level::new(ch));
            }
            b')' | b']' | b'}' => {
                let open = match ch {
                    b')' => b'(',
                    b']' => b'[',
                    _ => b'{',
                };
                let Some(at) = self.levels.iter().rposition(|level| level.open == open) else {
                    return self.token(position, next, false);
                };
                let mut level = self.levels.swap_remove(at);
                self.levels.truncate(at);
                if level.open == b'{' {
                    level.finish_member(self.clicked);
                    self.objects.members.extend(level.found);
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
            b':' => match self.levels.last_mut() {
                Some(level) if level.open == b'{' && level.part == Part::Key => {
                    level.part = Part::Colon;
                    level.colon = Some(position);
                    let [before, after] = &mut self.objects.colons;
                    if position < self.clicked {
                        *before = Some(position);
                    } else {
                        after.get_or_insert(position);
                    }
                }
                _ => self.token(position, next, false),
            },
            _ => self.token(position, next, false),
        }
    }

    /// Quoted text from its opening quote to past its closing one, or to the end of its row
    fn quoted(&mut self, start: P, end: P, closed: bool) {
        self.reach(start);
        self.token(start, end, closed);
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

/// The objects around `clicked` in the window text, which can start inside quotes
pub(super) fn objects_in(text: &[char], clicked: usize, leading_quote: Option<char>) -> Objects<usize> {
    let mut scanner = Scanner::new(clicked);
    let mut at = 0;
    let mut leading_quote = leading_quote;
    while at < text.len() && !scanner.done {
        let (quote, from) = match leading_quote.take() {
            Some(quote) => (quote, 0),
            None if matches!(text[at], '"' | '\'' | '`') => (text[at], at + 1),
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
    ch.is_ascii_alphanumeric() || ch >= 0x80 || matches!(ch, b'.' | b'-' | b'+' | b'_')
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
                // A run of a bare value, such as a number, is one token
                let run = line[byte..].iter().position(|&ch| !is_bare(ch)).unwrap_or(line.len() - byte);
                scanner.reach((row, byte));
                scanner.token((row, byte), (row, byte + run), false);
                byte += run;
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
