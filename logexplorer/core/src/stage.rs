//! A stage: the rows of the view after some operations, as spans into shared buffers.

use crate::text::{as_str, char_len, Buf, BufRef, Span, ARENA};
use rayon::prelude::*;
use std::ops::Range;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, OnceLock};

/// Row without a source file (JS -1)
pub const NO_SRC: u16 = u16::MAX;
/// Row without an original row (JS -1)
pub const NO_ROW: u32 = u32::MAX;

/// Number of background tints; a source's tone cycles through them
pub const SOURCE_TONES: usize = 8;

/// One loaded file (or the clipboard) the rows come from.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SourceDesc {
    pub label: String,
    pub path: String,
    pub tone: usize,
}

impl SourceDesc {
    pub fn new(name: &str, path: &str, index: usize) -> Self {
        let label = first_non_empty(&[name, path, "Clipboard"]);
        let path = first_non_empty(&[path, name, "Clipboard"]);
        SourceDesc { label, path, tone: index % SOURCE_TONES }
    }

    /// What a Filter stores to name this source ("Only in files")
    pub fn filter_key(&self) -> String {
        format!("{}\u{241f}{}", self.path, self.label)
    }

    pub fn display(&self) -> &str {
        if self.path.is_empty() {
            &self.label
        } else {
            &self.path
        }
    }
}

fn first_non_empty(candidates: &[&str]) -> String {
    candidates.iter().find(|text| !text.is_empty()).copied().unwrap_or("").to_string()
}

pub enum Starts {
    U32(Vec<u32>),
    U64(Vec<u64>),
}

impl Starts {
    #[inline]
    pub fn len(&self) -> usize {
        match self {
            Starts::U32(list) => list.len(),
            Starts::U64(list) => list.len(),
        }
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    #[inline]
    pub fn get(&self, index: usize) -> u64 {
        match self {
            Starts::U32(list) => list[index] as u64,
            Starts::U64(list) => list[index],
        }
    }

    #[inline]
    pub fn partition_point_le(&self, offset: u64) -> usize {
        match self {
            Starts::U32(list) => list.partition_point(|&start| (start as u64) <= offset).saturating_sub(1),
            Starts::U64(list) => list.partition_point(|&start| start <= offset).saturating_sub(1),
        }
    }

    pub(crate) fn heap_bytes(&self) -> usize {
        match self {
            Starts::U32(list) => list.capacity() * 4,
            Starts::U64(list) => list.capacity() * 8,
        }
    }
}

/// Number of consecutive lines represented by one checkpoint in [`LineIndex`].
pub const LINE_INDEX_STRIDE: usize = 64;

/// Finds `(line_end_without_eol, next_line_start)` starting at `start` in `bytes[..region_end]`.
#[inline]
pub(crate) fn scan_line_end(bytes: &[u8], start: usize, region_end: usize) -> (usize, usize) {
    match memchr::memchr2(b'\n', b'\r', &bytes[start..region_end]) {
        None => (region_end, region_end),
        Some(rel) => {
            let pos = start + rel;
            if bytes[pos] == b'\r' && pos + 1 < region_end && bytes[pos + 1] == b'\n' {
                (pos, pos + 2)
            } else {
                (pos, pos + 1)
            }
        }
    }
}

#[inline]
fn bounds_from_starts(starts: &Starts, index: usize, region_end: usize, bytes: &[u8]) -> (usize, usize) {
    let start = starts.get(index) as usize;
    if index + 1 == starts.len() {
        return (start, region_end);
    }
    let mut end = starts.get(index + 1) as usize - 1;
    if bytes[end] == b'\n' && end > start && bytes[end - 1] == b'\r' {
        end -= 1;
    }
    (start, end)
}

/// Sequential iterator over `(start, end)` byte bounds of consecutive lines.
pub struct LineBoundsIter<'a> {
    bytes: &'a [u8],
    pos: usize,
    region_end: usize,
    remaining: usize,
}

impl Iterator for LineBoundsIter<'_> {
    type Item = (usize, usize);

    #[inline]
    fn next(&mut self) -> Option<(usize, usize)> {
        if self.remaining == 0 {
            return None;
        }
        self.remaining -= 1;
        let start = self.pos;
        let (end, next) = scan_line_end(self.bytes, start, self.region_end);
        self.pos = next;
        Some((start, end))
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        (self.remaining, Some(self.remaining))
    }
}

