//! Smoke tests: the built application as its own process on an X display of its own,
//! driven from outside with the real keyboard and clipboard (Xvfb, openbox, xdotool,
//! xclip - the tools of the smoke tests of File Search Manager).
//!
//! `cargo test -p logexplorer --test smoke -- --ignored`
//!
//! They are skipped where the tools are missing, unless CI is set: there a missing tool fails.

#![cfg(target_os = "linux")]

use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

const SAMPLE: &str = "[4711] 09:14:02.105 START GET /api/orders
[4711] 09:14:02.248 END   200 GET /api/orders
[4712] 09:14:02.110 START POST /api/payments
[4712] 09:14:03.031 END   201 POST /api/payments";

fn have(tool: &str) -> bool {
    Command::new("sh").args(["-c", &format!("command -v {tool}")]).stdout(Stdio::null()).status().is_ok_and(|status| status.success())
}

/// An X server with a window manager, and the app running in it
struct Desktop {
    display: String,
    server: Child,
    manager: Child,
    app: Option<Child>,
    pub dir: tempfile::TempDir,
}

impl Drop for Desktop {
    fn drop(&mut self) {
        for child in self.app.iter_mut().chain([&mut self.manager, &mut self.server]) {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

fn wait_for<T>(what: &str, timeout: Duration, mut probe: impl FnMut() -> Option<T>) -> T {
    let started = Instant::now();
    loop {
        if let Some(found) = probe() {
            return found;
        }
        assert!(started.elapsed() < timeout, "timed out waiting for {what}");
        std::thread::sleep(Duration::from_millis(100));
    }
}

impl Desktop {
    /// `None` where the tools are missing (and CI is not set)
    fn start() -> Option<Desktop> {
        let missing: Vec<&str> = ["Xvfb", "openbox", "xdotool", "xclip"].into_iter().filter(|tool| !have(tool)).collect();
        if !missing.is_empty() {
            assert!(std::env::var_os("CI").is_none(), "the smoke tests need {missing:?}");
            eprintln!("skipped: {missing:?} not installed");
            return None;
        }
        // The server picks a free display and tells its number
        let mut server =
            Command::new("Xvfb").args(["-displayfd", "1", "-screen", "0", "1400x900x24", "-nolisten", "tcp"]).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().expect("Xvfb");
        let mut number = String::new();
        BufReader::new(server.stdout.take().unwrap()).read_line(&mut number).expect("display number");
        let display = format!(":{}", number.trim());
        let manager = Command::new("openbox").env("DISPLAY", &display).env_remove("WAYLAND_DISPLAY").stdout(Stdio::null()).stderr(Stdio::null()).spawn().expect("openbox");
        std::thread::sleep(Duration::from_millis(500));
        Some(Desktop { display, server, manager, app: None, dir: tempfile::tempdir().expect("temporary folder") })
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
        let app = Command::new(env!("CARGO_BIN_EXE_logexplorer"))
            .args(["--lang", "en", "--data-dir"])
            .arg(self.data())
            .args(args)
            .env("DISPLAY", &self.display)
            .env_remove("WAYLAND_DISPLAY")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .expect("the app starts");
        self.app = Some(app);
    }

    fn run(&self, program: &str, args: &[&str]) -> Option<String> {
        let output = Command::new(program).args(args).env("DISPLAY", &self.display).env_remove("WAYLAND_DISPLAY").stderr(Stdio::null()).output().ok()?;
        output.status.success().then(|| String::from_utf8_lossy(&output.stdout).into_owned())
    }

    /// Waits for the window whose title starts with `title` and gives it the keyboard
    fn window(&self, title: &str) -> String {
        let pattern = format!("^{}", title.replace('.', "\\.").replace('+', "\\+"));
        let id = wait_for(&format!("the window '{title}'"), Duration::from_secs(60), || {
            self.run("xdotool", &["search", "--onlyvisible", "--name", &pattern]).and_then(|ids| ids.lines().next().map(str::to_string))
        });
        self.run("xdotool", &["windowactivate", "--sync", &id]);
        id
    }

    fn keys(&self, keys: &[&str]) {
        for key in keys {
            self.run("xdotool", &["key", "--clearmodifiers", key]).expect("key sent");
            std::thread::sleep(Duration::from_millis(120));
        }
    }

    fn type_text(&self, text: &str) {
        self.run("xdotool", &["type", "--delay", "30", text]).expect("text typed");
        std::thread::sleep(Duration::from_millis(150));
    }

    fn clipboard(&self) -> String {
        self.run("xclip", &["-o", "-selection", "clipboard"]).unwrap_or_default()
    }

    fn set_clipboard(&self, text: &str) {
        let mut xclip = Command::new("xclip")
            .args(["-i", "-selection", "clipboard"])
            .env("DISPLAY", &self.display)
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .expect("xclip");
        xclip.stdin.take().unwrap().write_all(text.as_bytes()).unwrap();
        xclip.wait().unwrap();
    }

    /// Selects the whole view, copies it and waits until the clipboard holds `expected`
    fn expect_view(&self, expected: &str) {
        let mut last = String::new();
        let started = Instant::now();
        while started.elapsed() < Duration::from_secs(20) {
            self.keys(&["ctrl+a", "ctrl+c"]);
            last = self.clipboard();
            if last == expected {
                return;
            }
            std::thread::sleep(Duration::from_millis(200));
        }
        panic!("the view is not the expected one.\n--- expected ---\n{expected}\n--- copied ---\n{last}");
    }

    /// Closes the window the way a user does and waits for the process to end cleanly
    fn close(&mut self) {
        self.keys(&["alt+F4"]);
        let mut app = self.app.take().expect("the app runs");
        let status = wait_for("the app to exit", Duration::from_secs(20), || app.try_wait().expect("process state"));
        assert!(status.success(), "the app exited with {status}");
    }
}

fn read(path: &Path) -> String {
    std::fs::read_to_string(path).unwrap_or_default()
}

#[test]
#[ignore = "needs Xvfb, openbox, xdotool and xclip; run with --ignored"]
fn a_file_is_shown_filtered_from_the_keyboard_and_the_pipeline_is_kept() {
    let Some(mut desktop) = Desktop::start() else { return };
    let log = desktop.file("smoke.log", SAMPLE);
    desktop.launch(&[log.to_str().unwrap()]);
    desktop.window("smoke.log - Log Explorer");
    desktop.expect_view(SAMPLE);

    // Find END, close find, and Enter keeps the rows with the found text
    desktop.keys(&["ctrl+Escape", "ctrl+f"]);
    desktop.type_text("END");
    desktop.keys(&["Return", "Escape", "Return"]);
    let ends: Vec<&str> = SAMPLE.lines().filter(|line| line.contains("END")).collect();
    desktop.expect_view(&ends.join("\n"));

    // Delete on a selection removes that text from every row
    desktop.keys(&["ctrl+Escape", "ctrl+f"]);
    desktop.type_text("GET ");
    desktop.keys(&["Return", "Escape", "Delete"]);
    desktop.expect_view(&ends.join("\n").replace("GET ", ""));

    desktop.close();
    let kept = read(&desktop.data().join("working-set.json"));
    assert!(kept.contains("\"filter\"") && kept.contains("END") && kept.contains("\"replace\""), "{kept}");
}

#[test]
#[ignore = "needs Xvfb, openbox, xdotool and xclip; run with --ignored"]
fn a_host_opens_several_files_interleaved_with_a_filter() {
    let Some(mut desktop) = Desktop::start() else { return };
    let (old, new) = (desktop.file("old.log", "x 1\nSTART a\nSTART b"), desktop.file("new.log", "y 1\nSTART c"));
    desktop.launch(&["--combine", "zip", "--filter", "START", "--case-sensitive", old.to_str().unwrap(), new.to_str().unwrap()]);
    desktop.window("Zipped: old.log + 1 more - Log Explorer");
    desktop.expect_view("START a\nSTART c\nSTART b");
    desktop.close();
}

#[test]
#[ignore = "needs Xvfb, openbox, xdotool and xclip; run with --ignored"]
fn text_pasted_from_the_clipboard_becomes_the_source() {
    let Some(mut desktop) = Desktop::start() else { return };
    desktop.launch(&[]);
    desktop.window("Log Explorer");
    desktop.set_clipboard("pasted one\npasted two");
    desktop.keys(&["ctrl+v"]);
    desktop.window("Clipboard - Log Explorer");
    desktop.expect_view("pasted one\npasted two");
    // The keyboard walks and selects: Shift+End from the top takes everything too
    desktop.keys(&["ctrl+Escape", "Home", "shift+End", "ctrl+c"]);
    let copied = wait_for("the copied selection", Duration::from_secs(10), || Some(desktop.clipboard()).filter(|text| text.contains("two")));
    assert_eq!(copied, "pasted one\npasted two");
    desktop.close();
}
