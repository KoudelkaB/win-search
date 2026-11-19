//! Where the profiles, the last pipeline and view settings are kept between sessions.
//! Several windows share the files. Profile changes apply to the latest contents under a lock.

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
        // Written beside and renamed: a crash never leaves half a file. The name is of this
        // process, so two windows saving at once never write into one temporary file.
        let target = dir.join(name);
        let temporary = dir.join(format!("{name}.{}.tmp", std::process::id()));
        let written = std::fs::create_dir_all(dir)
            .and_then(|_| std::fs::write(&temporary, serde_json::to_vec_pretty(value).unwrap_or_default()))
            .and_then(|_| std::fs::rename(&temporary, &target));
        if let Err(error) = written {
            let _ = std::fs::remove_file(&temporary);
            eprintln!("Log Explorer: could not save {}: {error}", target.display());
        }
    }

    /// Held while the profiles are read, changed and written; released when dropped. None
    /// when the lock cannot be had - the change is still saved, as before there was one.
    fn lock_profiles(dir: &Path) -> Option<std::fs::File> {
        let file = std::fs::create_dir_all(dir)
            .and_then(|_| std::fs::OpenOptions::new().create(true).truncate(false).write(true).open(dir.join("profiles.lock")))
            .and_then(|file| file.lock().map(|_| file));
        match file {
            Ok(file) => Some(file),
            Err(error) => {
                eprintln!("Log Explorer: profiles saved without a lock: {error}");
                None
            }
        }
    }

    pub fn load_profiles(&self) -> ProfileStore {
        self.read("profiles.json").map(|value| ProfileStore::from_storage_json(&value)).unwrap_or_default()
    }

    /// Makes `change` to the profiles as the file holds them now - another window may have
    /// saved its own since this one read them - and saves the result, all under the lock.
    /// `profiles` becomes that result (with its "Last"): the other window's profiles too.
    pub fn update_profiles<R>(&self, profiles: &mut ProfileStore, change: impl FnOnce(&mut ProfileStore) -> R) -> R {
        let Some(dir) = &self.dir else { return change(profiles) };
        let _lock = Self::lock_profiles(dir);
        let mut current = self.load_profiles().with_last_of(profiles);
        let before = current.clone();
        let result = change(&mut current);
        if current != before {
            Self::write(dir, "profiles.json", &current.to_storage_json());
        }
        *profiles = current;
        result
    }

    pub fn load_working_set(&self) -> Option<WorkingSet> {
        self.read("working-set.json").map(|value| WorkingSet::from_json(&value))
    }

    pub fn load_show_line_numbers(&self) -> bool {
        self.read("view-settings.json").and_then(|value| value["showLineNumbers"].as_bool()).unwrap_or_default()
    }

    pub fn save_show_line_numbers(&self, show: bool) {
        if let Some(dir) = &self.dir {
            Self::write(dir, "view-settings.json", &serde_json::json!({ "showLineNumbers": show }));
        }
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

#[cfg(test)]
mod tests {
    use super::*;
    use logexplorer_core::operator::{create_default, OpType};

    fn set_of(op_type: OpType) -> WorkingSet {
        let mut set = WorkingSet::default();
        let id = set.take_id();
        set.operators.push(create_default(id, op_type, None));
        set
    }

    #[test]
    fn two_windows_keep_the_profiles_each_of_them_saved() {
        let folder = tempfile::tempdir().unwrap();
        let (first, second) = (Storage::new(Some(folder.path().into())), Storage::new(Some(folder.path().into())));
        // Both windows are open before either saves
        let mut first_profiles = first.load_profiles();
        let mut second_profiles = second.load_profiles();
        second_profiles.set_last(&set_of(OpType::Sort));
        first.update_profiles(&mut first_profiles, |profiles| profiles.save_as("errors", &set_of(OpType::Filter)));
        second.update_profiles(&mut second_profiles, |profiles| profiles.save_as("sorted", &set_of(OpType::Sort)));

        let mut reopened = Storage::new(Some(folder.path().into())).load_profiles();
        assert_eq!(reopened.ordered_names(), ["sorted", "errors"]);
        // The window that saved last shows the profile of the other one, and keeps its "Last"
        assert_eq!(second_profiles.ordered_names(), ["Last", "sorted", "errors"]);

        // A delete in one window leaves the profile the other one saved since
        first.update_profiles(&mut first_profiles, |profiles| profiles.delete("errors"));
        assert_eq!(Storage::new(Some(folder.path().into())).load_profiles().ordered_names(), ["sorted"]);
        assert_eq!(first_profiles.ordered_names(), ["sorted"]);
    }
}
