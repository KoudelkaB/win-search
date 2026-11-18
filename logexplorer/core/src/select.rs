//! What a click selects: the value under the pointer (an identifier, a number, a date, a
//! path...), the content of quotes and brackets, an expression around `=`, and how
//! repeated clicks grow or shrink the selection through the enclosing structures.
//!
//! The HTML app scans the whole stage for brackets and tags on the first click. Here the
//! structures are looked for in a window of rows around the click, so a click costs the
//! same in a 10 KB and in a 10 GB view.

use crate::selection::{Point, Selection};
use crate::stage::Stage;
use crate::text::{byte_to_col, col_to_byte};
use std::sync::OnceLock;

/// Rows taken around the clicked one, and the text they may add up to
const WINDOW_ROWS: usize = 400;
const WINDOW_BYTES: usize = 1 << 20;
/// A longer clicked row is cut to this much around the click
const ROW_BYTES: usize = 2 << 20;
/// Tokens are looked for this far around the click in a long row
const TOKEN_REACH: usize = 8192;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Range {
    start: usize,
    end: usize,
    /// The range with its quotes, when this is the content between them
    delimiters: Option<(usize, usize)>,
    precedence: u8,
}

impl Range {
    fn new(start: usize, end: usize) -> Self {
        Range { start, end, delimiters: None, precedence: 0 }
    }

    fn len(&self) -> usize {
        self.end.saturating_sub(self.start)
    }

    fn same(&self, other: &Range) -> bool {
        self.start == other.start && self.end == other.end
    }

    fn contains_offset(&self, offset: usize) -> bool {
        offset >= self.start && offset < self.end && self.end > self.start
    }

    fn contains(&self, inner: &Range) -> bool {
        self.start <= inner.start && self.end >= inner.end
    }
}

#[derive(Clone, Copy)]
struct Pair {
    start: usize,
    end: usize,
}

impl Pair {
    fn content(&self) -> Range {
        Range::new(self.start + 1, self.end - 1)
    }

    fn full(&self) -> Range {
        Range::new(self.start, self.end)
    }
}

#[derive(Clone)]
struct Element {
    start: usize,
    end: usize,
    open_tag_end: usize,
    close_start: usize,
}

impl Element {
    fn content(&self) -> Range {
        Range::new(self.open_tag_end, self.close_start)
    }

    fn full(&self) -> Range {
        Range::new(self.start, self.end)
    }
}

fn is_quote(ch: char) -> bool {
    ch == '"' || ch == '\'' || ch == '`'
}

fn is_blank(ch: char) -> bool {
    ch == ' ' || ch == '\t'
}

fn is_escaped_at(text: &[char], index: usize) -> bool {
    text[..index].iter().rev().take_while(|ch| **ch == '\\').count() % 2 == 1
}

struct Token {
    regex: fancy_regex::Regex,
    precedence: u8,
    group: usize,
}

fn token(pattern: &str, precedence: u8, group: usize) -> Token {
    Token { regex: fancy_regex::Regex::new(pattern).expect("token pattern"), precedence, group }
}

const DATE_TIME_ISO: &str = r"\b[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{1,2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,9})?)?(?:Z|[+-][0-9]{2}:?[0-9]{2})?\b";
const DATE_TIME_YMD: &str = r"(?i)\b[0-9]{4}[/.-][0-9]{1,2}[/.-][0-9]{1,2}\s+[0-9]{1,2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,9})?)?(?:\s*(?:AM|PM))?\b";
const DATE_TIME_DMY: &str = r"(?i)\b[0-9]{1,2}[/.-][0-9]{1,2}[/.-][0-9]{2,4}\s+[0-9]{1,2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,9})?)?(?:\s*(?:AM|PM))?\b";
const DATE_YMD: &str = r"\b[0-9]{4}[/.-][0-9]{1,2}[/.-][0-9]{1,2}\b";
const DATE_DMY: &str = r"\b[0-9]{1,2}[/.-][0-9]{1,2}[/.-][0-9]{2,4}\b";
const TIME: &str = r"(?i)\b[0-9]{1,2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,9})?)?(?:\s*(?:AM|PM))?\b";
const GUID: &str = r"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b";
const GUID_LIKE: &str = r"(?i)(^|[^A-Za-z0-9-])([A-Z0-9]{4,8}(?:-[A-Z0-9]{4,8}){2,})(?![A-Za-z0-9-])";

/// The values a click can select, as the HTML app lists them
fn tokens() -> &'static [Token] {
    static TOKENS: OnceLock<Vec<Token>> = OnceLock::new();
    TOKENS.get_or_init(|| {
        vec![
            token(DATE_TIME_ISO, 4, 0),
            token(DATE_TIME_YMD, 4, 0),
            token(DATE_TIME_DMY, 4, 0),
            token(GUID, 3, 0),
            token(GUID_LIKE, 3, 2),
            token(r"\b(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\b", 3, 0),
            token(r"\b[0-9]+(?:\.[0-9]+){2,}(?:[-+][A-Za-z0-9][A-Za-z0-9.-]*)?\b", 3, 0),
            token(r#"(?i)\b(?:https?|ftp)://[^\s"'<>]+"#, 3, 0),
            token(r#"(?:[A-Za-z]:\\[^\s"'<>|]+|\\\\[^\s"'<>|]+|(?:[\\/]|\.{1,2}[\\/])?[A-Za-z0-9_.-]+(?:[\\/][A-Za-z0-9_.-]+)+)"#, 3, 0),
            token(r"\b[A-Za-z_][A-Za-z0-9_]*(?:(?:::|\.)[A-Za-z_][A-Za-z0-9_]*)+\b", 3, 0),
            token(DATE_YMD, 3, 0),
            token(DATE_DMY, 3, 0),
            token(TIME, 3, 0),
            token(r"(?i)(^|[^A-Za-z0-9_])([+-]?(?:(?:[0-9]+\.[0-9]*|\.[0-9]+)(?:e[+-]?[0-9]+)?|[0-9]+e[+-]?[0-9]+))", 2, 2),
            token(r"(^|[^A-Za-z0-9_])([+-]?(?:0x[0-9A-Fa-f]+|[0-9]+))", 1, 2),
            // Identifiers such as 11ty and 11ty__eleventy-img. Ctrl+click can pick a segment.
            token(r"(?<![\p{L}\p{N}_])\p{N}*[\p{L}_][\p{L}\p{N}_]*", 1, 0),
            token(r"(?<![\p{L}\p{N}_])(?=(?:[\p{L}\p{N}_]+-)*\p{N}*[\p{L}_])[\p{L}\p{N}_]+(?:-[\p{L}\p{N}_]+)+", 1, 0),
        ]
    })
}

fn date_time_tokens() -> &'static [fancy_regex::Regex] {
    static TOKENS: OnceLock<Vec<fancy_regex::Regex>> = OnceLock::new();
    TOKENS.get_or_init(|| [DATE_TIME_ISO, DATE_TIME_YMD, DATE_TIME_DMY, DATE_YMD, DATE_DMY, TIME].iter().map(|pattern| fancy_regex::Regex::new(pattern).unwrap()).collect())
}

