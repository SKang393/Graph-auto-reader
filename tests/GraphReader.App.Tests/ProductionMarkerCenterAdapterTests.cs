// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerCenterAdapterTests
{
    [TestMethod]
    [DataRow(-0.25, 8.0, false)]
    [DataRow(8.0, -0.25, false)]
    [DataRow(16.0, 8.0, false)]
    [DataRow(8.0, 16.0, false)]
    [DataRow(0.0, 0.0, true)]
    [DataRow(15.999, 15.999, true)]
    public async Task MarkerCenterRasterBoundsAreEnforced(double x, double y, bool accepted)
    {
        const int width = 16;
        const int height = 16;
        byte[] sourceBytes = [1, 2, 3];
        string hash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        var image = new WorkflowImageEvidence("memory:marker.png", hash, width, height, WorkflowImageVariant.Original);
        Guid projectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid panelId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var panel = new WorkflowImportedPanel(panelId, Guid.NewGuid(), "marker.png", image);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(panel, image, enhanced: null),
            image,
            WorkflowImageVariant.Original,
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            projectId,
            sourceBytes);
        var model = new ModelIdentity("marker-test", "1", new string('a', 64), "marker.onnx");
        var frame = new MarkerImageFrame(
            width,
            height,
            1,
            Enumerable.Repeat(1f, width * height).ToArray(),
            MarkerSourceImage.Original,
            MarkerAffineTransform.Identity,
            MarkerMask.Empty(width, height),
            MarkerMask.Empty(width, height));
        var marker = new MarkerCenter(
            "outside-raster",
            new MarkerPoint(x, y),
            2,
            0,
            0.9,
            MarkerSourceImage.Original);
        var result = new MarkerDetectionResult(
            MarkerContract.Version,
            Guid.NewGuid().ToString("D"),
            projectId.ToString("D"),
            panelId.ToString("D"),
            MarkerContract.Stage,
            "test-v1",
            hash,
            MarkerContract.CoordinateSpace,
            [marker],
            new MarkerDetectionTiming(0, 1, 0, 1),
            0.9,
            [],
            [],
            new MarkerModelReport(model.ModelId, model.Version, model.Sha256, InferenceProvider.Cpu),
            null);
        var adapter = new ProductionMarkerCenterAdapter(
            model,
            CreateOptions(),
            isApproved: true,
            new FixedMarkerDetectionService(result));

        Task<ProductionMarkerCenterEvidence> detect() => adapter.DetectAsync(
                request,
                frame,
                MarkerPolygon.FromRectangle(new(-1, -1, width + 2, height + 2)),
                enhancedImage: null,
                enhancedTransforms: null,
                CancellationToken.None);

        if (accepted)
        {
            ProductionMarkerCenterEvidence evidence = await detect();
            Assert.AreEqual(new MarkerPoint(x, y), evidence.Markers.Single().Center);
        }
        else
        {
            ProductionWorkflowStageException failure =
                await Assert.ThrowsAsync<ProductionWorkflowStageException>(detect);
            Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, failure.Failure.Code);
        }
    }

    private static MarkerDetectionOptions CreateOptions() => new(new MarkerModelTensorContract(
        "input",
        "output",
        32,
        32,
        3,
        MarkerTensorLayout.ChannelsFirst,
        32,
        32,
        3,
        MarkerTensorLayout.ChannelsFirst,
        0,
        1,
        2,
        MarkerHeadActivation.Identity,
        MarkerHeadActivation.Identity,
        1,
        0,
        1));

    private sealed class FixedMarkerDetectionService(MarkerDetectionResult result) : IMarkerDetectionService
    {
        public ValueTask<MarkerDetectionResult> DetectAsync(
            MarkerDetectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
