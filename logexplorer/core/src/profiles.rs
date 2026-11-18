//! Saved pipelines. The stored JSON and the exported bundle are those of the HTML app.

use crate::operator::{operators_from_json, Operator};
use serde_json::{json, Map, Value};

const PROFILE_KIND: &str = "LogExplorerProfiles";
pub const PROFILE_VERSION: u64 = 1;
/// How many profile chips are shown
pub const PROFILE_RECENT_LIMIT: usize = 6;
/// The pipeline of the previous session
pub const IMPLICIT_PROFILE_NAME: &str = "Last";

/// The pipeline being edited.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct WorkingSet {
    pub operators: Vec<Operator>,
    pub selected_operator_id: Option<u64>,
    pub next_operator_id: u64,
}

impl WorkingSet {
    pub fn to_json(&self) -> Value {
        json!({
            "operators": self.operators,
            "selectedOperatorId": self.selected_operator_id,
            "nextOperatorId": self.next_operator_id.max(1),
        })
    }

    pub fn from_json(value: &Value) -> Self {
        WorkingSet {
            operators: value.get("operators").map(operators_from_json).unwrap_or_default(),
            selected_operator_id: value.get("selectedOperatorId").and_then(Value::as_u64),
            next_operator_id: value.get("nextOperatorId").and_then(Value::as_u64).unwrap_or(1).max(1),
        }
    }

    /// The id for a new operation
    pub fn take_id(&mut self) -> u64 {
        let id = self.next_operator_id.max(self.operators.iter().map(|operator| operator.id + 1).max().unwrap_or(1)).max(1);
        self.next_operator_id = id + 1;
        id
    }

    pub fn index_of(&self, id: u64) -> Option<usize> {
        self.operators.iter().position(|operator| operator.id == id)
    }

    pub fn selected_index(&self) -> Option<usize> {
        self.selected_operator_id.and_then(|id| self.index_of(id))
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct Profile {
    pub set: WorkingSet,
    pub saved_at: u64,
    /// "Last": kept automatically, not exported
    pub implicit: bool,
}

impl Profile {
    fn from_json(value: &Value, implicit: Option<bool>) -> Self {
        Profile {
            set: WorkingSet::from_json(value),
            saved_at: value.get("savedAt").and_then(Value::as_u64).unwrap_or_else(now_millis),
            implicit: implicit.unwrap_or_else(|| value.get("implicit").and_then(Value::as_bool).unwrap_or(false)),
        }
    }

    fn to_json(&self) -> Value {
        let mut value = self.set.to_json();
        value["savedAt"] = json!(self.saved_at);
        value["implicit"] = json!(self.implicit);
        value
    }

    pub fn of(set: &WorkingSet, implicit: bool) -> Self {
        Profile { set: set.clone(), saved_at: now_millis(), implicit }
    }
}

fn now_millis() -> u64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |elapsed| elapsed.as_millis() as u64)
}

/// The named profiles in the order they were added, and the order of their chips.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ProfileStore {
    named: Vec<(String, Profile)>,
    order: Vec<String>,
}

impl ProfileStore {
    pub fn get(&self, name: &str) -> Option<&Profile> {
        self.named.iter().find(|(key, _)| key == name).map(|(_, profile)| profile)
    }

    pub fn contains(&self, name: &str) -> bool {
        self.get(name).is_some()
    }

    fn set(&mut self, name: &str, profile: Profile) {
        match self.named.iter_mut().find(|(key, _)| key == name) {
            Some(entry) => entry.1 = profile,
            None => self.named.push((name.to_string(), profile)),
        }
    }

    fn remove(&mut self, name: &str) -> Option<Profile> {
        let index = self.named.iter().position(|(key, _)| key == name)?;
        Some(self.named.remove(index).1)
    }

    fn sync_order(&mut self) {
        let names: Vec<&String> = self.named.iter().map(|(name, _)| name).filter(|name| *name != IMPLICIT_PROFILE_NAME).collect();
        let mut order: Vec<String> = Vec::new();
        for name in self.order.iter().filter(|name| names.contains(name)) {
            if !order.contains(name) {
                order.push(name.clone());
            }
        }
        for name in names {
            if !order.contains(name) {
                order.push(name.clone());
            }
        }
        self.order = order;
    }

    /// The chips: "Last" first, then the named profiles in their order
    pub fn ordered_names(&mut self) -> Vec<String> {
        self.sync_order();
        let mut names = Vec::new();
        if self.contains(IMPLICIT_PROFILE_NAME) {
            names.push(IMPLICIT_PROFILE_NAME.to_string());
        }
        names.extend(self.order.iter().cloned());
        names
    }

