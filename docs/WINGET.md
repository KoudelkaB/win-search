# Publishing Win Search to winget

This checklist is for publishing `BohdanKoudelka.WinSearch` to the Windows Package Manager community repository.

Microsoft's current manifest documentation recommends YAML manifests with required package metadata, installer URL, and SHA-256 hash. Multi-file manifests separate version, default locale, and installer data.

Useful references:

- Windows Package Manager manifest docs: https://learn.microsoft.com/windows/package-manager/package/manifest
- Community repository: https://github.com/microsoft/winget-pkgs
- Manifest schema docs: https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema

## Package Identity

- PackageIdentifier: `BohdanKoudelka.WinSearch`
- PackageName: `Win Search`
- Publisher: `Bohdan Koudelka`
- Moniker: `win-search`
- InstallerType: `inno`
- Scope: `machine`
- Architecture: `x64`
- Installer filename: `WinSearch-Setup-<version>.exe`
- Release URL pattern: `https://github.com/KoudelkaB/win-search/releases/download/v<version>/WinSearch-Setup-<version>.exe`

The installer writes the same package name and publisher to Apps & Features, which helps winget correlate installs and upgrades.

## Release Checklist

1. Tag the release:

   ```powershell
   git tag v0.1.0
   git push origin v0.1.0
   ```

2. Wait for the GitHub Actions release workflow to publish:

   ```text
   WinSearch-Setup-0.1.0.exe
   SHA256SUMS.txt
   ```

3. Download the installer or use the local build output.

4. Generate winget manifests:

   ```powershell
   .\tools\New-WingetManifest.ps1 `
     -Version 0.1.0 `
     -InstallerUrl https://github.com/KoudelkaB/win-search/releases/download/v0.1.0/WinSearch-Setup-0.1.0.exe `
     -InstallerPath .\installer\Output\WinSearch-Setup-0.1.0.exe
   ```

5. Copy the generated `manifests\b\BohdanKoudelka\WinSearch\0.1.0` folder into a fork of `microsoft/winget-pkgs`.

6. Validate from the `winget-pkgs` checkout:

   ```powershell
   winget validate .\manifests\b\BohdanKoudelka\WinSearch\0.1.0
   ```

7. Test install locally from the manifest folder:

   ```powershell
   winget install --manifest .\manifests\b\BohdanKoudelka\WinSearch\0.1.0
   ```

8. Submit a pull request to `microsoft/winget-pkgs`.

## Notes

- The community repository requires installers to support silent installation. Inno Setup installers support silent mode and winget knows the `inno` installer type.
- The installer requires elevation because it installs to Program Files and can install an optional Windows service.
- Keep the release asset URL stable. Do not replace an installer after submitting a manifest, because that changes the SHA-256 hash.
- If the package version differs from the Apps & Features display version, add or update `AppsAndFeaturesEntries`.
