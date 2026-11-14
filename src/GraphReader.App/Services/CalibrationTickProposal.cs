// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Services;

public sealed record CalibrationTickAnchors(CalibrationOcrPoint FirstSessionY0,
    CalibrationOcrPoint FirstSessionYMaximum, CalibrationOcrPoint FinalSessionY0);
public sealed record CalibrationXReviewDraft(int FirstPixelPosition, int FinalPixelPosition,
    int FirstSession, double FinalSession, bool UsesEstimatedPositions);
public sealed record CalibrationYReviewDraft(int ZeroPixelPosition, int MaximumPixelPosition,
    double YMaximum, bool UsesEstimatedPositions);
public sealed record CalibrationPairedTick(string Axis, int PixelPosition, double Value, CalibrationOcrProposal Label)
{
    public bool IsEstimated { get; init; }
}
public sealed record CalibrationTickProposalResult(CalibrationTickAnchors? Anchors, int? FirstSession,
    double? FinalSession, double? YMaximum, IReadOnlyList<CalibrationPairedTick> PairedTicks, IReadOnlyList<string> Reasons)
{
    public CalibrationXReviewDraft? XReviewDraft { get; init; }
    public CalibrationYReviewDraft? YReviewDraft { get; init; }
    public bool UsesEstimatedPositions => PairedTicks.Any(pair => pair.IsEstimated);
    public bool IsComplete => Anchors is not null && FirstSession is not null && FinalSession is not null &&
        YMaximum is not null && Reasons.Count == 0;
}

public static class CalibrationTickProposalReasons
{
    public const string InvalidEvidence = "CALIBRATION_TICKS_INVALID_EVIDENCE";
    public const string InvalidMeasuredTicks = "CALIBRATION_TICKS_INVALID_MEASURED_TICKS";
    public const string InsufficientPairs = "CALIBRATION_TICKS_INSUFFICIENT_PAIRS";
    public const string InvalidNumericLabel = "CALIBRATION_TICKS_INVALID_NUMERIC_LABEL";
    public const string WrongAxis = "CALIBRATION_TICKS_WRONG_AXIS";
    public const string UnpairedLabel = "CALIBRATION_TICKS_UNPAIRED_LABEL";
    public const string AmbiguousAssociation = "CALIBRATION_TICKS_AMBIGUOUS_ASSOCIATION";
    public const string DuplicateAssociation = "CALIBRATION_TICKS_DUPLICATE_ASSOCIATION";
    public const string MissingEndpoint = "CALIBRATION_TICKS_MISSING_ENDPOINT";
    public const string ObservedZeroRequired = "CALIBRATION_TICKS_OBSERVED_ZERO_REQUIRED";
    public const string InvalidRange = "CALIBRATION_TICKS_INVALID_RANGE";
    public const string Nonmonotonic = "CALIBRATION_TICKS_NONMONOTONIC";
    public const string InconsistentSpacing = "CALIBRATION_TICKS_INCONSISTENT_SPACING";
}

/// <summary>Associates observed numeric labels with measured ticks, without applying calibration.</summary>
public static class CalibrationTickProposal
{
    public static CalibrationTickProposalResult Create(RustAxisGeometry geometry, CalibrationOcrResponse response) =>
        CreateCore(geometry, response, estimateEmptyAxes: false);

    /// <summary>Retains independently validated axes for manual review, estimating only axes without measured ticks.</summary>
    public static CalibrationTickProposalResult CreateReviewDraft(RustAxisGeometry geometry, CalibrationOcrResponse response) =>
        CreateCore(geometry, response, estimateEmptyAxes: true, publishPartialAxes: true);

