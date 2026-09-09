// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Classification;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Legends.Tests;

[TestClass]
public sealed class LegendReasoningServiceTests
{
    private const string ArrowSeriesId = "00000000-0000-0000-0000-000000000008";
    private const string ArrowMarkerId = "00000000-0000-0000-0000-000000000009";

    [TestMethod]
    public async Task ResultUsesFrozenVisionEnvelope()
    {
        var request = LegendTestFixtures.Request();

        var result = await ResolveAsync(request);

        Assert.AreEqual(LegendReasoningContract.Version, result.ContractVersion);
        Assert.IsTrue(Guid.TryParseExact(result.RunId, "D", out _));
        Assert.AreEqual(request.ProjectId, result.ProjectId);
        Assert.AreEqual(request.PanelId, result.PanelId);
        Assert.AreEqual(LegendReasoningContract.Stage, result.Stage);
        Assert.AreEqual(request.Options.StageVersion, result.StageVersion);
        Assert.AreEqual(request.InputSha256, result.InputSha256);
        Assert.AreEqual(LegendReasoningContract.CoordinateSpace, result.CoordinateSpace);
        Assert.IsNull(result.Model);
        Assert.AreSame(result.Payload.Series, result.Series);
    }

    [TestMethod]
    public async Task NoLegendFallsBackToStableSymbolAndAccessibleName()
    {
        var result = await ResolveAsync(LegendTestFixtures.Request());

        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(2, result.Series);
        var filled = result.Series.Single(static series => series.SeriesId == LegendTestFixtures.FilledSeriesId);
        Assert.AreEqual("● filled circle", filled.Name);
        Assert.AreEqual("●", filled.Symbol);
        Assert.AreEqual("filled circle", filled.AccessibleName);
        Assert.AreEqual(LegendEvidenceSource.SymbolFallback, filled.Source);
        Assert.IsNull(filled.EntryId);
        Assert.AreEqual(LegendSemanticHint.Unknown, filled.Semantic.Hint);
    }

    [TestMethod]
    public async Task OpenCircleLegendEmitsGeneralizationEvidence()
    {
        var request = LegendTestFixtures.Request(
            textRegions: [LegendTestFixtures.Text("generalization", 105, 80, "Generalization")],
            glyphs: [LegendTestFixtures.Glyph("open", 85, 82, MarkerShape.Circle, MarkerFill.Open, [0f, 1f, 0f])]);

        var result = await ResolveAsync(request);

        var series = result.Series.Single(static item => item.SeriesId == LegendTestFixtures.OpenSeriesId);
        Assert.AreEqual("Generalization", series.Name);
        Assert.AreEqual(LegendEvidenceSource.DetectedLegend, series.Source);
        Assert.AreEqual(LegendSemanticHint.Generalization, series.Semantic.Hint);
        Assert.AreEqual("generalization", series.Semantic.NormalizedText);
        Assert.IsGreaterThanOrEqualTo(0.60, series.Semantic.Confidence);
        Assert.AreEqual(LegendTestFixtures.OpenSeriesId, result.Regions.Single().Entries.Single().NormalizedSeriesId);
        Assert.IsEmpty(result.ExcludedArtifactMarkerIds);
    }

