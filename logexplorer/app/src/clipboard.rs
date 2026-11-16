//! Pasting: text, or the files a file manager put on the clipboard.
//!
//! A file manager of Linux offers copied files as text too (their paths or `file://`
//! addresses), so there the pasted text tells. Explorer of Windows offers only a file
//! list - a paste the UI toolkit reports nothing for - so on Windows this app reads the
//! clipboard for Ctrl+V itself.

use std::path::PathBuf;

pub enum Pasted {
    Empty,
    Text(String),
    Files(Vec<PathBuf>),
}

pub trait ClipboardSource {
    fn read(&mut self) -> Pasted;
}

/// The clipboard of the system
pub struct SystemClipboard;

impl ClipboardSource for SystemClipboard {
    fn read(&mut self) -> Pasted {
        let Ok(mut clipboard) = arboard::Clipboard::new() else { return Pasted::Empty };
        if let Ok(files) = clipboard.get().file_list() {
            if !files.is_empty() {
                return Pasted::Files(files);
            }
        }
        match clipboard.get_text() {
            Ok(text) if !text.is_empty() => Pasted::Text(text.replace("\r\n", "\n")),
            _ => Pasted::Empty,
        }
    }
}

/// Whether this app turns the clipboard keys into cut, copy and paste itself (see above);
/// elsewhere the UI toolkit does, and must be left to.
pub const OWN_SHORTCUTS: bool = cfg!(windows);

fn is_cut(modifiers: egui::Modifiers, key: egui::Key) -> bool {
    key == egui::Key::Cut || (modifiers.command_only() && key == egui::Key::X) || (cfg!(windows) && modifiers.shift && key == egui::Key::Delete)
}

fn is_copy(modifiers: egui::Modifiers, key: egui::Key) -> bool {
    key == egui::Key::Copy || (modifiers.command_only() && key == egui::Key::C) || (cfg!(windows) && modifiers.ctrl && key == egui::Key::Insert)
}

fn is_paste(modifiers: egui::Modifiers, key: egui::Key) -> bool {
    key == egui::Key::Paste || (modifiers.command_only() && key == egui::Key::V) || (cfg!(windows) && modifiers.shift && key == egui::Key::Insert)
}

/// Turns the clipboard keys of this frame into cut, copy and paste events, like the UI
/// toolkit does when it handles them. Pasted text becomes a paste event; pasted files are
/// returned.
pub fn translate_keys(ctx: &egui::Context, source: &mut dyn ClipboardSource) -> Option<Vec<PathBuf>> {
    let mut paste_at = None;
    ctx.input_mut(|input| {
        for (index, event) in input.events.iter_mut().enumerate() {
            let egui::Event::Key { key, pressed: true, modifiers, .. } = event else { continue };
            let (key, modifiers) = (*key, *modifiers);
            if is_cut(modifiers, key) {
                *event = egui::Event::Cut;
            } else if is_copy(modifiers, key) {
                *event = egui::Event::Copy;
            } else if is_paste(modifiers, key) && paste_at.is_none() {
                paste_at = Some(index);
            }
        }
    });
    let index = paste_at?;
    let pasted = source.read();
    let mut files = None;
    ctx.input_mut(|input| match pasted {
        Pasted::Text(text) => input.events[index] = egui::Event::Paste(text),
        Pasted::Files(paths) => {
            input.events.remove(index);
            files = Some(paths);
        }
        Pasted::Empty => {
            input.events.remove(index);
        }
    });
    files
}

fn percent_decoded(text: &str) -> String {
    let bytes = text.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        let hex = |at: usize| bytes.get(at).and_then(|byte| (*byte as char).to_digit(16));
        match (bytes[index], hex(index + 1), hex(index + 2)) {
            (b'%', Some(high), Some(low)) => {
                out.push((high * 16 + low) as u8);
                index += 3;
            }
            (byte, _, _) => {
                out.push(byte);
                index += 1;
            }
        }
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// The file a line of pasted text names: an absolute path or a `file://` address
fn file_of(line: &str) -> Option<PathBuf> {
    let path = match line.strip_prefix("file://") {
        Some(rest) => {
            // file://host/path: only this machine
            let local = rest.strip_prefix("localhost").unwrap_or(rest);
            if !local.starts_with('/') {
                return None;
            }
            let decoded = percent_decoded(local);
            // file:///C:/logs/a.log
            let windows_drive = decoded.as_bytes().get(2) == Some(&b':') && decoded.as_bytes()[1].is_ascii_alphabetic();
            PathBuf::from(if cfg!(windows) && windows_drive { decoded[1..].to_string() } else { decoded })
        }
        None => PathBuf::from(line),
    };
    (path.is_absolute() && path.is_file()).then_some(path)
}

/// The files a pasted text names, when it is nothing but existing files: what a file
/// manager puts on the clipboard for copied files.
pub fn files_named_by(text: &str) -> Option<Vec<PathBuf>> {
    let lines: Vec<&str> = text.lines().map(str::trim).filter(|line| !line.is_empty()).collect();
    if lines.is_empty() || lines.len() > 10_000 || text.len() > (4 << 20) {
        return None;
    }
    lines.into_iter().map(file_of).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_text_of_existing_files_names_them_and_any_other_text_does_not() {
        let dir = tempfile::tempdir().unwrap();
        let (plain, spaced) = (dir.path().join("a.log"), dir.path().join("with space ž.log"));
        std::fs::write(&plain, "a").unwrap();
        std::fs::write(&spaced, "b").unwrap();
        let address = format!("file://{}", spaced.to_string_lossy().replace(' ', "%20").replace('ž', "%C5%BE"));
        let address = if cfg!(windows) { address.replace("file://", "file:///").replace('\\', "/") } else { address };

        assert_eq!(files_named_by(&plain.to_string_lossy()), Some(vec![plain.clone()]));
        assert_eq!(files_named_by(&format!("{}\n{address}\n", plain.display())), Some(vec![plain.clone(), spaced.clone()]));
        // A folder, a missing file, a relative name or any other line: the text is text
        assert_eq!(files_named_by(&dir.path().to_string_lossy()), None);
        assert_eq!(files_named_by(&format!("{}\nERROR something", plain.display())), None);
        assert_eq!(files_named_by(&format!("{}.missing", plain.display())), None);
        assert_eq!(files_named_by("a.log"), None);
        assert_eq!(files_named_by("file://elsewhere/share/a.log"), None);
        assert_eq!(files_named_by("  \n"), None);
    }

    #[test]
    fn percent_escapes_are_decoded() {
        assert_eq!(percent_decoded("/a%20b/%C5%BE%zz%2"), "/a b/ž%zz%2");
    }
}
