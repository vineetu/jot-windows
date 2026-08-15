# Builds a Microsoft Store MSIX package for Jot (Jot Transcribe).
#
# Produces AppxPackages\JotTranscribe_<ver>_x64.msix — UNSIGNED. Upload the unsigned
# .msix straight to Partner Center; the Store re-signs it during certification (no
# code-signing cert required). For LOCAL testing, register the loose layout in dev mode
# with -Register (no signing needed, Developer Mode must be on).
#
# Usage:
#   .\build-msix.ps1                    # Public flavor: publish + pack -> AppxPackages\JotTranscribe_*.msix
#   .\build-msix.ps1 -Flavor Sony       # Sony flavor (PFB gateway only) -> AppxPackages\JotSie_*.msix
#   .\build-msix.ps1 -Register          # also (re)register the loose layout for local testing
#
# Public identity (src\Jot\Package.appxmanifest) MUST match its Partner Center product:
#   Name=Vineetsriram.JotTranscribe  Publisher=CN=1269BFA0-02DC-4524-8A77-55C4EE9EADD4
#   PublisherDisplayName="Vineet sriram"  DisplayName="Jot Transcribe" (reserved name)
# Sony identity (src\Jot\Package.Sony.appxmanifest) is a SEPARATE reserved product under the SAME
# account: Name=Vineetsriram.JotSie  DisplayName="Jot Sie"  (same Publisher/PublisherDisplayName).
# Confirm the Sony Name/DisplayName against the "Jot Sie" Partner Center reservation before upload.

param(
    [ValidateSet('Public','Sony')][string]$Flavor = 'Public',
    [switch]$Register)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$proj = Join-Path $repo "src\Jot\Jot.csproj"

$isSony = $Flavor -eq 'Sony'
if ($isSony) {
    $manifestSrc = Join-Path $repo "src\Jot\Package.Sony.appxmanifest"
    $msixName = "JotSie"
} else {
    $manifestSrc = Join-Path $repo "src\Jot\Package.appxmanifest"
    $msixName = "JotTranscribe"
}
$tfmDir = "net10.0-windows10.0.26100.0\win-x64"
$pub = Join-Path $repo "src\Jot\bin\Release\$tfmDir\publish"
$outDir = Join-Path $repo "AppxPackages"

# Per-user dotnet is not on the inherited PATH on this box.
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

# makeappx.exe from the Windows SDK build-tools NuGet (NuGet cache is on D:).
$makeappx = Get-ChildItem "D:\caches\nuget-packages\microsoft.windows.sdk.buildtools" `
    -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $makeappx) { throw "makeappx.exe not found in the SDK build-tools NuGet package." }

Get-Process Jot -ErrorAction SilentlyContinue | Stop-Process -Force   # unlock Jot.exe

$flavorArgs = @()
if ($isSony) { $flavorArgs += "-p:Flavor=Sony" }

Write-Host "==> publishing self-contained x64 ($Flavor flavor)..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:GenerateAppxPackageOnBuild=false @flavorArgs --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Official transcribe.cpp natives must actually be in the payload. A script that *intends*
# to copy them is not evidence — unpack/launch is the bar, but fail the pack if they're absent.
foreach ($need in @("transcribe.dll", "ggml-vulkan.dll", "ggml.dll", "contract.json")) {
    $p = Join-Path $pub $need
    if (-not (Test-Path $p)) { throw "publish missing ggml native: $need (under $pub)" }
}

# Read the version out of the manifest so the .msix filename matches.
[xml]$mx = Get-Content $manifestSrc
$ver = $mx.Package.Identity.Version

Write-Host "==> writing resolved AppxManifest.xml (token + arch)..." -ForegroundColor Cyan
$m = Get-Content $manifestSrc -Raw
$m = $m -replace '\$targetnametoken\$', 'Jot'                       # MSBuild token -> exe name
$m = $m -replace '(Version="[\d.]+")\s*/>', '$1 ProcessorArchitecture="x64" />'
[System.IO.File]::WriteAllText((Join-Path $pub "AppxManifest.xml"), $m,
    (New-Object System.Text.UTF8Encoding($false)))

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msix = Join-Path $outDir "${msixName}_${ver}_x64.msix"
Write-Host "==> packing $msix ..." -ForegroundColor Cyan
& $makeappx pack /o /d $pub /p $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)" }
$mb = [math]::Round((Get-Item $msix).Length / 1MB, 1)
Write-Host "==> built $msix ($mb MB)" -ForegroundColor Green

if ($Register) {
    Write-Host "==> registering loose layout for local testing..." -ForegroundColor Cyan
    Add-AppxPackage -Register (Join-Path $pub "AppxManifest.xml")
    $identityName = $mx.Package.Identity.Name
    $pkg = Get-AppxPackage -Name $identityName | Select-Object -First 1
    Write-Host "==> registered $($pkg.PackageFullName)." -ForegroundColor Green
    Write-Host "==> Launch: explorer shell:AppsFolder\$($pkg.PackageFamilyName)!jot" -ForegroundColor Green
}
