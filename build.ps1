# Builds both installers into dist\:
#   ClaudeSpend-Setup-<version>.exe     Windows (x64) — per-user install, no admin needed
#   ClaudeSpend-Linux-<version>.run     Linux (x86-64 and ARM64) — self-extracting installer
#
# Needs: .NET 8 SDK (downloads NuGet packages on first run) and Python 3 (for the Linux package).
# Version: edit <Version> in Directory.Build.props.
#
# Code signing (optional): set ONE of
#   CLAUDE_SPEND_SIGN_THUMBPRINT               certificate in your Windows certificate store
#   CLAUDE_SPEND_SIGN_PFX (+ _PFX_PASSWORD)    certificate file
# and optionally CLAUDE_SPEND_TIMESTAMP_URL (default http://timestamp.digicert.com).
# The app and uninstaller are signed before they're packed into the setup, then the setup itself.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Copyright (C) 2026 Pacsy1. Free software under the GNU GPL v3 or later (see LICENSE).

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$version = @(([xml](Get-Content Directory.Build.props)).Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }

# ---- signing certificate (optional) ----
$cert = $null
$timestamp = if ($env:CLAUDE_SPEND_TIMESTAMP_URL) { $env:CLAUDE_SPEND_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
if ($env:CLAUDE_SPEND_SIGN_THUMBPRINT) {
    $tp = ($env:CLAUDE_SPEND_SIGN_THUMBPRINT -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert | Where-Object Thumbprint -eq $tp | Select-Object -First 1
    if (-not $cert) { throw "No code-signing certificate with thumbprint $tp in CurrentUser\My or LocalMachine\My." }
} elseif ($env:CLAUDE_SPEND_SIGN_PFX) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        (Resolve-Path $env:CLAUDE_SPEND_SIGN_PFX).Path, $env:CLAUDE_SPEND_SIGN_PFX_PASSWORD,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
}
if ($cert -and -not $cert.HasPrivateKey) { throw "The signing certificate has no private key available." }

function Sign($path) {
    if (-not $cert) { return }
    $r = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $timestamp
    $trusted = $r.Status -eq 'Valid'
    $ours = $r.SignerCertificate -and $r.SignerCertificate.Thumbprint -eq $cert.Thumbprint
    if (-not $ours) { throw "Signing $path failed: $($r.StatusMessage)" }
    if (-not $r.TimeStamperCertificate) { throw "Signing $path failed: no timestamp from $timestamp" }
    if (-not $trusted -and -not $env:CLAUDE_SPEND_SIGN_ALLOW_UNTRUSTED) {
        throw "Signed $path, but Windows doesn't trust the certificate ($($r.StatusMessage))."
    }
    Write-Host ("    signed {0}  ({1})" -f (Split-Path $path -Leaf), $cert.GetNameInfo('SimpleName', $false))
}

Step "Claude Code Spend $version"
if ($cert) { Write-Host "    Signing with: $($cert.Subject)  [$($cert.Thumbprint)]" }
else { Write-Host "    No signing certificate configured: building unsigned." -ForegroundColor Yellow }
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item -ItemType Directory -Force dist | Out-Null

Step "Windows app (self-contained)"
dotnet publish ClaudeSpend -c Release -o build\win-x64 --nologo -v q; Check "Windows app"
Sign build\win-x64\ClaudeSpend.exe

foreach ($rid in 'linux-x64', 'linux-arm64') {
    Step "Linux app ($rid)"
    dotnet publish ClaudeSpend.Linux -c Release -r $rid -o "build\$rid" --nologo -v q; Check "Linux app $rid"
}

Step "Windows uninstaller"
dotnet build installer\windows\Uninstall -c Release -o build\uninstaller --nologo -v q; Check "Uninstaller"
Sign build\uninstaller\Uninstall.exe

Step "Windows setup"
dotnet build installer\windows\Setup -c Release -o build\setup --nologo -v q; Check "Setup"
Sign build\setup\ClaudeSpend-Setup.exe
Copy-Item build\setup\ClaudeSpend-Setup.exe "dist\ClaudeSpend-Setup-$version.exe" -Force

Step "Linux installer"
python installer\linux\package.py --version $version --build build --ico ClaudeSpend\app.ico --out "dist\ClaudeSpend-Linux-$version.run"
Check "Linux installer"

Step "Checksums"
$sums = Get-ChildItem dist | Where-Object { $_.Name -like "ClaudeSpend-*-$version.*" } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllText("$PWD\dist\SHA256SUMS-$version.txt", ($sums -join "`n") + "`n")
$sums | ForEach-Object { "    $_" }

Step "Done"
Get-ChildItem dist | Where-Object Name -like "*$version*" | ForEach-Object {
    $label = ''
    if ($_.Extension -eq '.exe') {
        $s = Get-AuthenticodeSignature $_.FullName
        $label = if ($s.Status -eq 'Valid') { 'signed' } elseif ($s.SignerCertificate) { 'signed (certificate not trusted)' } else { 'unsigned' }
    }
    "{0,-36} {1,7:N1} MB  {2}" -f $_.Name, ($_.Length / 1MB), $label
}
