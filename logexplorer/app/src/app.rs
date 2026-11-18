//! The state of the application and everything it does; the drawing is in `panels` and `viewer`.

use crate::storage::Storage;
use logexplorer_core::cancel::{Cancel, OpError};
use logexplorer_core::find::{find_backward, find_forward};
use logexplorer_core::i18n::{grouped, I18n};
use logexplorer_core::literal::Needle;
use logexplorer_core::load::{self, Bundle, LoadProgress, TextSource};
use logexplorer_core::operator::{create_default, Clause, OpKind, OpType, Operator};
use logexplorer_core::ops::diff::uses_expanded_output;
use logexplorer_core::ops::filter::FilterMatcher;
use logexplorer_core::pipeline::{active_operators, cached_stage, compute, StageCache};
use logexplorer_core::profiles::{ProfileStore, WorkingSet, IMPLICIT_PROFILE_NAME};
use logexplorer_core::selection::{block_text, column_text, prefill, selection_chars, selection_text, stage_text, Point, Selection};
use logexplorer_core::stage::Stage;
use std::path::PathBuf;
use std::sync::mpsc::{channel, Receiver};
use std::sync::{Arc, Mutex};

/// How new files join what is loaded.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LoadAction {
    /// Show only the new file
    Replace,
    /// Only the new files, one after another
    ReplaceAppend,
    /// Only the new files, interleaved row by row
    ReplaceZip,
    /// After the loaded rows
    Append,
    /// Interleaved with the loaded rows
    Zip,
}

/// What the command line (an embedding host) asks for.
#[derive(Clone, Debug, Default)]
pub struct Options {
    pub files: Vec<PathBuf>,
    /// Several files without asking: one after another (`false`) or interleaved (`true`)
    pub combine_zip: Option<bool>,
    /// Start the pipeline with this include filter: `(text, case sensitive)`
    pub filter: Option<(String, bool)>,
    pub language: Option<String>,
    /// Where profiles are kept; `None` keeps nothing (tests)
    pub data_dir: Option<PathBuf>,
    /// Compute and load on the UI thread, for deterministic tests
    pub synchronous: bool,
}

pub struct Source {
    pub name: String,
    pub encoding: String,
    pub raw: Arc<Stage>,
    pub chars: u64,
}

#[derive(Default)]
pub struct View {
    /// Pixels from the top of the first row; negative when scrolled past the start
    pub scroll_top: f64,
    pub scroll_left: usize,
    pub row_height: f32,
    pub char_width: f32,
    pub viewport_height: f32,
    pub viewport_rows: usize,
    pub viewport_cols: usize,
    pub hovered_row: Option<usize>,
}

pub struct VerticalMetrics {
    pub overscroll: f64,
    pub min: f64,
    pub max_content: f64,
    pub max: f64,
}

#[derive(Default)]
pub struct Find {
    pub open: bool,
    pub query: String,
    pub case_sensitive: bool,
    pub status: String,
    pub not_found: bool,
    /// The find box asks for the keyboard on the next frame
    pub focus: bool,
}

/// A question that needs a typed answer or a yes.
pub enum Prompt {
    SaveAs {
        name: String,
    },
    Rename {
        profile: String,
        name: String,
    },
    /// Save `name` over the existing profile of that name; `rename_from` when renaming
    Overwrite {
        name: String,
        rename_from: Option<String>,
    },
}

pub struct PendingLoad {
    pub incoming: Vec<TextSource>,
    pub has_existing: bool,
}

struct ComputeJob {
    chain: String,
    cancel: Cancel,
    result: Arc<Mutex<Option<Result<(), OpError>>>>,
    step: Arc<Mutex<Option<OpType>>>,
}

struct LoadJob {
    receiver: Receiver<Vec<Result<TextSource, String>>>,
    progress: Arc<LoadProgress>,
    cancel: Cancel,
    action: Option<LoadAction>,
    filter: Option<(String, bool)>,
}

pub enum FileDialog {
    Open,
    ImportProfiles,
    ExportView,
    ExportProfiles,
}

pub struct App {
    pub i18n: I18n,
    pub source: Option<Source>,
    pub work: WorkingSet,
    pub profiles: ProfileStore,
    pub loaded_profile: String,
    pub view: View,
    pub selection: Option<Selection>,
    pub cursor: Point,
    pub find: Find,
    pub status: String,
    pub help_open: bool,
    pub pending_load: Option<PendingLoad>,
    pub prompt: Option<Prompt>,
    /// Several files were loaded one after another without asking: offer to interleave them
    pub offer_zip: bool,
    /// The start screen note: progress or a problem while nothing is loaded
    pub drop_note: Option<(String, bool)>,
    /// Text for the clipboard, taken by the UI
    pub clipboard: Option<String>,
    /// Where pasted content is read from when the app handles the clipboard keys itself
    pub clipboard_source: Box<dyn crate::clipboard::ClipboardSource>,
    /// Files pasted in this frame
    pub pasted_files: Option<Vec<PathBuf>>,
    /// Input that arrived behind the key press of a frame and waits for the next one
    pub(crate) waiting_events: Vec<egui::Event>,
    /// A file dialog the UI should open
    pub dialog: Option<FileDialog>,
    /// The viewer asks for the keyboard
    pub focus_viewer: bool,
    pub viewer: crate::viewer::ViewerState,
    /// The window title last sent to the system
    pub(crate) title: String,
    pub(crate) dialog_result: Option<Receiver<(FileDialogKind, Option<Vec<PathBuf>>)>>,
    cache: Arc<Mutex<StageCache>>,
    job: Option<ComputeJob>,
    /// The chain that failed and why: not started again until it changes
    failed: Option<(String, String)>,
    loader: Option<LoadJob>,
    storage: Storage,
    synchronous: bool,
    repaint: Option<egui::Context>,
    selection_chars: Option<(u64, Selection, u64)>,
    preview: Option<(String, Arc<FilterMatcher>)>,
    last_stage: u64,
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum FileDialogKind {
    Open,
    ImportProfiles,
    ExportView,
    ExportProfiles,
}

impl App {
    pub fn new(options: Options) -> Self {
        let requested: Vec<String> =
            options.language.clone().into_iter().chain(["LC_ALL", "LC_MESSAGES", "LANG"].iter().filter_map(|name| std::env::var(name).ok())).chain(system_language()).collect();
        let storage = Storage::new(options.data_dir.clone());
        let mut profiles = storage.load_profiles();
        // The pipeline of the last session waits as the profile "Last"; this one starts empty
        if let Some(last) = storage.load_working_set() {
            profiles.set_last(&last);
        }
        let mut app = App {
            i18n: I18n::new(&I18n::resolve(&requested)),
            source: None,
            work: WorkingSet { next_operator_id: 1, ..Default::default() },
            profiles,
            loaded_profile: String::new(),
            view: View { row_height: 18.0, char_width: 8.0, viewport_rows: 1, viewport_cols: 4, ..Default::default() },
            selection: None,
            cursor: Point::default(),
            find: Find::default(),
            status: String::new(),
            help_open: false,
            pending_load: None,
            prompt: None,
            offer_zip: false,
            drop_note: None,
            clipboard: None,
            clipboard_source: Box::new(crate::clipboard::SystemClipboard),
            pasted_files: None,
            waiting_events: Vec::new(),
            dialog: None,
            focus_viewer: false,
            viewer: Default::default(),
            title: String::new(),
            dialog_result: None,
            cache: Arc::new(Mutex::new(StageCache::default())),
            job: None,
            failed: None,
            loader: None,
            storage,
            synchronous: options.synchronous,
            repaint: None,
            selection_chars: None,
            preview: None,
            last_stage: 0,
        };
        app.status = app.t("Ready.").to_string();
        if !options.files.is_empty() {
            let action = if options.files.len() == 1 {
                Some(LoadAction::Replace)
            } else {
                options.combine_zip.map(|zip| if zip { LoadAction::ReplaceZip } else { LoadAction::ReplaceAppend })
            };
            app.open_paths(options.files, action, options.filter);
        }
        app
    }

