# Finishing the native Log Explorer on Windows

Instructions for Claude working in this repository **on a Windows machine**. Everything in
`logexplorer/` was written and run on Linux only. For Windows it is type-checked and linted
(`cargo clippy --target x86_64-pc-windows-msvc --all-targets -- -D warnings`), nothing more:
no binary was ever linked or started there. Your job is to make that true on Windows too and
to verify the parts that only exist there. Read `logexplorer/README.md` first.

Work through the steps in order and stop to ask the user where a step says so. Do not
commit or push unless the user asks. When you are done, replace this file's claims in
`README.md` ("Not done yet") with what is now true, and delete this file.

## 0. What exists

- `logexplorer/core` - the engine. Pure Rust, nothing platform specific except reading
  local time zones through `chrono`. Its tests must pass unchanged.
- `logexplorer/app` - the egui/eframe window. Windows-only code paths:
  - `src/clipboard.rs`: on Windows the app turns Ctrl+C/X/V (and Ctrl+Insert, Shift+Insert,
    Shift+Delete) into cut/copy/paste itself (`OWN_SHORTCUTS`, `NativeOptions::clipboard_shortcuts
    = false` in `main.rs`), because Explorer puts copied files on the clipboard as a file
    list without text and the toolkit reports nothing for such a paste. `SystemClipboard`
    reads the file list and the text through `arboard`.
  - `src/main.rs`: `windows_subsystem = "windows"` in release builds (no console).
  - `src/app.rs` `system_language()`: the UI language from `sys-locale`.
  - `src/storage.rs` `default_dir()`: profiles under the user's config folder (`directories`).
- `app/tests/smoke/windows.rs` - the Windows driver of the smoke tests: finds the window of
  the started process, takes the foreground the way `search.smoke/ForegroundWindow.cs` does,
  sends keys with `SendInput`, reads and sets the clipboard with `arboard`, and finds
  controls through UI Automation (`uiautomation` crate). **Never run.**
- `search/NativeLogExplorer.cs` - File Search Manager starts `LogExplorer\logexplorer.exe`
  next to itself instead of the HTML page when that file exists. `search/search.csproj`
  copies `logexplorer\target\release\logexplorer.exe` there when it has been built.
- `.github/workflows/logexplorer.yml` - the `smoke-windows` job has `continue-on-error: true`
  until step 3 is done.

## 1. Build and the tests that need no window

Needs: rustup with the stable MSVC toolchain (and the Visual Studio C++ build tools it asks
for), .NET SDK 10 for the C# side, Node 22+ only if you regenerate fixtures.

```powershell
cd logexplorer
cargo build --release -p logexplorer
cargo test --workspace
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
```

Expected: 78 unit tests and 2 parity tests in `logexplorer-core`, 2 unit tests and 22
headless UI tests in `logexplorer`, all green; the smoke tests are listed as ignored.
A failure here is a real bug - most likely a path or line-ending assumption in a test.
Fix the cause, not the expectation, and keep the Linux behaviour: you cannot run Linux here,
so do not touch `app/tests/smoke/linux.rs` and keep `cfg` splits narrow.

Things worth a second look because Linux could not exercise them:
- `clipboard::file_of` and its test build `file:///C:/...` addresses for Windows.
- `load::read_input` and `App::open_paths` with drive letters, UNC paths, and paths longer
  than 260 characters.

## 2. The application by hand

Start `target\release\logexplorer.exe` with a log of your own and check, noting anything
that is off:

1. It starts without a console window and shows the file; the title is `<file> - Log Explorer`.
   (If the window stays black or the process exits: eframe uses wgpu. Try `WGPU_BACKEND=dx12`,
   `vulkan`, `gl` to find which backend fails, and report it to the user before changing
   the renderer.)
2. Text is sharp at the display's scale; the toolbar symbols (`⊕ ⇄ ⇅ ≠ ▾ ▤ ▣ ≡ ×`) are
   drawn, not boxes; `--lang ja` shows Japanese (system font fallback).
3. Without `--lang` the UI follows the Windows display language.
4. Drag files from Explorer into the window; drop several and answer the question.
5. `▤` opens the system file dialog; `≡ → Download view` saves through it.
6. **Clipboard** (the Windows-only path):
   - copy a file in Explorer, Ctrl+V in the file view: the file opens;
   - copy several files: the "how to combine" question appears;
   - copy text elsewhere, Ctrl+V in the file view: it becomes the "Clipboard" source;
   - in a field of an operation and in the find box: Ctrl+C, Ctrl+X, Ctrl+V, Shift+Insert,
     Ctrl+Insert work as in any Windows text box, and Ctrl+V of a copied *file* there does nothing;
   - Ctrl+C in the file view copies the selection (paste it into Notepad: lines end properly).
7. A file of 1 GiB or more: `cargo run --release -p logexplorer-core --example bench -- big.log --generate 1024`
   prints timings; then open `big.log` in the app, add a filter, scroll with the wheel,
   the scrollbar and Page Down. Tell the user the numbers next to the Linux ones in `README.md`.
