<#
.SYNOPSIS
    Draws the DeepUninstall icon and writes src\DeepUninstall.App\Assets\DeepUninstall.ico
    plus PNG copies used inside the app. Run with Windows PowerShell on Windows 11
    (it uses the Segoe Fluent Icons font).
#>
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$ErrorActionPreference = "Stop"
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) "src\DeepUninstall.App\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconBitmap([int]$size) {
    $s = [double]$size
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    # Rounded square with a blue-to-violet gradient.
    $gradient = New-Object System.Windows.Media.LinearGradientBrush
    $gradient.StartPoint = New-Object System.Windows.Point 0, 0
    $gradient.EndPoint = New-Object System.Windows.Point 1, 1
    $gradient.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x2B, 0x6C, 0xFF)), 0))
    $gradient.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x7A, 0x4C, 0xF5)), 1))
    $margin = [Math]::Max(0.5, $s * 0.04)
    $rect = New-Object System.Windows.Rect $margin, $margin, ($s - 2 * $margin), ($s - 2 * $margin)
    $dc.DrawRoundedRectangle($gradient, $null, $rect, $s * 0.22, $s * 0.22)

    # Subtle top highlight.
    $shine = New-Object System.Windows.Media.LinearGradientBrush
    $shine.StartPoint = New-Object System.Windows.Point 0.5, 0
    $shine.EndPoint = New-Object System.Windows.Point 0.5, 0.6
    $shine.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(70, 255, 255, 255)), 0))
    $shine.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(0, 255, 255, 255)), 1))
    $dc.DrawRoundedRectangle($shine, $null, $rect, $s * 0.22, $s * 0.22)

    # White trash can glyph from Segoe Fluent Icons.
    $typeface = New-Object System.Windows.Media.Typeface "Segoe Fluent Icons"
    $glyphSize = $s * 0.56
    $text = New-Object System.Windows.Media.FormattedText ([string][char]0xE74D), ([Globalization.CultureInfo]::InvariantCulture),
        ([System.Windows.FlowDirection]::LeftToRight), $typeface, $glyphSize, ([System.Windows.Media.Brushes]::White), 1.0
    $dc.DrawText($text, (New-Object System.Windows.Point (($s - $text.Width) / 2 - $s * 0.045), (($s - $text.Height) / 2 + $s * 0.02)))

    # A small sparkle: "completely clean". Skipped at tiny sizes where it would be noise.
    if ($size -ge 24) {
        $cx = $s * 0.76; $cy = $s * 0.245; $r = $s * 0.12; $w = $r * 0.28
        $geo = New-Object System.Windows.Media.StreamGeometry
        $ctx = $geo.Open()
        $ctx.BeginFigure((New-Object System.Windows.Point $cx, ($cy - $r)), $true, $true)
        $pts = @(
            (New-Object System.Windows.Point ($cx + $w), ($cy - $w)), (New-Object System.Windows.Point ($cx + $r), $cy),
            (New-Object System.Windows.Point ($cx + $w), ($cy + $w)), (New-Object System.Windows.Point $cx, ($cy + $r)),
            (New-Object System.Windows.Point ($cx - $w), ($cy + $w)), (New-Object System.Windows.Point ($cx - $r), $cy),
            (New-Object System.Windows.Point ($cx - $w), ($cy - $w)))
        foreach ($p in $pts) { $ctx.LineTo($p, $true, $true) }
        $ctx.Close()
        $dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $geo)
    }

    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    return $bmp
}

function Get-PngBytes($bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (Get-PngBytes (New-IconBitmap $size)) }

# .ico with PNG-compressed entries (supported since Windows Vista).
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets "DeepUninstall.ico"), $ms.ToArray())

[IO.File]::WriteAllBytes((Join-Path $assets "icon-32.png"), $images[[array]::IndexOf($sizes, 32)])
[IO.File]::WriteAllBytes((Join-Path $assets "icon-256.png"), $images[[array]::IndexOf($sizes, 256)])
Get-ChildItem $assets | Select-Object Name, Length
