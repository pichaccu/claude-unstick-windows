<#
    Builds claude-repair.exe with the in-box .NET Framework compiler.
    No SDK, no NuGet, no install - works on a locked-down corporate machine.

    Usage:
        powershell -ExecutionPolicy Bypass -File build.ps1
        powershell -ExecutionPolicy Bypass -File build.ps1 -Sign
        powershell -ExecutionPolicy Bypass -File build.ps1 -Sign -Shortcut

    -Sign uses a self-signed certificate created in your own user store (no admin
    needed). Read the "Signing and antivirus" section of README.md first: a
    self-signed signature stops the SmartScreen "unknown publisher" prompt only on
    machines that trust the certificate, and it is NOT what clears a Defender
    false positive. The free fix for that is submitting the binary to Microsoft.
#>
param(
    [switch]$Shortcut,
    [switch]$Sign,
    [string]$CertSubject = 'CN=pichaccu, O=pichaccu, C=HU',
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sources = @(
    (Join-Path $root 'src\ClaudeKiller.cs')
    (Join-Path $root 'src\AssemblyInfo.cs')
)
$manifest = Join-Path $root 'src\app.manifest'
$outDir = Join-Path $root 'bin'
$exe = Join-Path $outDir 'claude-repair.exe'

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
) + $refs + $sources

Write-Host "Compiling -> $exe"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit $LASTEXITCODE)" }

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "Built claude-repair.exe ($size KB)" -ForegroundColor Green

# Metadata is the cheapest defence against a machine-learning false positive.
# Fail loudly if it did not make it into the binary.
$vi = (Get-Item $exe).VersionInfo
if ([string]::IsNullOrWhiteSpace($vi.CompanyName) -or $vi.FileVersion -eq '0.0.0.0') {
    throw "Version resource missing - check src\AssemblyInfo.cs"
}
Write-Host ("  {0} {1} - {2}" -f $vi.ProductName, $vi.FileVersion, $vi.CompanyName)

if ($Sign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending | Select-Object -First 1

    if (-not $cert) {
        Write-Host "Creating self-signed code signing certificate in CurrentUser\My"
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $CertSubject `
            -CertStoreLocation Cert:\CurrentUser\My `
            -KeyAlgorithm RSA -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy Exportable `
            -NotAfter (Get-Date).AddYears(5)
    }
    Write-Host "  cert: $($cert.Subject)  thumbprint $($cert.Thumbprint)"

    # Timestamping matters: without it the signature dies with the certificate.
    $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert `
               -HashAlgorithm SHA256 -TimestampServer $TimestampServer

    Write-Host "  signature: $($sig.Status) - $($sig.StatusMessage)" -ForegroundColor Cyan
    if ($sig.Status -notin @('Valid', 'UnknownError')) { throw "Signing failed: $($sig.Status)" }

    # Export the public certificate so it can be trusted elsewhere on purpose.
    $cerPath = Join-Path $outDir 'claude-repair-signing.cer'
    Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
    Write-Host "  public cert exported: $cerPath"
}

Write-Host ("SHA256: " + (Get-FileHash $exe -Algorithm SHA256).Hash)

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