    [TestMethod]
    public async Task ResolvedLocalLegendGlyphIsExcludedWhileItsSeriesDataMarkerRemains()
    {
        const string dataMarkerId = "00000000-0000-0000-0000-000000000014";
        var openSeries = new LegendSeriesCandidate(
            LegendTestFixtures.OpenSeriesId,
            MarkerShape.Circle,
            MarkerFill.Open,
            "○",
            "open circle",
            [LegendTestFixtures.OpenMarkerId, dataMarkerId],
            [0f, 1f, 0f]);
        var request = LegendTestFixtures.Request(
            textRegions: [LegendTestFixtures.Text("generalization", 105, 80, "Generalization")],
            glyphs:
            [
                LegendTestFixtures.Glyph(
                    LegendTestFixtures.OpenMarkerId,
                    85,
                    82,
                    MarkerShape.Circle,
                    MarkerFill.Open,
                    [0f, 1f, 0f]),
            ],
            series: [openSeries],
            markers:
            [
                new LegendPlotMarker(
                    LegendTestFixtures.OpenMarkerId,
                    LegendTestFixtures.OpenSeriesId,
                    new LegendPoint(90, 87),
                    MarkerShape.Circle,
                    MarkerFill.Open),
                new LegendPlotMarker(
                    dataMarkerId,
                    LegendTestFixtures.OpenSeriesId,
                    new LegendPoint(250, 180),
                    MarkerShape.Circle,
                    MarkerFill.Open),
            ]);

        var result = await ResolveAsync(request);

        CollectionAssert.AreEquivalent(
            new[] { LegendTestFixtures.OpenMarkerId },
            result.ExcludedArtifactMarkerIds.ToArray());
        var series = result.Series.Single();
        Assert.AreEqual("Generalization", series.Name);
        Assert.AreEqual(LegendEvidenceSource.DetectedLegend, series.Source);
    }

    [TestMethod]
    public async Task UnresolvedLegendGlyphRemainsAReviewablePlotMarker()
    {
        var request = LegendTestFixtures.Request(
            textRegions: [LegendTestFixtures.Text("annotation", 105, 80, "Generalization", OcrTextRole.Annotation)],
            glyphs:
            [
                LegendTestFixtures.Glyph(
                    LegendTestFixtures.OpenMarkerId,
                    85,
                    82,
                    MarkerShape.Circle,
                    MarkerFill.Open,
                    [0f, 1f, 0f]),
            ],
            series: [LegendTestFixtures.DefaultSeries()[1]],
            markers: [LegendTestFixtures.DefaultMarkers()[1]]);

        var result = await ResolveAsync(request);

        Assert.IsEmpty(result.ExcludedArtifactMarkerIds);
        Assert.HasCount(1, result.Series);
        Assert.AreEqual(LegendEvidenceSource.SymbolFallback, result.Series[0].Source);
    }

    [TestMethod]
    public async Task ServicePreservesBroadSemanticNormalizationFromRegionResolver()
    {
        var request = LegendTestFixtures.Request(
            textRegions: [LegendTestFixtures.Text("generalization", 105, 80, "GENERALIZATION probes!")],
            glyphs: [LegendTestFixtures.Glyph("open", 85, 82, MarkerShape.Circle, MarkerFill.Open, [0f, 1f, 0f])]);

        var result = await ResolveAsync(request);

        var series = result.Series.Single(static item => item.SeriesId == LegendTestFixtures.OpenSeriesId);
        Assert.AreEqual(LegendSemanticHint.Generalization, series.Semantic.Hint);
        Assert.AreEqual("generalization", series.Semantic.NormalizedText);
        Assert.AreEqual(series.Semantic, result.Regions.Single().Entries.Single().Semantic);
    }

    [TestMethod]
    public async Task ParticipantAtRightIsPanelMetadataNotSeriesName()
    {
        var request = LegendTestFixtures.Request(
            textRegions:
            [
                LegendTestFixtures.Text("participant", 405, 130, "Participant A", OcrTextRole.Participant, 0.94),
            ]);

        var result = await ResolveAsync(request);

        Assert.HasCount(1, result.Participants);
        Assert.AreEqual("Participant A", result.Participants[0].Name);
        Assert.AreEqual("participant", result.Participants[0].TextRegionId);
        Assert.IsFalse(result.Series.Any(static series => series.Name == "Participant A"));
    }

    [TestMethod]
    public async Task AmbiguousNearbyTextIsNotInferredAsParticipant()
    {
        var request = LegendTestFixtures.Request(
            textRegions:
            [
                LegendTestFixtures.Text("ambiguous", 100, 130, "Participant A", OcrTextRole.Other, 0.94),
            ]);

        var result = await ResolveAsync(request);

        Assert.IsEmpty(result.Participants);
    }

