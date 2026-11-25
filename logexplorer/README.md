# Log Explorer (Rust)

Log Explorer is a standalone native viewer for large logs on Windows and Linux (egui/eframe).
It supports staged Filter / Replace / Sort / Diff / Extract / Unique / Join operations,
smart click selection, find, per-file tints, and profiles. It handles files of gigabytes as
long as they fit in memory.

```sh
cargo run --release -p logexplorer -- [--combine append|zip] [--filter TEXT] [--case-sensitive]
                                      [--lang LANG] [--data-dir DIR] [--files0-from LIST] [FILE...]
```

`--combine` selects how to load multiple files without prompting, and `--filter` supplies
the first include filter.
`--files0-from` reads more files from LIST (`-` is standard input), each name ended by a NUL
byte: File Search Manager hands over a list longer than a Windows command line (32 767
characters) this way. Files can also be dropped into the window or pasted after copying them
in a file manager. The viewer opens JSON as text. Import profile bundles through
`≡` → Upload profiles.
On Wayland desktops Log Explorer uses XWayland for file drops when it is available.
In sessions without XWayland, open files with **Open file** or paste them from the file manager.
The close button beside Open file (`Ctrl+W`) unloads all files in the view and keeps the
current pipeline.
Line numbers are hidden by default. Use **App menu → Show line numbers** to turn them on.

## Layout

| Path | What |
| --- | --- |
| `core/` | `logexplorer-core`: the engine, no UI. Loading, stages, the operations, the pipeline cache, selection, find, profiles, texts. |
| `app/` | `logexplorer`: the egui application. `app.rs` is state and behaviour, `viewer.rs` the virtual file view, `panels.rs` everything around it. |
| `core/assets/translations.json` | UI translations and help text. Edit them here. |
| `tools/` | Scripts for generating license notices and application icons. |

## How memory is kept low

- The text of a file exists **once**: the bytes as read (UTF-8 without tabs is used in
  place; tabs, UTF-16 and invalid UTF-8 are converted once and the original released).
- The raw stage is an index of line starts: **4 bytes a row** (8 above 4 GiB).
- Every other stage is a list of **12-byte spans** into shared buffers plus the original
  row id (4 bytes). Filter, Sort, Unique and Extract write no text at all - an extracted
  value is a piece of an existing row. Replace, Diff and Join write only the rows they
  change into one new buffer.
- Stages are cached under the whole chain of operations that leads to them, so editing
  operation 5 keeps stages 1-4, and switching an operation off and on costs nothing.
  The least recently used stages are dropped when the cache passes the size of the source.

## How it stays fast

- Loading validates UTF-8, looks for tabs and indexes lines in parallel, chunk by chunk.
- Operations run over row chunks on all cores (rayon) and can be cancelled between chunks;
  the UI thread never computes - it shows the deepest stage already computed.
- Literal search uses `memchr`/Aho-Corasick without lowercasing or copying rows; regular
  expressions run row by row in parallel unless the pattern can match a line break.
- The view lays out only the rows on screen. Scrolling is kept in `f64` with scrollbars of
  its own, so a hundred million rows do not hit the `f32` limits of a scroll area.
- Click selection looks for brackets, tags and quotes around the click. If their boundaries
  lie farther away, a streaming scan finds them in the original rows and caches them for
  subsequent clicks at the same place. The same rules apply to standalone documents and to
  fragments mixed with log text. Clicking `=` in a tag selects the attribute. In ordinary
  expressions, clicking an operator follows the expression to its end, including multiline
  bracketed blocks and call chains.

Measured on a 32-thread machine, release build, the generated log of
`core/examples/bench.rs` (10.1 million rows, 1 GiB):

| | time | memory |
| --- | --- | --- |
| Load | 0.35 s | 1.04x the file |
| Filter keeping 50 % | 43 ms | 77 MiB |
| Sort by timestamp | 138 ms | 154 MiB |
| Diff of timestamps (rewrites every row) | 540 ms | 1.1 GiB |
| Replace in every row | 318 ms | 1.1 GiB |
| Extract by regex | 428 ms | 154 MiB |
| Unique (all rows distinct) | 676 ms | 154 MiB |

The window shows the file 0.5 s after the process starts and uses no CPU while idle.

The same file on Windows 10 in a virtual machine (20 threads, no GPU): load 0.46 s and
1.04x the file, Filter 59 ms, Sort 208 ms, Diff 749 ms, Replace 526 ms, Extract 653 ms,
Unique 966 ms, with the same memory. The window is there after 0.5 s as well; without a
GPU the first frames cost a few seconds of CPU on all cores and a key takes 0.1-0.2 s to
show (Direct3D 12 on the software adapter; Vulkan and OpenGL are not to be had there).

```sh
cargo run --release -p logexplorer-core --example bench -- big.log --generate 1024
```

## Tests

```sh
cargo test --workspace                                                      # unit, regression and headless UI tests
cargo test -p logexplorer --test smoke -- --ignored                         # smoke tests, Linux
cargo test -p logexplorer --test smoke -- --ignored --test-threads=1        # smoke tests, Windows
```

- **Unit tests** (`core/src/**`, `app/src/clipboard.rs`): every operation, loading and
  encodings, typed values, collation, selection, find, profiles.