fn guid_tokens() -> &'static [(fancy_regex::Regex, usize)] {
    static TOKENS: OnceLock<Vec<(fancy_regex::Regex, usize)>> = OnceLock::new();
    TOKENS.get_or_init(|| vec![(fancy_regex::Regex::new(GUID).unwrap(), 0), (fancy_regex::Regex::new(GUID_LIKE).unwrap(), 2)])
}

fn xml_tags() -> &'static regex::Regex {
    static TAGS: OnceLock<regex::Regex> = OnceLock::new();
    TAGS.get_or_init(|| regex::Regex::new(r"<\?xml\b[^?]*\?>|<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|</?([A-Za-z_][A-Za-z0-9_:.-]*)(?:\s[^<>]*)?/?>").unwrap())
}

/// A piece of a row as text for the token patterns, with character offsets
struct Piece {
    text: String,
    /// Character offset of the piece in its row
    base: usize,
    /// Character index of every byte, when they differ
    chars: Option<Vec<u32>>,
}

impl Piece {
    fn new(line: &[char], around: usize) -> Piece {
        let (from, to) = if line.len() <= 2 * TOKEN_REACH { (0, line.len()) } else { (around.saturating_sub(TOKEN_REACH), (around + TOKEN_REACH).min(line.len())) };
        let text: String = line[from..to].iter().collect();
        let chars = (!text.is_ascii()).then(|| {
            let mut map = vec![0u32; text.len() + 1];
            let mut count = 0;
            for (index, ch) in text.char_indices() {
                map[index..index + ch.len_utf8()].fill(count);
                count += 1;
            }
            map[text.len()] = count;
            map
        });
        Piece { text, base: from, chars }
    }

    /// Character offset in the row of a byte offset in the piece
    fn col(&self, byte: usize) -> usize {
        self.base + self.chars.as_ref().map_or(byte, |map| map[byte] as usize)
    }

    /// Calls `found(start, end)` in columns of the row for every match of the group
    fn each_match(&self, regex: &fancy_regex::Regex, group: usize, mut found: impl FnMut(usize, usize) -> bool) {
        let mut at = 0;
        while at <= self.text.len() {
            let Ok(Some(captures)) = regex.captures_from_pos(&self.text, at) else { break };
            let Some(whole) = captures.get(0) else { break };
            if whole.start() == whole.end() {
                at = whole.end() + 1;
                while at < self.text.len() && !self.text.is_char_boundary(at) {
                    at += 1;
                }
                continue;
            }
            at = whole.end();
            if let Some(matched) = captures.get(group).filter(|matched| matched.start() < matched.end()) {
                if !found(self.col(matched.start()), self.col(matched.end())) {
                    return;
                }
            }
        }
    }
}

/// The rows around a click as one text, like the HTML app's `stage.text`
struct Window {
    text: Vec<char>,
    /// Offset of the first and past the last character of each row of the window
    starts: Vec<usize>,
    ends: Vec<usize>,
    /// Stage row of the first row of the window
    row0: usize,
    /// Columns cut from the start of the (only) row of the window
    col0: usize,
    stage_rows: usize,
    brackets: OnceLock<Vec<(Pair, bool)>>,
    angles: OnceLock<Vec<Pair>>,
    elements: OnceLock<Vec<Element>>,
}

impl Window {
    fn new(stage: &Stage, point: Point) -> Window {
        let row = point.row.min(stage.len().saturating_sub(1));
        let line = stage.line(row);
        let mut window = Window {
            text: Vec::new(),
            starts: Vec::new(),
            ends: Vec::new(),
            row0: row,
            col0: 0,
            stage_rows: stage.len(),
            brackets: OnceLock::new(),
            angles: OnceLock::new(),
            elements: OnceLock::new(),
        };
        if line.len() > ROW_BYTES {
            // One huge row (one-line JSON): only its part around the click
            let at = col_to_byte(line, point.col);
            let mut from = at.saturating_sub(ROW_BYTES / 2);
            while !line.is_char_boundary(from) {
                from -= 1;
            }
            let mut to = (at + ROW_BYTES / 2).min(line.len());
            while !line.is_char_boundary(to) {
                to += 1;
            }
            window.col0 = byte_to_col(line, from);
            window.text = line[from..to].chars().collect();
            window.starts.push(0);
            window.ends.push(window.text.len());
            return window;
        }
        let mut first = row;
        let mut last = row;
        let mut bytes = line.len();
        loop {
            let before = first > 0 && row - first < WINDOW_ROWS;
            let after = last + 1 < stage.len() && last - row < WINDOW_ROWS;
            if !before && !after {
                break;
            }
            let mut grew = false;
            if before {
                let size = stage.span(first - 1).len() as usize;
                if bytes + size <= WINDOW_BYTES {
                    first -= 1;
                    bytes += size + 1;
                    grew = true;
                }
            }
            if after {
                let size = stage.span(last + 1).len() as usize;
                if bytes + size <= WINDOW_BYTES {
                    last += 1;
                    bytes += size + 1;
                    grew = true;
                }
            }
            if !grew {
                break;
            }
        }
        window.row0 = first;
        for current in first..=last {
            if current > first {
                window.text.push('\n');
            }
            window.starts.push(window.text.len());
            window.text.extend(stage.line(current).chars());
            window.ends.push(window.text.len());
        }
        window
    }

