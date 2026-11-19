//! The file view: only the rows on screen are laid out and painted, whatever the size of
//! the stage. Scrolling is kept in f64 pixels with scrollbars of its own, because a
//! hundred million rows do not fit the f32 offsets of an egui scroll area.

use crate::app::App;
use crate::theme;
use egui::{Color32, FontId, Modifiers, PointerButton, Pos2, Rect, Sense, Stroke, Vec2};
use logexplorer_core::i18n::grouped;
use logexplorer_core::ops::filter::FilterMatcher;
use logexplorer_core::select::ClickSelector;
use logexplorer_core::selection::{Point, Selection};
use logexplorer_core::stage::Stage;
use logexplorer_core::text::cols_to_bytes;
use std::{collections::HashMap, ops::Range, sync::Arc};

const SCROLLBAR: f32 = 14.0;
const TEXT_PADDING: f32 = 10.0;
/// Rows longer than this get an index of their columns instead of a scan from their start
const LONG_ROW_BYTES: usize = 1 << 16;
const CHECKPOINT_COLS: usize = 4096;
/// Two presses at one place within this time are a double click (the default of Windows)
const DOUBLE_CLICK_SECONDS: f64 = 0.5;

#[derive(Default)]
pub struct ViewerState {
    click_selector: ClickSelector,
    selecting: bool,
    anchor: Point,
    start_point: Point,
    /// Time and place of the last press, to tell a double click
    last_press: Option<(f64, Point)>,
    drag_vertical: Option<(f32, f64)>,
    drag_horizontal: Option<(f32, usize)>,
    /// Byte offset of every 4096th column of long rows that are not ASCII: `(stage, row, offsets)`
    long_rows: Vec<(u64, usize, Vec<usize>)>,
    preview: Option<(u64, Arc<FilterMatcher>)>,
    preview_rows: HashMap<usize, bool>,
    /// The rectangle of the rows, for tests that click into it
    pub rect: Option<Rect>,
    pub text_left: f32,
}

impl ViewerState {
    /// Byte range of columns `start..end` of a row
    fn cols_to_bytes(&mut self, stage: &Stage, row: usize, start: usize, end: usize) -> (usize, usize) {
        let span = stage.span(row);
        let length = span.len() as usize;
        if stage.bufs[span.buf() as usize].ascii {
            return (start.min(length), end.min(length));
        }
        let line = stage.line(row);
        if length <= LONG_ROW_BYTES {
            return cols_to_bytes(line, start, end);
        }
        if !self.long_rows.iter().any(|(id, cached, _)| *id == stage.id && *cached == row) {
            let offsets = line.char_indices().step_by(CHECKPOINT_COLS).map(|(index, _)| index).collect();
            if self.long_rows.len() >= 8 {
                self.long_rows.remove(0);
            }
            self.long_rows.push((stage.id, row, offsets));
        }
        let offsets = &self.long_rows.iter().find(|(id, cached, _)| *id == stage.id && *cached == row).unwrap().2;
        let Some(&base) = offsets.get(start / CHECKPOINT_COLS) else { return (length, length) };
        let (from, to) = cols_to_bytes(&line[base..], start % CHECKPOINT_COLS, end - start / CHECKPOINT_COLS * CHECKPOINT_COLS);
        (base + from, base + to)
    }

    /// The part of a row that is on screen
    fn visible<'a>(&mut self, stage: &'a Stage, row: usize, scroll_left: usize, cols: usize) -> &'a str {
        let (start, end) = self.cols_to_bytes(stage, row, scroll_left, scroll_left + cols);
        &stage.line(row)[start..end]
    }

    fn prepare_preview(&mut self, stage: &Stage, matcher: Option<Arc<FilterMatcher>>, visible: Range<usize>) {
        let next = matcher.map(|matcher| (stage.id, matcher));
        match (&self.preview, &next) {
            (Some((id, cached)), Some((next_id, matcher))) if id == next_id && Arc::ptr_eq(cached, matcher) => {
                self.preview_rows.retain(|row, _| visible.contains(row));
            }
            _ => self.preview_rows.clear(),
        }
        self.preview = next;
    }

    fn preview_matches(&mut self, stage: &Stage, row: usize) -> bool {
        self.preview.as_ref().is_some_and(|(_, matcher)| *self.preview_rows.entry(row).or_insert_with(|| matcher.matches(stage, row)))
    }
}

