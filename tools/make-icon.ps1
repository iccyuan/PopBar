# Generates Assets/popbar.ico (multi-size, PNG-compressed) with System.Drawing.
# Usage: pwsh tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\PopBar\Assets\popbar.ico"
$sizes = 16, 24, 32, 48, 64, 128, 256

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square, blue -> violet gradient
    $r = [math]::Max(2, $s * 0.22)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 59, 130, 246)), ([System.Drawing.Color]::FromArgb(255, 139, 92, 246)), 45
    $g.FillPath($brush, $path)

    # white "bar" pill with three dots
    $pw = $s * 0.68; $ph = $s * 0.30
    $px = ($s - $pw) / 2; $py = ($s - $ph) / 2 - $s * 0.04
    $pill = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pr = $ph / 2
    $pill.AddArc($px, $py, $pr * 2, $pr * 2, 90, 180)
    $pill.AddArc($px + $pw - $pr * 2, $py, $pr * 2, $pr * 2, 270, 180)
    $pill.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $pill)

    # small arrow under the pill
    $aw = $s * 0.12; $ah = $s * 0.07
    $cx = $s / 2; $top = $py + $ph - 1
    $tri = @(
        (New-Object System.Drawing.PointF ($cx - $aw / 2), $top),
        (New-Object System.Drawing.PointF ($cx + $aw / 2), $top),
        (New-Object System.Drawing.PointF $cx, ($top + $ah))
    )
    $g.FillPolygon([System.Drawing.Brushes]::White, $tri)

    # dots
    $d = $ph * 0.38
    $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 100, 230))
    foreach ($k in -1, 0, 1) {
        $dx = $cx + $k * $pw * 0.27 - $d / 2
        $dy = $py + $ph / 2 - $d / 2
        $g.FillEllipse($dotBrush, $dx, $dy, $d, $d)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$images = @()
foreach ($s in $sizes) { $images += , (Draw $s) }

# ICO container: header (6) + entries (16 each) + PNG blobs
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $out)).Path + "\popbar.ico", $ms.ToArray())
Write-Host "wrote $out ($($ms.Length) bytes)"
