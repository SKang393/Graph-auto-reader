// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.MarkerContextEvidence;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ContextEvidence = GraphReader.MarkerContextEvidence.MarkerContextEvidence;

namespace GraphReader.MarkerContextProductionRequest.Tests;

[TestClass]
public sealed class ProductionClassifierRequestParityTests
{
    private static readonly int[] Shape32 = [1, 1, 32, 32];
    private static readonly int[] Shape65 = [1, 1, 65, 65];

    [TestMethod]
    public async Task SuppliedManifestOptionsProductionAdapterAndServiceBuild32By32RequestWithoutModelExecution()
    {
        SyntheticInput fixture = CreateInput(withMeasuredBounds: true);
        var captureRunner = new StopBeforeModelRunner();
        var service = new MarkerClassificationService(captureRunner);
        (ModelIdentity model, MarkerClassificationOptions options) = ReadCurrentManifestBinding(fixture.ContentBounds);
        // Test-only approval flag is required by the public adapter seam. The synthetic path and
        // capture-only runner do not load or approve a production model.
        var adapter = new ProductionMarkerClassificationAdapter(
            model,
            options,
            isApproved: true,
            service);

        ProductionWorkflowStageException failure = await Assert.ThrowsAsync<ProductionWorkflowStageException>(
            () => adapter.ClassifyAsync(
                CreateWorkflowRequest(fixture.EncodedSource),
                fixture.Frame,
                [fixture.Marker],
                fixture.ContentBounds!,
                CancellationToken.None));

        Assert.IsTrue(failure.Message.Contains("capture-only", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(captureRunner.Captured);
        AssertCapturedRequest(captureRunner.Captured!, Shape32, fixture, 32, 32, 2.25, fixture.ContentBounds,
            inputName: "marker_patch", outputName: "classification_probabilities");
    }

    [TestMethod]
    public void CurrentClassifierManifestBindsTheSuppliedAdapterDimensionsAndNormalization()
    {
        (ModelIdentity model, MarkerClassificationOptions options) = ReadCurrentManifestBinding(contentBounds: null);

        Assert.AreEqual("graph-marker-classifier", model.ModelId);
        Assert.AreEqual("0.1.0", model.Version);
        Assert.AreEqual("26f9304f1689053a0b94aa896a1e239f6ade1e5c1920736a3535c1b32f803b8a", model.Sha256);
        Assert.AreEqual(32, options.TensorContract.PatchWidth);
        Assert.AreEqual(32, options.TensorContract.PatchHeight);
        Assert.AreEqual(1, options.TensorContract.InputChannelCount);
        Assert.AreEqual("marker_patch", options.TensorContract.InputName);
        Assert.AreEqual("classification_probabilities", options.TensorContract.OutputName);
        Assert.AreEqual(0, options.TensorContract.NormalizeMean);
        Assert.AreEqual(1, options.TensorContract.NormalizeScale);
        Assert.AreEqual(64, options.BatchSize);
        Assert.AreEqual(2.25, options.PatchRadiusScale);
        Assert.AreEqual(4, options.MinimumPatchHalfExtentFramePixels);
        Assert.AreEqual(0, options.PatchPaddingValue);
    }

    [TestMethod]
    public async Task LowerLevelExperimental65By65ServiceRequestMatchesPythonWithoutProductionAdapter()
    {
        SyntheticInput fixture = CreateInput(withMeasuredBounds: true);
        var captureRunner = new StopBeforeModelRunner();
        var service = new MarkerClassificationService(captureRunner);
        var request = new MarkerClassificationRequest(
            "synthetic-project",
            "synthetic-panel",
            fixture.ImageSha256,
            SyntheticModelIdentity(),
            fixture.Frame,
            [fixture.Marker],
            CreateOptions(65, 65, radiusScale: 4.5, fixture.ContentBounds));

        MarkerClassificationResult result = await service.ClassifyAsync(request, CancellationToken.None);

        Assert.IsFalse(result.Succeeded, "The runner intentionally stops before any model or provider execution.");
        Assert.IsNotNull(captureRunner.Captured);
        AssertCapturedRequest(captureRunner.Captured!, Shape65, fixture, 65, 65, 4.5, fixture.ContentBounds,
            inputName: "marker_patch", outputName: "marker_classes");
    }

    [TestMethod]
    public async Task ExperimentalSecond32By32ContextViewUsesRadius675AndNoSymbolWhitening()
    {
        SyntheticInput fixture = CreateInput(withMeasuredBounds: true);
        var narrowRunner = new StopBeforeModelRunner();
        var contextRunner = new StopBeforeModelRunner();
        MarkerClassificationOptions narrowOptions = CreateOptions(32, 32, 2.25, contentBounds: null);
        MarkerClassificationOptions contextOptions = CreateOptions(32, 32, 6.75, contentBounds: null) with
        {
            MinimumPatchHalfExtentFramePixels = 12,
        };

        await new MarkerClassificationService(narrowRunner).ClassifyAsync(
            CreateClassificationRequest(fixture, narrowOptions), CancellationToken.None);
        await new MarkerClassificationService(contextRunner).ClassifyAsync(
            CreateClassificationRequest(fixture, contextOptions), CancellationToken.None);

        Assert.IsNotNull(narrowRunner.Captured);
        Assert.IsNotNull(contextRunner.Captured);
        AssertCapturedRequest(narrowRunner.Captured!, Shape32, fixture, 32, 32, 2.25, null,
            inputName: "marker_patch", outputName: "marker_classes");
        AssertCapturedRequest(contextRunner.Captured!, Shape32, fixture, 32, 32, 6.75, null,
            minimumHalfExtent: 12, inputName: "marker_patch", outputName: "marker_classes");
        Assert.IsFalse(narrowRunner.Captured!.Input.Values.Span.SequenceEqual(
            contextRunner.Captured!.Input.Values.Span),
            "The larger-context view should differ while retaining the same 32x32 model shape.");
        Assert.IsFalse(contextRunner.Captured.CacheMaterial.Parameters.ContainsKey("content_isolation_version"));
        Assert.IsFalse(contextRunner.Captured.CacheMaterial.Parameters.ContainsKey("original_pixel_content_bounds"));
    }

    [TestMethod]
    public async Task ClassifierRequestDoesNotConsumeOcrOrArtifactMaskPlanes()
    {
        SyntheticInput first = CreateInput(withMeasuredBounds: false);
        SyntheticInput second = first with
        {
            Frame = first.Frame with
            {
                OcrMask = new MarkerMask(first.Frame.Width, first.Frame.Height,
                    Enumerable.Repeat(1f, first.Frame.Width * first.Frame.Height).ToArray()),
                ArtifactMask = new MarkerMask(first.Frame.Width, first.Frame.Height,
                    Enumerable.Repeat(0.7f, first.Frame.Width * first.Frame.Height).ToArray()),
            },
        };
        var firstRunner = new StopBeforeModelRunner();
        var secondRunner = new StopBeforeModelRunner();
        MarkerClassificationOptions options = CreateOptions(32, 32, 2.25, contentBounds: null);
        var firstService = new MarkerClassificationService(firstRunner);
        var secondService = new MarkerClassificationService(secondRunner);

        await firstService.ClassifyAsync(CreateClassificationRequest(first, options), CancellationToken.None);
        await secondService.ClassifyAsync(CreateClassificationRequest(second, options), CancellationToken.None);

        Assert.IsNotNull(firstRunner.Captured);
        Assert.IsNotNull(secondRunner.Captured);
        CollectionAssert.AreEqual(firstRunner.Captured!.Input.Values.ToArray(), secondRunner.Captured!.Input.Values.ToArray());
        Assert.AreNotEqual(first.Frame.OcrMask.Values.Span[0], second.Frame.OcrMask.Values.Span[0]);
        Assert.AreNotEqual(first.Frame.ArtifactMask.Values.Span[0], second.Frame.ArtifactMask.Values.Span[0]);
    }

    private static void AssertCapturedRequest(
        InferenceRequest request,
        int[] expectedShape,
        SyntheticInput fixture,
        int patchWidth,
        int patchHeight,
        double radiusScale,
        IReadOnlyDictionary<string, MarkerRectangle>? contentBounds,
        double minimumHalfExtent = 4,
        string inputName = "marker_patch",
        string outputName = "marker_classes")
    {
        CollectionAssert.AreEqual(expectedShape, request.Input.Shape.Select(static value => checked((int)value)).ToArray());
        Assert.AreEqual(inputName, request.Input.InputName);
        Assert.AreEqual(outputName, request.Input.OutputName);
        Assert.AreEqual(checked(patchWidth * patchHeight), request.Input.Values.Length);

        ContextEvidence expected = MarkerContextEvidenceCapture.Capture(
            $"production-request-{patchWidth}",
            fixture.Frame,
            fixture.Marker,
            new MarkerPatchExtractionOptions(patchWidth, patchHeight, 1)
            {
                RadiusScale = radiusScale,
                MinimumHalfExtentFramePixels = minimumHalfExtent,
                PaddingValue = 0,
                OriginalPixelContentBounds = contentBounds,
            });
        ContextEvidence actual = expected with { ProductionTensor = request.Input.Values.ToArray() };
        CollectionAssert.AreEqual(expected.OutputShapeNchw, expectedShape);
        AssertPythonParity(actual, expectedShape);
    }

    private static void AssertPythonParity(ContextEvidence evidence, int[] requestShape)
    {
        string projectRoot = FindProjectRoot(Environment.CurrentDirectory);
        string script = Path.Combine(projectRoot, "tools", "GraphReader.MarkerContextEvidence", "python", "marker_context_reference.py");
        Assert.IsTrue(File.Exists(script), "Python reference is required for cross-language request parity.");
        string fixtureId = Guid.NewGuid().ToString("N");
        string parent = Path.Combine(projectRoot, "artifacts", "goal22-runs", "marker-context-production-request-tests");
        string root = Path.Combine(parent, fixtureId);
        Assert.IsTrue(Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(root);
        string inputPath = Path.Combine(root, evidence.Input.CaseId + ".json");
        try
        {
            byte[] bytes = MarkerContextEvidenceCapture.Serialize(evidence);
            File.WriteAllBytes(inputPath, bytes);
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add("--compare");
            process.StartInfo.ArgumentList.Add(inputPath);
            if (!process.Start())
            {
                Assert.Inconclusive("Python executable could not be started for captured request parity.");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode,
                $"Python reference rejected captured request evidence SHA-256 {digest}: {stderr}");
            using JsonDocument result = JsonDocument.Parse(stdout);
            JsonElement rootElement = result.RootElement;
            Assert.IsTrue(rootElement.GetProperty("parity").GetBoolean());
            CollectionAssert.AreEqual(requestShape,
                rootElement.GetProperty("output_shape_nchw").EnumerateArray().Select(static value => value.GetInt32()).ToArray());
            Assert.AreEqual(0, rootElement.GetProperty("mismatched_values").GetInt32());
            Assert.IsFalse(rootElement.GetProperty("annotations_read").GetBoolean());
            Assert.IsFalse(rootElement.GetProperty("private_or_sealed_data_read").GetBoolean());
            Assert.AreEqual(0, rootElement.GetProperty("optimizer_steps").GetInt32());
        }
        catch (Win32Exception exception)
        {
            Assert.Inconclusive($"Python executable is unavailable for parity: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SyntheticInput CreateInput(bool withMeasuredBounds)
    {
        const int width = 43;
        const int height = 37;
        float[] pixels = new float[width * height];
        float[] ocrMask = new float[width * height];
        float[] artifactMask = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                pixels[index] = ((x + (3 * y)) % 23) / 22f;
                ocrMask[index] = (x % 5) / 4f;
                artifactMask[index] = (y % 7) / 6f;
            }
        }

        var marker = new MarkerCenter("synthetic-marker", new MarkerPoint(21.2, 17.4), 2.5, 0, 1,
            MarkerSourceImage.Original);
        IReadOnlyDictionary<string, MarkerRectangle>? bounds = withMeasuredBounds
            ? new Dictionary<string, MarkerRectangle>(StringComparer.Ordinal)
            {
                [marker.MarkerId] = new MarkerRectangle(14, 10, 15, 15),
            }
            : null;
        var frame = new MarkerImageFrame(width, height, 1, pixels, MarkerSourceImage.Original,
            MarkerAffineTransform.Identity,
            new MarkerMask(width, height, ocrMask),
            new MarkerMask(width, height, artifactMask));
        byte[] encoded = Encoding.UTF8.GetBytes("synthetic-only marker-context request fixture v1");
        return new SyntheticInput(frame, marker, bounds, encoded,
            Convert.ToHexStringLower(SHA256.HashData(encoded)));
    }

    private static MarkerClassificationOptions CreateOptions(
        int width,
        int height,
        double radiusScale,
        IReadOnlyDictionary<string, MarkerRectangle>? contentBounds,
        string inputName = "marker_patch",
        string outputName = "marker_classes") =>
        new(new MarkerClassifierTensorContract(inputName, outputName, width, height, 1, 12)
        {
            NormalizeMean = 0,
            NormalizeScale = 1,
            OutputEncoding = MarkerClassifierOutputEncoding.Probabilities,
        })
        {
            BatchSize = 1,
            PatchRadiusScale = radiusScale,
            MinimumPatchHalfExtentFramePixels = 4,
            PatchPaddingValue = 0,
            OriginalPixelContentBounds = contentBounds,
            StageVersion = "synthetic-capture-only",
        };

    private static (ModelIdentity Model, MarkerClassificationOptions Options) ReadCurrentManifestBinding(
        IReadOnlyDictionary<string, MarkerRectangle>? contentBounds)
    {
        string root = FindProjectRoot(Environment.CurrentDirectory);
        string path = Path.Combine(root, "models", "manifest", "markers", "graph-marker-classifier-0.1.0.json");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.AreEqual("FB0AEBDE5785E760C8C10BCF7FDB2C04DBA7E63315D16DDCB6DBC6B25D4AF312",
            Convert.ToHexString(SHA256.HashData(bytes)), "The manifest pin changed. Re-review this test binding.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement manifest = document.RootElement;
        JsonElement input = manifest.GetProperty("inputs").EnumerateArray().Single();
        JsonElement output = manifest.GetProperty("outputs").EnumerateArray().Single();
        JsonElement inputShape = input.GetProperty("shape");
        JsonElement outputShape = output.GetProperty("shape");
        Assert.AreEqual("N", inputShape[0].GetString());
        Assert.AreEqual(1, inputShape[1].GetInt32());
        Assert.AreEqual(32, inputShape[2].GetInt32());
        Assert.AreEqual(32, inputShape[3].GetInt32());
        Assert.AreEqual("N", outputShape[0].GetString());
        Assert.AreEqual(25, outputShape[1].GetInt32());
        JsonElement preprocessing = manifest.GetProperty("preprocessing");

        var model = new ModelIdentity(
            manifest.GetProperty("model_id").GetString()!,
            manifest.GetProperty("model_version").GetString()!,
            manifest.GetProperty("sha256").GetString()!,
            "synthetic://capture-only-no-payload");
        var options = CreateOptions(
            inputShape[3].GetInt32(),
            inputShape[2].GetInt32(),
            radiusScale: 2.25,
            contentBounds,
            input.GetProperty("name").GetString()!,
            output.GetProperty("name").GetString()!) with
        {
            BatchSize = 64,
            StageVersion = model.Version,
            TensorContract = new MarkerClassifierTensorContract(
                input.GetProperty("name").GetString()!,
                output.GetProperty("name").GetString()!,
                inputShape[3].GetInt32(),
                inputShape[2].GetInt32(),
                inputShape[1].GetInt32(),
                outputShape[1].GetInt32() - MarkerClassifierTensorContract.EmbeddingOffset)
            {
                NormalizeMean = preprocessing.GetProperty("normalization_mean").GetSingle(),
                NormalizeScale = preprocessing.GetProperty("normalization_scale").GetSingle(),
                OutputEncoding = MarkerClassifierOutputEncoding.Probabilities,
            },
        };
        return (model, options);
    }

    private static MarkerClassificationRequest CreateClassificationRequest(
        SyntheticInput fixture,
        MarkerClassificationOptions options) =>
        new("synthetic-project", "synthetic-panel", fixture.ImageSha256, SyntheticModelIdentity(),
            fixture.Frame, [fixture.Marker], options);

    private static ProductionWorkflowDetectionRequest CreateWorkflowRequest(byte[] encodedSource)
    {
        string sha = Convert.ToHexStringLower(SHA256.HashData(encodedSource));
        Guid projectId = Guid.Parse("8eb68a14-d70d-43ba-9a30-f5f94c345aa1");
        Guid sourceId = Guid.Parse("a18408fd-f150-43fd-afad-6c6c03509dc7");
        Guid panelId = Guid.Parse("67d5c7a5-f7ba-4bf2-a6d1-0e40711c80e2");
        var image = new WorkflowImageEvidence("synthetic://marker-context/source", sha, 43, 37,
            WorkflowImageVariant.Original);
        var imported = new WorkflowImportedPanel(panelId, sourceId, "Synthetic marker context fixture", image);
        var prepared = new WorkflowPreparedPanel(imported, image, null);
        return new ProductionWorkflowDetectionRequest(prepared, image, WorkflowImageVariant.Original,
            Guid.Parse("3ae6f71a-99a3-40bb-b043-b1373193be59"), projectId, encodedSource);
    }

    private static ModelIdentity SyntheticModelIdentity() =>
        new("synthetic-capture-only-classifier", "test-only", new string('a', 64), "synthetic://no-model-loaded");

    private static string FindProjectRoot(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(current.FullName, "src", "GraphReader.App", "GraphReader.App.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Graph Auto Reader project root.");
    }

    private sealed record SyntheticInput(
        MarkerImageFrame Frame,
        MarkerCenter Marker,
        IReadOnlyDictionary<string, MarkerRectangle>? ContentBounds,
        byte[] EncodedSource,
        string ImageSha256);

    private sealed class StopBeforeModelRunner : IMarkerClassificationInferenceRunner
    {
        public InferenceRequest? Captured { get; private set; }

        public ValueTask<InferenceResponse> RunAsync(InferenceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Captured = request;
            return ValueTask.FromResult(InferenceResponse.Failure(new InferenceError(
                "CAPTURE_ONLY_STOP",
                "diagnostic",
                "Errors.DetectionEvidenceRejected",
                "Capture-only runner stopped before model execution.",
                Recoverable: true,
                "No ONNX model was loaded or executed.")));
        }
    }
}
