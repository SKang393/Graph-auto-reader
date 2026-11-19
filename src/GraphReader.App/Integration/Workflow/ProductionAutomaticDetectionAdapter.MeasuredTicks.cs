// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

public sealed partial class ProductionAutomaticDetectionAdapter
{
    private sealed record MeasuredLabel(ParsedTick Tick, double Position, double Start, double End,
        double Near, double Far);

    private static HashSet<string> CheckMeasuredReadings(ParsedTick[] ticks, TickLabelGeometryResult geometry,
        HashSet<string>? inliers, List<string> evidence)
    {
        var verified = new HashSet<string>(StringComparer.Ordinal);
        if (inliers is null) return verified;
        var positions = geometry.Positions;
        // Orient the measured axis so increasing positions mean increasing graph values.
        MeasuredLabel Measure(ParsedTick tick)
        {
            double position = positions[tick.Region.RegionId];
            var box = tick.Region.Polygon.Bounds;
            return tick.Region.Role == OcrTextRole.XTick
                ? new(tick, position, box.Left, box.Right, box.Top, box.Bottom)
                : new(tick, -position, -box.Bottom, -box.Top, box.Left, box.Right);
        }

        var labels = ticks.Where(t => positions.ContainsKey(t.Region.RegionId)).Select(Measure)
            .OrderBy(label => label.Position).ToArray();
        if (labels.Length < 2) return verified;

        var interior = labels[1..^1];
        var anchors = interior.Where(label => label.Tick.Competing.Count == 0 && HasNativeLiteral(label.Tick)).ToArray();
        double tolerance = new RobustFitOptions().InlierTolerancePixels;
        string? associationDefect = CheckAssociations();
        var fixedReadings = labels.Where(label => label.Tick.Competing.Count == 0).ToArray();
        // Interpolate only between undisputed, independently attached literal readings.
        // Two crops establish the line and at least a third must agree with it.
        if (associationDefect is null && fixedReadings.Length >= 3 &&
            labels.All(label => geometry.AttachedStrokeIds.Contains(label.Tick.Region.RegionId) && HasNativeLiteral(label.Tick)) &&
            labels.Zip(labels.Skip(1)).All(pair => pair.First.Tick.Value < pair.Second.Tick.Value))
        {
            var first = fixedReadings[0];
            var last = fixedReadings[^1];
            if (fixedReadings.All(anchor => SupportsPrimary(anchor, first, last, tolerance)))
            {
                foreach (var label in interior.Where(label => label.Tick.Competing.Count > 0 &&
                    first.Position < label.Position && label.Position < last.Position))
                {
                    if (!label.Tick.Competing.All(other => other.Text.All(char.IsAsciiDigit)) ||
                        !SupportsPrimary(label, first, last, tolerance)) continue;
                    verified.Add(label.Tick.Region.RegionId);
                    evidence.Add("measured_tick_interior:" + JsonSerializer.Serialize(new
                    {
                        role = label.Tick.Region.Role.ToString(), reading = Source(label),
                        anchors = fixedReadings.Select(Source), tolerancePixels = tolerance,
                    }));
                }
            }
        }

        string? defect = CheckInterior();
        foreach (var endpoint in new[] { labels[0], labels[^1] })
        {
            var tick = endpoint.Tick;
            if (tick.Competing.Count == 0) continue;
            string? reason = defect;
            if (reason is null)
            {
                bool upper = endpoint == labels[^1];
                if (!geometry.AttachedStrokeIds.Contains(tick.Region.RegionId)) reason = "detached_endpoint";
                else if (!HasNativeLiteral(tick) || !tick.Competing.All(other => IsSingleDigitEdit(tick.Region.Text, other.Text)))
                    reason = "independent_or_unverified_reading";
                else if (!SupportsPrimary(endpoint, anchors[0], anchors[^1], tolerance) || (upper ? tick.Value <= interior[^1].Tick.Value : tick.Value >= interior[0].Tick.Value))
                    reason = "endpoint_disagrees_with_interior";
                else if (tick.Competing.Any(other => upper ? other.Value > anchors[^1].Tick.Value : other.Value < anchors[0].Tick.Value))
                    reason = "alternative_preserves_axis_order";
            }
            if (reason is null) verified.Add(tick.Region.RegionId);
            evidence.Add("measured_tick_endpoint:" + JsonSerializer.Serialize(new
            {
                role = tick.Region.Role.ToString(),
                decision = reason ?? "corroborated",
                endpoint = Source(endpoint),
                interior = interior.Select(Source),
                anchorIds = anchors.Length < 2 ? [] : new[] { anchors[0].Tick.Region.RegionId, anchors[^1].Tick.Region.RegionId },
            }));
        }
        return verified;

        object Source(MeasuredLabel label) => new
        {
            id = label.Tick.Region.RegionId,
            pixel = positions[label.Tick.Region.RegionId],
            attached = geometry.AttachedStrokeIds.Contains(label.Tick.Region.RegionId),
            primary = label.Tick.Region.Text,
            alternatives = label.Tick.Competing.Select(other => other.Text),
        };

        string? CheckAssociations()
        {
            if (labels.Length != ticks.Length) return "unassociated_numeric_reading";
            if (labels.Max(p => p.Near) >= labels.Min(p => p.Far)) return "mixed_label_lanes";
            for (int i = 0; i < labels.Length; i++)
            {
                var label = labels[i];
                if (!double.IsFinite(label.Position) || label.Position < label.Start || label.Position > label.End ||
                    !inliers.Contains(label.Tick.Region.RegionId) || (i > 0 && label.Start <= labels[i - 1].End))
                    return "unverified_stroke_association";
            }
            return null;
        }

        string? CheckInterior()
        {
            // Two fixed interior observations set the scale. At least one other
            // interior crop must independently agree, with both endpoints withheld.
            if (interior.Length < 3 || anchors.Length < 2) return "insufficient_independent_interior";
            if (associationDefect is not null) return associationDefect;
            if (anchors[0].Tick.Value >= anchors[^1].Tick.Value) return "unordered_interior";
            for (int i = 0; i < interior.Length; i++)
            {
                var label = interior[i];
                if (!geometry.AttachedStrokeIds.Contains(label.Tick.Region.RegionId)) return "detached_interior";
                if (!HasNativeLiteral(label.Tick) ||
                    !label.Tick.Competing.All(other => IsSingleDigitEdit(label.Tick.Region.Text, other.Text)) ||
                    (i > 0 && label.Tick.Value <= interior[i - 1].Tick.Value) || !SupportsPrimary(label, anchors[0], anchors[^1], tolerance))
                    return "interior_reading_conflict";
            }
            return null;
        }
    }

    private static bool SupportsPrimary(MeasuredLabel label, MeasuredLabel first, MeasuredLabel last, double tolerance)
    {
        double Fraction(double value) => (value - first.Tick.Value) / (last.Tick.Value - first.Tick.Value);
        double Error(double fraction) => Math.Abs(label.Position -
            (first.Position + fraction * (last.Position - first.Position)));
        double primaryError = Error(Fraction(label.Tick.Value));
        if (!double.IsFinite(primaryError) || primaryError > tolerance) return false;
        foreach (var other in label.Tick.Competing)
        {
            double f = Fraction(other.Value);
            // Both anchor positions and this stroke can vary by the existing
            // fit tolerance. Admit the entire resulting projection interval.
            double error = Error(f), bound = (1 + Math.Abs(1 - f) + Math.Abs(f)) * tolerance;
            if (!double.IsFinite(error) || !double.IsFinite(bound) || error <= bound) return false;
        }
        return true;
    }

    private static bool HasNativeLiteral(ParsedTick tick) =>
        tick.Region.ReviewStatus == OcrReviewStatus.Unreviewed && tick.Region.Text.All(char.IsAsciiDigit) &&
        tick.Region.SingleCtcRead?.Matches(tick.Region) == true;
}
