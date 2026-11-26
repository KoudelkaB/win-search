//! The operations of the pipeline. Each takes a stage and gives a new one that shares the
//! text it did not change.

pub mod diff;
pub mod extract;
pub mod filter;
pub mod join;
pub mod replace;
pub mod sort;
pub mod unique;

use crate::cancel::{Cancel, OpError, OpResult};
use crate::operator::{OpKind, Operator};
use crate::stage::{OutRows, Stage, NO_ROW};
use crate::text::{as_str, char_len, Span, ARENA};
use rayon::prelude::*;
use std::ops::Range;

/// The stage after `operator`.
pub fn apply(stage: &std::sync::Arc<Stage>, operator: &Operator, cancel: &Cancel) -> OpResult<Stage> {
    // Materialize a dense line table before parallel workers enter `for_each_row` on a large
    // `Rows::Indexed` stage, so `OnceLock` never stalls rayon workers during `build_dense`.
    if matches!(stage.rows, crate::stage::Rows::Indexed { .. }) && stage.len() >= 16_384 {
        stage.ensure_random_access();
    }
    match &operator.kind {
        OpKind::Filter(op) => filter::apply(stage, op, cancel),
        OpKind::Replace(op) => replace::apply(stage, op, cancel),
        OpKind::Sort(op) => sort::apply(stage, op, cancel),
        OpKind::Diff(op) => diff::apply(stage, op, cancel),
        OpKind::Extract(op) => extract::apply(stage, op, cancel),
        OpKind::Unique(op) => unique::apply(stage, op, cancel),
        OpKind::Join(op) => join::apply(stage, op, cancel),
    }
}

/// Row ranges small enough to balance the threads and to notice a cancel soon
pub(crate) fn chunk_ranges(rows: usize) -> Vec<Range<usize>> {
    let size = (rows / (rayon::current_num_threads() * 16)).clamp(2048, 1 << 16);
    (0..rows).step_by(size).map(|start| start..(start + size).min(rows)).collect()
}

/// Runs `work` over the chunks of `rows` in parallel; the results are in row order.
pub(crate) fn par_chunks<T: Send>(rows: usize, cancel: &Cancel, work: impl Fn(Range<usize>) -> OpResult<T> + Sync) -> OpResult<Vec<T>> {
    chunk_ranges(rows)
        .into_par_iter()
        .map(|range| {
            cancel.check()?;
            work(range)
        })
        .collect()
}

/// Rows produced by one chunk; spans of buffer [`ARENA`] point into its own `arena`.
#[derive(Default)]
pub(crate) struct Chunk {
    pub spans: Vec<Span>,
    pub origin: Vec<u32>,
    pub arena: Vec<u8>,
    pub max_cols: u64,
    pub arena_non_ascii: bool,
}

pub(crate) fn merge_chunks(chunks: Vec<Chunk>, keep_origin: bool, arena_buf: u16) -> OpResult<OutRows> {
    let rows: usize = chunks.iter().map(|chunk| chunk.spans.len()).sum();
    if rows > u32::MAX as usize - 1 {
        return Err(OpError::msg("The result has too many rows."));
    }
    let total_arena: usize = chunks.iter().map(|chunk| chunk.arena.len()).sum();
    let max_cols = chunks.iter().map(|chunk| chunk.max_cols).max().unwrap_or(0);
    let arena_ascii = !chunks.iter().any(|chunk| chunk.arena_non_ascii);
    let mut out = OutRows {
        spans: Vec::with_capacity(rows),
        origin: keep_origin.then(|| Vec::with_capacity(rows)),
        arena: Vec::new(),
        max_cols: Some(max_cols),
        arena_resolved: true,
        arena_ascii: (total_arena > 0).then_some(arena_ascii),
    };
    out.arena.reserve_exact(total_arena);
    for chunk in chunks {
        let base = out.arena.len() as u64;
        out.arena.extend_from_slice(&chunk.arena);
        if total_arena > 0 {
            out.spans.extend(chunk.spans.iter().map(|span| if span.buf() == ARENA { span.shifted(base).with_buf(arena_buf) } else { *span }));
        } else {
            out.spans.extend_from_slice(&chunk.spans);
        }
        if let Some(origin) = &mut out.origin {
            origin.extend_from_slice(&chunk.origin);
        }
    }
    Ok(out)
}

