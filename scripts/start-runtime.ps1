param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$entry = Join-Path $projectRoot 'src\runtime.mjs'
$existing = Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($entry) }
if ($existing) {
  Write-Output "Runtime is already running (PID $($existing.ProcessId))."
  exit 0
}
$process = Start-Process -FilePath 'node.exe' -ArgumentList @($entry) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 500
if ($process.HasExited) { throw "Runtime exited with code $($process.ExitCode)" }
Write-Output "Runtime started (PID $($process.Id))."
