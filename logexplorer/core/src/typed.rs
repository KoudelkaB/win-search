//! Values read from rows: numbers, dates and durations, and how Diff writes them back.

use chrono::{Datelike, Local, NaiveDate, NaiveDateTime, TimeZone};
use std::cell::Cell;
use std::fmt::Write as _;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ValueType {
    String,
    Number,
    DateTime,
    TimeSpan,
}

impl ValueType {
    pub const ALL: [ValueType; 4] = [ValueType::String, ValueType::Number, ValueType::DateTime, ValueType::TimeSpan];

    pub fn as_str(self) -> &'static str {
        match self {
            ValueType::String => "string",
            ValueType::Number => "number",
            ValueType::DateTime => "date-time",
            ValueType::TimeSpan => "time-span",
        }
    }

    pub fn parse(text: &str) -> Self {
        match text {
            "number" => ValueType::Number,
            "date-time" => ValueType::DateTime,
            "time-span" => ValueType::TimeSpan,
            _ => ValueType::String,
        }
    }

    /// The name shown in the UI (translated there)
    pub fn label(self) -> &'static str {
        match self {
            ValueType::String => "Text",
            ValueType::Number => "Number",
            ValueType::DateTime => "Date and time",
            ValueType::TimeSpan => "Duration",
        }
    }
}

// --- JavaScript number rules ----------------------------------------------------------------

/// `Number(text)` for an already trimmed, non-empty text; `None` for NaN
fn js_number(text: &str) -> Option<f64> {
    let bytes = text.as_bytes();
    if bytes.len() > 2 && bytes[0] == b'0' {
        let radix = match bytes[1] {
            b'x' | b'X' => 16,
            b'o' | b'O' => 8,
            b'b' | b'B' => 2,
            _ => 0,
        };
        if radix != 0 {
            let digits = &text[2..];
            if !digits.bytes().all(|byte| (byte as char).is_digit(radix)) {
                return None;
            }
            return Some(digits.bytes().fold(0f64, |value, byte| value * radix as f64 + (byte as char).to_digit(radix).unwrap() as f64));
        }
    }
    let mut index = 0;
    if index < bytes.len() && (bytes[index] == b'+' || bytes[index] == b'-') {
        index += 1;
    }
    if &text[index..] == "Infinity" {
        return Some(if bytes[0] == b'-' { f64::NEG_INFINITY } else { f64::INFINITY });
    }
    let digits_from = index;
    while index < bytes.len() && bytes[index].is_ascii_digit() {
        index += 1;
    }
    let mut digits = index - digits_from;
    if index < bytes.len() && bytes[index] == b'.' {
        index += 1;
        let fraction_from = index;
        while index < bytes.len() && bytes[index].is_ascii_digit() {
            index += 1;
        }
        digits += index - fraction_from;
    }
    if digits == 0 {
        return None;
    }
    if index < bytes.len() && (bytes[index] == b'e' || bytes[index] == b'E') {
        index += 1;
        if index < bytes.len() && (bytes[index] == b'+' || bytes[index] == b'-') {
            index += 1;
        }
        let exponent_from = index;
        while index < bytes.len() && bytes[index].is_ascii_digit() {
            index += 1;
        }
        if index == exponent_from {
            return None;
        }
    }
    if index != bytes.len() {
        return None;
    }
    text.parse::<f64>().ok()
}

/// `Math.round`: halves go up
pub fn js_round(value: f64) -> f64 {
    if value.abs() >= 4503599627370496.0 {
        value
    } else {
        (value + 0.5).floor()
    }
}

/// `String(number)` written directly into `out`
fn write_js_string(out: &mut String, value: f64) {
    if value == 0.0 {
        out.push('0');
    } else if value.fract() == 0.0 && value.abs() < 1e21 {
        let _ = write!(out, "{value:.0}");
    } else {
        let _ = write!(out, "{value}");
    }
}

/// `String(number)`
pub fn js_string(value: f64) -> String {
    let mut out = String::with_capacity(24);
    write_js_string(&mut out, value);
    out
}

struct StackBuf<const N: usize> {
    buf: [u8; N],
    len: usize,
    overflow: bool,
}

impl<const N: usize> StackBuf<N> {
    #[inline]
    fn new() -> Self {
        StackBuf { buf: [0; N], len: 0, overflow: false }
    }

    #[inline]
    fn as_str(&self) -> &str {
        // SAFETY: only valid UTF-8 from `fmt::Write::write_str` is pushed
        unsafe { std::str::from_utf8_unchecked(&self.buf[..self.len]) }
    }
}

impl<const N: usize> std::fmt::Write for StackBuf<N> {
    #[inline]
    fn write_str(&mut self, text: &str) -> std::fmt::Result {
        let bytes = text.as_bytes();
        if self.len + bytes.len() > N {
            self.overflow = true;
            return Err(std::fmt::Error);
        }
        self.buf[self.len..self.len + bytes.len()].copy_from_slice(bytes);
        self.len += bytes.len();
        Ok(())
    }
}

fn round_up_decimal(out: &mut String, longer: &str) {
    let mut kept = [0u8; 128];
    let prefix = longer[..longer.len() - 1].trim_end_matches('.');
    if prefix.len() + 1 <= kept.len() {
        let len = prefix.len();
        kept[1..1 + len].copy_from_slice(prefix.as_bytes());
        let mut position = 1 + len;
        let mut carry = true;
        while position > 1 {
            position -= 1;
            match kept[position] {
                b'.' => continue,
                b'9' => kept[position] = b'0',
                digit => {
                    kept[position] = digit + 1;
                    carry = false;
                    break;
                }
            }
        }
        let slice = if carry {
            kept[0] = b'1';
            &kept[..1 + len]
        } else {
            &kept[1..1 + len]
        };
        // SAFETY: ASCII digits and '.'
        out.push_str(unsafe { std::str::from_utf8_unchecked(slice) });
        return;
    }
    let mut vec: Vec<u8> = prefix.bytes().collect();
    let mut position = vec.len();
    loop {
        if position == 0 {
            vec.insert(0, b'1');
            break;
        }
        position -= 1;
        match vec[position] {
            b'.' => continue,
            b'9' => vec[position] = b'0',
            digit => {
                vec[position] = digit + 1;
                break;
            }
        }
    }
    // SAFETY: ASCII digits and '.'
    out.push_str(unsafe { std::str::from_utf8_unchecked(&vec) });
}

