# Log Explorer (Rust)

The Log explorer of File Search Manager, a native rewrite of
`external/singleHtmlApps/LogExplorer.html` that replaced that page: a viewer for big logs with a
staged Filter / Replace / Sort / Diff / Extract / Unique / Join pipeline, smart click
selection, find, multi-file sources with per-file tints, and profiles. It runs on Windows
and Linux (egui/eframe) and is meant for files of gigabytes, as long as they fit in memory.

```sh
cargo run --release -p logexplorer -- [--combine append|zip] [--filter TEXT] [--case-sensitive]
                                      [--lang LANG] [--data-dir DIR] [--files0-from LIST] [FILE...]
```

The options are what the embedding API of the HTML app took (`window.logExplorer.open`):
several files without asking how to combine them, and a first include filter.
`--files0-from` reads more files from LIST (`-` is standard input), each name ended by a NUL
byte: File Search Manager hands over a list longer than a Windows command line (32 767
characters) this way. Files can also be dropped into the window or pasted after copying them
in a file manager. The viewer opens JSON as text. Import profile bundles through
`≡` → Upload profiles. The bundles are interchangeable with the HTML app, which also
provides the UI texts and their ten translations (`tools/extract-translations.cjs`).
The close button beside Open file (`Ctrl+W`) unloads all files in the view and keeps the
current pipeline.

## Layout

| Path | What |
| --- | --- |
| `core/` | `logexplorer-core`: the engine, no UI. Loading, stages, the operations, the pipeline cache, selection, find, profiles, texts. |
| `app/` | `logexplorer`: the egui application. `app.rs` is state and behaviour, `viewer.rs` the virtual file view, `panels.rs` everything around it. |
| `tools/` | Node scripts that regenerate the translations and the parity fixtures from the HTML app, and write the notices of the crates. |

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
- Click selection looks for brackets, tags and quotes in a window of rows around the
  click instead of scanning the whole stage.

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
cargo test --workspace                                                      # unit, parity and headless UI tests
cargo test -p logexplorer --test smoke -- --ignored                         # smoke tests, Linux
cargo test -p logexplorer --test smoke -- --ignored --test-threads=1        # smoke tests, Windows
```

- **Unit tests** (`core/src/**`, `app/src/clipboard.rs`): every operation, loading and
  encodings, typed values, collation, selection, find, profiles.
- **Parity** (`core/tests/parity.rs`): pipelines run through the *JavaScript* operations of
  the HTML app (`tools/parity-fixtures.cjs` writes `fixtures/parity.json`); the Rust engine
  must give the same rows, from the same sources and original rows.
- **Headless UI** (`app/tests/headless.rs`, egui_kittest): the whole window without a
  display or GPU, driven through its accessibility tree, the keyboard and the pointer -
  the counterpart of the Avalonia headless tests.
- **Smoke** (`app/tests/smoke/`): the built binary as its own process, with the real
  keyboard and clipboard, down to a clean exit and the files it leaves - the counterpart
  of `search.smoke`. The scenarios are shared; `linux.rs` drives a private X server (Xvfb,
  openbox, xdotool, xclip), `windows.rs` the desktop of the session (SendInput, UI Automation).
  On Windows they take the foreground and type real keys - leave the machine alone while
  they run; a key is sent only while the window of the app is in front.

`.github/workflows/logexplorer.yml` runs all of it, and fails when the translations or the
parity fixtures no longer match the pinned HTML app. After the HTML app changes:
`node tools/extract-translations.cjs` and `node tools/parity-fixtures.cjs`, then the tests.
The workflow runs for a pull request that touches this folder, by hand, and in every
release beside the build of the packages - not on a push to main.

## File Search Manager

This program is the Log explorer of File Search Manager (L, T/F3, Show in files): the HTML
page is no longer shipped or opened. A release build of this package is copied by
`search/search.csproj` to `LogExplorer/` next to the app, and `search/NativeLogExplorer.cs`
starts it with the files. A build made without Rust has no Log explorer - L is not offered
and text opens in the editor - and `-p:RequireLogExplorer=true` makes that an error, as the
packages are built. The app asks before loading only when the files would not fit in half
of the memory. `LOpensTheFileInTheNativeLogExplorer` of `LinuxSmokeTests` and
`WindowsSmokeTests` in `search.smoke` covers the hand-over.

Started by an elevated File Search Manager the program is elevated too, and can read what
the app shows an administrator. Windows lets no program of lower rights - Explorer - use
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

## Deliberate differences from the HTML app

- A column is a Unicode character, not a UTF-16 unit (they differ only beyond the BMP).
- Text is ordered by a root collation implemented here (punctuation, digits, letters
  regardless of case and accents) rather than by the locale of the browser.
- Only what a sane parser takes for a date is a date: `Date.parse` of a browser also
  accepts texts like `1 2` or `01:60`.
- A capture group that the pattern does not have is reported at once, not at the first match.
- Identifiers of any alphabet are selected by a click, not only ASCII ones.
- A stage that follows several edits keeps its earlier stages (the HTML app recomputes
  the whole pipeline after every change).
- File view navigation shows one movement per frame. Separate presses keep their order
  and excess repeats from holding a key are discarded.

## Windows

Built, run and tested on Windows 10 (stable MSVC toolchain): the unit, parity and headless
tests, the smoke tests of this package and of File Search Manager with the program beside
it. Checked by eye: no console window, the title, the toolbar symbols, Japanese through
the system fonts, the UI in the display language of Windows, profiles under
`%APPDATA%\LogExplorer\config`, paths longer than 260 characters.

- **Ctrl+Esc** (clear the selection) never reaches a program on Windows - it opens the
  Start menu, in the HTML app too. A click on the line numbers clears the selection.
- **Not checked by hand:** the system file dialogs (`▤`, Download view), a file copied in
  Explorer itself (the smoke test puts the same file list on the clipboard through
  `arboard`), sharpness on a scaled display, a real GPU.
- **Not run yet:** the release workflow with this program in it (the Linux build in its
  container, the packages, the installer) and the `smoke-windows` job on a runner - they
  pass here, on a desktop without a GPU, or were only written.
