<#
.SYNOPSIS
    Recovers Claude Desktop on Windows after a failed MSIX update, without a
    reboot and without administrator rights.

.DESCRIPTION
    Same job as claude-repair.exe, using only built-in PowerShell cmdlets - no
    compiled code, no Add-Type, nothing to download. That is the point: there is
    no binary for an antivirus to flag, and an administrator can read the whole
    thing in a couple of minutes before allowing it.

    The problem it solves: Claude Desktop ships as an MSIX package. After an
    update two things keep file locks on it, and neither is visible on the
    Processes tab of Task Manager:

      1. CoworkVMService - a LocalSystem service whose display name is just
         "Claude". It is AUTO_START and independent of the app window, so killing
         the process alone is useless; the Service Control Manager respawns it.
      2. Orphaned Claude.exe helper processes from the package.

    No administrator rights are needed. The service's security descriptor grants
    Authenticated Users SERVICE_START and SERVICE_STOP - check it yourself with
    "sc.exe sdshow CoworkVMService" and look for RP and WP in the (…;;;AU) entry.

.PARAMETER Diagnose
    Report what was found and change nothing.

.PARAMETER KeepCli
    Leave running Claude Code CLI sessions alone. By default they are terminated
    too, because a stuck update means everything has to let go of the package.
    The session running this script is never killed either way.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File claude-repair.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File claude-repair.ps1 -Diagnose

.NOTES
    MIT License. https://github.com/pichaccu/claude-unstick-windows
    If Windows blocks the downloaded file, clear the Mark of the Web first:
        Unblock-File .\claude-repair.ps1
#>
[CmdletBinding()]
param(
    [switch]$Diagnose,
    [switch]$KeepCli
)

$ServiceName     = 'CoworkVMService'
$VerifyTimeoutSec = 30
$ServiceWaitSec   = 25

function Write-Step { param([string]$Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "   $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "   $Text" -ForegroundColor Yellow }
function Write-Info { param([string]$Text) Write-Host "   $Text" }

# ---------------------------------------------------------------- discovery

function Get-ClaudeIdentity {
    $result = [pscustomobject]@{
        PackageFullName = $null
        FamilyName      = $null
        InstallLocation = $null
        Status          = $null
        Aumid           = $null
        AumidSource     = 'none'
    }

    # Registration is the authority. The service's ImagePath is NOT: during an
    # update it already points at the new version while only the old one is
    # registered, and activating an unregistered identity fails with
    # 0x800704C7 (ERROR_CANCELLED).
    $pkg = Get-AppxPackage -Name 'Claude*' -ErrorAction SilentlyContinue |
           Sort-Object Version -Descending | Select-Object -First 1
    if ($pkg) {
        $result.PackageFullName = $pkg.PackageFullName
        $result.FamilyName      = $pkg.PackageFamilyName
        $result.InstallLocation = $pkg.InstallLocation
        $result.Status          = $pkg.Status
    }

    # Get-StartApps reports the real AppUserModelId the shell would use.
    $app = Get-StartApps -ErrorAction SilentlyContinue |
           Where-Object { $_.AppID -like 'Claude_*!*' } | Select-Object -First 1
    if ($app) {
        $result.Aumid = $app.AppID
        $result.AumidSource = 'Get-StartApps'
    }
    elseif ($result.FamilyName) {
        # Fall back to the application id declared in the package manifest
        # rather than assuming a naming convention.
        $manifest = Join-Path $result.InstallLocation 'AppxManifest.xml'
        if (Test-Path $manifest) {
            try {
                [xml]$xml = Get-Content $manifest -Raw -ErrorAction Stop
                $id = $xml.Package.Applications.Application.Id | Select-Object -First 1
                if ($id) {
                    $result.Aumid = "$($result.FamilyName)!$id"
                    $result.AumidSource = 'AppxManifest.xml'
                }
            } catch { }
        }
    }

    return $result
}

# Everything the packaged app writes is redirected here, including the bundled
# Claude Code CLI. Task Manager reports the un-redirected path, which is why the
# CLI looks like it lives under %APPDATA%\Claude when it does not.
function Get-ClaudeRoots {
    param($Identity)

    $roots = @(
        (Join-Path $env:APPDATA 'Claude')
        (Join-Path $env:LOCALAPPDATA 'AnthropicClaude')
        (Join-Path $env:LOCALAPPDATA 'Claude')
        (Join-Path $env:USERPROFILE '.claude')
    )
    if ($Identity.FamilyName) {
        $roots += (Join-Path $env:LOCALAPPDATA "Packages\$($Identity.FamilyName)")
    }
    return $roots
}

# The script's own process and everything it descends from must survive, or it
# would kill the shell running it before the repair finishes.
function Get-AncestorPids {
    $ancestors = @($PID)
    $current = $PID
    for ($i = 0; $i -lt 24; $i++) {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $current" -ErrorAction SilentlyContinue
        if (-not $proc -or -not $proc.ParentProcessId -or $proc.ParentProcessId -eq 0) { break }
        $current = [int]$proc.ParentProcessId
        if ($ancestors -contains $current) { break }
        $ancestors += $current
    }
    return $ancestors
}

function Get-ClaudeTargets {
    param($Identity)

    $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    $protected = Get-AncestorPids
    $roots = Get-ClaudeRoots -Identity $Identity
    $targets = @{}

    foreach ($p in $all) {
        if ($protected -contains [int]$p.ProcessId) { continue }
        $path = $p.ExecutablePath
        $reason = $null

        if ($path -and $path -match '\\WindowsApps\\Claude_[^\\]+\\') {
            $reason = 'MSIX package'
        }
        elseif ($p.Name -eq 'cowork-svc.exe') {
            $reason = 'cowork service process'
        }
        elseif ($path -and -not $KeepCli) {
            foreach ($r in $roots) {
                if ($r -and $path.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    $reason = 'Claude Code CLI'
                    break
                }
            }
        }

        if ($reason) { $targets[[int]$p.ProcessId] = $reason }
    }

    # Descendants: MCP servers, hook scripts, shells the CLI spawned. Their own
    # paths say nothing about Claude, so the parent chain is the only signal.
    if (-not $KeepCli) {
        $byPid = @{}
        foreach ($p in $all) { $byPid[[int]$p.ProcessId] = $p }

        foreach ($p in $all) {
            $procId = [int]$p.ProcessId
            if ($targets.ContainsKey($procId) -or $protected -contains $procId) { continue }

            $cursor = $procId
            for ($depth = 0; $depth -lt 24; $depth++) {
                $node = $byPid[$cursor]
                if (-not $node -or -not $node.ParentProcessId) { break }
                $parentId = [int]$node.ParentProcessId
                if ($parentId -eq 0 -or $parentId -eq $cursor) { break }
                if ($protected -contains $parentId) { break }

                # Guard against PID reuse: a parent cannot be younger than its child.
                $parent = $byPid[$parentId]
                if (-not $parent) { break }
                if ($parent.CreationDate -and $node.CreationDate -and
                    $parent.CreationDate -gt $node.CreationDate) { break }

                if ($targets.ContainsKey($parentId)) {
                    $targets[$procId] = 'Claude child process'
                    break
                }
                $cursor = $parentId
            }
        }
    }

    return $targets
}

# ---------------------------------------------------------------- actions

function Stop-ClaudeService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Info "$ServiceName not present - skipping"; return $false }
    if ($svc.Status -eq 'Stopped') { Write-Info 'Service already stopped'; return $true }

    try {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($ServiceWaitSec))
        Write-Ok 'Service stopped'
        return $true
    } catch {
        Write-Warn "Could not stop the service: $($_.Exception.Message)"
        return $false
    }
}

