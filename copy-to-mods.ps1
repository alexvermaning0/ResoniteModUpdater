# Copies the built ModUpdater.dll into the Resonite rml_mods folder.
# Usage:  powershell -ExecutionPolicy Bypass -File .\copy-to-mods.ps1
# If the Resonite install is elsewhere, pass -ResonitePath "D:\Path\To\Resonite".
param(
    [string]$ResonitePath = "C:\Program Files (x86)\Steam\steamapps\common\Resonite",
    [string]$Configuration = "Release"
)

$dll = Join-Path $PSScriptRoot "bin\$Configuration\ModUpdater.dll"
$dest = Join-Path $ResonitePath "rml_mods"

if (-not (Test-Path $dll)) {
    Write-Error "Build output not found: $dll`nRun: dotnet build -c $Configuration"
    exit 1
}
if (-not (Test-Path $dest)) {
    Write-Error "rml_mods folder not found: $dest"
    exit 1
}

Copy-Item $dll $dest -Force
Write-Host "Copied ModUpdater.dll -> $dest"
Write-Host "Restart Resonite to load the mod."
