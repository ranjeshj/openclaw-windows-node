<#
.SYNOPSIS
  Stand up a known-good openclaw gateway inside an isolated WSL distro for
  the gateway-compat test harness.

.DESCRIPTION
  Idempotent host-side orchestrator. On a clean Windows machine this takes
  ~3-4 minutes (mostly WSL image download + apt-get + npm install). On a
  warm machine where the distro and gateway are already up, it's ~10s
  (just re-applies the fake-LLM provider patch).

  Uses distro name 'OpenClawGatewayCompat' which is intentionally
  DIFFERENT from the production tray's default 'OpenClawGateway' so this
  can coexist with a real production install on a dev machine.

.PARAMETER GatewayVersion
  openclaw npm version or dist-tag. Default 'latest'.

.PARAMETER GatewayPort
  Local TCP port the gateway will bind. Default 18789.

.PARAMETER FakeLlmPort
  Local TCP port the fake-LLM mock will bind. Default 18888.

.PARAMETER SetupCodeOutPath
  Where the bootstrap setup-code JSON is written on the host. Default is a
  path under the repo's tmp-artifacts/ dir (which is .gitignored).

.OUTPUTS
  Writes the resolved setup-code path to stdout on the last line so callers
  can capture it. Also writes structured progress to stderr/Information.

.EXAMPLE
  pwsh tools/gateway-compat/Ensure-TestGateway.ps1
#>
[CmdletBinding()]
param(
  [string]$GatewayVersion = 'latest',
  # Use a port well away from the production default 18789 so this co-exists
  # with a user's real OpenClawGateway distro under WSL2 mirrored-mode
  # networking, which shares localhost across all distros.
  [int]$GatewayPort = 28789,
  [int]$FakeLlmPort = 28888,
  [string]$SetupCodeOutPath
)

$ErrorActionPreference = 'Stop'
# wsl.exe emits UTF-16 LE; force UTF-8 so captured stdout is parseable.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$DistroName = 'OpenClawGatewayCompat'
$BaseDistro = 'Ubuntu-24.04'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ScriptDir = (Resolve-Path $PSScriptRoot).Path
if (-not $SetupCodeOutPath) {
  $SetupCodeOutPath = Join-Path $RepoRoot 'tmp-artifacts\setup-code.json'
}
New-Item -ItemType Directory -Path (Split-Path $SetupCodeOutPath) -Force | Out-Null

function ConvertTo-WslPath([string]$Path) {
  $p = ($Path -replace '\\','/').TrimEnd('/')
  if ($p -match '^([A-Za-z]):/(.*)$') {
    return "/mnt/" + $matches[1].ToLower() + "/" + $matches[2]
  }
  return $p
}

function Invoke-Wsl {
  param([string[]]$WslArgs, [switch]$IgnoreExit)
  Write-Information ("wsl.exe " + ($WslArgs -join ' ')) -InformationAction Continue
  & wsl.exe @WslArgs
  $ec = $LASTEXITCODE
  if (-not $IgnoreExit -and $ec -ne 0) {
    throw "wsl.exe $($WslArgs -join ' ') failed with exit code $ec"
  }
  return $ec
}

#----------------------------------------------------------------------
# Step 1: Ensure the OpenClawGatewayCompat distro exists.
# We import Ubuntu-24.04 under our distro name so the production
# OpenClawGateway distro (if present) is untouched.
#----------------------------------------------------------------------
$existingDistros = (& wsl.exe --list --quiet 2>$null) | ForEach-Object { $_.Trim() } | Where-Object { $_ }
if ($existingDistros -notcontains $DistroName) {
  Write-Information "Installing WSL distro $DistroName (base: $BaseDistro)..." -InformationAction Continue
  Invoke-Wsl @('--install', $BaseDistro, '--name', $DistroName, '--no-launch', '--version', '2')
} else {
  Write-Information "WSL distro $DistroName already exists; skipping install." -InformationAction Continue
}

#----------------------------------------------------------------------
# Step 2: Provision the distro (creates openclaw user, installs deps).
#----------------------------------------------------------------------
$repoWsl = ConvertTo-WslPath $RepoRoot
$scriptsWsl = ConvertTo-WslPath $ScriptDir
$setupCodeWslPath = ConvertTo-WslPath $SetupCodeOutPath

Invoke-Wsl @(
  '-d', $DistroName,
  '-u', 'root',
  '--',
  'bash', "$scriptsWsl/setup-distro.sh"
)

#----------------------------------------------------------------------
# Step 3: Install/start gateway, patch fake-LLM provider, emit setup-code.
#----------------------------------------------------------------------
$envPairs = @(
  "OPENCLAW_GATEWAY_VERSION=$GatewayVersion",
  "FAKE_LLM_PORT=$FakeLlmPort",
  "GATEWAY_PORT=$GatewayPort",
  "REPO_WSL_PATH=$repoWsl",
  "SETUP_CODE_OUT=$setupCodeWslPath"
) -join ' '

# We invoke through bash -lc so the openclaw user's profile (PATH for
# /opt/openclaw/bin, OPENCLAW_PROFILE, etc.) is loaded.
$bashCmd = "$envPairs bash '$scriptsWsl/setup-gateway.sh'"
Invoke-Wsl @(
  '-d', $DistroName,
  '-u', 'openclaw',
  '--',
  'bash', '-lc', $bashCmd
)

#----------------------------------------------------------------------
# Step 4: Verify the setup-code landed on the host.
#----------------------------------------------------------------------
if (-not (Test-Path $SetupCodeOutPath)) {
  throw "setup-gateway.sh reported success but no setup-code at $SetupCodeOutPath"
}
$setupCodeBytes = (Get-Item $SetupCodeOutPath).Length
if ($setupCodeBytes -lt 10) {
  throw "setup-code at $SetupCodeOutPath looks empty ($setupCodeBytes bytes)"
}

#----------------------------------------------------------------------
# Step 5: Keep the distro alive. WSL2 auto-shuts a distro after a few
# seconds of no interactive sessions, which would kill our nohup'd
# gateway. Spawn a long-lived detached keepalive (`sleep infinity`)
# unless one is already running.
#----------------------------------------------------------------------
$keepaliveMarker = Join-Path $env:LOCALAPPDATA "OpenClawGatewayCompat-keepalive.pid"
$keepaliveAlive = $false
if (Test-Path $keepaliveMarker) {
  $oldPid = Get-Content $keepaliveMarker -ErrorAction SilentlyContinue
  if ($oldPid -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {
    $keepaliveAlive = $true
  }
}
if (-not $keepaliveAlive) {
  $proc = Start-Process -FilePath 'wsl.exe' `
    -ArgumentList @('-d', $DistroName, '--', 'bash', '-c', 'exec sleep infinity') `
    -WindowStyle Hidden -PassThru
  Set-Content -Path $keepaliveMarker -Value $proc.Id
  Write-Information "Started WSL keepalive (pid $($proc.Id)) so gateway stays running." -InformationAction Continue
}

Write-Information "Gateway is ready. Setup-code: $SetupCodeOutPath ($setupCodeBytes bytes)" -InformationAction Continue
# Last stdout line is the path so callers can: $path = pwsh Ensure-TestGateway.ps1 | Select -Last 1
Write-Output $SetupCodeOutPath
