<#
.SYNOPSIS
  Builds build\out\pt-ffmpeg-bridge-setup-<version>.exe: one self-contained installer with the bridge
  and a pinned LGPL FFmpeg build.

.DESCRIPTION
  1. Extracts the protocol schema from the local Pro Tools install (if not done yet).
  2. Runs the unit tests and publishes the server.
  3. Downloads the pinned FFmpeg build (verified by SHA-256) and stages ffmpeg/ffprobe + DLLs.
  4. Compiles installer\pt-ffmpeg-bridge.iss with Inno Setup 6.
  Requires: .NET 8 SDK, Python 3 + protobuf, Inno Setup 6 (winget install JRSoftware.InnoSetup), gh or internet.
#>
param(
    [string]$Version = "0.1.0"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$build = Join-Path $root "build"
$stage = Join-Path $build "stage"

# Pinned FFmpeg: BtbN/FFmpeg-Builds, LGPL v3, shared libraries (includes libsoxr).
$ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-18-13-22/ffmpeg-n8.1.2-54-gc573a95381-win64-lgpl-shared-8.1.zip"
$ffmpegSha256 = "fdc77dcc567180a435858e817ab498853eab0f7b843021d7ce1fc32839a13088"

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

Step "Schema"
if (-not (Test-Path (Join-Path $root "proto\QuickTimeWrapper.proto"))) {
    python (Join-Path $root "tools\extract_proto.py")
    if ($LASTEXITCODE) { throw "extract_proto.py failed" }
}

Step "Tests"
dotnet test (Join-Path $root "tests\Bridge.Tests") -nologo -v q
if ($LASTEXITCODE) { throw "tests failed" }

Step "Publish server"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root "src\Bridge.Server") -c Release -nologo -v q -o (Join-Path $stage "server") -p:Version=$Version -p:DebugType=none
if ($LASTEXITCODE) { throw "publish failed" }

Step "FFmpeg"
$cache = Join-Path $build "cache"
New-Item -ItemType Directory -Force $cache | Out-Null
$zip = Join-Path $cache ([IO.Path]::GetFileName($ffmpegUrl))
if (-not (Test-Path $zip)) {
    Invoke-WebRequest $ffmpegUrl -OutFile $zip -UseBasicParsing
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($hash -ne $ffmpegSha256) { Remove-Item $zip; throw "FFmpeg download hash mismatch ($hash)" }
$unzipped = Join-Path $cache "unzipped"
Remove-Item $unzipped -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $zip $unzipped
$ffRoot = Get-ChildItem $unzipped -Directory | Select-Object -First 1
$ffStage = Join-Path $stage "ffmpeg"
New-Item -ItemType Directory -Force (Join-Path $ffStage "bin") | Out-Null
Get-ChildItem (Join-Path $ffRoot.FullName "bin") -File | Where-Object { $_.Name -ne "ffplay.exe" } |
    Copy-Item -Destination (Join-Path $ffStage "bin")
Copy-Item (Join-Path $ffRoot.FullName "LICENSE.txt") (Join-Path $ffStage "LICENSE.txt")
Set-Content (Join-Path $ffStage "SOURCE.txt") -Encoding UTF8 -Value @(
    "FFmpeg is licensed under the GNU LGPL v3 (see LICENSE.txt). This is an unmodified build from",
    "https://github.com/BtbN/FFmpeg-Builds; the corresponding source is available at",
    "https://github.com/FFmpeg/FFmpeg (tag n8.1.2) and the build scripts at the URL above.",
    "Downloaded from: $ffmpegUrl",
    "SHA-256: $ffmpegSha256")

Step "Installer"
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$Version" "/DStage=$stage" (Join-Path $root "installer\pt-ffmpeg-bridge.iss")
if ($LASTEXITCODE) { throw "ISCC failed" }

$out = Get-Item (Join-Path $build "out\pt-ffmpeg-bridge-setup-$Version.exe")
Step ("Done: {0} ({1:N1} MB)" -f $out.FullName, ($out.Length / 1MB))
