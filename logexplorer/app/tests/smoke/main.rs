//! Smoke tests: the built application as its own process, driven from outside with the
//! real keyboard and clipboard, down to a clean exit and the files it leaves - what
//! `search.smoke` is to File Search Manager.
//!
//! `cargo test -p logexplorer --test smoke -- --ignored --test-threads=1`
//!
//! Linux: on an X display of its own (Xvfb, openbox, xdotool, xclip), so the tests may run
//! in parallel and touch nothing of the desktop. Windows: on the desktop of the session,
//! one test at a time, with real keys (SendInput) and UI Automation.
//!
//! Where the tools or the foreground are not to be had a test is skipped - unless CI is
//! set: there it fails.

#![cfg(any(target_os = "linux", windows))]

#[cfg(target_os = "linux")]
#[path = "linux.rs"]
mod platform;
#[cfg(windows)]
#[path = "windows.rs"]
mod platform;

use platform::Backend;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

const SAMPLE: &str = "[4711] 09:14:02.105 START GET /api/orders
[4711] 09:14:02.248 END   200 GET /api/orders
[4712] 09:14:02.110 START POST /api/payments
[4712] 09:14:03.031 END   201 POST /api/payments";

pub fn wait_for<T>(what: &str, timeout: Duration, mut probe: impl FnMut() -> Option<T>) -> T {
    let started = Instant::now();
    loop {
        if let Some(found) = probe() {
            return found;
        }
        assert!(started.elapsed() < timeout, "timed out waiting for {what}");
        std::thread::sleep(Duration::from_millis(100));
    }
}

/// Skips the test with `reason`; in CI it fails instead
pub fn skip(reason: &str) {
    assert!(std::env::var_os("CI").is_none(), "{reason}");
    eprintln!("skipped: {reason}");
}

/// The app running where it can be driven
struct Desktop {
    backend: Backend,
    app: Option<Child>,
    dir: tempfile::TempDir,
}

impl Drop for Desktop {
    fn drop(&mut self) {
        if let Some(app) = &mut self.app {
            let _ = app.kill();
            let _ = app.wait();
        }
    }
}

impl Desktop {
    /// `None` where the test cannot run (and CI is not set)
    fn start() -> Option<Desktop> {
        Some(Desktop { backend: Backend::start()?, app: None, dir: tempfile::tempdir().expect("temporary folder") })
    }

    fn file(&self, name: &str, content: &str) -> PathBuf {
        let path = self.dir.path().join(name);
        std::fs::write(&path, content).expect("file written");
        path
    }

    fn data(&self) -> PathBuf {
        self.dir.path().join("data")
    }

    fn launch(&mut self, args: &[&str]) {
        let mut command = Command::new(env!("CARGO_BIN_EXE_logexplorer"));
        command.args(["--lang", "en", "--data-dir"]).arg(self.data()).args(args).stdout(Stdio::null()).stderr(Stdio::null());
        self.backend.prepare(&mut command);
        self.app = Some(command.spawn().expect("the app starts"));
    }

    fn pid(&self) -> u32 {
        self.app.as_ref().expect("the app runs").id()
    }

    /// Waits for the window of the app whose title starts with `title` and gives it the
    /// keyboard; `false` (after `skip`) when the foreground cannot be taken
    #[must_use]
    fn window(&self, title: &str) -> bool {
        let window = wait_for(&format!("the window '{title}'"), Duration::from_secs(60), || self.backend.find_window(title, self.pid()));
        if self.backend.activate(&window) {
            return true;
        }
        skip("the window of the app could not be brought to the foreground");
        false
    }

    fn keys(&self, keys: &[&str]) {
        for key in keys {
            self.backend.key(key);
            std::thread::sleep(Duration::from_millis(120));
        }
    }

    fn type_text(&self, text: &str) {
        self.backend.type_text(text);
        std::thread::sleep(Duration::from_millis(150));
    }

    /// Selects the whole view, copies it and waits until the clipboard holds `expected`
    fn expect_view(&self, expected: &str) {
        let mut last = String::new();
        let started = Instant::now();
        while started.elapsed() < Duration::from_secs(20) {
            // Where the clipboard is the one of the session, what an earlier copy left there
            // must not pass for this one
            const STALE: &str = "not copied yet";
            self.backend.set_clipboard(STALE);
            self.keys(&["ctrl+a", "ctrl+c"]);
            let copied = Instant::now();
            loop {
                last = self.backend.clipboard().replace("\r\n", "\n");
                if last != STALE || copied.elapsed() > Duration::from_secs(2) {
                    break;
                }
                std::thread::sleep(Duration::from_millis(50));
            }
            if last == expected {
                return;
            }
            std::thread::sleep(Duration::from_millis(200));
        }
        panic!("the view is not the expected one.\n--- expected ---\n{expected}\n--- copied ---\n{last}\n--- pipeline ---\n{}", read(&self.data().join("working-set.json")));
    }

    /// Ctrl+Esc clears the selection. Windows opens its Start menu on that key and the app
    /// never sees it; the scenarios hold there without it (find wraps around, and Shift+End
    /// extends from the start of the selection).
    fn clear_selection(&self) {
        if cfg!(not(windows)) {
            self.keys(&["ctrl+Escape"]);
        }
    }