struct Metrics {
    rect: Rect,
    font: FontId,
    row_height: f32,
    char_width: f32,
    text_left: f32,
    gutter: f32,
}

impl Metrics {
    fn row_at(&self, y: f32, scroll_top: f64, rows: usize) -> usize {
        let row = (((y - self.rect.top()) as f64 + scroll_top) / self.row_height as f64).floor();
        (row.max(0.0) as usize).min(rows.saturating_sub(1))
    }

    fn row_top(&self, row: usize, scroll_top: f64) -> f32 {
        self.rect.top() + (row as f64 * self.row_height as f64 - scroll_top) as f32
    }
}

/// X offsets of the character boundaries of a laid out row: `offsets[i]` is where character
/// `i` starts, the last one where the text ends
fn boundaries(galley: &egui::Galley) -> Vec<f32> {
    let mut offsets = Vec::new();
    if let Some(placed) = galley.rows.first() {
        for glyph in &placed.row.glyphs {
            offsets.push(glyph.pos.x);
        }
        offsets.push(placed.row.glyphs.last().map_or(0.0, |glyph| glyph.pos.x + glyph.advance_width));
    } else {
        offsets.push(0.0);
    }
    offsets
}

/// The column under `x` in a row
fn col_at(ui: &egui::Ui, state: &mut ViewerState, metrics: &Metrics, stage: &Stage, row: usize, (scroll_left, cols): (usize, usize), x: f32) -> usize {
    let offset = (x - metrics.text_left).max(0.0);
    if row >= stage.len() {
        return scroll_left;
    }
    let visible = state.visible(stage, row, scroll_left, cols + 1);
    if visible.is_ascii() {
        return scroll_left + (offset / metrics.char_width).floor() as usize;
    }
    // Wide and combined characters: ask the layout where each one is
    let galley = ui.painter().layout_no_wrap(visible.to_owned(), metrics.font.clone(), Color32::BLACK);
    let offsets = boundaries(&galley);
    let end = *offsets.last().unwrap_or(&0.0);
    if offset >= end {
        return scroll_left + offsets.len() - 1 + ((offset - end) / metrics.char_width).floor() as usize;
    }
    scroll_left + offsets.partition_point(|start| *start <= offset).saturating_sub(1)
}

fn point_at(ui: &egui::Ui, app: &mut App, metrics: &Metrics, stage: &Stage, pos: Pos2) -> Point {
    let row = metrics.row_at(pos.y, app.view.scroll_top, stage.len());
    let (scroll_left, cols) = (app.view.scroll_left, app.view.viewport_cols);
    Point::new(row, col_at(ui, &mut app.viewer, metrics, stage, row, (scroll_left, cols), pos.x))
}

fn begin_selection(app: &mut App, stage: &Stage, point: Point, in_gutter: bool, modifiers: Modifiers, time: f64) {
    let double = app.viewer.last_press.is_some_and(|(at, place)| time - at < DOUBLE_CLICK_SECONDS && place.row == point.row && place.col.abs_diff(point.col) <= 1);
    app.viewer.last_press = Some((time, point));
    if double {
        // With several files: a filter for the file of the row
        app.viewer.selecting = false;
        app.viewer.last_press = None;
        app.add_source_filter_for_row(point.row);
        return;
    }
    if in_gutter && modifiers.is_none() {
        app.viewer.selecting = false;
        app.clear_selection();
        return;
    }
    let shrink = modifiers.command || modifiers.ctrl;
    let range = app.viewer.click_selector.click_range(stage, point, app.selection.as_ref(), shrink, modifiers.shift);
    let anchor = if modifiers.shift { app.selection_anchor() } else { point };
    app.viewer.anchor = anchor;
    app.viewer.start_point = point;
    if modifiers.shift {
        let target = match range {
            Some((start, end)) => {
                if anchor <= start {
                    end
                } else {
                    start
                }
            }
            None => point,
        };
        app.selection = Some(Selection::new(anchor, target));
        app.set_cursor(stage, target);
    } else if let Some((start, end)) = range {
        app.selection = Some(Selection::new(start, end));
        app.set_cursor(stage, end);
    } else {
        app.selection = Some(Selection::caret(point));
        app.set_cursor(stage, point);
    }
    app.viewer.selecting = true;
}

