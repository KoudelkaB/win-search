# Win Search

Win Search is a fast Windows file search tool for people who prefer a keyboard-first workflow. It loads local drives, filters file and folder names as you type, searches inside matching files, and can perform common file operations directly from the result list.

The app is built as a WPF desktop application with an optional Windows service for prompt-free NTFS Master File Table indexing. Without the service, Win Search still works by using an elevated helper when allowed or by falling back to a normal folder walk.

## Features

- Fast indexing of NTFS drives through the MFT when the app is elevated, the broker is approved, or the optional service is installed.
- Zero-service fallback for non-NTFS drives and locked-down environments.
- Live filtering by file name, parent folder, path, or exact directory.
- Content search across the filtered result set with UTF-8, UTF-16, and space-separated HEX input.
- Keyboard-command panel for opening, copying, moving, deleting, renaming, zipping, unzipping, and selecting results.
- Result colors for content search status: green for found, red for not found, black for not searched, blue for folders.
- Persistent filter/search history and window layout under `%LOCALAPPDATA%\win-search`.

## Install

Download the latest installer from [GitHub Releases](https://github.com/KoudelkaB/win-search/releases).

The installer is named `WinSearch-Setup-<version>.exe`. It installs the app to `Program Files`, adds a Start Menu shortcut, and offers an optional background service for faster NTFS indexing without a UAC prompt.

After Win Search is accepted into the Windows Package Manager community repository, it will install with:

```powershell
winget install BohdanKoudelka.WinSearch
```

## Quick Start

1. Start **Win Search**.
2. If Windows asks for elevation, approve it for instant NTFS indexing or decline it to use service/folder-walk fallback.
3. Type in **Filter** to reduce the result list.
4. Type in **Search** and press `Enter` to search file contents inside the filtered results.
5. Focus the result list and press a command key. The right-side **Hints** panel shows the available next keys.

See [docs/HELP.md](docs/HELP.md) for filter syntax, content search details, mouse actions, and the command map.

## Build

Prerequisites:

- Windows
- .NET 10 SDK
- Inno Setup 6 for installer builds

Build the app:

```powershell
dotnet build search/search.sln
```

Publish self-contained binaries:

```powershell
dotnet publish search/search.csproj -c Release -r win-x64 --self-contained true -o publish/app
dotnet publish search.service/search.service.csproj -c Release -r win-x64 --self-contained true -o publish/service
```

Build the installer:

```powershell
iscc installer/setup.iss
```

Release versions come from git tags such as `v0.1.0`. The release workflow builds the installer and publishes a GitHub release when a `v*` tag is pushed.

## winget Publishing

The winget package identifier is:

```text
BohdanKoudelka.WinSearch
```

This repository includes a manifest generator:

```powershell
.\tools\New-WingetManifest.ps1 `
  -Version 0.1.0 `
  -InstallerUrl https://github.com/KoudelkaB/win-search/releases/download/v0.1.0/WinSearch-Setup-0.1.0.exe `
  -InstallerPath .\installer\Output\WinSearch-Setup-0.1.0.exe
```

The generated files are meant to be copied into a fork of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) and submitted as a pull request. See [docs/WINGET.md](docs/WINGET.md) for the release and submission checklist.

## License

Win Search is licensed under the [MIT License](LICENSE).

Third-party dependency notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
