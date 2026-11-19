//! Headless UI tests: the whole window is built and driven like a user would - buttons by
//! their labels, the keyboard, the pointer in the file view.

mod common;

use common::*;
use egui::Key;
use egui_kittest::kittest::Queryable as _;
use logexplorer::app::Prompt;
use logexplorer::Options;
use std::path::{Path, PathBuf};
use std::sync::Arc;

#[derive(Debug)]
struct DroppedPath(PathBuf);

impl egui::DroppedFile for DroppedPath {
    fn path(&self) -> &Path {
        &self.0
    }

    fn bytes(&self) -> Result<Vec<u8>, String> {
        std::fs::read(&self.0).map_err(|error| error.to_string())
    }
}

#[test]
fn an_empty_window_says_so_and_offers_the_operations() {
    let harness = start();
    harness.get_by_label("No source loaded");
    harness.get_by_label("Encoding: n/a");
    harness.get_by_label("Lines: 0");
    harness.get_by_label("Showing: original");
    for button in ["⊕ Filter", "⇄ Replace", "⇅ Sort", "≠ Diff", "Transform ▾", "New"] {
        harness.get_by_label(button);
    }
}

#[test]
fn an_opened_file_shows_its_name_size_and_rows() {
    let (harness, _files) = with_sample();
    harness.get_by_label("sample.log");
    harness.get_by_label("Encoding: utf-8");
    harness.get_by_label("Lines: 5");
    assert_eq!(rows(&harness).len(), 5);
    assert_eq!(harness.state().status, "Loaded source 'sample.log'.");
    assert_eq!(harness.state().window_title(), "sample.log - Log Explorer");
}

#[test]
fn closing_the_view_unloads_every_file_and_keeps_the_pipeline() {
    let files = Files::new();
    let (a, b) = (files.write("a.log", "keep this"), files.write("b.log", "other line"));
    let mut harness = start();
    harness.state_mut().open_paths(vec![a.clone(), b], Some(logexplorer::LoadAction::ReplaceZip), Some(("keep".into(), true)));
    harness.run();
    assert_eq!(rows(&harness), ["keep this"]);
    assert!(harness.state().cache_info().0 > 0);

    harness.get_by_label("Close file view (Ctrl+W)").click();
    harness.run();
    harness.get_by_label("No source loaded");
    assert_eq!(harness.state().window_title(), "Log Explorer");
    assert!(harness.state().source.is_none());
    assert_eq!(harness.state().cache_info(), (0, 0));
    assert_eq!(harness.state().work.operators.len(), 1);

    harness.state_mut().open_paths(vec![a], None, None);
    harness.run();
    assert_eq!(rows(&harness), ["keep this"]);
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::F);
    assert!(harness.state().find.open);
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::W);
    assert!(harness.state().source.is_none());
    assert!(!harness.state().find.open);
}

#[test]
fn enter_and_minus_update_the_filter_and_escape_clears_selection() {
    let (mut harness, _files) = with_sample();
    let pos = text_at(&harness, 0, "START");
    click(&mut harness, pos);
    assert_eq!(selected(&harness), "START");

    key(&mut harness, Key::Enter);
    assert_eq!(rows(&harness).len(), 3);
    assert!(rows(&harness).iter().all(|row| row.contains("START")));
    harness.get_by_label("Showing: after #1 Filter");

    let pos = text_at(&harness, 2, "health");
    click(&mut harness, pos);
    assert_eq!(selected(&harness), "health");
    key(&mut harness, Key::Escape);
    assert!(harness.state().selection.is_none());
    assert_eq!(rows(&harness).len(), 3);

    click(&mut harness, pos);
    key_with(&mut harness, egui::Modifiers::SHIFT, Key::Minus);
    assert_eq!(rows(&harness).len(), 3);
    key(&mut harness, Key::Minus);
    assert_eq!(rows(&harness), [SAMPLE.lines().next().unwrap(), SAMPLE.lines().nth(2).unwrap()]);
    assert_eq!(harness.state().work.operators.len(), 1);
}

