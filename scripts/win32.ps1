param(
  [Parameter(Mandatory=$true)][string]$Action,
  [double]$X = 0,
  [double]$Y = 0,
  [double]$ToX = 0,
  [double]$ToY = 0,
  [string]$Button = 'left',
  [int]$Count = 1,
  [int]$Delta = -120,
  [int]$DurationMs = 350,
  [string]$Key = '',
  [string]$Text = '',
  [int]$IntervalMs = 8,
  [string]$Ctrl = '0',
  [string]$Alt = '0',
  [string]$Shift = '0',
  [string]$Focus = '1',
  [string]$IncludeBorder = '0',
  [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$ctrlEnabled = $Ctrl -match '^(1|true)$'
$altEnabled = $Alt -match '^(1|true)$'
$shiftEnabled = $Shift -match '^(1|true)$'
$focusEnabled = $Focus -match '^(1|true)$'
$includeBorderEnabled = $IncludeBorder -match '^(1|true)$'

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class AcsWin32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion {
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
  }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

  public static IntPtr FindTopLevelWindow(int processId) {
    IntPtr first = IntPtr.Zero;
    EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
      uint candidateProcessId;
      GetWindowThreadProcessId(hWnd, out candidateProcessId);
      if (candidateProcessId != (uint)processId) return true;
      if (first == IntPtr.Zero) first = hWnd;
      if (!IsWindowVisible(hWnd)) return true;
      first = hWnd;
      return false;
    }, IntPtr.Zero);
    return first;
  }

  public static void UnicodeText(string text, int intervalMs) {
    foreach (char c in text) {
      INPUT down = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 } } };
      INPUT up = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 | 0x0002 } } };
      SendInput(2, new [] { down, up }, Marshal.SizeOf(typeof(INPUT)));
      if (intervalMs > 0) System.Threading.Thread.Sleep(intervalMs);
    }
  }
}
'@

function Write-Result([hashtable]$Value) {
  $Value | ConvertTo-Json -Compress -Depth 8
}

function Find-Game {
  $matches = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -like '*Amazing*Cultivation*' -or (
      $_.MainWindowTitle -like '*Amazing Cultivation Simulator*' -or
      $_.MainWindowTitle -like '*了不起的修仙模拟器*'
    )
  })
  if ($matches.Count -eq 0) { throw 'Amazing Cultivation Simulator window was not found' }
  $process = $matches[0]
  $handle = [IntPtr]$process.MainWindowHandle
  if ($handle -eq [IntPtr]::Zero) { $handle = [AcsWin32]::FindTopLevelWindow($process.Id) }
  if ($handle -eq [IntPtr]::Zero) { throw 'Amazing Cultivation Simulator window handle was not found' }
  return [pscustomobject]@{
    Id = $process.Id
    ProcessName = $process.ProcessName
    MainWindowTitle = $process.MainWindowTitle
    MainWindowHandle = $handle.ToInt64()
  }
}

function Get-Geometry($Process) {
  $handle = [IntPtr]$Process.MainWindowHandle
  $window = New-Object AcsWin32+RECT
  $client = New-Object AcsWin32+RECT
  $origin = New-Object AcsWin32+POINT
  if (-not [AcsWin32]::GetWindowRect($handle, [ref]$window)) { throw 'GetWindowRect failed' }
  if (-not [AcsWin32]::GetClientRect($handle, [ref]$client)) { throw 'GetClientRect failed' }
  if (-not [AcsWin32]::ClientToScreen($handle, [ref]$origin)) { throw 'ClientToScreen failed' }
  return @{
    handle = $handle.ToInt64()
    window = @{ x=$window.Left; y=$window.Top; width=$window.Right-$window.Left; height=$window.Bottom-$window.Top }
    client = @{ x=$origin.X; y=$origin.Y; width=$client.Right-$client.Left; height=$client.Bottom-$client.Top }
    minimized = [AcsWin32]::IsIconic($handle)
  }
}

