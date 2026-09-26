# Builds both installers into dist\:
#   ClaudeSpend-Setup-<version>.exe     Windows (x64) — per-user install, no admin needed
#   ClaudeSpend-Linux-<version>.run     Linux (x86-64 and ARM64) — self-extracting installer
#
# Needs: .NET 8 SDK (downloads NuGet packages on first run) and Python 3 (for the Linux package).
# Version: edit <Version> in Directory.Build.props.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }

Step "Claude Code Spend $version"
if (Test-Path build) { Remove-Item -Recurse -Force build }
New-Item -ItemType Directory -Force dist | Out-Null

Step "Windows app (self-contained)"
dotnet publish ClaudeSpend -c Release -o build\win-x64 --nologo -v q; Check "Windows app"

foreach ($rid in 'linux-x64', 'linux-arm64') {
    Step "Linux app ($rid)"
    dotnet publish ClaudeSpend.Linux -c Release -r $rid -o "build\$rid" --nologo -v q; Check "Linux app $rid"
}

Step "Windows uninstaller"
dotnet build installer\windows\Uninstall -c Release -o build\uninstaller --nologo -v q; Check "Uninstaller"

Step "Windows setup"
dotnet build installer\windows\Setup -c Release -o build\setup --nologo -v q; Check "Setup"
Copy-Item build\setup\ClaudeSpend-Setup.exe "dist\ClaudeSpend-Setup-$version.exe" -Force

Step "Linux installer"
python installer\linux\package.py --version $version --build build --ico ClaudeSpend\app.ico --out "dist\ClaudeSpend-Linux-$version.run"
Check "Linux installer"

Step "Done"
Get-ChildItem dist | Where-Object Name -like "*$version*" | ForEach-Object {
    "{0,-36} {1,7:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