    [TestMethod]
    public async Task RejectedParticipantTextIsNotPanelMetadata()
    {
        var rejected = LegendTestFixtures.Text(
            "participant-rejected",
            405,
            130,
            "Participant A",
            OcrTextRole.Participant,
            0.94) with
        {
            ReviewStatus = OcrReviewStatus.Rejected,
        };
        var request = LegendTestFixtures.Request(textRegions: [rejected]);

        var result = await ResolveAsync(request);

        Assert.IsEmpty(result.Participants);
    }

    [TestMethod]
    public async Task CrossPanelLegendPropagatesNameAndSourcePanelProvenance()
    {
        var peer = new LegendPeerPanelEvidence(LegendTestFixtures.PeerPanelId, [LegendTestFixtures.PeerEntry()]);
        var request = LegendTestFixtures.Request(peers: [peer]);

        var result = await ResolveAsync(request);

        var series = result.Series.Single(static item => item.SeriesId == LegendTestFixtures.OpenSeriesId);
        Assert.AreEqual("Generalization", series.Name);
        Assert.AreEqual(LegendEvidenceSource.CrossPanel, series.Source);
        Assert.AreEqual(LegendTestFixtures.PeerPanelId, series.SourcePanelId);
        Assert.AreEqual("peer-entry", series.EntryId);
        Assert.AreEqual(LegendSemanticHint.Generalization, series.Semantic.Hint);
    }

    [TestMethod]
    public async Task UnreliableCrossPanelLabelCannotOverrideFallback()
    {
        var unreliable = LegendTestFixtures.PeerEntry() with { Confidence = 0 };
        var peer = new LegendPeerPanelEvidence(LegendTestFixtures.PeerPanelId, [unreliable]);
        var request = LegendTestFixtures.Request(peers: [peer]);

        var result = await ResolveAsync(request);

        var series = result.Series.Single(static item => item.SeriesId == LegendTestFixtures.OpenSeriesId);
        Assert.AreEqual(LegendEvidenceSource.SymbolFallback, series.Source);
        Assert.AreEqual("○ open circle", series.Name);
    }

    [TestMethod]
    public async Task UserConfirmedNameCannotBeOverwrittenByDetectedLegend()
    {
        var confirmed = LegendTestFixtures.Series(
            LegendTestFixtures.FilledSeriesId,
            LegendTestFixtures.FilledMarkerId,
            MarkerShape.Circle,
            MarkerFill.Filled,
            "●",
            "filled circle",
            [1f, 0f, 0f],
            "Verified treatment",
            confirmed: true);
        var request = LegendTestFixtures.Request(
            textRegions: [LegendTestFixtures.Text("legend", 105, 51, "Wrong detected name")],
            glyphs: [LegendTestFixtures.Glyph("glyph", 85, 52, MarkerShape.Circle, MarkerFill.Filled, [1f, 0f, 0f])],
            series: [confirmed],
            markers: [LegendTestFixtures.DefaultMarkers()[0]]);

        var result = await ResolveAsync(request);

        Assert.HasCount(1, result.Series);
        Assert.AreEqual("Verified treatment", result.Series[0].Name);
        Assert.AreEqual(LegendEvidenceSource.UserConfirmed, result.Series[0].Source);
        Assert.IsTrue(result.Series[0].UserConfirmedPreserved);
    }