/// The consecutive lines of one region of a buffer: sparse checkpoints every 64 lines (~0.7 MiB
/// for 11M lines) with a full per-line index materialized lazily only when heavy random access needs it.
pub struct LineIndex {
    pub buf: u16,
    pub lines: usize,
    /// Offset of every [`LINE_INDEX_STRIDE`]-th line (line 0, 64, 128, ...)
    pub checkpoints: Starts,
    /// End of the region (the end of the last line)
    pub end: u64,
    /// Full per-line offsets, built on demand for heavy random access
    dense: OnceLock<Starts>,
}

impl LineIndex {
    pub fn new(buf: u16, lines: usize, checkpoints: Starts, end: u64, dense: Option<Starts>) -> Self {
        let cell = OnceLock::new();
        if let Some(starts) = dense {
            let _ = cell.set(starts);
        }
        LineIndex { buf, lines, checkpoints, end, dense: cell }
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.lines
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.lines == 0
    }

    pub fn heap_bytes(&self) -> usize {
        self.checkpoints.heap_bytes() + self.dense.get().map_or(0, Starts::heap_bytes)
    }

    /// Materializes the full per-line start table once, in parallel for large files.
    pub fn ensure_dense(&self, bytes: &[u8]) -> &Starts {
        self.dense.get_or_init(|| self.build_dense(bytes))
    }

    fn build_dense(&self, bytes: &[u8]) -> Starts {
        let region_end = self.end as usize;
        let parallel = self.lines >= 16_384;
        if self.end <= u32::MAX as u64 {
            let mut all = vec![0u32; self.lines];
            let fill = |(cp_idx, slot): (usize, &mut [u32])| {
                let mut pos = self.checkpoints.get(cp_idx) as usize;
                for out in slot {
                    *out = pos as u32;
                    let (_, next) = scan_line_end(bytes, pos, region_end);
                    pos = next;
                }
            };
            if parallel {
                all.par_chunks_mut(LINE_INDEX_STRIDE).enumerate().for_each(fill);
            } else {
                all.chunks_mut(LINE_INDEX_STRIDE).enumerate().for_each(fill);
            }
            Starts::U32(all)
        } else {
            let mut all = vec![0u64; self.lines];
            let fill = |(cp_idx, slot): (usize, &mut [u64])| {
                let mut pos = self.checkpoints.get(cp_idx) as usize;
                for out in slot {
                    *out = pos as u64;
                    let (_, next) = scan_line_end(bytes, pos, region_end);
                    pos = next;
                }
            };
            if parallel {
                all.par_chunks_mut(LINE_INDEX_STRIDE).enumerate().for_each(fill);
            } else {
                all.chunks_mut(LINE_INDEX_STRIDE).enumerate().for_each(fill);
            }
            Starts::U64(all)
        }
    }

    /// Byte offset where line `index` begins.
    #[inline]
    pub fn row_start(&self, index: usize, bytes: &[u8]) -> usize {
        if let Some(dense) = self.dense.get() {
            return dense.get(index) as usize;
        }
        let mut pos = self.checkpoints.get(index / LINE_INDEX_STRIDE) as usize;
        let skip = index % LINE_INDEX_STRIDE;
        let region_end = self.end as usize;
        for _ in 0..skip {
            let (_, next) = scan_line_end(bytes, pos, region_end);
            pos = next;
        }
        pos
    }

    /// Byte range of line `index` without its terminator (`\n`, `\r\n` or a lone `\r`)
    #[inline]
    pub fn bounds(&self, index: usize, bytes: &[u8]) -> (usize, usize) {
        if let Some(dense) = self.dense.get() {
            return bounds_from_starts(dense, index, self.end as usize, bytes);
        }
        let cp_start = self.checkpoints.get(index / LINE_INDEX_STRIDE) as usize;
        let mut pos = cp_start;
        let skip = index % LINE_INDEX_STRIDE;
        let region_end = self.end as usize;
        for _ in 0..skip {
            let (_, next) = scan_line_end(bytes, pos, region_end);
            pos = next;
        }
        let (end, _) = scan_line_end(bytes, pos, region_end);
        // Guard against a single huge line inside a checkpoint block turning random lookups into megabyte scans
        if end.saturating_sub(cp_start) > (1 << 20) && self.lines * 4 <= bytes.len() / 8 {
            let _ = self.ensure_dense(bytes);
        }
        (pos, end)
    }