#[test]
fn plus_and_minus_type_in_find_and_filter_in_the_view() {
    let (mut harness, _files) = with_sample();
    key(&mut harness, Key::Minus);
    assert!(harness.state().work.operators.is_empty());

    let pos = text_at(&harness, 0, "START");
    click(&mut harness, pos);
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::F);
    harness.key_press(Key::Minus);
    harness.event(egui::Event::Text("-".into()));
    harness.run();
    assert_eq!(harness.state().find.query, "-");
    harness.key_press_modifiers(egui::Modifiers::SHIFT, Key::Plus);
    harness.event(egui::Event::Text("+".into()));
    harness.run();
    assert_eq!(harness.state().find.query, "-+");
    assert!(harness.state().work.operators.is_empty());

    key(&mut harness, Key::Escape);
    assert!(!harness.state().find.open);
    key(&mut harness, Key::Minus);
    assert_eq!(harness.state().work.operators.len(), 1);
    assert_eq!(rows(&harness), SAMPLE.lines().filter(|row| !row.contains("START")).collect::<Vec<_>>());

    let pos = text_at(&harness, 0, "GET");
    click(&mut harness, pos);
    key(&mut harness, Key::Equals);
    assert_eq!(rows(&harness).len(), 2);
    key_with(&mut harness, egui::Modifiers::SHIFT, Key::Plus);
    assert_eq!(harness.state().work.operators.len(), 1);
    assert_eq!(rows(&harness), [SAMPLE.lines().nth(1).unwrap()]);
}

#[test]
fn the_toolbar_and_the_keys_add_operations_after_the_selected_one() {
    let (mut harness, _files) = with_sample();
    harness.get_by_label("⊕ Filter").click();
    harness.run();
    for letter in [Key::S, Key::D, Key::R] {
        key(&mut harness, letter);
    }
    let types: Vec<&str> = harness.state().work.operators.iter().map(|operator| operator.op_type().label()).collect();
    assert_eq!(types, ["Filter", "Sort", "Diff", "Replace"]);
    harness.get_by_label("Showing: after #4 Replace");

    // The Transform menu adds the rest; "Collect unique values" three at once
    harness.get_by_label("Transform ▾").click();
    harness.run();
    harness.get_by_label("Collect unique values").click();
    harness.run();
    let types: Vec<&str> = harness.state().work.operators.iter().map(|operator| operator.op_type().label()).collect();
    assert_eq!(types, ["Filter", "Sort", "Diff", "Replace", "Extract", "Unique", "Join"]);
    harness.get_by_label("Showing: after #5 Extract");
}

#[test]
fn delete_removes_the_selected_text_everywhere() {
    let (mut harness, _files) = with_sample();
    let pos = text_at(&harness, 0, "2026");
    click(&mut harness, pos);
    assert_eq!(selected(&harness), "2026-03-13T09:14:02.105Z");
    // Ctrl+click takes the smallest part: the year
    click_with(&mut harness, pos, egui::PointerButton::Primary, egui::Modifiers::COMMAND);
    assert_eq!(selected(&harness), "2026");
    key(&mut harness, Key::Delete);
    assert!(rows(&harness).iter().all(|row| row.contains("] -03-13T")), "{:?}", rows(&harness));
}

#[test]
fn typing_into_a_filter_narrows_the_view_and_its_card_can_be_switched_off_and_deleted() {
    let (mut harness, _files) = with_sample();
    key(&mut harness, Key::F);
    assert_eq!(rows(&harness).len(), 5);
    let field = harness.get_by_role(egui::accesskit::Role::MultilineTextInput);
    field.focus();
    field.type_text("payments");
    harness.run();
    assert_eq!(rows(&harness).len(), 2);

    // While the field has the keyboard, letters are text and not shortcuts
    assert_eq!(harness.state().work.operators.len(), 1);

    harness.get_by_label("Filter").click();
    harness.run();
    assert!(!harness.state().work.operators[0].enabled);
    assert_eq!(rows(&harness).len(), 5);

    harness.get_by_label("Delete").click();
    harness.run();
    assert!(harness.state().work.operators.is_empty());
    harness.get_by_label("Showing: original");
}

