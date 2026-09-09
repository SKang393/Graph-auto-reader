// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionDetectionMaskComposerTests
{
    [TestMethod]
    public async Task LocalSyntheticSeedMatchesTheSeedUsedByProductionComposition()
    {
        TestInputs inputs = CreateInputs();
        var composer = new ProductionDetectionMaskComposer(new SeedOnlyArtifactMaskAdapter());

        ProductionDetectionMaskSeed seed = ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
            inputs.Request,
            inputs.Raster,
            inputs.AxisEvidence,
            inputs.OcrEvidence,
            inputs.OcrResult,
            CancellationToken.None);
        ProductionDetectionMaskEvidence composed = await composer.ComposeAsync(
            inputs.Request,
            inputs.Raster,
            inputs.AxisEvidence,
            inputs.OcrEvidence,
            inputs.OcrResult,
            CancellationToken.None);

        CollectionAssert.AreEqual(
            seed.CopyOcrMask().Values.ToArray(),
            composed.CopyOcrMask().Values.ToArray());
        CollectionAssert.AreEqual(
            seed.CopyArtifactMask().Values.ToArray(),
            composed.CopyArtifactMask().Values.ToArray());
        Assert.IsGreaterThan(0, composed.OcrMaskedPixelCount);
        Assert.IsGreaterThan(0, composed.ArtifactMaskedPixelCount);
    }

    [TestMethod]
    public void LocalSyntheticSeedRejectsChangedRasterIdentity()
    {
        TestInputs inputs = CreateInputs();
        var changedRaster = new ProductionDecodedRaster(
            inputs.Raster.Width,
            inputs.Raster.Height,
            new string('f', 64),
            inputs.Raster.Variant,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            inputs.Raster.Width,
            inputs.Raster.Height,
            new byte[inputs.Raster.Width * inputs.Raster.Height],
            new float[inputs.Raster.Width * inputs.Raster.Height]);
        ProductionWorkflowStageException exception = Assert.ThrowsExactly<ProductionWorkflowStageException>(
            () => ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
                inputs.Request,
                changedRaster,
                inputs.AxisEvidence,
                inputs.OcrEvidence,
                inputs.OcrResult,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
    }

    [TestMethod]
    public void LocalSyntheticSeedRejectsChangedAxisEvidenceIdentity()
    {
        TestInputs inputs = CreateInputs();
        var changedAxisEvidence = new ProductionAxisGeometryEvidence(
            Envelope(inputs.Request, "axis", "axis-v1", "axis", 'a', new string('e', 64)),
            inputs.AxisEvidence.Geometry);
        ProductionWorkflowStageException exception = Assert.ThrowsExactly<ProductionWorkflowStageException>(
            () => ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
                inputs.Request,
                inputs.Raster,
                changedAxisEvidence,
                inputs.OcrEvidence,
                inputs.OcrResult,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
    }

    [TestMethod]
    public void LocalSyntheticSeedRejectsChangedOcrEvidenceIdentity()
    {
        TestInputs inputs = CreateInputs();
        ProductionOcrModelEvidence[] changedOcrEvidence = inputs.OcrEvidence.ToArray();
        changedOcrEvidence[0] = new ProductionOcrModelEvidence(
            "ocr_detection",
            Envelope(
                inputs.Request,
                "ocr",
                "ocr-v1",
                "ocr-detection",
                'b',
                new string('e', 64)));
        ProductionWorkflowStageException exception = Assert.ThrowsExactly<ProductionWorkflowStageException>(
            () => ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
                inputs.Request,
                inputs.Raster,
                inputs.AxisEvidence,
                changedOcrEvidence,
                inputs.OcrResult,
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
    }

    private static TestInputs CreateInputs()
    {
        const int width = 32;
        const int height = 32;
        byte[] bytes = [1, 2, 3, 4];
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var image = new WorkflowImageEvidence(
            "memory:synthetic-family.png",
            sha256,
            width,
            height,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(
            Guid.Parse("10000000-0000-0000-0000-000000000019"),
            Guid.Parse("20000000-0000-0000-0000-000000000019"),
            "synthetic-family.png",
            image);
        var request = new ProductionWorkflowDetectionRequest(
            new WorkflowPreparedPanel(imported, image, enhanced: null),
            image,
            WorkflowImageVariant.Original,
            Guid.Parse("30000000-0000-0000-0000-000000000019"),
            Guid.Parse("40000000-0000-0000-0000-000000000019"),
            bytes);
        var raster = new ProductionDecodedRaster(
            width,
            height,
            sha256,
            WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            width,
            height,
            new byte[width * height],
            new float[width * height]);

        var xAxis = new AxisLineFit(
            new GeometryLineSegment(new PixelPoint(4, 27), new PixelPoint(27, 27)),
            0.98,
            0,
            1,
            ["x"]);
        var yAxis = new AxisLineFit(
            new GeometryLineSegment(new PixelPoint(4, 27), new PixelPoint(4, 4)),
            0.98,
            0,
            1,
            ["y"]);
        var geometry = new AxisGeometryResult(
            "original_pixels",
            new PlotPolygon(
                new PixelPoint(4, 27),
                new PixelPoint(27, 27),
                new PixelPoint(27, 4),
                new PixelPoint(4, 4)),
            xAxis,
            yAxis,
            [],
            [],
            [],
            0.98,
            new AxisGeometryUncertainty(0, 0, 1, false, []),
            new AxisGeometryDiagnostics(2, 2, 0, 1, 2, 0, 0, 0, TimeSpan.Zero, []));
        var axisEvidence = new ProductionAxisGeometryEvidence(
            Envelope(request, "axis", "axis-v1", "axis", 'a'),
            geometry);

        OcrRegion region = new(
            "text-1",
            OcrPolygon.FromRectangle(new OcrRectangle(12, 8, 5, 4)),
            "A",
            [new OcrRecognitionAlternative("A", 0.98, OcrSourceImage.Original)],
            OcrTextRole.Annotation,
            0.98,
            OcrSourceImage.Original,
            OcrReviewStatus.Unreviewed);
        var ocrResult = new OcrResult(
            OcrContract.Version,
            request.RunId.ToString("D"),
            request.ProjectId.ToString("D"),
            request.Panel.ImportedPanel.PanelId.ToString("D"),
            OcrContract.Stage,
            "ocr-v1",
            request.Image.Sha256,
            OcrContract.CoordinateSpace,
            [region],
            [new OcrMask(region.RegionId, region.Polygon, region.Confidence)],
            new OcrTiming(1, 1, 1, 3),
            0.98,
            [],
            new OcrCacheDiagnostics(false, "test", 1, 1),
            null,
            []);
        ProductionOcrModelEvidence[] ocrEvidence =
        [
            new("ocr_detection", Envelope(request, "ocr", "ocr-v1", "ocr-detection", 'b')),
            new("ocr_recognition", Envelope(request, "ocr", "ocr-v1", "ocr-recognition", 'c')),
        ];
        return new TestInputs(request, raster, axisEvidence, ocrEvidence, ocrResult);
    }

    private static WorkflowVisionEnvelope Envelope(
        ProductionWorkflowDetectionRequest request,
        string stage,
        string version,
        string modelId,
        char checksum,
        string? inputSha256 = null) => new(
            1,
            request.RunId,
            request.ProjectId,
            request.Panel.ImportedPanel.PanelId,
            stage,
            version,
            inputSha256 ?? request.Image.Sha256,
            new WorkflowVisionModel(modelId, version, new string(checksum, 64), "cpu"),
            new WorkflowVisionTiming(1, 1, 1, 3),
            0.98,
            transforms: request.Transforms);

    private sealed class SeedOnlyArtifactMaskAdapter : IProductionArtifactMaskAdapter
    {
        public string AdapterId => "test-seed-only-artifact-mask";

        public bool IsApproved => true;

        public Task<ProductionArtifactMaskEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request,
            ProductionDecodedRaster raster,
            ProductionDetectionMaskSeed seed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProductionArtifactMaskEvidence(
                raster.Width,
                raster.Height,
                raster.InputSha256,
                raster.Variant,
                Envelope(request, "markers", "artifact-v1", "artifact", 'd'),
                seed.CopyArtifactMask().Values.ToArray()));
        }
    }

    private sealed record TestInputs(
        ProductionWorkflowDetectionRequest Request,
        ProductionDecodedRaster Raster,
        ProductionAxisGeometryEvidence AxisEvidence,
        IReadOnlyList<ProductionOcrModelEvidence> OcrEvidence,
        OcrResult OcrResult);
}
