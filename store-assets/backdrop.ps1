# A blank full-screen panel, used only as a screenshot backdrop.
#
# The pill and the prompt picker are translucent by design, so whatever happens to be on screen behind
# them bleeds into the capture (a browser window showed through the first pass). Run this behind them
# and the bleed is a flat neutral tone instead. Kill the process to dismiss it.
#
# Launch it WITHOUT -WindowStyle Hidden: that flag lands in STARTUPINFO and suppresses the process's
# first top-level window — the form itself — so the backdrop silently never appears. The console window
# is hidden here instead.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Name Con -Namespace W32 -MemberDefinition @"
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
"@
[W32.Con]::ShowWindow([W32.Con]::GetConsoleWindow(), 0) | Out-Null   # SW_HIDE

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.WindowState = 'Maximized'
$form.BackColor = [System.Drawing.Color]::FromArgb(243, 244, 248)
$form.ShowInTaskbar = $false
[System.Windows.Forms.Application]::Run($form)
