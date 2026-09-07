// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.IO;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Markers.Detection;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionProposalMarkerCenterAdapterTests
{
    [TestMethod]
    public async Task CandidateEvaluationBatchesPatchesAndIsDeterministic()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateAdapter(runner);
        MarkerImageFrame frame = FrameWithMarkers();
        IReadOnlyList<MarkerCenter> first = await adapter.DetectCandidateAsync(frame, MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None);
        IReadOnlyList<MarkerCenter> second = await adapter.DetectCandidateAsync(frame, MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None);

        Assert.IsTrue(runner.BatchSizes.Count > 1);
        Assert.IsTrue(runner.BatchSizes.All(static size => size <= ProductionProposalMarkerCenterAdapter.BatchSize));
        Assert.AreEqual(first.Count, second.Count);
        CollectionAssert.AreEqual(first.Select(static item => item.Center).ToArray(), second.Select(static item => item.Center).ToArray());
        Assert.IsTrue(first.Count > 0);
        Assert.IsTrue(first.Count < runner.TotalPatches);
        for (int left = 0; left < first.Count; left++)
        {
            for (int right = left + 1; right < first.Count; right++)
            {
                double distance = Math.Sqrt(Math.Pow(first[left].Center.X - first[right].Center.X, 2) + Math.Pow(first[left].Center.Y - first[right].Center.Y, 2));
                Assert.IsTrue(distance >= ProductionProposalMarkerCenterAdapter.MinimumCenterSeparationForTesting);
            }
        }
    }

    [TestMethod]
    public async Task CandidateDiagnosticsPreserveCandidateOutputAndReportStageCounts()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateAdapter(runner);
        MarkerImageFrame frame = FrameWithMarkers();
        MarkerPolygon polygon = MarkerPolygon.FromRectangle(new(0, 0, 512, 512));

        IReadOnlyList<MarkerCenter> legacy = await adapter.DetectCandidateAsync(frame, polygon, CancellationToken.None);
        ProposalMarkerCandidateDiagnosticResult diagnostic = await adapter.DetectCandidateWithDiagnosticsAsync(frame, polygon, CancellationToken.None);

        CollectionAssert.AreEqual(legacy.Select(static item => item.Center).ToArray(), diagnostic.Candidates.Select(static item => item.Center).ToArray());
        CollectionAssert.AreEqual(legacy.Select(static item => item.Radius).ToArray(), diagnostic.Candidates.Select(static item => item.Radius).ToArray());
        CollectionAssert.AreEqual(legacy.Select(static item => item.CenterConfidence).ToArray(), diagnostic.Candidates.Select(static item => item.CenterConfidence).ToArray());
        ProposalMarkerStageCounters counters = diagnostic.StageCounters;
        Assert.IsTrue(counters.ProposalGridPositionsConsidered > 0);
        Assert.IsTrue(counters.EmittedProposals > 0);
        Assert.AreEqual(counters.EmittedProposals, counters.InferenceOutputs);
        Assert.AreEqual(counters.OutputsAbove025, counters.InferenceOutputs);
        Assert.AreEqual(
            counters.OutputsAbove025,
            counters.DecodedPointsMasked +
            counters.GeometryConsensusRejectsAfterRefinementAttempts +
            counters.DecodedPointsOutsidePlot +
            counters.CandidatesBeforeNms);
        Assert.AreEqual(counters.CandidatesBeforeNms, counters.FinalCandidates + counters.NmsSuppressions);
        Assert.AreEqual(diagnostic.Candidates.Count, counters.FinalCandidates);
        Assert.AreEqual(counters.CandidatesBeforeNms, diagnostic.PreNmsCandidates.Count);
        Assert.IsTrue(diagnostic.PreNmsCandidates.Count >= diagnostic.Candidates.Count);
        Assert.AreEqual(counters.ProposalGridPositionsConsidered, diagnostic.GridProposalCenters.Count);
        Assert.AreEqual(
            counters.ProposalGridPositionsConsidered - counters.LowInkRejects,
            diagnostic.InkSupportedProposalCenters.Count);
        Assert.AreEqual(
            diagnostic.InkSupportedProposalCenters.Count - counters.OcrMaskRejects,
            diagnostic.OcrUnmaskedProposalCenters.Count);
        Assert.AreEqual(
            diagnostic.OcrUnmaskedProposalCenters.Count - counters.ArtifactMaskRejects,
            diagnostic.EmittedProposalCenters.Count);
        Assert.AreEqual(counters.EmittedProposals, diagnostic.EmittedProposalCenters.Count);
        Assert.AreEqual(counters.OutputsAbove025, diagnostic.AboveThresholdDecodedPoints.Count);
    }

    [TestMethod]
    public async Task CandidateDiagnosticsCountLowInkAndMaskRejectStages()
    {
        MarkerPolygon polygon = MarkerPolygon.FromRectangle(new(0, 0, 512, 512));
        var clearAdapter = CreateAdapter(new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray()));
        ProposalMarkerStageCounters lowInk = (await clearAdapter.DetectCandidateWithDiagnosticsAsync(
            new MarkerImageFrame(512, 512, 1, Enumerable.Repeat(1f, 512 * 512).ToArray(), MarkerSourceImage.Original,
                MarkerAffineTransform.Identity, MarkerMask.Empty(512, 512), MarkerMask.Empty(512, 512)), polygon, CancellationToken.None)).StageCounters;
        Assert.IsTrue(lowInk.ProposalGridPositionsConsidered > 0);
        Assert.AreEqual(lowInk.ProposalGridPositionsConsidered, lowInk.LowInkRejects);
        Assert.AreEqual(0, lowInk.EmittedProposals);

        MarkerImageFrame frame = FrameWithMarkers();
        var ocrMaskedAdapter = CreateAdapter(new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray()));
        ProposalMarkerStageCounters ocrMasked = (await ocrMaskedAdapter.DetectCandidateWithDiagnosticsAsync(
            frame with { OcrMask = new MarkerMask(512, 512, Enumerable.Repeat(1f, 512 * 512).ToArray()) }, polygon, CancellationToken.None)).StageCounters;
        Assert.IsTrue(ocrMasked.OcrMaskRejects > 0);
        Assert.AreEqual(0, ocrMasked.InferenceOutputs);

        var artifactMaskedAdapter = CreateAdapter(new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray()));
        ProposalMarkerStageCounters artifactMasked = (await artifactMaskedAdapter.DetectCandidateWithDiagnosticsAsync(
            frame with { ArtifactMask = new MarkerMask(512, 512, Enumerable.Repeat(1f, 512 * 512).ToArray()) }, polygon, CancellationToken.None)).StageCounters;
        Assert.IsTrue(artifactMasked.ArtifactMaskRejects > 0);
        Assert.AreEqual(0, artifactMasked.InferenceOutputs);
    }

    [TestMethod]
    public async Task MasksRejectProposalSupport()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateAdapter(runner);
        MarkerImageFrame clear = FrameWithMarkers();
        MarkerImageFrame masked = clear with
        {
            OcrMask = new MarkerMask(512, 512, Enumerable.Repeat(1f, 512 * 512).ToArray()),
        };
        var polygon = MarkerPolygon.FromRectangle(new(0, 0, 512, 512));
        IReadOnlyList<MarkerCenter> clearMarkers = await adapter.DetectCandidateAsync(clear, polygon, CancellationToken.None);
        int clearPatches = runner.TotalPatches;
        IReadOnlyList<MarkerCenter> maskedMarkers = await adapter.DetectCandidateAsync(masked, polygon, CancellationToken.None);
        Assert.IsTrue(maskedMarkers.Count <= clearMarkers.Count);
        Assert.IsTrue(runner.TotalPatches - clearPatches < clearPatches);
    }

    [TestMethod]
    public async Task MultiradiusCandidateRecoversSupportOutsideLegacyRadiusClip()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var legacy = CreateAdapter(runner);
        var multiradius = CreateMultiradiusAdapter(runner);
        MarkerImageFrame frame = FrameWithOuterRadiusMarker();
        var polygon = MarkerPolygon.FromRectangle(new(0, 0, 512, 512));

        IReadOnlyList<MarkerCenter> legacyMarkers = await legacy.DetectCandidateAsync(frame, polygon, CancellationToken.None);
        IReadOnlyList<MarkerCenter> multiradiusMarkers = await multiradius.DetectCandidateAsync(frame, polygon, CancellationToken.None);

        Assert.IsFalse(legacyMarkers.Any(static marker => Math.Abs(marker.Center.X - 100) <= 2 && Math.Abs(marker.Center.Y - 100) <= 2));
        Assert.IsTrue(multiradiusMarkers.Any(static marker => Math.Abs(marker.Center.X - 100) <= 2 && Math.Abs(marker.Center.Y - 100) <= 2));
    }

    [TestMethod]
    public async Task ArtifactMaskStillRejectsMultiradiusCandidate()
    {
        var adapter = CreateMultiradiusAdapter(new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray()));
        MarkerImageFrame frame = FrameWithOuterRadiusMarker() with
        {
            ArtifactMask = new MarkerMask(512, 512, Enumerable.Repeat(1f, 512 * 512).ToArray()),
        };

        IReadOnlyList<MarkerCenter> markers = await adapter.DetectCandidateAsync(
            frame,
            MarkerPolygon.FromRectangle(new(0, 0, 512, 512)),
            CancellationToken.None);

        Assert.AreEqual(0, markers.Count);
    }

    [TestMethod]
    public async Task CandidateOutputMapsThroughFrameTransformAndProductionFailsClosedWhenUnapproved()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateAdapter(runner);
        MarkerImageFrame frame = FrameWithMarkers() with
        {
            OriginalToFrame = new MarkerAffineTransform(1, 0, 10, 0, 1, 20),
        };
        IReadOnlyList<MarkerCenter> markers = await adapter.DetectCandidateAsync(frame, MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None);
        Assert.IsTrue(markers.Any(static marker => marker.Center.X < 48 && marker.Center.Y < 48));
        int callsBeforeProduction = runner.TotalCalls;
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() =>
            adapter.DetectAsync(CreateRequest(), FrameWithMarkers(), MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), null, null, CancellationToken.None));
        Assert.AreEqual(callsBeforeProduction, runner.TotalCalls);
    }

    [TestMethod]
    public async Task ApprovedV24PathProducesCpuBoundWorkflowEvidence()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateMaskPreservingAdapter(runner, isApproved: true);
        ProductionWorkflowDetectionRequest request = CreateRequest();

        ProductionMarkerCenterEvidence evidence = await adapter.DetectAsync(
            request,
            FrameWithMarkers(),
            MarkerPolygon.FromRectangle(new(0, 0, 512, 512)),
            enhancedImage: null,
            enhancedTransforms: null,
            CancellationToken.None);

        Assert.IsTrue(adapter.IsApproved);
        Assert.AreEqual("markers", evidence.Envelope.Stage);
        Assert.AreEqual("cpu", evidence.Envelope.Model?.Provider);
        Assert.AreEqual(request.Image.Sha256, evidence.Envelope.InputSha256);
        Assert.IsTrue(evidence.Markers.Count > 0);
        Assert.AreEqual(1, evidence.Frames.Count);
        Assert.AreEqual(InferenceProvider.Cpu, evidence.Frames[0].Provider);
        Assert.AreEqual(evidence.Markers.Count, evidence.Frames[0].AcceptedCandidateCount);
        Assert.IsTrue(runner.TotalCalls > 0);
    }

    [TestMethod]
    public async Task ApprovedV24PathRejectsTransformedOrEnhancedFramesBeforeInference()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateMaskPreservingAdapter(runner, isApproved: true);
        MarkerImageFrame transformed = FrameWithMarkers() with
        {
            OriginalToFrame = new MarkerAffineTransform(1, 0, 1, 0, 1, 0),
        };

        ProductionWorkflowStageException exception =
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.DetectAsync(
                CreateRequest(),
                transformed,
                MarkerPolygon.FromRectangle(new(0, 0, 512, 512)),
                enhancedImage: null,
                enhancedTransforms: null,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
        Assert.AreEqual(0, runner.TotalCalls);
    }

    [TestMethod]
    public async Task CancellationStopsBeforeInference()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateAdapter(runner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            adapter.DetectCandidateAsync(FrameWithMarkers(), MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), cancellation.Token));
        Assert.AreEqual(0, runner.TotalCalls);
    }

    [TestMethod]
    public async Task InvalidFrameValuesAndDecodedCandidateLimitFailClosed()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateAdapter(runner, maximumDecodedCandidates: 1);
        float[] invalidPixels = new float[512 * 512];
        invalidPixels[0] = float.NaN;
        MarkerImageFrame invalid = FrameWithMarkers() with { ChannelsFirstPixels = invalidPixels };
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.DetectCandidateAsync(invalid, MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None));
        float[] invalidMask = new float[512 * 512];
        invalidMask[0] = 1.1f;
        invalid = FrameWithMarkers() with { OcrMask = new MarkerMask(512, 512, invalidMask) };
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.DetectCandidateAsync(invalid, MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.DetectCandidateAsync(FrameWithMarkers(), MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidModelOutputFailsClosed()
    {
        var adapter = CreateAdapter(new FakeRunner(static count =>
        {
            float[] output = Enumerable.Repeat(1f, count * 4).ToArray();
            output[0] = float.NaN;
            return output;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.DetectCandidateAsync(FrameWithMarkers(), MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None));
    }

    [TestMethod]
    public void PublicCandidateFactoryRejectsMissingModelBytes()
    {
        var model = new ModelIdentity("marker-center-runtime-consistency-v2", "P2", ProductionProposalMarkerCenterAdapter.ExpectedModelSha256, Path.Combine(Path.GetTempPath(), "missing-marker-p2.onnx"));
        Assert.ThrowsExactly<FileNotFoundException>(() => ProductionProposalMarkerCenterAdapter.CreateCandidate(model, null!));
    }

    [TestMethod]
    public void PublicCandidateFactoryRejectsChangedModelBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"changed-marker-p2-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var model = new ModelIdentity("marker-center-runtime-consistency-v2", "P2", ProductionProposalMarkerCenterAdapter.ExpectedModelSha256, path);
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionProposalMarkerCenterAdapter.CreateCandidate(model, null!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MultiradiusCandidateFactoryRejectsIdentityOrHashMismatch()
    {
        var wrongIdentity = new ModelIdentity(
            ProductionProposalMarkerCenterAdapter.CandidateRevision,
            ProductionProposalMarkerCenterAdapter.CandidateId,
            ProductionProposalMarkerCenterAdapter.ExpectedMultiradiusModelSha256,
            Path.Combine(Path.GetTempPath(), "missing-marker-v23.onnx"));
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionProposalMarkerCenterAdapter.CreateMultiradiusCandidate(wrongIdentity, null!));

        string path = Path.Combine(Path.GetTempPath(), $"changed-marker-v23-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var wrongHash = new ModelIdentity(
                ProductionProposalMarkerCenterAdapter.MultiradiusCandidateRevision,
                ProductionProposalMarkerCenterAdapter.MultiradiusCandidateId,
                ProductionProposalMarkerCenterAdapter.ExpectedMultiradiusModelSha256,
                path);
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionProposalMarkerCenterAdapter.CreateMultiradiusCandidate(wrongHash, null!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task MultiradiusCandidateDoesNotImplyProductionApproval()
    {
        var adapter = CreateMultiradiusAdapter(new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray()));
        Assert.IsFalse(adapter.IsApproved);

        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => adapter.DetectAsync(
            CreateRequest(),
            FrameWithMarkers(),
            MarkerPolygon.FromRectangle(new(0, 0, 512, 512)),
            null,
            null,
            CancellationToken.None));
    }

    [TestMethod]
    public void MaskPreservingCandidateFactoryBindsExactIdentityAndBytes()
    {
        var wrongIdentity = new ModelIdentity(
            ProductionProposalMarkerCenterAdapter.MultiradiusCandidateRevision,
            ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId,
            ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256,
            Path.Combine(Path.GetTempPath(), "missing-marker-v24.onnx"));
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionProposalMarkerCenterAdapter.CreateMaskPreservingCandidate(wrongIdentity, null!));

        string path = Path.Combine(Path.GetTempPath(), $"changed-marker-v24-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var model = new ModelIdentity(
                ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateRevision,
                ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId,
                ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256,
                path);
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionProposalMarkerCenterAdapter.CreateMaskPreservingCandidate(model, null!));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task MaskPreservingCandidateKeepsMaskedInkAndBothMaskChannels()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(1f, count * 4).ToArray());
        var adapter = CreateMaskPreservingAdapter(runner);
        float[] mask = Enumerable.Repeat(1f, 512 * 512).ToArray();
        ProposalMarkerCandidateDiagnosticResult result = await adapter.DetectCandidateWithDiagnosticsAsync(
            FrameWithOuterRadiusMarker() with
            {
                OcrMask = new MarkerMask(512, 512, mask),
                ArtifactMask = new MarkerMask(512, 512, mask.ToArray()),
            },
            MarkerPolygon.FromRectangle(new(0, 0, 512, 512)),
            CancellationToken.None);

        Assert.IsTrue(result.Candidates.Any(static marker => Math.Abs(marker.Center.X - 100) <= 2 && Math.Abs(marker.Center.Y - 100) <= 2));
        Assert.AreEqual(0, result.StageCounters.OcrMaskRejects);
        Assert.AreEqual(0, result.StageCounters.ArtifactMaskRejects);
        Assert.AreEqual(result.StageCounters.EmittedProposals, result.StageCounters.InferenceOutputs);
        int planeSize = ProductionProposalMarkerCenterAdapter.PatchSize * ProductionProposalMarkerCenterAdapter.PatchSize;
        Assert.IsTrue(runner.LastInputValues.Length >= 3 * planeSize);
        Assert.AreEqual(1f, runner.LastInputValues[planeSize]);
        Assert.AreEqual(1f, runner.LastInputValues[2 * planeSize]);
        Assert.IsFalse(adapter.IsApproved);
    }

    [TestMethod]
    [DataRow(4.5, 2.5, 1)]
    [DataRow(5.0, 2.5, 2)]
    [DataRow(5.5, 2.5, 2)]
    [DataRow(9.5, 8.0, 1)]
    [DataRow(10.0, 8.0, 2)]
    public async Task MaskPreservingNmsMatchesPythonDistanceAndRadiusBoundaries(
        double distance, double radius, int expectedCount)
    {
        // Shared with Python's synthetic regression: a 32x32 ink plane,
        // proposals (8,8) and (12,8), and decoded separation `distance`.
        var runner = new FakeRunner(count =>
        {
            Assert.AreEqual(64, count);
            float[] output = new float[count * 4];
            output[18 * 4] = 0.9f;
            output[18 * 4 + 3] = (float)radius;
            output[19 * 4] = 0.8f;
            output[19 * 4 + 1] = (float)((distance - 4) / 4);
            output[19 * 4 + 3] = (float)radius;
            return output;
        });
        var frame = new MarkerImageFrame(32, 32, 1, new float[32 * 32],
            MarkerSourceImage.Original, MarkerAffineTransform.Identity,
            MarkerMask.Empty(32, 32), MarkerMask.Empty(32, 32));
        var result = await CreateMaskPreservingAdapter(runner).DetectCandidateWithDiagnosticsAsync(
            frame, MarkerPolygon.FromRectangle(new(0, 0, 32, 32)), CancellationToken.None);

        Assert.AreEqual(2, result.StageCounters.CandidatesBeforeNms);
        Assert.AreEqual(expectedCount, result.Candidates.Count);
        Assert.AreEqual(2 - expectedCount, result.StageCounters.NmsSuppressions);
    }

    [TestMethod]
    public async Task MaskPreservingProposalLatticeMatchesPythonFullFrameUnfold()
    {
        var runner = new FakeRunner(static count => new float[count * 4]);
        var frame = new MarkerImageFrame(32, 32, 1, new float[32 * 32],
            MarkerSourceImage.Original, MarkerAffineTransform.Identity,
            MarkerMask.Empty(32, 32), MarkerMask.Empty(32, 32));

        ProposalMarkerCandidateDiagnosticResult result =
            await CreateMaskPreservingAdapter(runner).DetectCandidateWithDiagnosticsAsync(
                frame,
                MarkerPolygon.FromRectangle(new(12, 12, 4, 4)),
                CancellationToken.None);

        // ceil(32 / 4)^2, identical to V24 torch.unfold and max-pool output.
        Assert.AreEqual(64, result.StageCounters.ProposalGridPositionsConsidered);
        Assert.AreEqual(64, result.StageCounters.EmittedProposals);
        Assert.AreEqual(64, runner.TotalPatches);
    }

    [TestMethod]
    public async Task MaskPreservingConsensusDoesNotMoveDecodedPoint()
    {
        float[] luminance = Enumerable.Repeat(1f, 32 * 32).ToArray();
        foreach ((int x, int y) in new[]
                 {
                     (6, 8), (12, 8), (9, 5), (9, 11),
                     (6, 5), (12, 5), (6, 11), (12, 11),
                 })
        {
            luminance[(y * 32) + x] = 0;
        }
        var frame = new MarkerImageFrame(32, 32, 1, luminance,
            MarkerSourceImage.Original, MarkerAffineTransform.Identity,
            MarkerMask.Empty(32, 32), MarkerMask.Empty(32, 32));
        MarkerPolygon plot = MarkerPolygon.FromRectangle(new(0, 0, 32, 32));
        ProposalMarkerNegativePatchDiagnosticResult proposalDiagnostic =
            ProductionProposalMarkerCenterAdapter.DiagnoseMaskPreservingProposals(
                frame, plot, CancellationToken.None);
        int targetIndex = proposalDiagnostic.EmittedProposalFeatures
            .Select((feature, index) => (feature, index))
            .Single(item => item.feature.OriginalBaseCenter == new MarkerPoint(8, 8))
            .index;
        var runner = new FakeRunner(count =>
        {
            Assert.AreEqual(proposalDiagnostic.EmittedProposalFeatures.Count, count);
            float[] output = new float[count * 4];
            output[targetIndex * 4] = 0.9f; // Grid point (8,8), one pixel left of consensus.
            output[targetIndex * 4 + 3] = 3f;
            return output;
        });

        ProposalMarkerCandidateDiagnosticResult result =
            await CreateMaskPreservingAdapter(runner).DetectCandidateWithDiagnosticsAsync(
                frame,
                plot,
                CancellationToken.None);

        Assert.AreEqual(0, result.Candidates.Count);
        Assert.AreEqual(1, result.StageCounters.GeometryConsensusRejectsAfterRefinementAttempts);
    }

    [TestMethod]
    public void NegativePatchDiagnosticSummarizesWithoutInferenceOrPixels()
    {
        MarkerImageFrame frame = FrameWithOuterRadiusMarker() with
        {
            OcrMask = new MarkerMask(512, 512, Enumerable.Repeat(0.5f, 512 * 512).ToArray()),
            ArtifactMask = new MarkerMask(512, 512, Enumerable.Repeat(0.25f, 512 * 512).ToArray()),
        };
        ProposalMarkerNegativePatchDiagnosticResult result = ProductionProposalMarkerCenterAdapter.DiagnoseMaskPreservingProposals(
            frame, MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None);

        Assert.AreEqual(result.StageCounters.EmittedProposals, result.EmittedProposalFeatures.Count);
        Assert.IsTrue(result.EmittedProposalFeatures.Count > 0);
        ProposalMarkerPatchFeatureSummary feature = result.EmittedProposalFeatures[0];
        Assert.AreEqual(0.5, feature.OcrMaskMean, 1e-6);
        Assert.AreEqual(0.5, feature.OcrMaskMaximum, 1e-6);
        Assert.AreEqual(0.25, feature.ArtifactMaskMean, 1e-6);
        Assert.AreEqual(0.25, feature.ArtifactMaskMaximum, 1e-6);
        string serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.IsFalse(serialized.Contains("EmittedProposalFeatures", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("Patch", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MorphologyScoresAreBoundedAndDoNotExposePixels()
    {
        var runner = new FakeRunner(static count => Enumerable.Repeat(0.8f, count * 4).ToArray());
        var adapter = CreateMaskPreservingAdapter(runner);
        IReadOnlyList<ProposalMarkerMorphologyScoreSummary> scores = await adapter.DetectMaskPreservingMorphologyScoresAsync(
            FrameWithOuterRadiusMarker(), MarkerPolygon.FromRectangle(new(0, 0, 512, 512)), CancellationToken.None);
        Assert.IsTrue(scores.Count > 0);
        Assert.IsTrue(scores.All(score => score.CovarianceEigenvalueRatio is >= 1 and <= 1e6));
        Assert.IsTrue(scores.All(score => score.MaximumRingSupportCount is >= 0 and <= 8));
        string serialized = System.Text.Json.JsonSerializer.Serialize(scores);
        Assert.IsFalse(serialized.Contains("Patch", StringComparison.Ordinal));
        StringAssert.Contains(serialized, "MaximumRingSupportCount");
        StringAssert.Contains(serialized, "CovarianceEigenvalueRatio");
        Assert.IsTrue(runner.TotalCalls > 0);
    }

    private static ProductionProposalMarkerCenterAdapter CreateAdapter(FakeRunner runner, int? maximumDecodedCandidates = null) =>
        new(new ModelIdentity("marker-center-runtime-consistency-v2", "P2", ProductionProposalMarkerCenterAdapter.ExpectedModelSha256, "candidate-p2.onnx"), runner, maximumDecodedCandidates: maximumDecodedCandidates);

    private static ProductionProposalMarkerCenterAdapter CreateMultiradiusAdapter(FakeRunner runner, int? maximumDecodedCandidates = null) =>
        new(new ModelIdentity(
            ProductionProposalMarkerCenterAdapter.MultiradiusCandidateRevision,
            ProductionProposalMarkerCenterAdapter.MultiradiusCandidateId,
            ProductionProposalMarkerCenterAdapter.ExpectedMultiradiusModelSha256,
            "candidate-v23.onnx"),
            runner,
            maximumDecodedCandidates: maximumDecodedCandidates,
            multiradiusGeometry: true);

    private static ProductionProposalMarkerCenterAdapter CreateMaskPreservingAdapter(
        FakeRunner runner,
        bool isApproved = false) =>
        new(new ModelIdentity(
            ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateRevision,
            ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId,
            ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256,
            "candidate-v24.onnx"),
            runner,
            multiradiusGeometry: true,
            maskPreservingCandidate: true,
            isApproved: isApproved);

    private static MarkerImageFrame FrameWithMarkers()
    {
        float[] ink = new float[512 * 512];
        Array.Fill(ink, 1f);
        for (int y = 24; y < 512; y += 48)
        {
            for (int x = 24; x < 512; x += 48)
            {
                for (int dy = -4; dy <= 4; dy++)
                {
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        if (Math.Abs(dx * dx + dy * dy - 16) <= 4)
                        {
                            ink[(y + dy) * 512 + x + dx] = 0;
                        }
                    }
                }
            }
        }
        return new MarkerImageFrame(512, 512, 1, ink, MarkerSourceImage.Original, MarkerAffineTransform.Identity, MarkerMask.Empty(512, 512), MarkerMask.Empty(512, 512));
    }

    private static MarkerImageFrame FrameWithOuterRadiusMarker()
    {
        float[] ink = new float[512 * 512];
        Array.Fill(ink, 1f);
        const int centerX = 100;
        const int centerY = 100;
        ink[(centerY * 512) + centerX] = 0;
        foreach ((int x, int y) in new[]
                 {
                     (centerX - 10, centerY), (centerX + 10, centerY),
                     (centerX, centerY - 10), (centerX, centerY + 10),
                     (centerX - 10, centerY - 10), (centerX + 10, centerY - 10),
                     (centerX - 10, centerY + 10), (centerX + 10, centerY + 10),
                 })
        {
            ink[(y * 512) + x] = 0;
        }

        return new MarkerImageFrame(
            512,
            512,
            1,
            ink,
            MarkerSourceImage.Original,
            MarkerAffineTransform.Identity,
            MarkerMask.Empty(512, 512),
            MarkerMask.Empty(512, 512));
    }

    private static ProductionWorkflowDetectionRequest CreateRequest()
    {
        byte[] bytes = [1, 2, 3];
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var image = new WorkflowImageEvidence("candidate", sha, 512, 512, WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), "candidate", image);
        return new ProductionWorkflowDetectionRequest(new WorkflowPreparedPanel(panel, image, null), image, WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(), bytes);
    }

    private sealed class FakeRunner(Func<int, float[]> outputFactory) : IProposalMarkerInferenceRunner
    {
        public List<int> BatchSizes { get; } = [];
        public int TotalCalls => BatchSizes.Count;
        public int TotalPatches => BatchSizes.Sum();
        public float[] LastInputValues { get; private set; } = [];

        public ValueTask<InferenceResponse> RunAsync(InferenceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = checked((int)request.Input.Shape[0]);
            LastInputValues = request.Input.Values.ToArray();
            BatchSizes.Add(count);
            return ValueTask.FromResult(new InferenceResponse(
                true,
                new InferenceExecution(outputFactory(count), InferenceProvider.Cpu, new StageTiming(0, 0, 0, 0, 0, false, false), new MemoryDiagnostics(0, 0, 0, 0, 0)),
                null,
                [new ProviderAttempt(InferenceProvider.Cpu, true, null)]));
        }
    }
}