/// `number.toFixed(digits)` written into `out`: an exact half rounds away from zero
fn write_fixed(out: &mut String, value: f64, digits: usize) {
    if !value.is_finite() {
        write_js_string(out, value);
        return;
    }
    let absolute = value.abs();
    if value < 0.0 {
        out.push('-');
    }
    // Exact integer values can never land on an exact half-way rounding boundary.
    if absolute.fract() == 0.0 {
        let _ = write!(out, "{absolute:.digits$}");
        return;
    }
    let start = out.len();
    let _ = write!(out, "{absolute:.digits$}");
    // Rust rounds an exact half to even. A half is exact only when one more digit ends the
    // number, so that is the one case to correct.
    let mut stack_longer = StackBuf::<64>::new();
    let longer_owned: String;
    let longer: &str = if write!(stack_longer, "{:.*}", digits + 1, absolute).is_ok() && !stack_longer.overflow {
        stack_longer.as_str()
    } else {
        longer_owned = format!("{:.*}", digits + 1, absolute);
        &longer_owned
    };
    if longer.ends_with('5') {
        let mut stack_ext = StackBuf::<96>::new();
        let ext_owned: String;
        let extended: &str = if write!(stack_ext, "{:.*}", digits + 30, absolute).is_ok() && !stack_ext.overflow {
            stack_ext.as_str()
        } else {
            ext_owned = format!("{:.*}", digits + 30, absolute);
            &ext_owned
        };
        if extended.trim_end_matches('0') == longer.trim_end_matches('0') {
            out.truncate(start);
            round_up_decimal(out, longer);
        }
    }
}

/// `number.toFixed(digits)`: an exact half rounds away from zero
pub fn to_fixed(value: f64, digits: usize) -> String {
    let mut out = String::with_capacity(digits + 16);
    write_fixed(&mut out, value, digits);
    out
}

pub fn parse_number(raw: &str) -> Option<f64> {
    let text = raw.trim();
    if text.is_empty() {
        return None;
    }
    let value = if text.contains(',') { js_number(&text.replace(',', ".")) } else { js_number(text) }?;
    value.is_finite().then_some(value)
}

// --- durations ----------------------------------------------------------------------------------

/// The parts of `[-][d.]hh:mm[:ss][.fff]`
pub struct SpanParts<'a> {
    pub negative: bool,
    pub days: Option<&'a str>,
    pub hours: &'a str,
    pub minutes: &'a str,
    pub seconds: Option<&'a str>,
    pub fraction: Option<&'a str>,
}

fn digit_run(bytes: &[u8], from: usize) -> usize {
    let mut index = from;
    while index < bytes.len() && bytes[index].is_ascii_digit() {
        index += 1;
    }
    index
}

/// `^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.(\d{1,3}))?$`
pub fn match_time_span(text: &str) -> Option<SpanParts<'_>> {
    let bytes = text.as_bytes();
    let mut index = 0;
    let negative = bytes.first() == Some(&b'-');
    if negative {
        index = 1;
    }
    let first_end = digit_run(bytes, index);
    if first_end == index {
        return None;
    }
    let (days, hours_from) = if bytes.get(first_end) == Some(&b'.') { (Some(&text[index..first_end]), first_end + 1) } else { (None, index) };
    let hours_end = digit_run(bytes, hours_from);
    if hours_end == hours_from || hours_end - hours_from > 2 || bytes.get(hours_end) != Some(&b':') {
        return None;
    }
    let minutes_from = hours_end + 1;
    let minutes_end = digit_run(bytes, minutes_from);
    if minutes_end - minutes_from != 2 {
        return None;
    }
    let mut index = minutes_end;
    let mut seconds = None;
    if bytes.get(index) == Some(&b':') {
        let seconds_end = digit_run(bytes, index + 1);
        if seconds_end - (index + 1) != 2 {
            return None;
        }
        seconds = Some(&text[index + 1..seconds_end]);
        index = seconds_end;
    }
    let mut fraction = None;
    if bytes.get(index) == Some(&b'.') {
        let fraction_end = digit_run(bytes, index + 1);
        let length = fraction_end - (index + 1);
        if length == 0 || length > 3 {
            return None;
        }
        fraction = Some(&text[index + 1..fraction_end]);
        index = fraction_end;
    }
    if index != bytes.len() {
        return None;
    }
    Some(SpanParts { negative, days, hours: &text[hours_from..hours_end], minutes: &text[minutes_from..minutes_end], seconds, fraction })
}

fn digits_value(text: &str) -> f64 {
    text.bytes().fold(0f64, |value, byte| value * 10.0 + (byte - b'0') as f64)
}

/// Milliseconds of a duration; a plain number counts as milliseconds
pub fn parse_time_span(raw: &str) -> Option<f64> {
    let text = raw.trim();
    if text.is_empty() {
        return None;
    }
    let Some(parts) = match_time_span(text) else {
        return js_number(text).filter(|value| value.is_finite());
    };
    let days = parts.days.map_or(0.0, digits_value);
    let hours = digits_value(parts.hours);
    let minutes = digits_value(parts.minutes);
    let seconds = parts.seconds.map_or(0.0, digits_value);
    let millis = parts.fraction.map_or(0.0, |fraction| digits_value(fraction) * 10f64.powi(3 - fraction.len() as i32));
    if minutes > 59.0 || seconds > 59.0 || (parts.days.is_some() && hours > 23.0) {
        return None;
    }
    let sign = if parts.negative { -1.0 } else { 1.0 };
    Some(sign * ((((days * 24.0 + hours) * 60.0 + minutes) * 60.0 + seconds) * 1000.0 + millis))
}

#[inline]
fn push_two_digits(out: &mut String, value: u64) {
    let buf = [b'0' + (value / 10) as u8, b'0' + (value % 10) as u8];
    // SAFETY: ASCII digits
    out.push_str(unsafe { std::str::from_utf8_unchecked(&buf) });
}

#[inline]
fn push_three_digits(out: &mut String, value: u64) {
    let buf = [b'0' + (value / 100) as u8, b'0' + ((value / 10) % 10) as u8, b'0' + (value % 10) as u8];
    // SAFETY: ASCII digits
    out.push_str(unsafe { std::str::from_utf8_unchecked(&buf) });
}

#[inline]
fn push_u64_padded(out: &mut String, mut value: u64, min_width: usize) {
    let mut buf = [0u8; 20];
    let mut index = buf.len();
    loop {
        index -= 1;
        buf[index] = b'0' + (value % 10) as u8;
        value /= 10;
        if value == 0 {
            break;
        }
    }
    let digits = buf.len() - index;
    for _ in digits..min_width {
        out.push('0');
    }
    // SAFETY: ASCII digits
    out.push_str(unsafe { std::str::from_utf8_unchecked(&buf[index..]) });
}

#[inline]
fn push_spaces(out: &mut String, count: usize) {
    const SPACES: &str = "                                ";
    let mut remaining = count;
    while remaining >= SPACES.len() {
        out.push_str(SPACES);
        remaining -= SPACES.len();
    }
    if remaining > 0 {
        out.push_str(&SPACES[..remaining]);
    }
}

