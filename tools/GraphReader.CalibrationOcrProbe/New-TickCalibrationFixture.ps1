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
    [ValidateRange(0, 1700)][int]$WhiteMarginPixels = 0
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutputPath) -or (Test-Path -LiteralPath $OutputPath)) {
    throw 'An absolute, unused output path is required.'
}
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($OutputPath)) -PathType Container)) {
    throw 'Create the bounded validation output directory first.'
}
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap(640, 480)
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
    $xPositions = if ($DenseXTicks) { @(140, 160, 180) } else { @(140, 340, 540) }
    $yPositions = if ($LeftAxisContinuation) { @(80, 215, 350) } else { @(80, 230, 380) }
    if ($GridLines) {
        foreach ($x in $xPositions) { $graphics.DrawLine($pen, $x, 80, $x, 380) }
        foreach ($y in $yPositions) { $graphics.DrawLine($pen, 100, $y, 580, $y) }
    }
    $graphics.DrawLine($pen, $axisStart, 380, 580, 380)
    $graphics.DrawLine($pen, 100, 80, 100, 380)
    $xLabels = @('1', '2', '3')
    for ($index = 0; $index -lt $xPositions.Count; $index++) {
        $x = $xPositions[$index]
        $tickEnd = if ($InwardXTicks) { 375 } else { 385 }
        if (-not $NoTickMarks) { $graphics.DrawLine($pen, $x, 380, $x, $tickEnd) }
        $graphics.DrawString($xLabels[$index], $font, [Drawing.Brushes]::Black,
            [Drawing.PointF]::new($x, 403), $format)
    }
    $yLabels = @('10', '5', '0')
    for ($index = 0; $index -lt $yPositions.Count; $index++) {
        $y = $yPositions[$index]
        if (-not $NoTickMarks) { $graphics.DrawLine($pen, 95, $y, 100, $y) }
        $graphics.DrawString($yLabels[$index], $font, [Drawing.Brushes]::Black,
            [Drawing.PointF]::new(74, $y), $format)
    }
    if ($TransparentBackground) { $bitmap.MakeTransparent([Drawing.Color]::White) }
    if ($WhiteMarginPixels -eq 0) {
        $bitmap.Save($OutputPath, [Drawing.Imaging.ImageFormat]::Png)
    } else {
        $padded = New-Object Drawing.Bitmap((640 + 2 * $WhiteMarginPixels), (480 + 2 * $WhiteMarginPixels))
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
    width = 640 + 2 * $WhiteMarginPixels
    height = 480 + 2 * $WhiteMarginPixels
    x_tick_direction = if ($InwardXTicks) { 'inward' } else { 'outward' }
    transparent_background = [bool]$TransparentBackground
    left_axis_continuation = [bool]$LeftAxisContinuation
    white_margin_pixels = $WhiteMarginPixels
    tick_marks_drawn = -not $NoTickMarks
    grid_lines = [bool]$GridLines
    x_label_positions = $xPositions
    renderer = 'Windows System.Drawing with installed Segoe UI; no font binary copied'
} | ConvertTo-Json -Compress
