//! Where the profiles and the last pipeline are kept between sessions: two JSON files with
//! the content the HTML app keeps in localStorage.

use logexplorer_core::profiles::{ProfileStore, WorkingSet};
use std::path::PathBuf;

pub struct Storage {
    dir: Option<PathBuf>,
}

impl Storage {
    /// `None` keeps nothing
    pub fn new(dir: Option<PathBuf>) -> Self {
        Storage { dir }
    }

    /// The data folder of the user, unless the command line names one
    pub fn default_dir() -> Option<PathBuf> {
        directories::ProjectDirs::from("", "", "LogExplorer").map(|dirs| dirs.config_dir().to_path_buf())
    }

    fn read(&self, name: &str) -> Option<serde_json::Value> {
        let bytes = std::fs::read(self.dir.as_ref()?.join(name)).ok()?;
        serde_json::from_slice(&bytes).ok()
    }

    fn write(&self, name: &str, value: &serde_json::Value) {
        let Some(dir) = &self.dir else { return };
        // Written beside and renamed: a crash never leaves half a file
        let target = dir.join(name);
        let temporary = dir.join(format!("{name}.tmp"));
        let written = std::fs::create_dir_all(dir)
            .and_then(|_| std::fs::write(&temporary, serde_json::to_vec_pretty(value).unwrap_or_default()))
            .and_then(|_| std::fs::rename(&temporary, &target));
        if let Err(error) = written {
            eprintln!("Log Explorer: could not save {}: {error}", target.display());
        }
    }

    pub fn load_profiles(&self) -> ProfileStore {
        self.read("profiles.json").map(|value| ProfileStore::from_storage_json(&value)).unwrap_or_default()
    }

    pub fn save_profiles(&self, profiles: &mut ProfileStore) {
        self.write("profiles.json", &profiles.to_storage_json());
    }

    pub fn load_working_set(&self) -> Option<WorkingSet> {
        self.read("working-set.json").map(|value| WorkingSet::from_json(&value))
    }

    pub fn save_working_set(&self, set: &WorkingSet) {
        self.write("working-set.json", &set.to_json());
    }
}