pub fn write_time_span(out: &mut String, milliseconds: f64) {
    if milliseconds < 0.0 {
        out.push('-');
    }
    let mut value = js_round(milliseconds).abs() as u64;
    let millis = value % 1000;
    value /= 1000;
    let seconds = value % 60;
    value /= 60;
    let minutes = value % 60;
    value /= 60;
    let hours = value % 24;
    let days = value / 24;
    if days > 0 {
        push_u64_padded(out, days, 1);
        out.push('.');
    }
    push_two_digits(out, hours);
    out.push(':');
    push_two_digits(out, minutes);
    out.push(':');
    push_two_digits(out, seconds);
    out.push('.');
    push_three_digits(out, millis);
}

pub fn format_time_span(milliseconds: f64) -> String {
    let mut out = String::with_capacity(20);
    write_time_span(&mut out, milliseconds);
    out
}

// --- dates ----------------------------------------------------------------------------------------

#[derive(Clone, Copy)]
enum HourOffset {
    /// Every instant in `[HH:00:00.000, HH:59:59.999]` shares this unambiguous UTC offset (ms).
    Fixed(i64),
    /// A DST boundary or unusual offset change occurs in this hour: query chrono per timestamp.
    Transition,
}

const TZ_CACHE_SLOTS: usize = 16;

thread_local! {
    static LOCAL_TZ_CACHE: Cell<[(i64, HourOffset); TZ_CACHE_SLOTS]> = const { Cell::new([(i64::MIN, HourOffset::Transition); TZ_CACHE_SLOTS]) };
}

fn compute_hour_offset(date: NaiveDate, hours: u32) -> HourOffset {
    let Some(start) = date.and_hms_milli_opt(hours, 0, 0, 0) else {
        return HourOffset::Transition;
    };
    let Some(mid) = date.and_hms_milli_opt(hours, 30, 0, 0) else {
        return HourOffset::Transition;
    };
    let Some(end) = date.and_hms_milli_opt(hours, 59, 59, 999) else {
        return HourOffset::Transition;
    };
    match (Local.from_local_datetime(&start), Local.from_local_datetime(&mid), Local.from_local_datetime(&end)) {
        (chrono::LocalResult::Single(a), chrono::LocalResult::Single(b), chrono::LocalResult::Single(c)) => {
            let offset_secs = a.offset().local_minus_utc() as i64;
            if b.offset().local_minus_utc() as i64 == offset_secs && c.offset().local_minus_utc() as i64 == offset_secs {
                HourOffset::Fixed(offset_secs * 1000)
            } else {
                HourOffset::Transition
            }
        }
        _ => HourOffset::Transition,
    }
}

#[inline]
fn cached_hour_offset(date: NaiveDate, hours: u32) -> HourOffset {
    let key = (date.num_days_from_ce() as i64) * 24 + hours as i64;
    let slot = (key as usize) & (TZ_CACHE_SLOTS - 1);
    LOCAL_TZ_CACHE.with(|cache| {
        let mut entries = cache.get();
        if entries[slot].0 == key {
            return entries[slot].1;
        }
        let computed = compute_hour_offset(date, hours);
        entries[slot] = (key, computed);
        cache.set(entries);
        computed
    })
}

fn local_millis(date: NaiveDate, hours: u32, minutes: u32, seconds: u32, millis: u32) -> Option<f64> {
    let naive = date.and_hms_milli_opt(hours, minutes, seconds, millis)?;
    match cached_hour_offset(date, hours) {
        HourOffset::Fixed(offset_ms) => Some((naive.and_utc().timestamp_millis() - offset_ms) as f64),
        HourOffset::Transition => Local.from_local_datetime(&naive).earliest().map(|time| time.timestamp_millis() as f64),
    }
}

fn utc_millis(naive: NaiveDateTime, offset_minutes: i32) -> f64 {
    (naive.and_utc().timestamp_millis() - offset_minutes as i64 * 60_000) as f64
}

struct Cursor<'a> {
    bytes: &'a [u8],
    at: usize,
}

impl<'a> Cursor<'a> {
    fn new(text: &'a str) -> Self {
        Cursor { bytes: text.as_bytes(), at: 0 }
    }

    fn peek(&self) -> Option<u8> {
        self.bytes.get(self.at).copied()
    }

    fn eat(&mut self, byte: u8) -> bool {
        if self.peek() == Some(byte) {
            self.at += 1;
            true
        } else {
            false
        }
    }

    fn eat_any(&mut self, bytes: &[u8]) -> Option<u8> {
        let byte = self.peek().filter(|byte| bytes.contains(byte))?;
        self.at += 1;
        Some(byte)
    }

    /// Between `min` and `max` digits
    fn number(&mut self, min: usize, max: usize) -> Option<u32> {
        let end = digit_run(self.bytes, self.at);
        let length = end - self.at;
        if length < min || length > max {
            return None;
        }
        let value = self.bytes[self.at..end].iter().fold(0u32, |value, byte| value * 10 + (byte - b'0') as u32);
        self.at = end;
        Some(value)
    }

    fn spaces(&mut self) -> usize {
        let from = self.at;
        while matches!(self.peek(), Some(b' ' | b'\t')) {
            self.at += 1;
        }
        self.at - from
    }

    fn word(&mut self) -> &'a str {
        let from = self.at;
        while matches!(self.peek(), Some(byte) if byte.is_ascii_alphabetic()) {
            self.at += 1;
        }
        std::str::from_utf8(&self.bytes[from..self.at]).unwrap_or("")
    }

    fn done(&self) -> bool {
        self.at == self.bytes.len()
    }
}

struct Clock {
    hours: u32,
    minutes: u32,
    seconds: u32,
    millis: u32,
}

/// `H:mm[:ss[.fff]]` with an optional AM/PM
fn clock(cursor: &mut Cursor) -> Option<Clock> {
    let mut hours = cursor.number(1, 2)?;
    if !cursor.eat(b':') {
        return None;
    }
    let minutes = cursor.number(2, 2)?;
    let (mut seconds, mut millis) = (0, 0);
    if cursor.eat(b':') {
        seconds = cursor.number(2, 2)?;
        if matches!(cursor.peek(), Some(b'.' | b',')) && cursor.bytes.get(cursor.at + 1).is_some_and(u8::is_ascii_digit) {
            cursor.at += 1;
            let from = cursor.at;
            let end = digit_run(cursor.bytes, from);
            // Milliseconds; further digits are cut off
            for index in 0..3 {
                millis = millis * 10 + cursor.bytes.get(from + index).filter(|_| from + index < end).map_or(0, |byte| (byte - b'0') as u32);
            }
            cursor.at = end;
        }
    }
    let before_meridiem = cursor.at;
    cursor.spaces();
    let meridiem = cursor.word();
    if meridiem.eq_ignore_ascii_case("AM") && (1..=12).contains(&hours) {
        hours %= 12;
    } else if meridiem.eq_ignore_ascii_case("PM") && (1..=12).contains(&hours) {
        hours = hours % 12 + 12;
    } else {
        cursor.at = before_meridiem;
    }
    (hours < 24 && minutes < 60 && seconds < 60).then_some(Clock { hours, minutes, seconds, millis })
}