    /// Sequential iterator over `(start, end)` bounds of lines in `range`.
    #[inline]
    pub fn iter_range<'a>(&self, bytes: &'a [u8], range: Range<usize>) -> LineBoundsIter<'a> {
        if range.is_empty() {
            return LineBoundsIter { bytes, pos: 0, region_end: 0, remaining: 0 };
        }
        let pos = self.row_start(range.start, bytes);
        LineBoundsIter { bytes, pos, region_end: self.end as usize, remaining: range.len() }
    }

    /// `(row, line_start)` containing byte `offset` in the buffer.
    pub fn row_of_offset(&self, offset: usize, bytes: &[u8]) -> (usize, usize) {
        if let Some(dense) = self.dense.get() {
            let row = dense.partition_point_le(offset as u64);
            return (row, dense.get(row) as usize);
        }
        let cp_idx = self.checkpoints.partition_point_le(offset as u64);
        let mut row = cp_idx * LINE_INDEX_STRIDE;
        let mut pos = self.checkpoints.get(cp_idx) as usize;
        let region_end = self.end as usize;
        while row + 1 < self.lines {
            let (_, next) = scan_line_end(bytes, pos, region_end);
            if next > offset {
                break;
            }
            pos = next;
            row += 1;
        }
        (row, pos)
    }
}

pub enum Rows {
    Lines(Arc<LineIndex>),
    Indexed { base: Arc<LineIndex>, rows: Arc<Vec<u32>> },
    Spans(Arc<Vec<Span>>),
}

#[derive(Clone)]
pub enum SrcIds {
    Uniform(u16),
    PerRow(Arc<Vec<u16>>),
}

#[derive(Clone)]
pub enum RowIds {
    /// Row `i` is original row `i`
    Identity,
    List(Arc<Vec<u32>>),
}

/// What a Diff remembers so that a following Reverse with the same settings restores its input.
#[derive(Clone)]
pub struct DiffMeta {
    pub signature: String,
    pub original: Arc<Stage>,
}

pub struct Stage {
    /// Unique per stage; caches of the view key on it
    pub id: u64,
    pub label: &'static str,
    pub bufs: Vec<BufRef>,
    pub rows: Rows,
    pub src: SrcIds,
    pub row_ids: RowIds,
    pub sources: Arc<Vec<SourceDesc>>,
    /// Longest row in columns
    pub max_cols: u64,
    /// Memory this stage holds beyond what it shares with other stages
    pub own_bytes: usize,
    pub diff_meta: Option<DiffMeta>,
}

static NEXT_STAGE_ID: AtomicU64 = AtomicU64::new(1);

pub fn next_stage_id() -> u64 {
    NEXT_STAGE_ID.fetch_add(1, Ordering::Relaxed)
}