    private static CalibrationTickProposalResult CreateCore(RustAxisGeometry geometry, CalibrationOcrResponse response,
        bool estimateEmptyAxes, bool publishPartialAxes = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(response);
        var reasons = new List<string>();
        var paired = new List<CalibrationPairedTick>();
        if (response.FailureCode is not null || response.Request is not { } request ||
            request.Plot != new CalibrationOcrPlot(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom) ||
            geometry.Left < 0 || geometry.Top < 0 || geometry.Right >= request.Width || geometry.Bottom >= request.Height ||
            geometry.Left >= geometry.Right || geometry.Top >= geometry.Bottom)
            return Failure(paired, [CalibrationTickProposalReasons.InvalidEvidence]);
        int[] xTicks = geometry.XTickPositions.ToArray(), yTicks = geometry.YTickPositions.ToArray();
        if (!ValidTicks(xTicks, geometry.Left, geometry.Right) || !ValidTicks(yTicks, geometry.Top, geometry.Bottom))
            return Failure(paired, [CalibrationTickProposalReasons.InvalidMeasuredTicks]);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var xReasons = new List<string>();
        var yReasons = new List<string>();
        bool identityConflict = false;
        var estimatedIntervals = new Dictionary<string, List<(double Start, double End)>>(StringComparer.Ordinal);
        foreach (CalibrationOcrProposal row in response.Proposals.Where(row => row.Role is "XTick" or "YTick"))
        {
            string axis = row.Role == "XTick" ? "X" : "Y";
            List<string> axisReasons = axis == "X" ? xReasons : yReasons;
            void AddReason(string reason)
            {
                reasons.Add(reason);
                axisReasons.Add(reason);
            }
            if (row.SuggestedAxis != axis)
            {
                identityConflict = true;
                AddReason(CalibrationTickProposalReasons.WrongAxis);
                continue;
            }
            if (!ids.Add(row.RegionId))
            {
                identityConflict = true;
                AddReason(CalibrationTickProposalReasons.DuplicateAssociation);
            }
            if (!row.NumericLiteral || row.FailureCode is not null || !CalibrationNumericLiteral.IsLiteral(row.Text) ||
                row.NumericValue is not { } value || !double.IsFinite(value) ||
                !double.IsFinite(row.Confidence) || row.Confidence is <= 0 or > 1 ||
                GraphNumericParser.Parse(row.Text).Value != value || row.Polygon.Count < 3 ||
                row.Polygon.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                    point.X < 0 || point.Y < 0 || point.X > request.Width || point.Y > request.Height))
            {
                AddReason(CalibrationTickProposalReasons.InvalidNumericLabel);
                continue;
            }
            double left = row.Polygon.Min(point => point.X), right = row.Polygon.Max(point => point.X);
            double top = row.Polygon.Min(point => point.Y), bottom = row.Polygon.Max(point => point.Y);
            if (left >= right || top >= bottom ||
                (axis == "X" ? (top + bottom) / 2 <= geometry.Bottom : (left + right) / 2 >= geometry.Left))
            {
                AddReason(CalibrationTickProposalReasons.WrongAxis);
                continue;
            }
            int[] ticks = axis == "X" ? xTicks : yTicks;
            if (ticks.Length == 0)
            {
                if (!estimateEmptyAxes)
                {
                    AddReason(CalibrationTickProposalReasons.UnpairedLabel);
                    continue;
                }
                double start = axis == "X" ? left : top, end = axis == "X" ? right : bottom;
                int axisStart = axis == "X" ? geometry.Left : geometry.Top;
                int axisEnd = axis == "X" ? geometry.Right : geometry.Bottom;
                int imageLimit = axis == "X" ? request.Width : request.Height;
                // Glyph centers are explicitly approximate; they are not measured tick pixels.
                int center = (int)Math.Round((start + end) / 2, MidpointRounding.AwayFromZero);
                if (end < axisStart || start > axisEnd || center < 0 || center >= imageLimit)
                { AddReason(CalibrationTickProposalReasons.UnpairedLabel); continue; }
                if (!estimatedIntervals.TryGetValue(axis, out var intervals))
                    estimatedIntervals.Add(axis, intervals = []);
                if (intervals.Count >= 128 || intervals.Any(interval => start <= interval.End && end >= interval.Start))
                { AddReason(CalibrationTickProposalReasons.AmbiguousAssociation); continue; }
                intervals.Add((start, end));
                paired.Add(new CalibrationPairedTick(axis, center, value,
                    row with { Polygon = Array.AsReadOnly(row.Polygon.ToArray()) }) { IsEstimated = true });
                continue;
            }
            double position = axis == "X" ? (left + right) / 2 : (top + bottom) / 2;
            double nearestDistance = ticks.Min(tick => Math.Abs(tick - position));
            int[] nearest = ticks.Where(tick => Math.Abs(Math.Abs(tick - position) - nearestDistance) <= 1e-9).ToArray();
            if (nearest.Length != 1)
            {
                AddReason(CalibrationTickProposalReasons.AmbiguousAssociation);
                continue;
            }
            int tickIndex = Array.IndexOf(ticks, nearest[0]);
            double neighbor = Math.Min(tickIndex > 0 ? ticks[tickIndex] - ticks[tickIndex - 1] : double.PositiveInfinity,
                tickIndex + 1 < ticks.Length ? ticks[tickIndex + 1] - ticks[tickIndex] : double.PositiveInfinity);
            // Font-height accommodates glyph alignment; half the neighboring tick spacing prevents cross-lane assignment.
            double labelTolerance = axis == "X" ? Math.Max((right - left) / 2, bottom - top) : bottom - top;
            if (nearestDistance > Math.Min(labelTolerance, neighbor / 2))
            {
                AddReason(CalibrationTickProposalReasons.UnpairedLabel);
                continue;
            }
            if (paired.Any(pair => pair.Axis == axis && pair.PixelPosition == nearest[0]))
                AddReason(CalibrationTickProposalReasons.DuplicateAssociation);
            paired.Add(new(axis, nearest[0], value, row with { Polygon = Array.AsReadOnly(row.Polygon.ToArray()) }));
        }

