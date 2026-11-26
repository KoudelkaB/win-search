//! Loading sources: reading, decoding to UTF-8, the line index, and combining several files.
//!
//! A UTF-8 file without tabs is used exactly as it was read - one allocation of the file's
//! size plus sparse checkpoints (every 64 lines). Only tabs (expanded to four spaces), UTF-16
//! and invalid UTF-8 need a converted copy, and then the bytes read are released.

use crate::cancel::{Cancel, OpError, OpResult};
use crate::ops::par_chunks;
use crate::stage::{EolKind, LineIndex, RowIds, SourceDesc, SrcIds, Stage, Starts, LINE_INDEX_STRIDE, NO_SRC};
use crate::text::{char_len, Buf, BufRef, Span};
use rayon::prelude::*;
use std::io::Read;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

pub const TAB_REPLACEMENT: &str = "    ";
const SCAN_CHUNK: usize = 1 << 20;
const BATCH_CHUNKS: usize = 64;
const DENSE_THRESHOLD: usize = 65_536;
const SHRINK_SLACK: usize = 1 << 20;

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

/// Splits `bytes[offset..]` into ~1 MiB chunks on UTF-8 boundaries without ever splitting `\r\n`.
fn chunk_ranges_for(bytes: &[u8], offset: usize) -> Vec<(usize, usize)> {
    let end = bytes.len();
    if offset >= end {
        return Vec::new();
    }
    if end - offset < 2 * SCAN_CHUNK {
        return vec![(offset, end)];
    }
    let mut ranges = Vec::with_capacity((end - offset) / SCAN_CHUNK + 1);
    let mut start = offset;
    while start < end {
        let mut cut = (start + SCAN_CHUNK).min(end);
        while cut < end && ((bytes[cut] & 0xC0) == 0x80 || (bytes[cut] == b'\n' && bytes[cut - 1] == b'\r')) {
            cut += 1;
        }
        ranges.push((start, cut));
        start = cut;
    }
    ranges
}

/// Valid UTF-8, checked in parallel for large buffers
fn is_utf8(bytes: &[u8]) -> bool {
    let ranges = chunk_ranges_for(bytes, 0);
    if ranges.len() <= 1 {
        return bytes.is_ascii() || simdutf8::basic::from_utf8(bytes).is_ok();
    }
    ranges.par_iter().all(|&(start, end)| {
        let chunk = &bytes[start..end];
        chunk.is_ascii() || simdutf8::basic::from_utf8(chunk).is_ok()
    })
}

/// The text of `ranges` inside `bytes` with every tab expanded to four spaces, using precomputed per-chunk tab counts.
fn expand_tabs_with_counts(bytes: &[u8], ranges: &[(usize, usize)], chunk_tabs: &[usize], total_tabs: usize, parallel: bool) -> Vec<u8> {
    let extra = TAB_REPLACEMENT.len() - 1;
    let total_in: usize = ranges.iter().map(|&(start, end)| end - start).sum();
    let mut out = vec![0u8; total_in + total_tabs * extra];
    let mut targets = Vec::with_capacity(ranges.len());
    let mut rest: &mut [u8] = &mut out;
    for (&(start, end), &tabs) in ranges.iter().zip(chunk_tabs) {
        let size = (end - start) + tabs * extra;
        let (head, tail) = rest.split_at_mut(size);
        targets.push(head);
        rest = tail;
    }
    let expand_one = |(&(start, end), &tabs, target): (&(usize, usize), &usize, &mut [u8])| {
        let chunk = &bytes[start..end];
        if tabs == 0 {
            target.copy_from_slice(chunk);
            return;
        }
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
    };
    if parallel {
        ranges
            .par_iter()
            .zip(chunk_tabs.par_iter())
            .zip(targets.into_par_iter())
            .for_each(|((range, tabs), target)| expand_one((range, tabs, target)));
    } else {
        for ((range, tabs), target) in ranges.iter().zip(chunk_tabs.iter()).zip(targets) {
            expand_one((range, tabs, target));
        }
    }
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
    let ranges = chunk_ranges_for(&text, offset);
    if ranges.is_empty() {
        return (text, offset, detected.encoding);
    }
    let parallel = ranges.len() > 1;
    let chunk_tabs: Vec<usize> = if parallel {
        ranges.par_iter().map(|&(start, end)| memchr::memchr_iter(b'\t', &text[start..end]).count()).collect()
    } else {
        ranges.iter().map(|&(start, end)| memchr::memchr_iter(b'\t', &text[start..end]).count()).collect()
    };
    let total_tabs: usize = chunk_tabs.iter().sum();
    if total_tabs == 0 {
        return (text, offset, detected.encoding);
    }
    let expanded = expand_tabs_with_counts(&text, &ranges, &chunk_tabs, total_tabs, parallel);
    (expanded, 0, detected.encoding)
}