/// `Z`, `UTC`, `GMT`, `+hh:mm`, `+hhmm` or `+hh`; minutes east of UTC
fn zone(cursor: &mut Cursor) -> Option<i32> {
    let start = cursor.at;
    cursor.spaces();
    if let Some(sign) = cursor.eat_any(b"+-") {
        let digits = digit_run(cursor.bytes, cursor.at) - cursor.at;
        let parts = match digits {
            // +hhmm
            4 => cursor.number(4, 4).map(|value| (value / 100, value % 100)),
            2 => cursor.number(2, 2).map(|hours| {
                let before_minutes = cursor.at;
                let minutes = if cursor.eat(b':') { cursor.number(2, 2) } else { None };
                if minutes.is_none() {
                    cursor.at = before_minutes;
                }
                (hours, minutes.unwrap_or(0))
            }),
            _ => None,
        };
        if let Some((hours, minutes)) = parts {
            let offset = (hours * 60 + minutes) as i32;
            return Some(if sign == b'-' { -offset } else { offset });
        }
        cursor.at = start;
        return None;
    }
    let word = cursor.word();
    if word.eq_ignore_ascii_case("Z") || word.eq_ignore_ascii_case("UTC") || word.eq_ignore_ascii_case("GMT") || word.eq_ignore_ascii_case("UT") {
        Some(0)
    } else {
        cursor.at = start;
        None
    }
}

fn finish(date: NaiveDate, time: Option<Clock>, cursor: &mut Cursor, date_only_is_utc: bool) -> Option<f64> {
    let offset = zone(cursor);
    cursor.spaces();
    if !cursor.done() {
        return None;
    }
    match (time, offset) {
        (Some(time), Some(offset)) => Some(utc_millis(date.and_hms_milli_opt(time.hours, time.minutes, time.seconds, time.millis)?, offset)),
        (Some(time), None) => local_millis(date, time.hours, time.minutes, time.seconds, time.millis),
        (None, Some(offset)) => Some(utc_millis(date.and_hms_opt(0, 0, 0)?, offset)),
        (None, None) if date_only_is_utc => Some(utc_millis(date.and_hms_opt(0, 0, 0)?, 0)),
        (None, None) => local_millis(date, 0, 0, 0, 0),
    }
}

fn optional_time(cursor: &mut Cursor, separators: &[u8]) -> Option<Option<Clock>> {
    let start = cursor.at;
    let separated = cursor.eat_any(separators).is_some() | (cursor.spaces() > 0);
    if !separated || !cursor.peek().is_some_and(|byte| byte.is_ascii_digit()) {
        cursor.at = start;
        return Some(None);
    }
    clock(cursor).map(Some)
}

/// ISO 8601 and its common log variants: `2026-03-13[T ]09:14:02.105[Z|+01:00]`
fn iso_date(text: &str) -> Option<f64> {
    let mut cursor = Cursor::new(text);
    let year = cursor.number(4, 4)? as i32;
    if cursor.done() {
        return Some(utc_millis(NaiveDate::from_ymd_opt(year, 1, 1)?.and_hms_opt(0, 0, 0)?, 0));
    }
    if !cursor.eat(b'-') {
        return None;
    }
    let month = cursor.number(2, 2)?;
    let day = if cursor.eat(b'-') { cursor.number(2, 2)? } else { 1 };
    let date = NaiveDate::from_ymd_opt(year, month, day)?;
    let time = optional_time(&mut cursor, b"T")?;
    finish(date, time, &mut cursor, true)
}

/// `2026/03/13 09:14` and `3/13/2026 9:14 PM` (month first, as `Date.parse` reads it)
fn slashed_date(text: &str) -> Option<f64> {
    let mut cursor = Cursor::new(text);
    let first = cursor.number(1, 4)?;
    let first_length = cursor.at;
    let separator = cursor.eat_any(b"/.")?;
    let second = cursor.number(1, 2)?;
    if !cursor.eat(separator) {
        return None;
    }
    let third_from = cursor.at;
    let third = cursor.number(1, 4)?;
    let date = if first_length == 4 {
        NaiveDate::from_ymd_opt(first as i32, second, third)?
    } else if separator == b'/' {
        let year = if cursor.at - third_from <= 2 { 2000 + third as i32 - if third >= 50 { 100 } else { 0 } } else { third as i32 };
        NaiveDate::from_ymd_opt(year, first, second)?
    } else {
        return None;
    };
    let time = optional_time(&mut cursor, b"")?;
    finish(date, time, &mut cursor, false)
}

fn month_of(word: &str) -> Option<u32> {
    const MONTHS: [&[u8]; 12] = [b"jan", b"feb", b"mar", b"apr", b"may", b"jun", b"jul", b"aug", b"sep", b"oct", b"nov", b"dec"];
    const FULL: [&[u8]; 12] = [
        b"january",
        b"february",
        b"march",
        b"april",
        b"may",
        b"june",
        b"july",
        b"august",
        b"september",
        b"october",
        b"november",
        b"december",
    ];
    let bytes = word.as_bytes();
    if bytes.len() < 3 {
        return None;
    }
    (0..12)
        .find(|&index| {
            bytes.eq_ignore_ascii_case(MONTHS[index])
                || (bytes.len() > 3 && bytes.len() <= FULL[index].len() && FULL[index][..bytes.len()].eq_ignore_ascii_case(bytes))
        })
        .map(|index| index as u32 + 1)
}