        CalibrationPairedTick[] x = paired.Where(pair => pair.Axis == "X").OrderBy(pair => pair.PixelPosition).ToArray();
        CalibrationPairedTick[] y = paired.Where(pair => pair.Axis == "Y").OrderBy(pair => pair.PixelPosition).ToArray();
        int xLabelErrors = xReasons.Count, yLabelErrors = yReasons.Count;
        CheckAxis(x, xTicks, true, xReasons);
        CheckAxis(y, yTicks, false, yReasons);
        reasons.AddRange(xReasons.Skip(xLabelErrors));
        reasons.AddRange(yReasons.Skip(yLabelErrors));
        // Every row on an axis must pass, not just a fit's preferred subset.
        // Conflicting region identities cannot establish either partial result.
        CalibrationXReviewDraft? xDraft = publishPartialAxes && !identityConflict && xReasons.Count == 0 &&
            ValidXRange(x[0].Value, x[^1].Value)
                ? new(x[0].PixelPosition, x[^1].PixelPosition, (int)x[0].Value, x[^1].Value,
                    x.Any(pair => pair.IsEstimated)) : null;
        CalibrationYReviewDraft? yDraft = publishPartialAxes && !identityConflict && yReasons.Count == 0 &&
            y[^1].Value == 0 && y.Min(pair => pair.Value) == 0 &&
            y[0].Value > 0 && y[0].Value == y.Max(pair => pair.Value)
                ? new(y[^1].PixelPosition, y[0].PixelPosition, y[0].Value,
                    y.Any(pair => pair.IsEstimated)) : null;
        if (reasons.Count > 0) return Failure(paired, reasons, xDraft, yDraft);