#[derive(Default)]
struct ChunkScan {
    base: usize,
    valid_utf8: bool,
    ascii: bool,
    has_cr: bool,
    has_lone_cr: bool,
    tabs: usize,
    /// Relative offsets (from `base`) of each new line started inside this chunk
    starts: Vec<u32>,
    first_part_bytes: u64,
    first_part_cols: u64,
    inner_cols: u64,
    max_inner_cols: u64,
    max_inner_bytes: u64,
    last_part_bytes: u64,
    last_part_cols: u64,
}

fn scan_chunk(bytes: &[u8], base: usize, end: usize, check_utf8: bool, known_ascii: Option<bool>, count_tabs: bool, collect_lines: bool) -> ChunkScan {
    let chunk = &bytes[base..end];
    let ascii = known_ascii.unwrap_or_else(|| chunk.is_ascii());
    let valid_utf8 = !check_utf8 || ascii || simdutf8::basic::from_utf8(chunk).is_ok();
    if !valid_utf8 {
        return ChunkScan { base, valid_utf8: false, ..Default::default() };
    }
    if !collect_lines {
        let tabs = if count_tabs { memchr::memchr_iter(b'\t', chunk).count() } else { 0 };
        return ChunkScan { base, valid_utf8: true, ascii, tabs, ..Default::default() };
    }
    let mut starts = Vec::with_capacity(chunk.len() / 64);
    let mut tabs = 0usize;
    let mut has_cr = false;
    let mut has_lone_cr = false;
    let mut line_start = 0usize;
    let (mut first_part_bytes, mut first_part_cols) = (0u64, 0u64);
    let (mut inner_cols, mut max_inner_cols, mut max_inner_bytes) = (0u64, 0u64, 0u64);

    let mut on_break = |pos: usize, byte: u8| {
        // The \r of \r\n is not a line end of its own: the \n after it is (cuts never split \r\n)
        if byte == b'\r' {
            has_cr = true;
            if pos + 1 < chunk.len() && chunk[pos + 1] == b'\n' {
                return;
            }
            has_lone_cr = true;
        }
        let line_end = if byte == b'\n' && pos > line_start && chunk[pos - 1] == b'\r' { pos - 1 } else { pos };
        let byte_len = (line_end - line_start) as u64;
        let col_len = if ascii { byte_len } else { char_len(&chunk[line_start..line_end]) as u64 };
        if starts.is_empty() {
            first_part_bytes = byte_len;
            first_part_cols = col_len;
        } else {
            inner_cols += col_len;
            max_inner_cols = max_inner_cols.max(col_len);
            max_inner_bytes = max_inner_bytes.max(byte_len);
        }
        starts.push((pos + 1) as u32);
        line_start = pos + 1;
    };

    if count_tabs {
        for pos in memchr::memchr3_iter(b'\n', b'\r', b'\t', chunk) {
            let byte = chunk[pos];
            if byte == b'\t' {
                tabs += 1;
                continue;
            }
            on_break(pos, byte);
        }
    } else {
        for pos in memchr::memchr2_iter(b'\n', b'\r', chunk) {
            on_break(pos, chunk[pos]);
        }
    }

    let tail = &chunk[line_start..];
    let tail_bytes = tail.len() as u64;
    let tail_cols = if ascii { tail_bytes } else { char_len(tail) as u64 };
    let (last_part_bytes, last_part_cols) = if starts.is_empty() {
        first_part_bytes = tail_bytes;
        first_part_cols = tail_cols;
        (0, 0)
    } else {
        (tail_bytes, tail_cols)
    };

    ChunkScan { base, valid_utf8: true, ascii, has_cr, has_lone_cr, tabs, starts, first_part_bytes, first_part_cols, inner_cols, max_inner_cols, max_inner_bytes, last_part_bytes, last_part_cols }
}