/// Dates with a month name: `13 Mar 2026 09:14:02`, `Mar 13, 2026`, `Fri, 13 Mar 2026 09:14:02 GMT`,
/// `13/Mar/2026:09:14:02 +0100`
fn named_month_date(text: &str) -> Option<f64> {
    let mut cursor = Cursor::new(text);
    let (mut day, mut month, mut year) = (None, None, None);
    let mut time = None;
    loop {
        while cursor.eat_any(b" \t,/-").is_some() {}
        let Some(byte) = cursor.peek() else { break };
        if byte.is_ascii_alphabetic() {
            let before = cursor.at;
            let word = cursor.word();
            if month.is_none() {
                if let Some(found) = month_of(word) {
                    month = Some(found);
                    continue;
                }
            }
            const DAYS: [&[u8]; 7] = [b"mon", b"tue", b"wed", b"thu", b"fri", b"sat", b"sun"];
            if day.is_none() && month.is_none() && word.len() >= 3 && DAYS.iter().any(|name| word.as_bytes()[..3].eq_ignore_ascii_case(name)) {
                continue;
            }
            // Anything else can only be a time zone
            cursor.at = before;
            break;
        }
        if !byte.is_ascii_digit() {
            break;
        }
        let before = cursor.at;
        let end = digit_run(cursor.bytes, before);
        if cursor.bytes.get(end) == Some(&b':') && time.is_none() && (day.is_none() || year.is_some() || end - before <= 2) && end - before <= 2 {
            time = Some(clock(&mut cursor)?);
            continue;
        }
        let value = cursor.number(1, 4)?;
        if end - before == 4 && year.is_none() {
            year = Some(value as i32);
            // 13/Mar/2026:09:14:02
            if cursor.peek() == Some(b':') && time.is_none() {
                cursor.at += 1;
                time = Some(clock(&mut cursor)?);
            }
        } else if day.is_none() && end - before <= 2 {
            day = Some(value);
        } else {
            return None;
        }
    }
    let date = NaiveDate::from_ymd_opt(year?, month?, day?)?;
    finish(date, time, &mut cursor, false)
}

/// `Date.parse` for the formats found in logs
fn js_date_parse(text: &str) -> Option<f64> {
    let bytes = text.as_bytes();
    if !bytes.iter().any(u8::is_ascii_digit) {
        return None;
    }
    if bytes.len() >= 4 && bytes[..4].iter().all(u8::is_ascii_digit) && (bytes.len() == 4 || bytes[4] == b'-') {
        return iso_date(text);
    }
    if bytes[0].is_ascii_digit() && !bytes.iter().any(u8::is_ascii_alphabetic) {
        return slashed_date(text);
    }
    slashed_date(text).or_else(|| named_month_date(text))
}

/// Milliseconds since 1970 of a date
pub fn parse_date_time(raw: &str) -> Option<f64> {
    let text = raw.trim();
    if text.is_empty() {
        return None;
    }
    // Fast path for the dominant ISO 8601 shape (`YYYY-...`)
    let bytes = text.as_bytes();
    if bytes.len() >= 5 && bytes[4] == b'-' && bytes[..4].iter().all(u8::is_ascii_digit) {
        return iso_date(text);
    }
    // d.m.yyyy [h:mm[:ss]] is day first, before the US reading of Date.parse can apply
    let dotted = (|| {
        let mut cursor = Cursor::new(text);
        let day = cursor.number(1, 2)?;
        if !cursor.eat(b'.') {
            return None;
        }
        let month = cursor.number(1, 2)?;
        if !cursor.eat(b'.') {
            return None;
        }
        let year = cursor.number(4, 4)?;
        Some((day, month, year, cursor.at))
    })();
    if let Some((day, month, year, rest)) = dotted {
        let tail = &text[rest..];
        if tail.is_empty() {
            return local_millis(NaiveDate::from_ymd_opt(year as i32, month, day)?, 0, 0, 0, 0);
        }
        let mut cursor = Cursor::new(tail);
        if cursor.spaces() > 0 {
            let time = (|| {
                let hours = cursor.number(1, 2)?;
                if !cursor.eat(b':') {
                    return None;
                }
                let minutes = cursor.number(2, 2)?;
                let seconds = if cursor.eat(b':') { cursor.number(2, 2)? } else { 0 };
                cursor.done().then_some((hours, minutes, seconds))
            })();
            if let Some((hours, minutes, seconds)) = time {
                return local_millis(NaiveDate::from_ymd_opt(year as i32, month, day)?, hours, minutes, seconds, 0);
            }
        }
    }
    js_date_parse(text)
}

/// `new Date(ms).toISOString()` written into `out`
pub fn write_iso_string(out: &mut String, milliseconds: f64) {
    use chrono::Timelike;
    match chrono::DateTime::from_timestamp_millis(milliseconds as i64) {
        Some(time) => {
            let year = time.year();
            if (0..=9999).contains(&year) {
                push_u64_padded(out, year as u64, 4);
                out.push('-');
                push_two_digits(out, time.month() as u64);
                out.push('-');
                push_two_digits(out, time.day() as u64);
                out.push('T');
                push_two_digits(out, time.hour() as u64);
                out.push(':');
                push_two_digits(out, time.minute() as u64);
                out.push(':');
                push_two_digits(out, time.second() as u64);
                out.push('.');
                push_three_digits(out, (time.timestamp_subsec_millis()) as u64);
                out.push('Z');
            } else {
                let _ = write!(out, "{}", time.format("%Y-%m-%dT%H:%M:%S%.3fZ"));
            }
        }
        None => out.push_str("Invalid Date"),
    }
}

/// `new Date(ms).toISOString()`
pub fn iso_string(milliseconds: f64) -> String {
    let mut out = String::with_capacity(24);
    write_iso_string(&mut out, milliseconds);
    out
}

// --- typed values -------------------------------------------------------------------------------

/// The comparable value of a slice of a row
pub fn parse_typed(raw: &str, value_type: ValueType, for_invert_date_time: bool) -> Option<f64> {
    match value_type {
        ValueType::String => None,
        ValueType::Number => parse_number(raw),
        ValueType::TimeSpan => parse_time_span(raw),
        ValueType::DateTime if for_invert_date_time => parse_time_span(raw),
        ValueType::DateTime => parse_date_time(raw),
    }
}

/// What a selected text most likely is
pub fn infer_value_type(selected: &str) -> ValueType {
    if selected.is_empty() || selected.contains('\n') {
        return ValueType::String;
    }
    if parse_number(selected).is_some() {
        ValueType::Number
    } else if parse_date_time(selected).is_some() {
        ValueType::DateTime
    } else if parse_time_span(selected).is_some() {
        ValueType::TimeSpan
    } else {
        ValueType::String
    }
}

// --- writing diff values --------------------------------------------------------------------------

fn write_number(out: &mut String, value: f64) {
    if value.fract() == 0.0 {
        write_js_string(out, value);
    } else {
        let mut fixed = String::with_capacity(32);
        write_fixed(&mut fixed, value, 6);
        write_js_string(out, fixed.parse().unwrap_or(value));
    }
}

struct NumberSample<'a> {
    separator: Option<char>,
    fraction: Option<&'a str>,
}

