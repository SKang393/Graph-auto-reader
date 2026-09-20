// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Legends;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Markers.Grouping;
using GraphReader.Phases;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionSeriesPhaseContextTests
{
    [TestMethod]
    [DataRow("first", "last")]
    [DataRow("last", "first")]
    public void FinalPhasesReplaceTheWrongBaselineWithoutRegrouping(string baselineId, string fragmentId)
    {
        MarkerGroupingState original = Grouping(
            Series(baselineId, MarkerSeriesRole.Baseline, ["baseline"]),
            Series(fragmentId, MarkerSeriesRole.Baseline, ["intervention-fragment", "reversal-fragment"]),
            Series("target", MarkerSeriesRole.Intervention, ["target-point"], fragmentId));
        PhaseReasoningPayload phases = Phases(
            ("baseline", PhaseNormalizedType.Baseline),
            ("intervention-fragment", PhaseNormalizedType.Intervention),
            ("reversal-fragment", PhaseNormalizedType.Baseline),
            ("target-point", PhaseNormalizedType.Intervention));
        SeriesPhaseContextResult result = ProductionSeriesPhaseContext.Resolve(original, phases, Legend(), CancellationToken.None);
        Assert.AreEqual(MarkerSeriesRole.Intervention, result.Grouping.Series.Single(series => series.SeriesId == fragmentId).SemanticRole);
        Assert.IsTrue(result.Grouping.Series.Where(static series => series.SemanticRole == MarkerSeriesRole.Intervention)
            .All(series => series.SharedBaselineSeriesId == baselineId));
        Assert.AreEqual(fragmentId, original.Series.Single(static series => series.SeriesId == "target").SharedBaselineSeriesId);
        CollectionAssert.AreEqual(original.Markers.ToArray(), result.Grouping.Markers.ToArray());
        foreach (MarkerSeries series in original.Series)
            CollectionAssert.AreEqual(series.MarkerIds.ToArray(), result.Grouping.Series.Single(item => item.SeriesId == series.SeriesId).MarkerIds.ToArray());
    }

    [TestMethod]
    public void SeveralActualBaselinesAreFlaggedInsteadOfChosenByIdentifier()
    {
        MarkerGroupingState original = Grouping(
            Series("baseline-one", MarkerSeriesRole.Baseline, ["a1"]),
            Series("baseline-two", MarkerSeriesRole.Baseline, ["a2"]),
            Series("target", MarkerSeriesRole.Intervention, ["b"], "baseline-one"));
        SeriesPhaseContextResult result = ProductionSeriesPhaseContext.Resolve(original,
            Phases(("a1", PhaseNormalizedType.Baseline), ("a2", PhaseNormalizedType.Baseline), ("b", PhaseNormalizedType.Intervention)),
            Legend(), CancellationToken.None);
        Assert.IsNull(result.Grouping.Series.Single(static item => item.SeriesId == "target").SharedBaselineSeriesId);
        Assert.Contains("shared_baseline_relation_ambiguous", result.Warnings.ToArray());
    }

    [TestMethod]
    [DataRow(LegendSemanticHint.Maintenance, MarkerSeriesRole.Maintenance)]
    [DataRow(LegendSemanticHint.Generalization, MarkerSeriesRole.Generalization)]
    public void ExplicitLegendProbeRoleSurvivesPhaseReconciliation(LegendSemanticHint hint, MarkerSeriesRole expected)
    {
        MarkerGroupingState original = Grouping(Series("probe", MarkerSeriesRole.Intervention, ["p"]));
        var resolution = new LegendSeriesResolution("probe", "Probe", "circle", "circle", LegendEvidenceSource.DetectedLegend,
            null, null, 0.95, new(hint, "Probe", 0.95), true);
        SeriesPhaseContextResult result = ProductionSeriesPhaseContext.Resolve(original,
            Phases(("p", PhaseNormalizedType.Intervention)), Legend(resolution), CancellationToken.None);
        Assert.AreEqual(expected, result.Grouping.Series.Single().SemanticRole);
    }

    [TestMethod]
    public void UnknownPhasesDoNotManufactureAnInterventionRole()
    {
        SeriesPhaseContextResult result = ProductionSeriesPhaseContext.Resolve(
            Grouping(Series("unknown", MarkerSeriesRole.Baseline, ["p"])),
            Phases(("p", PhaseNormalizedType.Unknown)), Legend(), CancellationToken.None);
        Assert.AreEqual(MarkerSeriesRole.Unknown, result.Grouping.Series.Single().SemanticRole);
        Assert.IsTrue(result.Warnings.Any(static item => item.StartsWith("series_phase_role_requires_review:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AConnectedSeriesSpanningBaselineAndInterventionRemainsOneInterventionSeries()
    {
        MarkerGroupingState original = Grouping(Series("one", MarkerSeriesRole.Baseline, ["a", "b"]));
        SeriesPhaseContextResult result = ProductionSeriesPhaseContext.Resolve(original,
            Phases(("a", PhaseNormalizedType.Baseline), ("b", PhaseNormalizedType.Intervention)), Legend(), CancellationToken.None);
        Assert.HasCount(1, result.Grouping.Series);
        Assert.HasCount(2, result.Grouping.Series.Single().MarkerIds);
        Assert.AreEqual(MarkerSeriesRole.Intervention, result.Grouping.Series.Single().SemanticRole);
        Assert.IsNull(result.Grouping.Series.Single().SharedBaselineSeriesId);
    }

    [TestMethod]
    public void MissingPhaseAssignmentAndCancellationFailClosed()
    {
        MarkerGroupingState original = Grouping(Series("one", MarkerSeriesRole.Baseline, ["p"]));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionSeriesPhaseContext.Resolve(original, Phases(), Legend(), CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionSeriesPhaseContext.Resolve(
            original, Phases(), Legend(), new CancellationToken(canceled: true)));
    }

    private static MarkerSeries Series(string id, MarkerSeriesRole role, string[] points, string? shared = null) =>
        new(id, "circle", MarkerShape.Circle, MarkerFill.Filled, id, role, points, 0.95, sharedBaselineSeriesId: shared);

    private static MarkerGroupingState Grouping(params MarkerSeries[] series) => new(
        series.SelectMany(static item => item.MarkerIds).Select((id, index) => new MarkerGroupingEvidence(
            new ClassifiedMarker(new MarkerCenter(id, new MarkerPoint(index * 10, 30), 3, 0, 0.95, MarkerSourceImage.Original),
                MarkerShape.Circle, MarkerFill.Filled, "circle", "circle", 0, 0.95, 0.95, Enumerable.Repeat(0.1f, 12)), index + 1)), [], series);

    private static PhaseReasoningPayload Phases(params (string PointId, PhaseNormalizedType Type)[] points) => new([],
        points.Select(static point => point.Type).Distinct().Select((type, index) =>
            new PhaseRegion(type.ToString(), index + 1, type.ToString(), type, type.ToString(), index * 100, (index + 1) * 100,
                null, null, 0.95, PhaseEvidenceSource.Ocr)),
        points.Select(static point => new PhasePointAssignment(point.PointId, point.Type.ToString(), 0)), [], new());

    private static LegendReasoningPayload Legend(params LegendSeriesResolution[] series) => new([], series, [], [], [], []);
}
