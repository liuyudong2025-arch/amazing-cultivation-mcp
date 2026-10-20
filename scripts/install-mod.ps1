param(
  [string]$GameRoot = $(if ($env:ACS_ROOT) { $env:ACS_ROOT } else { 'C:\Program Files (x86)\Steam\steamapps\common\AmazingCultivationSimulator' })
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'mod\AmazingCultivationMCP'
$modsRoot = Join-Path $GameRoot 'Mods'
$destination = Join-Path $modsRoot 'AmazingCultivationMCP'

if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'Amazing Cultivation Simulator.exe') -PathType Leaf)) {
  throw "Game executable was not found under: $GameRoot"
}
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
  throw "Bundled mod source was not found: $source"
}

& (Join-Path $PSScriptRoot 'build-bridge.ps1') -GameRoot $GameRoot

New-Item -ItemType Directory -Path $destination -Force | Out-Null
$sourceInfoPath = Join-Path $source 'Info.json'
$destinationInfoPath = Join-Path $destination 'Info.json'

# Keep the fields written by the game's Workshop publisher (especially ID),
# while refreshing the fields owned by this source package.
if (Test-Path -LiteralPath $destinationInfoPath -PathType Leaf) {
  $utf8 = New-Object System.Text.UTF8Encoding($false)
  $sourceInfo = [System.IO.File]::ReadAllText($sourceInfoPath, $utf8) | ConvertFrom-Json
  $installedInfo = [System.IO.File]::ReadAllText($destinationInfoPath, $utf8) | ConvertFrom-Json
  foreach ($property in $sourceInfo.PSObject.Properties) {
    if ($installedInfo.PSObject.Properties.Name -contains $property.Name) {
      $installedInfo.$($property.Name) = $property.Value
    } else {
      $installedInfo | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value
    }
  }
  [System.IO.File]::WriteAllText($destinationInfoPath, ($installedInfo | ConvertTo-Json -Depth 20), $utf8)
} else {
  Copy-Item -LiteralPath $sourceInfoPath -Destination $destinationInfoPath -Force
}
Copy-Item -LiteralPath (Join-Path $source 'AmazingCultivationMCP.Bridge.dll') -Destination (Join-Path $destination 'AmazingCultivationMCP.Bridge.dll') -Force
New-Item -ItemType Directory -Path (Join-Path $destination 'Scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'Scripts\AmazingCultivationMCP.lua') -Destination (Join-Path $destination 'Scripts\AmazingCultivationMCP.lua') -Force
$preview = Join-Path $projectRoot 'assets\workshop-preview-under-1mb.png'
if (Test-Path -LiteralPath $preview -PathType Leaf) {
  Copy-Item -LiteralPath $preview -Destination (Join-Path $destination 'Preview.png') -Force
}

Write-Output "Installed game bridge Mod to: $destination"
Write-Output 'Enable Amazing Cultivation MCP in the in-game mod manager, then restart the game.'
