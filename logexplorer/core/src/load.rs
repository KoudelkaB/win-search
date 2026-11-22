//! Loading sources: reading, decoding to UTF-8, the line index, and combining several files.
//!
//! A UTF-8 file without tabs is used exactly as it was read - one allocation of the file's
//! size plus 4 bytes a line. Only tabs (expanded to four spaces), UTF-16 and
//! invalid UTF-8 need a converted copy, and then the bytes read are released.

use crate::cancel::{Cancel, OpError, OpResult};
use crate::stage::{RowIds, SourceDesc, SrcIds, Stage, Starts, NO_SRC};
use crate::text::{char_len, Buf, BufRef, Span};
use rayon::prelude::*;
use std::io::Read;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

pub const TAB_REPLACEMENT: &str = "    ";
const SCAN_CHUNK: usize = 1 << 20;

/// Bytes read so far and in total, for a progress display.
#[derive(Default)]
pub struct LoadProgress {
    pub done: AtomicU64,
    pub total: AtomicU64,
}

/// A decoded source ready to be shown or combined with others.
pub struct TextSource {
    pub name: String,
    pub path: String,
    pub encoding: String,
    pub stage: Stage,
    /// Characters of the text with one `\n` between rows
    pub chars: u64,
}

pub struct Detected {
    pub encoding: &'static str,
    pub offset: usize,
}

pub fn detect_encoding(bytes: &[u8]) -> Detected {
    if bytes.starts_with(&[0xef, 0xbb, 0xbf]) {
        return Detected { encoding: "utf-8-bom", offset: 3 };
    }
    if bytes.starts_with(&[0xff, 0xfe]) {
        return Detected { encoding: "utf-16le", offset: 2 };
    }
    if bytes.starts_with(&[0xfe, 0xff]) {
        return Detected { encoding: "utf-16be", offset: 2 };
    }
    let sample = bytes.len().min(4000);
    let (mut even_zeroes, mut odd_zeroes) = (0usize, 0usize);
    for (index, &byte) in bytes[..sample].iter().enumerate() {
        if byte == 0 {
            if index % 2 == 0 {
                even_zeroes += 1;
            } else {
                odd_zeroes += 1;
            }
        }
    }
    let sample = sample as f64;
    if even_zeroes as f64 > sample * 0.15 && (odd_zeroes as f64) < sample * 0.02 {
        return Detected { encoding: "utf-16be", offset: 0 };
    }
    if odd_zeroes as f64 > sample * 0.15 && (even_zeroes as f64) < sample * 0.02 {
        return Detected { encoding: "utf-16le", offset: 0 };
    }
    Detected { encoding: "utf-8", offset: 0 }
}

/// Valid UTF-8, checked in parallel
fn is_utf8(bytes: &[u8]) -> bool {
    if bytes.len() < 2 * SCAN_CHUNK {
        return simdutf8::basic::from_utf8(bytes).is_ok();
    }
    // Chunks start at character boundaries so that each can be checked on its own
    let mut cuts = vec![0];
    let mut at = SCAN_CHUNK;
    while at < bytes.len() {
        while at < bytes.len() && (bytes[at] & 0xC0) == 0x80 {
            at += 1;
        }
        cuts.push(at);
        at += SCAN_CHUNK;
    }
    cuts.push(bytes.len());
    cuts.par_windows(2).all(|pair| simdutf8::basic::from_utf8(&bytes[pair[0]..pair[1]]).is_ok())
}

fn count_tabs(bytes: &[u8]) -> usize {
    bytes.par_chunks(SCAN_CHUNK).map(|chunk| memchr::memchr_iter(b'\t', chunk).count()).sum()
}