#[test]
fn the_header_of_the_operations_switches_between_the_source_and_the_result() {
    let (mut harness, _files) = with_sample();
    let pos = text_at(&harness, 1, "END");
    click(&mut harness, pos);
    key(&mut harness, Key::Enter);
    assert_eq!(rows(&harness).len(), 2);
    harness.get_by_label("Operations").click();
    harness.run();
    assert_eq!(rows(&harness).len(), 5);
    harness.get_by_label("Showing: original");
    harness.get_by_label("Operations").click();
    harness.run();
    assert_eq!(rows(&harness).len(), 2);
}

#[test]
fn middle_click_shows_the_previous_stage_at_the_same_row() {
    let (mut harness, _files) = with_sample();
    let pos = text_at(&harness, 3, "END");
    click(&mut harness, pos);
    key(&mut harness, Key::Enter);
    assert_eq!(rows(&harness).len(), 2);
    // The second row of the filtered view is the fourth of the file
    let pos = cell(&harness, 1, 10);
    click_with(&mut harness, pos, egui::PointerButton::Middle, egui::Modifiers::NONE);
    assert_eq!(rows(&harness).len(), 5);
    assert_eq!(harness.state().cursor.row, 3);
    assert_eq!(harness.state().status, "Moved to previous stage.");
    // Ctrl + middle click goes forward again, to the same row of the result
    let pos = cell(&harness, 3, 10);
    click_with(&mut harness, pos, egui::PointerButton::Middle, egui::Modifiers::COMMAND);
    assert_eq!(rows(&harness).len(), 2);
    assert_eq!(harness.state().cursor.row, 1);
}

#[test]
fn dragging_selects_a_block_and_ctrl_c_copies_it() {
    let (mut harness, _files) = with_sample();
    let (from, to) = (cell(&harness, 0, 1), cell(&harness, 1, 5));
    press(&mut harness, from, egui::PointerButton::Primary, egui::Modifiers::NONE);
    harness.event(egui::Event::PointerMoved(to));
    harness.run();
    release(&mut harness, to, egui::PointerButton::Primary);
    assert_eq!(selected(&harness), format!("{}\n[4711", &SAMPLE.lines().next().unwrap()[1..]));
    harness.get_by_label_contains("Selection: rows 1-2");

    harness.event(egui::Event::Copy);
    assert_eq!(copied(&mut harness), Some(selected(&harness)));
    assert_eq!(harness.state().status, "Copied selection.");

    // The line numbers clear the selection
    let gutter = egui::pos2(harness.state().viewer.rect.unwrap().left() + 10.0, from.y);
    click(&mut harness, gutter);
    harness.get_by_label("Selection: none");
}

#[test]
fn the_keyboard_moves_the_cursor_and_extends_the_selection() {
    let (mut harness, _files) = with_sample();
    key_with(&mut harness, egui::Modifiers::SHIFT, Key::ArrowRight);
    key_with(&mut harness, egui::Modifiers::SHIFT, Key::ArrowRight);
    key_with(&mut harness, egui::Modifiers::SHIFT, Key::ArrowDown);
    assert_eq!(selected(&harness), format!("{}\n[4", SAMPLE.lines().next().unwrap()));
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::A);
    assert_eq!(selected(&harness), SAMPLE);
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::Escape);
    assert!(harness.state().selection.is_none());
    key(&mut harness, Key::End);
    assert_eq!((harness.state().cursor.row, harness.state().cursor.col), (4, SAMPLE.lines().last().unwrap().len()));
    key(&mut harness, Key::Home);
    assert_eq!((harness.state().cursor.row, harness.state().cursor.col), (0, 0));
}

