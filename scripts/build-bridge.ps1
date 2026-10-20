param(
  [string]$GameRoot = $(if ($env:ACS_ROOT) { $env:ACS_ROOT } else { 'C:\Program Files (x86)\Steam\steamapps\common\AmazingCultivationSimulator' })
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $GameRoot 'Amazing Cultivation Simulator_Data\Managed'
$compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$output = Join-Path $projectRoot 'mod\AmazingCultivationMCP\AmazingCultivationMCP.Bridge.dll'
$source = Join-Path $projectRoot 'bridge\Bridge.cs'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "C# compiler was not found: $compiler" }
if (-not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -PathType Leaf)) { throw "Game managed assemblies were not found: $managed" }

$references = @(
  'mscorlib.dll',
  'System.dll',
  'System.Core.dll',
  'Assembly-CSharp.dll',
  'Assembly-CSharp-firstpass.dll',
  'UnityEngine.dll',
  'UnityEngine.CoreModule.dll',
  'Newtonsoft.Json.dll'
) | ForEach-Object { '/reference:' + (Join-Path $managed $_) }

& $compiler /noconfig /nologo /nostdlib+ /target:library /optimize+ ('/out:' + $output) $references $source
if ($LASTEXITCODE -ne 0) { throw "Bridge compilation failed with exit code $LASTEXITCODE" }
Write-Output "Built: $output"