/// Single-pass UTF-8 check, ASCII check, tab count, sparse line indexing and column metrics.
#[allow(clippy::too_many_arguments)]
fn scan_and_build(
    mut bytes: Vec<u8>,
    offset: usize,
    encoding: &str,
    check_utf8: bool,
    check_tabs: bool,
    known_ascii: Option<bool>,
    name: &str,
    path: &str,
) -> OpResult<TextSource> {
    if bytes.capacity().saturating_sub(bytes.len()) > SHRINK_SLACK {
        bytes.shrink_to_fit();
    }
    let total = bytes.len();
    let ranges = chunk_ranges_for(&bytes, offset);
    if ranges.is_empty() {
        let (checkpoints, dense) = if total <= u32::MAX as usize {
            (Starts::U32(vec![offset as u32]), Starts::U32(vec![offset as u32]))
        } else {
            (Starts::U64(vec![offset as u64]), Starts::U64(vec![offset as u64]))
        };
        let buf = Buf::with_ascii(bytes, true);
        let index = LineIndex::new(0, 1, checkpoints, total as u64, EolKind::LfOnly, Some(dense));
        let stage = Stage::from_lines(buf, index, 0, SourceDesc::new(name, path, 0));
        return Ok(TextSource {
            name: if name.is_empty() { "Clipboard".to_string() } else { name.to_string() },
            path: if path.is_empty() { name.to_string() } else { path.to_string() },
            encoding: encoding.to_string(),
            stage,
            chars: 0,
        });
    }

    let parallel = ranges.len() > 1;
    let use_u32 = total <= u32::MAX as usize;
    let mut cp_u32 = if use_u32 { vec![offset as u32] } else { Vec::new() };
    let mut cp_u64 = if use_u32 { Vec::new() } else { vec![offset as u64] };
    let mut small_dense: Option<Starts> = None;

    let mut chunk_tabs = Vec::with_capacity(ranges.len());
    let mut total_tabs = 0usize;
    let mut all_ascii = true;
    let mut any_cr = false;
    let mut any_lone_cr = false;
    let mut invalid_utf8 = false;

    let mut line_idx = 1usize;
    let (mut carry_bytes, mut carry_cols) = (0u64, 0u64);
    let (mut max_cols, mut longest_bytes, mut total_content_cols) = (0u64, 0u64, 0u64);

    for batch in ranges.chunks(BATCH_CHUNKS) {
        let collect_lines = total_tabs == 0;
        let scans: Vec<ChunkScan> = if parallel {
            batch.par_iter().map(|&(start, end)| scan_chunk(&bytes, start, end, check_utf8, known_ascii, check_tabs, collect_lines)).collect()
        } else {
            batch.iter().map(|&(start, end)| scan_chunk(&bytes, start, end, check_utf8, known_ascii, check_tabs, collect_lines)).collect()
        };
        for scan in scans {
            if !scan.valid_utf8 {
                invalid_utf8 = true;
                break;
            }
            all_ascii &= scan.ascii;
            any_cr |= scan.has_cr;
            any_lone_cr |= scan.has_lone_cr;
            chunk_tabs.push(scan.tabs);
            total_tabs += scan.tabs;
            if total_tabs > 0 {
                continue;
            }
            // Extract every LINE_INDEX_STRIDE-th global line start directly from this chunk
            let mut next_cp = (LINE_INDEX_STRIDE - (line_idx % LINE_INDEX_STRIDE)) % LINE_INDEX_STRIDE;
            if use_u32 {
                let base = scan.base as u32;
                while next_cp < scan.starts.len() {
                    cp_u32.push(base + scan.starts[next_cp]);
                    next_cp += LINE_INDEX_STRIDE;
                }
            } else {
                let base = scan.base as u64;
                while next_cp < scan.starts.len() {
                    cp_u64.push(base + scan.starts[next_cp] as u64);
                    next_cp += LINE_INDEX_STRIDE;
                }
            }
            if !parallel && scan.starts.len() < DENSE_THRESHOLD {
                if use_u32 {
                    let base = scan.base as u32;
                    let mut all = Vec::with_capacity(scan.starts.len() + 1);
                    all.push(offset as u32);
                    all.extend(scan.starts.iter().map(|&rel| base + rel));
                    small_dense = Some(Starts::U32(all));
                } else {
                    let base = scan.base as u64;
                    let mut all = Vec::with_capacity(scan.starts.len() + 1);
                    all.push(offset as u64);
                    all.extend(scan.starts.iter().map(|&rel| base + rel as u64));
                    small_dense = Some(Starts::U64(all));
                }
            }
            if scan.starts.is_empty() {
                carry_bytes += scan.first_part_bytes;
                carry_cols += scan.first_part_cols;
            } else {
                let joined_bytes = carry_bytes + scan.first_part_bytes;
                let joined_cols = carry_cols + scan.first_part_cols;
                longest_bytes = longest_bytes.max(joined_bytes).max(scan.max_inner_bytes);
                max_cols = max_cols.max(joined_cols).max(scan.max_inner_cols);
                total_content_cols += joined_cols + scan.inner_cols;
                carry_bytes = scan.last_part_bytes;
                carry_cols = scan.last_part_cols;
            }
            line_idx += scan.starts.len();
        }
        if invalid_utf8 {
            break;
        }
    }

    if invalid_utf8 {
        // Like a browser's TextDecoder: invalid sequences become U+FFFD
        let decoded = String::from_utf8_lossy(&bytes[offset..]).into_owned().into_bytes();
        drop(bytes);
        return scan_and_build(decoded, 0, encoding, false, check_tabs, None, name, path);
    }
    if total_tabs > 0 {
        let expanded = expand_tabs_with_counts(&bytes, &ranges, &chunk_tabs, total_tabs, parallel);
        drop(bytes);
        return scan_and_build(expanded, 0, encoding, false, false, Some(all_ascii), name, path);
    }

    // Finish the final line
    longest_bytes = longest_bytes.max(carry_bytes);
    max_cols = max_cols.max(carry_cols);
    total_content_cols += carry_cols;

    if line_idx > u32::MAX as usize - 1 {
        return Err(OpError::msg("The source has too many lines."));
    }
    if longest_bytes > u32::MAX as u64 {
        return Err(OpError::msg("A line of the source is longer than 4 GiB."));
    }

    let checkpoints = if use_u32 { Starts::U32(cp_u32) } else { Starts::U64(cp_u64) };
    let eol_kind = if any_lone_cr {
        EolKind::Mixed
    } else if any_cr {
        EolKind::CrLf
    } else {
        EolKind::LfOnly
    };
    let buf: BufRef = Buf::with_ascii(bytes, all_ascii);
    let index = LineIndex::new(0, line_idx, checkpoints, total as u64, eol_kind, small_dense);
    if line_idx <= DENSE_THRESHOLD || (longest_bytes > 8192 && line_idx * 4 <= total / 16) {
        index.ensure_dense(buf.bytes());
    }
    let stage = Stage::from_lines(buf, index, max_cols, SourceDesc::new(name, path, 0));
    Ok(TextSource {
        name: if name.is_empty() { "Clipboard".to_string() } else { name.to_string() },
        path: if path.is_empty() { name.to_string() } else { path.to_string() },
        encoding: encoding.to_string(),
        stage,
        chars: total_content_cols + line_idx as u64 - 1,
    })
}

