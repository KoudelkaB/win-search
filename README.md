# File Search Manager

File Search Manager is a fast Windows desktop search and file-management application. It indexes local drives, filters millions of file-system entries as you type, searches file contents, and performs common file operations directly from the result list.

The WPF desktop app can read the NTFS Master File Table through an optional Windows service or an elevated helper. When neither is available, it falls back to a normal directory walk.

## Features

- Fast NTFS indexing with live file-system updates and safe fallback scanning.
- Filtering by name, immediate parent folder, full path, or exact directory.
- UTF-8, UTF-16, case-insensitive, and hexadecimal content search.
- Sortable Name, Size, Changed, Folder, and content-result columns.
- Extended mouse selection plus keyboard-driven selection commands.
- Drag files and directories to other applications from the Name or Folder column.
- Drop files onto selected directories to copy, move, create symbolic links, or create hard links.
- Drop files onto executables and scripts to pass them as arguments.
- Pin named filters for one-click switching between reusable result sets.
- Keep folders, archives, and executables in a persistent target basket and apply one source selection to several targets.
- Export and import pinned filters and the complete target basket as one JSON settings file.
- Standard `Ctrl+C`, `Ctrl+X`, and `Ctrl+V` shell-compatible clipboard operations.
- Collision handling with overwrite, skip, rename, and apply-to-all choices.
- Transfer progress, cancellation, and normal deletion through the Recycle Bin.
- Dynamic **Open with** menu for installed compatible applications, including diff tools, 7-Zip, browsers, editors, and Ghostscript-family viewers.
- ZIP and 7z creation, archive extraction, and browsing of supported archive contents.
- Persistent filter/search history and window layout under `%LOCALAPPDATA%\win-search`.

## Install

Download the latest installer from [GitHub Releases](https://github.com/KoudelkaB/win-search/releases).

The installer is named `FileSearchManager-Setup-<version>.exe`. It installs `File Search Manager.exe` under `Program Files`, creates Start Menu shortcuts, and optionally installs the read-only `WinSearchService` for prompt-free NTFS indexing.

After the package is accepted into the Windows Package Manager community repository:

```powershell
winget install BohdanKoudelka.FileSearchManager
```

## Quick Start

1. Start **File Search Manager**.
2. Approve the optional startup elevation prompt for immediate MFT access, or decline it to use the installed service or folder-walk fallback.
3. Type in **Filter** to narrow the result list.
4. Type in **Search** and press `Enter` to search contents within the filtered files.
5. Select results with the mouse or keyboard.
6. Use the right-click menu, drag-and-drop, standard clipboard shortcuts, or commands shown in the **Hints** panel.

See [docs/HELP.md](docs/HELP.md) for filter syntax and detailed controls.

## Build

Prerequisites:

- Windows
- .NET 10 SDK
- Inno Setup 6 for installer builds

Build:

```powershell
dotnet build search/search.sln
```

Publish self-contained binaries:

```powershell
dotnet publish search/search.csproj -c Release -r win-x64 --self-contained true -o publish/app
dotnet publish search.service/search.service.csproj -c Release -r win-x64 --self-contained true -o publish/service
```

The main publish output is `File Search Manager.exe`.

Build the installer:

```powershell
iscc installer/setup.iss
```

Release versions come from `v*` git tags. The release workflow publishes the installer and SHA-256 checksum.

## winget Publishing

The winget package identifier is:

```text
BohdanKoudelka.FileSearchManager
```

Generate manifests with:

```powershell
.\tools\New-WingetManifest.ps1 `
  -Version 0.1.0 `
  -InstallerUrl https://github.com/KoudelkaB/win-search/releases/download/v0.1.0/FileSearchManager-Setup-0.1.0.exe `
  -InstallerPath .\installer\Output\FileSearchManager-Setup-0.1.0.exe
```

See [docs/WINGET.md](docs/WINGET.md) for the complete release checklist.

## Compatibility Notes

The repository name, internal `search` namespace, optional service name `WinSearchService`, installer AppId, and `%LOCALAPPDATA%\win-search` data directory remain unchanged so existing installations and settings continue to upgrade cleanly.

## License

File Search Manager is licensed under the [MIT License](LICENSE). Third-party dependency notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
