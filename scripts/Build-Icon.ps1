[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Storloom.App/Assets'
[xml] $svg = Get-Content -LiteralPath (Join-Path $assetRoot 'storloom.svg') -Raw
$canvas = [Drawing.Bitmap]::new(256, 256)
$graphics = [Drawing.Graphics]::FromImage($canvas)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
try {
    foreach ($shape in $svg.svg.ChildNodes) {
        if ($shape.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
        $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($shape.fill))
        try {
            if ($shape.LocalName -eq 'rect') {
                $x = [single]::Parse($shape.x); $y = [single]::Parse($shape.y)
                $w = [single]::Parse($shape.width); $h = [single]::Parse($shape.height); $d = 2 * [single]::Parse($shape.rx)
                $path = [Drawing.Drawing2D.GraphicsPath]::new()
                try {
                    $path.AddArc($x, $y, $d, $d, 180, 90)
                    $path.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
                    $path.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
                    $path.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
                    $path.CloseFigure(); $graphics.FillPath($brush, $path)
                } finally { $path.Dispose() }
            } elseif ($shape.LocalName -eq 'polygon') {
                [Drawing.PointF[]] $points = $shape.points.Split(' ') | ForEach-Object {
                    $pair = $_.Split(','); [Drawing.PointF]::new([single]::Parse($pair[0]), [single]::Parse($pair[1]))
                }
                $graphics.FillPolygon($brush, $points)
            } else { throw 'Unsupported icon geometry' }
        } finally { $brush.Dispose() }
    }
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $frames = foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $scaled = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $scaled.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $scaled.DrawImage($canvas, 0, 0, $size, $size)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            ,$stream.ToArray()
        } finally { $stream.Dispose(); $scaled.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $assetRoot 'storloom.ico'))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $graphics.Dispose(); $canvas.Dispose() }