fn scrollbars(ui: &mut egui::Ui, app: &mut App, stage: &Stage, vertical: Rect, horizontal: Rect) {
    let painter = ui.painter().clone();
    let track = theme::SURFACE_2;
    painter.rect_filled(vertical, 4.0, track);
    painter.rect_filled(horizontal, 4.0, track);

    // Vertical: the thumb covers the content and the room to scroll past both ends
    let metrics = app.vertical_metrics(stage);
    let viewport = app.view.viewport_height as f64;
    let total = stage.len() as f64 * app.view.row_height as f64 + metrics.overscroll * 2.0;
    let range = (metrics.max - metrics.min).max(1.0);
    let track_height = vertical.height();
    let thumb_height = if total <= viewport { track_height } else { ((viewport / total) as f32 * track_height).max(22.0) };
    let max_thumb_top = (track_height - thumb_height).max(0.0);
    let thumb_top = ((app.view.scroll_top - metrics.min) / range) as f32 * max_thumb_top;
    let thumb = Rect::from_min_size(Pos2::new(vertical.left() + 2.0, vertical.top() + thumb_top), Vec2::new(vertical.width() - 4.0, thumb_height));
    let response = ui.interact(vertical, ui.scope_id().with("vertical scrollbar"), Sense::click_and_drag());
    if let Some(pos) = response.interact_pointer_pos() {
        if response.drag_started() || response.is_pointer_button_down_on() && app.viewer.drag_vertical.is_none() {
            if !thumb.contains(pos) {
                // A press beside the thumb jumps there
                let percent = ((pos.y - vertical.top()) / track_height).clamp(0.0, 1.0) as f64;
                app.set_scroll_top(stage, metrics.min + range * percent);
            }
            app.viewer.drag_vertical = Some((pos.y, app.view.scroll_top));
        } else if let Some((start_y, start_scroll)) = app.viewer.drag_vertical {
            app.set_scroll_top(stage, start_scroll + ((pos.y - start_y) / max_thumb_top.max(1.0)) as f64 * range);
        }
    } else {
        app.viewer.drag_vertical = None;
    }
    let color = |response: &egui::Response| {
        if response.dragged() || response.is_pointer_button_down_on() {
            theme::ACCENT
        } else if response.hovered() {
            theme::BORDER_STRONG
        } else {
            theme::BORDER
        }
    };
    painter.rect_filled(thumb, 4.0, color(&response));

    // Horizontal: columns
    let cols = app.view.viewport_cols;
    let total_cols = (stage.max_cols as usize).max(cols);
    let max_scroll = total_cols - cols;
    let track_width = horizontal.width();
    let thumb_width = if total_cols <= cols { track_width } else { (cols as f32 / total_cols as f32 * track_width).max(24.0) };
    let max_thumb_left = (track_width - thumb_width).max(0.0);
    let thumb_left = if max_scroll > 0 { app.view.scroll_left.min(max_scroll) as f32 / max_scroll as f32 * max_thumb_left } else { 0.0 };
    let thumb = Rect::from_min_size(Pos2::new(horizontal.left() + thumb_left, horizontal.top() + 2.0), Vec2::new(thumb_width, horizontal.height() - 4.0));
    let response = ui.interact(horizontal, ui.scope_id().with("horizontal scrollbar"), Sense::click_and_drag());
    if let Some(pos) = response.interact_pointer_pos() {
        if response.drag_started() || response.is_pointer_button_down_on() && app.viewer.drag_horizontal.is_none() {
            if !thumb.contains(pos) {
                let percent = ((pos.x - horizontal.left()) / track_width).clamp(0.0, 1.0);
                app.set_scroll_left(stage, (max_scroll as f32 * percent) as i64);
            }
            app.viewer.drag_horizontal = Some((pos.x, app.view.scroll_left));
        } else if let Some((start_x, start_scroll)) = app.viewer.drag_horizontal {
            app.set_scroll_left(stage, start_scroll as i64 + ((pos.x - start_x) / max_thumb_left.max(1.0) * max_scroll as f32) as i64);
        }
    } else {
        app.viewer.drag_horizontal = None;
    }
    painter.rect_filled(thumb, 4.0, color(&response));
}

