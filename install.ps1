# Builds StallWatch (tray + CLI) in Release and installs it to a fixed folder, so the "Start with Windows" logon task
# keeps pointing at the same StallWatchTray.exe across rebuilds. Rerun after pulling changes to update.
#   .\install.ps1 [-Destination <folder>] [-NoStart]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\StallWatch'),
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stage = Join-Path ([IO.Path]::GetTempPath()) "stallwatch-publish-$PID"

dotnet publish "$root\src\StallWatch.Tray" -c Release -o $stage --nologo -v q
if ($LASTEXITCODE) { throw 'Publishing the tray app failed.' }
dotnet publish "$root\src\StallWatch.Cli" -c Release -o $stage --nologo -v q
if ($LASTEXITCODE) { throw 'Publishing the CLI failed.' }

# A running installed copy locks its files.
$running = Get-Process StallWatchTray, stallwatch -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) }
$running | Stop-Process -Force
$running | ForEach-Object { $_.WaitForExit(5000) | Out-Null }

New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item "$stage\*" $Destination -Recurse -Force
Remove-Item $stage -Recurse -Force
Write-Host "Installed to $Destination"
Write-Host "  tray: $Destination\StallWatchTray.exe  CLI: $Destination\stallwatch.exe"

if (-not $NoStart) { Start-Process (Join-Path $Destination 'StallWatchTray.exe') }
