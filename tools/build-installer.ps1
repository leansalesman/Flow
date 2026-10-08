<#
.SYNOPSIS
    Builds Flow-Setup-x64-<version>.exe from a published Flow folder (Flow.exe + librespot.exe).

.DESCRIPTION
    Requires Inno Setup 6 (free): winget install JRSoftware.InnoSetup --scope user
    The version comes from src\Flow\Flow.csproj. Output: installer\Output\Flow-Setup-x64-<version>.exe

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -PublishDir publish
#>
param([Parameter(Mandatory)][string]$PublishDir)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup --scope user" }

[xml]$proj = Get-Content (Join-Path $root "src\Flow\Flow.csproj")
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> in Flow.csproj" }

$source = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $source "Flow.exe"))) { throw "Flow.exe not found in $source" }

& $iscc "/DAppVersion=$version" "/DSourceDir=$source" "/Q" (Join-Path $root "installer\Flow.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$out = Join-Path $root "installer\Output\Flow-Setup-x64-$version.exe"
Write-Host ("Built {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))
