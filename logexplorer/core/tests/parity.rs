//! The engine against the HTML app: every pipeline of `fixtures/parity.json` was run
//! through the JavaScript operations (`tools/parity-fixtures.cjs`), and the Rust
//! operations must give the same rows, from the same sources and original rows.

use logexplorer_core::cancel::Cancel;
use logexplorer_core::load::{merge_append, stage_from_text, Bundle};
use logexplorer_core::operator::operators_from_json;
use logexplorer_core::ops;
use logexplorer_core::stage::{Stage, NO_ROW, NO_SRC};
use logexplorer_core::typed::{infer_value_type, parse_number, parse_time_span};
use serde_json::Value;
use std::sync::Arc;

fn fixtures() -> Value {
    serde_json::from_str(include_str!("fixtures/parity.json")).expect("fixtures")
}

fn strings(value: &Value) -> Vec<String> {
    value.as_array().expect("array").iter().map(|item| item.as_str().expect("string").to_string()).collect()
}

fn numbers(value: &Value) -> Vec<i64> {
    value.as_array().expect("array").iter().map(|item| item.as_i64().expect("integer")).collect()
}

fn rows(stage: &Stage) -> (Vec<String>, Vec<i64>, Vec<i64>) {
    let count = stage.len();
    (
        (0..count).map(|row| stage.line(row).to_string()).collect(),
        (0..count).map(|row| if stage.src(row) == NO_SRC { -1 } else { stage.src(row) as i64 }).collect(),
        (0..count).map(|row| if stage.row_id(row) == NO_ROW { -1 } else { stage.row_id(row) as i64 }).collect(),
    )
}

#[test]
fn pipelines_give_the_rows_of_the_html_app() {
    let fixtures = fixtures();
    let mut failures = Vec::new();
    for case in fixtures["cases"].as_array().unwrap() {
        let name = case["name"].as_str().unwrap();
        let files: Vec<Stage> = case["files"]
            .as_array()
            .unwrap()
            .iter()
            .enumerate()
            .map(|(index, lines)| stage_from_text(strings(lines).join("\n"), &format!("f{index}"), &format!("f{index}")))
            .collect();
        let bundles: Vec<Bundle> = files.iter().map(Bundle::of).collect();
        let mut stage = Arc::new(merge_append(&bundles).unwrap());
        // The operations as the app would read them from a profile
        let mut with_ids = case["operators"].clone();
        for (index, operator) in with_ids.as_array_mut().unwrap().iter_mut().enumerate() {
            operator["id"] = Value::from(index + 1);
        }
        let operators = operators_from_json(&with_ids);
        assert_eq!(operators.len(), case["operators"].as_array().unwrap().len(), "{name}: every operation is understood");
        for (step, (operator, expected)) in operators.iter().zip(case["stages"].as_array().unwrap()).enumerate() {
            let label = format!("{name} / #{} {}", step + 1, operator.op_type().label());
            match ops::apply(&stage, operator, &Cancel::new()) {
                Ok(next) => {
                    let Some(lines) = expected.get("lines") else {
                        failures.push(format!("{label}: the HTML app fails ({}), Rust does not", expected["error"]));
                        break;
                    };
                    let (expected_lines, expected_src, expected_rows) = (strings(lines), numbers(&expected["src"]), numbers(&expected["rowIds"]));
                    let (got_lines, got_src, got_rows) = rows(&next);
                    if got_lines != expected_lines {
                        failures.push(format!("{label}: rows\n   html: {expected_lines:?}\n   rust: {got_lines:?}"));
                    } else if got_src != expected_src {
                        failures.push(format!("{label}: sources\n   html: {expected_src:?}\n   rust: {got_src:?}"));
                    } else if got_rows != expected_rows {
                        failures.push(format!("{label}: original rows\n   html: {expected_rows:?}\n   rust: {got_rows:?}"));
                    }
                    stage = Arc::new(next);
                }
                Err(error) => {
                    if expected.get("error").is_none() {
                        failures.push(format!("{label}: Rust fails ({error}), the HTML app does not"));
                    }
                    break;
                }
            }
        }
    }
    assert!(failures.is_empty(), "{} difference(s) from the HTML app:\n{}", failures.len(), failures.join("\n"));
}

#[test]
fn values_are_read_like_the_html_app_reads_them() {
    let fixtures = fixtures();
    let mut failures = Vec::new();
    for value in fixtures["typed"].as_array().unwrap() {
        let text = value["text"].as_str().unwrap();
        let (kind, number, span) = (infer_value_type(text).as_str(), parse_number(text), parse_time_span(text));
        // What only the lenient legacy parser of a browser takes for a date ("1 2", "01:60")
        // is not a date here: the one deliberate difference
        let same_kind = if value["legacyDate"] == true { kind != "date-time" } else { kind == value["type"].as_str().unwrap() };
        if !same_kind || number != value["number"].as_f64() || span != value["span"].as_f64() {
            failures.push(format!("{text:?}: html {} {} {}, rust {kind} {number:?} {span:?}", value["type"], value["number"], value["span"]));
        }
    }
    assert!(failures.is_empty(), "{} value(s) read differently:\n{}", failures.len(), failures.join("\n"));
}
