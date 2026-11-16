//! Everything around the file view: the toolbar, the operations with their settings, the
//! profiles, find, the status bar, the dialogs and the keyboard.

use crate::app::{App, FileDialog, FileDialogKind, LoadAction, Prompt};
use crate::{theme, viewer};
use egui::text::LayoutJob;
use egui::{Align, Color32, FontId, Key, Layout, Modifiers, RichText, Sense, Stroke, TextEdit, TextFormat};
use logexplorer_core::i18n::{grouped, I18n};
use logexplorer_core::operator::*;
use logexplorer_core::profiles::{IMPLICIT_PROFILE_NAME, PROFILE_RECENT_LIMIT};
use logexplorer_core::stage::{SourceDesc, Stage};
use logexplorer_core::typed::ValueType;
use std::sync::Arc;

pub fn find_input_id() -> egui::Id {
    egui::Id::unique("find input")
}

fn pill(ui: &mut egui::Ui, text: impl Into<String>) -> egui::Response {
    egui::Frame::NONE
        .fill(theme::SURFACE_2)
        .stroke(Stroke::new(1.0, theme::BORDER))
        .corner_radius(9)
        .inner_margin(egui::Margin::symmetric(8, 2))
        .show(ui, |ui| ui.add(egui::Label::new(RichText::new(text).small().color(theme::MUTED)).truncate()))
        .inner
}

/// Gives a control that shows only a symbol its name, for assistive tools and tests
fn named(response: egui::Response, role: egui::Role, name: &str) -> egui::Response {
    let enabled = response.enabled();
    response.widget_info(|| egui::WidgetInfo::labeled(role, enabled, name));
    response
}

/// A button that shows a symbol; its name is also its tooltip
fn icon(ui: &mut egui::Ui, button: egui::Button<'_>, name: &str) -> egui::Response {
    let response = ui.add(button).on_hover_text(name);
    named(response, egui::Role::Button, name)
}

fn meta(ui: &mut egui::Ui, text: &str) {
    ui.add(egui::Label::new(RichText::new(text).small().color(theme::MUTED)).wrap());
}

/// Text with the `<code>`, `<kbd>` and `<strong>` of the help as fonts
fn markup(text: &str, size: f32) -> LayoutJob {
    let mut job = LayoutJob::default();
    let plain = TextFormat { font_id: FontId::proportional(size), color: theme::TEXT, ..Default::default() };
    let code = TextFormat { font_id: FontId::monospace(size - 1.0), color: theme::ACCENT_STRONG, background: theme::SURFACE_2, ..Default::default() };
    let strong = TextFormat { font_id: FontId::proportional(size), color: Color32::BLACK, ..Default::default() };
    let (mut in_code, mut in_strong) = (0usize, 0usize);
    let mut rest = text;
    let unescape = |piece: &str| piece.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&amp;", "&");
    while !rest.is_empty() {
        let (piece, tag, after) = match rest.find('<').and_then(|open| rest[open..].find('>').map(|close| (open, open + close))) {
            Some((open, close)) => (&rest[..open], &rest[open + 1..close], &rest[close + 1..]),
            None => (rest, "", ""),
        };
        if !piece.is_empty() {
            let format = if in_code > 0 {
                code.clone()
            } else if in_strong > 0 {
                strong.clone()
            } else {
                plain.clone()
            };
            job.append(&unescape(piece), 0.0, format);
        }
        match tag {
            "code" | "kbd" => in_code += 1,
            "/code" | "/kbd" => in_code = in_code.saturating_sub(1),
            "strong" | "b" => in_strong += 1,
            "/strong" | "/b" => in_strong = in_strong.saturating_sub(1),
            _ => {}
        }
        rest = after;
    }
    job
}

// --- the keyboard ---------------------------------------------------------------------------------

fn pressed(ctx: &egui::Context, modifiers: Modifiers, key: Key) -> bool {
    ctx.input_mut(|input| input.consume_key(modifiers, key))
}

/// The shortcuts of the file view. They do not apply while typing in a field, except the
/// ones of find in the find box.
fn handle_keys(ctx: &egui::Context, app: &mut App) {
    let typing = ctx.text_edit_focused();
    let in_find = typing && ctx.memory(|memory| memory.focused()) == Some(find_input_id());
    let escape = ctx.input(|input| input.key_pressed(Key::Escape));
    if escape && (app.prompt.is_some() || app.pending_load.is_some() || app.help_open) {
        // The dialogs close themselves (Modal consumes the key)
        return;
    }
    if app.pending_load.is_some() || app.prompt.is_some() {
        return;
    }
    if pressed(ctx, Modifiers::COMMAND, Key::F) {
        app.show_find_bar(true, true);
        return;
    }
    if !typing || in_find {
        for (modifiers, forward) in [(Modifiers::COMMAND | Modifiers::SHIFT, false), (Modifiers::COMMAND, true)] {
            if pressed(ctx, modifiers, Key::F3) {
                app.find_from_selection(forward);
                return;
            }
        }
        for (modifiers, forward) in [(Modifiers::SHIFT, false), (Modifiers::NONE, true)] {
            if pressed(ctx, modifiers, Key::F3) {
                app.show_find_bar(false, false);
                app.find_in_view(forward);
                return;
            }
        }
        if app.find.open && !ctx.input(|input| input.modifiers.command) && pressed(ctx, Modifiers::NONE, Key::Escape) {
            app.close_find_bar();
            return;
        }
    }
    if pressed(ctx, Modifiers::NONE, Key::F1) {
        app.help_open = true;
        return;
    }
    let pasted_files = app.pasted_files.take();
    if typing {
        return;
    }
    if let Some(files) = pasted_files {
        app.open_paths(files, None, None);
        return;
    }
    // Ctrl+V arrives as a paste event with the text
    let pasted = ctx.input(|input| input.events.iter().find_map(|event| if let egui::Event::Paste(text) = event { Some(text.clone()) } else { None }));
    if let Some(text) = pasted {
        app.paste_text(text);
        return;
    }
    if app.source.is_none() {
        if escape && app.is_busy() {
            app.cancel_load();
        }
        return;
    }
    for (key, op_type) in [(Key::F, OpType::Filter), (Key::R, OpType::Replace), (Key::S, OpType::Sort), (Key::D, OpType::Diff)] {
        if !ctx.input(|input| input.modifiers.any()) && pressed(ctx, Modifiers::NONE, key) {
            app.add_operator(op_type);
            return;
        }
    }
    if pressed(ctx, Modifiers::COMMAND, Key::A) {
        app.select_whole_stage();
        return;
    }
    if ctx.input(|input| input.events.iter().any(|event| matches!(event, egui::Event::Copy))) {
        app.copy_selection();
        return;
    }
    if pressed(ctx, Modifiers::COMMAND, Key::Escape) {
        app.clear_selection();
        return;
    }
    let plain = !ctx.input(|input| input.modifiers.command || input.modifiers.alt);
    if plain && pressed(ctx, Modifiers::NONE, Key::Enter) {
        app.apply_selection_to_filter(false);
        return;
    }
    if plain && pressed(ctx, Modifiers::NONE, Key::Escape) {
        if !app.apply_selection_to_filter(true) {
            app.clear_selection();
        }
        return;
    }
    if plain && !ctx.input(|input| input.modifiers.shift) && pressed(ctx, Modifiers::NONE, Key::Delete) {
        app.add_delete_replace_from_selection();
        return;
    }
    let page = app.view.viewport_rows.saturating_sub(1).max(1) as i64;
    for extend in [true, false] {
        let modifiers = if extend { Modifiers::SHIFT } else { Modifiers::NONE };
        for (key, rows, cols) in
            [(Key::ArrowUp, -1, 0), (Key::ArrowDown, 1, 0), (Key::ArrowLeft, 0, -1), (Key::ArrowRight, 0, 1), (Key::PageUp, -page, 0), (Key::PageDown, page, 0)]
        {
            if pressed(ctx, modifiers, key) {
                app.move_cursor(rows, cols, extend);
            }
        }
        if pressed(ctx, modifiers, Key::Home) {
            app.move_to_edge(false, extend);
        }
        if pressed(ctx, modifiers, Key::End) {
            app.move_to_edge(true, extend);
        }
    }
}

