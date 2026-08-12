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

    Output is English by default and Hungarian on a Hungarian Windows. Override
    with -Language en|hu.

.PARAMETER Diagnose
    Report what was found and change nothing.

.PARAMETER KeepCli
    Leave running Claude Code CLI sessions alone. By default they are terminated
    too, because a stuck update means everything has to let go of the package.
    The session running this script is never killed either way.

.PARAMETER Language
    Force the output language: en or hu.

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
    [switch]$KeepCli,
    [ValidateSet('en', 'hu')]
    [string]$Language
)

$ServiceName      = 'CoworkVMService'
$VerifyTimeoutSec = 30
$ServiceWaitSec   = 25

# ---------------------------------------------------------------- localisation

if (-not $Language) {
    $Language = if ((Get-UICulture).TwoLetterISOLanguageName -eq 'hu') { 'hu' } else { 'en' }
}

# One row per message: en, hu. Everything the user reads comes from here; code
# and comments stay English-only.
$Strings = @{
    'title'        = @('Claude repair', 'Claude javito')
    'found'        = @('Found', 'Amit talaltam')
    'package'      = @('Package:  {0}', 'Csomag:   {0}')
    'notreg'       = @('(not registered)', '(nincs regisztralva)')
    'status'       = @('Status:   {0}', 'Allapot:  {0}')
    'aumid'        = @('AUMID:    {0}  [from {1}]', 'AUMID:    {0}  [{1}]')
    'service'      = @('Service:  {0}', 'Szolg.:   {0}')
    'svcabsent'    = @('not present', 'nincs telepitve')
    'targets'      = @('Processes to stop: {0}', 'Leallitando folyamatok: {0}')
    'signature'    = @('Signature kind: {0}   AllowAllTrustedApps: {1}',
                       'Alairas tipusa: {0}   AllowAllTrustedApps: {1}')
    'notset'       = @('(not set)', '(nincs beallitva)')
    'diagmode'     = @('Diagnose mode - nothing was changed', 'Diagnosztika mod - semmi nem valtozott')
    'stopsvc'      = @('Stopping the service', 'Szolgaltatas leallitasa')
    'svcstopped'   = @('Service stopped', 'Szolgaltatas leallitva')
    'svcalready'   = @('Service already stopped', 'A szolgaltatas mar allt')
    'svcstopfail'  = @('Could not stop the service: {0}', 'A szolgaltatast nem sikerult leallitani: {0}')
    'svcstarted'   = @('Service restarted', 'Szolgaltatas ujrainditva')
    'svcstartfail' = @('Could not restart the service: {0}', 'A szolgaltatast nem sikerult visszainditani: {0}')
    'svcretry'     = @('Re-registration restarted the service - stopping it again',
                       'Az ujraregisztralas visszainditotta a szolgaltatast - ujra leallitom')
    'stopprocs'    = @('Stopping Claude processes', 'Claude folyamatok leallitasa')
    'terminated'   = @('Stopped {0} process(es)', '{0} folyamat leallitva')
    'clearlocks'   = @('Clearing stale locks', 'Beragadt lockok torlese')
    'removed'      = @('Removed {0} stale lock file(s)', '{0} beragadt lock fajl torolve')
    'starting'     = @('Starting Claude', 'Claude inditasa')
    'noaumid'      = @('No AUMID - cannot activate the packaged app',
                       'Nincs AUMID - a csomagolt appot nem tudom inditani')
    'actfail'      = @('Activation failed: {0}', 'Az inditas nem sikerult: {0}')
    'notback'      = @('Did not come back - trying to repair the package registration',
                       'Nem jott fel - megprobalom javitani a csomag regisztraciojat')
    'reregister'   = @('Re-registering the package', 'Csomag ujraregisztralasa')
    'reregok'      = @('Package re-registered', 'Csomag ujraregisztralva')
    'reregfail'    = @('Re-registration failed: {0}', 'Az ujraregisztralas nem sikerult: {0}')
    'noinstall'    = @('No install location - cannot re-register',
                       'Nincs telepitesi hely - nem tudom ujraregisztralni')
    'nomanifest'   = @('Manifest not readable: {0}', 'A manifest nem olvashato: {0}')
    'identitynow'  = @('Identity now: {0}  AUMID {1}', 'Azonossag most: {0}  AUMID {1}')
    'done'         = @('DONE - Claude is running again.', 'KESZ - a Claude ujraindult.')
    'failed'       = @('FAILED - Claude did not come back.', 'NEM SIKERULT - a Claude nem indult el.')
    'whatnext'     = @('What to try next:', 'Mit erdemes meg probalni:')
    'advnotreg'    = @('  The package is not registered for this user. Reinstall from claude.ai/download.',
                       '  A csomag nincs regisztralva ehhez a felhasznalohoz. Telepitsd ujra: claude.ai/download')
    'advsideload'  = @('  The package is Developer-signed and sideloading is disabled by policy.',
                       '  A csomag Developer-alairasu, es az oldaltelepites hazirenddel tiltva van.')
    'advsideload2' = @('  That alone prevents activation - an administrator has to allow trusted apps.',
                       '  Mar ez megakadalyozza az inditast - a rendszergazdanak engedelyeznie kell.')
    'advheld'      = @('  Something outside this script is holding the package files - on a managed',
                       '  Valami ezen a szkripten kivul fogja a csomag fajljait - felugyelt gepen')
    'advheld2'     = @('  machine that is usually security software. Ask whoever administers it to',
                       '  ez altalaban biztonsagi szoftver. Kerd meg a rendszergazdat, hogy nezze meg')
    'advheld3'     = @('  check for handles on {0}.', '  a nyitott hivatkozasokat itt: {0}')
    'advreboot'    = @('  A reboot clears it in the meantime.', '  Addig a gep ujrainditasa segit.')
    'reasonmsix'   = @('MSIX package', 'MSIX csomag')
    'reasonsvc'    = @('cowork service process', 'cowork szolgaltatas folyamat')
    'reasoncli'    = @('Claude Code CLI', 'Claude Code CLI')
    'reasonchild'  = @('Claude child process', 'Claude gyerekfolyamat')
}

