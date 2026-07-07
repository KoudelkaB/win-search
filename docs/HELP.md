# Win Search Help

Win Search has two main fields:

- **Filter** narrows the file list by name, folder, path, or selected directories.
- **Search** searches file contents inside the currently filtered files.

The result list is designed for keyboard use. When the list has focus, the **Hints** panel shows commands that are currently available for the selection.

## Indexing and Elevation

On startup, Win Search loads ready drives and watches for file-system changes.

For NTFS drives, the fastest path is reading the NTFS Master File Table. Win Search can do that in one of these ways:

- Optional Windows service: no prompt after installation.
- Elevated broker: approve the startup UAC prompt once per app run.
- Direct elevated app: run Win Search as administrator.

If none of those are available, Win Search falls back to walking folders. The app still works, but the initial load may be slower.

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

- Double-click a file or folder name to filter into that item.
- Double-click the folder column to open File Explorer with the item selected.
- Ctrl + right-click a result to open it with the default Windows action.
- Click a column header to sort by that column.

## Keyboard Commands

Focus the result list and press a key shown in the **Hints** panel. Some commands continue while keys are held and finish when all keys are released.

Common commands:

- `Enter`: filter into selected folders.
- `Delete`: delete selected items.
- `Shift+Delete`: delete selected items without confirmation.
- `C`: copy selected items to the clipboard.
- `X`: cut selected items to the clipboard.
- `V`: paste clipboard files into the selected folders or parent folders.
- `O`: open selected items in another app.
- `A`: open selected items as administrator.
- `F2`: rename or change selected items.
- `F3`: view selected items.
- `F4`: edit selected items.
- `N`: copy selected names.
- `P`: copy selected full paths.
- `F`: copy selected folder paths.
- `M`: make a directory in selected folders.
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

If a mapped or external drive is unavailable, Win Search skips it after a short readiness check so startup does not stall on unreachable storage.