/// The text with every tab as four spaces
fn expand_tabs(bytes: &[u8], tabs: usize) -> Vec<u8> {
    let extra = TAB_REPLACEMENT.len() - 1;
    let mut out = vec![0u8; bytes.len() + tabs * extra];
    // Output ranges of the chunks, then every chunk is expanded on its own
    let mut targets = Vec::new();
    let mut rest: &mut [u8] = &mut out;
    for chunk in bytes.chunks(SCAN_CHUNK) {
        let size = chunk.len() + memchr::memchr_iter(b'\t', chunk).count() * extra;
        let (head, tail) = rest.split_at_mut(size);
        targets.push(head);
        rest = tail;
    }
    bytes.par_chunks(SCAN_CHUNK).zip(targets.into_par_iter()).for_each(|(chunk, target)| {
        let mut written = 0;
        let mut copied = 0;
        for tab in memchr::memchr_iter(b'\t', chunk) {
            let plain = &chunk[copied..tab];
            target[written..written + plain.len()].copy_from_slice(plain);
            written += plain.len();
            target[written..written + TAB_REPLACEMENT.len()].copy_from_slice(TAB_REPLACEMENT.as_bytes());
            written += TAB_REPLACEMENT.len();
            copied = tab + 1;
        }
        let plain = &chunk[copied..];
        target[written..written + plain.len()].copy_from_slice(plain);
    });
    out
}

/// The UTF-8 text of a file's bytes: `(text, offset of its first byte, encoding name)`.
pub fn decode_bytes(bytes: Vec<u8>) -> (Vec<u8>, usize, &'static str) {
    let detected = detect_encoding(&bytes);
    let (text, offset) = match detected.encoding {
        "utf-16le" | "utf-16be" => {
            let encoding = if detected.encoding == "utf-16le" { encoding_rs::UTF_16LE } else { encoding_rs::UTF_16BE };
            let (decoded, _) = encoding.decode_without_bom_handling(&bytes[detected.offset..]);
            let decoded = decoded.into_owned().into_bytes();
            drop(bytes);
            (decoded, 0)
        }
        _ => {
            if is_utf8(&bytes[detected.offset..]) {
                (bytes, detected.offset)
            } else {
                // Like a browser's TextDecoder: invalid sequences become U+FFFD
                let decoded = String::from_utf8_lossy(&bytes[detected.offset..]).into_owned().into_bytes();
                drop(bytes);
                (decoded, 0)
            }
        }
    };
    let tabs = count_tabs(&text[offset..]);
    if tabs == 0 {
        return (text, offset, detected.encoding);
    }
    let expanded = expand_tabs(&text[offset..], tabs);
    (expanded, 0, detected.encoding)
}

struct LineScan {
    starts: Starts,
    max_cols: u64,
    chars: u64,
}