function M {
    param([Parameter(Mandatory)][string]$Key, [object[]]$Args)
    $row = $Strings[$Key]
    if (-not $row) { return $Key }
    $text = if ($Language -eq 'hu' -and $row.Count -gt 1) { $row[1] } else { $row[0] }

    # Explicit count test, not truthiness: a single argument whose value is 0 -
    # PackageStatus.Ok, for one - makes a one-element array evaluate as false.
    if ($null -ne $Args -and $Args.Count -gt 0) {
        return ($text -f ($Args | ForEach-Object { "$_" }))
    }
    return $text
}

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
        SignatureKind   = $null
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
        $result.SignatureKind   = $pkg.SignatureKind
    }

    # Get-StartApps reports the real AppUserModelId the shell would use.
    $app = Get-StartApps -ErrorAction SilentlyContinue |
           Where-Object { $_.AppID -like 'Claude_*!*' } | Select-Object -First 1
    if ($app) {
        $result.Aumid = $app.AppID
        $result.AumidSource = 'Get-StartApps'
    }
    elseif ($result.FamilyName) {
        # Fall back to the application id declared in the package manifest rather
        # than assuming a naming convention.
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

# A Developer-signed package will not activate where sideloading is disabled by
# policy - common on managed hardware, and invisible from the package's Status.
function Get-SideloadPolicy {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    try {
        $v = (Get-ItemProperty -Path $key -Name AllowAllTrustedApps -ErrorAction Stop).AllowAllTrustedApps
        return "$v"
    } catch { return $null }
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
            $reason = M 'reasonmsix'
        }
        elseif ($p.Name -eq 'cowork-svc.exe') {
            $reason = M 'reasonsvc'
        }
        elseif ($path -and -not $KeepCli) {
            foreach ($r in $roots) {
                if ($r -and $path.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    $reason = M 'reasoncli'
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
                    $targets[$procId] = M 'reasonchild'
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
    if (-not $svc) { return $false }
    if ($svc.Status -eq 'Stopped') { Write-Info (M 'svcalready'); return $true }

    try {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($ServiceWaitSec))
        Write-Ok (M 'svcstopped')
        return $true
    } catch {
        Write-Warn (M 'svcstopfail' @($_.Exception.Message))
        return $false
    }
}

function Start-ClaudeService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -eq 'Running') { return }
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Write-Ok (M 'svcstarted')
    } catch {
        Write-Warn (M 'svcstartfail' @($_.Exception.Message))
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
            Write-Warn "PID $procId : $($_.Exception.Message)"
        }
    }
    Write-Ok (M 'terminated' @($killed))
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

    Write-Ok (M 'removed' @($removed))
    return $removed
}