function Focus-Game($Process) {
  $handle = [IntPtr]$Process.MainWindowHandle
  if ([AcsWin32]::IsIconic($handle)) { [void][AcsWin32]::ShowWindow($handle, 9) }
  [void][AcsWin32]::SetForegroundWindow($handle)
  Start-Sleep -Milliseconds 100
}

function Client-Point($Geometry, [double]$ClientX, [double]$ClientY) {
  $width = [int]$Geometry.client.width
  $height = [int]$Geometry.client.height
  $cx = [int][Math]::Round($ClientX)
  $cy = [int][Math]::Round($ClientY)
  if ($cx -lt 0 -or $cy -lt 0 -or $cx -ge $width -or $cy -ge $height) {
    throw "Point ($cx,$cy) is outside client area ${width}x${height}"
  }
  return @{ x=[int]$Geometry.client.x+$cx; y=[int]$Geometry.client.y+$cy; clientX=$cx; clientY=$cy }
}

function Mouse-Flags([string]$Name, [bool]$Down) {
  switch ($Name.ToLowerInvariant()) {
    'left' { if ($Down) { return 0x0002 } else { return 0x0004 } }
    'right' { if ($Down) { return 0x0008 } else { return 0x0010 } }
    'middle' { if ($Down) { return 0x0020 } else { return 0x0040 } }
    default { throw "Unsupported mouse button: $Name" }
  }
}

function Resolve-Key([string]$Name) {
  $upper = $Name.Trim().ToUpperInvariant()
  $map = @{
    'BACKSPACE'=0x08; 'TAB'=0x09; 'ENTER'=0x0D; 'RETURN'=0x0D; 'ESC'=0x1B; 'ESCAPE'=0x1B; 'SPACE'=0x20
    'PAGEUP'=0x21; 'PAGEDOWN'=0x22; 'END'=0x23; 'HOME'=0x24; 'LEFT'=0x25; 'UP'=0x26; 'RIGHT'=0x27; 'DOWN'=0x28
    'DELETE'=0x2E; 'INSERT'=0x2D; 'PAUSE'=0x13; 'PLUS'=0xBB; 'MINUS'=0xBD
  }
  if ($map.ContainsKey($upper)) { return [byte]$map[$upper] }
  if ($upper -match '^F([1-9]|1[0-2])$') { return [byte](0x6F + [int]$Matches[1]) }
  if ($upper.Length -eq 1) { return [byte][char]$upper }
  throw "Unsupported key: $Name"
}