fn pointer_input(ui: &egui::Ui, app: &mut App, metrics: &Metrics, stage: &Stage, response: &egui::Response) {
    let rect = metrics.rect;
    let (modifiers, time) = ui.input(|input| (input.modifiers, input.time));
    let inside = response.contains_pointer();

    if inside {
        // The wheel scrolls rows; with Shift (egui turns it sideways) or Ctrl it scrolls columns
        let (scroll, wheel) = ui.input(|input| {
            let wheel: f32 = input
                .events
                .iter()
                .filter_map(|event| match event {
                    egui::Event::MouseWheel { delta, modifiers, .. } if modifiers.ctrl || modifiers.command => Some(delta.y + delta.x),
                    _ => None,
                })
                .sum();
            (input.smooth_scroll_delta, wheel)
        });
        if wheel != 0.0 {
            app.set_scroll_left(stage, app.view.scroll_left as i64 - wheel.signum() as i64 * 4);
        } else {
            if scroll.y != 0.0 {
                app.scroll_by(stage, -scroll.y as f64);
            }
            if scroll.x != 0.0 {
                app.set_scroll_left(stage, app.view.scroll_left as i64 - (scroll.x / metrics.char_width).round() as i64);
            }
        }
    }

    let (pressed, down, middle, pos) = ui.input(|input| {
        (
            input.pointer.button_pressed(PointerButton::Primary),
            input.pointer.button_down(PointerButton::Primary),
            input.pointer.button_pressed(PointerButton::Middle),
            input.pointer.interact_pos(),
        )
    });
    let Some(pos) = pos else {
        app.viewer.selecting = false;
        return;
    };
    if inside && middle {
        let point = point_at(ui, app, metrics, stage, pos);
        let cols = ((pos.x - metrics.text_left).max(0.0) / metrics.char_width).floor() as usize;
        app.navigate_stage(point, if modifiers.ctrl || modifiers.command { 1 } else { -1 }, pos.y - rect.top(), cols);
        return;
    }
    if inside && pressed {
        // Typing in a field of an operation ends with a click into the file
        app.focus_viewer = true;
        let point = point_at(ui, app, metrics, stage, pos);
        begin_selection(app, stage, point, pos.x < rect.left() + metrics.gutter, modifiers, time);
    } else if app.viewer.selecting && down && app.selection.is_some() {
        // Dragging past the top or bottom scrolls on
        if pos.y < rect.top() {
            app.scroll_by(stage, -(metrics.row_height as f64) * 2.0);
            ui.ctx().request_repaint();
        } else if pos.y > rect.bottom() {
            app.scroll_by(stage, metrics.row_height as f64 * 2.0);
            ui.ctx().request_repaint();
        }
        let clamped = Pos2::new(pos.x, pos.y.clamp(rect.top(), rect.bottom() - 1.0));
        let point = point_at(ui, app, metrics, stage, clamped);
        if point != app.viewer.start_point {
            app.selection = Some(Selection::new(app.viewer.anchor, point));
            app.set_cursor(stage, point);
        }
    } else {
        app.viewer.selecting = false;
    }
}

