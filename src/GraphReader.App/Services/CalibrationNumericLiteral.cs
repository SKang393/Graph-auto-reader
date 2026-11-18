// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Ocr;

namespace GraphReader.App.Services;

/// <summary>Calibration labels must not concatenate separate OCR text fragments into a number.</summary>
internal static class CalibrationNumericLiteral
{
    public static bool IsLiteral(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        foreach (char character in trimmed)
            if (char.IsWhiteSpace(character)) return false;

        return GraphNumericParser.IsLiteralGraphNumber(trimmed);
    }

    // Zero is shared by both interpretations. A fit in pixels cannot distinguish
    // scales that differ by the same factor for every nonzero supporting tick.
    internal static bool HasAmbiguousScale(IEnumerable<(string Text, double Value)> ticks)
    {
        bool hasNonzero = false;
        foreach (var (text, value) in ticks)
        {
            if (value == 0) continue;
            if (GraphNumericParser.Parse(text).Alternatives.Count <= 1) return false;
            hasNonzero = true;
        }
        return hasNonzero;
    }
}
