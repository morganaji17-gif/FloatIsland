<#
.SYNOPSIS
  Authenticode-signs the Float Island binaries.

.DESCRIPTION
  Unsigned binaries are what behaviour-based AV reacts to, so a signature is the root fix.
  Three ways to get a signing certificate, in order of how much they actually help:

    -Thumbprint <hex>     an existing cert (a real CA-issued / EV cert, or one already in
                          Cert:\CurrentUser\My). Best: reputation is what stops SmartScreen.
    -Pfx <file> ...       a .pfx on disk (CI uses this via a secret).
    -SelfSigned           generate a local dev cert "CN=Float Island Dev" and trust it in
                          the *current user* stores. This clears a local AV block, but it
                          carries no reputation and will not help anyone else's SmartScreen.

  Signing uses the built-in Set-AuthenticodeSignature, so no Windows SDK / signtool is
  required. Every signature is timestamped (DigiCert, fallback Sectigo) so it stays valid
  after the certificate expires.

.EXAMPLE
  # one-off dev cert, then sign the Release build
  powershell -ExecutionPolicy Bypass -File .\tools\sign.ps1 -SelfSigned
  powershell -ExecutionPolicy Bypass -File .\tools\sign.ps1 -Thumbprint 1A2B3C...

.EXAMPLE
  # CI / release zip contents
  powershell -ExecutionPolicy Bypass -File .\tools\sign.ps1 -Pfx .\cert.pfx -PfxPassword $env:PFX_PW -Path .\dist
#>
[CmdletBinding(DefaultParameterSetName = 'Thumbprint')]
param(
    [Parameter(ParameterSetName = 'Thumbprint', Mandatory = $true)]
    [string]$Thumbprint,

    [Parameter(ParameterSetName = 'Pfx', Mandatory = $true)]
    [string]$Pfx,

    [Parameter(ParameterSetName = 'Pfx')]
    [string]$PfxPassword = '',

    [Parameter(ParameterSetName = 'SelfSigned')]
    [switch]$SelfSigned,

    # With -SelfSigned: also trust the dev cert in the current user's Root store.
    # Windows may show a one-time confirmation dialog for that store.
    [Parameter(ParameterSetName = 'SelfSigned')]
    [switch]$TrustRoot,

    # File or directory to sign. Defaults to the x64 Release output.
    [string]$Path,

    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Get-Certificate {
    if ($SelfSigned) {
        $subject = 'CN=Float Island Dev'
        $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $subject } |
            Sort-Object NotAfter -Descending | Select-Object -First 1

        if (-not $cert) {
            Write-Host "creating self-signed code-signing cert: $subject"
            $cert = New-SelfSignedCertificate `
                -Type CodeSigningCert `
                -Subject $subject `
                -KeyUsage DigitalSignature `
                -FriendlyName 'Float Island (dev)' `
                -CertStoreLocation Cert:\CurrentUser\My `
                -NotAfter (Get-Date).AddYears(3) `
                -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
        }
        Write-Host "cert thumbprint: $($cert.Thumbprint)"

        # Trust it for the current user so the OS (and AV) accept the signature.
        # TrustedPublisher is the one that matters and needs no dialog. Root (only with
        # -TrustRoot) may show a one-time confirmation dialog - that is Windows itself.
        $cer = Join-Path $env:TEMP 'floatisland-dev.cer'
        Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
        $dest = "Cert:\CurrentUser\TrustedPublisher"
        if (-not (Get-ChildItem $dest | Where-Object Thumbprint -eq $cert.Thumbprint)) {
            Write-Host "trusting cert in $dest"
            Import-Certificate -FilePath $cer -CertStoreLocation $dest | Out-Null
        }
        if ($TrustRoot) {
            # LocalMachine\Root takes no confirmation but needs admin; the per-user Root
            # store shows a one-time "install this certificate?" dialog instead.
            $elevated = ([Security.Principal.WindowsPrincipal] `
                [Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)

            $dest = if ($elevated) { 'Cert:\LocalMachine\Root' } else { 'Cert:\CurrentUser\Root' }
            if (-not (Get-ChildItem $dest | Where-Object Thumbprint -eq $cert.Thumbprint)) {
                Write-Host "trusting cert in $dest$(if (-not $elevated) { ' (Windows will ask once - click Yes)' })"
                Import-Certificate -FilePath $cer -CertStoreLocation $dest | Out-Null
            }
        }
        Remove-Item $cer -Force -ErrorAction SilentlyContinue
        return $cert
    }

    if ($Pfx) {
        $full = (Resolve-Path $Pfx).Path
        $secure = if ($PfxPassword) { ConvertTo-SecureString $PfxPassword -AsPlainText -Force } else { $null }
        return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($full, $secure)
    }

    $found = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
        Where-Object { $_.Thumbprint -eq ($Thumbprint -replace '\s', '') }
    if (-not $found) { throw "no certificate with thumbprint $Thumbprint in My stores" }
    return $found | Select-Object -First 1
}

function Sign-One([System.IO.FileInfo]$file, $cert) {
    $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert `
        -HashAlgorithm SHA256 -TimestampServer $TimestampServer

    if ($result.Status -ne 'Valid') {
        # Some networks block the timestamp authority; a signature without one is still valid now.
        $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -HashAlgorithm SHA256
    }
    $check = Get-AuthenticodeSignature -FilePath $file.FullName
    "{0,-28} {1,-8} {2}" -f $file.Name, $check.Status, $check.SignerCertificate.Subject
    if ($check.Status -notin @('Valid', 'UnknownError')) { throw "signing failed: $($check.Status)" }
}

$cert = Get-Certificate
if ($cert.NotAfter -lt (Get-Date)) { throw "certificate expired on $($cert.NotAfter)" }

if (-not $Path) { $Path = Join-Path $root 'bin\x64\Release\net10.0-windows10.0.19041.0\win-x64' }
if (-not (Test-Path $Path)) { throw "path not found: $Path" }
$Path = (Resolve-Path $Path).Path

if (Test-Path -PathType Container $Path) {
    $targets = Get-ChildItem $Path -Recurse -Include FloatIsland.exe, FloatIsland.dll
} else {
    $targets = @(Get-Item $Path)
}
if (-not $targets) { throw "no FloatIsland binaries under $Path" }

Write-Host "signing with: $($cert.Subject)"
foreach ($t in $targets) { Sign-One $t $cert }
Write-Host "done - $($targets.Count) file(s) signed"