function Start-ClaudeService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -eq 'Running') { return }
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Write-Ok 'Service restarted'
    } catch {
        Write-Warn "Could not restart the service: $($_.Exception.Message)"
    }
}

function Stop-ClaudeProcesses {
    param($Targets)

    $killed = 0
    foreach ($procId in $Targets.Keys) {
        try {
            Stop-Process -Id $procId -Force -ErrorAction Stop
            $killed++
        } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # Already gone - stopping the service takes cowork-svc down first.
        } catch {
            Write-Warn "PID $procId ($($Targets[$procId])): $($_.Exception.Message)"
        }
    }
    Write-Ok "Terminated $killed process(es)"
    return $killed
}

function Clear-ClaudeLocks {
    param($Identity)

    $dirs = @(Get-ClaudeRoots -Identity $Identity)
    $dirs += (Join-Path $env:APPDATA 'Claude\claude-code')
    if ($Identity.FamilyName) {
        $base = Join-Path $env:LOCALAPPDATA "Packages\$($Identity.FamilyName)"
        $dirs += (Join-Path $base 'LocalCache\Roaming\Claude')
        $dirs += (Join-Path $base 'LocalCache\Roaming\Claude\claude-code')
        $dirs += (Join-Path $base 'LocalCache\Local\Claude')
        $dirs += (Join-Path $base 'LocalState')
    }

    $names = @('SingletonLock', 'SingletonCookie', 'SingletonSocket', 'lockfile', '.lock', 'update.lock')
    $removed = 0

    foreach ($dir in ($dirs | Select-Object -Unique)) {
        if (-not $dir -or -not (Test-Path $dir)) { continue }

        foreach ($n in $names) {
            $f = Join-Path $dir $n
            if (Test-Path $f) {
                try { Remove-Item $f -Force -Recurse -ErrorAction Stop; $removed++ } catch { }
            }
        }
        Get-ChildItem -Path $dir -Filter '*.lock' -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item $_.FullName -Force -ErrorAction Stop; $removed++ } catch { }
        }
        $locks = Join-Path $dir 'locks'
        if (Test-Path $locks) {
            Get-ChildItem -Path $locks -File -ErrorAction SilentlyContinue | ForEach-Object {
                try { Remove-Item $_.FullName -Force -ErrorAction Stop; $removed++ } catch { }
            }
        }
    }

    Write-Ok "Removed $removed stale lock file(s)"
    return $removed
}