- **Engine regressions** (`core/tests/regression.rs`): each pipeline stage is checked against
  the expected text, source IDs and original row IDs in `core/tests/fixtures/regression.json`.
  The same fixture records expected types, numbers and durations for representative input.
- **Headless UI** (`app/tests/headless.rs`, egui_kittest): the whole window without a
  display or GPU, driven through its accessibility tree, the keyboard and the pointer -
  the counterpart of the Avalonia headless tests.
- **Smoke** (`app/tests/smoke/`): the built binary as its own process, with the real
  keyboard and clipboard, down to a clean exit and the files it leaves - the counterpart
  of `search.smoke`. The scenarios are shared; `linux.rs` drives a private X server (Xvfb,
  openbox, xdotool, xclip), `windows.rs` the desktop of the session (SendInput, UI Automation).
  On Windows they take the foreground and type real keys - leave the machine alone while
  they run; a key is sent only while the window of the app is in front.

`.github/workflows/logexplorer.yml` runs these checks on pull requests that touch this
folder, on manual dispatch, and during each release.

## File Search Manager

File Search Manager opens Log Explorer with L, T/F3 and Show in files.
`search/search.csproj` copies the Cargo release binary to `LogExplorer/` next to the
app, and `search/NativeLogExplorer.cs` starts it with the selected files. Pressing F5
in VS Code passes `-p:BuildLogExplorer=true`, so Cargo builds the binary before .NET
copies it. A build without a Log Explorer binary opens text in the editor. Release
packaging enables `-p:RequireLogExplorer=true` to require the binary and license
notices. File Search Manager asks before loading files whose total size exceeds half
the available memory. `LOpensTheFileInTheNativeLogExplorer` in the Windows and Linux
smoke tests covers the hand-off.

Started by an elevated File Search Manager the program is elevated too, and can read what
the app shows an administrator. `A`, `L` (or `A`, `T`) starts it elevated from an
unelevated app as well: through the elevated helper or the UAC question on Windows, with
the user's profiles, and through pkexec on Linux, where root keeps profiles of its own.
`L` alone does the same when File Search Manager may not read one of the files (tried
off the UI thread, local files only: on a network share an administrator is the same
user). An elevated program on Windows has no mapped (`net use`) or `subst` drives of
the user's logon session, so their files go by the share or the folder behind the
letter. The command line is then one string. A long list of files still comes through
`--files0-from`: from the standard input, which pkexec passes on, and on Windows - where
neither the helper nor the UAC question gives the program one - from a named pipe of File
Search Manager (`\\.\pipe\WinSearch-…`), read like a file. Windows lets no program of lower rights - Explorer - use
the drop target of such a window, so an elevated program takes dropped files the older
way (`app/src/elevated_drop.rs`: `WM_DROPFILES`, let through the border between the
rights); only the "drop here" picture is missing then.

## Releases

`release.yml` builds the program for both systems before the app is published, and no
package is made without it:

- **Windows:** `tools/Build-Installer.ps1` runs `cargo build --release` and the installer
  takes `LogExplorer\logexplorer.exe` with the app folder. The C runtime is linked in
  (`.cargo/config.toml`), so no Visual C++ redistributable is needed. The program is not
  signed, like the rest of the installer's files.
- **Linux:** built in an Ubuntu 20.04 container, so that it needs no newer glibc than
  2.31 (the workflow checks the symbols it asks for); the portable archive and the .rpm
  and .deb carry `LogExplorer/logexplorer`, and the packages recommend the libraries it
  loads at run time (xkbcommon, Xcursor, Xi, EGL).
- **Licenses:** `node tools/third-party-notices.cjs` writes
  `licenses/logexplorer-THIRD-PARTY-NOTICES.txt` from the crates of that very build - each
  with its version, license and project, and the license texts they ship. A crate under a
  license that is not in the script's accepted list, or without a text, fails the release.
- **Tests:** the workflow of this folder runs beside the build of the packages, and
  nothing is published unless it passes.

## Text processing

- Columns count Unicode characters.
- Text sorts by punctuation, digits and letters with case and accents folded.
- Date parsing accepts supported date formats with valid calendar dates and times.
- Extract reports a missing capture group as soon as it compiles the pattern.
- Click selection recognizes identifiers from any alphabet.
- File view navigation shows one movement per frame. Separate presses keep their order
  and excess repeats from holding a key are discarded.

## Windows

Built, run and tested on Windows 10 (stable MSVC toolchain): the unit, regression and headless
tests, the smoke tests of this package and of File Search Manager with the program beside
it. Checked by eye: no console window, the title, the toolbar symbols, Japanese through
the system fonts, the UI in the display language of Windows, profiles under
`%APPDATA%\LogExplorer\config`, paths longer than 260 characters.

- **Esc** or a click on the line numbers clears the selection in the file view.
  **Enter** or **+** includes the selection, and **-** excludes it.
- **Not checked by hand:** the system file dialogs (`▤`, Download view), a file copied in
  Explorer itself (the smoke test puts the same file list on the clipboard through
  `arboard`), sharpness on a scaled display, a real GPU.
- **Not run yet:** the release workflow with this program in it (the Linux build in its
  container, the packages, the installer) and the `smoke-windows` job on a runner - they
  pass here, on a desktop without a GPU, or were only written.
