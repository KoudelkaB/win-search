//! Pipeline operations and their JSON representation in saved profiles.

use crate::typed::{infer_value_type, ValueType};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::Value;

macro_rules! text_enum {
    ($(#[$meta:meta])* $name:ident { $($variant:ident = $text:literal / $label:literal),+ $(,)? }) => {
        $(#[$meta])*
        #[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
        pub enum $name { $($variant),+ }

        impl $name {
            pub const ALL: &'static [$name] = &[$($name::$variant),+];

            pub fn as_str(self) -> &'static str {
                match self { $($name::$variant => $text),+ }
            }

            /// The name shown in the UI (translated there)
            pub fn label(self) -> &'static str {
                match self { $($name::$variant => $label),+ }
            }

            /// Unknown values use the first variant as the default
            pub fn parse(text: &str) -> Self {
                match text { $($text => $name::$variant,)+ _ => $name::ALL[0] }
            }
        }

        impl Default for $name {
            fn default() -> Self { $name::ALL[0] }
        }

        impl Serialize for $name {
            fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
                serializer.serialize_str(self.as_str())
            }
        }

        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
                Ok(Value::deserialize(deserializer)?.as_str().map(Self::parse).unwrap_or_default())
            }
        }
    };
}

text_enum!(SortMode { Positional = "positional" / "At column", Anchor = "anchor" / "Next to text" });
text_enum!(AnchorSide { After = "after" / "after it", Before = "before" / "before it" });
text_enum!(SortOrder { Asc = "asc" / "Ascending", Desc = "desc" / "Descending" });
text_enum!(ExtractMode { Regex = "regex" / "Regex", Column = "column" / "Column", Between = "between" / "Between text" });
text_enum!(ExtractScope { Whole = "whole" / "Whole view", Line = "line" / "Each row" });
text_enum!(RowMode { All = "all" / "All rows", Range = "range" / "Row range" });
text_enum!(UniqueOrder { Stable = "stable" / "Original order", Asc = "asc" / "Ascending", Desc = "desc" / "Descending" });

impl Serialize for ValueType {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(self.as_str())
    }
}

impl<'de> Deserialize<'de> for ValueType {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        Ok(Value::deserialize(deserializer)?.as_str().map(ValueType::parse).unwrap_or(ValueType::String))
    }
}

/// Reads a number from a JSON number or numeric string and defaults to zero
fn lenient_u64<'de, D: Deserializer<'de>>(deserializer: D) -> Result<u64, D::Error> {
    Ok(match Value::deserialize(deserializer)? {
        Value::Number(number) => number.as_f64().filter(|value| value.is_finite() && *value > 0.0).map_or(0, |value| value as u64),
        Value::String(text) => text.trim().parse::<f64>().ok().filter(|value| value.is_finite() && *value > 0.0).map_or(0, |value| value as u64),
        _ => 0,
    })
}

fn lenient_string<'de, D: Deserializer<'de>>(deserializer: D) -> Result<String, D::Error> {
    Ok(match Value::deserialize(deserializer)? {
        Value::String(text) => text,
        Value::Null => String::new(),
        other => other.to_string(),
    })
}

/// `value !== false`
fn unless_false<'de, D: Deserializer<'de>>(deserializer: D) -> Result<bool, D::Error> {
    Ok(Value::deserialize(deserializer)? != Value::Bool(false))
}

/// `!!value`
fn truthy<'de, D: Deserializer<'de>>(deserializer: D) -> Result<bool, D::Error> {
    Ok(match Value::deserialize(deserializer)? {
        Value::Bool(value) => value,
        Value::Null => false,
        Value::String(text) => !text.is_empty(),
        Value::Number(number) => number.as_f64().is_some_and(|value| value != 0.0),
        _ => true,
    })
}

fn yes() -> bool {
    true
}

fn one() -> u64 {
    1
}

fn pipe() -> String {
    "|".to_string()
}

#[derive(Clone, Debug, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Clause {
    #[serde(default, deserialize_with = "lenient_string")]
    pub text: String,
    #[serde(default, deserialize_with = "truthy")]
    pub case_sensitive: bool,
}