    pub fn set_repaint(&mut self, ctx: &egui::Context) {
        if self.repaint.is_none() {
            self.repaint = Some(ctx.clone());
        }
    }

    pub fn t<'a>(&'a self, text: &'a str) -> &'a str {
        self.i18n.t(text)
    }

    pub fn tf(&self, text: &str, args: &[&dyn std::fmt::Display]) -> String {
        self.i18n.tf(text, args)
    }

    pub fn set_status(&mut self, message: &str) {
        self.status = self.t(message).to_string();
    }

    fn set_status_text(&mut self, message: String) {
        self.status = message;
    }

    fn handle_error(&mut self, message: String) {
        if self.source.is_none() {
            self.drop_note = Some((message.clone(), true));
        }
        self.status = message;
    }

    // --- the stage shown ----------------------------------------------------------------------

    fn chain_signature(&self, raw: &Stage) -> String {
        let mut signature = raw.id.to_string();
        for operator in active_operators(&self.work.operators, self.work.selected_index()) {
            signature.push('|');
            signature.push_str(&operator.signature());
        }
        signature
    }

    /// The stage to show: the selected one when it is computed, else the deepest stage on
    /// the way to it while the rest is computed in the background.
    pub fn stage(&mut self) -> Option<Arc<Stage>> {
        let raw = self.source.as_ref()?.raw.clone();
        self.poll_job();
        let signature = self.chain_signature(&raw);
        let (stage, complete) = {
            let chain = active_operators(&self.work.operators, self.work.selected_index());
            cached_stage(&self.cache, &raw, &chain)
        };
        // A chain that failed is not tried again until it changes
        let stage = if complete || self.failed.as_ref().is_some_and(|(chain, _)| *chain == signature) {
            stage
        } else if self.synchronous {
            let chain = active_operators(&self.work.operators, self.work.selected_index());
            match compute(&self.cache, &raw, &chain, &Cancel::new(), &|_| {}) {
                Ok(stage) => stage,
                Err(error) => {
                    let message = self.i18n.error(&error);
                    self.failed = Some((signature, message.clone()));
                    self.handle_error(message);
                    stage
                }
            }
        } else {
            if !self.job.as_ref().is_some_and(|job| job.chain == signature) {
                self.start_job(raw, signature);
            }
            stage
        };
        if complete {
            if let Some(job) = self.job.take() {
                job.cancel.cancel();
            }
        }
        if stage.id != self.last_stage {
            self.last_stage = stage.id;
            self.clamp_to(&stage);
        }
        Some(stage)
    }

    /// The stage shown now, without starting any computation
    pub fn stage_now(&self) -> Option<Arc<Stage>> {
        let raw = &self.source.as_ref()?.raw;
        let chain = active_operators(&self.work.operators, self.work.selected_index());
        Some(cached_stage(&self.cache, raw, &chain).0)
    }

    fn start_job(&mut self, raw: Arc<Stage>, signature: String) {
        if let Some(job) = self.job.take() {
            job.cancel.cancel();
        }
        let chain: Vec<Operator> = active_operators(&self.work.operators, self.work.selected_index()).into_iter().cloned().collect();
        let job = ComputeJob { chain: signature, cancel: Cancel::new(), result: Arc::new(Mutex::new(None)), step: Arc::new(Mutex::new(None)) };
        let (cache, cancel, result, step, repaint) = (self.cache.clone(), job.cancel.clone(), job.result.clone(), job.step.clone(), self.repaint.clone());
        std::thread::Builder::new()
            .name("pipeline".into())
            .spawn(move || {
                let chain: Vec<&Operator> = chain.iter().collect();
                let wake = || {
                    if let Some(ctx) = &repaint {
                        ctx.request_repaint();
                    }
                };
                let on_step = |operator: &Operator| {
                    *step.lock().unwrap() = Some(operator.op_type());
                    wake();
                };
                let outcome = compute(&cache, &raw, &chain, &cancel, &on_step).map(|_| ());
                *result.lock().unwrap() = Some(outcome);
                wake();
            })
            .expect("pipeline thread");
        self.job = Some(job);
    }

    fn poll_job(&mut self) {
        let Some(job) = &self.job else { return };
        let Some(outcome) = job.result.lock().unwrap().take() else { return };
        let chain = job.chain.clone();
        self.job = None;
        match outcome {
            Ok(()) | Err(OpError::Canceled) => {}
            Err(error) => {
                let message = self.i18n.error(&error);
                self.failed = Some((chain, message.clone()));
                self.handle_error(message);
            }
        }
    }

    /// What is being computed, for the status bar
    pub fn busy(&self) -> String {
        if let Some(loader) = &self.loader {
            let (done, total) = (loader.progress.done.load(std::sync::atomic::Ordering::Relaxed), loader.progress.total.load(std::sync::atomic::Ordering::Relaxed));
            return format!("{} {} / {} MB", self.t("Loading..."), done >> 20, total >> 20);
        }
        match self.job.as_ref().and_then(|job| *job.step.lock().unwrap()) {
            Some(op_type) => self.tf("Applying {0}...", &[&self.t(op_type.label())]),
            None => String::new(),
        }
    }

