<#
    Installs the self-signed code signing certificate into the machine's trust
    stores, so Windows treats claude-repair.exe as signed by a known publisher.

    RUN THIS ONLY ON MACHINES YOU OWN, AND ONLY WITH A CERTIFICATE YOU CREATED.

    What it actually does: adds the certificate to Trusted Root Certification
    Authorities and to Trusted Publishers, for the whole machine. That means
    Windows will from then on trust ANY code signed with the matching private key.
    That key lives in your own user certificate store. Treat it accordingly - if
    someone else gets it, they can sign software that this machine trusts.

    Requires an elevated PowerShell. Remove it later with -Remove.

    Usage:
        powershell -ExecutionPolicy Bypass -File trust-cert.ps1
        powershell -ExecutionPolicy Bypass -File trust-cert.ps1 -Remove
#>
param(
    [string]$CerPath = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'bin\claude-repair-signing.cer'),
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this from an elevated PowerShell (Run as administrator)." }

if (-not (Test-Path $CerPath)) { throw "Certificate not found: $CerPath  (run build.ps1 -Sign first)" }

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $CerPath
Write-Host "Certificate: $($cert.Subject)"
Write-Host "Thumbprint:  $($cert.Thumbprint)"
Write-Host "Valid until: $($cert.NotAfter)"

$stores = @('Root', 'TrustedPublisher')

if ($Remove) {
    foreach ($name in $stores) {
        $path = "Cert:\LocalMachine\$name\$($cert.Thumbprint)"
        if (Test-Path $path) {
            Remove-Item $path -Force
            Write-Host "Removed from LocalMachine\$name" -ForegroundColor Yellow
        } else {
            Write-Host "Not present in LocalMachine\$name"
        }
    }
    return
}

Write-Host ""
Write-Host "This machine will trust anything signed with the matching private key." -ForegroundColor Yellow
$answer = Read-Host "Type YES to continue"
if ($answer -cne 'YES') { Write-Host "Cancelled."; return }

foreach ($name in $stores) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, 'LocalMachine')
    $store.Open('ReadWrite')
    $store.Add($cert)
    $store.Close()
    Write-Host "Installed into LocalMachine\$name" -ForegroundColor Green
}

Write-Host ""
Write-Host "Verify with:  Get-AuthenticodeSignature .\bin\claude-repair.exe"
Write-Host "Status should now read Valid."
