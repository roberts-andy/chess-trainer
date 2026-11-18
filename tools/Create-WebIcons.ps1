$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$directory = Join-Path $PSScriptRoot '..\src\ChessTrainer.Web\wwwroot\icons'
New-Item -ItemType Directory -Force $directory | Out-Null
foreach ($size in @(192, 512)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::FromArgb(32, 32, 32))
        $graphics.ScaleTransform($size / 100.0, $size / 100.0)
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(240, 217, 181))
        try {
            $graphics.FillRectangle($brush, 29, 27, 11, 17)
            $graphics.FillRectangle($brush, 45, 27, 10, 17)
            $graphics.FillRectangle($brush, 60, 27, 11, 17)
            $graphics.FillRectangle($brush, 29, 39, 42, 10)
            $points = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(37, 46), [System.Drawing.PointF]::new(63, 46),
                [System.Drawing.PointF]::new(67, 68), [System.Drawing.PointF]::new(33, 68))
            $graphics.FillPolygon($brush, $points)
            $graphics.FillRectangle($brush, 29, 67, 42, 8)
        }
        finally { $brush.Dispose() }
        $bitmap.Save((Join-Path $directory "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}
