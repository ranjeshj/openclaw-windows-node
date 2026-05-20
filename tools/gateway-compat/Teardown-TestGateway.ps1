<#
.SYNOPSIS
  Tear down the OpenClawGatewayCompat WSL distro created by Ensure-TestGateway.ps1.

.DESCRIPTION
  By default just terminates the distro (frees the gateway port + LLM port,
  releases RAM). With -Unregister, also removes the registration so the
  next Ensure-TestGateway.ps1 starts from scratch.

  In CI, always run with -Unregister so subsequent jobs start clean. On a
  dev box, omit -Unregister to keep the npm install cached between runs.

.PARAMETER Unregister
  Also wsl --unregister the distro. Destructive: drops every artifact in
  the distro (npm install, /home/openclaw, etc.).
#>
[CmdletBinding()]
param([switch]$Unregister)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$DistroName = 'OpenClawGatewayCompat'

# Kill any keepalive process Ensure-TestGateway.ps1 spawned.
$keepaliveMarker = Join-Path $env:LOCALAPPDATA "OpenClawGatewayCompat-keepalive.pid"
if (Test-Path $keepaliveMarker) {
  $kpid = Get-Content $keepaliveMarker -ErrorAction SilentlyContinue
  if ($kpid) {
    Get-Process -Id $kpid -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  }
  Remove-Item $keepaliveMarker -ErrorAction SilentlyContinue
}

$existing = (& wsl.exe --list --quiet 2>$null) | ForEach-Object { $_.Trim() } | Where-Object { $_ }
if ($existing -notcontains $DistroName) {
  Write-Information "Distro $DistroName not registered; nothing to do." -InformationAction Continue
  exit 0
}

Write-Information "Terminating distro $DistroName..." -InformationAction Continue
& wsl.exe --terminate $DistroName | Out-Null

if ($Unregister) {
  Write-Information "Unregistering distro $DistroName (-Unregister set)." -InformationAction Continue
  & wsl.exe --unregister $DistroName | Out-Null
}
