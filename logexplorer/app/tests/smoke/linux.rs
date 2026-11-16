//! The X server of a smoke test: Xvfb with a window manager, driven by xdotool and xclip.

use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::time::Duration;

fn have(tool: &str) -> bool {
    Command::new("sh").args(["-c", &format!("command -v {tool}")]).stdout(Stdio::null()).status().is_ok_and(|status| status.success())
}

pub struct Backend {
    display: String,
    server: Child,
    manager: Child,
}

impl Drop for Backend {
    fn drop(&mut self) {
        for child in [&mut self.manager, &mut self.server] {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

impl Backend {
    pub fn start() -> Option<Backend> {
        let missing: Vec<&str> = ["Xvfb", "openbox", "xdotool", "xclip"].into_iter().filter(|tool| !have(tool)).collect();
        if !missing.is_empty() {
            crate::skip(&format!("the smoke tests need {missing:?}"));
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
        Some(Backend { display, server, manager })
    }

    /// The app runs on this display, never on the Wayland session of the user
    pub fn prepare(&self, command: &mut Command) {
        command.env("DISPLAY", &self.display).env_remove("WAYLAND_DISPLAY");
    }

    fn run(&self, program: &str, args: &[&str]) -> Option<String> {
        let mut command = Command::new(program);
        self.prepare(command.args(args).stderr(Stdio::null()));
        let output = command.output().ok()?;
        output.status.success().then(|| String::from_utf8_lossy(&output.stdout).into_owned())
    }

    /// The window whose title starts with `title` (the display has no other program)
    pub fn find_window(&self, title: &str, _pid: u32) -> Option<String> {
        let pattern = format!("^{}", title.replace('.', "\\.").replace('+', "\\+"));
        self.run("xdotool", &["search", "--onlyvisible", "--name", &pattern]).and_then(|ids| ids.lines().next().map(str::to_string))
    }

    pub fn activate(&self, window: &str) -> bool {
        self.run("xdotool", &["windowactivate", "--sync", window]).is_some()
    }

    /// A key in the notation of xdotool: `ctrl+a`, `shift+End`, `Return`
    pub fn key(&self, key: &str) {
        self.run("xdotool", &["key", "--clearmodifiers", key]).expect("key sent");
    }

    pub fn type_text(&self, text: &str) {
        self.run("xdotool", &["type", "--delay", "30", text]).expect("text typed");
    }

    pub fn clipboard(&self) -> String {
        self.run("xclip", &["-o", "-selection", "clipboard"]).unwrap_or_default()
    }

    pub fn set_clipboard(&self, text: &str) {
        let mut command = Command::new("xclip");
        self.prepare(command.args(["-i", "-selection", "clipboard"]).stdin(Stdio::piped()).stdout(Stdio::null()).stderr(Stdio::null()));
        let mut xclip = command.spawn().expect("xclip");
        xclip.stdin.take().unwrap().write_all(text.as_bytes()).unwrap();
        xclip.wait().unwrap();
    }

    /// A file manager offers copied files as the text of their paths too
    pub fn set_clipboard_files(&self, files: &[PathBuf]) {
        let paths: Vec<String> = files.iter().map(|file| file.to_string_lossy().into_owned()).collect();
        self.set_clipboard(&paths.join("\n"));
    }
}