/// `^[+-]?\d+(?:([.,])(\d+))?$`
fn match_number_sample(text: &str) -> Option<NumberSample<'_>> {
    let bytes = text.as_bytes();
    let mut index = usize::from(matches!(bytes.first(), Some(b'+' | b'-')));
    let digits_end = digit_run(bytes, index);
    if digits_end == index {
        return None;
    }
    index = digits_end;
    if index == bytes.len() {
        return Some(NumberSample { separator: None, fraction: None });
    }
    if bytes[index] != b'.' && bytes[index] != b',' {
        return None;
    }
    let fraction_end = digit_run(bytes, index + 1);
    if fraction_end == index + 1 || fraction_end != bytes.len() {
        return None;
    }
    Some(NumberSample { separator: Some(bytes[index] as char), fraction: Some(&text[index + 1..]) })
}

fn replace_first_dot(out: &mut String, start: usize, separator: char) {
    if separator == '.' {
        return;
    }
    if separator.is_ascii() {
        // SAFETY: replacing single ASCII byte '.' with another ASCII byte keeps valid UTF-8
        let bytes = unsafe { out.as_bytes_mut() };
        if let Some(pos) = bytes[start..].iter().position(|&b| b == b'.') {
            bytes[start + pos] = separator as u8;
        }
    } else if let Some(rel) = out[start..].find('.') {
        let pos = start + rel;
        let mut sep_buf = [0u8; 4];
        out.replace_range(pos..pos + 1, separator.encode_utf8(&mut sep_buf));
    }
}

fn write_number_like_sample(out: &mut String, value: f64, sample: &str) {
    let Some(matched) = match_number_sample(sample.trim()) else {
        write_number(out, value);
        return;
    };
    let decimals = matched.fraction.map_or(0, str::len);
    let start = out.len();
    if decimals > 0 {
        write_fixed(out, value, decimals);
    } else {
        write_js_string(out, js_round(value));
    }
    if let Some(sep) = matched.separator {
        replace_first_dot(out, start, sep);
    }
}

struct Broken {
    days: u64,
    hours: u64,
    minutes: u64,
    seconds: u64,
    millis: u64,
}

fn break_down(milliseconds: f64, step: f64) -> Broken {
    let mut rest = (js_round(milliseconds.abs() / step) * step) as u64;
    let days = rest / 86_400_000;
    rest -= days * 86_400_000;
    let hours = rest / 3_600_000;
    rest -= hours * 3_600_000;
    let minutes = rest / 60_000;
    rest -= minutes * 60_000;
    let seconds = rest / 1000;
    Broken { days, hours, minutes, seconds, millis: rest - seconds * 1000 }
}

fn rounding_step(fraction_digits: usize, has_seconds: bool) -> f64 {
    if fraction_digits > 0 {
        10f64.powi(3 - fraction_digits as i32)
    } else if has_seconds {
        1000.0
    } else {
        60_000.0
    }
}

fn write_fraction_millis(out: &mut String, millis: u64, fraction_digits: usize) {
    let buf = [b'0' + (millis / 100) as u8, b'0' + ((millis / 10) % 10) as u8, b'0' + (millis % 10) as u8];
    out.push('.');
    // SAFETY: ASCII digits
    out.push_str(unsafe { std::str::from_utf8_unchecked(&buf[..fraction_digits]) });
}

fn write_time_span_like_sample(out: &mut String, milliseconds: f64, sample: &str) {
    let Some(parts) = match_time_span(sample.trim()) else {
        write_time_span(out, milliseconds);
        return;
    };
    let has_days = parts.days.is_some();
    let has_seconds = parts.seconds.is_some();
    let fraction_digits = parts.fraction.map_or(0, str::len);
    let broken = break_down(milliseconds, rounding_step(fraction_digits, has_seconds));
    if !has_days && broken.days > 0 {
        write_time_span(out, milliseconds);
        return;
    }
    if milliseconds < 0.0 {
        out.push('-');
    }
    if has_days {
        push_u64_padded(out, broken.days, 1);
        out.push('.');
    }
    push_two_digits(out, broken.hours);
    out.push(':');
    push_two_digits(out, broken.minutes);
    if has_seconds {
        out.push(':');
        push_two_digits(out, broken.seconds);
    }
    if fraction_digits > 0 {
        write_fraction_millis(out, broken.millis, fraction_digits);
    }
}

#[inline]
fn chars(text: &str) -> usize {
    if text.is_ascii() {
        text.len()
    } else {
        text.chars().count()
    }
}

/// Pads `formatted` to the width of `slice` and writes it into `out`.
pub fn write_aligned_to_slice(out: &mut String, formatted: &str, slice: &str, value_type: ValueType) {
    let (width, template) = (chars(formatted), chars(slice));
    if template == 0 || width >= template {
        out.push_str(formatted);
        return;
    }
    let (leading, trailing) = if slice.is_ascii() {
        let bytes = slice.as_bytes();
        let leading = bytes.iter().take_while(|&&b| b.is_ascii_whitespace()).count();
        let trailing = bytes.iter().rev().take_while(|&&b| b.is_ascii_whitespace()).count();
        (leading, trailing)
    } else {
        let leading = slice.chars().take_while(|ch| ch.is_whitespace()).count();
        let trailing = slice.chars().rev().take_while(|ch| ch.is_whitespace()).count();
        (leading, trailing)
    };
    let padding = template - width;
    let left = if leading > 0 && trailing == 0 {
        padding
    } else if trailing > 0 && leading == 0 {
        0
    } else if leading > 0 || trailing > 0 {
        js_round(padding as f64 * (leading as f64 / (leading + trailing) as f64)) as usize
    } else if value_type != ValueType::String {
        padding
    } else {
        0
    };
    push_spaces(out, left);
    out.push_str(formatted);
    push_spaces(out, padding - left);
}

/// Pads the value to the width of the slice it replaces, on the side the slice had its spaces
pub fn align_to_slice(formatted: &str, slice: &str, value_type: ValueType) -> String {
    let mut out = String::with_capacity(formatted.len().max(slice.len()));
    write_aligned_to_slice(&mut out, formatted, slice, value_type);
    out
}

fn write_aligned_to_width(out: &mut String, formatted: &str, width: usize, align_right: bool) {
    let length = chars(formatted);
    if width == 0 || length >= width {
        out.push_str(formatted);
        return;
    }
    let pad = width - length;
    if align_right {
        push_spaces(out, pad);
        out.push_str(formatted);
    } else {
        out.push_str(formatted);
        push_spaces(out, pad);
    }
}

/// How the values of a Diff are written so that the whole column lines up.
#[derive(Clone, Debug, PartialEq)]
pub enum DiffProfile {
    Number { separator: char, fraction_digits: usize, width: usize },
    TimeSpan { has_days: bool, day_digits: usize, hour_digits: usize, has_seconds: bool, fraction_digits: usize, width: usize },
}

