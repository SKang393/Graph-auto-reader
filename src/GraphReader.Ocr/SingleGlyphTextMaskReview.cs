// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Text;

namespace GraphReader.Ocr;

/// <summary>Unreviewed annotation glyphs and symbol-only runs can also be plotted markers.</summary>
internal static class SingleGlyphTextMaskReview
{
    internal const string Version = "inside-plot-symbol-mask-review-v2";

    internal static bool RequiresReview(OcrRegion region, OcrRectangle plotBounds)
    {
        if (region.ReviewStatus != OcrReviewStatus.Unreviewed ||
            region.Role is not (OcrTextRole.Annotation or OcrTextRole.Other))
            return false;

        string text = region.Text.Trim();
        if (text.Length == 0 || (!IsSingleGlyph(text) && !IsSymbolRun(text)))
            return false;

        OcrPoint center = region.Polygon.Bounds.Center;
        return center.X >= plotBounds.Left && center.X <= plotBounds.Right &&
            center.Y >= plotBounds.Top && center.Y <= plotBounds.Bottom;
    }

    internal static string WarningCode(OcrRegion region) => IsSingleGlyph(region.Text.Trim())
        ? "ocr_single_glyph_annotation_needs_review"
        : "ocr_symbol_run_annotation_needs_review";

    private static bool IsSingleGlyph(string text) => new StringInfo(text).LengthInTextElements == 1;

    private static bool IsSymbolRun(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsSymbol(rune) && !Rune.IsPunctuation(rune))
                return false;
        }
        return true;
    }
}
