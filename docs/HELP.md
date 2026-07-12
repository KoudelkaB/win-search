# File Search Manager Help

File Search Manager has two main fields:

- **Filter** narrows the file list by name, folder, path, or selected directories.
- **Search** searches file contents inside the currently filtered files.

The result list is designed for keyboard use. When the list has focus, the **Hints** panel shows commands that are currently available for the selection.

## Indexing and Elevation

On startup, File Search Manager loads ready drives and watches for file-system changes.

For NTFS drives, the fastest path is reading the NTFS Master File Table. File Search Manager can do that in one of these ways:

- Optional Windows service: no prompt after installation.
- Elevated broker: approve the startup UAC prompt once per app run.
- Direct elevated app: run File Search Manager as administrator.

If none of those are available, File Search Manager falls back to walking folders. The app still works, but the initial load may be slower.

The optional service is read-only for indexing. It exposes MFT data to the desktop app and is named `WinSearchService`.

## Filter Syntax

Filter terms are separated by spaces. Use quotes around terms or paths that contain spaces.

Name matching:

- `report` matches names containing `report`.
- `:report` matches names starting with `report`.
- `report:` matches names ending with `report`.
- `:report:` matches the exact name `report`.
- `pdf|docx` matches either `pdf` or `docx`.
- Multiple terms are ANDed, so `report pdf:` finds names matching both terms.

Folder matching:

- `src\` matches items whose immediate parent folder name matches `src`.
- `src\\` matches items with `src` anywhere in the full path.
- `"C:\Work"` matches items directly inside `C:\Work`.
- `"C:\Work\\"` matches items recursively under `C:\Work`.

History:

- `Ctrl+Left` and `Ctrl+Right` move through filter history.
- `Down` opens suggestions.
- `Del` removes the selected suggestion.
- Hold `Ctrl` while opening suggestions to use most-recent history instead of most-used history.

### Pinned Filters

- Click **Pin…** to add an editable tab to the pinned-filter row. Type its name in place and press `Enter`; `Esc` cancels it.
- Click a named filter to restore its saved expression. The active named filter is highlighted.
- Right-click a named filter to update it from the current expression, rename it in place, or unpin it.
- Pinned filters are restored when the application starts.

**Export…** saves all pinned filters and target-basket entries to one JSON settings file. **Import…** validates a settings file and, after confirmation, replaces the current pinned filters and targets with its contents.

## Content Search

Type a term in **Search** and press `Enter`.

Encoding options:

- `UTF-8` searches text encoded as UTF-8.
- `UTF-16` searches text encoded as UTF-16 little-endian.
- `HEX` searches bytes written as space-separated hex, for example `48 65 6C 6C 6F`.

The **Case insensitive** checkbox applies to text searches.

Result colors after a content search:

- Green: the content was found.
- Red: the content was not found.
- Black: the item was not searched or the search was cleared.
- Blue: folder.

Typing anything other than `Enter` in the search field clears the current content-search result state.

## Mouse Actions

- Click, Ctrl+click, or Shift+click results to select individual items or ranges.
- Double-click a file or folder name to filter into that item.
- Double-click the folder column to open File Explorer with the item selected.
- Ctrl + right-click a result to open it with the default Windows action.
- Click a column header to sort by that column.
- Right-click selected items for Open, dynamic Open with, clipboard, rename, archive, and deletion actions.
- **Add to target basket** follows the column that was right-clicked: Name adds the selected items, while Folder adds their parent folders. It is hidden when the corresponding paths are not supported targets or are already in the basket.
- Drag from the Name column to send selected files or directories to another application.
- Drag from the Folder column to send the selected items' parent folders.
- Drop files onto a directory in the Name column or a parent path in the Folder column. Choose copy, move, symbolic link, or hard link.
- Drop files onto an executable Name cell to launch it with the dropped paths as arguments.
- File drops follow Explorer defaults: one folder on the same volume defaults to move, another volume defaults to copy. Hold `Ctrl` for copy, `Shift` for move, or `Alt` for a symbolic link. Multiple destinations default to copy.

## Context Menu

The context menu adapts to the current selection:

- **Open with** lists only installed applications compatible with the selection. A configured diff tool is listed first for exactly two files or directories.
- **7-Zip** is shown when `7zFM.exe` is installed.
- **Zip** creates a ZIP archive.
- **Create 7z archive** is shown only when `7z.exe` or `7zz.exe` is installed.
- **Unzip** is shown for supported archive selections.
- **Move to Recycle Bin** performs recoverable deletion.

Directory drops and `Ctrl+V` show an action chooser. Existing-name conflicts offer overwrite, skip, automatic rename, and apply-to-all behavior. Transfers show item progress and can be cancelled between top-level operations.

## Target Basket

The target basket at the bottom keeps reusable drop targets:

- **+ Name** adds selected Name-column items. Folders, supported archives, and executables have target-specific behavior.
- **+ Folder** adds the selected items' parent folders.
- Dropping paths on **Drop here to add targets** adds exactly the paths produced by the drag. A drag started in the Folder column therefore adds parent folders; a drag started in the Name column adds the named items.
- Dropping on empty space after the target chips also adds targets. Dropping directly on a chip uses that target instead.
- Dropping sources onto one target uses only that target. Drop onto the separate green **Use all (n)** zone to use every available target after showing a summary when needed.
- Folder targets show the copy, move, symbolic-link, and hard-link chooser.
- Archive targets add the sources to the archive. Updating non-ZIP formats requires an installed `7z.exe` or `7zz.exe`.
- Executable targets are started with the source paths as arguments and require confirmation.
- **Paste to all…** uses file paths currently on the clipboard with every target.
- Right-click a target to filter to it, open it, or remove it. **Clear** removes all targets.

The basket is saved automatically between application runs. Missing targets remain in the saved configuration but are skipped until they become available again.

Target-bar access keys are `Alt+N` (add Name), `Alt+F` (add Folder), `Alt+A` (paste to all), and `Alt+C` (clear). `Ctrl+Shift+V` also invokes **Paste to all…**. Filter controls use `Alt+P` or `Ctrl+D` to pin, `Alt+I` to import, and `Alt+E` to export.

## Keyboard Commands

Focus the result list and press a key shown in the **Hints** panel. Some commands continue while keys are held and finish when all keys are released.

Common commands:

- `Enter`: filter into selected folders.
- `Delete`: move selected items to the Recycle Bin after confirmation.
- `Shift+Delete`: permanently delete selected items without confirmation.
- `Ctrl+C`: standard shell-compatible copy.
- `Ctrl+X`: standard shell-compatible cut.
- `Ctrl+V`: paste after choosing copy, move, symbolic link, or hard link.
- `C`: copy selected items to the clipboard.
- `X`: cut selected items to the clipboard.
- `V`: paste clipboard files into the selected folders or parent folders.
- `O`: open selected items in another app.
- `A`: open selected items as administrator.
- `F2`: rename one physical file or folder directly in its Name cell. With multiple items, use one of the transformation commands below.
- `F3`: view selected items.
- `F4`: edit selected items.
- `N`: copy selected names.
- `P`: copy selected full paths.
- `F`: copy selected folder paths.
- `M`: show an inline bar for creating a directory in the selected folders.
- `S`: selection commands.
- `U`: unzip selected archives.
- `Z`: zip selected items.
- `F12`: refresh from NTFS.
- `Right Shift`: move focus back to the filter field.

Open targets after `O` or `A`:

- `B`: File Explorer.
- `W`: default detected web browser.
- `C`: Chrome.
- `F`: Firefox.
- `E`: Edge.
- `T`: text viewer.
- `D`: Visual Studio Code.
- `V`: Visual Studio.
- `G`: Ghostscript.
- `P`: GhostPCL.
- `X`: GhostXPS.
- `R`: viewer detected for PRN content.
- `S`, then `P`: PowerShell.
- `S` alone: Command Prompt.

Selection commands after `S`:

- `A`: select or unselect all.
- `D`: select directories.
- `F`: select files.
- `I`: invert selection.
- `G`: select green rows.
- `R`: select red rows.
- `B`: select black rows.

Rename/change commands after `F2`:

- `N`: change name.
- `E`: change extension.
- `.`: add extension.
- `Delete`: delete text from the name.
- `F`: add prefix.
- `L`: add postfix.
- `Insert`: insert text at an index.
- `R`: replace text.
- `C`: change creation time.
- `W`: change last write time.
- `A`: change last access time.
- Add `O` first to overwrite existing targets when supported.

## Data and Troubleshooting

User state is stored under:

```text
%LOCALAPPDATA%\win-search
```

If NTFS loading is slow, install the optional service from the installer or approve the startup UAC prompt. The status bar reports whether each drive used the service, direct access, admin helper, or folder walk.

If a mapped or external drive is unavailable, File Search Manager skips it after a short readiness check so startup does not stall on unreachable storage.