# A packaged app cannot be started from its WindowsApps path - activation has to
# go through the AppUserModelId.
function Start-ClaudeApp {
    param($Identity)

    if (-not $Identity.Aumid) { Write-Warn 'No AUMID - cannot activate the packaged app'; return $false }
    try {
        Start-Process "shell:AppsFolder\$($Identity.Aumid)" -ErrorAction Stop
        return $true
    } catch {
        Write-Warn "Activation failed: $($_.Exception.Message)"
        return $false
    }
}

function Wait-ForClaude {
    $deadline = (Get-Date).AddSeconds($VerifyTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $running = Get-Process -Name 'claude' -ErrorAction SilentlyContinue |
                   Where-Object { $_.Path -and $_.Path -match '\\WindowsApps\\Claude_' }
        if ($running) { return $true }
        Start-Sleep -Milliseconds 750
    }
    return $false
}

# Repair for a half-applied update: the files are on disk but the registration is
# broken. A user may re-register a package already installed for them, so this
# needs no admin.
function Repair-ClaudeRegistration {
    param($Identity)

    if (-not $Identity.InstallLocation) { Write-Warn 'No install location - cannot re-register'; return $false }
    $manifest = Join-Path $Identity.InstallLocation 'AppxManifest.xml'
    if (-not (Test-Path $manifest)) { Write-Warn "Manifest not readable: $manifest"; return $false }

    try {
        Add-AppxPackage -Register $manifest -DisableDevelopmentMode -ForceApplicationShutdown -ErrorAction Stop
        Write-Ok 'Package re-registered'
        return $true
    } catch {
        Write-Warn "Re-registration failed: $($_.Exception.Message)"
        return $false
    }
}

# ---------------------------------------------------------------- main

Write-Host "Claude repair - https://github.com/pichaccu/claude-unstick-windows"

$identity = Get-ClaudeIdentity

Write-Step 'Found'
Write-Info "Package:  $(if ($identity.PackageFullName) { $identity.PackageFullName } else { '(not registered)' })"
Write-Info "Status:   $($identity.Status)"
Write-Info "AUMID:    $($identity.Aumid)  [from $($identity.AumidSource)]"
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
Write-Info "Service:  $(if ($svc) { "$($svc.Status) (CanStop=$($svc.CanStop))" } else { 'not present' })"

$targets = Get-ClaudeTargets -Identity $identity
Write-Info "Processes to stop: $($targets.Count)"
foreach ($procId in ($targets.Keys | Sort-Object)) {
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    Write-Info "   [$procId] $(if ($p) { $p.ProcessName } else { '?' }) - $($targets[$procId])"
}

if ($Diagnose) {
    Write-Step 'Diagnose mode - nothing was changed'
    if (-not $identity.PackageFullName) {
        Write-Warn 'No registered Claude package. Reinstall from claude.ai/download.'
    }
    return
}

Write-Step 'Stopping the service'
$serviceWasPresent = $null -ne $svc
$null = Stop-ClaudeService

Write-Step 'Stopping Claude processes'
$null = Stop-ClaudeProcesses -Targets $targets

Write-Step 'Clearing stale locks'
$null = Clear-ClaudeLocks -Identity $identity

Write-Step 'Starting Claude'
$recovered = $false

if ((Start-ClaudeApp -Identity $identity) -and (Wait-ForClaude)) {
    $recovered = $true
} else {
    Write-Warn 'Did not come back - trying to repair the package registration'

    Write-Step 'Re-registering the package'
    if (Repair-ClaudeRegistration -Identity $identity) {
        $identity = Get-ClaudeIdentity
        Write-Info "Identity now: $($identity.PackageFullName)  AUMID $($identity.Aumid)"
        if ((Start-ClaudeApp -Identity $identity) -and (Wait-ForClaude)) { $recovered = $true }
    }
}

if ($serviceWasPresent) { Start-ClaudeService }

Write-Host ''
if ($recovered) {
    Write-Host 'DONE - Claude is running again.' -ForegroundColor Green
    exit 0
}

Write-Host 'FAILED - Claude did not come back.' -ForegroundColor Red
Write-Host ''
Write-Host 'What to try next:'
if (-not $identity.PackageFullName) {
    Write-Host '  The package is not registered for this user. Reinstall from claude.ai/download.'
} else {
    Write-Host '  Something outside this script is holding the package files - on a managed'
    Write-Host '  machine that is usually security software. Ask whoever administers it to'
    Write-Host "  check for handles on $($identity.InstallLocation)."
    Write-Host '  A reboot clears it in the meantime.'
}
exit 1
