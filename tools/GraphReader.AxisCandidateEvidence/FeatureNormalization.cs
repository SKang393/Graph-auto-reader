// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;

namespace GraphReader.AxisCandidateEvidence;

internal static class FeatureNormalization
{
    public const string Schema = "graphreader.axis-candidate-features.v1";

    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly<string>(
    [
        "pair_score",
        "bottom_left_x_over_width",
        "bottom_left_y_over_height",
        "bottom_right_x_over_width",
        "bottom_right_y_over_height",
        "top_right_x_over_width",
        "top_right_y_over_height",
        "top_left_x_over_width",
        "top_left_y_over_height",
        "x_span_over_width",
        "y_span_over_height",
        "x_coverage",
        "y_coverage",
        "x_rms_over_height",
        "y_rms_over_width",
        "x_support_count",
        "y_support_count",
    ]);

    public static AxisCandidateEvidenceFeatureMatrix Build(
        int width,
        int height,
        IReadOnlyList<AxisGeometryCandidateSnapshot> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Feature dimensions must be positive.");
        }

        var rows = new AxisCandidateEvidenceFeatureRow[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            AxisGeometryCandidateSnapshot candidate = candidates[index];
            double[] values =
            [
                candidate.Score,
                candidate.Intersection.X / width,
                candidate.Intersection.Y / height,
                candidate.BottomRight.X / width,
                candidate.BottomRight.Y / height,
                candidate.TopRight.X / width,
                candidate.TopRight.Y / height,
                candidate.TopLeft.X / width,
                candidate.TopLeft.Y / height,
                candidate.XSpan / width,
                candidate.YSpan / height,
                candidate.XCoverageFraction,
                candidate.YCoverageFraction,
                candidate.XRootMeanSquareError / height,
                candidate.YRootMeanSquareError / width,
                candidate.XSupportingCandidateIds.Count,
                candidate.YSupportingCandidateIds.Count,
            ];

            if (values.Any(static value => !double.IsFinite(value)))
            {
                throw new InvalidDataException("Normalized candidate features must be finite.");
            }

            rows[index] = new AxisCandidateEvidenceFeatureRow(index + 1, Array.AsReadOnly(values));
        }

        return new AxisCandidateEvidenceFeatureMatrix(Schema, Names, Array.AsReadOnly(rows));
    }
}