// --- the toolbar ----------------------------------------------------------------------------------

fn toolbar(ui: &mut egui::Ui, app: &mut App) {
    ui.horizontal(|ui| {
        for (label, tip, op_type) in [
            ("⊕ Filter", "Keep or hide lines with the selected text (F; Enter keeps, Esc hides)", OpType::Filter),
            ("⇄ Replace", "Replace or delete the selected text (R; Delete deletes)", OpType::Replace),
            ("⇅ Sort", "Sort rows by a value (S)", OpType::Sort),
            ("≠ Diff", "Show how a value changes from row to row (D)", OpType::Diff),
        ] {
            let button = egui::Button::new(app.t(label));
            let button = if op_type == OpType::Filter { button.fill(theme::ACCENT_SOFT) } else { button };
            if ui.add(button).on_hover_text(app.t(tip)).clicked() {
                app.add_operator(op_type);
            }
        }
        let transform = app.t("Transform ▾").to_string();
        ui.menu_button(transform, |ui| {
            for (title, description, op_type) in [
                ("Extract", "Pull values out of the rows by column, delimiters, or regex", Some(OpType::Extract)),
                ("Unique", "Remove duplicate rows and optionally sort them", Some(OpType::Unique)),
                ("Join", "Join all rows into one line, for example with |", Some(OpType::Join)),
                ("Collect unique values", "Extract → Unique → Join in one step", None),
            ] {
                if op_type.is_none() {
                    ui.separator();
                }
                if ui.button(app.t(title)).on_hover_text(app.t(description)).clicked() {
                    match op_type {
                        Some(op_type) => app.add_operator(op_type),
                        None => app.add_collect_pipeline(),
                    }
                    ui.close();
                }
            }
        })
        .response
        .on_hover_text(app.t("Extract, deduplicate, or join values"));

        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            app_menu(ui, app);
            if icon(ui, egui::Button::new("?"), app.t("Help and shortcuts (F1)")).clicked() {
                app.help_open = true;
            }
            let copy = ui.add_enabled(app.selection.is_some(), egui::Button::new("▣")).on_hover_text(app.t("Copy selection (Ctrl+C)"));
            if named(copy, egui::Role::Button, app.t("Copy selection (Ctrl+C)")).clicked() {
                app.copy_selection();
            }
            if icon(ui, egui::Button::new("▤").fill(theme::ACCENT_SOFT), app.t("Open file")).clicked() {
                app.dialog = Some(FileDialog::Open);
            }
            match &app.source {
                Some(source) => {
                    let lines = app.tf("Lines: {0}", &[&grouped(source.raw.len() as u64)]);
                    let chars = app.tf("Characters: {0}", &[&grouped(source.chars)]);
                    let (cached, bytes) = app.cache_info();
                    let memory = format!("{chars}\n{}  ({} MB)", app.tf("Cached stages: {0}", &[&cached]), bytes >> 20);
                    pill(ui, lines).on_hover_text(memory);
                    pill(ui, app.tf("Encoding: {0}", &[&source.encoding]));
                    pill(ui, source.name.clone()).on_hover_text(&source.name);
                }
                None => {
                    pill(ui, app.tf("Lines: {0}", &[&0]));
                    pill(ui, app.t("Encoding: n/a"));
                    pill(ui, app.t("No source loaded"));
                }
            }
            let hint = app.t("Select text in the file: Enter keeps lines with it, Esc hides them, Delete removes the text. F1 shows all shortcuts.");
            ui.add(egui::Label::new(RichText::new(hint).small().color(theme::MUTED)).truncate());
        });
    });
}

fn app_menu(ui: &mut egui::Ui, app: &mut App) {
    let has_source = app.source.is_some();
    let has_selection = app.selection.is_some();
    let several = app.source.as_ref().is_some_and(|source| source.raw.sources.len() > 1);
    let menu = ui.menu_button("≡", |ui| {
        ui.label(RichText::new(app.t("View")).small().color(theme::MUTED));
        let item = |ui: &mut egui::Ui, app: &mut App, enabled: bool, label: &str, tip: &str| {
            let clicked = ui.add_enabled(enabled, egui::Button::new(app.t(label))).on_hover_text(app.t(tip)).clicked();
            if clicked {
                ui.close();
            }
            clicked
        };
        if item(ui, app, has_source, "⇩ Download view", "Save the rows of the current view as a text file") {
            app.dialog = Some(FileDialog::ExportView);
        }
        if item(ui, app, has_source, "◫ Copy view", "Copy all rows of the current view") {
            app.copy_view();
        }
        if item(ui, app, has_source && has_selection, "║ Copy column", "Copy the selected columns from every row") {
            app.copy_column();
        }
        if item(ui, app, has_source && has_selection, "▦ Copy block", "Copy the selected rectangle") {
            app.copy_block();
        }
        if item(ui, app, has_source, "↺ Reset view", "Show the result of all operations from the top and clear the selection") {
            app.reset_view();
        }
        if item(ui, app, several, "≡ Merge sources", "Treat the loaded files as one source; the per-file colors disappear") {
            app.flatten_sources();
        }
        ui.separator();
        ui.label(RichText::new(app.t("Profiles")).small().color(theme::MUTED));
        if item(ui, app, true, "⇩ Download profiles", "Save all profiles to a JSON file") {
            app.dialog = Some(FileDialog::ExportProfiles);
        }
        if item(ui, app, true, "⇧ Upload profiles", "Import profiles from a JSON file") {
            app.dialog = Some(FileDialog::ImportProfiles);
        }
    });
    named(menu.response.on_hover_text(app.t("App menu")), egui::Role::Button, app.t("App menu"));
}

// --- the operations -------------------------------------------------------------------------------

enum Action {
    Select(u64),
    Delete(u64),
    Duplicate(u64),
    Move(u64, i64),
    AddClause(u64, bool),
}

