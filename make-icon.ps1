# ValoLive ikon uretici: GDI+ ile cizer, cok boyutlu (PNG gomulu) icon.ico yazar.
Add-Type -AssemblyName System.Drawing
$root = $PSScriptRoot
$red   = [Drawing.ColorTranslator]::FromHtml('#ff4655')
$cream = [Drawing.ColorTranslator]::FromHtml('#ece8e1')

function Draw-Icon([int]$S) {
    $bmp = New-Object Drawing.Bitmap $S, $S, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $k = $S / 256.0
    $g.ScaleTransform($k, $k)

    # zemin: koyu, yuvarlatilmis kare
    $r = 56; $x = 6; $w = 244
    $bg = New-Object Drawing.Drawing2D.GraphicsPath
    $bg.AddArc($x, $x, $r, $r, 180, 90); $bg.AddArc($x + $w - $r, $x, $r, $r, 270, 90)
    $bg.AddArc($x + $w - $r, $x + $w - $r, $r, $r, 0, 90); $bg.AddArc($x, $x + $w - $r, $r, $r, 90, 90)
    $bg.CloseFigure()
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, 0)), ([Drawing.PointF]::new(256, 256)), ([Drawing.ColorTranslator]::FromHtml('#22303d')), ([Drawing.ColorTranslator]::FromHtml('#0b131b'))
    $g.FillPath($grad, $bg)
    if ($S -ge 48) { $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(90, $red)), 4), $bg) }

    # V: ortadan capraz bir yarikla bolunmus iki kol (sol kirmizi, sag krem)
    $left  = [Drawing.PointF[]]@([Drawing.PointF]::new(40, 78),  [Drawing.PointF]::new(92, 78),  [Drawing.PointF]::new(132, 176), [Drawing.PointF]::new(116, 216), [Drawing.PointF]::new(102, 216))
    $right = [Drawing.PointF[]]@([Drawing.PointF]::new(166, 78), [Drawing.PointF]::new(216, 78), [Drawing.PointF]::new(160, 216), [Drawing.PointF]::new(128, 216), [Drawing.PointF]::new(140, 186))
    $g.FillPolygon((New-Object Drawing.SolidBrush $red), $left)
    $g.FillPolygon((New-Object Drawing.SolidBrush $cream), $right)

    # CANLI noktasi: halkali kirmizi daire
    if ($S -ge 24) { $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, $red))), 176, 18, 56, 56) }
    $g.FillEllipse((New-Object Drawing.SolidBrush $red), 188, 30, 32, 32)
    $g.DrawEllipse((New-Object Drawing.Pen $cream, 5), 188, 30, 32, 32)

    $g.Dispose(); $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
# 256 PNG olarak, kucukler eski okuyucular (.NET Framework) icin 32 bit DIB olarak gomulur
$pngs = foreach ($s in $sizes) {
    $b = Draw-Icon $s; $ms = New-Object IO.MemoryStream
    if ($s -eq 256) {
        $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $b.Save("$root\icon.png", [Drawing.Imaging.ImageFormat]::Png)
    } else {
        $w = New-Object IO.BinaryWriter $ms
        $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $w.Write([int32]$b.GetPixel($x, $y).ToArgb()) } }
        $w.Write((New-Object byte[] ([Math]::Ceiling($s / 32.0) * 4 * $s)))   # AND maskesi (alfa kullanildigi icin bos)
    }
    $b.Dispose(); , $ms.ToArray()
}

$out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$off)
    $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes("$root\icon.ico", $out.ToArray())
"OK: $root\icon.ico"