8. Profiles survive a restart (they are in `%APPDATA%\LogExplorer\config`).

Fix what is broken; add a headless test in `app/tests/headless.rs` for every bug that can
be shown without a real window.

## 3. The smoke tests

```powershell
cargo test -p logexplorer --test smoke -- --ignored --test-threads=1
```

They take the foreground and type real keys: leave the machine alone while they run. The
scenarios are in `app/tests/smoke/main.rs` and are shared with Linux, where all pass - so a
failure is most likely in `windows.rs`. Likely trouble spots:

- **Keys do not arrive.** `key_input` sends virtual keys with scan code 0. If winit ignores
  them, fill `wScan` with `MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)` and set
  `KEYEVENTF_EXTENDEDKEY` for Home, End, Delete and the arrows.
- **Typed text.** `type_text` sends `KEYEVENTF_UNICODE` units; egui must receive them as text.
- **Foreground.** `activate` mirrors `search.smoke/ForegroundWindow.cs`. If the foreground
  cannot be taken the test skips (fails when `CI` is set) - that is intended.
- **Alt+F4** must end the process with exit code 0.
- **UI Automation** (`ui_automation_finds_and_invokes_the_controls_by_name`): the test
  expects a control named `⊕ Filter` that can be invoked, and afterwards an element named
  `Showing: after #1 Filter`. Look at the real tree with Accessibility Insights or
  `inspect.exe` and adjust how `element_named` searches (not the app) if the names are
  there but not found; if they are *not* there, AccessKit is not active - find out why.
- **Explorer's clipboard format.** `set_clipboard_files` uses `arboard`'s file list, which
  should be the `CF_HDROP` Explorer writes. Confirm with a real copy in Explorer (step 2.6).

When all pass locally, remove `continue-on-error: true` from the `smoke-windows` job in
`.github/workflows/logexplorer.yml`, and ask the user to run the workflow: the runner has no
GPU and its own desktop rules, so green on your machine is not yet green there.

## 4. File Search Manager

```powershell
cd logexplorer; cargo build --release -p logexplorer; cd ..
dotnet test search.tests -c Release
dotnet build search -c Release
```

1. `search.tests` must pass, including `NativeLogExplorerTests` (written against Linux paths).
2. The build output has `LogExplorer\logexplorer.exe` next to `FileSearchManager.exe`.
3. In the running app: `L` on one file, `L` on several, `T`/`F3` on a text file, and
   "Show in files" (`G`) after a content search open the **native** window - the last one
   with the files appended and the filter in place, case respected. Try a file name with
   spaces, with a quote-like character, and one starting with `--`.
4. Delete `LogExplorer\logexplorer.exe` from the output: the same keys open the HTML page
   in WebView2 as before.
5. Above half of the machine's memory in selected files the question `LogExplorerMemory`
   is asked (in the app's language); below it nothing is asked. The HTML fallback still asks
   from 100 MB with `LogExplorerLarge`.
6. When File Search Manager runs elevated, the native Log Explorer inherits that. It is
   the same as the in-process web view was, but drag and drop from Explorer into it will not
   work. Tell the user; do not build a de-elevation without asking.
7. `search.smoke` on Windows: `WindowsSmokeTests.TheLogExplorerShowsTheFileInItsWebView`
   expects the web view. With the native program in the published folder it will fail.
   Make it apply only to a build without `LogExplorer\logexplorer.exe`, and add the Windows
   counterpart of `LinuxSmokeTests.LOpensTheFileInTheNativeLogExplorer` (window titled
   `<file> - Log Explorer` of a child process, the file's rows through Ctrl+A, Ctrl+C).

## 5. Shipping it - ask the user first

Releases do **not** contain the native program yet: `release.yml` and the installers were
left alone on purpose, because each of these is the user's decision. Present them and do
only what the user chooses:

- Build `logexplorer` in `release.yml` (Windows and Linux jobs) before the app is published,
  so that `search.csproj` picks it up; `tools/Build-Installer.ps1` and `installer/setup.iss`
  for the installer, `installer/linux/package` for the Linux packages.
- Licences of the Rust crates in `THIRD-PARTY-NOTICES.md` (and
  `tools/check-third-party-notices.py`, which today knows only NuGet). `cargo about` or
  `cargo license` can produce the list.
- Whether the HTML page stays as a fallback in the installer once the native program ships.
- Code signing of `logexplorer.exe`, if the installer's files are signed.

## Conventions here

- Comments say what a thing is for or why it is so, in plain English; tests are named as
  sentences that state the behaviour. Match the surrounding code.
- `logexplorer/rustfmt.toml` sets the width; run `cargo fmt --all`.
- Behaviour follows the HTML app. A deliberate difference goes into `README.md` under
  "Deliberate differences"; `core/tests/parity.rs` holds the engine to the JavaScript
  operations and its fixtures are only ever regenerated from `external/singleHtmlApps`
  (`node tools/parity-fixtures.cjs`), never edited.
- Report plainly at the end: what passed, what you changed and why, what is still open.