fn combo<T: Copy + PartialEq>(
    ui: &mut egui::Ui,
    i18n: &I18n,
    id: impl std::hash::Hash + std::fmt::Debug,
    caption: &str,
    value: &mut T,
    options: &[T],
    label: impl Fn(T) -> &'static str,
) -> bool {
    let before = *value;
    ui.horizontal(|ui| {
        let caption = ui.label(i18n.t(caption));
        egui::ComboBox::from_id_salt(id)
            .selected_text(i18n.t(label(*value)))
            .width(110.0)
            .show_ui(ui, |ui| {
                for option in options {
                    ui.selectable_value(value, *option, i18n.t(label(*option)));
                }
            })
            .response
            .labelled_by(caption.id);
    });
    *value != before
}

fn number(ui: &mut egui::Ui, i18n: &I18n, caption: &str, value: &mut u64, min: u64) -> bool {
    ui.horizontal(|ui| {
        let label = ui.label(i18n.t(caption));
        ui.add(egui::DragValue::new(value).range(min..=u64::MAX / 4).speed(0.1)).labelled_by(label.id).changed()
    })
    .inner
}

/// Column (shown from 1, kept from 0) and width
fn column_fields(ui: &mut egui::Ui, i18n: &I18n, pos: &mut u64, len: &mut u64) -> bool {
    let mut column = *pos + 1;
    let mut changed = number(ui, i18n, "Column", &mut column, 1);
    *pos = column.saturating_sub(1);
    changed |= number(ui, i18n, "Width", len, 1);
    changed
}

fn check(ui: &mut egui::Ui, i18n: &I18n, value: &mut bool, label: &str) -> bool {
    ui.checkbox(value, i18n.t(label)).changed()
}

fn line(ui: &mut egui::Ui, i18n: &I18n, caption: &str, value: &mut String, hint: &str) -> bool {
    ui.horizontal(|ui| {
        let label = ui.label(i18n.t(caption));
        ui.add(TextEdit::singleline(value).desired_width(f32::INFINITY).hint_text(i18n.t(hint).to_string()).font(egui::TextStyle::Monospace)).labelled_by(label.id).changed()
    })
    .inner
}

/// A field that may hold several lines; the count of its line breaks is shown beside its caption
fn area(ui: &mut egui::Ui, i18n: &I18n, caption: Option<&str>, value: &mut String, hint: &str, labelled_by: Option<egui::Id>) -> bool {
    let mut label_id = labelled_by;
    if let Some(caption) = caption {
        ui.horizontal(|ui| {
            label_id = Some(ui.label(i18n.t(caption)).id);
            let breaks = value.matches('\n').count();
            if breaks > 0 {
                let only = if breaks == value.len() { format!(" \u{b7} {}", i18n.t("line break only")) } else { String::new() };
                ui.label(RichText::new(format!("\u{21b5} LF \u{d7} {breaks}{only}")).small().color(theme::ACCENT_STRONG));
            }
        });
    }
    let response = ui.add(TextEdit::multiline(value).desired_rows(1).desired_width(f32::INFINITY).hint_text(i18n.t(hint).to_string()).font(egui::TextStyle::Monospace));
    match label_id {
        Some(id) => response.labelled_by(id).changed(),
        None => response.changed(),
    }
}

fn filter_body(ui: &mut egui::Ui, i18n: &I18n, id: u64, filter: &mut FilterOp, sources: &[SourceDesc], actions: &mut Vec<Action>) -> bool {
    let mut changed = false;
    meta(ui, i18n.t("Keeps lines containing any Include text, then removes lines containing any Exclude text. Each line of a box is one alternative."));
    for exclude in [false, true] {
        let title = ui
            .horizontal(|ui| {
                let title = ui.strong(i18n.t(if exclude { "Exclude" } else { "Include" })).id;
                if ui.small_button(i18n.t(if exclude { "⊖ Exclude" } else { "⊕ Include" })).clicked() {
                    actions.push(Action::AddClause(id, exclude));
                }
                title
            })
            .inner;
        let clauses = if exclude { &mut filter.exclude } else { &mut filter.include };
        if clauses.is_empty() {
            meta(ui, i18n.t(if exclude { "No exclude clauses." } else { "No include clauses." }));
        }
        let mut removed = None;
        for (index, clause) in clauses.iter_mut().enumerate() {
            ui.push_id((exclude, index), |ui| {
                ui.horizontal(|ui| {
                    ui.with_layout(Layout::right_to_left(Align::Min), |ui| {
                        if icon(ui, egui::Button::new("×").small(), i18n.t("Remove clause")).clicked() {
                            removed = Some(index);
                        }
                        changed |= ui.checkbox(&mut clause.case_sensitive, "Aa").on_hover_text(i18n.t("Match case")).changed();
                        changed |= area(ui, i18n, None, &mut clause.text, "Text; one alternative per line", Some(title));
                    });
                });
            });
        }
        if let Some(index) = removed {
            clauses.remove(index);
            changed = true;
        }
    }
    ui.strong(i18n.t("Only in files")).on_hover_text(i18n.t("Check files to filter only their lines; none checked means all files"));
    if sources.is_empty() {
        meta(ui, i18n.t("No source list available."));
    }
    ui.horizontal_wrapped(|ui| {
        for source in sources {
            let key = source.filter_key();
            let mut checked = filter.sources.contains(&key);
            let frame = egui::Frame::NONE.fill(theme::source_tint(source.tone, true)).corner_radius(5).inner_margin(egui::Margin::symmetric(4, 1));
            let toggled = frame
                .show(ui, |ui| {
                    let toggle = ui.checkbox(&mut checked, "").on_hover_text(source.display());
                    toggle.widget_info(|| egui::WidgetInfo::selected(egui::Role::CheckBox, true, checked, source.display()));
                    toggle.changed()
                })
                .inner;
            if toggled {
                filter.sources.retain(|entry| *entry != key);
                if checked {
                    filter.sources.push(key);
                }
                changed = true;
            }
        }
    });
    changed
}

