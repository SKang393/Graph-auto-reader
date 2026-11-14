# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [switch]$InwardXTicks,
    [switch]$TransparentBackground,
    [switch]$LeftAxisContinuation,
    [switch]$NoTickMarks,
    [switch]$DenseXTicks,
    [switch]$GridLines,
    [switch]$SmallPlotWithCaption,
    [ValidateRange(0, 1700)][int]$WhiteMarginPixels = 0
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutputPath) -or (Test-Path -LiteralPath $OutputPath)) {
    throw 'An absolute, unused output path is required.'
}
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($OutputPath)) -PathType Container)) {
    throw 'Create the bounded validation output directory first.'
}
if ($SmallPlotWithCaption -and ($InwardXTicks -or $TransparentBackground -or $LeftAxisContinuation -or $NoTickMarks -or $DenseXTicks -or $GridLines -or $WhiteMarginPixels -ne 0)) {
    throw 'SmallPlotWithCaption cannot be combined with other fixture options.'
}
Add-Type -AssemblyName System.Drawing
$baseWidth = if ($SmallPlotWithCaption) { 512 } else { 640 }
$baseHeight = if ($SmallPlotWithCaption) { 512 } else { 480 }
$plotTop = if ($SmallPlotWithCaption) { 100 } else { 80 }
$plotBottom = if ($SmallPlotWithCaption) { 190 } else { 380 }
$plotRight = if ($SmallPlotWithCaption) { 195 } else { 580 }
$xLabelY = if ($SmallPlotWithCaption) { 213 } else { 403 }
$tickLength = if ($SmallPlotWithCaption) { 4 } else { 5 }
$bitmap = New-Object Drawing.Bitmap($baseWidth, $baseHeight)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$pen = New-Object Drawing.Pen([Drawing.Color]::Black, 1)
$font = New-Object Drawing.Font('Segoe UI', 18, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$format = New-Object Drawing.StringFormat
try {
    $graphics.Clear([Drawing.Color]::White)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::None
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $format.Alignment = [Drawing.StringAlignment]::Center
    $format.LineAlignment = [Drawing.StringAlignment]::Center
    $axisStart = if ($LeftAxisContinuation) { 50 } else { 100 }
    $xPositions = if ($SmallPlotWithCaption) { @(110, 150, 190) } elseif ($DenseXTicks) { @(140, 160, 180) } else { @(140, 340, 540) }
    $yPositions = if ($SmallPlotWithCaption) { @(100, 145, 190) } elseif ($LeftAxisContinuation) { @(80, 215, 350) } else { @(80, 230, 380) }
    if ($GridLines) {
        foreach ($x in $xPositions) { $graphics.DrawLine($pen, $x, 80, $x, 380) }
        foreach ($y in $yPositions) { $graphics.DrawLine($pen, 100, $y, 580, $y) }
    }
    $graphics.DrawLine($pen, $axisStart, $plotBottom, $plotRight, $plotBottom)
    $graphics.DrawLine($pen, 100, $plotTop, 100, $plotBottom)
    $xLabels = @('1', '2', '3')
    for ($index = 0; $index -lt $xPositions.Count; $index++) {
        $x = $xPositions[$index]
        $tickEnd = if ($InwardXTicks) { $plotBottom - $tickLength } else { $plotBottom + $tickLength }
        if (-not $NoTickMarks) { $graphics.DrawLine($pen, $x, $plotBottom, $x, $tickEnd) }
        $graphics.DrawString($xLabels[$index], $font, [Drawing.Brushes]::Black,
            [Drawing.PointF]::new($x, $xLabelY), $format)
    }
    $yLabels = @('10', '5', '0')
    for ($index = 0; $index -lt $yPositions.Count; $index++) {
        $y = $yPositions[$index]
        if (-not $NoTickMarks) { $graphics.DrawLine($pen, (100 - $tickLength), $y, 100, $y) }
        $graphics.DrawString($yLabels[$index], $font, [Drawing.Brushes]::Black,
            [Drawing.PointF]::new(74, $y), $format)
    }
    if ($SmallPlotWithCaption) {
        for ($x = 0; $x -lt $baseWidth; $x += 2) { $graphics.FillRectangle([Drawing.Brushes]::Black, $x, 400, 1, 1) }
    }
    if ($TransparentBackground) { $bitmap.MakeTransparent([Drawing.Color]::White) }
    if ($WhiteMarginPixels -eq 0) {
        $bitmap.Save($OutputPath, [Drawing.Imaging.ImageFormat]::Png)
    } else {
        $padded = New-Object Drawing.Bitmap(($baseWidth + 2 * $WhiteMarginPixels), ($baseHeight + 2 * $WhiteMarginPixels))
        $paddedGraphics = [Drawing.Graphics]::FromImage($padded)
        try {
            $paddedGraphics.Clear([Drawing.Color]::White)
            $paddedGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $paddedGraphics.DrawImageUnscaled($bitmap, $WhiteMarginPixels, $WhiteMarginPixels)
            $padded.Save($OutputPath, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $paddedGraphics.Dispose(); $padded.Dispose() }
    }
}
finally {
    $format.Dispose(); $font.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
[pscustomobject]@{
    scope = 'owned-synthetic-control-only'
    path = $OutputPath
    sha256 = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes = (Get-Item -LiteralPath $OutputPath).Length
    width = $baseWidth + 2 * $WhiteMarginPixels
    height = $baseHeight + 2 * $WhiteMarginPixels
    x_tick_direction = if ($InwardXTicks) { 'inward' } else { 'outward' }
    transparent_background = [bool]$TransparentBackground
    left_axis_continuation = [bool]$LeftAxisContinuation
    white_margin_pixels = $WhiteMarginPixels
    tick_marks_drawn = -not $NoTickMarks
    grid_lines = [bool]$GridLines
    small_plot_with_caption = [bool]$SmallPlotWithCaption
    plot_bounds = @((100 + $WhiteMarginPixels), ($plotTop + $WhiteMarginPixels), ($plotRight + $WhiteMarginPixels), ($plotBottom + $WhiteMarginPixels))
    x_label_positions = @($xPositions | ForEach-Object { $_ + $WhiteMarginPixels })
    y_label_positions = @($yPositions | ForEach-Object { $_ + $WhiteMarginPixels })
    renderer = 'Windows System.Drawing with installed Segoe UI; no font binary copied'
} | ConvertTo-Json -Compress