    pub fn is_busy(&self) -> bool {
        self.job.is_some() || self.loader.is_some()
    }

    /// Stages kept computed, and the memory they hold beyond the source
    pub fn cache_info(&self) -> (usize, usize) {
        let cache = self.cache.lock().unwrap();
        (cache.len(), cache.bytes())
    }

    fn clamp_to(&mut self, stage: &Stage) {
        let last_row = stage.len().saturating_sub(1);
        self.cursor.row = self.cursor.row.min(last_row);
        self.cursor.col = self.cursor.col.min(stage.line_cols(self.cursor.row));
        let metrics = self.vertical_metrics(stage);
        self.view.scroll_top = self.view.scroll_top.clamp(metrics.min, metrics.max);
        self.view.scroll_left = self.view.scroll_left.min(self.max_scroll_left(stage));
    }

    /// The next enabled operation after the selected one, when it is a Filter: the rows it
    /// would keep are shown in blue
    pub fn preview_filter(&mut self, stage: &Stage) -> Option<Arc<FilterMatcher>> {
        let from = self.work.selected_index().map_or(0, |index| index + 1);
        let next = self.work.operators.iter().skip(from).find(|operator| operator.enabled)?;
        let OpKind::Filter(filter) = &next.kind else {
            self.preview = None;
            return None;
        };
        let key = format!("{}|{}", Arc::as_ptr(&stage.sources) as usize, next.signature());
        if !self.preview.as_ref().is_some_and(|(cached, _)| *cached == key) {
            self.preview = Some((key, Arc::new(FilterMatcher::new(filter, &stage.sources))));
        }
        self.preview.as_ref().map(|(_, matcher)| matcher.clone())
    }

    // --- scrolling ----------------------------------------------------------------------------

    pub fn vertical_metrics(&self, stage: &Stage) -> VerticalMetrics {
        let row_height = self.view.row_height as f64;
        let viewport = self.view.viewport_height as f64;
        let total = stage.len() as f64 * row_height;
        let overscroll = (viewport - row_height).max(0.0);
        let max_content = (total - viewport).max(0.0);
        VerticalMetrics { overscroll, min: -overscroll, max_content, max: max_content + overscroll }
    }

    pub fn max_scroll_left(&self, stage: &Stage) -> usize {
        (stage.max_cols as usize).saturating_sub(self.view.viewport_cols)
    }

    pub fn set_scroll_top(&mut self, stage: &Stage, scroll_top: f64) {
        let metrics = self.vertical_metrics(stage);
        self.view.scroll_top = scroll_top.clamp(metrics.min, metrics.max);
    }

    pub fn set_scroll_left(&mut self, stage: &Stage, cols: i64) {
        self.view.scroll_left = cols.clamp(0, self.max_scroll_left(stage) as i64) as usize;
    }

    /// The wheel: never past the content, unless the view already is (after a jump between stages)
    pub fn scroll_by(&mut self, stage: &Stage, delta: f64) {
        let metrics = self.vertical_metrics(stage);
        let current = self.view.scroll_top;
        let next = current + delta;
        self.view.scroll_top = if current < 0.0 {
            next.max(current).min(0.0)
        } else if current > metrics.max_content {
            next.min(current).max(metrics.max_content)
        } else {
            next.clamp(0.0, metrics.max_content)
        };
    }

    fn ensure_visible(&mut self, stage: &Stage, row: usize, col: usize) {
        let row_height = self.view.row_height as f64;
        let viewport = self.view.viewport_height as f64;
        let (top, bottom) = (row as f64 * row_height, (row + 1) as f64 * row_height);
        if top < self.view.scroll_top {
            self.set_scroll_top(stage, top);
        } else if bottom > self.view.scroll_top + viewport {
            self.set_scroll_top(stage, bottom - viewport);
        }
        if col < self.view.scroll_left {
            self.view.scroll_left = col;
        } else if col + 1 > self.view.scroll_left + self.view.viewport_cols {
            self.view.scroll_left = (col + 1).saturating_sub(self.view.viewport_cols);
        }
    }

    // --- selection and cursor -----------------------------------------------------------------

    pub fn set_cursor(&mut self, stage: &Stage, point: Point) {
        let row = point.row.min(stage.len().saturating_sub(1));
        self.cursor = Point::new(row, point.col.min(stage.line_cols(row)));
    }

    fn selection_anchor(&self) -> Point {
        self.selection.map_or(self.cursor, |selection| selection.normalized().start)
    }

    pub fn clear_selection(&mut self) {
        self.selection = None;
    }

    pub fn move_cursor(&mut self, delta_row: i64, delta_col: i64, extend: bool) {
        let Some(stage) = self.stage_now() else { return };
        let row = (self.cursor.row as i64 + delta_row).clamp(0, stage.len().saturating_sub(1) as i64) as usize;
        let col = (self.cursor.col as i64 + delta_col).clamp(0, stage.line_cols(row) as i64) as usize;
        self.move_cursor_to(&stage, Point::new(row, col), extend);
    }

    pub fn move_cursor_to(&mut self, stage: &Stage, target: Point, extend: bool) {
        let row = target.row.min(stage.len().saturating_sub(1));
        let point = Point::new(row, target.col.min(stage.line_cols(row)));
        if extend {
            self.selection = Some(Selection::new(self.selection_anchor(), point));
        }
        self.cursor = point;
        self.ensure_visible(stage, point.row, point.col);
    }

    pub fn move_to_edge(&mut self, end: bool, extend: bool) {
        let Some(stage) = self.stage_now() else { return };
        let target = if end { Point::new(stage.len().saturating_sub(1), usize::MAX) } else { Point::default() };
        self.move_cursor_to(&stage, target, extend);
    }

    pub fn select_whole_stage(&mut self) {
        let Some(stage) = self.stage_now().filter(|stage| !stage.is_empty()) else { return };
        let last = Point::new(stage.len() - 1, stage.line_cols(stage.len() - 1));
        self.selection = Some(Selection::new(Point::default(), last));
        self.cursor = last;
    }

    /// Characters of the selection, counted once per selection
    pub fn selection_len(&mut self, stage: &Stage) -> u64 {
        let Some(selection) = self.selection else { return 0 };
        match self.selection_chars {
            Some((id, cached, chars)) if id == stage.id && cached == selection => chars,
            _ => {
                let chars = selection_chars(stage, &selection);
                self.selection_chars = Some((stage.id, selection, chars));
                chars
            }
        }
    }