#[test]
fn shift_navigation_keeps_its_anchor_and_shows_each_press_in_order() {
    let mut harness = start();
    harness.state_mut().paste_text("abcdefghi".into());
    harness.run();
    let queue = |harness: &mut Ui, key, count, modifiers| {
        let event = |pressed| egui::Event::Key { key, physical_key: None, pressed, repeat: false, modifiers };
        harness.input_mut().events.extend((0..count).flat_map(|_| [event(true), event(false)]));
    };

    queue(&mut harness, Key::ArrowRight, 4, egui::Modifiers::NONE);
    for col in 1..=4 {
        frame(&mut harness);
        assert_eq!(harness.state().cursor.col, col);
    }
    queue(&mut harness, Key::ArrowRight, 2, egui::Modifiers::SHIFT);
    frame(&mut harness);
    assert_eq!(selected(&harness), "e");
    frame(&mut harness);
    assert_eq!(selected(&harness), "ef");
    harness.get_by_label_contains("len 2");
    queue(&mut harness, Key::ArrowLeft, 5, egui::Modifiers::SHIFT);
    for text in ["e", "", "d", "cd", "bcd"] {
        frame(&mut harness);
        assert_eq!(selected(&harness), text);
        harness.get_by_label_contains(&format!("len {}", text.len()));
    }
    queue(&mut harness, Key::ArrowRight, 1, egui::Modifiers::SHIFT);
    frame(&mut harness);
    assert_eq!(selected(&harness), "cd");
    harness.get_by_label_contains("len 2");
}

#[test]
fn repeats_from_a_held_arrow_show_one_step_per_frame_and_do_not_build_up() {
    let mut harness = start();
    harness.state_mut().paste_text("abcdefghi".into());
    harness.run();
    let right = |pressed| egui::Event::Key { key: Key::ArrowRight, physical_key: None, pressed, repeat: false, modifiers: egui::Modifiers::SHIFT };

    harness.input_mut().events.push(right(true));
    frame(&mut harness);
    assert_eq!(selected(&harness), "a");
    harness.input_mut().events.extend(std::iter::repeat_n(right(true), 512));
    frame(&mut harness);
    assert_eq!(selected(&harness), "ab");
    harness.input_mut().events.extend(std::iter::repeat_n(right(true), 512));
    harness.input_mut().events.push(right(false));
    frame(&mut harness);
    assert_eq!(selected(&harness), "abc");
    for _ in 0..4 {
        frame(&mut harness);
        assert_eq!(selected(&harness), "abc");
    }

    key_with(&mut harness, egui::Modifiers::SHIFT, Key::ArrowLeft);
    assert_eq!(selected(&harness), "ab");
}

#[test]
fn find_walks_the_matches_from_the_find_box() {
    let (mut harness, _files) = with_sample();
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::F);
    let input = harness.get_by_role(egui::accesskit::Role::TextInput);
    assert!(input.is_focused());
    input.type_text("end");
    harness.run();
    key(&mut harness, Key::Enter);
    assert_eq!(selected(&harness), "END");
    assert_eq!(harness.state().cursor.row, 1);
    harness.get_by_label("Row 2, col 33");
    // Enter keeps the keyboard in the box: again is the next match, Shift+Enter the previous
    key(&mut harness, Key::Enter);
    assert_eq!(harness.state().cursor.row, 3);
    key(&mut harness, Key::Enter);
    assert_eq!(harness.state().cursor.row, 1);
    harness.get_by_label_contains("wrapped");
    key_with(&mut harness, egui::Modifiers::SHIFT, Key::Enter);
    assert_eq!(harness.state().cursor.row, 3);

    harness.get_by_label("Match case: off").click();
    harness.run();
    key(&mut harness, Key::F3);
    harness.get_by_label("No matches");
    key(&mut harness, Key::Escape);
    assert!(!harness.state().find.open);

    // Ctrl+F again: the old query is selected, typing replaces it
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::Escape);
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::F);
    harness.get_by_role(egui::accesskit::Role::TextInput).type_text("POST");
    harness.run();
    assert_eq!(harness.state().find.query, "POST");
}