impl Stage {
    #[inline]
    pub fn len(&self) -> usize {
        match &self.rows {
            Rows::Lines(index) => index.len(),
            Rows::Indexed { rows, .. } => rows.len(),
            Rows::Spans(spans) => spans.len(),
        }
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    #[inline]
    pub fn line_bytes(&self, row: usize) -> &[u8] {
        match &self.rows {
            Rows::Lines(index) => {
                let bytes = self.bufs[index.buf as usize].bytes();
                let (start, end) = index.bounds(row, bytes);
                &bytes[start..end]
            }
            Rows::Indexed { base, rows } => {
                let bytes = self.bufs[base.buf as usize].bytes();
                let (start, end) = base.bounds(rows[row] as usize, bytes);
                &bytes[start..end]
            }
            Rows::Spans(spans) => {
                let span = spans[row];
                let start = span.off() as usize;
                &self.bufs[span.buf() as usize].bytes()[start..start + span.len() as usize]
            }
        }
    }

    /// The text of a row; empty outside the stage
    #[inline]
    pub fn line(&self, row: usize) -> &str {
        if row >= self.len() {
            return "";
        }
        as_str(self.line_bytes(row))
    }

    #[inline]
    pub fn span(&self, row: usize) -> Span {
        match &self.rows {
            Rows::Lines(index) => {
                let (start, end) = index.bounds(row, self.bufs[index.buf as usize].bytes());
                Span::new(index.buf, start as u64, (end - start) as u32)
            }
            Rows::Indexed { base, rows } => {
                let (start, end) = base.bounds(rows[row] as usize, self.bufs[base.buf as usize].bytes());
                Span::new(base.buf, start as u64, (end - start) as u32)
            }
            Rows::Spans(spans) => spans[row],
        }
    }

    #[inline]
    pub fn span_bytes(&self, span: Span) -> &[u8] {
        let start = span.off() as usize;
        &self.bufs[span.buf() as usize].bytes()[start..start + span.len() as usize]
    }

    /// Length of a row in columns
    #[inline]
    pub fn line_cols(&self, row: usize) -> usize {
        if row >= self.len() {
            return 0;
        }
        let span = self.span(row);
        if self.bufs[span.buf() as usize].ascii {
            span.len() as usize
        } else {
            char_len(self.span_bytes(span))
        }
    }

    #[inline]
    pub fn src(&self, row: usize) -> u16 {
        match &self.src {
            SrcIds::Uniform(id) => *id,
            SrcIds::PerRow(list) => list.get(row).copied().unwrap_or(NO_SRC),
        }
    }

    #[inline]
    pub fn row_id(&self, row: usize) -> u32 {
        match &self.row_ids {
            RowIds::Identity => row as u32,
            RowIds::List(list) => list.get(row).copied().unwrap_or(NO_ROW),
        }
    }

    pub fn source_of(&self, row: usize) -> Option<&SourceDesc> {
        if row >= self.len() {
            return None;
        }
        self.sources.get(self.src(row) as usize)
    }

    pub fn multiple_sources(&self) -> bool {
        self.sources.len() > 1
    }

    /// Calls `f(row, span, line_bytes)` sequentially over `range` without per-row checkpoint scanning.
    #[inline]
    pub fn for_each_row(&self, range: Range<usize>, mut f: impl FnMut(usize, Span, &[u8])) {
        match &self.rows {
            Rows::Lines(index) => {
                let bytes = self.bufs[index.buf as usize].bytes();
                let buf = index.buf;
                for (offset, (start, end)) in index.iter_range(bytes, range.clone()).enumerate() {
                    let row = range.start + offset;
                    f(row, Span::new(buf, start as u64, (end - start) as u32), &bytes[start..end]);
                }
            }
            Rows::Indexed { base, rows } => {
                let bytes = self.bufs[base.buf as usize].bytes();
                if range.len() >= 1024 {
                    base.ensure_dense(bytes);
                }
                let buf = base.buf;
                for (offset, &base_row) in rows[range.clone()].iter().enumerate() {
                    let row = range.start + offset;
                    let (start, end) = base.bounds(base_row as usize, bytes);
                    f(row, Span::new(buf, start as u64, (end - start) as u32), &bytes[start..end]);
                }
            }
            Rows::Spans(spans) => {
                for (offset, &span) in spans[range.clone()].iter().enumerate() {
                    let row = range.start + offset;
                    let start = span.off() as usize;
                    let line = &self.bufs[span.buf() as usize].bytes()[start..start + span.len() as usize];
                    f(row, span, line);
                }
            }
        }
    }

    /// Fallible variant of [`Stage::for_each_row`].
    #[inline]
    pub fn try_for_each_row<E>(&self, range: Range<usize>, mut f: impl FnMut(usize, Span, &[u8]) -> Result<(), E>) -> Result<(), E> {
        match &self.rows {
            Rows::Lines(index) => {
                let bytes = self.bufs[index.buf as usize].bytes();
                let buf = index.buf;
                for (offset, (start, end)) in index.iter_range(bytes, range.clone()).enumerate() {
                    let row = range.start + offset;
                    f(row, Span::new(buf, start as u64, (end - start) as u32), &bytes[start..end])?;
                }
            }
            Rows::Indexed { base, rows } => {
                let bytes = self.bufs[base.buf as usize].bytes();
                if range.len() >= 1024 {
                    base.ensure_dense(bytes);
                }
                let buf = base.buf;
                for (offset, &base_row) in rows[range.clone()].iter().enumerate() {
                    let row = range.start + offset;
                    let (start, end) = base.bounds(base_row as usize, bytes);
                    f(row, Span::new(buf, start as u64, (end - start) as u32), &bytes[start..end])?;
                }
            }
            Rows::Spans(spans) => {
                for (offset, &span) in spans[range.clone()].iter().enumerate() {
                    let row = range.start + offset;
                    let start = span.off() as usize;
                    let line = &self.bufs[span.buf() as usize].bytes()[start..start + span.len() as usize];
                    f(row, span, line)?;
                }
            }
        }
        Ok(())
    }

    /// Materializes a dense line index when an operation is about to perform millions of random row lookups.
    pub fn ensure_random_access(&self) {
        match &self.rows {
            Rows::Lines(index) => {
                index.ensure_dense(self.bufs[index.buf as usize].bytes());
            }
            Rows::Indexed { base, .. } => {
                base.ensure_dense(self.bufs[base.buf as usize].bytes());
            }
            Rows::Spans(_) => {}
        }
    }

    /// Bytes of all rows joined by `\n`
    pub fn text_bytes(&self) -> u64 {
        let rows = self.len();
        let content: u64 = match &self.rows {
            Rows::Lines(index) => {
                let bytes = self.bufs[index.buf as usize].bytes();
                let step = 1 << 16;
                let chunks: Vec<Range<usize>> = (0..rows).step_by(step).map(|start| start..(start + step).min(rows)).collect();
                chunks
                    .into_par_iter()
                    .map(|range| index.iter_range(bytes, range).map(|(start, end)| (end - start) as u64).sum::<u64>())
                    .sum()
            }
            Rows::Indexed { base, rows: indices } => {
                let bytes = self.bufs[base.buf as usize].bytes();
                if indices.len() >= 16_384 {
                    base.ensure_dense(bytes);
                }
                indices
                    .par_iter()
                    .with_min_len(1 << 14)
                    .map(|&row| {
                        let (start, end) = base.bounds(row as usize, bytes);
                        (end - start) as u64
                    })
                    .sum()
            }
            Rows::Spans(spans) => spans.par_iter().with_min_len(1 << 14).map(|span| span.len() as u64).sum(),
        };
        content + rows.saturating_sub(1) as u64
    }

    /// The stage of a loaded source: the consecutive lines of one buffer.
    pub fn from_lines(buf: BufRef, index: LineIndex, max_cols: u64, source: SourceDesc) -> Stage {
        let own_bytes = index.heap_bytes();
        Stage {
            id: next_stage_id(),
            label: "Raw",
            bufs: vec![buf],
            rows: Rows::Lines(Arc::new(index)),
            src: SrcIds::Uniform(0),
            row_ids: RowIds::Identity,
            sources: Arc::new(vec![source]),
            max_cols,
            own_bytes,
            diff_meta: None,
        }
    }

    /// A stage made of explicit rows (several sources combined, or the result of an operation).
    pub fn from_spans(label: &'static str, bufs: Vec<BufRef>, spans: Vec<Span>, src: SrcIds, row_ids: RowIds, sources: Arc<Vec<SourceDesc>>) -> Stage {
        Stage::from_spans_with_cols(label, bufs, spans, src, row_ids, sources, None)
    }

    fn from_spans_with_cols(
        label: &'static str,
        bufs: Vec<BufRef>,
        spans: Vec<Span>,
        src: SrcIds,
        row_ids: RowIds,
        sources: Arc<Vec<SourceDesc>>,
        known_max_cols: Option<u64>,
    ) -> Stage {
        let max_cols = match known_max_cols {
            Some(cols) => {
                debug_assert_eq!(cols, max_cols_of(&bufs, &spans));
                cols
            }
            None => max_cols_of(&bufs, &spans),
        };
        let mut own_bytes = spans.capacity() * std::mem::size_of::<Span>();
        if let SrcIds::PerRow(list) = &src {
            own_bytes += list.capacity() * 2;
        }
        if let RowIds::List(list) = &row_ids {
            own_bytes += list.capacity() * 4;
        }
        Stage { id: next_stage_id(), label, bufs, rows: Rows::Spans(Arc::new(spans)), src, row_ids, sources, max_cols, own_bytes, diff_meta: None }
    }

    /// The result of an operation on `self`.
    pub fn derive(&self, label: &'static str, out: OutRows) -> Stage {
        let OutRows { mut spans, origin, mut arena, max_cols, arena_resolved, arena_ascii } = out;
        let mut bufs = self.bufs.clone();
        let mut arena_bytes = 0;
        if !arena.is_empty() {
            let arena_index = bufs.len() as u16;
            if arena.capacity() - arena.len() > (1 << 20) {
                arena.shrink_to_fit();
            }
            arena_bytes = arena.capacity();
            let buf = match arena_ascii {
                Some(ascii) => {
                    debug_assert_eq!(ascii, crate::text::is_ascii_par(&arena));
                    Buf::with_ascii(arena, ascii)
                }
                None => Buf::new(arena),
            };
            bufs.push(buf);
            if !arena_resolved {
                spans.par_iter_mut().with_min_len(1 << 14).for_each(|span| {
                    if span.buf() == ARENA {
                        *span = span.with_buf(arena_index);
                    }
                });
            }
        } else {
            debug_assert!(spans.iter().all(|span| span.buf() != ARENA));
        }
        let shared_ids = origin.is_none();
        let (src, row_ids) = match origin {
            // Same rows in the same order: the ids are shared with the input
            None => {
                debug_assert_eq!(spans.len(), self.len());
                (self.src.clone(), self.row_ids.clone())
            }
            Some(origin) => {
                let src = match &self.src {
                    // A row that comes from no row (an empty Join) has no source either
                    SrcIds::Uniform(id) if origin.par_iter().with_min_len(1 << 14).any(|&row| row == NO_ROW) => {
                        SrcIds::PerRow(Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| if row == NO_ROW { NO_SRC } else { *id }).collect()))
                    }
                    SrcIds::Uniform(id) => SrcIds::Uniform(*id),
                    SrcIds::PerRow(list) => {
                        SrcIds::PerRow(Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| list.get(row as usize).copied().unwrap_or(NO_SRC)).collect()))
                    }
                };
                let row_ids = match &self.row_ids {
                    RowIds::Identity => RowIds::List(Arc::new(origin)),
                    RowIds::List(list) => RowIds::List(Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| list.get(row as usize).copied().unwrap_or(NO_ROW)).collect())),
                };
                (src, row_ids)
            }
        };
        let mut stage = Stage::from_spans_with_cols(label, bufs, spans, src, row_ids, self.sources.clone(), max_cols);
        if shared_ids {
            // from_spans counted the id lists this stage only borrows
            stage.own_bytes = match &stage.rows {
                Rows::Spans(spans) => spans.capacity() * std::mem::size_of::<Span>(),
                Rows::Lines(_) | Rows::Indexed { .. } => 0,
            };
        }
        stage.own_bytes += arena_bytes;
        stage
    }

    /// The result of an operation that only picks or reorders whole rows of `self`.
    /// When `self` is backed by a `LineIndex`, keeps 4 bytes per row instead of 16.
    pub fn derive_rows(&self, label: &'static str, origin: Vec<u32>, known_max_cols: Option<u64>) -> Stage {
        debug_assert!(origin.iter().all(|&row| (row as usize) < self.len()));
        match &self.rows {
            Rows::Lines(base) => {
                let src = match &self.src {
                    SrcIds::Uniform(id) => SrcIds::Uniform(*id),
                    SrcIds::PerRow(list) => {
                        SrcIds::PerRow(Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| list.get(row as usize).copied().unwrap_or(NO_SRC)).collect()))
                    }
                };
                let rows = Arc::new(origin);
                let (row_ids, shared_row_ids) = match &self.row_ids {
                    RowIds::Identity => (RowIds::List(Arc::clone(&rows)), true),
                    RowIds::List(list) => (
                        RowIds::List(Arc::new(rows.par_iter().with_min_len(1 << 14).map(|&row| list.get(row as usize).copied().unwrap_or(NO_ROW)).collect())),
                        false,
                    ),
                };
                let max_cols = match known_max_cols {
                    Some(cols) => {
                        debug_assert_eq!(cols, max_cols_indexed(&self.bufs, base, &rows));
                        cols
                    }
                    None => max_cols_indexed(&self.bufs, base, &rows),
                };
                let mut own_bytes = rows.capacity() * 4;
                if !shared_row_ids {
                    if let RowIds::List(list) = &row_ids {
                        own_bytes += list.capacity() * 4;
                    }
                }
                if let SrcIds::PerRow(list) = &src {
                    own_bytes += list.capacity() * 2;
                }
                Stage {
                    id: next_stage_id(),
                    label,
                    bufs: self.bufs.clone(),
                    rows: Rows::Indexed { base: Arc::clone(base), rows },
                    src,
                    row_ids,
                    sources: self.sources.clone(),
                    max_cols,
                    own_bytes,
                    diff_meta: None,
                }
            }
            Rows::Indexed { base, rows: parent_rows } => {
                let src = match &self.src {
                    SrcIds::Uniform(id) => SrcIds::Uniform(*id),
                    SrcIds::PerRow(list) => {
                        SrcIds::PerRow(Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| list.get(row as usize).copied().unwrap_or(NO_SRC)).collect()))
                    }
                };
                let shares_with_row_ids = matches!(&self.row_ids, RowIds::List(parent_ids) if Arc::ptr_eq(parent_rows, parent_ids));
                let (mapped, row_ids, shared_row_ids) = if shares_with_row_ids {
                    let mut mapped = origin;
                    mapped.par_iter_mut().with_min_len(1 << 14).for_each(|row| *row = parent_rows[*row as usize]);
                    let mapped = Arc::new(mapped);
                    (Arc::clone(&mapped), RowIds::List(mapped), true)
                } else {
                    let mapped: Arc<Vec<u32>> = Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| parent_rows[row as usize]).collect());
                    let row_ids = match &self.row_ids {
                        RowIds::Identity => RowIds::List(Arc::new(origin)),
                        RowIds::List(parent_ids) => RowIds::List(Arc::new(
                            origin.par_iter().with_min_len(1 << 14).map(|&row| parent_ids.get(row as usize).copied().unwrap_or(NO_ROW)).collect(),
                        )),
                    };
                    (mapped, row_ids, false)
                };
                let max_cols = match known_max_cols {
                    Some(cols) => {
                        debug_assert_eq!(cols, max_cols_indexed(&self.bufs, base, &mapped));
                        cols
                    }
                    None => max_cols_indexed(&self.bufs, base, &mapped),
                };
                let mut own_bytes = mapped.capacity() * 4;
                if !shared_row_ids {
                    if let RowIds::List(list) = &row_ids {
                        own_bytes += list.capacity() * 4;
                    }
                }
                if let SrcIds::PerRow(list) = &src {
                    own_bytes += list.capacity() * 2;
                }
                Stage {
                    id: next_stage_id(),
                    label,
                    bufs: self.bufs.clone(),
                    rows: Rows::Indexed { base: Arc::clone(base), rows: mapped },
                    src,
                    row_ids,
                    sources: self.sources.clone(),
                    max_cols,
                    own_bytes,
                    diff_meta: None,
                }
            }
            Rows::Spans(parent_spans) => {
                let spans: Vec<Span> = origin.par_iter().with_min_len(1 << 14).map(|&row| parent_spans[row as usize]).collect();
                self.derive(label, OutRows { spans, origin: Some(origin), arena: Vec::new(), max_cols: known_max_cols, arena_resolved: true, arena_ascii: None })
            }
        }
    }

    /// The same rows as a new stage (an operation with nothing to do).
    pub fn copy_as(&self, label: &'static str) -> Stage {
        let rows = match &self.rows {
            Rows::Lines(index) => Rows::Lines(Arc::clone(index)),
            Rows::Indexed { base, rows } => Rows::Indexed { base: Arc::clone(base), rows: Arc::clone(rows) },
            Rows::Spans(spans) => Rows::Spans(Arc::clone(spans)),
        };
        Stage {
            id: next_stage_id(),
            label,
            bufs: self.bufs.clone(),
            rows,
            src: self.src.clone(),
            row_ids: self.row_ids.clone(),
            sources: self.sources.clone(),
            max_cols: self.max_cols,
            own_bytes: 0,
            diff_meta: None,
        }
    }
}