    fn rows(&self) -> usize {
        self.starts.len()
    }

    fn line(&self, index: usize) -> &[char] {
        &self.text[self.starts[index]..self.ends[index]]
    }

    fn at(&self, offset: usize) -> Option<char> {
        self.text.get(offset).copied()
    }

    /// Index in the window of a stage row, when the window has it
    fn index_of(&self, row: usize) -> Option<usize> {
        (row >= self.row0 && row - self.row0 < self.rows()).then(|| row - self.row0)
    }

    /// (row of the window, column in it) of a stage point, the column not clamped
    fn local(&self, point: Point) -> Option<(usize, usize)> {
        Some((self.index_of(point.row)?, point.col.saturating_sub(self.col0)))
    }

    fn offset_of(&self, point: Point) -> Option<usize> {
        let (index, col) = self.local(point)?;
        Some(self.starts[index] + col.min(self.line(index).len()))
    }

    fn row_of(&self, offset: usize) -> usize {
        self.starts.partition_point(|start| *start <= offset).saturating_sub(1)
    }

    fn point_of(&self, offset: usize) -> Point {
        let offset = offset.min(self.text.len());
        let index = self.row_of(offset);
        Point::new(self.row0 + index, self.col0 + (offset - self.starts[index]).min(self.line(index).len()))
    }

    fn is_last_stage_row(&self, index: usize) -> bool {
        self.row0 + index + 1 >= self.stage_rows
    }

    // --- quotes -------------------------------------------------------------------------------

    fn quote_content(&self, index: usize, point_col: usize, direct_only: bool) -> Option<Range> {
        let line = self.line(index);
        if line.is_empty() {
            return None;
        }
        let col = point_col.min(line.len() - 1);
        let quote = line[col];
        if direct_only && !is_quote(quote) {
            return None;
        }
        let start = self.starts[index];
        let candidates: &[char] = if direct_only { std::slice::from_ref(&quote) } else { &['"', '\'', '`'] };
        for &quote_char in candidates {
            let is_at = |position: usize| line[position] == quote_char && !is_escaped_at(line, position);
            let left_from = if direct_only { col } else { point_col.min(line.len()) };
            let left = (0..left_from).rev().find(|&position| is_at(position));
            let right_from = if direct_only { col + 1 } else { point_col };
            let right = (right_from..line.len()).find(|&position| is_at(position));
            let content = |from: usize, to: usize| Range { start: start + from + 1, end: start + to, delimiters: Some((start + from, start + to + 1)), precedence: 0 };
            if direct_only && is_at(col) {
                match (left, right) {
                    (Some(left), Some(right)) => return Some(if right - col <= col - left { content(col, right) } else { content(left, col) }),
                    (None, Some(right)) => return Some(content(col, right)),
                    (Some(left), None) => return Some(content(left, col)),
                    (None, None) => {}
                }
            } else if !direct_only {
                if let (Some(left), Some(right)) = (left, right) {
                    if point_col > left && point_col < right {
                        return Some(content(left, right));
                    }
                }
            }
        }
        None
    }

    // --- values of a row ----------------------------------------------------------------------

    /// One group of a GUID-like value
    fn guid_group(&self, index: usize, col: usize) -> Option<Range> {
        let line = self.line(index);
        let piece = Piece::new(line, col);
        let mut result = None;
        for (regex, group) in guid_tokens() {
            piece.each_match(regex, *group, |token_start, token_end| {
                if col < token_start || col >= token_end {
                    return true;
                }
                let mut group_start = col;
                if line[col] == '-' {
                    group_start = if col + 1 < token_end { col + 1 } else { col - 1 };
                }
                let mut group_end = group_start + 1;
                while group_start > token_start && line[group_start - 1] != '-' {
                    group_start -= 1;
                }
                while group_end < token_end && line[group_end] != '-' {
                    group_end += 1;
                }
                if group_start < group_end {
                    result = Some(Range { start: self.starts[index] + group_start, end: self.starts[index] + group_end, delimiters: None, precedence: 3 });
                    return false;
                }
                true
            });
            if result.is_some() {
                break;
            }
        }
        result
    }

    /// One number of a date or time
    fn date_time_component(&self, index: usize, col: usize) -> Option<Range> {
        let line = self.line(index);
        let piece = Piece::new(line, col);
        let digit = |position: usize| line[position].is_ascii_digit();
        let mut result = None;
        for regex in date_time_tokens() {
            piece.each_match(regex, 0, |token_start, token_end| {
                if col < token_start || col >= token_end {
                    return true;
                }
                let mut start = col;
                if !digit(start) {
                    let next = (start + 1..token_end).find(|&position| digit(position));
                    let previous = (token_start..start).rev().find(|&position| digit(position));
                    match next.or(previous) {
                        Some(position) => start = position,
                        None => return true,
                    }
                }
                let mut end = start + 1;
                while start > token_start && digit(start - 1) {
                    start -= 1;
                }
                while end < token_end && digit(end) {
                    end += 1;
                }
                result = Some(Range { start: self.starts[index] + start, end: self.starts[index] + end, delimiters: None, precedence: 4 });
                false
            });
            if result.is_some() {
                break;
            }
        }
        result
    }

