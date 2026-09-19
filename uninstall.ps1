<#
.SYNOPSIS
  Restores Avid's original ProToolsQuickTimeServer.exe (saved by install.ps1).
#>
param([string]$ProToolsDir = "C:\Program Files\Avid\Pro Tools")
$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-ProToolsDir", "`"$ProToolsDir`"") -Wait
    exit
}

$serverDir = Join-Path $ProToolsDir "QuickTimeServer"
$target = Join-Path $serverDir "ProToolsQuickTimeServer.exe"
$backup = Join-Path $serverDir "ProToolsQuickTimeServer.orig.exe"

if (-not (Test-Path $backup)) { throw "No backup at $backup; nothing to restore." }
if (Get-Process ProTools -ErrorAction SilentlyContinue) { throw "Close Pro Tools first." }
Get-Process ProToolsQuickTimeServer -ErrorAction SilentlyContinue | Stop-Process -Force

Move-Item $backup $target -Force
Remove-Item (Join-Path $serverDir "bridge.json") -ErrorAction SilentlyContinue
Write-Host "Restored Avid's original $target"