#[test]
fn several_files_ask_how_to_be_combined_and_keep_their_tints() {
    let files = Files::new();
    let (a, b) = (files.write("a.log", "a1\na2\na3"), files.write("b.log", "b1"));
    let mut harness = start();
    harness.state_mut().open_paths(vec![a.clone(), b.clone()], None, None);
    harness.run();
    harness.get_by_label("How should the files be combined?");
    harness.get_by_label("Files: a.log + 1 more.");
    harness.get_by_label("Zip").click();
    harness.run();
    assert_eq!(rows(&harness), ["a1", "b1", "a2", "", "a3", ""]);
    harness.get_by_label("Zipped: a.log + 1 more");

    // A double click on a row keeps only the rows of its file
    let pos = cell(&harness, 1, 0);
    double_click(&mut harness, pos);
    assert_eq!(rows(&harness), ["b1", "", ""]);
    assert!(harness.state().status.starts_with("Added source filter for"), "{}", harness.state().status);

    // More files on top of loaded ones: the question offers to add or to replace
    harness.state_mut().open_paths(vec![b], None, None);
    harness.run();
    harness.get_by_label("How should the new files be loaded?");
    harness.get_by_label("Append").click();
    harness.run();
    harness.get_by_label("Showing: original");
    assert_eq!(rows(&harness), ["a1", "b1", "a2", "", "a3", "", "b1"]);
}

#[test]
fn a_host_can_open_files_concatenated_with_a_filter_and_the_user_can_zip_them() {
    let files = Files::new();
    let (a, b) = (files.write("a.log", "x a1\ny a2"), files.write("b.log", "x b1\ny b2"));
    let mut harness = start();
    harness.state_mut().open_paths(vec![a, b], Some(logexplorer::LoadAction::ReplaceAppend), Some(("x".into(), true)));
    harness.run();
    assert_eq!(rows(&harness), ["x a1", "x b1"]);
    harness.get_by_label("⇄ Interleave rows (Zip)").click();
    harness.run();
    // Only the order of the rows changed: the filter is still applied
    assert_eq!(rows(&harness), ["x a1", "x b1"]);
    harness.get_by_label("Operations").click();
    harness.run();
    assert_eq!(rows(&harness), ["x a1", "x b1", "y a2", "y b2"]);
}

#[test]
fn pasted_text_is_loaded_as_the_clipboard_source() {
    let mut harness = start();
    harness.event(egui::Event::Paste("one\ttab\r\ntwo".into()));
    harness.run();
    assert_eq!(rows(&harness), ["one    tab", "two"]);
    harness.get_by_label("Clipboard");
    harness.get_by_label("Encoding: clipboard");
}

#[test]
fn a_profile_bundle_can_be_viewed_via_drop_or_paste_and_imported_explicitly() {
    let (mut dropped, mut pasted) = (start(), start());
    let files = Files::new();
    let bundle = dropped.state_mut().export_profiles_json();
    let path = files.write("profiles.json", &bundle);
    dropped.input_mut().dropped_files.push(Arc::new(DroppedPath(path.clone())));
    dropped.run();
    pasted.event(egui::Event::Paste(bundle.clone()));
    pasted.run();
    let expected: Vec<_> = bundle.lines().collect();
    assert_eq!(rows(&dropped), expected);
    assert_eq!(rows(&pasted), expected);
    assert_eq!(dropped.state().status, "Loaded source 'profiles.json'.");
    assert_eq!(pasted.state().status, "Loaded source 'Clipboard'.");

    dropped.state_mut().import_profiles_file(&path);
    assert_eq!(dropped.state().status, "Imported profile bundle.");
}

#[test]
fn the_viewer_shows_the_row_under_the_pointer_and_ignores_blank_space() {
    let (mut harness, _files) = with_sample();
    harness.get_by_label("Rows — / 5");
    harness.event(egui::Event::PointerMoved(cell(&harness, 2, 0)));
    harness.run();
    harness.get_by_label("Rows 3 / 5");
    harness.event(egui::Event::PointerMoved(cell(&harness, 7, 0)));
    harness.run();
    harness.get_by_label("Rows — / 5");
}

#[test]
fn a_profile_is_saved_by_name_and_brings_its_pipeline_back() {
    let (mut harness, _files) = with_sample();
    let pos = text_at(&harness, 0, "START");
    click(&mut harness, pos);
    key(&mut harness, Key::Enter);
    harness.get_by_label("New •").click_secondary();
    harness.run();
    harness.get_by_label("Save as").click();
    harness.run();
    harness.get_by_label("Save current profile as:");
    harness.get_by_role(egui::accesskit::Role::TextInput).type_text("starts");
    harness.run();
    harness.get_by_label("Save").click();
    harness.run();
    assert_eq!(harness.state().status, "Saved current profile as 'starts'.");

    // "New" starts over; the chip brings the pipeline back
    harness.get_by_label("New").click();
    harness.run();
    assert!(harness.state().work.operators.is_empty());
    assert_eq!(rows(&harness).len(), 5);
    harness.get_by_label("starts").click();
    harness.run();
    assert_eq!(harness.state().work.operators.len(), 1);
    assert_eq!(harness.state().status, "Loaded profile 'starts'.");
}