fn operator_body(ui: &mut egui::Ui, i18n: &I18n, operator: &mut Operator, sources: &[SourceDesc], actions: &mut Vec<Action>) -> bool {
    let id = operator.id;
    let mut changed = false;
    match &mut operator.kind {
        OpKind::Filter(filter) => changed |= filter_body(ui, i18n, id, filter, sources, actions),
        OpKind::Replace(replace) => {
            changed |= area(ui, i18n, Some("Find"), &mut replace.search, "Text to find; Enter inserts a line break", None);
            changed |= area(ui, i18n, Some("Replace with"), &mut replace.replacement, "Empty deletes the text; Enter inserts a line break", None);
        }
        OpKind::Sort(sort) => {
            ui.horizontal_wrapped(|ui| {
                changed |= combo(ui, i18n, (id, "mode"), "Value", &mut sort.mode, SortMode::ALL, SortMode::label);
                changed |= combo(ui, i18n, (id, "order"), "Order", &mut sort.order, SortOrder::ALL, SortOrder::label);
                changed |= combo(ui, i18n, (id, "type"), "Type", &mut sort.value_type, &ValueType::ALL, ValueType::label);
            });
            if sort.mode == SortMode::Anchor {
                changed |= line(ui, i18n, "Text", &mut sort.anchor, "Text next to the value");
                changed |= combo(ui, i18n, (id, "side"), "Value is", &mut sort.anchor_side, AnchorSide::ALL, AnchorSide::label);
            } else {
                ui.horizontal_wrapped(|ui| changed |= column_fields(ui, i18n, &mut sort.pos, &mut sort.len));
            }
            meta(ui, i18n.t("Rows without a readable value move together with the row above them."));
        }
        OpKind::Diff(diff) => {
            ui.horizontal_wrapped(|ui| {
                changed |= column_fields(ui, i18n, &mut diff.pos, &mut diff.len);
                changed |= combo(ui, i18n, (id, "type"), "Type", &mut diff.value_type, &ValueType::ALL, ValueType::label);
                changed |= ui.checkbox(&mut diff.invert, i18n.t("Reverse")).on_hover_text(i18n.t("Turn differences back into values (running total)")).changed();
            });
            meta(ui, i18n.t("Replaces the value in each row with its change from the previous row; for text only the changed characters stay. Rows without a readable value stay with the row above."));
        }
        OpKind::Extract(extract) => {
            meta(ui, i18n.t("Outputs the found values, one per row. Regex and Between can search the whole view, even one-line JSON."));
            changed |= combo(ui, i18n, (id, "mode"), "Mode", &mut extract.mode, ExtractMode::ALL, ExtractMode::label);
            match extract.mode {
                ExtractMode::Regex => {
                    changed |= area(ui, i18n, Some("Pattern"), &mut extract.pattern, r#""ConnectionId"\s*:\s*"([^"]+)""#, None);
                    ui.horizontal_wrapped(|ui| {
                        changed |= number(ui, i18n, "Group", &mut extract.group, 0);
                        changed |= combo(ui, i18n, (id, "scope"), "Search in", &mut extract.scope, ExtractScope::ALL, ExtractScope::label);
                        changed |= check(ui, i18n, &mut extract.all_matches, "All matches");
                        changed |= check(ui, i18n, &mut extract.case_sensitive, "Match case");
                    });
                }
                ExtractMode::Between => {
                    changed |= line(ui, i18n, "After", &mut extract.start_anchor, "Opening text");
                    changed |= line(ui, i18n, "Before", &mut extract.end_anchor, "Closing text");
                    ui.horizontal_wrapped(|ui| {
                        changed |= combo(ui, i18n, (id, "scope"), "Search in", &mut extract.scope, ExtractScope::ALL, ExtractScope::label);
                        changed |= check(ui, i18n, &mut extract.all_matches, "All matches");
                        changed |= check(ui, i18n, &mut extract.case_sensitive, "Match case");
                    });
                }
                ExtractMode::Column => {
                    ui.horizontal_wrapped(|ui| changed |= column_fields(ui, i18n, &mut extract.pos, &mut extract.len));
                }
            }
            if extract.mode == ExtractMode::Column || extract.scope == ExtractScope::Line {
                ui.horizontal_wrapped(|ui| {
                    changed |= combo(ui, i18n, (id, "rows"), "Rows", &mut extract.row_mode, RowMode::ALL, RowMode::label);
                    if extract.row_mode == RowMode::Range {
                        changed |= number(ui, i18n, "From row", &mut extract.from_row, 1);
                        changed |= number(ui, i18n, "To row", &mut extract.to_row, 1);
                    }
                });
            }
            ui.horizontal_wrapped(|ui| {
                changed |= check(ui, i18n, &mut extract.trim, "Trim spaces");
                changed |= check(ui, i18n, &mut extract.skip_empty, "Skip empty");
            });
        }
        OpKind::Unique(unique) => {
            meta(ui, i18n.t("Keeps the first occurrence of each row."));
            ui.horizontal_wrapped(|ui| {
                changed |= combo(ui, i18n, (id, "order"), "Order", &mut unique.order, UniqueOrder::ALL, UniqueOrder::label);
                changed |= check(ui, i18n, &mut unique.trim, "Trim spaces");
                changed |= check(ui, i18n, &mut unique.ignore_empty, "Drop empty rows");
                changed |= check(ui, i18n, &mut unique.case_sensitive, "Match case");
            });
        }
        OpKind::Join(join) => {
            meta(ui, i18n.t("Joins all rows into one line. With Escapes on, \\n and \\t mean a line break and a tab."));
            changed |= line(ui, i18n, "Delimiter", &mut join.delimiter, "|");
            changed |= line(ui, i18n, "Prefix", &mut join.prefix, "");
            changed |= line(ui, i18n, "Suffix", &mut join.suffix, "");
            ui.horizontal_wrapped(|ui| {
                changed |= check(ui, i18n, &mut join.trim, "Trim spaces");
                changed |= check(ui, i18n, &mut join.skip_empty, "Skip empty");
                changed |= check(ui, i18n, &mut join.interpret_escapes, "Escapes");
            });
        }
    }
    changed
}

fn operator_card(ui: &mut egui::Ui, i18n: &I18n, operator: &mut Operator, index: usize, selected: bool, sources: &[SourceDesc], actions: &mut Vec<Action>) -> bool {
    let id = operator.id;
    let mut changed = false;
    // The card itself takes the clicks its fields do not: they select the operation
    let card = ui.scope_builder(egui::UiBuilder::new().id_salt(("operator", id)).sense(Sense::click()), |ui| {
        let frame = egui::Frame::NONE
            .fill(if selected { Color32::from_rgb(0xf3, 0xf8, 0xff) } else { theme::PANEL })
            .stroke(if selected { Stroke::new(2.0, theme::ACCENT) } else { Stroke::new(1.0, theme::BORDER) })
            .corner_radius(8)
            .inner_margin(8);
        frame.show(ui, |ui| {
            ui.set_width(ui.available_width());
            if !operator.enabled {
                ui.style_mut().visuals.override_text_color = Some(theme::MUTED);
            }
            ui.horizontal(|ui| {
                ui.label(RichText::new(format!("#{}", index + 1)).color(theme::MUTED));
                let name = RichText::new(i18n.t(operator.op_type().label())).strong();
                if ui.checkbox(&mut operator.enabled, name).on_hover_text(i18n.t("Uncheck to skip this operation")).changed() {
                    changed = true;
                    actions.push(Action::Select(id));
                }
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    if icon(ui, egui::Button::new(RichText::new("×").color(theme::DANGER)).small(), i18n.t("Delete")).clicked() {
                        actions.push(Action::Delete(id));
                    }
                    if icon(ui, egui::Button::new("⊞").small(), i18n.t("Duplicate")).clicked() {
                        actions.push(Action::Duplicate(id));
                    }
                    if icon(ui, egui::Button::new("↓").small(), i18n.t("Move down")).clicked() {
                        actions.push(Action::Move(id, 1));
                    }
                    if icon(ui, egui::Button::new("↑").small(), i18n.t("Move up")).clicked() {
                        actions.push(Action::Move(id, -1));
                    }
                });
            });
            if operator_body(ui, i18n, operator, sources, actions) {
                changed = true;
                actions.push(Action::Select(id));
            }
        });
    });
    if card.response.clicked() {
        actions.push(Action::Select(id));
    }
    changed
}

