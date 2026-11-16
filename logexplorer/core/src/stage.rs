//! A stage: the rows of the view after some operations, as spans into shared buffers.

use crate::text::{as_str, char_len, Buf, BufRef, Span, ARENA};
use rayon::prelude::*;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

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

    fn heap_bytes(&self) -> usize {
        match self {
            Starts::U32(list) => list.capacity() * 4,
            Starts::U64(list) => list.capacity() * 8,
        }
    }
}

/// The consecutive lines of one region of a buffer: 4 bytes a row for files under 4 GiB.
pub struct LineIndex {
    pub buf: u16,
    /// Offset of the first byte of each line
    pub starts: Starts,
    /// End of the region (the end of the last line)
    pub end: u64,
}

impl LineIndex {
    /// Byte range of line `index` without its terminator (`\n`, `\r\n` or a lone `\r`)
    #[inline]
    pub fn bounds(&self, index: usize, bytes: &[u8]) -> (usize, usize) {
        let start = self.starts.get(index) as usize;
        if index + 1 == self.starts.len() {
            return (start, self.end as usize);
        }
        let mut end = self.starts.get(index + 1) as usize - 1;
        if bytes[end] == b'\n' && end > start && bytes[end - 1] == b'\r' {
            end -= 1;
        }
        (start, end)
    }
}

pub enum Rows {
    Lines(LineIndex),
    Spans(Vec<Span>),
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
            Rows::Lines(index) => index.starts.len(),
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

    /// Bytes of all rows joined by `\n`
    pub fn text_bytes(&self) -> u64 {
        let rows = self.len();
        let content: u64 = match &self.rows {
            Rows::Lines(index) => {
                let bytes = self.bufs[index.buf as usize].bytes();
                (0..rows)
                    .into_par_iter()
                    .with_min_len(1 << 14)
                    .map(|row| {
                        let (start, end) = index.bounds(row, bytes);
                        (end - start) as u64
                    })
                    .sum()
            }
            Rows::Spans(spans) => spans.par_iter().with_min_len(1 << 14).map(|span| span.len() as u64).sum(),
        };
        content + rows.saturating_sub(1) as u64
    }

    /// The stage of a loaded source: the consecutive lines of one buffer.
    pub fn from_lines(buf: BufRef, starts: Starts, end: u64, max_cols: u64, source: SourceDesc) -> Stage {
        let own_bytes = starts.heap_bytes();
        Stage {
            id: next_stage_id(),
            label: "Raw",
            bufs: vec![buf],
            rows: Rows::Lines(LineIndex { buf: 0, starts, end }),
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
        let max_cols = max_cols_of(&bufs, &spans);
        let mut own_bytes = spans.capacity() * std::mem::size_of::<Span>();
        if let SrcIds::PerRow(list) = &src {
            own_bytes += list.capacity() * 2;
        }
        if let RowIds::List(list) = &row_ids {
            own_bytes += list.capacity() * 4;
        }
        Stage { id: next_stage_id(), label, bufs, rows: Rows::Spans(spans), src, row_ids, sources, max_cols, own_bytes, diff_meta: None }
    }

    /// The result of an operation on `self`.
    pub fn derive(&self, label: &'static str, out: OutRows) -> Stage {
        let OutRows { mut spans, origin, arena } = out;
        let mut bufs = self.bufs.clone();
        let mut arena_bytes = 0;
        if !arena.is_empty() {
            let arena_index = bufs.len() as u16;
            let mut arena = arena;
            arena.shrink_to_fit();
            arena_bytes = arena.capacity();
            bufs.push(Buf::new(arena));
            spans.par_iter_mut().with_min_len(1 << 14).for_each(|span| {
                if span.buf() == ARENA {
                    *span = span.with_buf(arena_index);
                }
            });
        } else {
            debug_assert!(spans.iter().all(|span| span.buf() != ARENA));
        }
        let (src, row_ids) = match &origin {
            // Same rows in the same order: the ids are shared with the input
            None => {
                debug_assert_eq!(spans.len(), self.len());
                (self.src.clone(), self.row_ids.clone())
            }
            Some(origin) => {
                let src = match &self.src {
                    // A row that comes from no row (an empty Join) has no source either
                    SrcIds::Uniform(id) if origin.par_iter().with_min_len(1 << 14).any(|&row| row == NO_ROW) => {
                        SrcIds::PerRow(Arc::new(origin.iter().map(|&row| if row == NO_ROW { NO_SRC } else { *id }).collect()))
                    }
                    SrcIds::Uniform(id) => SrcIds::Uniform(*id),
                    SrcIds::PerRow(list) => {
                        SrcIds::PerRow(Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| list.get(row as usize).copied().unwrap_or(NO_SRC)).collect()))
                    }
                };
                let row_ids = match &self.row_ids {
                    RowIds::Identity => RowIds::List(Arc::new(origin.clone())),
                    RowIds::List(list) => RowIds::List(Arc::new(origin.par_iter().with_min_len(1 << 14).map(|&row| list.get(row as usize).copied().unwrap_or(NO_ROW)).collect())),
                };
                (src, row_ids)
            }
        };
        let shared_ids = origin.is_none();
        let mut stage = Stage::from_spans(label, bufs, spans, src, row_ids, self.sources.clone());
        if shared_ids {
            // from_spans counted the id lists this stage only borrows
            stage.own_bytes = match &stage.rows {
                Rows::Spans(spans) => spans.capacity() * std::mem::size_of::<Span>(),
                Rows::Lines(_) => 0,
            };
        }
        stage.own_bytes += arena_bytes;
        stage
    }

    /// The same rows as a new stage (an operation with nothing to do).
    pub fn copy_as(&self, label: &'static str) -> Stage {
        let spans: Vec<Span> = (0..self.len()).into_par_iter().with_min_len(1 << 14).map(|row| self.span(row)).collect();
        self.derive(label, OutRows { spans, origin: None, arena: Vec::new() })
    }
}

/// Rows an operation produced.
#[derive(Default)]
pub struct OutRows {
    /// Spans into the input's buffers, or into `arena` (buffer [`ARENA`])
    pub spans: Vec<Span>,
    /// Input row each output row comes from ([`NO_ROW`] for none); `None` when the rows are
    /// the input rows one to one
    pub origin: Option<Vec<u32>>,
    /// Text written by the operation
    pub arena: Vec<u8>,
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