try {
  if ($Action -eq 'status') {
    try { $process = Find-Game } catch {
      Write-Result @{ ok=$true; running=$false; error=$_.Exception.Message }
      exit 0
    }
    $geometry = Get-Geometry $process
    Write-Result @{ ok=$true; running=$true; pid=$process.Id; processName=$process.ProcessName; title=$process.MainWindowTitle; geometry=$geometry }
    exit 0
  }

  $process = Find-Game
  if ($focusEnabled -or $Action -eq 'focus') { Focus-Game $process }
  $geometry = Get-Geometry $process

  switch ($Action) {
    'focus' {
      Write-Result @{ ok=$true; focused=$true; pid=$process.Id; geometry=$geometry }
    }
    'capture' {
      if ([string]::IsNullOrWhiteSpace($OutputPath)) { throw 'OutputPath is required' }
      $area = if ($includeBorderEnabled) { $geometry.window } else { $geometry.client }
      if ($area.width -le 0 -or $area.height -le 0) { throw 'Game window has an empty capture area' }
      $bitmap = New-Object System.Drawing.Bitmap($area.width, $area.height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
      $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
      try {
        $graphics.CopyFromScreen($area.x, $area.y, 0, 0, $bitmap.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
      } finally {
        $graphics.Dispose(); $bitmap.Dispose()
      }
      Write-Result @{ ok=$true; captured=$true; width=$area.width; height=$area.height; coordinateSpace= $(if ($includeBorderEnabled) {'screen-window'} else {'client'}); pid=$process.Id }
    }
    'move' {
      $point = Client-Point $geometry $X $Y
      [void][AcsWin32]::SetCursorPos($point.x, $point.y)
      Write-Result @{ ok=$true; moved=$true; point=@{x=$point.clientX;y=$point.clientY}; pid=$process.Id }
    }
    'click' {
      if ($Count -lt 1 -or $Count -gt 3) { throw 'Count must be between 1 and 3' }
      $point = Client-Point $geometry $X $Y
      [void][AcsWin32]::SetCursorPos($point.x, $point.y)
      for ($i=0; $i -lt $Count; $i++) {
        [AcsWin32]::mouse_event((Mouse-Flags $Button $true), 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 35
        [AcsWin32]::mouse_event((Mouse-Flags $Button $false), 0, 0, 0, [UIntPtr]::Zero)
        if ($i + 1 -lt $Count) { Start-Sleep -Milliseconds 90 }
      }
      Write-Result @{ ok=$true; clicked=$true; point=@{x=$point.clientX;y=$point.clientY}; button=$Button; count=$Count; pid=$process.Id }
    }
    'drag' {
      $from = Client-Point $geometry $X $Y
      $to = Client-Point $geometry $ToX $ToY
      [void][AcsWin32]::SetCursorPos($from.x, $from.y)
      [AcsWin32]::mouse_event((Mouse-Flags $Button $true), 0, 0, 0, [UIntPtr]::Zero)
      $steps = [Math]::Max(2, [Math]::Min(60, [int]($DurationMs / 16)))
      for ($i=1; $i -le $steps; $i++) {
        $sx = [int][Math]::Round($from.x + (($to.x-$from.x) * $i / $steps))
        $sy = [int][Math]::Round($from.y + (($to.y-$from.y) * $i / $steps))
        [void][AcsWin32]::SetCursorPos($sx, $sy)
        Start-Sleep -Milliseconds ([Math]::Max(1, [int]($DurationMs / $steps)))
      }
      [AcsWin32]::mouse_event((Mouse-Flags $Button $false), 0, 0, 0, [UIntPtr]::Zero)
      Write-Result @{ ok=$true; dragged=$true; from=@{x=$from.clientX;y=$from.clientY}; to=@{x=$to.clientX;y=$to.clientY}; button=$Button; pid=$process.Id }
    }
    'scroll' {
      if ($PSBoundParameters.ContainsKey('X') -or $PSBoundParameters.ContainsKey('Y')) {
        $point = Client-Point $geometry $X $Y
        [void][AcsWin32]::SetCursorPos($point.x, $point.y)
      }
      $wheelData = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int32]$Delta), 0)
      [AcsWin32]::mouse_event(0x0800, 0, 0, $wheelData, [UIntPtr]::Zero)
      Write-Result @{ ok=$true; scrolled=$true; delta=$Delta; pid=$process.Id }
    }
    'key' {
      $vk = Resolve-Key $Key
      $modifiers = @()
      if ($ctrlEnabled) { $modifiers += [byte]0x11 }
      if ($altEnabled) { $modifiers += [byte]0x12 }
      if ($shiftEnabled) { $modifiers += [byte]0x10 }
      foreach ($modifier in $modifiers) { [AcsWin32]::keybd_event($modifier, 0, 0, [UIntPtr]::Zero) }
      [AcsWin32]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
      Start-Sleep -Milliseconds 35
      [AcsWin32]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
      [array]::Reverse($modifiers)
      foreach ($modifier in $modifiers) { [AcsWin32]::keybd_event($modifier, 0, 2, [UIntPtr]::Zero) }
      Write-Result @{ ok=$true; pressed=$Key; ctrl=$ctrlEnabled; alt=$altEnabled; shift=$shiftEnabled; pid=$process.Id }
    }
    'type' {
      [AcsWin32]::UnicodeText($Text, [Math]::Max(0, [Math]::Min(1000, $IntervalMs)))
      Write-Result @{ ok=$true; typed=$true; length=$Text.Length; pid=$process.Id }
    }
    default { throw "Unknown action: $Action" }
  }
} catch {
  Write-Result @{ ok=$false; action=$Action; error=$_.Exception.Message }
  exit 1
}
