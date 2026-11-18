//! Where the profiles and the last pipeline are kept between sessions: two JSON files with
//! the content the HTML app keeps in localStorage.

use logexplorer_core::profiles::{ProfileStore, WorkingSet};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Sender};
use std::thread::{self, JoinHandle};

pub struct Storage {
    dir: Option<PathBuf>,
    working_set_writer: Option<(Sender<WorkingSet>, JoinHandle<()>)>,
}

impl Storage {
    /// `None` keeps nothing
    pub fn new(dir: Option<PathBuf>) -> Self {
        Storage { dir, working_set_writer: None }
    }

    /// The data folder of the user, unless the command line names one
    pub fn default_dir() -> Option<PathBuf> {
        directories::ProjectDirs::from("", "", "LogExplorer").map(|dirs| dirs.config_dir().to_path_buf())
    }

    fn read(&self, name: &str) -> Option<serde_json::Value> {
        let bytes = std::fs::read(self.dir.as_ref()?.join(name)).ok()?;
        serde_json::from_slice(&bytes).ok()
    }

    fn write(dir: &Path, name: &str, value: &serde_json::Value) {
        // Written beside and renamed: a crash never leaves half a file
        let target = dir.join(name);
        let temporary = dir.join(format!("{name}.{}.tmp", std::process::id()));
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
        if let Some(dir) = &self.dir {
            Self::write(dir, "profiles.json", &profiles.to_storage_json());
        }
    }

    pub fn load_working_set(&self) -> Option<WorkingSet> {
        self.read("working-set.json").map(|value| WorkingSet::from_json(&value))
    }

    pub fn save_working_set(&mut self, set: &WorkingSet) {
        let Some(dir) = &self.dir else { return };
        let (sender, _) = self.working_set_writer.get_or_insert_with(|| {
            let dir = dir.clone();
            let (sender, receiver) = mpsc::channel::<WorkingSet>();
            let writer = thread::spawn(move || {
                while let Ok(queued) = receiver.recv() {
                    // If editing outpaces the disk, persist the newest snapshot of the pipeline.
                    let latest = receiver.try_iter().last().unwrap_or(queued);
                    Self::write(&dir, "working-set.json", &latest.to_json());
                }
            });
            (sender, writer)
        });
        let _ = sender.send(set.clone());
    }
}

impl Drop for Storage {
    fn drop(&mut self) {
        if let Some((sender, writer)) = self.working_set_writer.take() {
            drop(sender);
            let _ = writer.join();
        }
    }
}