    fn line_token(&self, index: usize, col: usize, prefer_minimal: bool) -> Option<Range> {
        let line = self.line(index);
        if col >= line.len() {
            return None;
        }
        if prefer_minimal {
            if let Some(range) = self.date_time_component(index, col).or_else(|| self.guid_group(index, col)) {
                return Some(range);
            }
        }
        let piece = Piece::new(line, col);
        let mut candidates = Vec::new();
        for token in tokens() {
            piece.each_match(&token.regex, token.group, |start, end| {
                if col >= start && col < end {
                    candidates.push(Range { start: self.starts[index] + start, end: self.starts[index] + end, delimiters: None, precedence: token.precedence });
                }
                // Matches come in order: nothing after the click can contain it
                start <= col
            });
        }
        if prefer_minimal {
            candidates.sort_by(|left, right| left.len().cmp(&right.len()).then(left.precedence.cmp(&right.precedence)).then(left.start.cmp(&right.start)));
        } else {
            candidates.sort_by(|left, right| right.precedence.cmp(&left.precedence).then(right.len().cmp(&left.len())).then(left.start.cmp(&right.start)));
        }
        candidates.first().copied()
    }

    /// Spaces, with the line break they touch
    fn whitespace(&self, index: usize, col: usize) -> Option<Range> {
        let line = self.line(index);
        let (line_start, line_end) = (self.starts[index], self.ends[index]);
        let has_next = !self.is_last_stage_row(index) && index + 1 < self.rows();
        let with_break = if has_next { line_end + 1 } else { line_end };
        if line.is_empty() {
            return Some(Range::new(line_start, with_break));
        }
        if col >= line.len() {
            let mut start = line.len();
            while start > 0 && is_blank(line[start - 1]) {
                start -= 1;
            }
            return Some(if start < line.len() { Range::new(line_start + start, with_break) } else { Range::new(line_end, line_end) });
        }
        if !is_blank(line[col]) {
            return None;
        }
        let (mut start, mut end) = (col, col + 1);
        while start > 0 && is_blank(line[start - 1]) {
            start -= 1;
        }
        while end < line.len() && is_blank(line[end]) {
            end += 1;
        }
        let mut range = Range::new(line_start + start, line_start + end);
        if start == 0 && index > 0 {
            range.start -= 1;
        }
        if end == line.len() && has_next {
            range.end += 1;
        }
        Some(range)
    }

    // --- brackets and tags ------------------------------------------------------------------------

    /// Matching `()`, `[]` and `{}`; quotes hide brackets up to the end of their row
    fn bracket_pairs(&self) -> &[(Pair, bool)] {
        self.brackets.get_or_init(|| {
            let text = &self.text;
            let mut stack: Vec<(char, usize)> = Vec::new();
            let mut pairs = Vec::new();
            let mut quote = None;
            for (index, &ch) in text.iter().enumerate() {
                if let Some(open_quote) = quote {
                    if ch == '\n' || (ch == open_quote && !is_escaped_at(text, index)) {
                        quote = None;
                    }
                    continue;
                }
                if is_quote(ch) {
                    quote = Some(ch);
                    continue;
                }
                let expected = match ch {
                    '(' | '[' | '{' => {
                        stack.push((ch, index));
                        continue;
                    }
                    ')' => '(',
                    ']' => '[',
                    '}' => '{',
                    _ => continue,
                };
                if let Some(position) = stack.iter().rposition(|(open, _)| *open == expected) {
                    let (_, open_index) = stack.remove(position);
                    pairs.push((Pair { start: open_index, end: index + 1 }, ch != ')'));
                }
            }
            pairs
        })
    }

    fn bracket_at(&self, offset: usize) -> Option<Pair> {
        if !matches!(self.at(offset)?, '(' | ')' | '[' | ']' | '{' | '}') {
            return None;
        }
        self.bracket_pairs().iter().map(|(pair, _)| *pair).find(|pair| pair.start == offset || pair.end - 1 == offset)
    }

    /// `<...>` that looks like a tag
    fn angle_pairs(&self) -> &[Pair] {
        self.angles.get_or_init(|| {
            let text = &self.text;
            let mut pairs = Vec::new();
            let mut index = 0;
            while index < text.len() {
                if text[index] != '<' {
                    index += 1;
                    continue;
                }
                let mut quote = None;
                let mut end = None;
                for cursor in index + 1..text.len() {
                    let ch = text[cursor];
                    if let Some(open_quote) = quote {
                        if ch == open_quote && !is_escaped_at(text, cursor) {
                            quote = None;
                        }
                        continue;
                    }
                    if ch == '"' || ch == '\'' {
                        quote = Some(ch);
                    } else if ch == '>' {
                        end = Some(cursor);
                        break;
                    } else if ch == '<' {
                        break;
                    }
                }
                match end.filter(|end| is_angle_tag_content(&text[index + 1..*end])) {
                    Some(end) => {
                        pairs.push(Pair { start: index, end: end + 1 });
                        index = end + 1;
                    }
                    None => index += 1,
                }
            }
            pairs
        })
    }

    fn angle_at(&self, offset: usize) -> Option<Pair> {
        if !matches!(self.at(offset)?, '<' | '>') {
            return None;
        }
        self.angle_pairs().iter().copied().find(|pair| pair.start == offset || pair.end - 1 == offset)
    }

