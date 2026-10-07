<#
.SYNOPSIS
    Builds librespot.exe for Flow's optional built-in Spotify playback and copies it to tools\librespot\.

.DESCRIPTION
    librespot (https://github.com/librespot-org/librespot, MIT) has no reliable Windows binaries, so Flow builds
    it from source. Only the always-available "pipe" audio backend is compiled in (Flow plays the audio itself),
    with rustls for TLS and no network discovery (zeroconf is disabled at runtime anyway).

    Requirements: Rust (https://rustup.rs, MSVC toolchain) and the Visual Studio 2022 Build Tools with the
    "Desktop development with C++" workload, plus git.

    The source is cloned outside the repo (and outside OneDrive) to keep the build fast and the repo clean.
    The resulting exe is not committed (.gitignore ignores *.exe); Flow.csproj copies it next to Flow.exe when
    it exists.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-librespot.ps1
#>
param(
    [string]$Version = "v0.8.0",
    [string]$SourceDir = (Join-Path $env:LOCALAPPDATA "FlowBuild\librespot-src")
)

$ErrorActionPreference = "Stop"
$cargo = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
if (-not (Test-Path $cargo)) { $cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source }
if (-not $cargo) { throw "Rust is not installed. Install it from https://rustup.rs (MSVC toolchain), then run this again." }

# 1. Source at the requested tag.
if (-not (Test-Path (Join-Path $SourceDir ".git"))) {
    git clone --quiet --depth 1 --branch $Version https://github.com/librespot-org/librespot $SourceDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
} else {
    git -C $SourceDir fetch --quiet --depth 1 origin "refs/tags/${Version}:refs/tags/${Version}"
    git -C $SourceDir checkout --quiet $Version
    if ($LASTEXITCODE -ne 0) { throw "Could not check out $Version" }
}

# 2. Build: no default features (which would add the rodio audio backend, native-tls and mDNS); just rustls.
#    The pipe backend is always available.
Push-Location $SourceDir
try {
    & $cargo build --release --locked --no-default-features --features rustls-tls-native-roots
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
} finally { Pop-Location }

$built = Join-Path $SourceDir "target\release\librespot.exe"
if (-not (Test-Path $built)) { throw "Build finished but $built is missing" }

# 3. Sanity check: the pipe backend must be present.
# (librespot logs to stderr; run it through cmd so Windows PowerShell doesn't treat that as an error)
$backends = cmd /c "`"$built`" --backend ? 2>&1" | Out-String
if ($backends -notmatch "pipe") { throw "librespot was built without the pipe backend:`n$backends" }

# 4. Copy next to the repo's tools, where Flow.csproj picks it up.
$dest = Join-Path $PSScriptRoot "librespot"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $built (Join-Path $dest "librespot.exe") -Force
$size = [math]::Round((Get-Item (Join-Path $dest "librespot.exe")).Length / 1MB, 1)
Write-Host "librespot $Version built: tools\librespot\librespot.exe ($size MB)"