fn profiles_panel(ui: &mut egui::Ui, app: &mut App) {
    ui.horizontal(|ui| {
        ui.strong(app.t("Profiles"));
        ui.label(RichText::new(app.t("Click to switch")).small().color(theme::MUTED));
    });
    let names: Vec<String> = app.profiles.ordered_names().into_iter().take(PROFILE_RECENT_LIMIT).collect();
    let new_active = app.loaded_profile.is_empty();
    let new_dirty = new_active && !app.work.operators.is_empty();
    ui.horizontal_wrapped(|ui| {
        let tip = if !new_active {
            "Start an empty pipeline. Right-click to save the current one."
        } else if new_dirty {
            "Unsaved pipeline with changes. Click to start over, right-click to save it."
        } else {
            "Current unsaved pipeline. Click to start over, right-click to save it."
        };
        let label = format!("{}{}", app.t("New"), if new_dirty { " •" } else { "" });
        let chip = ui.add(egui::Button::new(label).selected(new_active)).on_hover_text(app.t(tip));
        if chip.clicked() {
            app.clear_workspace();
        }
        chip.context_menu(|ui| {
            if ui.button(app.t("Save as")).clicked() {
                app.prompt = Some(Prompt::SaveAs { name: app.save_as_suggestion() });
                ui.close();
            }
        });
        for name in &names {
            let implicit = app.profiles.get(name).is_some_and(|profile| profile.implicit);
            let loaded = app.loaded_profile == *name;
            let dirty = app.is_profile_dirty(name);
            let shown = if implicit { app.t(IMPLICIT_PROFILE_NAME).to_string() } else { name.clone() };
            let tip = match (loaded, implicit, dirty) {
                (true, true, true) => app.t("Pipeline from the last session, changed. Right-click to save or delete it.").to_string(),
                (true, true, false) => app.t("Pipeline from the last session. Right-click to save or delete it.").to_string(),
                (true, false, true) => app.tf("'{0}' with unsaved changes. Right-click to save, rename, or delete.", &[name]),
                (true, false, false) => app.tf("'{0}' is in use. Right-click for more, drag to reorder.", &[name]),
                (false, true, _) => app.t("Pipeline from the last session. Click to use it, right-click to delete it.").to_string(),
                (false, false, _) => app.tf("Click to use '{0}'. Right-click for more, drag to reorder.", &[name]),
            };
            let label = format!("{shown}{}", if dirty { " •" } else { "" });
            let chip = ui.add(egui::Button::new(label).selected(loaded).sense(Sense::click_and_drag())).on_hover_text(tip);
            if chip.clicked() {
                app.load_profile(name);
            }
            // Named chips are reordered by dragging one onto another
            if !implicit && chip.drag_started() {
                chip.dnd_set_drag_payload(name.clone());
            }
            if let Some(dragged) = chip.dnd_release_payload::<String>() {
                if !implicit && *dragged != *name {
                    app.move_profile(&dragged, Some(name));
                }
            }
            chip.context_menu(|ui| {
                if loaded && dirty && ui.button(app.t("Save")).clicked() {
                    app.save_profile(name);
                    ui.close();
                }
                if loaded && ui.button(app.t("Save as")).clicked() {
                    app.prompt = Some(Prompt::SaveAs { name: app.save_as_suggestion() });
                    ui.close();
                }
                if !implicit && ui.button(app.t("Rename")).clicked() {
                    app.prompt = Some(Prompt::Rename { profile: name.clone(), name: name.clone() });
                    ui.close();
                }
                if implicit && ui.button(app.t("Save as")).clicked() {
                    // "Last" is kept by giving it a name
                    app.prompt = Some(Prompt::Rename { profile: name.clone(), name: String::new() });
                    ui.close();
                }
                if ui.button(RichText::new(app.t("Delete profile")).color(theme::DANGER)).clicked() {
                    app.delete_profile(name);
                    ui.close();
                }
            });
        }
    });
}

fn operations(ui: &mut egui::Ui, app: &mut App, stage: Option<&Arc<Stage>>) {
    ui.horizontal(|ui| {
        let title = ui.add(egui::Button::new(RichText::new(app.t("Operations")).heading()).frame(false));
        if title.clicked() {
            app.toggle_raw();
        }
        let showing = match app.work.selected_index() {
            None => app.t("Showing: original").to_string(),
            Some(index) => app.tf("Showing: after #{0} {1}", &[&(index + 1), &app.t(app.work.operators[index].op_type().label())]),
        };
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| pill(ui, showing));
    });
    meta(ui, app.t("Operations run from top to bottom. Click one to see the result up to it."));

    egui::Panel::bottom("profiles").resizable(false).show_separator_line(true).show(ui, |ui| {
        ui.add_space(4.0);
        profiles_panel(ui, app);
        ui.add_space(4.0);
    });

    let sources: Arc<Vec<SourceDesc>> = stage.map(|stage| stage.sources.clone()).unwrap_or_default();
    let selected = app.work.selected_operator_id;
    let mut actions = Vec::new();
    let mut changed = false;
    // The operations are edited in place while the rest of the app is only read
    let mut operators = std::mem::take(&mut app.work.operators);
    egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| {
        if operators.is_empty() {
            egui::Frame::NONE.fill(theme::PANEL).stroke(Stroke::new(1.0, theme::BORDER)).corner_radius(8).inner_margin(8).show(ui, |ui| {
                ui.set_width(ui.available_width());
                meta(ui, app.t("No operations yet. Select text in the file and press Enter, or use the buttons above."));
            });
        }
        for (index, operator) in operators.iter_mut().enumerate() {
            changed |= operator_card(ui, &app.i18n, operator, index, selected == Some(operator.id), &sources, &mut actions);
            ui.add_space(2.0);
        }
    });
    app.work.operators = operators;
    for action in actions {
        match action {
            Action::Select(id) => app.select_operator(Some(id)),
            Action::Delete(id) => app.delete_operator(id),
            Action::Duplicate(id) => app.duplicate_operator(id),
            Action::Move(id, delta) => {
                app.select_operator(Some(id));
                app.move_operator(id, delta);
            }
            Action::AddClause(id, exclude) => {
                let text = app.selected_text();
                app.select_operator(Some(id));
                if let Some(OpKind::Filter(filter)) = app.work.index_of(id).map(|index| &mut app.work.operators[index].kind) {
                    let clause = Clause::new(&text);
                    if exclude {
                        filter.exclude.push(clause)
                    } else {
                        filter.include.push(clause)
                    }
                    changed = true;
                }
            }
        }
    }
    if changed {
        app.operator_edited();
    }
}

// --- the file view and its bars -------------------------------------------------------------------

