# Генерирует src\MeloWG\Assets\app.ico (PNG-кадры 16–256 px). Запуск: powershell -File tools\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot "..\src\MeloWG\Assets\app.ico"))

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 100.0

    # Фон: скруглённый квадрат с диагональным градиентом.
    $bg = New-RoundedRect (3 * $k) (3 * $k) (94 * $k) (94 * $k) (24 * $k)
    $c1 = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x6E, 0xF6)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0x12, 0xB8, 0xD6)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)), $c1, $c2)
    $g.FillPath($grad, $bg)

    # Щит.
    $sh = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sh.StartFigure()
    $sh.AddLine((50 * $k), (17 * $k), (77 * $k), (27 * $k))
    $sh.AddLine((77 * $k), (27 * $k), (77 * $k), (47 * $k))
    $sh.AddBezier((77 * $k), (47 * $k), (77 * $k), (67 * $k), (65 * $k), (79 * $k), (50 * $k), (85 * $k))
    $sh.AddBezier((50 * $k), (85 * $k), (35 * $k), (79 * $k), (23 * $k), (67 * $k), (23 * $k), (47 * $k))
    $sh.AddLine((23 * $k), (47 * $k), (23 * $k), (27 * $k))
    $sh.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $sh)

    # Галочка-волна внутри щита (на 16 px не рисуем — сливается).
    if ($s -ge 24) {
        $pen = New-Object System.Drawing.Pen($c1, [float](7.5 * $k))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF((37 * $k), (51 * $k))),
            (New-Object System.Drawing.PointF((46 * $k), (60 * $k))),
            (New-Object System.Drawing.PointF((63 * $k), (41 * $k))))
        $g.DrawLines($pen, $pts)
        $pen.Dispose()
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) { $frames += , (New-Frame $s) }

$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $frames[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $bw.Write($data) }
$bw.Flush()

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[System.IO.File]::WriteAllBytes($Out, $fs.ToArray())
Write-Output "OK: $Out ($($fs.Length) bytes)"