    // --- operations ---------------------------------------------------------------------------

    fn changed(&mut self) {
        self.storage.save_working_set(&self.work);
        self.failed = None;
    }

    fn insert_after_selected(&mut self, operators: Vec<Operator>, select: usize) {
        let at = self.work.selected_index().map_or(self.work.operators.len(), |index| index + 1);
        self.work.selected_operator_id = operators.get(select).map(|operator| operator.id);
        for (offset, operator) in operators.into_iter().enumerate() {
            self.work.operators.insert(at + offset, operator);
        }
    }

    pub fn add_operator(&mut self, op_type: OpType) {
        let stage = self.stage_now();
        let filled = prefill(stage.as_deref(), self.selection.as_ref());
        let id = self.work.take_id();
        let mut operator = create_default(id, op_type, Some(&filled));
        if let OpKind::Diff(diff) = &mut operator.kind {
            if filled.has_selection && uses_expanded_output(diff.value_type) {
                diff.output_extend = Some(self.expand_selection_for_diff(stage.as_deref()).to_string());
            }
        }
        self.insert_after_selected(vec![operator], 0);
        self.changed();
    }

    /// A numeric diff needs one more column for its sign: taken left of the selection when
    /// there is a space, else right of it
    fn expand_selection_for_diff(&mut self, stage: Option<&Stage>) -> &'static str {
        let (Some(stage), Some(selection)) = (stage, self.selection.map(|selection| selection.normalized())) else { return "right" };
        let space_on_the_left = selection.start.col > 0
            && (selection.start.row..=selection.end.row.min(stage.len().saturating_sub(1)))
                .all(|row| logexplorer_core::text::slice_cols(stage.line(row), selection.start.col - 1, selection.start.col).chars().all(char::is_whitespace));
        if space_on_the_left {
            self.selection = Some(Selection::new(Point::new(selection.start.row, selection.start.col - 1), selection.end));
            "left"
        } else {
            self.selection = Some(Selection::new(selection.start, Point::new(selection.end.row, selection.end.col + 1)));
            "right"
        }
    }

    pub fn add_collect_pipeline(&mut self) {
        let stage = self.stage_now();
        let filled = prefill(stage.as_deref(), self.selection.as_ref());
        let operators = [OpType::Extract, OpType::Unique, OpType::Join].map(|op_type| create_default(self.work.take_id(), op_type, Some(&filled))).to_vec();
        self.insert_after_selected(operators, 0);
        self.changed();
        self.set_status("Added Extract, Unique, and Join. Configure Extract, then select Join for the final value.");
    }

    pub fn delete_operator(&mut self, id: u64) {
        let Some(index) = self.work.index_of(id) else { return };
        self.work.operators.remove(index);
        if self.work.selected_operator_id == Some(id) {
            let fallback = self.work.operators.get(index.saturating_sub(1)).or_else(|| self.work.operators.get(index));
            self.work.selected_operator_id = fallback.map(|operator| operator.id);
        }
        self.changed();
    }

    pub fn duplicate_operator(&mut self, id: u64) {
        let Some(index) = self.work.index_of(id) else { return };
        let mut copy = self.work.operators[index].clone();
        copy.id = self.work.take_id();
        self.work.selected_operator_id = Some(copy.id);
        self.work.operators.insert(index + 1, copy);
        self.changed();
    }

    pub fn move_operator(&mut self, id: u64, delta: i64) {
        let Some(index) = self.work.index_of(id) else { return };
        let target = index as i64 + delta;
        if target < 0 || target >= self.work.operators.len() as i64 {
            return;
        }
        let operator = self.work.operators.remove(index);
        self.work.operators.insert(target as usize, operator);
        self.changed();
    }

    /// Called after the UI edited an operation in place
    pub fn operator_edited(&mut self) {
        self.changed();
    }

    pub fn select_operator(&mut self, id: Option<u64>) {
        if self.work.selected_operator_id != id {
            self.work.selected_operator_id = id;
            self.storage.save_working_set(&self.work);
        }
    }

    /// The header of the panel switches between the source and the last operation
    pub fn toggle_raw(&mut self) {
        let next = if self.work.selected_operator_id.is_none() { self.work.operators.last().map(|operator| operator.id) } else { None };
        self.select_operator(next);
    }

    /// Enter and Esc: the selection becomes an include or exclude text of the selected
    /// Filter, or of a new Filter after the selected operation
    pub fn apply_selection_to_filter(&mut self, exclude: bool) -> bool {
        let stage = self.stage_now();
        let filled = prefill(stage.as_deref(), self.selection.as_ref());
        if filled.selected_text.is_empty() {
            return false;
        }
        let clause = Clause::new(&filled.selected_text);
        if let Some(OpKind::Filter(filter)) = self.work.selected_index().map(|index| &mut self.work.operators[index].kind) {
            if exclude {
                filter.exclude.push(clause)
            } else {
                filter.include.push(clause)
            }
            self.changed();
            return true;
        }
        let id = self.work.take_id();
        let mut operator = create_default(id, OpType::Filter, Some(&filled));
        if exclude {
            if let OpKind::Filter(filter) = &mut operator.kind {
                filter.include.clear();
                filter.exclude.push(clause);
            }
        }
        self.insert_after_selected(vec![operator], 0);
        self.changed();
        true
    }

    /// Delete: a Replace that removes the selected text
    pub fn add_delete_replace_from_selection(&mut self) {
        let stage = self.stage_now();
        let filled = prefill(stage.as_deref(), self.selection.as_ref());
        let id = self.work.take_id();
        let operator = create_default(id, OpType::Replace, Some(&filled));
        self.insert_after_selected(vec![operator], 0);
        self.changed();
    }

    /// The text a new filter clause starts with
    pub fn selected_text(&self) -> String {
        let stage = self.stage_now();
        prefill(stage.as_deref(), self.selection.as_ref()).selected_text
    }

    /// Double-click with several files loaded: a Filter that keeps only the row's file
    pub fn add_source_filter_for_row(&mut self, row: usize) -> bool {
        let Some(stage) = self.stage_now().filter(|stage| stage.multiple_sources()) else { return false };
        let Some(source) = stage.source_of(row) else { return false };
        let (key, label) = (source.filter_key(), source.display().to_string());
        let id = self.work.take_id();
        let mut operator = create_default(id, OpType::Filter, None);
        if let OpKind::Filter(filter) = &mut operator.kind {
            filter.include.clear();
            filter.sources = vec![key];
        }
        self.insert_after_selected(vec![operator], 0);
        self.changed();
        self.set_status_text(self.tf("Added source filter for '{0}'.", &[&label]));
        true
    }

