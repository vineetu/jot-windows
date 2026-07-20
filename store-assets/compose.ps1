# Centers each raw window capture on a clean 1920x1080 canvas so the images meet the
# Microsoft Store screenshot spec (>= 1366x768) and look like a polished listing.
# Soft diagonal gradient background (Jot-blue tinted) + subtle shadow + the app shot centered.

Add-Type -AssemblyName System.Drawing
$src = "C:\Users\vinee\projects\jot-windows\store-assets\screenshots"
$dst = "C:\Users\vinee\projects\jot-windows\store-assets\listing"
New-Item -ItemType Directory -Force -Path $dst | Out-Null

$CW = 1920; $CH = 1080
# Background gradient endpoints (light periwinkle -> soft white), echoing the blue "j".
$c1 = [System.Drawing.Color]::FromArgb(232, 238, 252)
$c2 = [System.Drawing.Color]::FromArgb(249, 250, 253)

$shots = @("01-main.png","02-pill.png","03-rewrite.png","04-settings.png","05-ai.png")
foreach ($name in $shots) {
    $inPath = Join-Path $src $name
    if (-not (Test-Path $inPath)) { Write-Output "skip (missing) $name"; continue }
    $img = [System.Drawing.Image]::FromFile($inPath)

    # Scale the shot to fit within ~78% of the canvas while keeping aspect ratio; never upscale small ones too much.
    $maxW = [int]($CW * 0.80); $maxH = [int]($CH * 0.82)
    $scale = [Math]::Min($maxW / $img.Width, $maxH / $img.Height)
    if ($scale -gt 1.6) { $scale = 1.6 }   # cap upscaling of the tiny pill so it stays crisp
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

    # Soft shadow: a few translucent, growing offset rects behind the shot.
    for ($s = 10; $s -ge 2; $s -= 2) {
        $a = [int](8 + (10 - $s))
        $sb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, 20, 30, 60))
        $g.FillRectangle($sb, $dx - $s, $dy - $s + 6, $dw + 2*$s, $dh + 2*$s)
        $sb.Dispose()
    }
    $g.DrawImage($img, $dx, $dy, $dw, $dh)
    # Thin border for crisp edge.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 0, 0, 0)), 1
    $g.DrawRectangle($pen, $dx, $dy, $dw - 1, $dh - 1)

    $out = Join-Path $dst $name
    $canvas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $canvas.Dispose(); $img.Dispose(); $brush.Dispose(); $pen.Dispose()
    Write-Output "composed $name  (shot ${dw}x${dh} on ${CW}x${CH})"
}
