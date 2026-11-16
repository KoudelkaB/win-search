//! The app in a test harness: the real UI without a window, driven through its
//! accessibility tree, the keyboard and the pointer.

#![allow(dead_code)]

use egui::{Modifiers, PointerButton, Pos2};
use egui_kittest::Harness;
use logexplorer::{App, Options};
use logexplorer_core::selection::selection_text;
use std::path::PathBuf;

pub type Ui = Harness<'static, App>;

pub fn options(language: &str) -> Options {
    Options { language: Some(language.to_string()), synchronous: true, ..Default::default() }
}

pub fn start_with(options: Options) -> Ui {
    // A step is a tenth of a second: two presses in two steps are a double click
    let mut harness = Harness::builder().with_size(egui::vec2(1280.0, 800.0)).with_step_dt(0.1).build_eframe(move |context| logexplorer::create(&context.egui_ctx, options));
    harness.run();
    harness
}

pub fn start() -> Ui {
    start_with(options("en"))
}

/// A folder of files for one test
pub struct Files {
    pub dir: tempfile::TempDir,
}

impl Files {
    pub fn new() -> Self {
        Files { dir: tempfile::tempdir().expect("temporary folder") }
    }

    pub fn write(&self, name: &str, content: &str) -> PathBuf {
        let path = self.dir.path().join(name);
        std::fs::write(&path, content).expect("file written");
        path
    }
}

pub const SAMPLE: &str = "[4711] 2026-03-13T09:14:02.105Z START GET /api/orders
[4711] 2026-03-13T09:14:02.248Z END   200 GET /api/orders
[4712] 2026-03-13T09:14:02.110Z START POST /api/payments
[4712] 2026-03-13T09:14:03.031Z END   201 POST /api/payments
[4713] 2026-03-13T09:14:04.000Z START GET /health";

/// The app with the sample log open
pub fn with_sample() -> (Ui, Files) {
    let files = Files::new();
    let path = files.write("sample.log", SAMPLE);
    let mut harness = start();
    harness.state_mut().open_paths(vec![path], None, None);
    harness.run();
    (harness, files)
}

/// The rows of the view
pub fn rows(harness: &Ui) -> Vec<String> {
    let stage = harness.state().stage_now().expect("a source is loaded");
    (0..stage.len()).map(|row| stage.line(row).to_string()).collect()
}

pub fn selected(harness: &Ui) -> String {
    let app = harness.state();
    match (app.stage_now(), app.selection) {
        (Some(stage), Some(selection)) => selection_text(&stage, &selection),
        _ => String::new(),
    }
}

/// Where column `col` of row `row` is on screen
pub fn cell(harness: &Ui, row: usize, col: usize) -> Pos2 {
    let app = harness.state();
    let rect = app.viewer.rect.expect("the file view was shown");
    let x = app.viewer.text_left + (col as f32 - app.view.scroll_left as f32 + 0.5) * app.view.char_width;
    let y = rect.top() + ((row as f64 + 0.5) * app.view.row_height as f64 - app.view.scroll_top) as f32;
    Pos2::new(x, y)
}

/// Where the first `needle` of row `row` starts on screen
pub fn text_at(harness: &Ui, row: usize, needle: &str) -> Pos2 {
    let line = rows(harness)[row].clone();
    let byte = line.find(needle).unwrap_or_else(|| panic!("'{needle}' in row {row}: {line}"));
    cell(harness, row, line[..byte].chars().count())
}

pub fn press(harness: &mut Ui, pos: Pos2, button: PointerButton, modifiers: Modifiers) {
    harness.event(egui::Event::PointerMoved(pos));
    harness.event_modifiers(egui::Event::PointerButton { pos, button, pressed: true, modifiers }, modifiers);
    harness.run();
}

pub fn release(harness: &mut Ui, pos: Pos2, button: PointerButton) {
    harness.event(egui::Event::PointerButton { pos, button, pressed: false, modifiers: Modifiers::NONE });
    harness.run();
}

pub fn click(harness: &mut Ui, pos: Pos2) {
    click_with(harness, pos, PointerButton::Primary, Modifiers::NONE);
}

pub fn click_with(harness: &mut Ui, pos: Pos2, button: PointerButton, modifiers: Modifiers) {
    press(harness, pos, button, modifiers);
    release(harness, pos, button);
    // Past the double click time, so that the next click is a click of its own
    harness.run_steps(8);
}

pub fn key(harness: &mut Ui, key: egui::Key) {
    harness.key_press(key);
    harness.run();
}

pub fn key_with(harness: &mut Ui, modifiers: Modifiers, key: egui::Key) {
    harness.key_press_modifiers(modifiers, key);
    harness.run();
}

/// Runs one frame and gives the text the app put on the clipboard in it
pub fn copied(harness: &mut Ui) -> Option<String> {
    harness.step();
    let text = harness.output().platform_output.commands.iter().find_map(|command| match command {
        egui::OutputCommand::CopyText(text) => Some(text.clone()),
        _ => None,
    });
    harness.run();
    text
}

/// Two presses at the same place within the double click time
pub fn double_click(harness: &mut Ui, pos: Pos2) {
    for pressed in [true, false, true, false] {
        harness.event(egui::Event::PointerMoved(pos));
        harness.event(egui::Event::PointerButton { pos, button: PointerButton::Primary, pressed, modifiers: Modifiers::NONE });
        harness.step();
    }
    harness.run();
}
