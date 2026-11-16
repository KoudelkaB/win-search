//! Text storage: immutable UTF-8 buffers and 12-byte spans into them.
//!
//! Every row of every stage is a [`Span`] into a shared buffer. Filter, Sort, Unique and
//! Extract only produce new spans; Replace, Diff and Join write just the rows they change
//! into a new buffer. The text of a file therefore exists once, however long the pipeline.

use std::sync::Arc;

/// An immutable block of valid UTF-8 without tabs (a loaded file or the text an operation wrote).
pub struct Buf {
    data: Vec<u8>,
    /// Bytes equal columns everywhere in this buffer
    pub ascii: bool,
}

pub type BufRef = Arc<Buf>;

impl Buf {
    /// `data` must be valid UTF-8
    pub fn new(data: Vec<u8>) -> BufRef {
        debug_assert!(std::str::from_utf8(&data).is_ok());
        let ascii = is_ascii_par(&data);
        Arc::new(Buf { data, ascii })
    }

    pub fn with_ascii(data: Vec<u8>, ascii: bool) -> BufRef {
        debug_assert!(std::str::from_utf8(&data).is_ok());
        Arc::new(Buf { data, ascii })
    }

    #[inline]
    pub fn bytes(&self) -> &[u8] {
        &self.data
    }

    pub fn len(&self) -> usize {
        self.data.len()
    }

    pub fn is_empty(&self) -> bool {
        self.data.is_empty()
    }

    /// Bytes this buffer keeps allocated
    pub fn capacity(&self) -> usize {
        self.data.capacity()
    }
}

pub fn is_ascii_par(data: &[u8]) -> bool {
    use rayon::prelude::*;
    if data.len() < (1 << 20) {
        return data.is_ascii();
    }
    data.par_chunks(1 << 20).all(|chunk| chunk.is_ascii())
}

/// Buffer index of text an operation is still writing; replaced when the stage is finished.
pub const ARENA: u16 = u16::MAX;

/// A row: `len` bytes at `off` of buffer `buf`. Offsets reach 2^48, a row 4 GiB.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
#[repr(C)]
pub struct Span {
    off_lo: u32,
    len: u32,
    buf: u16,
    off_hi: u16,
}

impl Span {
    pub const EMPTY: Span = Span { off_lo: 0, len: 0, buf: 0, off_hi: 0 };

    #[inline]
    pub fn new(buf: u16, off: u64, len: u32) -> Span {
        debug_assert!(off < (1 << 48));
        Span { off_lo: off as u32, len, buf, off_hi: (off >> 32) as u16 }
    }

    #[inline]
    pub fn buf(&self) -> u16 {
        self.buf
    }

    #[inline]
    pub fn off(&self) -> u64 {
        self.off_lo as u64 | (self.off_hi as u64) << 32
    }

    #[inline]
    pub fn len(&self) -> u32 {
        self.len
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.len == 0
    }

    /// Bytes `start..end` of this span
    #[inline]
    pub fn sub(&self, start: usize, end: usize) -> Span {
        debug_assert!(start <= end && end <= self.len as usize);
        Span::new(self.buf, self.off() + start as u64, (end - start) as u32)
    }

    #[inline]
    pub fn with_buf(&self, buf: u16) -> Span {
        Span { buf, ..*self }
    }

    #[inline]
    pub fn shifted(&self, by: u64) -> Span {
        Span::new(self.buf, self.off() + by, self.len)
    }
}

/// The text of a line as `&str`. Buffers hold valid UTF-8 and spans are only ever cut at
/// character boundaries, so no check is needed.
#[inline]
pub fn as_str(bytes: &[u8]) -> &str {
    debug_assert!(std::str::from_utf8(bytes).is_ok());
    // SAFETY: see above
    unsafe { std::str::from_utf8_unchecked(bytes) }
}

// --- columns -------------------------------------------------------------------------------
// A column is one Unicode character. In ASCII text - nearly every log - it equals the byte.

#[inline]
pub fn char_len(bytes: &[u8]) -> usize {
    if bytes.is_ascii() {
        bytes.len()
    } else {
        bytes.iter().filter(|&&b| (b & 0xC0) != 0x80).count()
    }
}

/// Byte offset of column `col`, the length of the line when it is shorter
#[inline]
pub fn col_to_byte(line: &str, col: usize) -> usize {
    if col >= line.len() {
        return line.len();
    }
    if line.is_ascii() {
        return col;
    }
    line.char_indices().nth(col).map_or(line.len(), |(index, _)| index)
}

#[inline]
pub fn byte_to_col(line: &str, byte: usize) -> usize {
    let byte = byte.min(line.len());
    if line.is_ascii() {
        byte
    } else {
        char_len(&line.as_bytes()[..byte])
    }
}

/// The characters `start_col..end_col` of the line (JS `line.slice(start, end)`)
#[inline]
pub fn slice_cols(line: &str, start_col: usize, end_col: usize) -> &str {
    let (start, end) = cols_to_bytes(line, start_col, end_col);
    &line[start..end]
}

#[inline]
pub fn cols_to_bytes(line: &str, start_col: usize, end_col: usize) -> (usize, usize) {
    if end_col <= start_col {
        let at = col_to_byte(line, start_col);
        return (at, at);
    }
    if line.is_ascii() {
        return (start_col.min(line.len()), end_col.min(line.len()));
    }
    let mut indices = line.char_indices();
    let start = indices.nth(start_col).map_or(line.len(), |(index, _)| index);
    if start == line.len() {
        return (start, start);
    }
    // nth() consumed the start character
    let end = indices.nth(end_col - start_col - 1).map_or(line.len(), |(index, _)| index);
    (start, end)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_span_is_twelve_bytes_and_keeps_large_offsets() {
        assert_eq!(std::mem::size_of::<Span>(), 12);
        let span = Span::new(7, (1 << 40) + 5, 9);
        assert_eq!((span.buf(), span.off(), span.len()), (7, (1 << 40) + 5, 9));
        let sub = span.sub(2, 6);
        assert_eq!((sub.off(), sub.len()), ((1 << 40) + 7, 4));
    }

    #[test]
    fn columns_count_characters_not_bytes() {
        let line = "žluť=1";
        assert_eq!(char_len(line.as_bytes()), 6);
        assert_eq!(slice_cols(line, 1, 4), "luť");
        assert_eq!(slice_cols(line, 4, 99), "=1");
        assert_eq!(slice_cols(line, 9, 12), "");
        assert_eq!(col_to_byte(line, 4), line.find('=').unwrap());
        assert_eq!(byte_to_col(line, line.find('=').unwrap()), 4);
        assert_eq!(slice_cols("abcdef", 2, 4), "cd");
        assert_eq!(slice_cols("abcdef", 4, 2), "");
    }
}