#[test]
fn profiles_and_the_last_pipeline_survive_a_restart() {
    let files = Files::new();
    let data = files.dir.path().join("data");
    let with_data = || Options { data_dir: Some(data.clone()), ..options("en") };
    {
        let mut harness = start_with(with_data());
        harness.state_mut().open_paths(vec![files.write("s.log", SAMPLE)], None, None);
        harness.run();
        key(&mut harness, Key::S);
        assert!(harness.state_mut().save_profile_as("by column", false));
        key(&mut harness, Key::D);
    }
    let mut harness = start_with(with_data());
    // The app starts with an empty pipeline; the last one waits as "Last"
    assert!(harness.state().work.operators.is_empty());
    harness.get_by_label("by column");
    harness.get_by_label("Last").click();
    harness.run();
    let types: Vec<&str> = harness.state().work.operators.iter().map(|operator| operator.op_type().label()).collect();
    assert_eq!(types, ["Sort", "Diff"]);
}

#[test]
fn a_name_another_window_saved_meanwhile_is_asked_about_before_it_is_replaced() {
    let files = Files::new();
    let data = files.dir.path().join("data");
    let with_data = || Options { data_dir: Some(data.clone()), ..options("en") };
    let path = files.write("s.log", SAMPLE);
    // Both windows are open before either saves
    let mut first = start_with(with_data());
    let mut second = start_with(with_data());
    for harness in [&mut first, &mut second] {
        harness.state_mut().open_paths(vec![path.clone()], None, None);
        harness.run();
    }
    key(&mut first, Key::S);
    assert!(first.state_mut().save_profile_as("shared", false));
    key(&mut second, Key::D);

    assert!(!second.state_mut().save_profile_as("shared", false));
    assert!(matches!(&second.state().prompt, Some(Prompt::Overwrite { name, rename_from: None }) if name == "shared"));
    second.state_mut().prompt = None;
    // A rename onto that name asks as well
    assert!(second.state_mut().save_profile_as("own", false));
    assert!(!second.state_mut().rename_profile("own", "shared", false));
    assert!(matches!(&second.state().prompt, Some(Prompt::Overwrite { name, rename_from: Some(from) }) if name == "shared" && from == "own"));

    let reopened = start_with(with_data());
    let types: Vec<&str> = reopened.state().profiles.get("shared").unwrap().set.operators.iter().map(|operator| operator.op_type().label()).collect();
    assert_eq!(types, ["Sort"]);
    // Confirmed, it is replaced
    assert!(second.state_mut().save_profile_as("shared", true));
    let reopened = start_with(with_data());
    let types: Vec<&str> = reopened.state().profiles.get("shared").unwrap().set.operators.iter().map(|operator| operator.op_type().label()).collect();
    assert_eq!(types, ["Diff"]);
}

#[test]
fn help_opens_with_f1_and_closes_with_escape() {
    let mut harness = start();
    key(&mut harness, Key::F1);
    harness.get_by_label("How to use Log Explorer");
    harness.get_by_label("Keyboard shortcuts");
    harness.get_by_label("Close file view");
    harness.get_by_label("How to use Log Explorer").click();
    harness.run();
    harness.get_by_label("Loading files");
    key(&mut harness, Key::Escape);
    assert!(!harness.state().help_open);
}

