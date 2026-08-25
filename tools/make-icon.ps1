# Generates Assets/afterglow.ico — an ember-orange glow dot on dark ground.
# PNG-compressed ICO entries (supported since Vista). Run from repo root.
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::FromArgb(255, 14, 17, 22))

    # Rounded-square background
    $g.Clear([System.Drawing.Color]::Transparent)
    $bgPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Max(2, $size * 0.22)
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $bgPath.AddArc($rect.X, $rect.Y, $r*2, $r*2, 180, 90)
    $bgPath.AddArc($rect.Right - $r*2, $rect.Y, $r*2, $r*2, 270, 90)
    $bgPath.AddArc($rect.Right - $r*2, $rect.Bottom - $r*2, $r*2, $r*2, 0, 90)
    $bgPath.AddArc($rect.X, $rect.Bottom - $r*2, $r*2, $r*2, 90, 90)
    $bgPath.CloseFigure()
    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 18, 22, 29))
    $g.FillPath($bgBrush, $bgPath)

    # Outer glow
    $cx = $size / 2.0; $cy = $size / 2.0
    $glowR = $size * 0.42
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse($cx - $glowR, $cy - $glowR, $glowR * 2, $glowR * 2)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(200, 255, 138, 60)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 138, 60))
    $g.FillPath($glow, $glowPath)

    # Core dot
    $coreR = $size * 0.20
    $core = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 160, 92))
    $g.FillEllipse($core, $cx - $coreR, $cy - $coreR, $coreR * 2, $coreR * 2)
    $hot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 236, 214))
    $hotR = $size * 0.09
    $g.FillEllipse($hot, $cx - $hotR, $cy - $hotR, $hotR * 2, $hotR * 2)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@($size, $ms.ToArray())
    $g.Dispose(); $bmp.Dispose()
}

# Pack ICO
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0)      # reserved
$writer.Write([uint16]1)      # type: icon
$writer.Write([uint16]$pngs.Count)
$offset = 6 + (16 * $pngs.Count)
foreach ($entry in $pngs) {
    $size = $entry[0]; $bytes = $entry[1]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dim)   # width
    $writer.Write([byte]$dim)   # height
    $writer.Write([byte]0)      # palette
    $writer.Write([byte]0)      # reserved
    $writer.Write([uint16]1)    # planes
    $writer.Write([uint16]32)   # bpp
    $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($entry in $pngs) { $writer.Write($entry[1]) }
$writer.Flush()

New-Item -ItemType Directory -Force "src\Afterglow.App\Assets" | Out-Null
[System.IO.File]::WriteAllBytes("src\Afterglow.App\Assets\afterglow.ico", $out.ToArray())
Write-Host "afterglow.ico written: $($out.Length) bytes"
