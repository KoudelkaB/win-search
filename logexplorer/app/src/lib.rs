//! Log Explorer: a viewer for big logs with a staged Filter / Replace / Sort / Diff /
//! Extract / Unique / Join pipeline. The engine is `logexplorer-core`; this crate is its UI.

pub mod app;
pub mod clipboard;
#[cfg(windows)]
pub mod elevated_drop;
pub mod panels;
pub mod storage;
pub mod theme;
pub mod viewer;

pub use app::{App, LoadAction, Options};

impl eframe::App for App {
    fn ui(&mut self, ui: &mut egui::Ui, frame: &mut eframe::Frame) {
        if self.windows_software_renderer {
            if let Some(mut surface) = frame.wgpu_surface_config() {
                // Save a display interval on frames that are already expensive on WARP.
                surface.present_mode = eframe::wgpu::PresentMode::AutoNoVsync;
                frame.set_wgpu_surface_config(surface);
            }
        }
        panels::show(ui, self);
    }

    fn raw_input_hook(&mut self, ctx: &egui::Context, raw_input: &mut egui::RawInput) {
        panels::one_key_press_a_frame(ctx, self, raw_input);
    }

    fn clear_color(&self, _visuals: &egui::Visuals) -> [f32; 4] {
        let color = if self.windows_software_renderer { theme::PANEL } else { egui::Color32::from_rgba_unmultiplied(12, 12, 12, 180) };
        color.to_normalized_gamma_f32()
    }
}

/// The application for a window or a test harness.
pub fn create(ctx: &egui::Context, options: Options) -> App {
    theme::apply(ctx);
    let mut app = App::new(Options { data_dir: options.data_dir.clone(), ..options.clone() });
    app.set_repaint(ctx);
    app
}

pub fn create_native(context: &eframe::CreationContext<'_>, options: Options) -> App {
    let mut app = create(&context.egui_ctx, options);
    app.windows_software_renderer =
        cfg!(windows) && context.wgpu_render_state.as_ref().is_some_and(|render_state| render_state.adapter.get_info().device_type == eframe::wgpu::DeviceType::Cpu);
    app
}

/// Tunes wgpu for a 2D text viewer: small GPU allocator blocks, low-power adapter
/// preference, and on Windows primary backends only (Vulkan and DX12 with the system
/// WARP fallback) so the OpenGL ICD driver is not loaded alongside them.
pub fn configure_wgpu(config: &mut eframe::egui_wgpu::WgpuConfiguration) {
    let eframe::egui_wgpu::WgpuSetup::CreateNew(setup) = &mut config.wgpu_setup else {
        return;
    };
    #[cfg(windows)]
    if eframe::wgpu::Backends::from_env().is_none() {
        setup.instance_descriptor.backends = eframe::wgpu::Backends::PRIMARY;
    }
    if eframe::wgpu::PowerPreference::from_env().is_none() {
        setup.power_preference = eframe::wgpu::PowerPreference::LowPower;
    }
    let default_descriptor = std::sync::Arc::clone(&setup.device_descriptor);
    setup.device_descriptor = std::sync::Arc::new(move |adapter| {
        let mut descriptor = default_descriptor(adapter);
        descriptor.memory_hints = eframe::wgpu::MemoryHints::Manual { suballocated_device_memory_block_size: (4 << 20)..(16 << 20) };
        descriptor
    });
}

/// Reads the command line: `[--combine append|zip] [--filter TEXT] [--case-sensitive]
/// [--lang LANG] [--data-dir DIR] [--files0-from LIST] [FILE...]`
pub fn parse_args(args: impl IntoIterator<Item = String>) -> Result<Options, String> {
    let mut options = Options { data_dir: storage::Storage::default_dir(), ..Default::default() };
    let mut filter: Option<String> = None;
    let mut case_sensitive = false;
    let mut args = args.into_iter();
    let mut only_files = false;
    let mut list: Option<String> = None;
    while let Some(arg) = args.next() {
        let mut value = |name: &str| args.next().ok_or_else(|| format!("{name} needs a value"));
        match arg.as_str() {
            _ if only_files => options.files.push(arg.into()),
            "--" => only_files = true,
            "--combine" => {
                options.combine_zip = Some(match value("--combine")?.as_str() {
                    "append" | "concat" => false,
                    "zip" => true,
                    other => return Err(format!("--combine is append or zip, not {other}")),
                })
            }
            "--filter" => filter = Some(value("--filter")?),
            "--case-sensitive" => case_sensitive = true,
            "--lang" => options.language = Some(value("--lang")?),
            "--data-dir" => options.data_dir = Some(value("--data-dir")?.into()),
            "--files0-from" => list = Some(value("--files0-from")?),
            "--help" | "-h" => return Err(USAGE.to_string()),
            other if other.starts_with("--") => return Err(format!("unknown option {other}\n{USAGE}")),
            _ => options.files.push(arg.into()),
        }
    }
    options.filter = filter.map(|text| (text, case_sensitive));
    if let Some(list) = list {
        let bytes = if list == "-" {
            let mut bytes = Vec::new();
            std::io::Read::read_to_end(&mut std::io::stdin(), &mut bytes).map(|_| bytes)
        } else {
            std::fs::read(&list)
        };
        options.files.extend(file_list(&bytes.map_err(|error| format!("--files0-from {list}: {error}"))?));
    }
    Ok(options)
}

