# Captures a single Jot window to a PNG at its true (DWM) bounds — app only, no desktop.
# Used to produce Microsoft Store listing screenshots.
#
#   .\capture.ps1 -JotArgs "--show"     -Select main   -Out main.png
#   .\capture.ps1 -JotArgs "--pilldemo" -Select other  -Out pill.png
#   .\capture.ps1 -JotArgs "--settings" -Select main   -Out settings.png -Keys "{PGDN}"
param(
    [string]$JotArgs = "--show",
    [ValidateSet("main","other")] [string]$Select = "main",
    [Parameter(Mandatory)] [string]$Out,
    [int]$Wait = 9,
    [string]$Keys = "",          # optional keystrokes to send after focusing (e.g. scroll)
    [int]$Pad = 0                # extra pixels to shave off each edge (drop shadow) if needed
)

$exe = "C:\Users\vinee\projects\jot-windows\src\Jot\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\Jot.exe"

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc e, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static List<IntPtr> ForPid(uint want) {
        var list = new List<IntPtr>();
        EnumWindows((h,p) => { uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid==want && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static RECT Frame(IntPtr h){ RECT r; DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))); return r; }
}
"@ -ReferencedAssemblies System.Runtime.InteropServices, System.Collections

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Get-Process Jot -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$p = Start-Process $exe -ArgumentList $JotArgs -PassThru
Start-Sleep -Seconds $Wait
$p.Refresh()

# Pick the target window
$hwnd = [IntPtr]::Zero
if ($Select -eq "main") {
    $hwnd = (Get-Process -Id $p.Id).MainWindowHandle
} else {
    # borderless overlay (pill/picker): the visible top-level window that isn't the main window
    $main = (Get-Process -Id $p.Id).MainWindowHandle
    $wins = [Win]::ForPid([uint32]$p.Id) | Where-Object { $_ -ne $main }
    # choose the largest such window (avoids stray 0-size helper hwnds)
    $best = $null; $bestArea = -1
    foreach ($w in $wins) { $r=[Win]::Frame($w); $a=($r.R-$r.L)*($r.B-$r.T); if ($a -gt $bestArea){$bestArea=$a;$best=$w} }
    $hwnd = $best
}
if (-not $hwnd -or $hwnd -eq [IntPtr]::Zero) { Write-Output "NO WINDOW FOUND"; exit 1 }

[Win]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500
if ($Keys) { [System.Windows.Forms.SendKeys]::SendWait($Keys); Start-Sleep -Milliseconds 800 }

$r = [Win]::Frame($hwnd)
$x = $r.L + $Pad; $y = $r.T + $Pad
$w = ($r.R - $r.L) - 2*$Pad; $h = ($r.B - $r.T) - 2*$Pad
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$g.Dispose()
$dir = Split-Path $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out  ($w x $h)"
