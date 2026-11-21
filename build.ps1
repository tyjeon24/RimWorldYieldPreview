# Builds the mod (1.6 always; 1.7 when the game is installed, see Source/YieldPreview.csproj) and optionally installs it.
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
    [switch]$Install
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet build (Join-Path $root "Source\YieldPreview.csproj") -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

if ($Install) {
    $dest = Join-Path $GameDir "Mods\YieldPreview"
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory $dest | Out-Null
    foreach ($d in "About", "1.6", "1.7") {
        if (Test-Path (Join-Path $root $d)) { Copy-Item (Join-Path $root $d) $dest -Recurse }
    }
    Write-Host "Installed to $dest"
}