impl Clause {
    pub fn new(text: &str) -> Self {
        Clause { text: text.to_string(), case_sensitive: false }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FilterOp {
    #[serde(default)]
    pub include: Vec<Clause>,
    #[serde(default)]
    pub exclude: Vec<Clause>,
    /// Filter keys of the files the filter applies to; empty for all
    #[serde(default)]
    pub sources: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplaceOp {
    #[serde(default, deserialize_with = "lenient_string")]
    pub search: String,
    #[serde(default, deserialize_with = "lenient_string")]
    pub replacement: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SortOp {
    #[serde(default)]
    pub mode: SortMode,
    /// Zero-based column
    #[serde(default, deserialize_with = "lenient_u64")]
    pub pos: u64,
    #[serde(default = "one", deserialize_with = "lenient_u64")]
    pub len: u64,
    #[serde(default, deserialize_with = "lenient_string")]
    pub anchor: String,
    #[serde(default)]
    pub anchor_side: AnchorSide,
    #[serde(default = "string_type")]
    pub value_type: ValueType,
    #[serde(default)]
    pub order: SortOrder,
}

fn string_type() -> ValueType {
    ValueType::String
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiffOp {
    #[serde(default, deserialize_with = "lenient_u64")]
    pub pos: u64,
    #[serde(default = "one", deserialize_with = "lenient_u64")]
    pub len: u64,
    #[serde(default = "string_type")]
    pub value_type: ValueType,
    #[serde(default, deserialize_with = "truthy")]
    pub invert: bool,
    /// "left" or "right": where the one extra column of a numeric diff goes
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub output_extend: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExtractOp {
    #[serde(default)]
    pub mode: ExtractMode,
    #[serde(default)]
    pub scope: ExtractScope,
    #[serde(default, deserialize_with = "lenient_string")]
    pub pattern: String,
    #[serde(default = "one", deserialize_with = "lenient_u64")]
    pub group: u64,
    #[serde(default, deserialize_with = "lenient_u64")]
    pub pos: u64,
    #[serde(default = "one", deserialize_with = "lenient_u64")]
    pub len: u64,
    #[serde(default, deserialize_with = "lenient_string")]
    pub start_anchor: String,
    #[serde(default, deserialize_with = "lenient_string")]
    pub end_anchor: String,
    #[serde(default)]
    pub row_mode: RowMode,
    /// One-based
    #[serde(default = "one", deserialize_with = "lenient_u64")]
    pub from_row: u64,
    #[serde(default = "one", deserialize_with = "lenient_u64")]
    pub to_row: u64,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub all_matches: bool,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub case_sensitive: bool,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub trim: bool,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub skip_empty: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UniqueOp {
    #[serde(default)]
    pub order: UniqueOrder,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub trim: bool,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub ignore_empty: bool,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub case_sensitive: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JoinOp {
    #[serde(default = "pipe")]
    pub delimiter: String,
    #[serde(default, deserialize_with = "lenient_string")]
    pub prefix: String,
    #[serde(default, deserialize_with = "lenient_string")]
    pub suffix: String,
    #[serde(default, deserialize_with = "truthy")]
    pub trim: bool,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub skip_empty: bool,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub interpret_escapes: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum OpKind {
    Filter(FilterOp),
    Replace(ReplaceOp),
    Sort(SortOp),
    Diff(DiffOp),
    Extract(ExtractOp),
    Unique(UniqueOp),
    Join(JoinOp),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum OpType {
    Filter,
    Replace,
    Sort,
    Diff,
    Extract,
    Unique,
    Join,
}

impl OpType {
    pub fn as_str(self) -> &'static str {
        match self {
            OpType::Filter => "filter",
            OpType::Replace => "replace",
            OpType::Sort => "sort",
            OpType::Diff => "diff",
            OpType::Extract => "extract",
            OpType::Unique => "unique",
            OpType::Join => "join",
        }
    }

    /// The capitalized name shown in the UI (translated there)
    pub fn label(self) -> &'static str {
        match self {
            OpType::Filter => "Filter",
            OpType::Replace => "Replace",
            OpType::Sort => "Sort",
            OpType::Diff => "Diff",
            OpType::Extract => "Extract",
            OpType::Unique => "Unique",
            OpType::Join => "Join",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Operator {
    #[serde(default, deserialize_with = "lenient_u64")]
    pub id: u64,
    #[serde(default = "yes", deserialize_with = "unless_false")]
    pub enabled: bool,
    #[serde(default, deserialize_with = "lenient_string")]
    pub name: String,
    #[serde(flatten)]
    pub kind: OpKind,
}

impl Operator {
    pub fn op_type(&self) -> OpType {
        match &self.kind {
            OpKind::Filter(_) => OpType::Filter,
            OpKind::Replace(_) => OpType::Replace,
            OpKind::Sort(_) => OpType::Sort,
            OpKind::Diff(_) => OpType::Diff,
            OpKind::Extract(_) => OpType::Extract,
            OpKind::Unique(_) => OpType::Unique,
            OpKind::Join(_) => OpType::Join,
        }
    }

    /// What the result of the operation depends on (not its id or name)
    pub fn signature(&self) -> String {
        serde_json::to_string(&self.kind).unwrap_or_default()
    }

    /// Clamps lengths and row ranges when loading an operation
    fn normalized(mut self) -> Self {
        match &mut self.kind {
            OpKind::Extract(extract) => {
                extract.len = extract.len.max(1);
                extract.from_row = extract.from_row.max(1);
                extract.to_row = extract.to_row.max(extract.from_row);
            }
            OpKind::Sort(sort) => sort.len = sort.len.max(1),
            OpKind::Diff(diff) => diff.len = diff.len.max(1),
            _ => {}
        }
        self
    }
}

/// The operations of a stored list; entries this version does not understand are dropped.
pub fn operators_from_json(value: &Value) -> Vec<Operator> {
    value.as_array().map(|list| list.iter().filter_map(|entry| serde_json::from_value::<Operator>(entry.clone()).ok()).map(Operator::normalized).collect()).unwrap_or_default()
}

/// What the selection offers a new operation.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Prefill {
    pub selected_text: String,
    pub start_row: usize,
    pub end_row: usize,
    pub start_col: usize,
    pub width: usize,
    /// Longest row of the view
    pub max_width: usize,
    pub has_selection: bool,
}

/// A new operation of `op_type`, set up from the selection.
pub fn create_default(id: u64, op_type: OpType, prefill: Option<&Prefill>) -> Operator {
    let has_selection = prefill.is_some_and(|prefill| prefill.has_selection);
    let selected = prefill.map_or("", |prefill| prefill.selected_text.as_str());
    let default_length = if has_selection { prefill.map_or(0, |prefill| prefill.width) } else { prefill.map_or(0, |prefill| prefill.max_width) }.max(1) as u64;
    let default_type = if has_selection { infer_value_type(selected) } else { ValueType::String };
    let pos = prefill.map_or(0, |prefill| prefill.start_col) as u64;
    let kind = match op_type {
        OpType::Filter => OpKind::Filter(FilterOp { include: vec![Clause::new(selected)], exclude: Vec::new(), sources: Vec::new() }),
        OpType::Replace => OpKind::Replace(ReplaceOp { search: selected.to_string(), replacement: String::new() }),
        OpType::Sort => OpKind::Sort(SortOp {
            mode: SortMode::Positional,
            pos,
            len: default_length,
            anchor: selected.to_string(),
            anchor_side: AnchorSide::After,
            value_type: default_type,
            order: SortOrder::Asc,
        }),
        OpType::Diff => OpKind::Diff(DiffOp { pos, len: default_length, value_type: default_type, invert: false, output_extend: None }),
        OpType::Extract => {
            let start_row = if has_selection { prefill.map_or(0, |prefill| prefill.start_row) + 1 } else { 1 } as u64;
            let end_row = if has_selection { prefill.map_or(0, |prefill| prefill.end_row) as u64 + 1 } else { start_row };
            OpKind::Extract(ExtractOp {
                mode: if has_selection { ExtractMode::Column } else { ExtractMode::Regex },
                scope: ExtractScope::Whole,
                pattern: String::new(),
                group: 1,
                pos,
                len: default_length,
                start_anchor: String::new(),
                end_anchor: String::new(),
                row_mode: if has_selection { RowMode::Range } else { RowMode::All },
                from_row: start_row,
                to_row: end_row,
                all_matches: true,
                case_sensitive: true,
                trim: true,
                skip_empty: true,
            })
        }
        OpType::Unique => OpKind::Unique(UniqueOp { order: UniqueOrder::Stable, trim: true, ignore_empty: true, case_sensitive: true }),
        OpType::Join => OpKind::Join(JoinOp { delimiter: "|".to_string(), prefix: String::new(), suffix: String::new(), trim: false, skip_empty: true, interpret_escapes: true }),
    };
    Operator { id, enabled: true, name: op_type.as_str().to_string(), kind }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn a_stored_filter_round_trips_as_json() {
        let stored = json!({
            "id": 3, "type": "filter", "enabled": true, "name": "filter",
            "include": [{"text": "ERROR\nWARN", "caseSensitive": false}],
            "exclude": [{"text": "/health", "caseSensitive": true}],
            "sources": []
        });
        let operator: Operator = serde_json::from_value(stored.clone()).unwrap();
        let OpKind::Filter(filter) = &operator.kind else { panic!("not a filter") };
        assert_eq!(filter.include[0].text, "ERROR\nWARN");
        assert!(filter.exclude[0].case_sensitive);
        assert_eq!(serde_json::to_value(&operator).unwrap(), stored);
    }

    #[test]
    fn missing_and_odd_fields_use_the_operation_defaults() {
        let list = json!([
            {"id": 1, "type": "extract"},
            {"id": "2", "type": "sort", "mode": "column", "pos": "4", "len": 0, "valueType": "number"},
            {"id": 3, "type": "join", "delimiter": ", "},
            {"id": 4, "type": "teleport"},
            {"id": 5, "type": "unique", "enabled": false, "trim": false}
        ]);
        let operators = operators_from_json(&list);
        assert_eq!(operators.len(), 4);
        let OpKind::Extract(extract) = &operators[0].kind else { panic!() };
        assert_eq!((extract.mode, extract.scope, extract.group, extract.len), (ExtractMode::Regex, ExtractScope::Whole, 1, 1));
        assert!(extract.all_matches && extract.case_sensitive && extract.trim && extract.skip_empty);
        let OpKind::Sort(sort) = &operators[1].kind else { panic!() };
        assert_eq!((operators[1].id, sort.mode, sort.pos, sort.len, sort.value_type), (2, SortMode::Positional, 4, 1, ValueType::Number));
        let OpKind::Join(join) = &operators[2].kind else { panic!() };
        assert_eq!((join.delimiter.as_str(), join.skip_empty, join.interpret_escapes, join.trim), (", ", true, true, false));
        let OpKind::Unique(unique) = &operators[3].kind else { panic!() };
        assert!(!operators[3].enabled && !unique.trim && unique.ignore_empty);
    }

    #[test]
    fn a_new_operation_takes_the_selection() {
        let prefill = Prefill { selected_text: "95.6".into(), start_row: 2, end_row: 2, start_col: 34, width: 4, max_width: 80, has_selection: true };
        let OpKind::Sort(sort) = create_default(1, OpType::Sort, Some(&prefill)).kind else { panic!() };
        assert_eq!((sort.pos, sort.len, sort.value_type), (34, 4, ValueType::Number));
        let OpKind::Extract(extract) = create_default(2, OpType::Extract, Some(&prefill)).kind else { panic!() };
        assert_eq!((extract.mode, extract.row_mode, extract.from_row, extract.to_row), (ExtractMode::Column, RowMode::Range, 3, 3));
        let none = Prefill { max_width: 80, ..Default::default() };
        let OpKind::Diff(diff) = create_default(3, OpType::Diff, Some(&none)).kind else { panic!() };
        assert_eq!((diff.pos, diff.len, diff.value_type), (0, 80, ValueType::String));
    }
}
