#Requires -Version 5.1
<#
.SYNOPSIS
  Installs Lucky for the current Windows user (no admin required).

.DESCRIPTION
  Copies the published app next to this script into
  %LOCALAPPDATA%\Programs\Lucky, creates Start Menu and optional Desktop
  shortcuts, and writes an uninstall helper.
#>
[CmdletBinding()]
param(
    [switch]$NoDesktopShortcut,
    [switch]$Launch
)

$ErrorActionPreference = "Stop"

$sourceRoot = $PSScriptRoot
$exeName = "Lucky.exe"
if (-not (Test-Path (Join-Path $sourceRoot $exeName))) {
    # Support nested publish layouts or older App assembly names.
    $candidates = @(
        (Join-Path $sourceRoot "Lucky.exe"),
        (Join-Path $sourceRoot "Lucky.App.exe")
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) {
        throw "Could not find Lucky.exe next to Install-Lucky.ps1. Run this script from the extracted release folder."
    }
    $exeName = Split-Path $found -Leaf
}

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\Lucky"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Lucky"
$desktopLink = Join-Path ([Environment]::GetFolderPath("Desktop")) "Lucky.lnk"
$startMenuLink = Join-Path $startMenuDir "Lucky.lnk"
$installedExe = Join-Path $installRoot $exeName

Write-Host "Installing Lucky to $installRoot ..."

if (Test-Path $installRoot) {
    # Stop a running instance if present so files can be replaced.
    Get-Process -Name "Lucky","Lucky.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null

# Copy everything except this installer/uninstaller scripts' source copies if re-run from install dir.
Get-ChildItem -LiteralPath $sourceRoot -Force | ForEach-Object {
    if ($_.Name -in @("Install-Lucky.ps1")) {
        return
    }
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installRoot $_.Name) -Recurse -Force
}

# Keep installer helpers in the install root for uninstall / reinstall.
Copy-Item -LiteralPath (Join-Path $sourceRoot "Install-Lucky.ps1") -Destination (Join-Path $installRoot "Install-Lucky.ps1") -Force -ErrorAction SilentlyContinue
$uninstallPath = Join-Path $installRoot "Uninstall-Lucky.ps1"
@'
#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$installRoot = $PSScriptRoot
Get-Process -Name "Lucky","Lucky.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Lucky"
$desktopLink = Join-Path ([Environment]::GetFolderPath("Desktop")) "Lucky.lnk"
Remove-Item -LiteralPath $desktopLink -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $startMenuDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installRoot -Recurse -Force
Write-Host "Lucky was uninstalled from $installRoot"
'@ | Set-Content -LiteralPath $uninstallPath -Encoding UTF8

function New-Shortcut {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string]$IconLocation
    )

    $folder = Split-Path $Path -Parent
    if (-not (Test-Path $folder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = $WorkingDirectory
    if ($IconLocation) {
        $shortcut.IconLocation = $IconLocation
    }
    $shortcut.Description = "Lucky — local-first AI agent harness"
    $shortcut.Save()
}

$icon = Join-Path $installRoot "Assets\AppIcon.ico"
if (-not (Test-Path $icon)) {
    $icon = $installedExe
}

New-Shortcut -Path $startMenuLink -Target $installedExe -WorkingDirectory $installRoot -IconLocation $icon
if (-not $NoDesktopShortcut) {
    New-Shortcut -Path $desktopLink -Target $installedExe -WorkingDirectory $installRoot -IconLocation $icon
}

Write-Host ""
Write-Host "Lucky installed."
Write-Host "  App:        $installedExe"
Write-Host "  Start Menu: $startMenuLink"
if (-not $NoDesktopShortcut) {
    Write-Host "  Desktop:    $desktopLink"
}
Write-Host "  Uninstall:  powershell -ExecutionPolicy Bypass -File `"$uninstallPath`""
Write-Host ""

if ($Launch) {
    Start-Process -FilePath $installedExe -WorkingDirectory $installRoot
}
