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
    let mut native = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title("Log Explorer")
            .with_app_id("io.github.koudelkab.LogExplorer")
            .with_icon(eframe::icon_data::from_png_bytes(include_bytes!("../assets/logexplorer.png")).expect("embedded Log Explorer icon"))
            .with_inner_size([1280.0, 800.0])
            .with_min_inner_size([640.0, 360.0])
            .with_drag_and_drop(!elevated),
        // Where Explorer's file list must be read on Ctrl+V, the app handles the clipboard keys itself
        clipboard_shortcuts: !logexplorer::clipboard::OWN_SHORTCUTS,
        #[cfg(target_os = "linux")]
        event_loop_builder: Some(Box::new(|builder| {
            use winit::platform::x11::EventLoopBuilderExtX11 as _;

            // Winit 0.30 receives file drops through X11. On a Wayland desktop use
            // XWayland when it is reachable and leave native Wayland as the fallback.
            let wayland = ["WAYLAND_DISPLAY", "WAYLAND_SOCKET"].iter().any(|name| std::env::var_os(name).is_some_and(|value| !value.is_empty()));
            if wayland && x11rb::connect(None).is_ok() {
                builder.with_x11();
            }
        })),
        ..Default::default()
    };
    logexplorer::configure_wgpu(&mut native.wgpu_options);
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
            Ok(Box::new(logexplorer::create_native(context, options)))
        }),
    )
}