    // --- copying ------------------------------------------------------------------------------

    fn copy(&mut self, text: String, done: &str) {
        if text.is_empty() {
            self.set_status("Nothing to copy.");
            return;
        }
        self.clipboard = Some(text);
        self.set_status(done);
    }

    pub fn copy_selection(&mut self) {
        if let (Some(stage), Some(selection)) = (self.stage_now(), self.selection) {
            self.copy(selection_text(&stage, &selection), "Copied selection.");
        }
    }

    pub fn copy_view(&mut self) {
        if let Some(stage) = self.stage_now() {
            self.copy(stage_text(&stage), "Copied current view.");
        }
    }

    pub fn copy_column(&mut self) {
        if let (Some(stage), Some(selection)) = (self.stage_now(), self.selection) {
            self.copy(column_text(&stage, &selection), "Copied column across view.");
        }
    }

    pub fn copy_block(&mut self) {
        if let (Some(stage), Some(selection)) = (self.stage_now(), self.selection) {
            self.copy(block_text(&stage, &selection), "Copied selected block.");
        }
    }

    pub fn export_view_to(&mut self, path: &std::path::Path) {
        let Some(stage) = self.stage_now() else { return };
        let written = std::fs::File::create(path).and_then(|mut file| logexplorer_core::selection::write_stage(&stage, &mut file));
        match written {
            Ok(()) => self.set_status_text(format!("{} {}", self.t("⇩ Download view"), path.display())),
            Err(error) => self.handle_error(error.to_string()),
        }
    }

    pub fn export_view_name(&self) -> String {
        format!("{}.view.txt", self.source.as_ref().map_or("log", |source| source.name.as_str()))
    }

    pub fn reset_view(&mut self) {
        self.work.selected_operator_id = self.work.operators.last().map(|operator| operator.id);
        self.view.scroll_top = 0.0;
        self.view.scroll_left = 0;
        self.cursor = Point::default();
        self.clear_selection();
        self.storage.save_working_set(&self.work);
    }

    // --- find ---------------------------------------------------------------------------------

    fn single_row_selection_text(&self, stage: &Stage) -> String {
        match self.selection.map(|selection| selection.normalized()) {
            Some(selection) if selection.start.row == selection.end.row && selection.start.col != selection.end.col => selection_text(stage, &selection),
            _ => String::new(),
        }
    }

    fn set_find_status(&mut self, message: &str, not_found: bool) {
        self.find.status = self.t(message).to_string();
        self.find.not_found = not_found;
    }

    pub fn show_find_bar(&mut self, focus: bool, use_selection: bool) {
        if use_selection {
            let selected = self.stage_now().map(|stage| self.single_row_selection_text(&stage)).unwrap_or_default();
            if !selected.is_empty() {
                self.find.query = selected;
            }
        }
        self.find.open = true;
        self.find.focus |= focus;
        self.set_find_status(if self.find.query.is_empty() { "Enter text to find" } else { "Enter to find" }, false);
    }

    pub fn close_find_bar(&mut self) {
        self.find.open = false;
        self.focus_viewer = true;
    }

    pub fn find_query_changed(&mut self) {
        self.set_find_status(if self.find.query.is_empty() { "Enter text to find" } else { "Enter to find" }, false);
    }

    pub fn toggle_find_case(&mut self) {
        self.find.case_sensitive = !self.find.case_sensitive;
        self.find_query_changed();
        self.find.focus = true;
    }

    pub fn find_in_view(&mut self, forward: bool) -> bool {
        let Some(stage) = self.stage_now() else {
            self.set_find_status("No file view", true);
            return false;
        };
        if self.find.query.is_empty() {
            self.show_find_bar(true, false);
            self.set_find_status("Enter text to find", true);
            return false;
        }
        let from = match self.selection.map(|selection| selection.normalized()) {
            Some(selection) => {
                if forward {
                    selection.end
                } else {
                    selection.start
                }
            }
            None => self.cursor,
        };
        let row = from.row.min(stage.len().saturating_sub(1));
        let from = Point::new(row, from.col.min(stage.line_cols(row)));
        let needle = Needle::new(&self.find.query, self.find.case_sensitive);
        let found = if forward { find_forward(&stage, &needle, from) } else { find_backward(&stage, &needle, from) };
        let Some(found) = found else {
            self.set_find_status("No matches", true);
            self.set_status_text(self.tf("No match for '{0}'.", &[&self.find.query]));
            return false;
        };
        self.selection = Some(Selection::new(Point::new(found.row, found.start_col), Point::new(found.row, found.end_col)));
        self.cursor = Point::new(found.row, found.end_col);
        // The whole match in view; its start when it is wider than the view
        self.ensure_visible(&stage, found.row, found.start_col);
        let width = found.end_col.saturating_sub(found.start_col).max(1);
        if width >= self.view.viewport_cols || found.start_col < self.view.scroll_left {
            self.view.scroll_left = found.start_col;
        } else if found.end_col > self.view.scroll_left + self.view.viewport_cols {
            self.view.scroll_left = found.end_col.saturating_sub(self.view.viewport_cols);
        }
        let (row_text, col_text) = (grouped(found.row as u64 + 1), grouped(found.start_col as u64 + 1));
        let mut status = self.tf("Row {0}, col {1}", &[&row_text, &col_text]);
        if found.wrapped {
            status = format!("{status} \u{b7} {}", self.t("wrapped"));
        }
        self.find.status = status;
        self.find.not_found = false;
        self.set_status_text(self.tf("Found '{0}' at row {1}, column {2}.", &[&self.find.query, &row_text, &col_text]));
        true
    }

    /// Ctrl+F3: find the selected text
    pub fn find_from_selection(&mut self, forward: bool) -> bool {
        let selected = self.stage_now().map(|stage| self.single_row_selection_text(&stage)).unwrap_or_default();
        if !selected.is_empty() {
            self.find.query = selected;
        }
        self.show_find_bar(false, false);
        if self.find.query.is_empty() {
            self.show_find_bar(true, false);
            self.set_find_status("Select text or enter a query", true);
            return false;
        }
        self.find_in_view(forward)
    }

    // --- stages by the pointer ----------------------------------------------------------------