fn viewer_toolbar(ui: &mut egui::Ui, app: &mut App, stage: Option<&Arc<Stage>>) {
    ui.horizontal(|ui| {
        let tip = match (&app.source, app.work.selected_index()) {
            (None, _) => app.t("No source is loaded yet.").to_string(),
            (_, None) => app.t("The original file, no operation applied.").to_string(),
            (_, Some(index)) => app.tf("The file after operations 1 to {0} ({1}).", &[&(index + 1), &app.t(app.work.operators[index].op_type().label())]),
        };
        ui.label(RichText::new(app.t("File view")).heading()).on_hover_text(tip);
        if ui.button(app.t("Find")).on_hover_text(app.t("Find in file view (Ctrl+F)")).clicked() {
            app.show_find_bar(true, true);
        }
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            let scroll = match stage {
                None => app.tf("Row {0}/{1}, col {2}/{3}", &[&1, &1, &1, &1]),
                Some(stage) => {
                    let rows = stage.len().max(1);
                    let row = ((app.view.scroll_top / app.view.row_height as f64).floor().max(0.0) as usize + 1).min(rows);
                    app.tf("Row {0}/{1}, col {2}/{3}", &[&grouped(row as u64), &grouped(rows as u64), &grouped(app.view.scroll_left as u64 + 1), &grouped(stage.max_cols.max(1))])
                }
            };
            pill(ui, scroll);
            let selection = match (stage, app.selection.map(|selection| selection.normalized())) {
                (Some(stage), Some(selection)) => {
                    let chars = app.selection_len(stage);
                    app.tf(
                        "Selection: rows {0}-{1}, cols {2}-{3} ({4}x{5}, len {6})",
                        &[
                            &grouped(selection.start.row as u64 + 1),
                            &grouped(selection.end.row as u64 + 1),
                            &grouped(selection.start.col as u64 + 1),
                            &grouped(selection.end.col as u64),
                            &grouped((selection.end.row - selection.start.row + 1) as u64),
                            &(selection.end.col as i64 - selection.start.col as i64),
                            &grouped(chars),
                        ],
                    )
                }
                _ => app.t("Selection: none").to_string(),
            };
            pill(ui, selection);
            let hint = app.t("Click selects a value, Ctrl+click a smaller part. Middle click shows the previous stage.");
            ui.add(egui::Label::new(RichText::new(hint).small().color(theme::MUTED)).truncate());
        });
    });
}

fn find_bar(ui: &mut egui::Ui, app: &mut App) {
    ui.horizontal(|ui| {
        let label = ui.label(app.t("Find"));
        let hint = app.t("Text in current file view").to_string();
        let input =
            ui.add(TextEdit::singleline(&mut app.find.query).id(find_input_id()).desired_width(260.0).hint_text(hint).font(egui::TextStyle::Monospace)).labelled_by(label.id);
        if input.changed() {
            app.find_query_changed();
        }
        if app.find.focus {
            // The old query is selected, so that typing replaces it
            app.find.focus = false;
            input.request_focus();
            let mut state = egui::text_edit::TextEditState::load(ui.ctx(), find_input_id()).unwrap_or_default();
            let end = egui::text::CCursor::new(app.find.query.chars().count());
            state.cursor.set_char_range(Some(egui::text_selection::CCursorRange::two(egui::text::CCursor::new(0), end)));
            state.store(ui.ctx(), find_input_id());
        }
        // Enter in the box finds the next match and keeps the keyboard there
        if input.lost_focus() && ui.input(|input| input.key_pressed(Key::Enter)) {
            let forward = !ui.input(|input| input.modifiers.shift);
            app.find_in_view(forward);
            input.request_focus();
        }
        if icon(ui, egui::Button::new("↑"), app.t("Previous match (Shift+Enter or Shift+F3)")).clicked() {
            app.find_in_view(false);
        }
        if icon(ui, egui::Button::new("↓"), app.t("Next match (Enter or F3)")).clicked() {
            app.find_in_view(true);
        }
        let case_tip = app.t(if app.find.case_sensitive { "Match case: on" } else { "Match case: off" });
        if icon(ui, egui::Button::new("Aa").selected(app.find.case_sensitive), case_tip).clicked() {
            app.toggle_find_case();
        }
        ui.label(RichText::new(&app.find.status).color(if app.find.not_found { theme::DANGER } else { theme::MUTED }));
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            if icon(ui, egui::Button::new("×"), app.t("Close find (Esc)")).clicked() {
                app.close_find_bar();
            }
            ui.label(RichText::new("Enter / Shift+Enter").small().color(theme::MUTED));
        });
    });
}

fn status_bar(ui: &mut egui::Ui, app: &mut App, stage: Option<&Arc<Stage>>) {
    ui.horizontal(|ui| {
        ui.strong(app.t("Status:"));
        ui.add(egui::Label::new(app.status.lines().next().unwrap_or("")).truncate()).on_hover_text(&app.status);
        if app.offer_zip && ui.button(app.t("⇄ Interleave rows (Zip)")).on_hover_text(app.t("Show the loaded files interleaved row by row instead of one after another")).clicked()
        {
            app.zip_loaded_sources();
        }
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            let busy = app.busy();
            if app.is_busy() {
                ui.spinner();
                ui.label(RichText::new(busy).color(theme::ACCENT_STRONG));
            }
            let source = stage.zip(app.view.hovered_row).and_then(|(stage, row)| stage.source_of(row)).map_or("-", SourceDesc::display);
            ui.add(egui::Label::new(RichText::new(app.tf("Source: {0}", &[&source])).color(theme::MUTED)).truncate());
        });
    });
}

// --- dialogs --------------------------------------------------------------------------------------

