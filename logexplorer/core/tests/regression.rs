//! Engine regressions. The fixture records expected results for every pipeline stage
//! and the expected types and values for representative input.

use logexplorer_core::cancel::Cancel;
use logexplorer_core::load::{merge_append, stage_from_text, Bundle};
use logexplorer_core::operator::operators_from_json;
use logexplorer_core::ops;
use logexplorer_core::stage::{Stage, NO_ROW, NO_SRC};
use logexplorer_core::typed::{infer_value_type, parse_number, parse_time_span};
use serde_json::Value;
use std::sync::Arc;

fn fixtures() -> Value {
    serde_json::from_str(include_str!("fixtures/regression.json")).expect("fixtures")
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
fn pipelines_produce_the_expected_rows_and_provenance() {
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
        let stages = case["stages"].as_array().unwrap();
        assert_eq!(stages.len(), operators.len(), "{name}: every operation has an expected result");
        for (step, (operator, expected)) in operators.iter().zip(stages).enumerate() {
            let label = format!("{name} / #{} {}", step + 1, operator.op_type().label());
            match ops::apply(&stage, operator, &Cancel::new()) {
                Ok(next) => {
                    let Some(lines) = expected.get("lines") else {
                        failures.push(format!("{label}: expected an error ({}), got rows", expected["error"]));
                        break;
                    };
                    let (expected_lines, expected_src, expected_rows) = (strings(lines), numbers(&expected["src"]), numbers(&expected["rowIds"]));
                    let (got_lines, got_src, got_rows) = rows(&next);
                    if got_lines != expected_lines {
                        failures.push(format!("{label}: rows\n   expected: {expected_lines:?}\n   actual: {got_lines:?}"));
                    } else if got_src != expected_src {
                        failures.push(format!("{label}: sources\n   expected: {expected_src:?}\n   actual: {got_src:?}"));
                    } else if got_rows != expected_rows {
                        failures.push(format!("{label}: original rows\n   expected: {expected_rows:?}\n   actual: {got_rows:?}"));
                    }
                    stage = Arc::new(next);
                }
                Err(error) => {
                    if expected.get("error").is_none() {
                        failures.push(format!("{label}: expected rows, got an error ({error})"));
                    }
                    break;
                }
            }
        }
    }
    assert!(failures.is_empty(), "{} unexpected pipeline result(s):\n{}", failures.len(), failures.join("\n"));
}

#[test]
fn values_are_read_with_the_expected_types() {
    let fixtures = fixtures();
    let mut failures = Vec::new();
    for value in fixtures["typed"].as_array().unwrap() {
        let text = value["text"].as_str().unwrap();
        let (kind, number, span) = (infer_value_type(text).as_str(), parse_number(text), parse_time_span(text));
        if kind != value["type"].as_str().unwrap() || number != value["number"].as_f64() || span != value["span"].as_f64() {
            failures.push(format!("{text:?}: expected {} {} {}, got {kind} {number:?} {span:?}", value["type"], value["number"], value["span"]));
        }
    }
    assert!(failures.is_empty(), "{} unexpected parsed value(s):\n{}", failures.len(), failures.join("\n"));
}