fn paint_rows(ui: &egui::Ui, app: &mut App, metrics: &Metrics, stage: &Stage) {
    let rect = metrics.rect;
    let painter = ui.painter().with_clip_rect(rect);
    if !app.windows_software_renderer {
        painter.rect_filled(rect, 0.0, theme::PANEL);
    }
    let gutter = (metrics.gutter > 0.0).then(|| Rect::from_min_max(rect.min, Pos2::new(rect.left() + metrics.gutter, rect.bottom())));
    if let Some(gutter) = gutter {
        painter.rect_filled(gutter, 0.0, theme::SURFACE);
    }

    let row_height = metrics.row_height as f64;
    let scroll_top = app.view.scroll_top;
    let first = (scroll_top / row_height).floor().max(0.0) as usize;
    let last = (((scroll_top + rect.height() as f64) / row_height).ceil().max(0.0) as usize).min(stage.len());
    let selection = app.selection.map(|selection| selection.normalized());
    let preview = app.preview_filter(stage);
    app.viewer.prepare_preview(stage, preview, first..last);
    let tinted = stage.multiple_sources();
    let (scroll_left, cols) = (app.view.scroll_left, app.view.viewport_cols + 1);
    let text_clip = Rect::from_min_max(Pos2::new(rect.left() + metrics.gutter, rect.top()), rect.max);
    let text_painter = painter.with_clip_rect(text_clip);

    for row in first..last {
        let top = metrics.row_top(row, scroll_top);
        let row_rect = Rect::from_min_size(Pos2::new(rect.left(), top), Vec2::new(rect.width(), metrics.row_height));
        let hovered = app.view.hovered_row == Some(row);
        if tinted {
            if let Some(source) = stage.source_of(row) {
                painter.rect_filled(row_rect, 0.0, theme::source_tint(source.tone, hovered));
            }
        } else if hovered {
            painter.rect_filled(row_rect, 0.0, theme::HOVER_ROW);
        }
        if let Some(gutter) = gutter {
            painter.text(Pos2::new(gutter.right() - 8.0, top + metrics.row_height / 2.0), egui::Align2::RIGHT_CENTER, grouped(row as u64 + 1), metrics.font.clone(), theme::MUTED);
        }

        let visible = app.viewer.visible(stage, row, scroll_left, cols);
        let keeps = app.viewer.preview_matches(stage, row);
        let galley = (!visible.is_empty()).then(|| text_painter.layout_no_wrap(visible.to_owned(), metrics.font.clone(), if keeps { theme::PREVIEW } else { theme::TEXT }));
        let text_top = top + (metrics.row_height - galley.as_ref().map_or(metrics.row_height, |galley| galley.size().y)) / 2.0;

        if let Some((start, end)) = selection.and_then(|selection| selection.cols_in_row(row)) {
            let to_end = end == usize::MAX;
            let (local_start, local_end) = (start.saturating_sub(scroll_left), end.saturating_sub(scroll_left));
            if (to_end || end > scroll_left) && (end > start || to_end) {
                let offsets = match &galley {
                    Some(galley) if !visible.is_ascii() => Some(boundaries(galley)),
                    _ => None,
                };
                let chars = offsets.as_ref().map_or(visible.len(), |offsets| offsets.len() - 1);
                let x_of = |col: usize| match &offsets {
                    Some(offsets) => offsets[col.min(chars)],
                    None => col.min(chars) as f32 * metrics.char_width,
                };
                let from = x_of(local_start);
                // A selection running on to the next row also covers the line break
                let to = if to_end { x_of(chars) + metrics.char_width * 0.5 } else { x_of(local_end) };
                if to > from && (local_start <= chars || to_end) && start <= scroll_left + chars + usize::from(to_end) {
                    let selected = Rect::from_min_max(Pos2::new(metrics.text_left + from, top), Pos2::new(metrics.text_left + to, top + metrics.row_height));
                    text_painter.rect_filled(selected, 2.0, theme::SELECTION);
                }
            }
        }
        if let Some(galley) = galley {
            text_painter.galley(Pos2::new(metrics.text_left, text_top), galley, theme::TEXT);
        }
    }
    if let Some(gutter) = gutter {
        painter.line_segment([Pos2::new(gutter.right(), rect.top()), Pos2::new(gutter.right(), rect.bottom())], Stroke::new(1.0, theme::BORDER));
    }
}

