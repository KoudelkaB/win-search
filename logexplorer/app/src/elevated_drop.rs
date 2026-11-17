//! Files dropped into the program while it runs as an administrator.
//!
//! Started by an elevated File Search Manager the program is elevated too, and Windows keeps
//! a program of lower rights - Explorer - from talking to its windows: the drop target the
//! window library registers (OLE) is never called, and the pointer shows "not allowed".
//! The older way of dropping files still works across that border once the window lets its
//! three messages through: the window accepts files (`DragAcceptFiles`), Explorer posts
//! `WM_DROPFILES`, and the names are read from the message. So an elevated program takes
//! the drop that way instead - without the "drop here" picture, which needs the OLE target.

use std::path::PathBuf;
use std::sync::Mutex;

use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::Security::{GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::Win32::UI::Shell::{DefSubclassProc, DragAcceptFiles, DragFinish, DragQueryFileW, SetWindowSubclass, HDROP};
use windows::Win32::UI::WindowsAndMessaging::{ChangeWindowMessageFilterEx, MSGFLT_ALLOW, WM_COPYDATA, WM_DROPFILES};

/// Explorer hands the dropped names over with this message; it has no name in the headers
const WM_COPYGLOBALDATA: u32 = 0x0049;

/// What was dropped and not opened yet, and the context to wake for it
static DROPPED: Mutex<(Vec<PathBuf>, Option<egui::Context>)> = Mutex::new((Vec::new(), None));

/// The program runs with the rights of an administrator (after the UAC question)
pub fn is_elevated() -> bool {
    unsafe {
        let mut token = HANDLE::default();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
            return false;
        }
        let mut elevation = TOKEN_ELEVATION::default();
        let mut size = 0;
        let read = GetTokenInformation(token, TokenElevation, Some(&mut elevation as *mut _ as *mut _), size_of::<TOKEN_ELEVATION>() as u32, &mut size);
        let _ = CloseHandle(token);
        read.is_ok() && elevation.TokenIsElevated != 0
    }
}

/// Lets the window take files dropped by programs of lower rights. The window must not
/// have the OLE drop target (`with_drag_and_drop(false)`): it would be asked first.
pub fn accept(window: isize, ctx: &egui::Context) {
    DROPPED.lock().unwrap().1 = Some(ctx.clone());
    let window = HWND(window as *mut _);
    unsafe {
        for message in [WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA] {
            let _ = ChangeWindowMessageFilterEx(window, message, MSGFLT_ALLOW, None);
        }
        DragAcceptFiles(window, true);
        let _ = SetWindowSubclass(window, Some(window_message), 1, 0);
    }
}

/// The files dropped since the last call
pub fn take() -> Vec<PathBuf> {
    std::mem::take(&mut DROPPED.lock().unwrap().0)
}

unsafe extern "system" fn window_message(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM, _id: usize, _data: usize) -> LRESULT {
    if message != WM_DROPFILES {
        return DefSubclassProc(window, message, wparam, lparam);
    }
    let drop = HDROP(wparam.0 as *mut _);
    let count = DragQueryFileW(drop, u32::MAX, None);
    let mut paths = Vec::new();
    for index in 0..count {
        let length = DragQueryFileW(drop, index, None) as usize;
        let mut name = vec![0u16; length + 1];
        let written = DragQueryFileW(drop, index, Some(&mut name)) as usize;
        if written > 0 {
            paths.push(PathBuf::from(String::from_utf16_lossy(&name[..written])));
        }
    }
    DragFinish(drop);
    let mut dropped = DROPPED.lock().unwrap();
    dropped.0.extend(paths);
    if let Some(ctx) = &dropped.1 {
        ctx.request_repaint();
    }
    LRESULT(0)
}
