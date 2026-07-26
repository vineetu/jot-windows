# Captures the full screenshot set for the Store listing in one pass.
#
# Swaps the real library.json for store-assets\demo-library.json (meaningful transcripts instead of
# whatever the machine happens to hold), captures every surface, then puts the real one back —
# the restore also runs on Ctrl+C/failure, so a half-finished run never leaves demo data in place.
param(
    [string]$OutDir = "C:\Users\vinee\projects\jot-windows\store-assets\screenshots-1.2.1",
    [switch]$KeepDemoData          # skip the restore (useful while iterating on a single shot)
)

$here    = Split-Path $MyInvocation.MyCommand.Path
$capture = Join-Path $here "capture.ps1"
$demo    = Join-Path $here "demo-library.json"
$jotDir  = Join-Path $env:LOCALAPPDATA "Jot"
$live    = Join-Path $jotDir "library.json"
$live2   = Join-Path $jotDir "settings.json"

# Snapshot the CURRENT live files and restore from that snapshot — never from a fixed backup
# path. A checked-in backup goes stale the moment the user changes a setting, and this script's
# finally block would then quietly roll their real settings back to whenever it was made.
$runBackup = Join-Path $env:TEMP ("jot-capture-restore-" + (Get-Process -Id $PID).StartTime.ToString("yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $runBackup | Out-Null
Copy-Item $live  (Join-Path $runBackup "library.json")  -Force
Copy-Item $live2 (Join-Path $runBackup "settings.json") -Force
$backup = Join-Path $runBackup "library.json"
$setBk  = Join-Path $runBackup "settings.json"
Write-Output "live settings/library snapshotted to $runBackup"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Each row: name, Jot args, which window to grab, extra settle seconds, blank-screen backdrop.
# b=$true for the translucent overlays only — without it, whatever is on screen bleeds through them.
$shots = @(
    @{ n="01-recents";          a='--show';                    w="main";  s=10 },
    @{ n="02-detail-dictation"; a='--detail 1';                w="main";  s=11 },
    @{ n="03-detail-transform"; a='--detail 2';                w="main";  s=11 },
    @{ n="04-pill";             a='--pilldemo';                w="other"; s=12; b=$true },
    @{ n="05-pill-expanded";    a='--pilldemo --expanded';     w="other"; s=14; b=$true },
    @{ n="06-transform-picker"; a='--pickerdemo';              w="other"; s=10; b=$true },
    @{ n="07-transform-voice";  a='--pickerdemo --augment "make it friendlier and about half as long"'; w="other"; s=10; b=$true },
    @{ n="08-shortcuts";        a='--shortcuts';               w="main";  s=11 },
    @{ n="09-prompts";          a='--page prompts';            w="main";  s=11 },
    @{ n="10-settings";         a='--settings';                w="main";  s=11 },
    @{ n="11-help";             a='--page help';               w="main";  s=11 },
    @{ n="12-ask-jot";          a='--page askjot';             w="main";  s=11 },
    @{ n="13-quick-tour";       a='--tour getting-started';    w="other"; s=10 },
    @{ n="14-about";            a='--page about';              w="main";  s=11 }
)

try {
    Copy-Item $demo $live -Force
    Write-Output "demo library installed"

    foreach ($shot in $shots) {
        $out = Join-Path $OutDir ($shot.n + ".png")
        $result = & $capture -JotArgs $shot.a -Select $shot.w -Out $out -Wait $shot.s -Backdrop:([bool]$shot.b)
        Write-Output ("{0,-20} {1}" -f $shot.n, $result)
    }
}
finally {
    Get-Process Jot -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
    if (-not $KeepDemoData) {
        Copy-Item $backup $live -Force
        Copy-Item $setBk  $live2 -Force
        Write-Output "real library + settings restored"
    } else {
        Write-Output "LEFT DEMO DATA IN PLACE (-KeepDemoData)"
    }
}
