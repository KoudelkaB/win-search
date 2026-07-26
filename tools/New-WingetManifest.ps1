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

    [string] $OutputRoot = "packaging\winget\generated",

    # Translations for the additional locale manifests. Kept out of this script so the
    # script itself stays pure ASCII - Windows PowerShell 5.1 decodes a BOM-less .ps1 as
    # ANSI, which would silently mangle the accented and CJK text.
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $LocaleData = (Join-Path $PSScriptRoot "winget-locales.json")
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

# Deliberately no InstallerLocale: the Inno installer bundles ten wizard languages, so it is
# locale-neutral. Pinning it to one locale can make "winget install --locale xx" find no
# applicable installer, even though the one we ship speaks that language.
$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
PackageIdentifier: $packageIdentifier
PackageVersion: $Version
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

# Additional locales only translate what "winget show" displays; they have nothing to do with
# the languages the app or the installer speak. Every field is optional and falls back to the
# default locale, so these carry just the two descriptions plus the ARP correlation fields.
$translations = Get-Content -LiteralPath $LocaleData -Raw -Encoding UTF8 | ConvertFrom-Json

foreach ($entry in $translations.PSObject.Properties) {
    $locale = $entry.Name
    $short = $entry.Value.ShortDescription
    $long = $entry.Value.Description

    if ([string]::IsNullOrWhiteSpace($short) -or [string]::IsNullOrWhiteSpace($long)) {
        throw "Locale '$locale' in $LocaleData is missing ShortDescription or Description."
    }

    # Quoted so punctuation in the translations can never be read as YAML syntax
    $manifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.12.0.schema.json
PackageIdentifier: $packageIdentifier
PackageVersion: $Version
PackageLocale: $locale
Publisher: "$publisher"
PackageName: "$packageName"
ShortDescription: "$short"
Description: "$long"
ManifestType: locale
ManifestVersion: $manifestVersion
"@

    Write-Utf8NoBomFile -Path (Join-Path $outDir "$packageIdentifier.locale.$locale.yaml") -Value $manifest
}

Write-Host "Generated winget manifests:"
Write-Host "  $outDir"
Write-Host "Locales:"
Write-Host "  en-US (default), $($translations.PSObject.Properties.Name -join ', ')"
Write-Host "Installer SHA256:"
Write-Host "  $sha256"