        double first = x[0].Value, final = x[^1].Value, maximum = y[0].Value;
        if (y[^1].Value != 0 || y.Min(pair => pair.Value) != 0)
            reasons.Add(CalibrationTickProposalReasons.ObservedZeroRequired);
        if (!ValidXRange(first, final) || maximum <= 0 ||
            maximum != y.Max(pair => pair.Value)) reasons.Add(CalibrationTickProposalReasons.InvalidRange);
        if (reasons.Count > 0) return Failure(paired, reasons, xDraft, yDraft);
        return new CalibrationTickProposalResult(new(new(x[0].PixelPosition, y[^1].PixelPosition), new(x[0].PixelPosition, y[0].PixelPosition),
                new(x[^1].PixelPosition, y[^1].PixelPosition)), (int)first, final, maximum,
            Array.AsReadOnly(paired.ToArray()), Array.Empty<string>()) { XReviewDraft = xDraft, YReviewDraft = yDraft };
    }

    private static bool ValidXRange(double first, double final) =>
        first >= 1 && first <= int.MaxValue && first == Math.Truncate(first) && final > first;

    private static bool ValidTicks(int[] ticks, int minimum, int maximum) => ticks.Length <= 128 &&
        ticks.All(tick => tick >= minimum && tick <= maximum) && ticks.Zip(ticks.Skip(1)).All(pair => pair.First < pair.Second);

    private static void CheckAxis(CalibrationPairedTick[] pairs, int[] measured, bool xAxis, List<string> reasons)
    {
        if (pairs.Length < 3 || pairs.Select(pair => pair.Value).Distinct().Count() < 3 ||
            pairs.Select(pair => pair.PixelPosition).Distinct().Count() != pairs.Length ||
            pairs.Select(pair => pair.Label.RegionId).Distinct(StringComparer.Ordinal).Count() != pairs.Length)
        {
            reasons.Add(CalibrationTickProposalReasons.InsufficientPairs);
            return;
        }
        bool estimated = measured.Length == 0 && pairs.All(pair => pair.IsEstimated);
        if (!estimated && (measured.Length == 0 || pairs[0].PixelPosition != measured[0] || pairs[^1].PixelPosition != measured[^1]))
            reasons.Add(CalibrationTickProposalReasons.MissingEndpoint);
        TickCandidate[] candidates = pairs.Select(pair => new TickCandidate(pair.Label.RegionId, pair.PixelPosition, pair.Value, pair.Label.Confidence)).ToArray();
        TickResolutionResult sequence = MonotonicTickResolver.Resolve(candidates,
            xAxis ? TickAxisDirection.IncreasingWithPixels : TickAxisDirection.DecreasingWithPixels);
        if (sequence.NeedsReview || sequence.RejectedTicks.Count > 0)
            reasons.Add(CalibrationTickProposalReasons.Nonmonotonic);
        LinearTransformFitResult fit = xAxis
            ? RobustCalibration.FitX(pairs.Select(pair => new PrintedXTickEvidence(pair.Label.RegionId, pair.PixelPosition, pair.Value, pair.Label.Confidence)).ToArray())
            : RobustCalibration.FitY(pairs.Select(pair => new NumericTickEvidence(pair.Label.RegionId, pair.PixelPosition, pair.Value, pair.Label.Confidence)).ToArray());
        if (!fit.IsValid || fit.Diagnostics.OutlierIds.Count > 0 || fit.Diagnostics.InlierIds.Count != pairs.Length)
            reasons.Add(CalibrationTickProposalReasons.InconsistentSpacing);
    }

    private static CalibrationTickProposalResult Failure(List<CalibrationPairedTick> pairs, IEnumerable<string> reasons,
        CalibrationXReviewDraft? xDraft = null, CalibrationYReviewDraft? yDraft = null) =>
        new(null, null, null, null, Array.AsReadOnly(pairs.ToArray()),
            Array.AsReadOnly(reasons.Distinct(StringComparer.Ordinal).ToArray())) { XReviewDraft = xDraft, YReviewDraft = yDraft };
}