/// Starts of the lines of `bytes[start..]`. A line ends with `\n`, `\r\n` or a lone `\r`.
fn index_lines(bytes: &[u8], start: usize, ascii: bool) -> OpResult<LineScan> {
    let region = &bytes[start..];
    let total = bytes.len();
    // Line starts of each chunk, relative to the chunk
    let chunks: Vec<Vec<u32>> = region
        .par_chunks(SCAN_CHUNK)
        .enumerate()
        .map(|(chunk_index, chunk)| {
            let base = start + chunk_index * SCAN_CHUNK;
            let mut starts = Vec::new();
            for position in memchr::memchr2_iter(b'\n', b'\r', chunk) {
                // The \r of \r\n is not a line end of its own: the \n after it is
                if chunk[position] == b'\r' && base + position + 1 < total && bytes[base + position + 1] == b'\n' {
                    continue;
                }
                starts.push(position as u32 + 1);
            }
            starts
        })
        .collect();
    let lines = 1 + chunks.iter().map(Vec::len).sum::<usize>();
    if lines > u32::MAX as usize - 1 {
        return Err(OpError::msg("The source has too many lines."));
    }
    let starts = if total <= u32::MAX as usize {
        let mut all = Vec::with_capacity(lines);
        all.push(start as u32);
        for (chunk_index, chunk) in chunks.iter().enumerate() {
            let base = (start + chunk_index * SCAN_CHUNK) as u32;
            all.extend(chunk.iter().map(|&offset| base + offset));
        }
        Starts::U32(all)
    } else {
        let mut all = Vec::with_capacity(lines);
        all.push(start as u64);
        for (chunk_index, chunk) in chunks.iter().enumerate() {
            let base = (start + chunk_index * SCAN_CHUNK) as u64;
            all.extend(chunk.iter().map(|&offset| base + offset as u64));
        }
        Starts::U64(all)
    };
    drop(chunks);
    let index = crate::stage::LineIndex { buf: 0, starts, end: total as u64 };
    let (max_cols, chars, longest_bytes) = (0..lines)
        .into_par_iter()
        .with_min_len(1 << 14)
        .map(|row| {
            let (line_start, line_end) = index.bounds(row, bytes);
            let cols = if ascii { line_end - line_start } else { char_len(&bytes[line_start..line_end]) } as u64;
            (cols, cols, (line_end - line_start) as u64)
        })
        .reduce(|| (0, 0, 0), |left, right| (left.0.max(right.0), left.1 + right.1, left.2.max(right.2)));
    if longest_bytes > u32::MAX as u64 {
        return Err(OpError::msg("A line of the source is longer than 4 GiB."));
    }
    Ok(LineScan { starts: index.starts, max_cols, chars: chars + lines as u64 - 1 })
}

fn source_from_decoded(text: Vec<u8>, offset: usize, name: &str, path: &str, encoding: &str) -> OpResult<TextSource> {
    let mut text = text;
    text.shrink_to_fit();
    // A byte order mark is skipped by the rows, so it must not make the buffer "not ASCII"
    let ascii = crate::text::is_ascii_par(&text[offset..]);
    let buf: BufRef = Buf::with_ascii(text, ascii);
    let scan = index_lines(buf.bytes(), offset, buf.ascii)?;
    let end = buf.len() as u64;
    let stage = Stage::from_lines(buf, scan.starts, end, scan.max_cols, SourceDesc::new(name, path, 0));
    Ok(TextSource {
        name: if name.is_empty() { "Clipboard".to_string() } else { name.to_string() },
        path: if path.is_empty() { name.to_string() } else { path.to_string() },
        encoding: encoding.to_string(),
        stage,
        chars: scan.chars,
    })
}

/// A source from the bytes of a file.
pub fn source_from_bytes(bytes: Vec<u8>, name: &str, path: &str) -> OpResult<TextSource> {
    let (text, offset, encoding) = decode_bytes(bytes);
    source_from_decoded(text, offset, name, path, encoding)
}

/// A source from text that is already a string (the clipboard).
pub fn source_from_text(text: String, name: &str, path: &str) -> OpResult<TextSource> {
    let bytes = text.into_bytes();
    let tabs = count_tabs(&bytes);
    let bytes = if tabs == 0 { bytes } else { expand_tabs(&bytes, tabs) };
    source_from_decoded(bytes, 0, name, path, "clipboard")
}

/// The raw stage of a text; for tests and small inputs.
pub fn stage_from_text(text: String, name: &str, path: &str) -> Stage {
    source_from_text(text, name, path).expect("text fits a stage").stage
}

fn read_file(path: &Path, progress: &LoadProgress, cancel: &Cancel) -> OpResult<Vec<u8>> {
    let io_error = |error: std::io::Error| OpError::Msg(error.to_string());
    let mut file = std::fs::File::open(path).map_err(io_error)?;
    let length = file.metadata().map_err(io_error)?.len() as usize;
    let mut data = Vec::new();
    data.try_reserve_exact(length + 1).map_err(|_| OpError::msg("Not enough memory to load the file."))?;
    loop {
        cancel.check()?;
        let read = (&mut file).take(16 << 20).read_to_end(&mut data).map_err(io_error)?;
        if read == 0 {
            break;
        }
        progress.done.fetch_add(read as u64, Ordering::Relaxed);
    }
    Ok(data)
}

