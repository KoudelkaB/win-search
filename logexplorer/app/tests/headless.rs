//! Headless UI tests: the whole window is built and driven like a user would - buttons by
//! their labels, the keyboard, the pointer in the file view.

mod common;

use common::*;
use egui::Key;
use egui_kittest::kittest::Queryable as _;
use logexplorer::Options;

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
fn a_click_selects_a_value_and_enter_keeps_the_rows_with_it() {
    let (mut harness, _files) = with_sample();
    let pos = text_at(&harness, 0, "START");
    click(&mut harness, pos);
    assert_eq!(selected(&harness), "START");

    key(&mut harness, Key::Enter);
    assert_eq!(rows(&harness).len(), 3);
    assert!(rows(&harness).iter().all(|row| row.contains("START")));
    harness.get_by_label("Showing: after #1 Filter");

    // Esc with a selection hides the rows with it: the selected Filter gets an exclude clause
    let pos = text_at(&harness, 2, "health");
    click(&mut harness, pos);
    assert_eq!(selected(&harness), "health");
    key(&mut harness, Key::Escape);
    assert_eq!(rows(&harness), [SAMPLE.lines().next().unwrap(), SAMPLE.lines().nth(2).unwrap()]);
    assert_eq!(harness.state().work.operators.len(), 1);
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
fn help_opens_with_f1_and_closes_with_escape() {
    let mut harness = start();
    key(&mut harness, Key::F1);
    harness.get_by_label("How to use Log Explorer");
    harness.get_by_label("Keyboard shortcuts");
    key(&mut harness, Key::Escape);
    assert!(!harness.state().help_open);
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