    /// Closes the window the way a user does and waits for the process to end cleanly
    fn close(&mut self) {
        self.keys(&["alt+F4"]);
        // The app stays owned until it has ended: one that does not end is killed by `drop`
        let status = wait_for("the app to exit", Duration::from_secs(20), || self.app.as_mut().expect("the app runs").try_wait().expect("process state"));
        self.app = None;
        assert!(status.success(), "the app exited with {status}");
    }
}

fn read(path: &Path) -> String {
    std::fs::read_to_string(path).unwrap_or_default()
}

#[test]
#[ignore = "drives a real window; run with --ignored"]
fn a_file_is_shown_filtered_from_the_keyboard_and_the_pipeline_is_kept() {
    let Some(mut desktop) = Desktop::start() else { return };
    let log = desktop.file("smoke.log", SAMPLE);
    desktop.launch(&[log.to_str().unwrap()]);
    if !desktop.window("smoke.log - Log Explorer") {
        return;
    }
    desktop.expect_view(SAMPLE);

    // Find END, close find, and Enter keeps the rows with the found text
    desktop.clear_selection();
    desktop.keys(&["ctrl+f"]);
    desktop.type_text("END");
    desktop.keys(&["Return", "Escape", "Return"]);
    let ends: Vec<&str> = SAMPLE.lines().filter(|line| line.contains("END")).collect();
    desktop.expect_view(&ends.join("\n"));

    // Delete on a selection removes that text from every row
    desktop.clear_selection();
    desktop.keys(&["ctrl+f"]);
    desktop.type_text("GET ");
    desktop.keys(&["Return", "Escape", "Delete"]);
    desktop.expect_view(&ends.join("\n").replace("GET ", ""));

    desktop.close();
    let kept = read(&desktop.data().join("working-set.json"));
    assert!(kept.contains("\"filter\"") && kept.contains("END") && kept.contains("\"replace\""), "{kept}");
}

#[test]
#[ignore = "drives a real window; run with --ignored"]
fn a_host_opens_several_files_interleaved_with_a_filter() {
    let Some(mut desktop) = Desktop::start() else { return };
    let (old, new) = (desktop.file("old.log", "x 1\nSTART a\nSTART b"), desktop.file("new.log", "y 1\nSTART c"));
    desktop.launch(&["--combine", "zip", "--filter", "START", "--case-sensitive", old.to_str().unwrap(), new.to_str().unwrap()]);
    if !desktop.window("Zipped: old.log + 1 more - Log Explorer") {
        return;
    }
    desktop.expect_view("START a\nSTART c\nSTART b");
    desktop.close();
}

#[test]
#[ignore = "drives a real window; run with --ignored"]
fn text_pasted_from_the_clipboard_becomes_the_source() {
    let Some(mut desktop) = Desktop::start() else { return };
    desktop.launch(&[]);
    if !desktop.window("Log Explorer") {
        return;
    }
    desktop.backend.set_clipboard("pasted one\npasted two");
    desktop.keys(&["ctrl+v"]);
    assert!(desktop.window("Clipboard - Log Explorer"));
    desktop.expect_view("pasted one\npasted two");
    // The keyboard walks and selects: Shift+End from the top takes everything too
    desktop.clear_selection();
    desktop.keys(&["Home", "shift+End", "ctrl+c"]);
    let copied = wait_for("the copied selection", Duration::from_secs(10), || Some(desktop.backend.clipboard().replace("\r\n", "\n")).filter(|text| text.contains("two")));
    assert_eq!(copied, "pasted one\npasted two");
    desktop.close();
}

#[test]
#[ignore = "drives a real window; run with --ignored"]
fn files_copied_in_the_file_manager_are_opened_by_paste() {
    let Some(mut desktop) = Desktop::start() else { return };
    let copied = desktop.file("copied file.log", "from a copied file\nsecond row");
    desktop.launch(&[]);
    if !desktop.window("Log Explorer") {
        return;
    }
    // What the file manager of the system puts on the clipboard for a copied file
    desktop.backend.set_clipboard_files(&[copied]);
    desktop.keys(&["ctrl+v"]);
    assert!(desktop.window("copied file.log - Log Explorer"));
    desktop.expect_view("from a copied file\nsecond row");
    desktop.close();
}

/// The accessibility tree the headless tests drive is what assistive tools (and UI
/// Automation tests) see of the real window: a button is found by its name and invoked.
#[cfg(windows)]
#[test]
#[ignore = "drives a real window; run with --ignored"]
fn ui_automation_finds_and_invokes_the_controls_by_name() {
    let Some(mut desktop) = Desktop::start() else { return };
    let log = desktop.file("uia.log", SAMPLE);
    desktop.launch(&[log.to_str().unwrap()]);
    if !desktop.window("uia.log - Log Explorer") {
        return;
    }
    let window = desktop.backend.find_window("uia.log - Log Explorer", desktop.pid()).expect("the window");
    platform::invoke_by_name(&window, "\u{2295} Filter");
    assert!(platform::has_element_named(&window, "Showing: after #1 Filter"), "the Filter added through UI Automation is shown as selected");
    // The new filter has no text yet: every row is still there
    desktop.expect_view(SAMPLE);
    desktop.close();
    assert!(read(&desktop.data().join("working-set.json")).contains("\"filter\""));
}