/// Reads and decodes one file for the viewer.
pub fn read_source(path: &Path, progress: &LoadProgress, cancel: &Cancel) -> OpResult<TextSource> {
    let bytes = read_file(path, progress, cancel)?;
    let name = path.file_name().map(|name| name.to_string_lossy().into_owned()).unwrap_or_default();
    cancel.check()?;
    source_from_bytes(bytes, &name, &path.to_string_lossy())
}

/// Total size of the files, for the progress display
pub fn total_size(paths: &[std::path::PathBuf]) -> u64 {
    paths.iter().filter_map(|path| std::fs::metadata(path).ok()).map(|meta| meta.len()).sum()
}

// --- combining sources ------------------------------------------------------------------------

/// A set of rows to combine with others; `fallback_src` tints the empty rows Zip adds.
pub struct Bundle<'a> {
    pub stage: &'a Stage,
    pub fallback_src: u16,
}

impl<'a> Bundle<'a> {
    pub fn of(stage: &'a Stage) -> Self {
        Bundle { stage, fallback_src: if stage.sources.len() == 1 { 0 } else { NO_SRC } }
    }
}

struct Prepared {
    bufs: Vec<BufRef>,
    sources: Vec<SourceDesc>,
    /// Per bundle: what to add to its buffer and source ids
    offsets: Vec<(u16, u16)>,
}

fn prepare(bundles: &[Bundle]) -> OpResult<Prepared> {
    let mut bufs = Vec::new();
    let mut sources = Vec::new();
    let mut offsets = Vec::new();
    for bundle in bundles {
        offsets.push((bufs.len() as u16, sources.len() as u16));
        bufs.extend(bundle.stage.bufs.iter().cloned());
        for source in bundle.stage.sources.iter() {
            let tone = sources.len();
            sources.push(SourceDesc { tone: tone % crate::stage::SOURCE_TONES, ..source.clone() });
        }
    }
    if bufs.len() >= crate::text::ARENA as usize - 64 || sources.len() >= NO_SRC as usize {
        return Err(OpError::msg("Too many files to combine."));
    }
    if bufs.is_empty() {
        bufs.push(Buf::new(Vec::new()));
    }
    Ok(Prepared { bufs, sources, offsets })
}

fn remap(span: Span, buf_offset: u16) -> Span {
    span.with_buf(span.buf() + buf_offset)
}

fn remap_src(src: u16, src_offset: u16) -> u16 {
    if src == NO_SRC {
        NO_SRC
    } else {
        src + src_offset
    }
}

fn combined(prepared: Prepared, spans: Vec<Span>, src: Vec<u16>) -> Stage {
    let src = if prepared.sources.len() <= 1 { SrcIds::Uniform(0) } else { SrcIds::PerRow(Arc::new(src)) };
    Stage::from_spans("Raw", prepared.bufs, spans, src, RowIds::Identity, Arc::new(prepared.sources))
}

/// The bundles one after another.
pub fn merge_append(bundles: &[Bundle]) -> OpResult<Stage> {
    let prepared = prepare(bundles)?;
    let total: usize = bundles.iter().map(|bundle| bundle.stage.len()).sum();
    if total > u32::MAX as usize - 1 {
        return Err(OpError::msg("The sources have too many lines."));
    }
    let mut spans = Vec::with_capacity(total);
    let mut src = Vec::with_capacity(total);
    for (bundle, &(buf_offset, src_offset)) in bundles.iter().zip(&prepared.offsets) {
        let stage = bundle.stage;
        spans.par_extend((0..stage.len()).into_par_iter().with_min_len(1 << 14).map(|row| remap(stage.span(row), buf_offset)));
        src.par_extend((0..stage.len()).into_par_iter().with_min_len(1 << 14).map(|row| remap_src(stage.src(row), src_offset)));
    }
    Ok(combined(prepared, spans, src))
}

