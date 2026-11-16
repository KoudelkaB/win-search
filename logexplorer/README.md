# Log Explorer (Rust)

A native rewrite of `external/singleHtmlApps/LogExplorer.html`: a viewer for big logs with a
staged Filter / Replace / Sort / Diff / Extract / Unique / Join pipeline, smart click
selection, find, multi-file sources with per-file tints, and profiles. It runs on Windows
and Linux (egui/eframe) and is meant for files of gigabytes, as long as they fit in memory.

```sh
cargo run --release -p logexplorer -- [--combine append|zip] [--filter TEXT] [--case-sensitive]
                                      [--lang LANG] [--data-dir DIR] [FILE...]
```

The options are what the embedding API of the HTML app took (`window.logExplorer.open`):
several files without asking how to combine them, and a first include filter. Files can
also be dropped into the window or pasted after copying them in a file manager. Profile
bundles (`≡` menu → Download / Upload profiles) are interchangeable with the HTML app; the UI
texts and their ten translations are taken from it (`tools/extract-translations.cjs`).

## Layout

| Path | What |
| --- | --- |
| `core/` | `logexplorer-core`: the engine, no UI. Loading, stages, the operations, the pipeline cache, selection, find, profiles, texts. |
| `app/` | `logexplorer`: the egui application. `app.rs` is state and behaviour, `viewer.rs` the virtual file view, `panels.rs` everything around it. |
| `tools/` | Node scripts that regenerate the translations and the parity fixtures from the HTML app. |

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

`.github/workflows/logexplorer.yml` runs all of it, and fails when the translations or the
parity fixtures no longer match the pinned HTML app. After the HTML app changes:
`node tools/extract-translations.cjs` and `node tools/parity-fixtures.cjs`, then the tests.

## File Search Manager

A release build of this package is copied by `search/search.csproj` to `LogExplorer/` next
to the app, and File Search Manager then opens Log explorer (L, T/F3, Show in files) in it
instead of the HTML page (`search/NativeLogExplorer.cs`); a build without it falls back to
the page. It asks before loading only when the files would not fit in half of the memory.
`LinuxSmokeTests.LOpensTheFileInTheNativeLogExplorer` in `search.smoke` covers the hand-over.

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

## Not done yet

- **Windows has never run it.** The application and the Windows smoke driver are type-checked
  and linted for `x86_64-pc-windows-msvc` only. `WINDOWS.md` is the list of what to run,
  verify and finish there (the smoke job of the workflow may fail until then).
- **Releases do not ship it.** The release workflow, the installers and the third-party
  notices are untouched: building the program there and listing the licences of its crates
  are decisions still to be made (`WINDOWS.md`, "Shipping it").