fn help(ctx: &egui::Context, app: &mut App) {
    let modal = egui::Modal::new(egui::Id::unique("help")).show(ctx, |ui| {
        ui.set_width(760.0_f32.min(ui.ctx().content_rect().width() - 60.0));
        let mut close = ui
            .horizontal(|ui| {
                ui.heading(app.t("How to use Log Explorer"));
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| icon(ui, egui::Button::new("×"), app.t("Close help")).clicked()).inner
            })
            .inner;
        egui::ScrollArea::vertical().max_height(ui.ctx().content_rect().height() - 140.0).show(ui, |ui| {
            let sections: [(&str, &[&str]); 6] = [
                ("Loading files", &["help.load.1", "help.load.2", "help.load.3"]),
                ("Browsing", &["help.browse.1", "help.browse.2"]),
                (
                    "Operations",
                    &[
                        "help.operations.1",
                        "help.operations.filter",
                        "help.operations.replace",
                        "help.operations.sort",
                        "help.operations.diff",
                        "help.operations.extract",
                        "help.operations.unique",
                        "help.operations.join",
                        "help.operations.2",
                    ],
                ),
                ("Profiles", &["help.profiles.1", "help.profiles.2"]),
                ("Selecting and copying", &["help.select.1", "help.select.2", "help.select.3"]),
                ("Find", &["help.find.1"]),
            ];
            for (title, keys) in sections {
                ui.add_space(6.0);
                ui.label(RichText::new(app.t(title)).strong().size(16.0));
                for key in keys {
                    let bullet = if key.matches('.').count() == 2 && !key.ends_with(|last: char| last.is_ascii_digit()) { "•  " } else { "" };
                    ui.add(egui::Label::new(markup(&format!("{bullet}{}", app.t(key)), 14.0)).wrap());
                }
            }
            ui.add_space(6.0);
            ui.label(RichText::new(app.t("Keyboard shortcuts")).strong().size(16.0));
            ui.add(egui::Label::new(markup(app.t("help.keys.1"), 14.0)).wrap());
            let rows: [(&str, &str); 14] = [
                ("<kbd>F1</kbd>", "help.keys.f1"),
                ("<kbd>Ctrl+F</kbd>", "help.keys.find"),
                ("<kbd>F3</kbd> / <kbd>Shift+F3</kbd>", "help.keys.next"),
                ("<kbd>Ctrl+F3</kbd> / <kbd>Ctrl+Shift+F3</kbd>", "help.keys.findSelection"),
                ("<kbd>Enter</kbd>", "help.keys.enter"),
                ("<kbd>Esc</kbd>", "help.keys.esc"),
                ("<kbd>Delete</kbd>", "help.keys.delete"),
                ("<kbd>F</kbd> <kbd>R</kbd> <kbd>S</kbd> <kbd>D</kbd>", "help.keys.add"),
                ("<kbd>Ctrl+A</kbd>", "help.keys.all"),
                ("<kbd>Ctrl+C</kbd>", "help.keys.copy"),
                ("<kbd>Ctrl+Esc</kbd>", "help.keys.clear"),
                ("<kbd>↑</kbd> <kbd>↓</kbd> <kbd>←</kbd> <kbd>→</kbd> <kbd>Page Up</kbd> <kbd>Page Down</kbd> <kbd>Home</kbd> <kbd>End</kbd>", "help.keys.move"),
                ("help.keys.wheelKey", "help.keys.wheel"),
                ("help.keys.middleKey", "help.keys.middle"),
            ];
            egui::Grid::new("help keys").num_columns(2).striped(true).spacing([16.0, 6.0]).show(ui, |ui| {
                for (keys, description) in rows {
                    ui.label(markup(app.t(keys), 13.0));
                    ui.add(egui::Label::new(markup(app.t(description), 13.0)).wrap());
                    ui.end_row();
                }
            });
            ui.add_space(8.0);
            close |= ui.button("OK").clicked();
        });
        close
    });
    if modal.should_close() || modal.inner {
        app.help_open = false;
    }
}

fn load_question(ctx: &egui::Context, app: &mut App) {
    let Some(pending) = &app.pending_load else { return };
    let has_existing = pending.has_existing;
    let several = pending.incoming.len() > 1;
    let names: Vec<&str> = pending.incoming.iter().map(|source| source.name.as_str()).collect();
    let incoming = match names.as_slice() {
        [only] => only.to_string(),
        [first, rest @ ..] => app.tf("{0} + {1} more", &[first, &rest.len()]),
        [] => String::new(),
    };
    let mut answer: Option<Option<LoadAction>> = None;
    let modal = egui::Modal::new(egui::Id::unique("load question")).show(ctx, |ui| {
        ui.set_width(520.0);
        ui.heading(app.t(if has_existing { "How should the new files be loaded?" } else { "How should the files be combined?" }));
        let summary = match &app.source {
            Some(source) if has_existing => app.tf("Loaded: {0}. New: {1}.", &[&source.name, &incoming]),
            _ => app.tf("Files: {0}.", &[&incoming]),
        };
        ui.label(summary);
        ui.add_space(6.0);
        ui.columns(if has_existing { 2 } else { 1 }, |columns| {
            let mut column = 0;
            if has_existing {
                let ui = &mut columns[0];
                ui.strong(app.t("Add to current"));
                meta(ui, app.t("Append puts them after the loaded rows, Zip interleaves them row by row."));
                ui.horizontal(|ui| {
                    if ui.button(app.t("Append")).clicked() {
                        answer = Some(Some(LoadAction::Append));
                    }
                    if ui.button(app.t("Zip")).clicked() {
                        answer = Some(Some(LoadAction::Zip));
                    }
                });
                column = 1;
            }
            let ui = &mut columns[column];
            ui.strong(app.t(if has_existing { "Replace current" } else { "Combine files" }));
            meta(
                ui,
                app.t(if has_existing {
                    "Close the loaded content and show only the new files."
                } else {
                    "Concat shows the files one after another, Zip interleaves them row by row."
                }),
            );
            ui.horizontal(|ui| {
                if !several && ui.button(app.t("Replace")).clicked() {
                    answer = Some(Some(LoadAction::Replace));
                }
                if several && ui.button(app.t("Concat")).clicked() {
                    answer = Some(Some(LoadAction::ReplaceAppend));
                }
                // Beside "Append/Zip" of the other column this one needs its own name
                let zip = if has_existing { format!("{} ({})", app.t("Zip"), app.t("Replace")) } else { app.t("Zip").to_string() };
                if several && ui.button(zip).clicked() {
                    answer = Some(Some(LoadAction::ReplaceZip));
                }
            });
        });
        ui.add_space(6.0);
        meta(ui, app.t("Zip leaves a row empty where a file has no more rows. Esc cancels."));
        if ui.button(app.t("Cancel load")).clicked() {
            answer = Some(None);
        }
    });
    if modal.should_close() {
        answer = Some(None);
    }
    if let Some(answer) = answer {
        app.resolve_load(answer);
    }
}

fn prompt(ctx: &egui::Context, app: &mut App) {
    let Some(current) = app.prompt.take() else { return };
    enum Outcome {
        Keep(Prompt),
        Close,
        SaveAs(String, bool),
        Rename(String, String, bool),
    }
    let mut outcome = None;
    let modal = egui::Modal::new(egui::Id::unique("prompt")).show(ctx, |ui| {
        ui.set_width(380.0);
        let buttons = |ui: &mut egui::Ui, ok: &str| {
            ui.horizontal(|ui| {
                let accepted = ui.button(ok).clicked();
                let canceled = ui.button(app.t("Cancel")).clicked();
                (accepted, canceled)
            })
            .inner
        };
        let name_field = |ui: &mut egui::Ui, name: &mut String, question: egui::Id| {
            let field = ui.add(TextEdit::singleline(name).desired_width(f32::INFINITY)).labelled_by(question);
            let entered = field.lost_focus() && ui.input(|input| input.key_pressed(Key::Enter));
            if !field.has_focus() && !entered {
                field.request_focus();
            }
            entered
        };
        match current {
            Prompt::SaveAs { mut name } => {
                let question = ui.label(app.t("Save current profile as:")).id;
                let entered = name_field(ui, &mut name, question);
                let (accepted, canceled) = buttons(ui, app.t("Save"));
                outcome = Some(if accepted || entered {
                    Outcome::SaveAs(name, false)
                } else if canceled {
                    Outcome::Close
                } else {
                    Outcome::Keep(Prompt::SaveAs { name })
                });
            }
            Prompt::Rename { profile, mut name } => {
                let question = ui.label(app.t(if profile == IMPLICIT_PROFILE_NAME { "Rename 'Last' into a new profile:" } else { "Rename profile:" })).id;
                let entered = name_field(ui, &mut name, question);
                let (accepted, canceled) = buttons(ui, app.t("Rename"));
                outcome = Some(if accepted || entered {
                    Outcome::Rename(profile, name, false)
                } else if canceled {
                    Outcome::Close
                } else {
                    Outcome::Keep(Prompt::Rename { profile, name })
                });
            }
            Prompt::Overwrite { name, rename_from } => {
                ui.label(app.tf("Overwrite existing profile '{0}'?", &[&name]));
                let (accepted, canceled) = buttons(ui, "OK");
                outcome = Some(if accepted {
                    match rename_from {
                        Some(profile) => Outcome::Rename(profile, name, true),
                        None => Outcome::SaveAs(name, true),
                    }
                } else if canceled {
                    Outcome::Close
                } else {
                    Outcome::Keep(Prompt::Overwrite { name, rename_from })
                });
            }
        }
    });
    let closed = modal.should_close();
    match outcome {
        Some(Outcome::Keep(kept)) if !closed => app.prompt = Some(kept),
        Some(Outcome::SaveAs(name, confirmed)) => {
            app.save_profile_as(&name, confirmed);
        }
        Some(Outcome::Rename(profile, name, confirmed)) => {
            app.rename_profile(&profile, &name, confirmed);
        }
        _ => {}
    }
}