    /// XML/HTML elements: an opening tag with its closing tag
    fn xml_elements(&self) -> &[Element] {
        self.elements.get_or_init(|| {
            let text: String = self.text.iter().collect();
            // Character offset of every byte offset a tag can start or end at
            let ascii = text.is_ascii();
            let offsets: Vec<usize> = if ascii { Vec::new() } else { text.char_indices().map(|(index, _)| index).collect() };
            let to_char = |byte: usize| if ascii { byte } else { offsets.partition_point(|index| *index < byte) };
            let mut stack: Vec<(String, usize, usize)> = Vec::new();
            let mut elements = Vec::new();
            for captures in xml_tags().captures_iter(&text) {
                let whole = captures.get(0).unwrap();
                let raw = whole.as_str();
                if raw.starts_with("<?") || raw.starts_with("<!") {
                    continue;
                }
                let (start, end) = (to_char(whole.start()), to_char(whole.end()));
                let name = captures.get(1).map_or("", |name| name.as_str());
                if raw.ends_with("/>") && !raw.starts_with("</") {
                    elements.push(Element { start, end, open_tag_end: end, close_start: end });
                } else if !raw.starts_with("</") {
                    stack.push((name.to_string(), start, end));
                } else if let Some(position) = stack.iter().rposition(|(open, _, _)| open == name) {
                    let (_, open_start, open_end) = stack.remove(position);
                    elements.push(Element { start: open_start, end, open_tag_end: open_end, close_start: start });
                }
            }
            elements.sort_by(|left, right| left.start.cmp(&right.start).then(right.end.cmp(&left.end)));
            elements
        })
    }

    fn xml_element_at_tag(&self, offset: usize) -> Option<&Element> {
        self.xml_elements().iter().find(|element| offset == element.start || offset + 1 == element.open_tag_end || offset == element.close_start || offset + 1 == element.end)
    }

    // --- expressions around = --------------------------------------------------------------------

    fn assignment_operator_at(&self, index: usize, col: usize) -> Option<(usize, usize)> {
        let line = self.line(index);
        let operator_char = |position: Option<usize>| position.and_then(|position| line.get(position)).is_some_and(|ch| "=!<>".contains(*ch));
        for operator in ["===", "!==", "==", "=>", "!=", "<=", ">=", "="] {
            let operator: Vec<char> = operator.chars().collect();
            let mut from = 0;
            while from + operator.len() <= line.len() {
                let Some(found) = (from..=line.len() - operator.len()).find(|&position| line[position..position + operator.len()] == operator[..]) else { break };
                let end = found + operator.len();
                if col >= found && col < end {
                    if operator_char(found.checked_sub(1)) || operator_char(Some(end)) {
                        break;
                    }
                    return Some((found, end));
                }
                from = found + 1;
            }
        }
        None
    }

    /// Where the expression left of the operator starts, in the row
    fn assignment_start(&self, line: &[char], operator_start: usize) -> usize {
        let mut quote = None;
        let mut depth = 0usize;
        for index in (0..operator_start).rev() {
            let ch = line[index];
            if let Some(open_quote) = quote {
                if ch == open_quote && !is_escaped_at(line, index) {
                    quote = None;
                }
                continue;
            }
            if is_quote(ch) {
                quote = Some(ch);
            } else if ")]}>".contains(ch) {
                depth += 1;
            } else if "([{<".contains(ch) {
                if depth == 0 {
                    return index + 1;
                }
                depth -= 1;
            } else if depth == 0 && (ch == ',' || ch == ';') {
                return index + 1;
            }
        }
        0
    }

    /// Where the expression right of the operator ends, in the window
    fn expression_end(&self, from: usize) -> usize {
        let text = &self.text;
        let mut quote = None;
        let mut depth = 0usize;
        for index in from..text.len() {
            let ch = text[index];
            if let Some(open_quote) = quote {
                if ch == '\n' || (ch == open_quote && !is_escaped_at(text, index)) {
                    quote = None;
                }
                continue;
            }
            if is_quote(ch) {
                quote = Some(ch);
            } else if "([{<".contains(ch) {
                depth += 1;
            } else if ")]}>".contains(ch) {
                if depth == 0 {
                    return index;
                }
                depth -= 1;
            } else if depth == 0 && (ch == ',' || ch == ';') {
                return index;
            } else if depth == 0 && ch == '\n' {
                // A call chain continues on the next row with a dot
                let next = (index + 1..text.len()).find(|&position| !is_blank(text[position]));
                if next.and_then(|position| self.at(position)) != Some('.') {
                    return index;
                }
            }
        }
        text.len()
    }

    fn assignment(&self, index: usize, col: usize) -> Option<Range> {
        let (local_start, local_end) = self.assignment_operator_at(index, col)?;
        let line_start = self.starts[index];
        let (operator_start, operator_end) = (line_start + local_start, line_start + local_end);
        let mut start = line_start + self.assignment_start(self.line(index), local_start);
        let mut end = self.expression_end(operator_end);
        while start < end && self.text[start].is_whitespace() {
            start += 1;
        }
        while end > start && self.text[end - 1].is_whitespace() {
            end -= 1;
        }
        // An index or call right before the operator belongs to the left side
        let mut bracket_end = operator_start;
        while bracket_end > 0 && self.text[bracket_end - 1].is_whitespace() {
            bracket_end -= 1;
        }
        if let Some((pair, _)) = self.bracket_pairs().iter().find(|(pair, _)| pair.end == bracket_end) {
            start = start.min(pair.start);
        }
        (start < end && start <= operator_start && operator_end <= end).then(|| Range::new(start, end))
    }

    // --- choosing ---------------------------------------------------------------------------------

