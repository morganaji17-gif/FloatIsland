# Creates Start Menu / Desktop shortcuts for the SonoIsland.exe sitting next to this
# script. Intended for the release zip: unzip, then run this once.
#   powershell -ExecutionPolicy Bypass -File .\Shortcuts.ps1
#   powershell -ExecutionPolicy Bypass -File .\Shortcuts.ps1 -Startup
param([switch]$Startup)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'SonoIsland.exe'
if (-not (Test-Path $exe)) { throw "SonoIsland.exe not found next to this script ($PSScriptRoot)" }

$shell = New-Object -ComObject WScript.Shell
function New-Lnk($path, $arguments) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.Arguments = $arguments
    $lnk.WorkingDirectory = $PSScriptRoot
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Sono Island - dynamic island for Spotify'
    $lnk.Save()
    Write-Host "shortcut: $path"
}

New-Lnk "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Sono Island.lnk" '--skia'
New-Lnk (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Sono Island.lnk') '--skia'
if ($Startup) { New-Lnk (Join-Path ([Environment]::GetFolderPath('Startup')) 'Sono Island.lnk') '--skia' }

Write-Host ""
Write-Host "Done. Launch 'Sono Island' from the Start Menu or Desktop."
Write-Host "It is hidden until Spotify is playing."