#[test]
fn help_shortcut_rows_have_room_for_multiline_keys() {
    let mut harness = start_with(options("cs"));
    key(&mut harness, Key::F1);
    for width in [1280.0, 680.0] {
        harness.set_size(egui::vec2(width, 800.0));
        harness.run();
        let movement = harness.get_by_label_contains("Page Up").rect();
        let mouse_wheel = harness.get_by_label("Ctrl + kolečko").rect();
        let middle_button = harness.get_by_label("Prostřední tlačítko").rect();
        assert!(movement.bottom() < mouse_wheel.top(), "{movement:?} overlaps {mouse_wheel:?}");
        assert!(mouse_wheel.bottom() < middle_button.top(), "{mouse_wheel:?} overlaps {middle_button:?}");
    }
}

#[test]
fn help_closes_with_its_cross_and_with_ok() {
    let mut harness = start();
    for button in ["Close help", "OK"] {
        harness.get_by_label("Help and shortcuts (F1)").click();
        harness.run();
        assert!(harness.state().help_open);
        harness.get_by_label(button).click();
        harness.run();
        assert!(!harness.state().help_open, "{button} closes the help");
    }
}

#[test]
fn the_ui_speaks_the_requested_language() {
    let harness = start_with(options("cs"));
    for label in ["Operace", "⊕ Filtr", "Není načten žádný zdroj", "Profily"] {
        harness.get_by_label(label);
    }
}

#[test]
fn a_bad_pattern_is_reported_and_the_view_stays() {
    let (mut harness, _files) = with_sample();
    harness.get_by_label("Transform ▾").click();
    harness.run();
    harness.get_by_label("Extract").click();
    harness.run();
    let pattern = harness.get_by_role(egui::accesskit::Role::MultilineTextInput);
    pattern.focus();
    pattern.type_text("(");
    harness.run();
    assert!(harness.state().status.starts_with("Invalid Extract regular expression"), "{}", harness.state().status);
    assert_eq!(rows(&harness).len(), 5);
    let pattern = harness.get_by_role(egui::accesskit::Role::MultilineTextInput);
    pattern.type_text(r"\d+)\]");
    harness.run();
    assert_eq!(rows(&harness), ["4711", "4711", "4712", "4712", "4713"]);
}

/// A clipboard for the tests of the keys the app translates itself (as it does on Windows)
struct FakeClipboard(Option<logexplorer::clipboard::Pasted>);

impl logexplorer::clipboard::ClipboardSource for FakeClipboard {
    fn read(&mut self) -> logexplorer::clipboard::Pasted {
        self.0.take().unwrap_or(logexplorer::clipboard::Pasted::Empty)
    }
}

#[test]
fn files_copied_in_a_file_manager_are_opened_by_paste() {
    let files = Files::new();
    let (a, b) = (files.write("a.log", "a1\na2"), files.write("b with space.log", "b1"));

    // Linux: the file manager offers the paths as text, plain or as file:// addresses
    let mut harness = start();
    harness.event(egui::Event::Paste(format!("{}\n", a.display())));
    harness.run();
    assert_eq!(rows(&harness), ["a1", "a2"]);
    harness.get_by_label("a.log");
    let address = format!("file://{}", b.to_string_lossy().replace(' ', "%20"));
    // The address of a Windows path: file:///C:/logs/b.log
    let address = if cfg!(windows) { address.replace("file://", "file:///").replace('\\', "/") } else { address };
    harness.event(egui::Event::Paste(address));
    harness.run();
    harness.get_by_label("How should the new files be loaded?");
    harness.get_by_label("Append").click();
    harness.run();
    assert_eq!(rows(&harness), ["a1", "a2", "b1"]);

    // Windows: Explorer offers a file list only, read when Ctrl+V is pressed
    let mut harness = start();
    harness.state_mut().clipboard_source = Box::new(FakeClipboard(Some(logexplorer::clipboard::Pasted::Files(vec![a.clone(), b.clone()]))));
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::V);
    harness.get_by_label("How should the files be combined?");
    harness.get_by_label("Concat").click();
    harness.run();
    assert_eq!(rows(&harness), ["a1", "a2", "b1"]);
}