    /// The pipeline of the last session becomes the profile "Last"
    pub fn set_last(&mut self, set: &WorkingSet) {
        if !set.operators.is_empty() {
            self.set(IMPLICIT_PROFILE_NAME, Profile::of(set, true));
        }
    }

    /// Saves the pipeline under `name` and puts it first
    pub fn save_as(&mut self, name: &str, set: &WorkingSet) {
        self.set(name, Profile::of(set, false));
        self.order.retain(|entry| entry != name);
        self.order.insert(0, name.to_string());
    }

    /// Saves the pipeline over the existing profile `name`
    pub fn save(&mut self, name: &str, set: &WorkingSet) -> bool {
        let Some(implicit) = self.get(name).map(|profile| profile.implicit) else { return false };
        self.set(name, Profile::of(set, implicit));
        if !implicit && !self.order.iter().any(|entry| entry == name) {
            self.order.insert(0, name.to_string());
        }
        true
    }

    pub fn delete(&mut self, name: &str) -> bool {
        let removed = self.remove(name).is_some();
        self.order.retain(|entry| entry != name);
        removed
    }

    /// Renames a profile; "Last" is copied to the new name instead
    pub fn rename(&mut self, name: &str, new_name: &str) -> bool {
        let Some(profile) = self.get(name).cloned() else { return false };
        let renamed = Profile { saved_at: now_millis(), implicit: false, ..profile.clone() };
        self.sync_order();
        if profile.implicit {
            self.set(new_name, renamed);
            self.order.retain(|entry| entry != new_name);
            self.order.insert(0, new_name.to_string());
        } else {
            let position = self.order.iter().position(|entry| entry == name);
            self.remove(name);
            self.set(new_name, renamed);
            if new_name != name {
                self.order.retain(|entry| entry != new_name && entry != name);
                let position = position.unwrap_or(self.order.len()).min(self.order.len());
                self.order.insert(position, new_name.to_string());
            }
        }
        true
    }

    /// Moves the chip of `dragged` before `target`, to the end without one
    pub fn move_before(&mut self, dragged: &str, target: Option<&str>) -> bool {
        self.sync_order();
        let Some(source) = self.order.iter().position(|entry| entry == dragged) else { return false };
        let name = self.order.remove(source);
        match target.and_then(|target| self.order.iter().position(|entry| entry == target)) {
            Some(position) => self.order.insert(position, name),
            None => self.order.push(name),
        }
        true
    }

    /// The pipeline differs from what profile `name` holds
    pub fn is_dirty(&self, name: &str, set: &WorkingSet) -> bool {
        self.get(name).is_some_and(|profile| profile.set.operators != set.operators || profile.set.selected_operator_id != set.selected_operator_id)
    }

    fn exportable(&self) -> Map<String, Value> {
        self.named.iter().filter(|(_, profile)| !profile.implicit).map(|(name, profile)| (name.clone(), profile.to_json())).collect()
    }

    /// What is kept between sessions
    pub fn to_storage_json(&mut self) -> Value {
        self.sync_order();
        json!({ "named": self.exportable(), "order": self.order })
    }