    /// What the character under the pointer selects by itself: a quote, a bracket, a tag, `=`
    fn direct_structure(&self, index: usize, col: usize) -> Option<Range> {
        let offset = self.starts[index] + col.min(self.line(index).len());
        let ch = self.at(offset)?;
        if "=!<>".contains(ch) {
            if let Some(range) = self.assignment(index, col) {
                return Some(range);
            }
        }
        if is_quote(ch) {
            return self.quote_content(index, col, true);
        }
        if ch == '<' || ch == '>' {
            if let Some(pair) = self.angle_at(offset) {
                return Some(pair.content());
            }
            return self.xml_element_at_tag(offset).map(Element::content);
        }
        self.bracket_at(offset).map(|pair| pair.content())
    }

    fn xml_expansions(&self, current: &Range) -> Vec<Range> {
        let elements = self.xml_elements();
        let mut ranges = Vec::new();
        for (position, element) in elements.iter().enumerate() {
            let (content, full) = (element.content(), element.full());
            if current.same(&content) {
                ranges.push(full);
            } else if content.contains(current) {
                ranges.push(content);
            }
            if !current.same(&full) {
                continue;
            }
            let is_top = |candidate: &Element| !elements.iter().any(|other| other.start < candidate.start && other.end > candidate.end);
            let top: Vec<usize> = (0..elements.len()).filter(|&candidate| is_top(&elements[candidate])).collect();
            if top.len() == 1 && top[0] == position {
                // With its <?xml ...?> header, when the view starts with one
                if self.row0 == 0 && self.col0 == 0 {
                    let text: String = self.text[..element.start].iter().collect();
                    let header = text.trim_start();
                    if header.starts_with("<?xml") && header.trim_end().ends_with("?>") && header.matches("?>").count() == 1 {
                        ranges.push(Range::new(0, element.end));
                    }
                }
            } else if let Some(mut first) = top.iter().position(|&candidate| candidate == position) {
                // Neighbouring elements with only whitespace between them
                let mut last = first;
                let blank_between = |left: usize, right: usize| self.text[elements[top[left]].end..elements[top[right]].start].iter().all(|ch| ch.is_whitespace());
                while first > 0 && blank_between(first - 1, first) {
                    first -= 1;
                }
                while last + 1 < top.len() && blank_between(last, last + 1) {
                    last += 1;
                }
                if last > first {
                    ranges.push(Range::new(elements[top[first]].start, elements[top[last]].end));
                }
            }
        }
        ranges
    }

    /// Every structure around the pointer
    fn structures_at(&self, index: usize, col: usize) -> Vec<Range> {
        let offset = self.starts[index] + col.min(self.line(index).len());
        let mut ranges = Vec::new();
        if let Some(quoted) = self.quote_content(index, col, false) {
            ranges.push(quoted);
            if let Some((start, end)) = quoted.delimiters {
                ranges.push(Range::new(start, end));
            }
        }
        let pairs = self.bracket_pairs().iter().map(|(pair, _)| pair).chain(self.angle_pairs());
        for pair in pairs {
            ranges.extend([pair.content(), pair.full()].into_iter().filter(|range| range.contains_offset(offset)));
        }
        for element in self.xml_elements() {
            ranges.extend([element.content(), element.full()].into_iter().filter(|range| range.contains_offset(offset)));
        }
        ranges.extend(self.line_token(index, col, true).filter(|range| range.contains_offset(offset)));
        ranges
    }

    /// The next larger structure around the current selection
    fn expansion(&self, index: usize, col: usize, current: &Range) -> Option<Range> {
        let offset = self.starts[index] + col.min(self.line(index).len());
        let mut candidates: Vec<Range> =
            self.structures_at(index, col).into_iter().filter(|range| range.contains_offset(offset) && range.contains(current) && !range.same(current)).collect();
        candidates.extend(self.xml_expansions(current));
        candidates.sort_by(|left, right| left.len().cmp(&right.len()).then(left.start.cmp(&right.start)));
        candidates.first().copied()
    }

    /// The next smaller structure inside the current selection
    fn shrink(&self, index: usize, col: usize, current: &Range) -> Option<Range> {
        let offset = self.starts[index] + col.min(self.line(index).len());
        let mut candidates: Vec<Range> = self
            .structures_at(index, col)
            .into_iter()
            .filter(|range| current.contains(range) && range.contains_offset(offset) && !range.same(current) && range.len() < current.len())
            .collect();
        candidates.sort_by(|left, right| right.len().cmp(&left.len()).then(left.start.cmp(&right.start)));
        candidates.first().copied()
    }

    fn structural(&self, index: usize, col: usize, prefer_minimal: bool) -> Option<Range> {
        self.direct_structure(index, col).or_else(|| self.whitespace(index, col)).or_else(|| self.line_token(index, col, prefer_minimal))
    }

    /// A click on the delimiter of the selected content takes the delimiters in
    fn repeated_delimiter(&self, index: usize, col: usize, current: &Range) -> Option<Range> {
        let clicked = self.starts[index] + col.min(self.line(index).len());
        if current.start > 0 && (clicked == current.start - 1 || clicked == current.end) {
            let (left, right) = (self.at(current.start - 1), self.at(current.end));
            if left.is_some_and(is_quote) && left == right && !is_escaped_at(&self.text, current.start - 1) && !is_escaped_at(&self.text, current.end) {
                return Some(Range::new(current.start - 1, current.end + 1));
            }
        }
        if let Some(pair) = self.bracket_at(clicked).or_else(|| self.angle_at(clicked)) {
            if current.same(&pair.content()) {
                return Some(pair.full());
            }
        }
        let direct = self.direct_structure(index, col)?;
        if !direct.same(current) {
            return None;
        }
        if let Some((start, end)) = direct.delimiters {
            return Some(Range::new(start, end));
        }
        let mut candidates: Vec<Range> = self
            .structures_at(index, col)
            .into_iter()
            .filter(|range| range.contains_offset(clicked) && range.contains(current) && !range.same(current) && range.len() > current.len())
            .collect();
        candidates.sort_by(|left, right| left.len().cmp(&right.len()).then(left.start.cmp(&right.start)));
        candidates.first().copied()
    }
}