/// Rows an operation produced.
#[derive(Default)]
pub struct OutRows {
    /// Spans into the input's buffers, or into `arena` (buffer [`ARENA`], or already resolved to `stage.bufs.len()`)
    pub spans: Vec<Span>,
    /// Input row each output row comes from ([`NO_ROW`] for none); `None` when the rows are
    /// the input rows one to one
    pub origin: Option<Vec<u32>>,
    /// Text written by the operation
    pub arena: Vec<u8>,
    /// Longest row in columns when already computed while building the spans
    pub max_cols: Option<u64>,
    /// True when spans pointing into `arena` already carry the final arena buffer index
    pub arena_resolved: bool,
    /// Whether `arena` is pure ASCII, when already known while writing it
    pub arena_ascii: Option<bool>,
}

fn max_cols_indexed(bufs: &[BufRef], base: &LineIndex, rows: &[u32]) -> u64 {
    let buf = &bufs[base.buf as usize];
    let bytes = buf.bytes();
    if rows.len() >= 16_384 {
        base.ensure_dense(bytes);
    }
    if buf.ascii {
        rows.par_iter()
            .with_min_len(1 << 14)
            .map(|&row| {
                let (start, end) = base.bounds(row as usize, bytes);
                (end - start) as u64
            })
            .max()
            .unwrap_or(0)
    } else {
        rows.par_iter()
            .with_min_len(1 << 14)
            .map(|&row| {
                let (start, end) = base.bounds(row as usize, bytes);
                char_len(&bytes[start..end]) as u64
            })
            .max()
            .unwrap_or(0)
    }
}

