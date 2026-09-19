<#
.SYNOPSIS
  Replaces Pro Tools' QuickTime helper (ProToolsQuickTimeServer.exe) with pt-ffmpeg-bridge.

.DESCRIPTION
  - Backs up Avid's original as ProToolsQuickTimeServer.orig.exe (once; never overwritten).
  - Copies the published bridge exe in its place, plus bridge.json with absolute ffmpeg/ffprobe paths.
  - Run uninstall.ps1 to put the original back.
  Pro Tools must be closed; it starts the helper when it launches.
#>
param(
    [string]$ProToolsDir = "C:\Program Files\Avid\Pro Tools",
    [string]$Build = (Join-Path $PSScriptRoot "dist\ProToolsQuickTimeServer.exe"),
    [string]$Ffmpeg,
    [string]$Ffprobe,
    [switch]$DebugLog
)
$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Resolve ffmpeg with *this* user's PATH before elevating (the admin session may not see it).
    if (-not $Ffmpeg) { $Ffmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source }
    if (-not $Ffprobe) { $Ffprobe = (Get-Command ffprobe.exe -ErrorAction SilentlyContinue).Source }
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-ProToolsDir", "`"$ProToolsDir`"", "-Build", "`"$Build`"")
    if ($Ffmpeg) { $argList += @("-Ffmpeg", "`"$Ffmpeg`"") }
    if ($Ffprobe) { $argList += @("-Ffprobe", "`"$Ffprobe`"") }
    if ($DebugLog) { $argList += "-DebugLog" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$serverDir = Join-Path $ProToolsDir "QuickTimeServer"
$target = Join-Path $serverDir "ProToolsQuickTimeServer.exe"
$backup = Join-Path $serverDir "ProToolsQuickTimeServer.orig.exe"

if (-not (Test-Path $Build)) { throw "Build not found: $Build (run: dotnet publish src\Bridge.Server -c Release -o dist)" }
if (-not (Test-Path $target)) { throw "Not found: $target. Is Pro Tools installed in $ProToolsDir?" }
if (Get-Process ProTools -ErrorAction SilentlyContinue) { throw "Close Pro Tools first." }
Get-Process ProToolsQuickTimeServer -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $Ffmpeg) { $Ffmpeg = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source }
if (-not $Ffprobe) { $Ffprobe = (Get-Command ffprobe.exe -ErrorAction SilentlyContinue).Source }
if (-not $Ffmpeg -or -not $Ffprobe) { throw "ffmpeg.exe / ffprobe.exe not found. Install FFmpeg (winget install Gyan.FFmpeg) or pass -Ffmpeg/-Ffprobe." }

if (-not (Test-Path $backup)) {
    $company = (Get-Item $target).VersionInfo.CompanyName
    if ($company -notlike "Avid*") { throw "$target is not Avid's original (company '$company') and no backup exists; refusing to continue." }
    Copy-Item $target $backup
    Write-Host "Backed up original -> $backup"
}

Copy-Item $Build $target -Force
$config = [ordered]@{ ffmpegPath = $Ffmpeg; ffprobePath = $Ffprobe; exactLengths = $true; debugLog = [bool]$DebugLog }
$config | ConvertTo-Json | Set-Content -Path (Join-Path $serverDir "bridge.json") -Encoding UTF8

Write-Host "Installed pt-ffmpeg-bridge -> $target"
Write-Host "  ffmpeg : $Ffmpeg"
Write-Host "  ffprobe: $Ffprobe"
Write-Host "Logs: $env:LOCALAPPDATA\pt-ffmpeg-bridge\logs"