    pub fn from_storage_json(value: &Value) -> Self {
        // An older store was the map of profiles itself
        let (named, order) = match value.get("named").and_then(Value::as_object) {
            Some(named) => (Some(named), value.get("order")),
            None => (value.as_object(), None),
        };
        let mut store = ProfileStore::default();
        store.add_profiles(named);
        store.order = order.and_then(Value::as_array).map(|list| list.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default();
        store.sync_order();
        store
    }

    fn add_profiles(&mut self, named: Option<&Map<String, Value>>) {
        for (name, profile) in named.into_iter().flatten() {
            self.set(name, Profile::from_json(profile, Some(name == IMPLICIT_PROFILE_NAME)));
        }
    }

    /// The bundle of "Download profiles"
    pub fn export_bundle(&mut self, set: &WorkingSet) -> Value {
        self.sync_order();
        json!({
            "kind": PROFILE_KIND,
            "version": PROFILE_VERSION,
            "profiles": self.exportable(),
            "profileOrder": self.order,
            "workingSet": set.to_json(),
        })
    }

    /// Adds the profiles of a bundle; gives the pipeline it carries
    pub fn import_bundle(&mut self, bundle: &Value) -> Result<Option<WorkingSet>, &'static str> {
        if bundle.get("kind").and_then(Value::as_str) != Some(PROFILE_KIND) || bundle.get("version").and_then(Value::as_u64) != Some(PROFILE_VERSION) {
            return Err("Unsupported profile bundle.");
        }
        if let Some(profiles) = bundle.get("profiles").and_then(Value::as_object) {
            self.sync_order();
            self.add_profiles(Some(profiles));
            let imported: Vec<String> = match bundle.get("profileOrder").and_then(Value::as_array) {
                Some(order) => order.iter().filter_map(Value::as_str).map(str::to_string).collect(),
                None => profiles.keys().cloned().collect(),
            };
            self.order.extend(imported);
            self.sync_order();
        }
        Ok(bundle.get("workingSet").filter(|set| set.is_object()).map(WorkingSet::from_json))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::operator::{create_default, OpType};

    fn set_of(types: &[OpType]) -> WorkingSet {
        let mut set = WorkingSet::default();
        for op_type in types {
            let id = set.take_id();
            set.operators.push(create_default(id, *op_type, None));
            set.selected_operator_id = Some(id);
        }
        set
    }

    #[test]
    fn profiles_keep_their_chip_order_with_last_first() {
        let mut store = ProfileStore::default();
        store.save_as("errors", &set_of(&[OpType::Filter]));
        store.save_as("durations", &set_of(&[OpType::Sort]));
        store.set_last(&set_of(&[OpType::Diff]));
        store.set_last(&WorkingSet::default());
        assert_eq!(store.ordered_names(), ["Last", "durations", "errors"]);
        assert!(store.move_before("durations", None));
        assert_eq!(store.ordered_names(), ["Last", "errors", "durations"]);
        assert!(store.rename("errors", "only errors"));
        assert_eq!(store.ordered_names(), ["Last", "only errors", "durations"]);
        assert!(store.rename("Last", "yesterday"));
        assert_eq!(store.ordered_names(), ["Last", "yesterday", "only errors", "durations"]);
        assert!(store.delete("Last") && !store.delete("Last"));
        assert_eq!(store.ordered_names(), ["yesterday", "only errors", "durations"]);
    }

    #[test]
    fn a_profile_is_dirty_until_saved_again() {
        let mut store = ProfileStore::default();
        let mut set = set_of(&[OpType::Filter]);
        store.save_as("p", &set);
        assert!(!store.is_dirty("p", &set));
        let id = set.take_id();
        set.operators.push(create_default(id, OpType::Sort, None));
        assert!(store.is_dirty("p", &set));
        assert!(store.save("p", &set) && !store.is_dirty("p", &set));
        assert!(!store.save("missing", &set));
    }

    #[test]
    fn the_store_and_the_bundle_survive_a_round_trip_without_last() {
        let mut store = ProfileStore::default();
        store.save_as("a", &set_of(&[OpType::Filter, OpType::Join]));
        store.save_as("b", &set_of(&[OpType::Extract]));
        store.set_last(&set_of(&[OpType::Unique]));
        let mut restored = ProfileStore::from_storage_json(&store.to_storage_json());
        assert_eq!(restored.ordered_names(), ["b", "a"]);
        assert_eq!(restored.get("a").unwrap().set, store.get("a").unwrap().set);

        let working = set_of(&[OpType::Replace]);
        let bundle = store.export_bundle(&working);
        assert_eq!(bundle["kind"], "LogExplorerProfiles");
        let mut other = ProfileStore::default();
        other.save_as("mine", &set_of(&[OpType::Diff]));
        let imported = other.import_bundle(&bundle).unwrap();
        assert_eq!(imported, Some(working));
        assert_eq!(other.ordered_names(), ["mine", "b", "a"]);
        assert!(other.import_bundle(&json!({"kind": "LogExplorerProfiles", "version": 2})).is_err());
    }

    #[test]
    fn a_bundle_written_by_the_html_app_is_understood() {
        let bundle: Value = serde_json::from_str(
            r#"{"kind":"LogExplorerProfiles","version":1,
                "profiles":{"REST duration":{"operators":[
                    {"id":1,"type":"filter","enabled":true,"name":"filter","include":[{"text":" START \n END ","caseSensitive":false}],"exclude":[],"sources":[]},
                    {"id":2,"type":"sort","enabled":true,"name":"sort","mode":"positional","pos":1,"len":4,"anchor":"4711","anchorSide":"after","valueType":"string","order":"asc"},
                    {"id":3,"type":"diff","enabled":true,"name":"diff","pos":7,"len":24,"valueType":"date-time","invert":false}],
                    "selectedOperatorId":3,"nextOperatorId":4,"savedAt":1773393242105,"implicit":false}},
                "profileOrder":["REST duration"],
                "workingSet":{"operators":[],"selectedOperatorId":null,"nextOperatorId":1}}"#,
        )
        .unwrap();
        let mut store = ProfileStore::default();
        let working = store.import_bundle(&bundle).unwrap().unwrap();
        assert!(working.operators.is_empty());
        let profile = store.get("REST duration").unwrap();
        assert_eq!(profile.set.operators.len(), 3);
        assert_eq!((profile.set.selected_operator_id, profile.set.next_operator_id, profile.saved_at), (Some(3), 4, 1773393242105));
    }
}