/// A source from the bytes of a file.
pub fn source_from_bytes(bytes: Vec<u8>, name: &str, path: &str) -> OpResult<TextSource> {
    let detected = detect_encoding(&bytes);
    match detected.encoding {
        "utf-16le" | "utf-16be" => {
            let encoding = if detected.encoding == "utf-16le" { encoding_rs::UTF_16LE } else { encoding_rs::UTF_16BE };
            let (decoded, _) = encoding.decode_without_bom_handling(&bytes[detected.offset..]);
            let decoded = decoded.into_owned().into_bytes();
            drop(bytes);
            scan_and_build(decoded, 0, detected.encoding, false, true, None, name, path)
        }
        _ => scan_and_build(bytes, detected.offset, detected.encoding, true, true, None, name, path),
    }
}

/// A source from text that is already a string (the clipboard).
pub fn source_from_text(text: String, name: &str, path: &str) -> OpResult<TextSource> {
    scan_and_build(text.into_bytes(), 0, "clipboard", false, true, None, name, path)
}

/// The raw stage of a text; for tests and small inputs.
pub fn stage_from_text(text: String, name: &str, path: &str) -> Stage {
    source_from_text(text, name, path).expect("text fits a stage").stage
}

fn read_file(path: &Path, progress: &LoadProgress, cancel: &Cancel) -> OpResult<Vec<u8>> {
    const READ_CHUNK: usize = 16 << 20;
    let io_error = |error: std::io::Error| OpError::Msg(error.to_string());
    let mut file = std::fs::File::open(path).map_err(io_error)?;
    let length = file.metadata().map_err(io_error)?.len() as usize;
    let mut data = Vec::new();
    data.try_reserve_exact(length).map_err(|_| OpError::msg("Not enough memory to load the file."))?;
    // Fill the exact preallocated capacity in large chunks without overshooting into a reallocation.
    while data.len() < length {
        cancel.check()?;
        let step = (length - data.len()).min(READ_CHUNK) as u64;
        let read = (&mut file).take(step).read_to_end(&mut data).map_err(io_error)?;
        if read == 0 {
            return Ok(data);
        }
        progress.done.fetch_add(read as u64, Ordering::Relaxed);
    }
    // Check whether the file grew while we were reading it without growing `data` on normal EOF.
    let mut probe = [0u8; 4096];
    cancel.check()?;
    let extra = file.read(&mut probe).map_err(io_error)?;
    if extra > 0 {
        data.extend_from_slice(&probe[..extra]);
        progress.done.fetch_add(extra as u64, Ordering::Relaxed);
        loop {
            cancel.check()?;
            let read = (&mut file).take(READ_CHUNK as u64).read_to_end(&mut data).map_err(io_error)?;
            if read == 0 {
                break;
            }
            progress.done.fetch_add(read as u64, Ordering::Relaxed);
        }
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
    let cancel = Cancel::new();
    for (bundle, &(buf_offset, src_offset)) in bundles.iter().zip(&prepared.offsets) {
        let stage = bundle.stage;
        let chunks = par_chunks(stage.len(), &cancel, |range| {
            let mut chunk_spans = Vec::with_capacity(range.len());
            let mut chunk_src = Vec::with_capacity(range.len());
            stage.for_each_row(range, |row, span, _| {
                chunk_spans.push(remap(span, buf_offset));
                chunk_src.push(remap_src(stage.src(row), src_offset));
            });
            Ok((chunk_spans, chunk_src))
        })?;
        for (chunk_spans, chunk_src) in chunks {
            spans.extend_from_slice(&chunk_spans);
            src.extend_from_slice(&chunk_src);
        }
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
    if longest >= 16_384 {
        for bundle in bundles {
            bundle.stage.ensure_random_access();
        }
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
    stage.for_each_row(0..stage.len(), |row, span, _| {
        if let Some(rows) = per_source.get_mut(stage.src(row) as usize) {
            rows.push(span);
        }
    });
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
    let cancel = Cancel::new();
    let chunks = par_chunks(stage.len(), &cancel, |range| {
        let mut chunk_spans = Vec::with_capacity(range.len());
        stage.for_each_row(range, |_, span, _| chunk_spans.push(span));
        Ok(chunk_spans)
    })
    .unwrap();
    let mut spans = Vec::with_capacity(stage.len());
    for chunk in chunks {
        spans.extend_from_slice(&chunk);
    }
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
        assert_eq!(source.stage.max_cols, 16);
        assert_eq!(source.chars, 200_000 * 16 + 200_000);
        // Sparse checkpoints: 200k lines use ~12 KiB instead of 800 KiB before random access
        assert!(source.stage.own_bytes < 64 * 1024, "own_bytes={}", source.stage.own_bytes);
        assert!((0..200_000).all(|row| source.stage.line(row) == "0123456789abcdef"));
        assert_eq!(source.stage.line(200_000), "");
        if let crate::stage::Rows::Lines(index) = &source.stage.rows {
            let bytes = source.stage.bufs[0].bytes();
            assert_eq!(index.row_of_offset(0, bytes), (0, 0));
            assert_eq!(index.row_of_offset(18 * 100 + 5, bytes), (100, 18 * 100));
            assert_eq!(index.row_of_offset(bytes.len(), bytes), (200_000, bytes.len()));
            // Materializing dense gives the exact same bounds and row_of_offset
            index.ensure_dense(bytes);
            assert_eq!(index.row_of_offset(18 * 100 + 5, bytes), (100, 18 * 100));
            assert_eq!(source.stage.line(199_999), "0123456789abcdef");
            assert_eq!(source.stage.line(200_000), "");
        } else {
            panic!("expected Rows::Lines");
        }
    }

    #[test]
    fn chunk_boundaries_preserve_crlf_multibyte_long_lines_and_tabs() {
        // Place \r\n and a multibyte UTF-8 character right across the 1 MiB chunk boundary
        let mut bytes = vec![b'a'; SCAN_CHUNK - 1];
        bytes.extend_from_slice(b"\r\n");
        bytes.extend_from_slice(&vec![b'b'; SCAN_CHUNK - 1]);
        bytes.extend_from_slice("ž\n".as_bytes());
        bytes.extend_from_slice(&vec![b'c'; SCAN_CHUNK + 50]);
        let source = source_from_bytes(bytes, "edge", "edge").unwrap();
        assert_eq!(source.stage.len(), 3);
        assert_eq!(source.stage.line(0).len(), SCAN_CHUNK - 1);
        assert_eq!(source.stage.line(1).chars().count(), SCAN_CHUNK);
        assert_eq!(source.stage.line(2).len(), SCAN_CHUNK + 50);
        assert_eq!(source.stage.max_cols, (SCAN_CHUNK + 50) as u64);
        assert_eq!(source.chars, (SCAN_CHUNK - 1 + SCAN_CHUNK + SCAN_CHUNK + 50 + 2) as u64);

        // Large file (> 2 MiB) with a single tab in the first chunk only
        let mut with_tab = Vec::with_capacity(2 * SCAN_CHUNK + 100);
        with_tab.extend_from_slice(b"\tfirst\n");
        for _ in 0..150_000 {
            with_tab.extend_from_slice(b"0123456789abcdef\n");
        }
        let source = source_from_bytes(with_tab, "tab", "tab").unwrap();
        assert_eq!(source.stage.len(), 150_002);
        assert_eq!(source.stage.line(0), "    first");
        assert_eq!(source.stage.line(1), "0123456789abcdef");
        assert_eq!(source.stage.line(150_000), "0123456789abcdef");
        assert_eq!(source.stage.line(150_001), "");
        assert_eq!(source.stage.max_cols, 16);
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
