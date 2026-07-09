param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+(\.\d+){1,3}([-.+][A-Za-z0-9.-]+)?$')]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://')]
    [string] $InstallerUrl,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $InstallerPath,

    [string] $OutputRoot = "packaging\winget\generated"
)

$ErrorActionPreference = "Stop"

$packageIdentifier = "BohdanKoudelka.FileSearchManager"
$manifestVersion = "1.12.0"
$publisher = "Bohdan Koudelka"
$packageName = "File Search Manager"
$repoUrl = "https://github.com/KoudelkaB/win-search"
$sha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()

$outDir = Join-Path $OutputRoot "manifests\b\BohdanKoudelka\FileSearchManager\$Version"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-Utf8NoBomFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Resolve-Path -LiteralPath (Split-Path -Parent $Path)).Path + "\" + (Split-Path -Leaf $Path), $Value, $encoding)
}

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
PackageIdentifier: $packageIdentifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestVersion
"@

$localeManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
PackageIdentifier: $packageIdentifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: $publisher
PublisherUrl: https://github.com/KoudelkaB
PublisherSupportUrl: $repoUrl/issues
PackageName: $packageName
PackageUrl: $repoUrl
License: MIT
LicenseUrl: $repoUrl/blob/main/LICENSE
Copyright: Copyright (c) 2026 Bohdan Koudelka
ShortDescription: Fast Windows file search and management with NTFS MFT indexing.
Description: Fast Windows desktop file search and management with live filtering, content search, drag-and-drop, archive support, optional NTFS MFT indexing, and keyboard or context-menu file operations.
Moniker: file-search-manager
Tags:
- files
- search
- ntfs
- mft
- desktop
- utility
- windows
ReleaseNotesUrl: $repoUrl/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@

$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
PackageIdentifier: $packageIdentifier
PackageVersion: $Version
InstallerLocale: en-US
Platform:
- Windows.Desktop
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: machine
UpgradeBehavior: install
ElevationRequirement: elevationRequired
InstallModes:
- silent
AppsAndFeaturesEntries:
- DisplayName: $packageName
  Publisher: $publisher
  ProductCode: "{D9AE5E34-602D-49AF-9263-89E7B851B8D4}_is1"
  InstallerType: inno
Installers:
- Architecture: x64
  InstallerUrl: $InstallerUrl
  InstallerSha256: $sha256
  ProductCode: "{D9AE5E34-602D-49AF-9263-89E7B851B8D4}_is1"
ManifestType: installer
ManifestVersion: $manifestVersion
"@

Write-Utf8NoBomFile -Path (Join-Path $outDir "$packageIdentifier.yaml") -Value $versionManifest
Write-Utf8NoBomFile -Path (Join-Path $outDir "$packageIdentifier.locale.en-US.yaml") -Value $localeManifest
Write-Utf8NoBomFile -Path (Join-Path $outDir "$packageIdentifier.installer.yaml") -Value $installerManifest

Write-Host "Generated winget manifests:"
Write-Host "  $outDir"
Write-Host "Installer SHA256:"
Write-Host "  $sha256"