/// What one value contributes to the profile of its column
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct ProfileSample {
    matched: bool,
    /// Separator of the first value with decimals
    separator: Option<char>,
    fraction_digits: usize,
    has_days: bool,
    day_digits: usize,
    hour_digits: usize,
    has_seconds: bool,
}

impl ProfileSample {
    pub fn of(raw: &str, value_type: ValueType) -> ProfileSample {
        let text = raw.trim();
        match value_type {
            ValueType::Number => match match_number_sample(text) {
                Some(sample) => {
                    ProfileSample { matched: true, separator: sample.fraction.and(sample.separator), fraction_digits: sample.fraction.map_or(0, str::len), ..Default::default() }
                }
                None => ProfileSample::default(),
            },
            ValueType::TimeSpan => match match_time_span(text) {
                Some(parts) => ProfileSample {
                    matched: true,
                    separator: None,
                    fraction_digits: parts.fraction.map_or(0, str::len),
                    has_days: parts.days.is_some(),
                    day_digits: parts.days.map_or(0, str::len),
                    hour_digits: parts.hours.len(),
                    has_seconds: parts.seconds.is_some(),
                },
                None => ProfileSample::default(),
            },
            _ => ProfileSample::default(),
        }
    }

    /// Both samples, `self` being the earlier rows
    pub fn merge(self, later: ProfileSample) -> ProfileSample {
        ProfileSample {
            matched: self.matched || later.matched,
            separator: self.separator.or(later.separator),
            fraction_digits: self.fraction_digits.max(later.fraction_digits),
            has_days: self.has_days || later.has_days,
            day_digits: self.day_digits.max(later.day_digits),
            hour_digits: self.hour_digits.max(later.hour_digits),
            has_seconds: self.has_seconds || later.has_seconds,
        }
    }

    pub fn profile(self, value_type: ValueType, width: usize) -> Option<DiffProfile> {
        if !self.matched {
            return None;
        }
        let width = width.max(1);
        match value_type {
            ValueType::Number => Some(DiffProfile::Number { separator: self.separator.unwrap_or('.'), fraction_digits: self.fraction_digits, width }),
            ValueType::TimeSpan => Some(DiffProfile::TimeSpan {
                has_days: self.has_days,
                day_digits: self.day_digits.max(1),
                hour_digits: self.hour_digits.max(2),
                has_seconds: self.has_seconds,
                fraction_digits: self.fraction_digits,
                width,
            }),
            _ => None,
        }
    }
}

fn write_number_for_profile(out: &mut String, scratch: &mut String, value: f64, profile: Option<&DiffProfile>, slice: &str) {
    scratch.clear();
    let Some(DiffProfile::Number { separator, fraction_digits, width }) = profile else {
        write_number_like_sample(scratch, value, slice);
        write_aligned_to_width(out, scratch, (chars(slice.trim()) + 1).max(1), true);
        return;
    };
    let absolute = value.abs();
    if value < 0.0 {
        scratch.push('-');
    }
    let start = scratch.len();
    if *fraction_digits > 0 {
        write_fixed(scratch, absolute, *fraction_digits);
    } else {
        write_js_string(scratch, js_round(absolute));
    }
    replace_first_dot(scratch, start, *separator);
    write_aligned_to_width(out, scratch, *width, true);
}

fn write_time_span_for_profile(out: &mut String, scratch: &mut String, milliseconds: f64, profile: Option<&DiffProfile>, slice: &str) {
    scratch.clear();
    let Some(DiffProfile::TimeSpan { has_days, day_digits, hour_digits, has_seconds, fraction_digits, width }) = profile else {
        write_time_span_like_sample(scratch, milliseconds, slice);
        write_aligned_to_width(out, scratch, (chars(slice.trim()) + 1).max(1), true);
        return;
    };
    let broken = break_down(milliseconds, rounding_step(*fraction_digits, *has_seconds));
    if !has_days && broken.days > 0 {
        write_time_span(out, milliseconds);
        return;
    }
    if milliseconds < 0.0 {
        scratch.push('-');
    }
    if *has_days {
        push_u64_padded(scratch, broken.days, *day_digits);
        scratch.push('.');
    }
    push_u64_padded(scratch, broken.hours, *hour_digits);
    scratch.push(':');
    push_two_digits(scratch, broken.minutes);
    if *has_seconds {
        scratch.push(':');
        push_two_digits(scratch, broken.seconds);
    }
    if *fraction_digits > 0 {
        write_fraction_millis(scratch, broken.millis, *fraction_digits);
    }
    write_aligned_to_width(out, scratch, *width, true);
}

/// Writes the text of a diff value in place of `slice` into `out`, reusing `scratch` to avoid allocations.
pub fn write_diff_value(out: &mut String, scratch: &mut String, value: f64, value_type: ValueType, slice: &str, profile: Option<&DiffProfile>) {
    match value_type {
        ValueType::Number => write_number_for_profile(out, scratch, value, profile, slice),
        ValueType::TimeSpan => write_time_span_for_profile(out, scratch, value, profile, slice),
        _ => {
            scratch.clear();
            write_time_span(scratch, value);
            write_aligned_to_slice(out, scratch, slice, value_type);
        }
    }
}

