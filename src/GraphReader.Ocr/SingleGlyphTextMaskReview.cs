// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;

namespace GraphReader.Ocr;

/// <summary>A lone unreviewed annotation glyph can also be a plotted symbol.</summary>
internal static class SingleGlyphTextMaskReview
{
    internal const string Version = "inside-plot-single-glyph-mask-review-v1";

    internal static bool RequiresReview(OcrRegion region, OcrRectangle plotBounds)
    {
        if (region.ReviewStatus != OcrReviewStatus.Unreviewed ||
            region.Role is not (OcrTextRole.Annotation or OcrTextRole.Other) ||
            new StringInfo(region.Text.Trim()).LengthInTextElements != 1)
            return false;

        OcrPoint center = region.Polygon.Bounds.Center;
        return center.X >= plotBounds.Left && center.X <= plotBounds.Right &&
            center.Y >= plotBounds.Top && center.Y <= plotBounds.Bottom;
    }
}