    /// Middle click: the previous (or next) stage with the row under the pointer kept under
    /// the pointer. `offset_y` is the pointer's distance from the top of the view, `cols`
    /// its distance from the first shown column.
    pub fn navigate_stage(&mut self, point: Point, direction: i64, offset_y: f32, cols: usize) {
        let Some(raw) = self.source.as_ref().map(|source| source.raw.clone()) else { return };
        let current = self.work.selected_index().map_or(-1, |index| index as i64);
        let target = (current + direction).clamp(-1, self.work.operators.len() as i64 - 1);
        if target == current {
            return;
        }
        let Some(current_stage) = self.stage_now() else { return };
        let row_id = current_stage.row_id(point.row);
        let target_index = (target >= 0).then_some(target as usize);
        let target_stage = {
            let chain = active_operators(&self.work.operators, target_index);
            match compute(&self.cache, &raw, &chain, &Cancel::new(), &|_| {}) {
                Ok(stage) => stage,
                Err(error) => {
                    let message = self.i18n.error(&error);
                    self.handle_error(message);
                    return;
                }
            }
        };
        let row = find_row_by_id(&target_stage, row_id, point.row);
        let col = point.col.min(target_stage.line_cols(row));
        self.work.selected_operator_id = target_index.map(|index| self.work.operators[index].id);
        self.selection = None;
        self.set_scroll_top(&target_stage, row as f64 * self.view.row_height as f64 - (offset_y - self.view.row_height / 2.0) as f64);
        self.set_scroll_left(&target_stage, col as i64 - cols as i64);
        self.set_cursor(&target_stage, Point::new(row, col));
        self.last_stage = target_stage.id;
        self.storage.save_working_set(&self.work);
        self.set_status(if direction < 0 { "Moved to previous stage." } else { "Moved to next stage." });
    }

    // --- profiles -----------------------------------------------------------------------------

    fn persist_profiles(&mut self) {
        self.storage.save_profiles(&mut self.profiles);
    }

    fn apply_working_set(&mut self, set: WorkingSet, name: &str) {
        self.work = set;
        self.loaded_profile = name.to_string();
        self.changed();
    }

    /// The "New" chip: an empty pipeline, the loaded files stay
    pub fn clear_workspace(&mut self) {
        self.work = WorkingSet { next_operator_id: 1, ..Default::default() };
        self.selection = None;
        self.view.scroll_top = 0.0;
        self.view.scroll_left = 0;
        self.cursor = Point::default();
        self.loaded_profile.clear();
        self.changed();
        self.set_status(if self.source.is_some() { "Cleared current profile state." } else { "Cleared current state." });
    }

    pub fn load_profile(&mut self, name: &str) -> bool {
        let Some(profile) = self.profiles.get(name).cloned() else { return false };
        self.apply_working_set(profile.set, name);
        self.set_status_text(self.tf("Loaded profile '{0}'.", &[&name]));
        true
    }

    pub fn save_profile(&mut self, name: &str) {
        if self.profiles.save(name, &self.work) {
            self.persist_profiles();
            self.set_status_text(self.tf("Saved profile '{0}'.", &[&name]));
        }
    }

    pub fn delete_profile(&mut self, name: &str) {
        if self.profiles.delete(name) {
            if self.loaded_profile == name {
                self.loaded_profile.clear();
            }
            self.persist_profiles();
            self.set_status_text(self.tf("Deleted profile '{0}'.", &[&name]));
        }
    }

    pub fn is_profile_dirty(&self, name: &str) -> bool {
        self.loaded_profile == name && self.profiles.is_dirty(name, &self.work)
    }

    /// The name suggested by "Save as"
    pub fn save_as_suggestion(&self) -> String {
        if self.loaded_profile.is_empty() || self.loaded_profile == IMPLICIT_PROFILE_NAME {
            String::new()
        } else {
            format!("{} copy", self.loaded_profile)
        }
    }

    /// "Save as" with the typed name; asks before overwriting unless `confirmed`
    pub fn save_profile_as(&mut self, name: &str, confirmed: bool) -> bool {
        let name = name.trim();
        if name.is_empty() {
            self.set_status("Profile name cannot be empty.");
            return false;
        }
        if name == IMPLICIT_PROFILE_NAME {
            self.set_status("'Last' is reserved.");
            return false;
        }
        if name == self.loaded_profile {
            self.set_status("Choose a different name for Save as.");
            return false;
        }
        if self.profiles.contains(name) && !confirmed {
            self.prompt = Some(Prompt::Overwrite { name: name.to_string(), rename_from: None });
            return false;
        }
        self.profiles.save_as(name, &self.work);
        self.loaded_profile = name.to_string();
        self.persist_profiles();
        self.set_status_text(self.tf("Saved current profile as '{0}'.", &[&name]));
        true
    }

    pub fn rename_profile(&mut self, profile: &str, new_name: &str, confirmed: bool) -> bool {
        let new_name = new_name.trim();
        if !self.profiles.contains(profile) {
            return false;
        }
        if new_name.is_empty() {
            self.set_status("Profile name cannot be empty.");
            return false;
        }
        if new_name == IMPLICIT_PROFILE_NAME {
            self.set_status("'Last' is reserved.");
            return false;
        }
        if new_name != profile && self.profiles.contains(new_name) && !confirmed {
            self.prompt = Some(Prompt::Overwrite { name: new_name.to_string(), rename_from: Some(profile.to_string()) });
            return false;
        }
        let overwrote_loaded = new_name != profile && self.loaded_profile == new_name;
        let was_implicit = self.profiles.get(profile).is_some_and(|stored| stored.implicit);
        self.profiles.rename(profile, new_name);
        if !was_implicit && self.loaded_profile == profile {
            self.loaded_profile = new_name.to_string();
        }
        if overwrote_loaded {
            if let Some(stored) = self.profiles.get(new_name).cloned() {
                self.apply_working_set(stored.set, new_name);
            }
        }
        self.persist_profiles();
        self.set_status_text(self.tf("Renamed profile '{0}' to '{1}'.", &[&profile, &new_name]));
        true
    }

    pub fn move_profile(&mut self, dragged: &str, target: Option<&str>) {
        if self.profiles.move_before(dragged, target) {
            self.persist_profiles();
            let message = if target.is_some() { self.tf("Moved profile '{0}'.", &[&dragged]) } else { self.tf("Moved profile '{0}' to the end.", &[&dragged]) };
            self.set_status_text(message);
        }
    }

    pub fn export_profiles_json(&mut self) -> String {
        serde_json::to_string_pretty(&self.profiles.export_bundle(&self.work)).unwrap_or_default()
    }

