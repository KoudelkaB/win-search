//! The desktop of a smoke test on Windows: the session the test runs in. Keys are real
//! input (SendInput), the clipboard is the system one, and controls are found through UI
//! Automation - which the accessibility tree of the app (AccessKit) is exposed as.

use std::path::PathBuf;
use std::process::Command;
use std::time::{Duration, Instant};
use uiautomation::patterns::UIInvokePattern;
use uiautomation::types::Handle;
use uiautomation::UIAutomation;
use windows::core::BOOL;
use windows::Win32::Foundation::{HWND, LPARAM};
use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, VIRTUAL_KEY, VK_CONTROL, VK_DELETE, VK_END, VK_ESCAPE, VK_F3,
    VK_F4, VK_HOME, VK_MENU, VK_RETURN, VK_SHIFT,
};
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, EnumWindows, GetForegroundWindow, GetWindowTextW, GetWindowThreadProcessId, IsIconic, IsWindowVisible, SetForegroundWindow, ShowWindow, SwitchToThisWindow,
    SW_RESTORE,
};

/// A top-level window of the app
#[derive(Clone, Copy)]
pub struct Window(isize);

impl Window {
    fn handle(&self) -> HWND {
        HWND(self.0 as *mut core::ffi::c_void)
    }
}

/// The desktop of the session, and the window of the app that was given the keyboard
pub struct Backend {
    keyboard: std::cell::Cell<Option<Window>>,
}

/// (window, title, process) of the visible top-level windows
fn windows() -> Vec<(HWND, String, u32)> {
    unsafe extern "system" fn collect(window: HWND, list: LPARAM) -> BOOL {
        let list = unsafe { &mut *(list.0 as *mut Vec<(HWND, String, u32)>) };
        if unsafe { IsWindowVisible(window) }.as_bool() {
            let mut title = [0u16; 512];
            let length = unsafe { GetWindowTextW(window, &mut title) }.max(0) as usize;
            let mut process = 0u32;
            unsafe { GetWindowThreadProcessId(window, Some(&mut process)) };
            list.push((window, String::from_utf16_lossy(&title[..length]), process));
        }
        BOOL(1)
    }
    let mut list: Vec<(HWND, String, u32)> = Vec::new();
    let _ = unsafe { EnumWindows(Some(collect), LPARAM(&mut list as *mut _ as isize)) };
    list
}

fn send(inputs: &[INPUT]) {
    let sent = unsafe { SendInput(inputs, std::mem::size_of::<INPUT>() as i32) };
    assert_eq!(sent as usize, inputs.len(), "the keys were sent");
}

fn key_input(key: VIRTUAL_KEY, scan: u16, flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: key, wScan: scan, dwFlags: flags, time: 0, dwExtraInfo: 0 } } }
}

/// The virtual key of a key named as xdotool names it
fn virtual_key(name: &str) -> VIRTUAL_KEY {
    match name {
        "ctrl" => VK_CONTROL,
        "shift" => VK_SHIFT,
        "alt" => VK_MENU,
        "Return" => VK_RETURN,
        "Escape" => VK_ESCAPE,
        "Delete" => VK_DELETE,
        "Home" => VK_HOME,
        "End" => VK_END,
        "F3" => VK_F3,
        "F4" => VK_F4,
        letter if letter.len() == 1 && letter.as_bytes()[0].is_ascii_alphanumeric() => VIRTUAL_KEY(letter.as_bytes()[0].to_ascii_uppercase() as u16),
        other => panic!("no virtual key for '{other}'"),
    }
}

impl Backend {
    pub fn start() -> Option<Backend> {
        Some(Backend { keyboard: Default::default() })
    }

    pub fn prepare(&self, _command: &mut Command) {}

    /// Real keys go to the foreground window, whichever program it belongs to: none is sent
    /// unless that is the window of the app. A window that got in front is asked back once.
    fn send(&self, inputs: &[INPUT]) {
        if let Some(window) = self.keyboard.get() {
            let in_front = || unsafe { GetForegroundWindow() } == window.handle();
            assert!(
                in_front() || (self.activate(&window) && in_front()),
                "another window took the foreground from the app, so no key was sent: leave the desktop alone while the smoke tests run"
            );
        }
        send(inputs);
    }