/// The text of a diff value in place of `slice`
pub fn format_diff_value(value: f64, value_type: ValueType, slice: &str, profile: Option<&DiffProfile>) -> String {
    let mut out = String::with_capacity(slice.len().max(24));
    let mut scratch = String::with_capacity(24);
    write_diff_value(&mut out, &mut scratch, value, value_type, slice, profile);
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::{Datelike, Timelike};

    fn local(milliseconds: f64) -> chrono::DateTime<Local> {
        Local.timestamp_millis_opt(milliseconds as i64).unwrap()
    }

    #[test]
    fn czech_dotted_dates_are_day_month_year() {
        let date = local(parse_date_time("1.2.2026 03:04:05").unwrap());
        assert_eq!((date.year(), date.month(), date.day(), date.hour()), (2026, 2, 1, 3));
        assert_eq!(parse_date_time("2026-02-01T03:04:05Z"), Some(1769915045000.0));
    }

    #[test]
    fn invalid_calendar_dates_and_times_are_not_rolled_into_another_month() {
        for text in ["31.2.2026", "29.2.2025", "1.13.2026", "0.2.2026", "1.2.2026 25:00", "1.2.2026 12:60", "1.2.2026 12:00:60"] {
            assert_eq!(parse_date_time(text), None, "{text}");
        }
        assert!(parse_date_time("29.2.2024").is_some());
    }

    #[test]
    fn log_timestamps_are_read_in_their_usual_shapes() {
        let utc = 1773393242105.0; // 2026-03-13T09:14:02.105Z
        assert_eq!(parse_date_time("2026-03-13T09:14:02.105Z"), Some(utc));
        assert_eq!(parse_date_time("2026-03-13T10:14:02.105+01:00"), Some(utc));
        assert_eq!(parse_date_time("2026-03-13 10:14:02,105 +0100"), Some(utc));
        assert_eq!(parse_date_time("2026-03-13T09:14:02.105999Z"), Some(utc));
        assert_eq!(parse_date_time("Fri, 13 Mar 2026 09:14:02 GMT"), Some(utc - 105.0));
        assert_eq!(parse_date_time("13/Mar/2026:10:14:02 +0100"), Some(utc - 105.0));
        assert_eq!(parse_date_time("2026-03-13"), Some(1773360000000.0));
        // Without a zone the time is local, in every notation the same instant
        let local_time = parse_date_time("2026-03-13 09:14:02").unwrap();
        assert_eq!(parse_date_time("2026-03-13T09:14:02"), Some(local_time));
        assert_eq!(parse_date_time("2026/03/13 09:14:02"), Some(local_time));
        assert_eq!(parse_date_time("3/13/2026 9:14:02 AM"), Some(local_time));
        assert_eq!(parse_date_time("13.3.2026 9:14:02"), Some(local_time));
        assert_eq!(parse_date_time("13 Mar 2026 09:14:02"), Some(local_time));
        assert_eq!(parse_date_time("Mar 13, 2026 09:14:02"), Some(local_time));
        assert_eq!(parse_date_time("3/13/2026 9:14:02 PM"), Some(local_time + 12.0 * 3_600_000.0));
        for text in ["09:14:02", "hello", "2026-13-01", "2026-02-30", "12", "1.5", "ERROR 42"] {
            assert_eq!(parse_date_time(text), None, "{text}");
        }
    }

    #[test]
    fn blank_numeric_fields_stay_invalid_rather_than_becoming_zero() {
        assert_eq!(parse_number("   "), None);
        assert_eq!(parse_number("0"), Some(0.0));
        assert_eq!(parse_number("-1,25"), Some(-1.25));
        assert_eq!(parse_number(" 12 "), Some(12.0));
        assert_eq!(parse_number("0x1F"), Some(31.0));
        assert_eq!(parse_number("1e3"), Some(1000.0));
        assert_eq!(parse_number(".5"), Some(0.5));
        assert_eq!(parse_number("5."), Some(5.0));
        for text in ["Infinity", "NaN", "12abc", "1 2", "--1", "inf", "nan", "e5", "."] {
            assert_eq!(parse_number(text), None, "{text}");
        }
    }

    #[test]
    fn time_spans_validate_clock_fields_and_finite_milliseconds() {
        for text in ["01:60", "01:02:60", "1.24:00:00", "Infinity", "NaN", ""] {
            assert_eq!(parse_time_span(text), None, "{text}");
        }
        for millis in [0.0, 1.0, -1.0, 1234.0, 90061001.0, -90061001.0, 359999999.0] {
            assert_eq!(parse_time_span(&format_time_span(millis)), Some(millis), "{millis}");
        }
        assert_eq!(parse_time_span("30:00:00"), Some(108000000.0));
        assert_eq!(parse_time_span("1.02:03:04.005"), Some(93784005.0));
        assert_eq!(parse_time_span("250"), Some(250.0));
    }

    #[test]
    fn to_fixed_rounds_like_javascript() {
        assert_eq!(to_fixed(0.5, 0), "1");
        assert_eq!(to_fixed(2.5, 0), "3");
        assert_eq!(to_fixed(-2.5, 0), "-3");
        assert_eq!(to_fixed(1.005, 2), "1.00");
        assert_eq!(to_fixed(0.125, 2), "0.13");
        assert_eq!(to_fixed(9.995, 1), "10.0");
        assert_eq!(to_fixed(99.5, 0), "100");
        assert_eq!(to_fixed(1.0, 3), "1.000");
        assert_eq!(js_round(-2.5), -2.0);
        assert_eq!(js_string(-0.0), "0");
        assert_eq!(js_string(1234567.0), "1234567");
        assert_eq!(js_string(0.1), "0.1");
    }

    #[test]
    fn the_type_of_a_selection_is_guessed() {
        assert_eq!(infer_value_type("95.6"), ValueType::Number);
        assert_eq!(infer_value_type("2026-03-13T09:14:02.105Z"), ValueType::DateTime);
        assert_eq!(infer_value_type("00:00:00.143"), ValueType::TimeSpan);
        assert_eq!(infer_value_type("START"), ValueType::String);
        assert_eq!(infer_value_type("1\n2"), ValueType::String);
    }

    #[test]
    fn diff_values_line_up_with_the_column() {
        let profile = [" 95.6", "  2.8", "100"]
            .iter()
            .map(|raw| ProfileSample::of(raw, ValueType::Number))
            .fold(ProfileSample::default(), ProfileSample::merge)
            .profile(ValueType::Number, 5);
        assert_eq!(profile, Some(DiffProfile::Number { separator: '.', fraction_digits: 1, width: 5 }));
        assert_eq!(format_diff_value(-0.4, ValueType::Number, "95.2", profile.as_ref()), " -0.4");
        assert_eq!(format_diff_value(143.0, ValueType::TimeSpan, "2026-03-13T09:14:02.248Z", None), "             00:00:00.143");
        assert_eq!(format_diff_value(61_000.0, ValueType::TimeSpan, "00:01", None), " 00:01");
        assert_eq!(align_to_slice("7", "  42", ValueType::Number), "   7");
        assert_eq!(align_to_slice("7", "42  ", ValueType::Number), "7   ");
        assert_eq!(align_to_slice("ab", "abcd", ValueType::String), "ab  ");
    }

    #[test]
    fn cached_local_millis_matches_chrono_across_the_whole_year_including_dst() {
        let mut date = NaiveDate::from_ymd_opt(2025, 1, 1).unwrap();
        let end = NaiveDate::from_ymd_opt(2026, 12, 31).unwrap();
        while date <= end {
            for hour in [0, 1, 2, 3, 12, 23] {
                for &(minute, second, millis) in &[(0, 0, 0), (15, 30, 500), (59, 59, 999)] {
                    let naive = date.and_hms_milli_opt(hour, minute, second, millis).unwrap();
                    let expected = Local.from_local_datetime(&naive).earliest().map(|t| t.timestamp_millis() as f64);
                    let actual = local_millis(date, hour, minute, second, millis);
                    assert_eq!(actual, expected, "{date} {hour:02}:{minute:02}:{second:02}.{millis:03}");
                }
            }
            date = date.succ_opt().unwrap();
        }
    }
}