    [TestMethod]
    public async Task ConfirmedSeriesDoesNotConsumeLegendNeededByUnconfirmedSeries()
    {
        const string confirmedSeriesId = "00000000-0000-0000-0000-000000000010";
        const string detectedSeriesId = "00000000-0000-0000-0000-000000000011";
        const string confirmedMarkerId = "00000000-0000-0000-0000-000000000012";
        const string detectedMarkerId = "00000000-0000-0000-0000-000000000013";
        var confirmed = new LegendSeriesCandidate(
            confirmedSeriesId,
            MarkerShape.Circle,
            MarkerFill.Filled,
            "●",
            "filled circle",
            [confirmedMarkerId],
            [1f, 0f, 0f],
            "Verified treatment",
            true);
        var unconfirmed = new LegendSeriesCandidate(
            detectedSeriesId,
            MarkerShape.Circle,
            MarkerFill.Filled,
            "●",
            "filled circle",
            [detectedMarkerId],
            [1f, 0f, 0f]);
        var request = LegendTestFixtures.Request(
            textRegions: [LegendTestFixtures.Text("legend", 105, 51, "Detected treatment")],
            glyphs: [LegendTestFixtures.Glyph("glyph", 85, 52, MarkerShape.Circle, MarkerFill.Filled, [1f, 0f, 0f])],
            series: [confirmed, unconfirmed],
            markers:
            [
                new LegendPlotMarker(
                    confirmedMarkerId,
                    confirmedSeriesId,
                    new LegendPoint(210, 140),
                    MarkerShape.Circle,
                    MarkerFill.Filled),
                new LegendPlotMarker(
                    detectedMarkerId,
                    detectedSeriesId,
                    new LegendPoint(230, 160),
                    MarkerShape.Circle,
                    MarkerFill.Filled),
            ]);

        var result = await ResolveAsync(request);

        Assert.AreEqual("Verified treatment", result.Series.Single(item => item.SeriesId == confirmedSeriesId).Name);
        Assert.AreEqual("Detected treatment", result.Series.Single(item => item.SeriesId == detectedSeriesId).Name);
        Assert.AreEqual(detectedSeriesId, result.Regions.Single().Entries.Single().NormalizedSeriesId);
    }

    [TestMethod]
    public async Task RequestAndResultCollectionsAreDefensiveCopies()
    {
        var texts = new List<LegendTextRegion>
        {
            LegendTestFixtures.Text("legend", 105, 51, "Treatment"),
        };
        var glyphEmbedding = new List<float> { 1f, 0f, 0f };
        var glyphs = new List<LegendGlyphCandidate>
        {
            new("glyph", new LegendRectangle(85, 52, 10, 10), MarkerShape.Circle, MarkerFill.Filled, glyphEmbedding, 0.97),
        };
        var request = LegendTestFixtures.Request(textRegions: texts, glyphs: glyphs);

        texts.Clear();
        glyphs.Clear();
        glyphEmbedding[0] = 0f;

        Assert.HasCount(1, request.TextRegions);
        Assert.HasCount(1, request.Glyphs);
        Assert.AreEqual(1f, request.Glyphs[0].Embedding[0]);

        var result = await ResolveAsync(request);
        Assert.HasCount(1, result.Regions);
        Assert.IsFalse(result.Regions is IList<LegendRegion>);
        Assert.IsFalse(result.Series is IList<LegendSeriesResolution>);
    }