    /// The window of process `pid` whose title starts with `title`
    pub fn find_window(&self, title: &str, pid: u32) -> Option<Window> {
        windows().into_iter().find(|(_, text, process)| *process == pid && text.starts_with(title)).map(|(window, _, _)| Window(window.0 as isize))
    }

    /// Brings the window to the foreground, where real keys go. Windows lets only the
    /// foreground program hand the foreground on, so the test joins the input of the
    /// current foreground thread for the moment of the switch.
    pub fn activate(&self, window: &Window) -> bool {
        let target = window.handle();
        unsafe {
            let foreground = GetForegroundWindow();
            let foreground_thread = GetWindowThreadProcessId(foreground, None);
            let own_thread = GetCurrentThreadId();
            let attached = foreground_thread != 0 && foreground_thread != own_thread && AttachThreadInput(own_thread, foreground_thread, true).as_bool();
            if IsIconic(target).as_bool() {
                let _ = ShowWindow(target, SW_RESTORE);
            }
            let _ = BringWindowToTop(target);
            // Refused while the user has just typed elsewhere (the foreground lock); the
            // Alt+Tab switch still works then and sends no key to anybody
            if !SetForegroundWindow(target).as_bool() || GetForegroundWindow() != target {
                SwitchToThisWindow(target, true);
            }
            if attached {
                let _ = AttachThreadInput(own_thread, foreground_thread, false);
            }
        }
        let started = Instant::now();
        while started.elapsed() < Duration::from_secs(5) {
            if unsafe { GetForegroundWindow() } == target {
                self.keyboard.set(Some(*window));
                // The app takes a moment to see that it has the keyboard
                std::thread::sleep(Duration::from_millis(300));
                return true;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        false
    }

    /// A key in the notation of xdotool: `ctrl+a`, `shift+End`, `Return`
    pub fn key(&self, key: &str) {
        let keys: Vec<VIRTUAL_KEY> = key.split('+').map(virtual_key).collect();
        let mut inputs: Vec<INPUT> = keys.iter().map(|key| key_input(*key, 0, KEYBD_EVENT_FLAGS(0))).collect();
        inputs.extend(keys.iter().rev().map(|key| key_input(*key, 0, KEYEVENTF_KEYUP)));
        self.send(&inputs);
    }

    pub fn type_text(&self, text: &str) {
        for unit in text.encode_utf16() {
            self.send(&[key_input(VIRTUAL_KEY(0), unit, KEYEVENTF_UNICODE), key_input(VIRTUAL_KEY(0), unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)]);
            std::thread::sleep(Duration::from_millis(30));
        }
    }

    pub fn clipboard(&self) -> String {
        arboard::Clipboard::new().and_then(|mut clipboard| clipboard.get_text()).unwrap_or_default()
    }

    pub fn set_clipboard(&self, text: &str) {
        arboard::Clipboard::new().and_then(|mut clipboard| clipboard.set_text(text)).expect("the clipboard is set");
    }

    /// What Explorer puts on the clipboard for copied files: a file list and no text
    pub fn set_clipboard_files(&self, files: &[PathBuf]) {
        arboard::Clipboard::new().and_then(|mut clipboard| clipboard.set().file_list(files)).expect("the clipboard is set");
    }
}

fn element_named(window: &Window, name: &str, timeout: Duration) -> Option<uiautomation::UIElement> {
    let automation = UIAutomation::new().expect("UI Automation");
    let root = automation.element_from_handle(Handle::from(window.0)).expect("the element of the window");
    automation.create_matcher().from(root).name(name).timeout(timeout.as_millis() as u64).find_first().ok()
}

/// Invokes (presses) the control called `name` in the window
pub fn invoke_by_name(window: &Window, name: &str) {
    let element = element_named(window, name, Duration::from_secs(15)).unwrap_or_else(|| panic!("no control named '{name}'"));
    match element.get_pattern::<UIInvokePattern>() {
        Ok(invoke) => invoke.invoke().expect("the control is invoked"),
        // A control without the pattern is clicked where it is
        Err(_) => element.click().expect("the control is clicked"),
    }
}

pub fn has_element_named(window: &Window, name: &str) -> bool {
    element_named(window, name, Duration::from_secs(15)).is_some()
}
