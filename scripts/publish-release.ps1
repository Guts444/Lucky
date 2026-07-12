#Requires -Version 5.1
<#
.SYNOPSIS
  Builds Lucky win-x64 installers: MSI (WiX) and Setup.exe (Inno Setup).

.DESCRIPTION
  1. Runs tests
  2. Publishes a self-contained unpackaged WinUI app
  3. Builds Lucky-{version}-win-x64.msi
  4. Builds Lucky-{version}-win-x64-Setup.exe

  Prerequisites (local):
  - .NET SDK
  - WiX CLI:  dotnet tool install -g wix
              wix extension add -g WixToolset.UI.wixext
  - Inno Setup 6 (ISCC.exe), typically via winget install JRSoftware.InnoSetup
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

function Resolve-Iscc {
    $candidates = @(
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) {
            return $path
        }
    }
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }
    return $null
}

function Resolve-WixUiExtension {
    $root = Join-Path $env:USERPROFILE ".wix\extensions\WixToolset.UI.wixext"
    if (-not (Test-Path $root)) {
        return $null
    }
    $dll = Get-ChildItem -Path $root -Recurse -Filter "WixToolset.UI.wixext.dll" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    return $dll?.FullName
}

$rid = "win-$Platform"
$publishDir = Join-Path $repoRoot "artifacts\publish\$rid"
$distDir = Join-Path $repoRoot "dist"
$installerDir = Join-Path $repoRoot "installer"
$msiPath = Join-Path $distDir "Lucky-$Version-$rid.msi"
$setupPath = Join-Path $distDir "Lucky-$Version-$rid-Setup.exe"

# MSI ProductVersion is major.minor.build (Windows Installer ignores a 4th field).
$msiVersion = $Version
if ($msiVersion -notmatch '^\d+\.\d+\.\d+') {
    throw "Version must look like 0.1.0 (got '$Version')"
}
if ($msiVersion -match '^(\d+\.\d+\.\d+)') {
    $msiVersion = $Matches[1]
}

if (-not $SkipTests) {
    Write-Host "==> Restoring and testing"
    dotnet test (Join-Path $repoRoot "Lucky.slnx") -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

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

# Keep installer icon next to WiX/Inno sources.
$iconSource = Join-Path $repoRoot "src\Lucky.App\Assets\AppIcon.ico"
$iconDest = Join-Path $installerDir "AppIcon.ico"
if (Test-Path $iconSource) {
    Copy-Item $iconSource $iconDest -Force
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# --- MSI (WiX) ---
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    throw "WiX CLI not found. Install with: dotnet tool install -g wix"
}

$uiExt = Resolve-WixUiExtension
if (-not $uiExt) {
    Write-Host "WiX UI extension missing; installing WixToolset.UI.wixext..."
    & wix extension add -g WixToolset.UI.wixext
    $uiExt = Resolve-WixUiExtension
}
if (-not $uiExt) {
    throw "Could not resolve WixToolset.UI.wixext.dll"
}

Write-Host "==> Building MSI (WiX)"
if (Test-Path $msiPath) { Remove-Item -LiteralPath $msiPath -Force }

& wix build `
    (Join-Path $installerDir "Package.wxs") `
    -arch $Platform `
    -ext $uiExt `
    -d "ProductVersion=$msiVersion" `
    -b "Payload=$publishDir" `
    -b "Installer=$installerDir" `
    -o $msiPath `
    -pdbtype none `
    -dcl high

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $msiPath)) {
    throw "WiX MSI build failed."
}

# --- Setup.exe (Inno Setup) ---
$iscc = Resolve-Iscc
if (-not $iscc) {
    throw "Inno Setup ISCC.exe not found. Install with: winget install JRSoftware.InnoSetup"
}

Write-Host "==> Building Setup.exe (Inno Setup)"
if (Test-Path $setupPath) { Remove-Item -LiteralPath $setupPath -Force }

& $iscc `
    "/DMyAppVersion=$Version" `
    "/DPayloadDir=$publishDir" `
    "/DOutputDir=$distDir" `
    (Join-Path $installerDir "Lucky.iss")

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setupPath)) {
    throw "Inno Setup build failed."
}

# Optional: drop legacy zip if present so releases stay focused on installers.
$legacyZip = Join-Path $distDir "Lucky-$Version-$rid.zip"
if (Test-Path $legacyZip) {
    Remove-Item -LiteralPath $legacyZip -Force
}

$msiMb = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
$setupMb = [math]::Round((Get-Item $setupPath).Length / 1MB, 1)

Write-Host ""
Write-Host "Release installers ready:"
Write-Host "  MSI:  $msiPath ($msiMb MB)"
Write-Host "  EXE:  $setupPath ($setupMb MB)"
Write-Host "  App:  $publishDir"