/// Builds rows from pieces of existing rows and new text. A row made of exactly one piece
/// of an existing row stays a span of it; only mixed rows are written out.
pub(crate) struct Assembler<'a> {
    stage: &'a Stage,
    pub out: Chunk,
    line_start: usize,
    single: Option<Span>,
    spilled: bool,
    origin: u32,
    keep_origin: bool,
    /// A row grew past what a span can hold
    pub overflow: bool,
}

impl<'a> Assembler<'a> {
    pub fn with_origin(stage: &'a Stage, keep_origin: bool) -> Self {
        Assembler { stage, out: Chunk::default(), line_start: 0, single: None, spilled: false, origin: NO_ROW, keep_origin, overflow: false }
    }

    fn spill(&mut self) {
        if let Some(span) = self.single.take() {
            self.out.arena.extend_from_slice(self.stage.span_bytes(span));
        }
        self.spilled = true;
    }

    /// A piece of an existing row
    pub fn push_span(&mut self, span: Span, origin: u32) {
        if span.is_empty() {
            return;
        }
        if self.origin == NO_ROW {
            self.origin = origin;
        }
        if !self.spilled && self.single.is_none() {
            self.single = Some(span);
            return;
        }
        self.spill();
        self.out.arena.extend_from_slice(self.stage.span_bytes(span));
    }

    /// New text without line breaks
    pub fn push_literal(&mut self, text: &[u8], origin: u32) {
        if text.is_empty() {
            return;
        }
        if self.origin == NO_ROW {
            self.origin = origin;
        }
        self.spill();
        self.out.arena.extend_from_slice(text);
    }

    /// New text; its line breaks end rows
    pub fn push_text(&mut self, text: &[u8], origin: u32) {
        let mut rest = text;
        while let Some(at) = memchr::memchr(b'\n', rest) {
            self.push_literal(&rest[..at], origin);
            self.end_row(origin);
            rest = &rest[at + 1..];
        }
        self.push_literal(rest, origin);
    }

    /// Ends the row; an empty row belongs to `fallback_origin`
    pub fn end_row(&mut self, fallback_origin: u32) {
        let (span, cols) = if self.spilled {
            let slice = &self.out.arena[self.line_start..];
            let length = slice.len();
            self.overflow |= length > u32::MAX as usize;
            let cols = if slice.is_ascii() {
                length as u64
            } else {
                self.out.arena_non_ascii = true;
                char_len(slice) as u64
            };
            (Span::new(ARENA, self.line_start as u64, length as u32), cols)
        } else {
            let span = self.single.take().unwrap_or(Span::EMPTY);
            let cols = if span.is_empty() {
                0
            } else if self.stage.bufs[span.buf() as usize].ascii {
                span.len() as u64
            } else {
                char_len(self.stage.span_bytes(span)) as u64
            };
            (span, cols)
        };
        self.out.max_cols = self.out.max_cols.max(cols);
        self.out.spans.push(span);
        if self.keep_origin {
            self.out.origin.push(if self.origin == NO_ROW { fallback_origin } else { self.origin });
        }
        self.line_start = self.out.arena.len();
        self.spilled = false;
        self.origin = NO_ROW;
    }

    pub fn finish(self) -> OpResult<Chunk> {
        if self.overflow {
            return Err(OpError::msg("A row of the result is longer than 4 GiB."));
        }
        Ok(self.out)
    }
}

/// The span without the whitespace around its text
pub(crate) fn trim_span(stage: &Stage, span: Span) -> Span {
    let text = as_str(stage.span_bytes(span));
    let trimmed = text.trim();
    if trimmed.len() == text.len() {
        return span;
    }
    let start = trimmed.as_ptr() as usize - text.as_ptr() as usize;
    span.sub(start, start + trimmed.len())
}
