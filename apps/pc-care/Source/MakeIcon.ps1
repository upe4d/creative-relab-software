param([string]$Destination = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap 256,256
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(8,8,100,100,180,90)
    $path.AddArc(148,8,100,100,270,90)
    $path.AddArc(148,148,100,100,0,90)
    $path.AddArc(8,148,100,100,90,90)
    $path.CloseFigure()
    $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Rectangle]::new(8,8,240,240)),([Drawing.ColorTranslator]::FromHtml('#29446F')),([Drawing.ColorTranslator]::FromHtml('#142139')),90
    $graphics.FillPath($gradient,$path)
    $pen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#A1C4FF')),24
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $monogram = New-Object Drawing.Drawing2D.GraphicsPath
    $monogram.AddLine(78,183,78,77)
    $monogram.AddLine(78,77,132,77)
    $monogram.AddBezier(132,77,184,77,184,137,132,137)
    $monogram.AddLine(132,137,78,137)
    $graphics.DrawPath($pen,$monogram)
    $dot = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#D2E4FF'))
    $graphics.FillEllipse($dot,162,170,22,22)
    $bitmap.Save((Join-Path $Destination 'app-icon.png'),[Drawing.Imaging.ImageFormat]::Png)
    $sizes = @(16,24,32,48,64,128,256)
    $images = foreach ($size in $sizes) {
        $small = New-Object Drawing.Bitmap $size,$size
        $canvas = [Drawing.Graphics]::FromImage($small)
        try {
            $canvas.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $canvas.DrawImage($bitmap,0,0,$size,$size)
            $stream = New-Object IO.MemoryStream
            try { $small.Save($stream,[Drawing.Imaging.ImageFormat]::Png); ,$stream.ToArray() } finally { $stream.Dispose() }
        } finally { $canvas.Dispose(); $small.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $Destination 'app.ico'))
    $writer = New-Object IO.BinaryWriter $file
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    } finally { $writer.Dispose(); $file.Dispose() }
    $dot.Dispose(); $monogram.Dispose(); $pen.Dispose(); $gradient.Dispose(); $path.Dispose()
} finally { $graphics.Dispose(); $bitmap.Dispose() }