    [TestMethod]
    public async Task InvalidContractReturnsStructuredFailure()
    {
        var request = LegendTestFixtures.Request(contractVersion: LegendReasoningContract.Version + 1);

        var result = await ResolveAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Code));
        Assert.IsTrue(result.Failure.Recoverable);
        Assert.IsEmpty(result.Regions);
        Assert.IsEmpty(result.Callouts);
    }

    [TestMethod]
    public async Task InvalidEnvelopeIdentifiersReturnSchemaSafeFailureEnvelope()
    {
        var request = RequestWithEnvelope("not-a-project-uuid", "not-a-panel-uuid", "not-a-hash");

        var result = await ResolveAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(Guid.Empty.ToString(), result.ProjectId);
        Assert.AreEqual(Guid.Empty.ToString(), result.PanelId);
        Assert.AreEqual(new string('0', 64), result.InputSha256);
        Assert.IsTrue(Guid.TryParseExact(result.RunId, "D", out _));
    }

    [TestMethod]
    public async Task InvalidInputHashPreservesValidIdsAndReturnsSafeHash()
    {
        var request = RequestWithEnvelope(
            LegendTestFixtures.ProjectId,
            LegendTestFixtures.PanelId,
            "not-a-hash");

        var result = await ResolveAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("LEGEND_INVALID_INPUT_HASH", result.Failure?.Code);
        Assert.AreEqual(LegendTestFixtures.ProjectId, result.ProjectId);
        Assert.AreEqual(LegendTestFixtures.PanelId, result.PanelId);
        Assert.AreEqual(new string('0', 64), result.InputSha256);
    }

    [TestMethod]
    public async Task InvalidGeometryReturnsStructuredFailure()
    {
        var request = new LegendReasoningRequest(
            LegendTestFixtures.ProjectId,
            LegendTestFixtures.PanelId,
            LegendTestFixtures.InputSha256,
            new LegendRectangle(0, 0, 0, 300),
            LegendTestFixtures.PlotBounds,
            [],
            [],
            LegendTestFixtures.DefaultSeries(),
            LegendTestFixtures.DefaultMarkers());

        var result = await ResolveAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure.Code));
    }

    [TestMethod]
    public async Task ZeroDistanceOptionReturnsStructuredFailure()
    {
        var request = LegendTestFixtures.Request(options: new LegendReasoningOptions
        {
            MaximumGlyphTextHorizontalGap = 0,
        });

        var result = await ResolveAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("LEGEND_INVALID_OPTIONS", result.Failure?.Code);
    }

    [TestMethod]
    public async Task MarkerWithMismatchedSeriesEvidenceReturnsStructuredFailure()
    {
        var markers = new[]
        {
            new LegendPlotMarker(
                LegendTestFixtures.FilledMarkerId,
                LegendTestFixtures.OpenSeriesId,
                new LegendPoint(220, 150),
                MarkerShape.Circle,
                MarkerFill.Filled),
            LegendTestFixtures.DefaultMarkers()[1],
        };
        var request = LegendTestFixtures.Request(markers: markers);

        var result = await ResolveAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("LEGEND_INVALID_MARKER", result.Failure?.Code);
    }

    [TestMethod]
    public async Task ArrowheadMarkerIsExcludedFromTriangleSeriesResolution()
    {
        var triangleSeries = new LegendSeriesCandidate(
            ArrowSeriesId,
            MarkerShape.TriangleUp,
            MarkerFill.Filled,
            "▲",
            "filled triangle",
            [ArrowMarkerId],
            [0f, 0f, 1f]);
        var arrowMarker = new LegendPlotMarker(
            ArrowMarkerId,
            ArrowSeriesId,
            new LegendPoint(207, 150),
            MarkerShape.TriangleUp,
            MarkerFill.Filled);
        var request = LegendTestFixtures.Request(
            textRegions: [LegendTestFixtures.Text("annotation", 100, 100, "Change condition", OcrTextRole.Annotation)],
            series: [.. LegendTestFixtures.DefaultSeries(), triangleSeries],
            markers: [.. LegendTestFixtures.DefaultMarkers(), arrowMarker],
            strokes:
            [
                new LegendStrokeCandidate(
                    "stroke",
                    new LegendPoint(172, 110),
                    new LegendPoint(207, 150),
                    1.5,
                    0.95),
            ],
            triangles:
            [
                new LegendTriangleCandidate(
                    "arrowhead",
                    [new LegendPoint(212, 150), new LegendPoint(204, 145), new LegendPoint(204, 155)],
                    0.96),
            ]);

        var result = await ResolveAsync(request);

        CollectionAssert.Contains(result.ExcludedArtifactMarkerIds.ToArray(), ArrowMarkerId);
        Assert.IsFalse(result.Series.Any(static series => series.SeriesId == ArrowSeriesId));
        Assert.IsTrue(result.Artifacts.Any(static artifact => artifact.Kind == LegendArtifactKind.Arrowhead));
    }

    [TestMethod]
    public async Task ResolveAsyncHonorsPreCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new LegendReasoningService().ResolveAsync(
                LegendTestFixtures.Request(),
                cancellation.Token));
    }

    private static async Task<LegendReasoningResult> ResolveAsync(LegendReasoningRequest request) =>
        await new LegendReasoningService().ResolveAsync(request, CancellationToken.None);

    private static LegendReasoningRequest RequestWithEnvelope(
        string projectId,
        string panelId,
        string inputSha256) =>
        new(
            projectId,
            panelId,
            inputSha256,
            LegendTestFixtures.PanelBounds,
            LegendTestFixtures.PlotBounds,
            [],
            [],
            LegendTestFixtures.DefaultSeries(),
            LegendTestFixtures.DefaultMarkers());
}