fn is_angle_tag_content(content: &[char]) -> bool {
    let text: String = content.iter().collect();
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return false;
    }
    let bytes = trimmed.as_bytes();
    // A tag name, optionally closing: </name ...
    let name_from = usize::from(bytes[0] == b'/');
    if bytes.get(name_from).is_some_and(|byte| byte.is_ascii_alphabetic() || *byte == b'_') {
        let name_end = (name_from + 1..bytes.len()).find(|&index| !(bytes[index].is_ascii_alphanumeric() || b"_:.-".contains(&bytes[index]))).unwrap_or(bytes.len());
        if name_end == bytes.len() || bytes[name_end] == b'/' || trimmed[name_end..].starts_with(char::is_whitespace) {
            return true;
        }
    }
    if bytes[0] == b'!' && bytes.get(1).is_some_and(u8::is_ascii_alphabetic) {
        return true;
    }
    if trimmed.len() >= 4 && trimmed[..4].eq_ignore_ascii_case("?xml") && !bytes.get(4).is_some_and(|byte| byte.is_ascii_alphanumeric() || *byte == b'_') {
        return true;
    }
    trimmed.len() >= 5 && trimmed.starts_with("!--") && trimmed.ends_with("--")
}

/// What a click at `point` selects, given the current selection: `None` leaves a caret.
/// `shrink` is Ctrl+click (the smallest useful part, or the next smaller structure);
/// with `extend` (Shift+click) the current selection plays no part.
pub fn click_range(stage: &Stage, point: Point, current: Option<&Selection>, shrink: bool, extend: bool) -> Option<(Point, Point)> {
    if stage.is_empty() {
        return None;
    }
    let window = Window::new(stage, point);
    let (index, col) = window.local(point)?;
    let clicked = window.offset_of(point)?;
    let current = current.filter(|_| !extend).and_then(|selection| {
        let selection = selection.normalized();
        Some(Range::new(window.offset_of(selection.start)?, window.offset_of(selection.end)?))
    });
    let mut growth = None;
    if let Some(current) = &current {
        if !shrink {
            growth = window.repeated_delimiter(index, col, current);
        }
        if growth.is_none() && current.end > current.start && clicked >= current.start && clicked <= current.end {
            growth = if shrink { window.shrink(index, col, current) } else { window.expansion(index, col, current) };
        }
    }
    let range = growth.or_else(|| window.structural(index, col, shrink))?;
    let (start, end) = (range.start.min(range.end), range.start.max(range.end));
    Some((window.point_of(start), window.point_of(end)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::selection::selection_text;
    use crate::stage::testing::*;

    /// The text a plain click on the first `needle` of row `row` selects
    fn click(stage: &Stage, row: usize, needle: &str) -> String {
        click_with(stage, row, needle, None, false).1
    }

    fn click_with(stage: &Stage, row: usize, needle: &str, current: Option<&Selection>, shrink: bool) -> (Option<Selection>, String) {
        let col = stage.line(row).chars().count() - stage.line(row)[stage.line(row).find(needle).expect("needle in row")..].chars().count();
        let selection = click_range(stage, Point::new(row, col), current, shrink, false).map(|(start, end)| Selection::new(start, end));
        let text = selection.as_ref().map_or(String::new(), |selection| selection_text(stage, selection));
        (selection, text)
    }

    #[test]
    fn a_click_selects_the_value_under_the_pointer() {
        let stage = stage_of(&[
            "2026-03-13T09:14:02.105Z ERROR api.Checkout id=823 took 12.5ms from 10.0.0.7",
            "GET https://example.com/a?b=1 C:\\logs\\app.log 1.2.3-beta 3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            "13.3.2026 10:02 /var/log/syslog count=0x1F žluťoučký_kůň",
        ]);
        assert_eq!(click(&stage, 0, "03-13"), "2026-03-13T09:14:02.105Z");
        assert_eq!(click(&stage, 0, "ERROR"), "ERROR");
        assert_eq!(click(&stage, 0, "Checkout"), "api.Checkout");
        assert_eq!(click(&stage, 0, "823"), "823");
        assert_eq!(click(&stage, 0, "12.5"), "12.5");
        assert_eq!(click(&stage, 0, "0.0.7"), "10.0.0.7");
        assert_eq!(click(&stage, 1, "example"), "https://example.com/a?b=1");
        assert_eq!(click(&stage, 1, "logs"), "C:\\logs\\app.log");
        assert_eq!(click(&stage, 1, "2.3"), "1.2.3-beta");
        assert_eq!(click(&stage, 1, "4f89"), "3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        assert_eq!(click(&stage, 2, "3.2026"), "13.3.2026 10:02");
        assert_eq!(click(&stage, 2, "log/"), "/var/log/syslog");
        assert_eq!(click(&stage, 2, "0x1F"), "0x1F");
        assert_eq!(click(&stage, 2, "kůň"), "žluťoučký_kůň");
    }

    #[test]
    fn an_identifier_can_start_with_numbers_and_contain_a_hyphen() {
        let stage = stage_of(&["plugin 11ty__eleventy-img enabled"]);
        for needle in ["11ty", "eleventy", "-img", "img"] {
            assert_eq!(click(&stage, 0, needle), "11ty__eleventy-img");
        }
        assert_eq!(click_with(&stage, 0, "img", None, true).1, "img");
        assert_eq!(click_with(&stage, 0, "eleventy", None, true).1, "11ty__eleventy");
    }

    #[test]
    fn ctrl_click_selects_the_smallest_useful_part() {
        let stage = stage_of(&["2026-03-13T09:14:02.105Z 3f2504e0-4f89-11d3-9a0c-0305e82c3301 api.Checkout"]);
        let part = |needle: &str| click_with(&stage, 0, needle, None, true).1;
        assert_eq!(part("03-13"), "03");
        assert_eq!(part("14:02"), "14");
        assert_eq!(part("4f89"), "4f89");
        assert_eq!(part("Checkout"), "Checkout");
    }

    #[test]
    fn whitespace_is_selected_with_the_line_break_it_touches() {
        let stage = stage_of(&["first   second", "    indented", "", "last  "]);
        assert_eq!(click(&stage, 0, "   second"), "   ");
        let indent = click_range(&stage, Point::new(1, 2), None, false, false).unwrap();
        assert_eq!(indent, (Point::new(0, 14), Point::new(1, 4)));
        // An empty row: the row with its line break
        assert_eq!(click_range(&stage, Point::new(2, 0), None, false, false).unwrap(), (Point::new(2, 0), Point::new(3, 0)));
        // Past the end of the last row: its trailing spaces, there is no break to take
        assert_eq!(click_range(&stage, Point::new(3, 20), None, false, false).unwrap(), (Point::new(3, 4), Point::new(3, 6)));
        // Past the end of a row without trailing spaces: a caret
        assert_eq!(click_range(&stage, Point::new(0, 40), None, false, false).unwrap(), (Point::new(0, 14), Point::new(0, 14)));
    }

    #[test]
    fn quotes_and_brackets_select_their_content_then_themselves() {
        let stage = stage_of(&[r#"call(a, [1, 2], "x y") {"k": "v"}"#]);
        assert_eq!(click(&stage, 0, "\"x"), "x y");
        assert_eq!(click(&stage, 0, "(a"), r#"a, [1, 2], "x y""#);
        assert_eq!(click(&stage, 0, "[1"), "1, 2");
        assert_eq!(click(&stage, 0, "{\"k"), r#""k": "v""#);
        // The same delimiter again takes the delimiters in
        let (content, _) = click_with(&stage, 0, "[1", None, false);
        assert_eq!(click_with(&stage, 0, "[1", content.as_ref(), false).1, "[1, 2]");
        let (quoted, _) = click_with(&stage, 0, "\"x", None, false);
        assert_eq!(click_with(&stage, 0, "\"x", quoted.as_ref(), false).1, "\"x y\"");
    }

    #[test]
    fn clicking_inside_the_selection_grows_it_and_ctrl_click_shrinks_it_back() {
        let stage = stage_of(&[r#"x {"user": {"name": "Ann Lee", "id": 7}} y"#]);
        let (token, text) = click_with(&stage, 0, "Ann", None, false);
        assert_eq!(text, "Ann");
        let (content, text) = click_with(&stage, 0, "Ann", token.as_ref(), false);
        assert_eq!(text, "Ann Lee");
        let (quoted, text) = click_with(&stage, 0, "Ann", content.as_ref(), false);
        assert_eq!(text, "\"Ann Lee\"");
        let (inner, text) = click_with(&stage, 0, "Ann", quoted.as_ref(), false);
        assert_eq!(text, r#""name": "Ann Lee", "id": 7"#);
        let (braces, text) = click_with(&stage, 0, "Ann", inner.as_ref(), false);
        assert_eq!(text, r#"{"name": "Ann Lee", "id": 7}"#);
        assert_eq!(click_with(&stage, 0, "Ann", braces.as_ref(), false).1, r#""user": {"name": "Ann Lee", "id": 7}"#);
        // Ctrl+click goes back down
        assert_eq!(click_with(&stage, 0, "Ann", braces.as_ref(), true).1, r#""name": "Ann Lee", "id": 7"#);
        assert_eq!(click_with(&stage, 0, "Ann", quoted.as_ref(), true).1, "Ann Lee");
        assert_eq!(click_with(&stage, 0, "Ann", content.as_ref(), true).1, "Ann");
    }

    #[test]
    fn an_equals_sign_selects_the_expression_around_it() {
        let stage = stage_of(&["set(a = b + 1, c == d); user.name = getName(x, y);", "let total = items", "    .map(f)", "    .sum();", "next"]);
        assert_eq!(click(&stage, 0, "= b"), "a = b + 1");
        assert_eq!(click(&stage, 0, "== d"), "c == d");
        assert_eq!(click(&stage, 0, "= getName"), "user.name = getName(x, y)");
        assert_eq!(click(&stage, 1, "= items"), "let total = items\n    .map(f)\n    .sum()");
    }

    #[test]
    fn tags_select_their_content_and_grow_to_the_element() {
        let stage = stage_of(&["<root>", "  <item id=\"1\">one</item>", "  <item id=\"2\">two</item>", "</root>"]);
        assert_eq!(click(&stage, 1, "<item"), "item id=\"1\"");
        let (token, text) = click_with(&stage, 1, "one", None, false);
        assert_eq!(text, "one");
        let (element, text) = click_with(&stage, 1, "one", token.as_ref(), false);
        assert_eq!(text, "<item id=\"1\">one</item>");
        let (inner, text) = click_with(&stage, 1, "one", element.as_ref(), false);
        assert_eq!(text, "\n  <item id=\"1\">one</item>\n  <item id=\"2\">two</item>\n");
        assert_eq!(click_with(&stage, 1, "one", inner.as_ref(), false).1, "<root>\n  <item id=\"1\">one</item>\n  <item id=\"2\">two</item>\n</root>");
    }

    #[test]
    fn a_huge_row_is_only_looked_at_around_the_click() {
        let unit = r#"{"id": 12345, "name": "value"}, "#;
        let line = format!("[{}]", unit.repeat(200_000));
        let stage = stage_of(&[&line]);
        let col = 3_000_000 + line[3_000_000..].find("12345").unwrap();
        let (start, end) = click_range(&stage, Point::new(0, col), None, false, false).unwrap();
        assert_eq!((start, end), (Point::new(0, col), Point::new(0, col + 5)));
    }
}