    pub fn import_profiles(&mut self, bundle: &serde_json::Value) -> bool {
        match self.profiles.import_bundle(bundle) {
            Ok(working) => {
                if let Some(working) = working {
                    if !working.operators.is_empty() || self.work.operators.is_empty() {
                        self.work = working;
                    }
                }
                self.loaded_profile.clear();
                self.persist_profiles();
                self.changed();
                self.set_status("Imported profile bundle.");
                true
            }
            Err(message) => {
                self.handle_error(self.t(message).to_string());
                false
            }
        }
    }

    pub fn import_profiles_file(&mut self, path: &std::path::Path) {
        match std::fs::read(path).ok().and_then(|bytes| serde_json::from_slice::<serde_json::Value>(&bytes).ok()) {
            Some(bundle) => {
                self.import_profiles(&bundle);
            }
            None => self.handle_error(self.t("The selected file is not valid JSON.").to_string()),
        }
    }

    // --- loading ------------------------------------------------------------------------------

    /// Loads the files; with several and no `action` the user is asked how to combine them.
    /// `filter` starts the pipeline with an include filter `(text, case sensitive)`.
    pub fn open_paths(&mut self, paths: Vec<PathBuf>, action: Option<LoadAction>, filter: Option<(String, bool)>) {
        if paths.is_empty() {
            return;
        }
        if let Some(loader) = self.loader.take() {
            loader.cancel.cancel();
        }
        self.drop_note = Some((self.tf("Loading files: {0}…", &[&paths.len()]), false));
        let progress = Arc::new(LoadProgress::default());
        progress.total.store(load::total_size(&paths), std::sync::atomic::Ordering::Relaxed);
        let cancel = Cancel::new();
        let read = {
            let (progress, cancel) = (progress.clone(), cancel.clone());
            move || paths.iter().map(|path| load::read_source(path, &progress, &cancel).map_err(|error| format!("{} - {error}", path.display()))).collect()
        };
        if self.synchronous {
            let results = read();
            self.loaded(results, action, filter);
            return;
        }
        let (sender, receiver) = channel();
        let repaint = self.repaint.clone();
        std::thread::Builder::new()
            .name("loader".into())
            .spawn(move || {
                let _ = sender.send(read());
                if let Some(ctx) = repaint {
                    ctx.request_repaint();
                }
            })
            .expect("loader thread");
        self.loader = Some(LoadJob { receiver, progress, cancel, action, filter });
    }

    /// Takes the result of a finished load; called every frame
    pub fn poll_loader(&mut self) {
        let Some(loader) = &self.loader else { return };
        let Ok(results) = loader.receiver.try_recv() else { return };
        let loader = self.loader.take().unwrap();
        if !loader.cancel.is_canceled() {
            self.loaded(results, loader.action, loader.filter);
        }
    }

    pub fn cancel_load(&mut self) {
        if let Some(loader) = self.loader.take() {
            loader.cancel.cancel();
            self.drop_note = None;
            self.set_status("Load canceled.");
        }
    }

    fn loaded(&mut self, results: Vec<Result<TextSource, String>>, action: Option<LoadAction>, filter: Option<(String, bool)>) {
        self.drop_note = None;
        let mut sources = Vec::new();
        let mut unreadable = Vec::new();
        for result in results {
            match result {
                Ok(source) => sources.push(source),
                Err(problem) => unreadable.push(problem),
            }
        }
        if !sources.is_empty() {
            self.incoming(sources, action);
            if let (Some((text, case_sensitive)), true) = (filter.filter(|(text, _)| !text.is_empty()), self.pending_load.is_none() && self.source.is_some()) {
                let id = self.work.take_id();
                let mut operator = create_default(id, OpType::Filter, None);
                if let OpKind::Filter(filter) = &mut operator.kind {
                    filter.include = vec![Clause { text, case_sensitive }];
                }
                self.work.selected_operator_id = Some(id);
                self.work.operators.push(operator);
                self.changed();
            }
        }
        if !unreadable.is_empty() {
            // One unreadable file must not hide the others
            let message = self.tf("Could not read files ({0}): {1}", &[&unreadable.len(), &unreadable.join("\n")]);
            self.handle_error(message);
        }
    }

    /// Opens paths copied in a file manager or shows the pasted text.
    pub fn paste_text(&mut self, text: String) {
        if text.is_empty() {
            return;
        }
        // Files copied in a file manager arrive as the text of their paths
        if let Some(files) = crate::clipboard::files_named_by(&text) {
            self.open_paths(files, None, None);
            return;
        }
        match load::source_from_text(text, "Clipboard", "Clipboard") {
            Ok(source) => self.incoming(vec![source], None),
            Err(error) => {
                let message = self.i18n.error(&error);
                self.handle_error(message);
            }
        }
    }

    fn incoming(&mut self, incoming: Vec<TextSource>, action: Option<LoadAction>) {
        let has_existing = self.source.is_some();
        match action {
            Some(action) => self.apply_incoming(incoming, action, action == LoadAction::ReplaceAppend),
            None if !has_existing && incoming.len() == 1 => self.apply_incoming(incoming, LoadAction::Replace, false),
            None => self.pending_load = Some(PendingLoad { incoming, has_existing }),
        }
    }

    /// The answer of the "how should the files be loaded" question; `None` cancels
    pub fn resolve_load(&mut self, action: Option<LoadAction>) {
        let Some(pending) = self.pending_load.take() else { return };
        match action {
            Some(action) => self.apply_incoming(pending.incoming, action, false),
            None => self.set_status("Load canceled."),
        }
    }

    fn summarize_names(&self, names: &[&str]) -> String {
        match names {
            [] => "Clipboard".to_string(),
            [only] => only.to_string(),
            [first, rest @ ..] => self.tf("{0} + {1} more", &[first, &rest.len()]),
        }
    }

