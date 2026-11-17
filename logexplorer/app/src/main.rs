// No console window beside the app on Windows
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

fn main() -> eframe::Result {
    let options = match logexplorer::parse_args(std::env::args().skip(1)) {
        Ok(options) => options,
        Err(message) => {
            eprintln!("{message}");
            std::process::exit(if message.starts_with("Usage") { 0 } else { 2 });
        }
    };
    // An elevated program on Windows gets its files dropped another way (see elevated_drop)
    #[cfg(windows)]
    let elevated = logexplorer::elevated_drop::is_elevated();
    #[cfg(not(windows))]
    let elevated = false;
    let native = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title("Log Explorer")
            .with_app_id("logexplorer")
            .with_inner_size([1280.0, 800.0])
            .with_min_inner_size([640.0, 360.0])
            .with_drag_and_drop(!elevated),
        // Where Explorer's file list must be read on Ctrl+V, the app handles the clipboard keys itself
        clipboard_shortcuts: !logexplorer::clipboard::OWN_SHORTCUTS,
        ..Default::default()
    };
    eframe::run_native(
        "Log Explorer",
        native,
        Box::new(move |context| {
            #[cfg(windows)]
            if elevated {
                use raw_window_handle::{HasWindowHandle, RawWindowHandle};
                if let Ok(RawWindowHandle::Win32(window)) = context.window_handle().map(|handle| handle.as_raw()) {
                    logexplorer::elevated_drop::accept(window.hwnd.get(), &context.egui_ctx);
                }
            }
            Ok(Box::new(logexplorer::create(&context.egui_ctx, options)))
        }),
    )
}
