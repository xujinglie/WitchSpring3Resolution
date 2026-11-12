param(
 [Parameter(Mandatory = $true)][string]$GameDirectory,
 [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Name = 'cursor-bounds',
 [switch]$Sweep
)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$reportDirectory = Join-Path $gameRoot 'ModDev/resolution-reports'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CursorBoundsTest {
 public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr data);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
 [DllImport("user32.dll")] public static extern bool GetClipCursor(out Rect rect);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int command);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
[void][CursorBoundsTest]::SetThreadDpiAwarenessContext([IntPtr](-4))
$game = Get-Process -Name 'WitchSpring3 ReFine' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $game) { throw 'Game is not running. Start the game before testing cursor bounds.' }
$script:cursorGameWindow = [IntPtr]::Zero
$callback = [CursorBoundsTest+EnumProc]{ param($window,$data)
 [uint32]$owner = 0
 [void][CursorBoundsTest]::GetWindowThreadProcessId($window,[ref]$owner)
 $rect = New-Object CursorBoundsTest+Rect
 if ($owner -eq $game.Id -and [CursorBoundsTest]::GetClientRect($window,[ref]$rect) -and $rect.Right -gt 600 -and $rect.Bottom -gt 400) { $script:cursorGameWindow = $window }
 return $true
}
[void][CursorBoundsTest]::EnumWindows($callback,[IntPtr]::Zero)
$window = $script:cursorGameWindow
if ($window -eq [IntPtr]::Zero) { throw 'Game window not found.' }
[void][CursorBoundsTest]::ShowWindow($window,5)
[void][CursorBoundsTest]::SetForegroundWindow($window)
Start-Sleep -Milliseconds 800
$client = New-Object CursorBoundsTest+Rect
$origin = New-Object CursorBoundsTest+Point
$clip = New-Object CursorBoundsTest+Rect
if (-not [CursorBoundsTest]::GetClientRect($window,[ref]$client) -or
    -not [CursorBoundsTest]::ClientToScreen($window,[ref]$origin) -or
    -not [CursorBoundsTest]::GetClipCursor([ref]$clip)) { throw 'Unable to read cursor or client bounds.' }
$report = [ordered]@{ process=$game.Id; foreground=([CursorBoundsTest]::GetForegroundWindow() -eq $window); client=@($origin.X,$origin.Y,($origin.X+$client.Right),($origin.Y+$client.Bottom)); clip=@($clip.Left,$clip.Top,$clip.Right,$clip.Bottom); samples=@() }
if ($Sweep) {
 if (-not $report.foreground) { throw 'Game is not in the foreground. Cursor sweep cancelled.' }
 $saved = New-Object CursorBoundsTest+Point
 [void][CursorBoundsTest]::GetCursorPos([ref]$saved)
 try {
  $points = @(@(2,2),@([int]($client.Right/2),2),@(($client.Right-3),2),@(2,[int]($client.Bottom/2)),@(($client.Right-3),[int]($client.Bottom/2)),@(2,($client.Bottom-3)),@([int]($client.Right/2),($client.Bottom-3)),@(($client.Right-3),($client.Bottom-3)))
  foreach ($p in $points) {
   if ([CursorBoundsTest]::GetForegroundWindow() -ne $window) { throw 'Game lost focus. Cursor sweep cancelled.' }
   if (-not [CursorBoundsTest]::SetCursorPos(($origin.X+$p[0]),($origin.Y+$p[1]))) { throw 'Unable to move the cursor to the test point.' }
   Start-Sleep -Milliseconds 120
   $actual = New-Object CursorBoundsTest+Point
   if (-not [CursorBoundsTest]::GetCursorPos([ref]$actual)) { throw 'Unable to read actual cursor position.' }
   $report.samples += [ordered]@{ requested=$p; actual=@(($actual.X-$origin.X),($actual.Y-$origin.Y)); pass=([Math]::Abs($actual.X-$origin.X-$p[0]) -le 1 -and [Math]::Abs($actual.Y-$origin.Y-$p[1]) -le 1) }
  }
 } finally { [void][CursorBoundsTest]::SetCursorPos($saved.X,$saved.Y) }
}
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$destination = Join-Path $reportDirectory ($Name + '.json')
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8
$report | ConvertTo-Json -Depth 8