# A packaged app cannot be started from its WindowsApps path - activation has to
# go through the AppUserModelId.
function Start-ClaudeApp {
    param($Identity)

    if (-not $Identity.Aumid) { Write-Warn (M 'noaumid'); return $false }
    try {
        Start-Process "shell:AppsFolder\$($Identity.Aumid)" -ErrorAction Stop
        return $true
    } catch {
        Write-Warn (M 'actfail' @($_.Exception.Message))
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

    if (-not $Identity.InstallLocation) { Write-Warn (M 'noinstall'); return $false }
    $manifest = Join-Path $Identity.InstallLocation 'AppxManifest.xml'
    if (-not (Test-Path $manifest)) { Write-Warn (M 'nomanifest' @($manifest)); return $false }

    try {
        Add-AppxPackage -Register $manifest -DisableDevelopmentMode -ForceApplicationShutdown -ErrorAction Stop
        Write-Ok (M 'reregok')
    } catch {
        Write-Warn (M 'reregfail' @($_.Exception.Message))
        return $false
    }

    # Re-registering the package starts its service again, and that service is
    # exactly what holds the package files. Without stopping it here the next
    # activation attempt is blocked by the repair we just performed.
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Info (M 'svcretry')
        $null = Stop-ClaudeService
    }

    return $true
}

# ---------------------------------------------------------------- main

Write-Host (M 'title') -NoNewline
Write-Host ' - https://github.com/pichaccu/claude-unstick-windows'

$identity = Get-ClaudeIdentity
$sideload = Get-SideloadPolicy

Write-Step (M 'found')
Write-Info (M 'package' @($(if ($identity.PackageFullName) { $identity.PackageFullName } else { M 'notreg' })))
Write-Info (M 'status'  @($identity.Status))
Write-Info (M 'aumid'   @($identity.Aumid, $identity.AumidSource))
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
Write-Info (M 'service' @($(if ($svc) { "$($svc.Status) (CanStop=$($svc.CanStop))" } else { M 'svcabsent' })))
Write-Info (M 'signature' @($identity.SignatureKind, $(if ($null -ne $sideload) { $sideload } else { M 'notset' })))

$targets = Get-ClaudeTargets -Identity $identity
Write-Info (M 'targets' @($targets.Count))
foreach ($procId in ($targets.Keys | Sort-Object)) {
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    Write-Info "   [$procId] $(if ($p) { $p.ProcessName } else { '?' }) - $($targets[$procId])"
}

if ($Diagnose) {
    Write-Step (M 'diagmode')
    if (-not $identity.PackageFullName) { Write-Warn (M 'advnotreg') }
    return
}

Write-Step (M 'stopsvc')
$serviceWasPresent = $null -ne $svc
$null = Stop-ClaudeService

Write-Step (M 'stopprocs')
$null = Stop-ClaudeProcesses -Targets $targets

Write-Step (M 'clearlocks')
$null = Clear-ClaudeLocks -Identity $identity

Write-Step (M 'starting')
$recovered = $false

if ((Start-ClaudeApp -Identity $identity) -and (Wait-ForClaude)) {
    $recovered = $true
} else {
    Write-Warn (M 'notback')

    Write-Step (M 'reregister')
    if (Repair-ClaudeRegistration -Identity $identity) {
        $identity = Get-ClaudeIdentity
        Write-Info (M 'identitynow' @($identity.PackageFullName, $identity.Aumid))
        if ((Start-ClaudeApp -Identity $identity) -and (Wait-ForClaude)) { $recovered = $true }
    }
}

if ($serviceWasPresent) { Start-ClaudeService }

Write-Host ''
if ($recovered) {
    Write-Host (M 'done') -ForegroundColor Green
    exit 0
}

Write-Host (M 'failed') -ForegroundColor Red
Write-Host ''
Write-Host (M 'whatnext')

if (-not $identity.PackageFullName) {
    Write-Host (M 'advnotreg')
}
elseif ($identity.SignatureKind -eq 'Developer' -and $sideload -eq '0') {
    Write-Host (M 'advsideload')
    Write-Host (M 'advsideload2')
}
else {
    Write-Host (M 'advheld')
    Write-Host (M 'advheld2')
    Write-Host (M 'advheld3' @($identity.InstallLocation))
    Write-Host (M 'advreboot')
}
exit 1
