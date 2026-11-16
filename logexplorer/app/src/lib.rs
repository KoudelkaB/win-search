//! Log Explorer: a viewer for big logs with a staged Filter / Replace / Sort / Diff /
//! Extract / Unique / Join pipeline. The engine is `logexplorer-core`; this crate is its UI.

pub mod app;
pub mod clipboard;
pub mod panels;
pub mod storage;
pub mod theme;
pub mod viewer;

pub use app::{App, LoadAction, Options};

impl eframe::App for App {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        panels::show(ui, self);
    }
}

/// The application for a window or a test harness.
pub fn create(ctx: &egui::Context, options: Options) -> App {
    theme::apply(ctx);
    let mut app = App::new(Options { data_dir: options.data_dir.clone(), ..options.clone() });
    app.set_repaint(ctx);
    app
}

/// Reads the command line: `[--combine append|zip] [--filter TEXT] [--case-sensitive]
/// [--lang LANG] [--data-dir DIR] [FILE...]`
pub fn parse_args(args: impl IntoIterator<Item = String>) -> Result<Options, String> {
    let mut options = Options { data_dir: storage::Storage::default_dir(), ..Default::default() };
    let mut filter: Option<String> = None;
    let mut case_sensitive = false;
    let mut args = args.into_iter();
    let mut only_files = false;
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
            "--help" | "-h" => return Err(USAGE.to_string()),
            other if other.starts_with("--") => return Err(format!("unknown option {other}\n{USAGE}")),
            _ => options.files.push(arg.into()),
        }
    }
    options.filter = filter.map(|text| (text, case_sensitive));
    Ok(options)
}

pub const USAGE: &str = "Usage: logexplorer [--combine append|zip] [--filter TEXT] [--case-sensitive] [--lang LANG] [--data-dir DIR] [FILE...]
  --combine append|zip   load several files without asking: one after another, or interleaved row by row
  --filter TEXT          start the pipeline with a Filter that keeps the rows containing TEXT
  --case-sensitive       the filter of --filter matches case
  --lang LANG            language of the UI (cs, de, es, fr, it, ja, ko, pl, pt-BR, zh-Hans, en)
  --data-dir DIR         where profiles are kept";
