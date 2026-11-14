// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.Ocr;

namespace GraphReader.App.Services;

/// <summary>Transient user input tied to one exact cached OCR row, never a worker receipt.</summary>
internal sealed record CalibrationTickFitEvidence(CalibrationOcrProposal RawProposal, string? CorrectedText)
{
    internal bool HasCorrection => CorrectedText is not null;
    internal bool HasValidCorrection => HasCorrection && EffectiveProposal is
        { NumericLiteral: true, FailureCode: null, NumericValue: double value } && double.IsFinite(value);

    internal CalibrationOcrProposal EffectiveProposal
    {
        get
        {
            if (CorrectedText is null) return RawProposal;
            bool literal = CalibrationNumericLiteral.IsLiteral(CorrectedText);
            NumericParseResult parsed = GraphNumericParser.Parse(CorrectedText);
            bool valid = literal && parsed.IsSuccess && parsed.Value is double value && double.IsFinite(value) &&
                RawProposal.SuggestedAxis is "X" or "Y" &&
                (RawProposal.Role != "XTick" || RawProposal.SuggestedAxis == "X") &&
                (RawProposal.Role != "YTick" || RawProposal.SuggestedAxis == "Y");
            // Confidence remains the original OCR score, including zero after a failed read.
            return RawProposal with { Text = CorrectedText, NumericLiteral = valid, NumericValue = valid ? parsed.Value : null,
                NormalizedText = valid ? parsed.NormalizedText : null, FailureCode = valid ? null : RawProposal.FailureCode,
                Role = valid ? RawProposal.SuggestedAxis == "X" ? "XTick" : "YTick" : RawProposal.Role };
        }
    }
}