/// The bundles interleaved row by row; a bundle with no more rows gives empty rows.
pub fn merge_zip(bundles: &[Bundle]) -> OpResult<Stage> {
    let prepared = prepare(bundles)?;
    let longest = bundles.iter().map(|bundle| bundle.stage.len()).max().unwrap_or(0);
    let total = longest * bundles.len();
    if total > u32::MAX as usize - 1 {
        return Err(OpError::msg("The sources have too many lines."));
    }
    let width = bundles.len();
    let offsets = &prepared.offsets;
    let rows: Vec<(Span, u16)> = (0..total)
        .into_par_iter()
        .with_min_len(1 << 14)
        .map(|index| {
            let (row, which) = (index / width, index % width);
            let stage = bundles[which].stage;
            let (buf_offset, src_offset) = offsets[which];
            if row < stage.len() {
                (remap(stage.span(row), buf_offset), remap_src(stage.src(row), src_offset))
            } else {
                (Span::EMPTY, remap_src(bundles[which].fallback_src, src_offset))
            }
        })
        .collect();
    let (spans, src): (Vec<Span>, Vec<u16>) = rows.into_par_iter().unzip();
    Ok(combined(prepared, spans, src))
}

/// The rows of each source interleaved (after files were loaded one after another).
pub fn rezip_by_source(stage: &Stage) -> OpResult<Stage> {
    let count = stage.sources.len();
    let mut per_source: Vec<Vec<Span>> = vec![Vec::new(); count];
    for row in 0..stage.len() {
        if let Some(rows) = per_source.get_mut(stage.src(row) as usize) {
            rows.push(stage.span(row));
        }
    }
    let longest = per_source.iter().map(Vec::len).max().unwrap_or(0);
    let mut spans = Vec::with_capacity(longest * count);
    let mut src = Vec::with_capacity(longest * count);
    for row in 0..longest {
        for (source, rows) in per_source.iter().enumerate() {
            spans.push(rows.get(row).copied().unwrap_or(Span::EMPTY));
            src.push(source as u16);
        }
    }
    let src = if count <= 1 { SrcIds::Uniform(0) } else { SrcIds::PerRow(Arc::new(src)) };
    Ok(Stage::from_spans("Raw", stage.bufs.clone(), spans, src, RowIds::Identity, stage.sources.clone()))
}

