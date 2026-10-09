param([string]$Out = "src/ScrTranslator.App/app.ico")
# 生成应用图标：深底圆角方块 + "한→中" 字样（韩译中的直白表达）。256x256。
Add-Type -AssemblyName System.Drawing
$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear([System.Drawing.Color]::Transparent)

# 圆角背景（深蓝黑 → 青蓝渐变感用两笔完成）
$rect = New-Object System.Drawing.Rectangle(8, 8, ($size - 16), ($size - 16))
$brushTop = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 24, 32, 56),
    [System.Drawing.Color]::FromArgb(255, 16, 88, 128),
    45.0)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 48
$path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
$path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
$path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
$path.CloseFigure()
$g.FillPath($brushTop, $path)

# 文本：한 → 中
$fontKo = New-Object System.Drawing.Font("Microsoft YaHei UI", 64, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = 'Center'
$fmt.LineAlignment = 'Center'
$area = New-Object System.Drawing.RectangleF(0, ($size * 0.18), $size, ($size * 0.42))
$g.DrawString("한", $fontKo, [System.Drawing.Brushes]::White, $area, $fmt)
$areaArrow = New-Object System.Drawing.RectangleF(0, ($size * 0.50), $size, ($size * 0.26))
$g.DrawString("↓", $fontKo, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 120, 220, 255))), $areaArrow, $fmt)
$areaZh = New-Object System.Drawing.RectangleF(0, ($size * 0.70), $size, ($size * 0.30))
$g.DrawString("中", $fontKo, [System.Drawing.Brushes]::White, $areaZh, $fmt)

$g.Dispose()

# Bitmap → Icon：用 Icon.FromHandle 拿句柄再克隆成多尺寸 .ico 较繁琐，
# 这里直接把 PNG 以 "ico" 容器写出（Vista+ 支持 PNG 压缩图标条目）。
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose(); $bmp.Dispose()

$bw = New-Object System.IO.BinaryWriter([System.IO.File]::Create($Out))
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type: icon
$bw.Write([UInt16]1)      # count
$bw.Write([Byte]0)        # width  (0 = 256)
$bw.Write([Byte]0)        # height (0 = 256)
$bw.Write([Byte]0)        # colors
$bw.Write([Byte]0)        # reserved
$bw.Write([UInt16]1)      # planes
$bw.Write([UInt16]32)     # bpp
$bw.Write([UInt32]$png.Length) # bytes in resource
$bw.Write([UInt32]22)     # offset (6+16)
$bw.Write($png)
$bw.Close()
Write-Host "icon written: $Out ($((Get-Item $Out).Length) bytes)"