/// The file view with its scrollbars, filling the rest of `ui`.
pub fn show(ui: &mut egui::Ui, app: &mut App, stage: Option<&Arc<Stage>>) {
    let outer = ui.available_rect_before_wrap();
    ui.allocate_rect(outer, Sense::hover());
    let rect = Rect::from_min_max(outer.min, Pos2::new(outer.right() - SCROLLBAR, outer.bottom() - SCROLLBAR));
    let font = theme::mono_font();
    let (char_width, font_height) = ui.fonts_mut(|fonts| (fonts.glyph_width(&font, '0'), fonts.row_height(&font)));
    let row_height = (font_height + 3.0).ceil();
    let gutter = if app.view.show_line_numbers {
        let digits = grouped(stage.map_or(1, |stage| stage.len().max(1)) as u64).chars().count();
        digits as f32 * char_width + 16.0
    } else {
        0.0
    };
    let metrics = Metrics { rect, font, row_height, char_width: char_width.max(1.0), text_left: rect.left() + gutter + TEXT_PADDING, gutter };

    app.view.row_height = row_height;
    app.view.char_width = metrics.char_width;
    app.view.viewport_height = rect.height().max(row_height);
    app.view.viewport_rows = ((rect.height() / row_height).floor() as usize).max(1);
    app.view.viewport_cols = (((rect.width() - gutter - TEXT_PADDING - 6.0) / metrics.char_width).floor().max(4.0)) as usize;
    app.viewer.rect = Some(rect);
    app.viewer.text_left = metrics.text_left;

    let response = ui.interact(rect, ui.scope_id().with("file view"), Sense::click_and_drag());
    let label = app.t("File view").to_string();
    response.widget_info(|| egui::WidgetInfo::labeled(egui::Role::Document, true, &label));

    let Some(stage) = stage else {
        let painter = ui.painter().with_clip_rect(outer);
        if !app.windows_software_renderer {
            painter.rect_filled(outer, 0.0, theme::PANEL);
        }
        let center = outer.center();
        let (title, hint) = (app.t("Drop files to open them").to_string(), app.t("F1 shows help and all shortcuts.").to_string());
        painter.text(center - Vec2::new(0.0, 14.0), egui::Align2::CENTER_CENTER, title, FontId::proportional(theme::HEADING_SIZE), theme::TEXT);
        painter.text(center + Vec2::new(0.0, 16.0), egui::Align2::CENTER_CENTER, hint, FontId::proportional(theme::BODY_SIZE), theme::MUTED);
        if let Some((note, is_error)) = &app.drop_note {
            painter.text(
                center + Vec2::new(0.0, 44.0),
                egui::Align2::CENTER_CENTER,
                note,
                FontId::proportional(theme::BODY_SIZE),
                if *is_error { theme::DANGER } else { theme::ACCENT_STRONG },
            );
        }
        app.view.hovered_row = None;
        return;
    };

    // The size of the view may have changed since the scroll position was set
    let limits = app.vertical_metrics(stage);
    app.view.scroll_top = app.view.scroll_top.clamp(limits.min, limits.max);
    app.view.scroll_left = app.view.scroll_left.min(app.max_scroll_left(stage));

    pointer_input(ui, app, &metrics, stage, &response);
    let vertical = Rect::from_min_max(Pos2::new(rect.right(), outer.top()), Pos2::new(outer.right(), rect.bottom()));
    let horizontal = Rect::from_min_max(Pos2::new(outer.left(), rect.bottom()), Pos2::new(rect.right(), outer.bottom()));
    scrollbars(ui, app, stage, vertical, horizontal);

    let hovered_row = response.contains_pointer().then(|| ui.input(|input| input.pointer.hover_pos())).flatten().and_then(|pos| {
        let row = ((f64::from(pos.y - rect.top()) + app.view.scroll_top) / f64::from(row_height)).floor();
        (row >= 0.0 && row < stage.len() as f64).then_some(row as usize)
    });
    if app.view.hovered_row != hovered_row {
        app.view.hovered_row = hovered_row;
        // The toolbar and status bar were laid out before the viewer.
        ui.ctx().request_repaint();
    }
    paint_rows(ui, app, &metrics, stage);
}
