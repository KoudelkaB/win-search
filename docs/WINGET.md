# Publishing File Search Manager to winget

This checklist is for publishing `BohdanKoudelka.FileSearchManager` to the Windows Package Manager community repository.

Microsoft's current manifest documentation recommends YAML manifests with required package metadata, installer URL, and SHA-256 hash. Multi-file manifests separate version, default locale, and installer data.

Useful references:

- Windows Package Manager manifest docs: https://learn.microsoft.com/windows/package-manager/package/manifest
- Community repository: https://github.com/microsoft/winget-pkgs
- Manifest schema docs: https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema

## Package Identity

- PackageIdentifier: `BohdanKoudelka.FileSearchManager`
- PackageName: `File Search Manager`
- Publisher: `Bohdan Koudelka`
- Moniker: `file-search-manager`
- InstallerType: `inno`
- Scope: `machine`
- Architecture: `x64`
- Installer filename: `FileSearchManager-Setup-<version>.exe`
- Release URL pattern: `https://github.com/KoudelkaB/win-search/releases/download/v<version>/FileSearchManager-Setup-<version>.exe`

The installer writes the same package name and publisher to Apps & Features, which helps winget correlate installs and upgrades.

## Locales

`tools\New-WingetManifest.ps1` emits `en-US` as the default locale plus one additional locale manifest per language in `tools\winget-locales.json` (cs-CZ, de-DE, fr-FR, es-ES, pl-PL, it-IT, pt-BR, ja-JP, ko-KR, zh-CN).

These only translate the catalog listing - what `winget show` and `winget search` display to a user whose system locale matches. They are unrelated to the languages the app UI and the installer wizard speak, which winget neither reads nor validates. Every field in an additional locale is optional and falls back to the default locale, so each file carries just the two descriptions.

Two things to keep in mind when editing them:

- Edit the translations in `winget-locales.json`, not in the script. The script stays pure ASCII on purpose: Windows PowerShell 5.1 decodes a BOM-less `.ps1` as ANSI, which would silently corrupt accented and CJK text.
- Manifests must be UTF-8 **without** a BOM. The script handles this; re-saving a generated file in an editor that adds a BOM will fail validation.

The installer manifest deliberately has no `InstallerLocale`. One Inno installer carries all the wizard languages, so it is locale-neutral; pinning it to `en-US` could make `winget install --locale cs-CZ` report no applicable installer even though that installer does speak Czech.

Because WinGet Releaser bases each update on the previous version's manifests, the locale files carry forward automatically. They only need touching when a description changes or a language is added.

## How Publishing Works

Releases are automated. Pushing a `v*` tag runs `.github/workflows/release.yml`, which builds the
installer, publishes the GitHub release, and then opens the manifest pull request against
`microsoft/winget-pkgs` via the [WinGet Releaser](https://github.com/vedantmgoyal9/winget-releaser)
action.

That action updates an **existing** package - it uses the previous version's manifests as its base.
So the first version has to be submitted by hand once (below); every version after that needs no
manual step beyond the tag push.

Prereleases (tags containing `-`, e.g. `v0.2.0-rc1`) skip the winget job.

## One-Time Setup

1. Fork `microsoft/winget-pkgs` to the `KoudelkaB` account and keep the fork.

2. Create a **classic** personal access token with the `public_repo` scope. Fine-grained tokens are
   not supported by the action.

3. Add it to this repository as the secret `WINGET_TOKEN`
   (Settings -> Secrets and variables -> Actions).

   Until this secret exists the winget job is skipped, so the release still succeeds without it.

4. Do the first manual submission described below.

## First Submission (one time only)

1. Tag the release and let the workflow publish `FileSearchManager-Setup-0.1.0.exe` and
   `SHA256SUMS.txt`:

   ```powershell
   git tag v0.1.0
   git push origin v0.1.0
   ```

2. Download the installer from the release, or use the local build output.

3. Generate the winget manifests:

   ```powershell
   .\tools\New-WingetManifest.ps1 `
     -Version 0.1.0 `
     -InstallerUrl https://github.com/KoudelkaB/win-search/releases/download/v0.1.0/FileSearchManager-Setup-0.1.0.exe `
     -InstallerPath .\installer\Output\FileSearchManager-Setup-0.1.0.exe
   ```

4. Copy the generated `manifests\b\BohdanKoudelka\FileSearchManager\0.1.0` folder into the
   `winget-pkgs` fork. It contains 13 files: version, installer, and 11 locales.

5. Validate from the `winget-pkgs` checkout:

   ```powershell
   winget validate .\manifests\b\BohdanKoudelka\FileSearchManager\0.1.0
   ```

6. Test install locally from the manifest folder:

   ```powershell
   winget install --manifest .\manifests\b\BohdanKoudelka\FileSearchManager\0.1.0
   ```

7. Submit the pull request to `microsoft/winget-pkgs`.

   Every pull request there needs community-moderator approval, and the first one for a new package
   gets the closest look - expect anywhere from two days to a week. Once it is merged, later
   releases publish automatically.

## Subsequent Releases

```powershell
git tag v0.2.0
git push origin v0.2.0
```

Then watch for the pull request the action opens against `microsoft/winget-pkgs`. It still has to be
approved by a moderator, but the manifest work is done for you.

## Notes

- The community repository requires installers to support silent installation. Inno Setup installers support silent mode and winget knows the `inno` installer type.
- winget runs the installer as `/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-`, which changes behaviour that never shows up in an interactive install: any message box Setup would have shown is answered with its default, and for Abort/Retry/Ignore that default is **Abort**. This is why `setup.iss` sets `CloseApplications=force` - with plain `yes`, installing while File Search Manager is running aborted with exit code 5. Test upgrades with the app open, not just on a clean machine.
- The installer requires elevation because it installs to Program Files and installs a Windows service.
- The `installservice` task is selected by default, so winget installs get the service even though a silent install can never show the checkbox. `UsePreviousTasks` (default yes) means an upgrade reuses the previous choice, so an existing install that declined the service keeps declining it. **Verify this in silent mode before release** - the Inno docs describe `UsePreviousTasks` in terms of wizard defaults, and a silent install has no wizard. If it does not hold there, every `winget upgrade` would force the service onto users who opted out.
- Keep the release asset URL stable. Do not replace an installer after submitting a manifest, because that changes the SHA-256 hash.
- If the package version differs from the Apps & Features display version, add or update `AppsAndFeaturesEntries`.
