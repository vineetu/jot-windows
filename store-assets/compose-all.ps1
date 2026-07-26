# Frames every raw window capture in -Src onto a 1920x1080 canvas (Store minimum is 1366x768, and the
# raw app window is narrower than that), keeping compose.ps1's look: soft periwinkle gradient + shadow.
# Generalized from compose.ps1, which had the original five filenames hardcoded.
param(
    [string]$Src = "C:\Users\vinee\projects\jot-windows\store-assets\screenshots-1.2.1",
    [string]$Dst = "C:\Users\vinee\projects\jot-windows\store-assets\listing-1.2.1"
)

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $Dst | Out-Null

$CW = 1920; $CH = 1080
$c1 = [System.Drawing.Color]::FromArgb(232, 238, 252)
$c2 = [System.Drawing.Color]::FromArgb(249, 250, 253)

foreach ($file in Get-ChildItem $Src -Filter *.png | Sort-Object Name) {
    $img = [System.Drawing.Image]::FromFile($file.FullName)

    $maxW = [int]($CW * 0.80); $maxH = [int]($CH * 0.82)
    $scale = [Math]::Min($maxW / $img.Width, $maxH / $img.Height)
    if ($scale -gt 1.6) { $scale = 1.6 }   # cap upscaling so the small overlays stay crisp
    $dw = [int]($img.Width * $scale); $dh = [int]($img.Height * $scale)
    $dx = [int](($CW - $dw) / 2); $dy = [int](($CH - $dh) / 2)

    $canvas = New-Object System.Drawing.Bitmap $CW, $CH
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'

    $rect = New-Object System.Drawing.Rectangle 0, 0, $CW, $CH
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 45.0
    $g.FillRectangle($brush, $rect)

    for ($s = 10; $s -ge 2; $s -= 2) {
        $a = [int](8 + (10 - $s))
        $sb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, 20, 30, 60))
        $g.FillRectangle($sb, $dx - $s, $dy - $s + 6, $dw + 2*$s, $dh + 2*$s)
        $sb.Dispose()
    }
    $g.DrawImage($img, $dx, $dy, $dw, $dh)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 0, 0, 0)), 1
    $g.DrawRectangle($pen, $dx, $dy, $dw - 1, $dh - 1)

    $out = Join-Path $Dst $file.Name
    $canvas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $canvas.Dispose(); $img.Dispose(); $brush.Dispose(); $pen.Dispose()
    Write-Output ("composed {0,-22} (shot {1}x{2})" -f $file.Name, $dw, $dh)
}
