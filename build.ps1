<#
    Builds claude-killer.exe with the in-box .NET Framework compiler.
    No SDK, no NuGet, no install - works on a locked-down corporate machine.

    Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
            powershell -ExecutionPolicy Bypass -File build.ps1 -Shortcut
#>
param(
    [switch]$Shortcut
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src\ClaudeKiller.cs'
$manifest = Join-Path $root 'src\app.manifest'
$outDir = Join-Path $root 'bin'
$exe = Join-Path $outDir 'claude-killer.exe'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe not found - is .NET Framework 4.x present?" }

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$refs = @(
    'System.dll'
    'System.Core.dll'
    'System.Management.dll'
    'System.ServiceProcess.dll'
    'System.Windows.Forms.dll'
) | ForEach-Object { "/reference:$_" }

$cscArgs = @(
    '/nologo'
    '/target:winexe'          # no console window - silent by default
    '/platform:anycpu'
    '/optimize+'
    '/codepage:65001'
    "/win32manifest:$manifest"
    "/out:$exe"
) + $refs + @($src)

Write-Host "Compiling -> $exe"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit $LASTEXITCODE)" }

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "Built claude-killer.exe ($size KB)" -ForegroundColor Green

if ($Shortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desktop 'Claude ujraindito.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $outDir
    $s.Description = 'Beragadt Claude folyamatok leallitasa es a Claude Desktop ujrainditasa'
    $s.IconLocation = "$env:SystemRoot\System32\shell32.dll,238"
    $s.Save()
    Write-Host "Shortcut created: $lnk" -ForegroundColor Green
}
