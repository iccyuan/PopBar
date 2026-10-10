# Simulates a text selection in Notepad and screenshots the result.
# Usage: pwsh tools/test-popup.ps1 [-Mode dblclick|drag] [-Out path.png]
param([string]$Mode = "dblclick", [string]$Out = "$env:TEMP\popbar-test.png", [int]$StartX = 60, [int]$DragSteps = 12)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class U {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[U]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null   # PER_MONITOR_AWARE_V2

$file = "$env:TEMP\popbar-sample-$(Get-Date -Format HHmmss).txt"
Set-Content $file "Hello PopBar world. Visit https://example.com or mail test@example.com for details." -Encoding UTF8
$np = Start-Process notepad.exe -ArgumentList $file -PassThru
Start-Sleep 2
$h = (Get-Process -Id $np.Id).MainWindowHandle
if ($h -eq 0) { $h = (Get-Process notepad | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1).MainWindowHandle }
[U]::MoveWindow($h, 100, 100, 900, 500, $true) | Out-Null
[U]::SetForegroundWindow($h) | Out-Null
Start-Sleep 1
$r = New-Object U+RECT
[U]::GetWindowRect($h, [ref]$r) | Out-Null

# text area: Win11 Notepad has a tab strip + menu (~95px); first line sits around +110
$x = $r.L + $StartX; $y = $r.T + 112
[U]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 200
$DOWN = 0x2; $UP = 0x4
if ($Mode -eq "drag") {
    [U]::mouse_event($DOWN, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80
    for ($i = 1; $i -le $DragSteps; $i++) { [U]::SetCursorPos($x + $i * 15, $y) | Out-Null; Start-Sleep -Milliseconds 15 }
    [U]::mouse_event($UP, 0, 0, 0, [UIntPtr]::Zero)
} else {
    [U]::mouse_event($DOWN, 0, 0, 0, [UIntPtr]::Zero); [U]::mouse_event($UP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [U]::mouse_event($DOWN, 0, 0, 0, [UIntPtr]::Zero); [U]::mouse_event($UP, 0, 0, 0, [UIntPtr]::Zero)
}
Start-Sleep -Milliseconds 900

$bmp = New-Object System.Drawing.Bitmap 900, 300
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $Out (cursor at $x,$y)"
