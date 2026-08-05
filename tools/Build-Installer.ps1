[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$IsccPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot "publish"
$appPublish = Join-Path $publishRoot "app"
$servicePublish = Join-Path $publishRoot "service"
$setupScript = Join-Path $repoRoot "installer\setup.iss"

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $Command (exit code $LASTEXITCODE)"
    }
}

function Reset-PublishDirectory([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullPublishRoot = [IO.Path]::GetFullPath($publishRoot).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullPublishRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a directory outside the publish root: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $fullPath
}

Reset-PublishDirectory $appPublish
Reset-PublishDirectory $servicePublish

Push-Location $repoRoot
try {
    Invoke-Checked "dotnet" @(
        "publish", "search/search.csproj",
        "-c", $Configuration, "-r", $Runtime,
        "--self-contained", "true", "-o", $appPublish
    )
    Invoke-Checked "dotnet" @(
        "publish", "search.service/search.service.csproj",
        "-c", $Configuration, "-r", $Runtime,
        "--self-contained", "true", "-o", $servicePublish
    )

    $appExe = Get-Item -LiteralPath (Join-Path $appPublish "File Search Manager.exe")
    $serviceExe = Get-Item -LiteralPath (Join-Path $servicePublish "search.service.exe")
    $appFileVersion = $appExe.VersionInfo.FileVersion
    $serviceFileVersion = $serviceExe.VersionInfo.FileVersion
    if ($appFileVersion -ne $serviceFileVersion) {
        throw "Published app version '$appFileVersion' does not match service version '$serviceFileVersion'."
    }

    if ($Version) {
        $expected = [regex]::Match($Version, '^\d+\.\d+\.\d+').Value
        if (-not $expected) {
            throw "Version '$Version' must start with major.minor.patch."
        }
        foreach ($published in @($appFileVersion, $serviceFileVersion)) {
            if ($published -ne $expected -and -not $published.StartsWith("$expected.")) {
                throw "Requested installer version '$Version' does not match published binary version '$published'."
            }
        }
    }

    if (-not $IsccPath) {
        $candidates = @(
            (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) "Inno Setup 6\ISCC.exe"),
            (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        )
        $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
        throw "Inno Setup 6 compiler was not found. Install it or pass -IsccPath."
    }

    $isccArguments = @("/Qp")
    if ($OutputDirectory) {
        $fullOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
        $null = New-Item -ItemType Directory -Force -Path $fullOutputDirectory
        $isccArguments += "/O$fullOutputDirectory"
    }
    if ($Version) {
        $isccArguments += "/DMyAppVersion=$Version"
    }
    $isccArguments += $setupScript
    Invoke-Checked $IsccPath $isccArguments
}
finally {
    Pop-Location
}

