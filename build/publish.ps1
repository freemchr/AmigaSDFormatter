<#
.SYNOPSIS
  Builds, tests and packages Amiga SD Creator for release.
.DESCRIPTION
  Produces in .\artifacts:
    AmigaSdCreator-<version>-win-x64-portable.zip   self-contained single-file app (no .NET install needed)
    asdc-<version>-win-x64.zip                      command-line tool (image files only)
  If Inno Setup 6 (ISCC.exe) is installed, also builds the installer from installer.iss.
.EXAMPLE
  pwsh build\publish.ps1
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
Push-Location $root
try {
    [xml]$props = Get-Content "Directory.Build.props"
    $version = $props.Project.PropertyGroup.Version
    Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $artifacts -ItemType Directory | Out-Null

    if (-not $SkipTests) {
        dotnet test tests\AmigaSdCreator.Core.Tests -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    $app = Join-Path $artifacts "app"
    dotnet publish src\AmigaSdCreator.App -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o $app --nologo
    if ($LASTEXITCODE -ne 0) { throw "App publish failed." }
    Copy-Item README.md, LICENSE -Destination $app -ErrorAction SilentlyContinue
    Compress-Archive -Path "$app\*" -DestinationPath (Join-Path $artifacts "AmigaSdCreator-$version-win-x64-portable.zip")

    $cli = Join-Path $artifacts "cli"
    dotnet publish src\AmigaSdCreator.Cli -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:DebugType=none -o $cli --nologo
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }
    Compress-Archive -Path "$cli\*" -DestinationPath (Join-Path $artifacts "asdc-$version-win-x64.zip")

    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc "/DAppVersion=$version" "/DSourceDir=$app" "/O$artifacts" (Join-Path $PSScriptRoot "installer.iss")
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
    } else {
        Write-Warning "Inno Setup 6 not found; skipping the installer. Install it from https://jrsoftware.org/isinfo.php to build one."
    }
    Get-ChildItem $artifacts -File | Format-Table Name, Length
}
finally { Pop-Location }
