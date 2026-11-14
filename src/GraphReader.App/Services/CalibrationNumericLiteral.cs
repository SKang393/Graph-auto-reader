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
}