// --- files ----------------------------------------------------------------------------------------

fn file_dialogs(ctx: &egui::Context, app: &mut App) {
    if let Some(dialog) = app.dialog.take() {
        let (kind, name) = match dialog {
            FileDialog::Open => (FileDialogKind::Open, String::new()),
            FileDialog::ImportProfiles => (FileDialogKind::ImportProfiles, String::new()),
            FileDialog::ExportView => (FileDialogKind::ExportView, app.export_view_name()),
            FileDialog::ExportProfiles => (FileDialogKind::ExportProfiles, "log-explorer-profiles.json".to_string()),
        };
        // The dialog runs beside the UI so that the window keeps painting
        let (sender, receiver) = std::sync::mpsc::channel();
        let repaint = ctx.clone();
        std::thread::spawn(move || {
            let picked = match kind {
                FileDialogKind::Open => rfd::FileDialog::new().pick_files(),
                FileDialogKind::ImportProfiles => rfd::FileDialog::new().add_filter("JSON", &["json"]).pick_file().map(|path| vec![path]),
                FileDialogKind::ExportView | FileDialogKind::ExportProfiles => rfd::FileDialog::new().set_file_name(name).save_file().map(|path| vec![path]),
            };
            let _ = sender.send((kind, picked));
            repaint.request_repaint();
        });
        app.dialog_result = Some(receiver);
    }
    let Some(result) = app.dialog_result.as_ref().and_then(|receiver| receiver.try_recv().ok()) else { return };
    app.dialog_result = None;
    let (kind, Some(paths)) = result else { return };
    match kind {
        FileDialogKind::Open => app.open_paths(paths, None, None),
        FileDialogKind::ImportProfiles => app.import_profiles_file(&paths[0]),
        FileDialogKind::ExportView => app.export_view_to(&paths[0]),
        FileDialogKind::ExportProfiles => {
            let json = app.export_profiles_json();
            if let Err(error) = std::fs::write(&paths[0], json) {
                app.status = error.to_string();
            }
        }
    }
}

fn dropped_files(ctx: &egui::Context, app: &mut App) {
    let dropped: Vec<std::path::PathBuf> =
        ctx.input(|input| input.raw.dropped_files.iter().map(|file| file.path().to_path_buf()).filter(|path| !path.as_os_str().is_empty()).collect());
    if !dropped.is_empty() {
        app.open_paths(dropped, None, None);
    }
    if ctx.input(|input| !input.raw.hovered_files.is_empty()) {
        let rect = ctx.content_rect();
        let painter = ctx.layer_painter(egui::LayerId::new(egui::Order::Foreground, egui::Id::unique("drop zone")));
        painter.rect_filled(rect, 0.0, Color32::from_rgba_unmultiplied(0xee, 0xf3, 0xfb, 235));
        painter.rect_stroke(rect.shrink(16.0), 16.0, Stroke::new(3.0, theme::ACCENT), egui::StrokeKind::Inside);
        painter.text(rect.center() - egui::vec2(0.0, 14.0), egui::Align2::CENTER_CENTER, app.t("Drop files to open them"), FontId::proportional(24.0), theme::TEXT);
        painter.text(
            rect.center() + egui::vec2(0.0, 18.0),
            egui::Align2::CENTER_CENTER,
            app.t("A JSON profile bundle is imported instead."),
            FontId::proportional(14.0),
            theme::MUTED,
        );
    }
}

/// One frame of the whole window.
pub fn show(ui: &mut egui::Ui, app: &mut App) {
    let ctx = ui.ctx().clone();
    app.set_repaint(&ctx);
    app.poll_loader();
    dropped_files(&ctx, app);
    file_dialogs(&ctx, app);
    // Before any field reads the input: the clipboard keys as cut, copy and paste
    app.pasted_files = crate::clipboard::translate_keys(&ctx, app.clipboard_source.as_mut());
    handle_keys(&ctx, app);
    let stage = app.stage();

    let bar = egui::Frame::NONE.fill(theme::BG).inner_margin(egui::Margin::symmetric(8, 6));
    egui::Panel::top("toolbar").frame(bar).show_separator_line(true).show(ui, |ui| toolbar(ui, app));
    egui::Panel::bottom("status").frame(bar).show_separator_line(true).show(ui, |ui| status_bar(ui, app, stage.as_ref()));
    egui::Panel::left("operations").frame(bar).resizable(true).default_size(390.0).size_range(280.0..=720.0).show(ui, |ui| operations(ui, app, stage.as_ref()));
    egui::CentralPanel::default().frame(egui::Frame::NONE.fill(theme::BG).inner_margin(egui::Margin { left: 0, right: 8, top: 6, bottom: 0 })).show(ui, |ui| {
        viewer_toolbar(ui, app, stage.as_ref());
        if app.find.open {
            find_bar(ui, app);
        }
        egui::Frame::NONE.stroke(Stroke::new(1.0, theme::BORDER)).show(ui, |ui| viewer::show(ui, app, stage.as_ref()));
    });

    if app.help_open {
        help(&ctx, app);
    }
    load_question(&ctx, app);
    prompt(&ctx, app);

    if let Some(text) = app.clipboard.take() {
        ctx.copy_text(text);
    }
    if app.focus_viewer {
        app.focus_viewer = false;
        if let Some(focused) = ctx.memory(|memory| memory.focused()) {
            ctx.memory_mut(|memory| memory.surrender_focus(focused));
        }
    }
    let title = app.window_title();
    if title != app.title {
        app.title = title.clone();
        ctx.send_viewport_cmd(egui::ViewportCommand::Title(title));
    }
    if app.is_busy() {
        ctx.request_repaint_after(std::time::Duration::from_millis(100));
    }
}
