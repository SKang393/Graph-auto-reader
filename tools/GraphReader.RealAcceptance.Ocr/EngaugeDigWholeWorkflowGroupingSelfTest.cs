// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDigWholeWorkflowGroupingSelfTest
{
    internal static object Run()
    {
        // These are in-memory identity fixtures. PNG parsing is separately tested
        // by EngaugeDigWholeWorkflowTruthAdapterSelfTest; no image is decoded here.
        byte[] sharedImage = [1, 2, 3, 4];
        byte[] otherImage = [5, 6, 7, 8];
        EngaugeDigWholeWorkflowTruth first = Fixture("case-a", "project-a", sharedImage,
            [(10, 20, 1, 20), (20, 30, 2, 30)]);
        EngaugeDigWholeWorkflowTruth second = Fixture("case-b", "project-b", sharedImage,
            [(10, 20, 100, 200), (30, 40, 3, 40)]);
        EngaugeDigWholeWorkflowTruth third = Fixture("case-c", "project-c", otherImage,
            [(40, 50, 4, 50)]);
        IReadOnlyList<EngaugeWorkflowImageGroup> grouped = EngaugeDigWholeWorkflowGrouping.Build(
            [first, second, third], CancellationToken.None);
        var checks = new List<string>();
        Require(grouped.Count == 2 && grouped.Sum(static group => group.Projects.Count) == 3 &&
            grouped.Sum(static group => group.TruthCase.Series.Count) == 3 &&
            grouped.Sum(static group => group.TruthCase.Points.Count) == 5,
            "complete_inventory_grouped_by_exact_image");
        checks.Add("complete_inventory_grouped_by_exact_image");

        EngaugeWorkflowImageGroup shared = grouped.Single(static group => group.Projects.Count == 2);
        Require(shared.CoincidentCrossProjectPointPairs == 1 && shared.TruthCase.Points.Count == 4,
            "coincident_points_counted_and_never_deduplicated");
        Require(shared.TruthCase.Points[0].GraphY == 20 && shared.TruthCase.Points[2].GraphY == 200 &&
            shared.TruthCase.Points[0].SourcePixelX == shared.TruthCase.Points[2].SourcePixelX &&
            shared.Projects[0].Anchors.Count == 3 && shared.Projects[1].Anchors.Count == 3 &&
            shared.Projects[0].Anchors.SequenceEqual(first.Anchors) &&
            shared.Projects[1].Anchors.SequenceEqual(second.Anchors) &&
            shared.Projects[0].Anchors[0].GraphY != shared.Projects[1].Anchors[0].GraphY,
            "per_project_calibration_is_preserved");
        checks.Add("coincident_points_counted_and_never_deduplicated");
        checks.Add("per_project_calibration_is_preserved");

        Require(shared.TruthCase.Series.Select(static series => series.SeriesKey)
                .Distinct(StringComparer.Ordinal).Count() == 2 &&
            shared.TruthCase.Points.Select(static point => point.PointKey)
                .Distinct(StringComparer.Ordinal).Count() == 4 &&
            shared.TruthCase.Relations is null &&
            shared.TruthCase.Points.All(static point => point.AuthoritativePhaseCode is null),
            "colliding_names_get_distinct_ids_without_role_inference");
        checks.Add("colliding_names_get_distinct_ids_without_role_inference");

        IReadOnlyList<EngaugeWorkflowImageGroup> reversed = EngaugeDigWholeWorkflowGrouping.Build(
            [third, second, first], CancellationToken.None);
        Require(grouped.SelectMany(static group => group.TruthCase.Points)
                .SequenceEqual(reversed.SelectMany(static group => group.TruthCase.Points)),
            "input_order_does_not_change_truth_identity");
        checks.Add("input_order_does_not_change_truth_identity");
        byte[] returned = shared.CopyImageBytes();
        returned[0] = 0;
        Require(shared.CopyImageBytes().SequenceEqual(sharedImage), "immutable_shared_source");
        checks.Add("immutable_shared_source");

        WholeWorkflowEvaluationResult failed = WholeWorkflowCsvEvaluator.Evaluate(
            grouped.Select(static group => group.TruthCase).ToArray(),
            grouped.Select(static group => new WholeWorkflowCaseOutput(
                group.TruthCase.CaseKey, group.TruthCase.SourceSha256, false,
                "FIXTURE_RUNTIME_FAILURE", [])).ToArray(),
            new WholeWorkflowEvaluationOptions(5, 0.01, 5), CancellationToken.None);
        Require(failed.FailedCases == 2 && failed.TruthPoints == 5 && failed.TruthSeries == 3 &&
            failed.UniquePointMissing == 5 && failed.MatchedPoints == 0,
            "runtime_failures_keep_all_project_points");
        checks.Add("runtime_failures_keep_all_project_points");

        ExpectFailure([first, first], "DIG_GROUP_DUPLICATE_CASE_IDENTITY");
        checks.Add("duplicate_case_inventory_rejected");
        ExpectFailure([first, Fixture("case-other", "project-a", sharedImage, [(1, 2, 3, 4)])],
            "DIG_GROUP_DUPLICATE_PROJECT_IDENTITY");
        checks.Add("duplicate_project_inventory_rejected");
        ExpectFailure([first, Fixture("case-d", "project-d", sharedImage, [(1, 2, 3, 4)], width: 99)],
            "DIG_GROUP_IMAGE_IDENTITY_CONFLICT");
        checks.Add("conflicting_shared_image_dimensions_rejected");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            _ = EngaugeDigWholeWorkflowGrouping.Build([first], canceled.Token);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
        checks.Add("cancellation_is_propagated");
        return new { status = "pass", check_count = checks.Count, checks,
            private_reads = 0, sealed_reads = 0, model_runs = 0 };
    }

    private static EngaugeDigWholeWorkflowTruth Fixture(
        string caseKey,
        string projectIdentity,
        byte[] image,
        (double X, double Y, double GraphX, double GraphY)[] coordinates,
        int width = 100)
    {
        string imageHash = Convert.ToHexStringLower(SHA256.HashData(image));
        string projectHash = Convert.ToHexStringLower(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(projectIdentity)));
        var points = coordinates.Select((point, index) => new WholeWorkflowTruthPoint(
            "point-" + index, "Curve1", point.X, point.Y, point.GraphX, point.GraphY,
            Math.Round(point.GraphX, MidpointRounding.ToEven), ExportMode.PrintedSession, null)).ToArray();
        var truth = new WholeWorkflowTruthCase(caseKey, imageHash, width, 100,
            [new WholeWorkflowTruthSeries("Curve1")], points, null);
        return new EngaugeDigWholeWorkflowTruth(projectHash, imageHash, width, 100,
            image,
            [new EngaugeDigAxisAnchor(0, 0, 0, coordinates[0].GraphY),
             new EngaugeDigAxisAnchor(0, 100, 0, coordinates[0].GraphY + 100),
             new EngaugeDigAxisAnchor(width, 0, width, coordinates[0].GraphY)],
            [], truth);
    }

    private static void ExpectFailure(EngaugeDigWholeWorkflowTruth[] projects, string code)
    {
        try
        {
            _ = EngaugeDigWholeWorkflowGrouping.Build(projects, CancellationToken.None);
            throw new InvalidOperationException("Expected grouping failure.");
        }
        catch (InvalidOperationException exception) when (exception.Message == code)
        {
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Engauge grouping self-test failed: " + label);
        }
    }
}