#[test]
fn the_clipboard_keys_work_when_the_app_handles_them_itself() {
    let (mut harness, _files) = with_sample();
    // Ctrl+V of text while a field has the keyboard goes into the field
    key(&mut harness, Key::F);
    let field = harness.get_by_role(egui::accesskit::Role::MultilineTextInput);
    field.focus();
    harness.run();
    harness.state_mut().clipboard_source = Box::new(FakeClipboard(Some(logexplorer::clipboard::Pasted::Text("payments".into()))));
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::V);
    assert_eq!(rows(&harness).len(), 2);
    assert_eq!(harness.state().source.as_ref().unwrap().name, "sample.log");

    // In the file view Ctrl+C copies the selection and Ctrl+V loads the text as a source
    let pos = text_at(&harness, 0, "START");
    click(&mut harness, pos);
    // (the press alone: the harness runs a frame for every queued event, and the copied
    // text is in the output of the frame of the press)
    harness.event(egui::Event::Key { key: Key::C, physical_key: None, pressed: true, repeat: false, modifiers: egui::Modifiers::COMMAND });
    assert_eq!(copied(&mut harness).as_deref(), Some("START"));
    harness.state_mut().clipboard_source = Box::new(FakeClipboard(Some(logexplorer::clipboard::Pasted::Text("pasted row".into()))));
    key_with(&mut harness, egui::Modifiers::COMMAND, Key::V);
    harness.get_by_label("How should the new files be loaded?");
}

/// One frame the way the window runs it: the app orders the keys of the input first
fn frame(harness: &mut Ui) {
    let ctx = harness.ctx.clone();
    let mut input = std::mem::take(harness.input_mut());
    logexplorer::panels::one_key_press_a_frame(&ctx, harness.state_mut(), &mut input);
    *harness.input_mut() = input;
    harness.step();
}

/// Keys come faster than frames from a script, or where drawing is slow: a frame then holds
/// several presses, and each must act as if it had a frame of its own
#[test]
fn keys_arriving_in_one_frame_act_one_after_another() {
    let (mut harness, _files) = with_sample();
    let (ctrl, plain) = (egui::Modifiers::COMMAND, egui::Modifiers::NONE);
    // A key pressed and released, between the changes of the modifiers held with it
    let tap = |key, modifiers: egui::Modifiers| {
        let key = |pressed| egui::Event::Key { key, physical_key: None, pressed, repeat: false, modifiers };
        let held = |now: egui::Modifiers| (modifiers != plain).then_some(egui::Event::ModifiersChanged(now));
        held(modifiers).into_iter().chain([key(true), key(false)]).chain(held(plain)).collect::<Vec<_>>()
    };
    let copied_text = |harness: &Ui| {
        harness.output().platform_output.commands.iter().find_map(|command| match command {
            egui::OutputCommand::CopyText(text) => Some(text.clone()),
            _ => None,
        })
    };

    // Select all and copy
    harness.input_mut().events.extend([tap(Key::A, ctrl), tap(Key::C, ctrl)].into_iter().flatten());
    frame(&mut harness);
    assert_eq!(copied_text(&harness), None);
    frame(&mut harness);
    assert_eq!(copied_text(&harness), Some(rows(&harness).join("\n")));

    // Find: the text behind Ctrl+F is typed into the find box, Enter finds it, Esc closes
    // find, and Enter in the view keeps the rows with the found text - Esc, which takes the
    // keyboard from a field, not before its turn
    let mut events = tap(Key::F, ctrl);
    // (a keyboard sends the key with its character; text sent by a program is text alone)
    let typed = tap(Key::E, plain);
    events.extend([typed[0].clone(), egui::Event::Text("E".into()), typed[1].clone(), egui::Event::Text("N".into()), egui::Event::Text("D".into())]);
    events.extend([Key::Enter, Key::Escape, Key::Enter].into_iter().flat_map(|key| tap(key, plain)));
    // The Ctrl of the next shortcut is not held yet when that last Enter is pressed
    events.extend(tap(Key::A, ctrl));
    harness.input_mut().events.extend(events);
    for _ in 0..12 {
        frame(&mut harness);
    }
    harness.run();
    assert_eq!(harness.state().find.query, "END");
    assert!(!harness.state().find.open);
    let kept = rows(&harness);
    assert_eq!(kept.len(), 2, "{kept:?}");
    assert!(kept.iter().all(|row| row.contains("END")), "{kept:?}");
}
