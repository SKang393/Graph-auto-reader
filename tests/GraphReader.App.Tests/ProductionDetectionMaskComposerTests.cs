// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Inference;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionDetectionMaskComposerTests
{
    private static readonly string[] ExpectedConfiguredOcrTasks =
        ["ocr_detection", "ocr_recognition"];

    [TestMethod]
    public async Task LocalSyntheticSeedMatchesTheSeedUsedByProductionComposition()
    {
        TestInputs inputs = CreateInputs();
        ProductionOcrEvidence candidateOcr = WithScope(inputs.OcrEvidence, approved: false);
        var composer = new ProductionDetectionMaskComposer(new SeedOnlyArtifactMaskAdapter());

        ProductionDetectionMaskSeed seed = ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
            inputs.Request,
            inputs.Raster,
            inputs.AxisEvidence,
            candidateOcr,
            CancellationToken.None);
        ProductionDetectionMaskEvidence composed = await composer.ComposeAsync(
            inputs.Request,
            inputs.Raster,
            inputs.AxisEvidence,
            inputs.OcrEvidence,
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
                WithScope(inputs.OcrEvidence, approved: false),
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
                WithScope(inputs.OcrEvidence, approved: false),
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
    }

    [TestMethod]
    public void LocalSyntheticSeedRejectsChangedOcrEvidenceIdentity()
    {
        TestInputs inputs = CreateInputs();
        ProductionOcrModelEvidence[] changedModelEvidence = inputs.OcrEvidence.ModelEvidence.ToArray();
        changedModelEvidence[0] = new ProductionOcrModelEvidence(
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
                new ProductionOcrEvidence(
                    inputs.OcrEvidence.Result,
                    changedModelEvidence,
                    WithScope(inputs.OcrEvidence, approved: false).ConfiguredModels),
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.DetectionEvidenceRejected, exception.Failure.Code);
    }

    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("wrong-assembly", false)]
    [DataRow("missing-algorithm", false)]
    [DataRow("model-and-algorithm", false)]
    [DataRow("duplicate-assembly", false)]
    [DataRow("unapproved", false)]
    public async Task DeterministicArtifactEvidenceRequiresExactIdentityAndApproval(string mode, bool accepted)
    {
        TestInputs inputs = CreateInputs();
        var adapter = new DeterministicArtifactFixture(inputs, mode);
        var composer = new ProductionDetectionMaskComposer(adapter);
        Task<ProductionDetectionMaskEvidence> Run() => composer.ComposeAsync(
            inputs.Request, inputs.Raster, inputs.AxisEvidence, inputs.OcrEvidence,
            CancellationToken.None);

        if (accepted)
        {
            ProductionDetectionMaskEvidence result = await Run();
            Assert.IsNull(result.ArtifactEnvelope.Model);
            Assert.AreEqual(1f, result.CopyArtifactMask().Values.Span[20 * inputs.Raster.Width + 20]);
            Assert.IsGreaterThan(1, result.ArtifactMaskedPixelCount);
        }
        else
        {
            await Assert.ThrowsAsync<ProductionWorkflowStageException>(Run);
        }
        Assert.AreEqual(mode != "unapproved", adapter.WasInvoked);
    }

    [TestMethod]
    public async Task CandidateCompositionRejectsApprovedAdaptersAndLabelsUnapprovedEvidence()
    {
        TestInputs inputs = CreateInputs(approved: false);
        var approved = new DeterministicArtifactFixture(inputs, "valid");
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() =>
            new ProductionDetectionMaskComposer(approved).ComposeForLocalSyntheticCandidateEvaluationAsync(
                inputs.Request, inputs.Raster, inputs.AxisEvidence, inputs.OcrEvidence,
                CancellationToken.None));
        Assert.IsFalse(approved.WasInvoked);

        var candidate = new DeterministicArtifactFixture(inputs, "unapproved");
        var composer = new ProductionDetectionMaskComposer(candidate);
        ProductionDetectionMaskEvidence result = await composer.ComposeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, inputs.AxisEvidence, inputs.OcrEvidence,
            CancellationToken.None);
        Assert.IsFalse(composer.IsApproved);
        Assert.Contains("artifact_mask_scope:unapproved_candidate_plus_axis_ticks_dividers_ambiguous", result.Warnings);
        Assert.DoesNotContain("artifact_mask_scope:approved_provider_plus_axis_ticks_dividers_ambiguous", result.Warnings);
    }

    [TestMethod]
    public async Task RasterCandidateBindsItsExecutableConfigurationAndOcrDependencyWithoutApproval()
    {
        TestInputs inputs = CreateInputs();
        var adapter = new RasterResidualArtifactMaskAdapter();
        var composer = new ProductionDetectionMaskComposer(adapter);
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => composer.ComposeAsync(
            inputs.Request, inputs.Raster, inputs.AxisEvidence, inputs.OcrEvidence,
            CancellationToken.None));
        ProductionOcrEvidence candidateOcr = WithScope(inputs.OcrEvidence, approved: false);
        ProductionDetectionMaskEvidence result = await composer.ComposeForLocalSyntheticCandidateEvaluationAsync(
            inputs.Request, inputs.Raster, inputs.AxisEvidence, candidateOcr,
            CancellationToken.None);
        Assert.IsFalse(adapter.IsApproved);
        Assert.IsTrue(adapter.Identity.Matches(result.ArtifactEnvelope));
        Assert.IsNull(result.ArtifactEnvelope.Model);
        Assert.AreEqual(adapter.Identity.ConfigurationSha256, Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(adapter.ConfigurationJson))));
        using System.Text.Json.JsonDocument configuration = System.Text.Json.JsonDocument.Parse(adapter.ConfigurationJson);
        Assert.AreEqual(adapter.OcrAssemblySha256,
            configuration.RootElement.GetProperty("dependencies")[0].GetProperty("sha256").GetString());
    }

    [TestMethod]
    public async Task ApprovedEmptyDetectorResultProducesZeroOcrMaskWithoutRecognitionEnvelope()
    {
        TestInputs inputs = CreateInputs();
        ProductionOcrEvidence emptyOcr = CreateEmptyOcrEvidence(inputs);
        var composer = new ProductionDetectionMaskComposer(new SeedOnlyArtifactMaskAdapter());

        ProductionDetectionMaskEvidence result = await composer.ComposeAsync(
            inputs.Request,
            inputs.Raster,
            inputs.AxisEvidence,
            emptyOcr,
            CancellationToken.None);

        Assert.AreEqual(0, result.OcrMaskedPixelCount);
        Assert.IsTrue(result.CopyOcrMask().Values.ToArray().All(static value => value == 0));
        Assert.AreEqual(3, result.SourceEnvelopes.Count);
        Assert.IsTrue(result.SourceEnvelopes.Any(envelope =>
            string.Equals(envelope.Model?.ModelId, "ocr-detection", StringComparison.Ordinal)));
        Assert.IsFalse(result.SourceEnvelopes.Any(envelope =>
            string.Equals(envelope.Model?.ModelId, "ocr-recognition", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            ExpectedConfiguredOcrTasks,
            emptyOcr.ConfiguredModels.Models.Select(static model => model.Task).ToArray());
        Assert.AreEqual("approved_production", emptyOcr.ConfiguredModels.Scope);
    }

    [TestMethod]
    public async Task LocalCandidateEmptyDetectorResultKeepsBothConfiguredModelsWithoutRecognitionExecution()
    {
        TestInputs inputs = CreateInputs(approved: false);
        ProductionOcrEvidence emptyOcr = CreateEmptyOcrEvidence(inputs);
        var composer = new ProductionDetectionMaskComposer(new RasterResidualArtifactMaskAdapter());

        ProductionDetectionMaskSeed seed =
            ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
                inputs.Request,
                inputs.Raster,
                inputs.AxisEvidence,
                emptyOcr,
                CancellationToken.None);
        ProductionDetectionMaskEvidence result =
            await composer.ComposeForLocalSyntheticCandidateEvaluationAsync(
                inputs.Request,
                inputs.Raster,
                inputs.AxisEvidence,
                emptyOcr,
                CancellationToken.None);

        Assert.IsTrue(seed.CopyOcrMask().Values.ToArray().All(static value => value == 0));
        Assert.AreEqual(0, result.OcrMaskedPixelCount);
        Assert.AreEqual("unapproved_local_synthetic_candidate", emptyOcr.ConfiguredModels.Scope);
        Assert.HasCount(2, emptyOcr.ConfiguredModels.Models);
        Assert.HasCount(1, emptyOcr.ModelEvidence);
        Assert.AreEqual("ocr_detection", emptyOcr.ModelEvidence[0].Task);
    }

    [TestMethod]
    [DataRow("recognition-envelope")]
    [DataRow("missing-warning")]
    [DataRow("nonempty-region")]
    [DataRow("nonzero-batch")]
    [DataRow("region-failure")]
    [DataRow("unapproved-configuration")]
    [DataRow("configured-model-mismatch")]
    public async Task EmptyDetectorResultRejectsInconsistentOrUnapprovedProvenance(string mode)
    {
        TestInputs inputs = CreateInputs();
        ProductionOcrEvidence empty = CreateEmptyOcrEvidence(inputs);
        OcrResult result = empty.Result;
        IReadOnlyList<ProductionOcrModelEvidence> executions = empty.ModelEvidence;
        ProductionOcrConfigurationEvidence configuration = empty.ConfiguredModels;
        switch (mode)
        {
            case "recognition-envelope":
                executions = inputs.OcrEvidence.ModelEvidence;
                break;
            case "missing-warning":
                result = result with { Warnings = [] };
                break;
            case "nonempty-region":
                result = result with { Regions = inputs.OcrEvidence.Result.Regions };
                break;
            case "nonzero-batch":
                result = result with { Cache = result.Cache with { BatchCount = 1 } };
                break;
            case "region-failure":
                result = result with
                {
                    RegionFailures =
                    [
                        new OcrRegionFailure(
                            "region-1",
                            OcrSourceImage.Original,
                            new OcrFailure(
                                "OCR_TEST_FAILURE",
                                "error",
                                "Errors.DetectionEvidenceRejected",
                                "test failure",
                                true,
                                "retry")),
                    ],
                };
                break;
            case "unapproved-configuration":
                configuration = WithScope(empty, approved: false).ConfiguredModels;
                break;
            case "configured-model-mismatch":
                configuration = new ProductionOcrConfigurationEvidence(
                [
                    new ProductionOcrConfiguredModel(
                        "ocr_detection",
                        new ModelIdentity("other-detector", "ocr-v1", new string('d', 64), "other.onnx"),
                        InferenceProvider.Cpu),
                    configuration.Models.Single(static model => model.Task == "ocr_recognition"),
                ],
                ProductionOcrConfigurationScope.ApprovedProduction);
                break;
            default:
                Assert.Fail($"Unknown test mode '{mode}'.");
                break;
        }

        var composer = new ProductionDetectionMaskComposer(new SeedOnlyArtifactMaskAdapter());
        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => composer.ComposeAsync(
            inputs.Request,
            inputs.Raster,
            inputs.AxisEvidence,
            new ProductionOcrEvidence(result, executions, configuration),
            CancellationToken.None));
    }

    [TestMethod]
    public async Task RecognizedCropsStillRequireRecognitionExecutionEnvelope()
    {
        TestInputs inputs = CreateInputs();
        var detectorOnly = new ProductionOcrEvidence(
            inputs.OcrEvidence.Result,
            [inputs.OcrEvidence.ModelEvidence.Single(static item => item.Task == "ocr_detection")],
            inputs.OcrEvidence.ConfiguredModels);
        var composer = new ProductionDetectionMaskComposer(new SeedOnlyArtifactMaskAdapter());

        await Assert.ThrowsAsync<ProductionWorkflowStageException>(() => composer.ComposeAsync(
            inputs.Request,
            inputs.Raster,
            inputs.AxisEvidence,
            detectorOnly,
            CancellationToken.None));
    }

    private static TestInputs CreateInputs(bool approved = true)
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
        var configuredModels = new ProductionOcrConfigurationEvidence(
        [
            new ProductionOcrConfiguredModel(
                "ocr_detection",
                new ModelIdentity("ocr-detection", "ocr-v1", new string('b', 64), "detection.onnx"),
                InferenceProvider.Cpu),
            new ProductionOcrConfiguredModel(
                "ocr_recognition",
                new ModelIdentity("ocr-recognition", "ocr-v1", new string('c', 64), "recognition.onnx"),
                InferenceProvider.Cpu),
        ],
        approved
            ? ProductionOcrConfigurationScope.ApprovedProduction
            : ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate);
        return new TestInputs(
            request,
            raster,
            axisEvidence,
            new ProductionOcrEvidence(ocrResult, ocrEvidence, configuredModels));
    }

    private static ProductionOcrEvidence WithScope(ProductionOcrEvidence evidence, bool approved) =>
        new(
            evidence.Result,
            evidence.ModelEvidence,
            new ProductionOcrConfigurationEvidence(
                evidence.ConfiguredModels.Models.Select(model => new ProductionOcrConfiguredModel(
                    model.Task,
                    model.Identity,
                    model.Provider)),
                approved
                    ? ProductionOcrConfigurationScope.ApprovedProduction
                    : ProductionOcrConfigurationScope.UnapprovedLocalSyntheticCandidate));

    private static ProductionOcrEvidence CreateEmptyOcrEvidence(TestInputs inputs)
    {
        OcrResult result = inputs.OcrEvidence.Result with
        {
            Regions = [],
            Masks = [],
            Confidence = 0,
            Warnings = ["no_text_regions_detected"],
            Cache = inputs.OcrEvidence.Result.Cache with { CropCount = 0, BatchCount = 0 },
            RegionFailures = [],
        };
        return new ProductionOcrEvidence(
            result,
            [inputs.OcrEvidence.ModelEvidence.Single(static item => item.Task == "ocr_detection")],
            inputs.OcrEvidence.ConfiguredModels);
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

    private sealed class DeterministicArtifactFixture(TestInputs inputs, string mode) : IProductionArtifactMaskAdapter
    {
        public string AdapterId => "test-deterministic-artifact-mask";
        public bool IsApproved => mode != "unapproved";
        public bool WasInvoked { get; private set; }

        public Task<ProductionArtifactMaskEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request, ProductionDecodedRaster raster,
            ProductionDetectionMaskSeed seed, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture requires the actual axis and OCR context.");

        public Task<ProductionArtifactMaskEvidence> DetectAsync(
            ProductionWorkflowDetectionRequest request, ProductionDecodedRaster raster,
            ProductionDetectionMaskSeed seed, ProductionAxisGeometryEvidence axisEvidence,
            IReadOnlyList<ProductionOcrModelEvidence> ocrModelEvidence, OcrResult ocrResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreSame(inputs.AxisEvidence, axisEvidence);
            Assert.AreSame(inputs.OcrEvidence.ModelEvidence, ocrModelEvidence);
            Assert.AreSame(inputs.OcrEvidence.Result, ocrResult);
            WasInvoked = true;
            var algorithm = new ProductionArtifactAlgorithmEvidence(
                "fixture-residual-artifacts", "1", new string('d', 64), new string('e', 64));
            var warnings = new List<string>
            {
                mode == "wrong-assembly"
                    ? $"artifact_algorithm_assembly_sha256:{new string('f', 64)}"
                    : algorithm.AssemblyWarning,
                algorithm.ConfigurationWarning,
            };
            if (mode == "duplicate-assembly")
            {
                warnings.Add(algorithm.AssemblyWarning);
            }
            var envelope = new WorkflowVisionEnvelope(
                1, request.RunId, request.ProjectId, request.Panel.ImportedPanel.PanelId,
                "markers", algorithm.StageVersion, raster.InputSha256,
                mode == "model-and-algorithm"
                    ? new WorkflowVisionModel("unexpected-model", "1", new string('a', 64), "cpu")
                    : null,
                new WorkflowVisionTiming(1, null, 1, 2), 0.98, warnings,
                request.Transforms);
            var mask = new float[raster.Width * raster.Height];
            mask[20 * raster.Width + 20] = 1f;
            return Task.FromResult(new ProductionArtifactMaskEvidence(
                raster.Width, raster.Height, raster.InputSha256, raster.Variant,
                envelope, mask, algorithm: mode == "missing-algorithm" ? null : algorithm));
        }
    }

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
        ProductionOcrEvidence OcrEvidence);
}
