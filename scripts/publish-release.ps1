#Requires -Version 5.1
<#
.SYNOPSIS
  Builds a self-contained win-x64 Lucky release zip with Install-Lucky.ps1.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0",
    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

$rid = "win-$Platform"
$publishDir = Join-Path $repoRoot "artifacts\publish\$rid"
$stageDir = Join-Path $repoRoot "artifacts\stage\Lucky-$Version-$rid"
$distDir = Join-Path $repoRoot "dist"
$zipPath = Join-Path $distDir "Lucky-$Version-$rid.zip"

Write-Host "==> Restoring and testing"
dotnet test (Join-Path $repoRoot "Lucky.slnx") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

Write-Host "==> Publishing self-contained $rid"
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish (Join-Path $repoRoot "src\Lucky.App\Lucky.App.csproj") `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:RuntimeIdentifier=$rid `
    -p:WindowsPackageType=None `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishReadyToRun=true `
    -p:Version=$Version `
    -p:PublishDir=$publishDir `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exePath = Join-Path $publishDir "Lucky.exe"
if (-not (Test-Path $exePath)) {
    $legacy = Join-Path $publishDir "Lucky.App.exe"
    if (Test-Path $legacy) {
        Copy-Item $legacy $exePath -Force
    } else {
        throw "Published output is missing Lucky.exe"
    }
}

Write-Host "==> Staging release folder"
if (Test-Path $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "scripts\Install-Lucky.ps1") -Destination (Join-Path $stageDir "Install-Lucky.ps1") -Force
Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $stageDir "LICENSE.txt") -Force
Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination (Join-Path $stageDir "README.md") -Force

@"
Lucky $Version (Windows $Platform)

Quick install (current user, no admin):
  1. Extract this zip anywhere.
  2. Right-click Install-Lucky.ps1 -> Run with PowerShell
     or: powershell -ExecutionPolicy Bypass -File .\Install-Lucky.ps1 -Launch

Portable run:
  Double-click Lucky.exe in this folder (Windows App SDK is bundled).

Uninstall:
  powershell -ExecutionPolicy Bypass -File `"$env:LOCALAPPDATA\Programs\Lucky\Uninstall-Lucky.ps1`"

Docs: https://github.com/Guts444/Lucky
"@ | Set-Content -LiteralPath (Join-Path $stageDir "INSTALL.txt") -Encoding UTF8

Write-Host "==> Creating zip"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# Compress-Archive is slow/fragile for large trees; prefer tar if available.
$tar = Get-Command tar -ErrorAction SilentlyContinue
if ($tar) {
    Push-Location (Split-Path $stageDir -Parent)
    try {
        & tar -a -cf $zipPath (Split-Path $stageDir -Leaf)
        if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    } finally {
        Pop-Location
    }
} else {
    Compress-Archive -Path $stageDir -DestinationPath $zipPath -Force
}

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "Release package ready:"
Write-Host "  $zipPath ($sizeMb MB)"
Write-Host "  staged: $stageDir"