/// The files of `--files0-from`: each name ends with a NUL byte (a name may hold any other
/// character, a line break too). A list longer than a command line comes this way - Windows
/// caps that at 32 767 characters (CreateProcessW).
pub fn file_list(bytes: &[u8]) -> Vec<std::path::PathBuf> {
    bytes.split(|byte| *byte == 0).filter(|name| !name.is_empty()).map(path_of).collect()
}

#[cfg(unix)]
fn path_of(name: &[u8]) -> std::path::PathBuf {
    <std::ffi::OsStr as std::os::unix::ffi::OsStrExt>::from_bytes(name).into()
}

#[cfg(not(unix))]
fn path_of(name: &[u8]) -> std::path::PathBuf {
    String::from_utf8_lossy(name).into_owned().into()
}

pub const USAGE: &str = "Usage: logexplorer [--combine append|zip] [--filter TEXT] [--case-sensitive] [--lang LANG] [--data-dir DIR] [--files0-from LIST] [FILE...]
  --combine append|zip   load several files without asking: one after another, or interleaved row by row
  --filter TEXT          start the pipeline with a Filter that keeps the rows containing TEXT
  --case-sensitive       the filter of --filter matches case
  --lang LANG            language of the UI (cs, de, es, fr, it, ja, ko, pl, pt-BR, zh-Hans, en)
  --data-dir DIR         where profiles are kept
  --files0-from LIST     also open the files named in LIST (- is standard input), each name ended by a NUL byte";

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn a_file_list_ends_each_name_with_nul_and_keeps_every_other_character() {
        assert_eq!(file_list(b"/logs/a b.log\0/logs/line\nbreak.log\0"), vec![PathBuf::from("/logs/a b.log"), PathBuf::from("/logs/line\nbreak.log")]);
        // The last name may end without NUL, an empty name is none
        assert_eq!(file_list(b"\0/a.log\0\0/b.log"), vec![PathBuf::from("/a.log"), PathBuf::from("/b.log")]);
        assert!(file_list(b"").is_empty());
    }

    #[test]
    fn the_files_of_a_list_follow_those_of_the_command_line() {
        let folder = tempfile::tempdir().unwrap();
        let list = folder.path().join("files");
        // Longer than a command line on Windows
        let names: Vec<String> = (0..400).map(|index| format!("/logs/{}/{index}.log", "x".repeat(80))).collect();
        std::fs::write(&list, names.join("\0")).unwrap();
        let options = parse_args(["--combine", "append", "--files0-from", list.to_str().unwrap(), "--", "/first.log"].map(String::from)).unwrap();
        assert_eq!(options.files.len(), 401);
        assert_eq!(options.files[0], PathBuf::from("/first.log"));
        assert_eq!(options.files[400], PathBuf::from(&names[399]));
        assert_eq!(options.combine_zip, Some(false));
        assert!(parse_args(["--files0-from", folder.path().join("missing").to_str().unwrap()].map(String::from)).unwrap_err().starts_with("--files0-from"));
    }

    #[test]
    fn wgpu_options_use_low_power_and_primary_backends_on_windows() {
        let mut native = eframe::NativeOptions::default();
        configure_wgpu(&mut native.wgpu_options);
        let eframe::egui_wgpu::WgpuSetup::CreateNew(setup) = native.wgpu_options.wgpu_setup else {
            panic!("expected CreateNew wgpu setup");
        };
        if eframe::wgpu::PowerPreference::from_env().is_none() {
            assert_eq!(setup.power_preference, eframe::wgpu::PowerPreference::LowPower);
        }
        #[cfg(windows)]
        if eframe::wgpu::Backends::from_env().is_none() {
            assert_eq!(setup.instance_descriptor.backends, eframe::wgpu::Backends::PRIMARY);
        }
    }
}