/// The same rows as one source called `name`.
pub fn flatten_sources(stage: &Stage, name: &str) -> Stage {
    let spans: Vec<Span> = (0..stage.len()).into_par_iter().with_min_len(1 << 14).map(|row| stage.span(row)).collect();
    let source = SourceDesc::new(name, name, 0);
    Stage::from_spans("Raw", stage.bufs.clone(), spans, SrcIds::Uniform(0), RowIds::Identity, Arc::new(vec![source]))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stage::testing::*;

    fn text(bytes: &[u8]) -> (Vec<String>, String) {
        let source = source_from_bytes(bytes.to_vec(), "a.log", "/logs/a.log").unwrap();
        (lines_of(&source.stage), source.encoding)
    }

    #[test]
    fn every_line_ending_splits_rows_and_tabs_become_four_spaces() {
        let (lines, encoding) = text(b"one\r\ntwo\rthree\n\tfour\n");
        assert_eq!(lines, ["one", "two", "three", "    four", ""]);
        assert_eq!(encoding, "utf-8");
    }

    #[test]
    fn boms_and_utf16_are_decoded() {
        assert_eq!(text(b"\xef\xbb\xbfab\nc"), (vec!["ab".to_string(), "c".to_string()], "utf-8-bom".to_string()));
        assert_eq!(text(b"\xff\xfea\x00\n\x00b\x00").0, ["a", "b"]);
        assert_eq!(text(b"\xfe\xff\x00a\x00\n\x00b").0, ["a", "b"]);
        // No BOM: the zero bytes tell the byte order
        let plain: Vec<u8> = "řádek 1\nřádek 2".encode_utf16().flat_map(|unit| unit.to_le_bytes()).collect();
        assert_eq!(text(&plain), (vec!["řádek 1".to_string(), "řádek 2".to_string()], "utf-16le".to_string()));
    }

    #[test]
    fn invalid_utf8_is_replaced_not_refused() {
        assert_eq!(text(b"a\xffb").0, ["a\u{fffd}b"]);
    }

    #[test]
    fn the_longest_row_counts_characters() {
        let source = source_from_text("ab\nžluťoučký\nc".to_string(), "x", "x").unwrap();
        assert_eq!(source.stage.max_cols, 9);
        assert_eq!(source.chars, 2 + 9 + 1 + 2);
    }

    #[test]
    fn a_large_text_is_indexed_across_chunk_borders() {
        let line = "0123456789abcdef\r\n";
        let text = line.repeat(200_000);
        let source = source_from_bytes(text.into_bytes(), "big", "big").unwrap();
        assert_eq!(source.stage.len(), 200_001);
        assert!((0..200_000).all(|row| source.stage.line(row) == "0123456789abcdef"));
        assert_eq!(source.stage.line(200_000), "");
    }

    #[test]
    fn append_keeps_the_files_apart_by_source() {
        let (a, b) = (stage_of(&["a1", "a2"]), stage_of(&["b1"]));
        let merged = merge_append(&[Bundle::of(&a), Bundle::of(&b)]).unwrap();
        assert_eq!(lines_of(&merged), ["a1", "a2", "b1"]);
        assert_eq!(srcs_of(&merged), [0, 0, 1]);
        assert_eq!(merged.sources.iter().map(|source| source.tone).collect::<Vec<_>>(), [0, 1]);
    }

    #[test]
    fn zip_interleaves_and_pads_the_shorter_file() {
        let (a, b) = (stage_of(&["a1", "a2", "a3"]), stage_of(&["b1"]));
        let merged = merge_zip(&[Bundle::of(&a), Bundle::of(&b)]).unwrap();
        assert_eq!(lines_of(&merged), ["a1", "b1", "a2", "", "a3", ""]);
        assert_eq!(srcs_of(&merged), [0, 1, 0, 1, 0, 1]);
    }

    #[test]
    fn concatenated_files_can_be_zipped_afterwards_and_merged_into_one() {
        let (a, b) = (stage_of(&["a1", "a2"]), stage_of(&["b1", "b2"]));
        let appended = merge_append(&[Bundle::of(&a), Bundle::of(&b)]).unwrap();
        let zipped = rezip_by_source(&appended).unwrap();
        assert_eq!(lines_of(&zipped), ["a1", "b1", "a2", "b2"]);
        let flat = flatten_sources(&zipped, "both");
        assert_eq!(srcs_of(&flat), [0, 0, 0, 0]);
        assert_eq!(flat.sources.len(), 1);
    }

    /// Windows refuses such a path to programs that do not ask for long paths
    #[test]
    fn a_file_under_a_path_longer_than_260_characters_is_read() {
        let dir = tempfile::tempdir().unwrap();
        let mut folder = dir.path().to_path_buf();
        while folder.as_os_str().len() < 300 {
            folder.push("a folder with a long name ž".repeat(2));
        }
        std::fs::create_dir_all(&folder).unwrap();
        let path = folder.join("long path.log");
        std::fs::write(&path, "one\r\ntwo").unwrap();
        assert_eq!(total_size(std::slice::from_ref(&path)), 8);
        let source = read_source(&path, &LoadProgress::default(), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&source.stage), ["one", "two"]);
        assert_eq!(source.stage.sources[0].label, "long path.log");
    }
}
