# Restores official transcribe.cpp v0.1.3 / a94e021 Windows x64 CPU+Vulkan natives
# next to the Jot build. Invoked from Jot.csproj before compile/publish.
# PowerShell 5.1: no &&, no ternary. Does not commit the 80.5 MB extract.
#
# Pin (Round 2): SHA-256 9F536CB0FB839BD305E6D92FB214FD417C7718A416A6C7646A9911FBD56FDAD5
# Handy-shipped DLLs are a different binary with the same version string — refused at load.

param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$CacheDir = "D:\caches\vulkan-spike\official-0.1.3"
)

$ErrorActionPreference = "Stop"

$url = "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz"
$expectedSha = "9F536CB0FB839BD305E6D92FB214FD417C7718A416A6C7646A9911FBD56FDAD5"
$tarName = "transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz"
$extractLeaf = "transcribe-native-windows-x86_64-cpu-vulkan"

if (-not (Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
}

$tarPath = Join-Path $CacheDir $tarName
$extractDir = Join-Path $CacheDir "extract\$extractLeaf"

function Test-OfficialExtract([string]$dir) {
    if (-not (Test-Path (Join-Path $dir "transcribe.dll"))) { return $false }
    if (-not (Test-Path (Join-Path $dir "ggml-vulkan.dll"))) { return $false }
    if (-not (Test-Path (Join-Path $dir "contract.json"))) { return $false }
    return $true
}

if (-not (Test-OfficialExtract $extractDir)) {
    if (-not (Test-Path $tarPath)) {
        Write-Host "Downloading official transcribe.cpp v0.1.3 natives..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $tarPath -UseBasicParsing
    }
    $actual = (Get-FileHash -Algorithm SHA256 -Path $tarPath).Hash
    if ($actual -ne $expectedSha) {
        throw "official natives tarball hash mismatch: expected $expectedSha got $actual"
    }
    $unpack = Join-Path $CacheDir "extract"
    if (Test-Path $unpack) { Remove-Item -Recurse -Force $unpack }
    New-Item -ItemType Directory -Force -Path $unpack | Out-Null
    tar -xf $tarPath -C $unpack
    if (-not (Test-OfficialExtract $extractDir)) {
        throw "extract did not produce $extractDir (transcribe.dll + ggml-vulkan.dll + contract.json)"
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $extractDir -File | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $OutDir $_.Name) -Force
}

if (-not (Test-Path (Join-Path $OutDir "transcribe.dll"))) {
    throw "restore-ggml-natives: transcribe.dll missing from $OutDir"
}
if (-not (Test-Path (Join-Path $OutDir "ggml-vulkan.dll"))) {
    throw "restore-ggml-natives: ggml-vulkan.dll missing from $OutDir"
}
