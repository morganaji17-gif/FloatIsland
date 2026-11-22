# Publishes Sono Island and installs a double-clickable shortcut.
#   powershell -ExecutionPolicy Bypass -File install.ps1
param(
    [string]$Dest = "$env:LOCALAPPDATA\SonoIsland-App",
    [switch]$Startup
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "Publishing to $Dest ..."
dotnet publish "$root\SonoIsland.csproj" -c Release -p:Platform=x64 -r win-x64 --self-contained false -o $Dest | Out-Host

$exe = Join-Path $Dest 'SonoIsland.exe'
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }

$shell = New-Object -ComObject WScript.Shell
function New-S_Alias($path, $arguments) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.Arguments = $arguments
    $lnk.WorkingDirectory = $Dest
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Sono Island - Dynamic Island for Spotify'
    $lnk.Save()
    Write-Host "shortcut: $path"
}

New-S_Alias "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Sono Island.lnk" '--skia'
New-S_Alias (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Sono Island.lnk') '--skia'

if ($Startup) {
    $run = Join-Path ([Environment]::GetFolderPath('Startup')) 'Sono Island.lnk'
    New-S_Alias $run '--skia'
}

Write-Host ""
Write-Host "Done. Launch from the Start Menu / Desktop shortcut (runs the Skia renderer)."
Write-Host "For the WPF renderer instead, run: `"$exe`""