    fn apply_incoming(&mut self, incoming: Vec<TextSource>, action: LoadAction, offer_zip: bool) {
        let incoming_names: Vec<&str> = incoming.iter().map(|source| source.name.as_str()).collect();
        let incoming_label = self.summarize_names(&incoming_names);
        if action == LoadAction::Replace && incoming.len() == 1 {
            let source = incoming.into_iter().next().unwrap();
            self.show_source(source.stage, incoming_label, source.encoding, source.chars);
            return;
        }
        let replaces = matches!(action, LoadAction::Replace | LoadAction::ReplaceAppend | LoadAction::ReplaceZip);
        let zips = matches!(action, LoadAction::Zip | LoadAction::ReplaceZip);
        let existing = self.source.as_ref().filter(|_| !replaces);
        let mut names: Vec<&str> = existing.iter().map(|source| source.name.as_str()).collect();
        names.extend(&incoming_names);
        let name = match action {
            LoadAction::Replace | LoadAction::ReplaceAppend => incoming_label,
            LoadAction::ReplaceZip => self.tf("Zipped: {0}", &[&incoming_label]),
            LoadAction::Append => self.summarize_names(&names),
            LoadAction::Zip => self.tf("Zipped: {0}", &[&self.summarize_names(&names)]),
        };
        let mut encodings: Vec<&str> = existing.iter().map(|source| source.encoding.as_str()).chain(incoming.iter().map(|source| source.encoding.as_str())).collect();
        encodings.sort_unstable();
        encodings.dedup();
        let encoding = if encodings.len() == 1 { encodings[0].to_string() } else { "mixed".to_string() };
        let merged = {
            let mut bundles: Vec<Bundle> = existing.iter().map(|source| Bundle::of(&source.raw)).collect();
            bundles.extend(incoming.iter().map(|source| Bundle::of(&source.stage)));
            if zips {
                load::merge_zip(&bundles)
            } else {
                load::merge_append(&bundles)
            }
        };
        match merged {
            Ok(stage) => {
                let chars = stage_chars(&stage);
                let count = incoming.len();
                self.show_source(stage, name, encoding, chars);
                if offer_zip {
                    self.offer_zip = true;
                    self.set_status_text(self.tf("Loaded {0} files one after another.", &[&count]));
                }
            }
            Err(error) => {
                let message = self.i18n.error(&error);
                self.handle_error(message);
            }
        }
    }

    pub fn close_source(&mut self) {
        self.cancel_load();
        self.pending_load = None;
        self.source = None;
        self.reset_source_view(0);
        self.find = Find::default();
        self.focus_viewer = true;
        self.set_status("Ready.");
    }

    fn reset_source_view(&mut self, source_bytes: usize) {
        if let Some(job) = self.job.take() {
            job.cancel.cancel();
        }
        // A canceled worker may still finish a stage in the previous source's cache.
        let mut cache = StageCache::default();
        cache.set_source_bytes(source_bytes);
        self.cache = Arc::new(Mutex::new(cache));
        self.offer_zip = false;
        self.drop_note = None;
        self.work.selected_operator_id = None;
        self.view.scroll_top = 0.0;
        self.view.scroll_left = 0;
        self.view.hovered_row = None;
        self.viewer = Default::default();
        self.selection = None;
        self.selection_chars = None;
        self.cursor = Point::default();
        self.failed = None;
        self.preview = None;
        self.last_stage = 0;
    }

    fn show_source(&mut self, stage: Stage, name: String, encoding: String, chars: u64) {
        // The source itself is shown first; a click on an operation applies the pipeline.
        self.reset_source_view(stage.bufs.iter().map(|buf| buf.len()).sum());
        self.source = Some(Source { name, encoding, raw: Arc::new(stage), chars });
        let name = self.source.as_ref().map(|source| source.name.clone()).unwrap_or_default();
        self.set_status_text(self.tf("Loaded source '{0}'.", &[&name]));
    }

    /// The loaded files interleaved row by row instead of one after another
    pub fn zip_loaded_sources(&mut self) {
        self.offer_zip = false;
        let Some(source) = &self.source else { return };
        let labels: Vec<&str> = source.raw.sources.iter().map(|source| source.label.as_str()).collect();
        let name = self.tf("Zipped: {0}", &[&self.summarize_names(&labels)]);
        let encoding = source.encoding.clone();
        match load::rezip_by_source(&source.raw) {
            Ok(stage) => {
                // Only the order of the rows changes: the view stays at the selected operation
                let selected = self.work.selected_operator_id;
                let chars = stage_chars(&stage);
                self.show_source(stage, name, encoding, chars);
                self.work.selected_operator_id = selected.filter(|id| self.work.index_of(*id).is_some());
            }
            Err(error) => {
                let message = self.i18n.error(&error);
                self.handle_error(message);
            }
        }
    }

    /// "Merge sources": the loaded files become one source without tints
    pub fn flatten_sources(&mut self) {
        let Some(source) = &self.source else { return };
        if source.raw.sources.len() <= 1 {
            self.set_status("The content is already one source.");
            return;
        }
        let stage = load::flatten_sources(&source.raw, &source.name);
        let (name, encoding, chars) = (source.name.clone(), source.encoding.clone(), source.chars);
        let (selected, scroll_top) = (self.work.selected_operator_id, self.view.scroll_top);
        self.show_source(stage, name, encoding, chars);
        self.work.selected_operator_id = selected;
        self.view.scroll_top = scroll_top;
        self.set_status("Merged the files into one source.");
    }

    pub fn window_title(&self) -> String {
        match &self.source {
            Some(source) => format!("{} - Log Explorer", source.name),
            None => "Log Explorer".to_string(),
        }
    }
}

fn stage_chars(stage: &Stage) -> u64 {
    use rayon::prelude::*;
    (0..stage.len()).into_par_iter().with_min_len(1 << 14).map(|row| stage.line_cols(row) as u64).sum::<u64>() + stage.len().saturating_sub(1) as u64
}

/// The row of `stage` that came from original row `row_id`, else the nearest original row
fn find_row_by_id(stage: &Stage, row_id: u32, fallback_row: usize) -> usize {
    let fallback = fallback_row.min(stage.len().saturating_sub(1));
    let mut closest: Option<(u64, usize, usize)> = None;
    for row in 0..stage.len() {
        let candidate = stage.row_id(row);
        if candidate == row_id {
            return row;
        }
        if candidate == logexplorer_core::stage::NO_ROW || row_id == logexplorer_core::stage::NO_ROW {
            continue;
        }
        let key = ((candidate as i64 - row_id as i64).unsigned_abs(), row.abs_diff(fallback), row);
        if closest.is_none_or(|best| (key.0, key.1) < (best.0, best.1)) {
            closest = Some(key);
        }
    }
    closest.map_or(fallback, |best| best.2)
}

/// The language of the system's UI, when the environment does not say
fn system_language() -> Vec<String> {
    let mut languages: Vec<String> = std::env::var("LANGUAGE").ok().map(|list| list.split(':').map(str::to_string).collect()).unwrap_or_default();
    languages.extend(sys_locale::get_locales());
    languages
}