pub fn max_cols_of(bufs: &[BufRef], spans: &[Span]) -> u64 {
    spans
        .par_iter()
        .with_min_len(1 << 14)
        .map(|span| {
            let buf = &bufs[span.buf() as usize];
            if buf.ascii {
                span.len() as u64
            } else {
                let start = span.off() as usize;
                char_len(&buf.bytes()[start..start + span.len() as usize]) as u64
            }
        })
        .max()
        .unwrap_or(0)
}

#[cfg(test)]
pub(crate) mod testing {
    use super::*;
    use crate::load::stage_from_text;

    /// A stage with the given rows, each of its own source and original row
    pub fn stage_of(lines: &[&str]) -> Stage {
        stage_named(lines, "test")
    }

    pub fn stage_named(lines: &[&str], name: &str) -> Stage {
        stage_from_text(lines.join("\n"), name, name)
    }

    pub fn lines_of(stage: &Stage) -> Vec<String> {
        (0..stage.len()).map(|row| stage.line(row).to_string()).collect()
    }

    pub fn row_ids_of(stage: &Stage) -> Vec<u32> {
        (0..stage.len()).map(|row| stage.row_id(row)).collect()
    }

    pub fn srcs_of(stage: &Stage) -> Vec<u16> {
        (0..stage.len()).map(|row| stage.src(row)).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::testing::*;
    use super::*;

    #[test]
    fn derive_rows_keeps_indexed_representation_and_shares_row_ids() {
        let raw = stage_of(&["alpha", "beta", "gamma", "delta"]);
        let filtered = raw.derive_rows("Filter", vec![3, 1, 2], None);
        assert!(matches!(filtered.rows, Rows::Indexed { .. }));
        assert_eq!(lines_of(&filtered), ["delta", "beta", "gamma"]);
        assert_eq!(row_ids_of(&filtered), [3, 1, 2]);
        assert_eq!(filtered.max_cols, 5);
        // Only the 4-byte row index list is owned (shared with row_ids)
        assert_eq!(filtered.own_bytes, 3 * 4);
        assert_eq!(filtered.text_bytes(), 5 + 4 + 5 + 2);

        // Chaining another row-reordering operation stays indexed and still shares row_ids
        let sorted = filtered.derive_rows("Sort", vec![1, 0, 2], Some(filtered.max_cols));
        assert!(matches!(sorted.rows, Rows::Indexed { .. }));
        assert_eq!(lines_of(&sorted), ["beta", "delta", "gamma"]);
        assert_eq!(row_ids_of(&sorted), [1, 3, 2]);
        assert_eq!(sorted.own_bytes, 3 * 4);

        let copied = sorted.copy_as("Copy");
        assert_eq!(copied.own_bytes, 0);
        assert_eq!(lines_of(&copied), ["beta", "delta", "gamma"]);
    }
}
