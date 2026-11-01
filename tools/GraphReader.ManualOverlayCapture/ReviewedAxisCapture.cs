// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class Program
{
    private static async Task<int> RecognizeReviewedAxis(string inputPath, string inputSha,
        string descriptionPath, string descriptionSha, string outputName)
    {
        const string expectedInput = "06d77e4621e536ea3328e6baed894d2a8f449bc5d6c2534df0c0f31dba2b8d81";
        const string sourceRoot = "artifacts/manual-dig-train-anchor-review-v1/images/";
        Require(inputSha == expectedInput, "Reviewed-axis input is not pinned.");
        using var classifierLocks = new ClassifierInputLocks();
        byte[] description = await ReadPinned(descriptionPath, descriptionSha, 1_000_000);
        Require(description.AsSpan().SequenceEqual(Describe(workflow: true)), "Frozen runtime description differs.");
        byte[] input = await ReadPinned(inputPath, inputSha, 1_000_000);
        using JsonDocument document = JsonDocument.Parse(input);
        JsonElement root = document.RootElement;
        Exact(root, "schema", "scope", "source_split", "rows");
        Require(root.GetProperty("schema").GetString() == "graphreader.manual-axis-ocr-input.v1" &&
            root.GetProperty("scope").GetString() == "assistant-reviewed-training-only" &&
            root.GetProperty("source_split").GetString() == "internal_train_build", "Reviewed-axis scope differs.");

        var entries = new List<(string Id, string Path, string Sha, double[] Box)>();
        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            Exact(row, "id", "image_path", "image_sha256", "box_xyxy");
            string id = row.GetProperty("id").GetString() ?? "";
            string path = row.GetProperty("image_path").GetString() ?? "";
            string sha = row.GetProperty("image_sha256").GetString() ?? "";
            double[] box = row.GetProperty("box_xyxy").EnumerateArray().Select(static value => value.GetDouble()).ToArray();
            Require(id == $"item-{entries.Count:D3}" && path.StartsWith(sourceRoot, StringComparison.Ordinal) &&
                box.Length == 4 && box.All(double.IsFinite) && box[0] >= 0 && box[1] >= 0 &&
                box[2] > box[0] && box[3] > box[1], "Reviewed-axis crop differs.");
            entries.Add((id, path, sha, box));
        }
        var sources = entries.GroupBy(static entry => (entry.Path, entry.Sha)).ToArray();
        Require(entries.Count == 76 && sources.Length == 8 &&
            sources.Select(static source => source.Key.Path).Distinct(StringComparer.Ordinal).Count() == 8 &&
            sources.Select(static source => source.Key.Sha).Distinct(StringComparer.Ordinal).Count() == 8,
            "Reviewed-axis source inventory differs.");
        string output = OutputPath(outputName);
        Require(!File.Exists(output) && !Directory.Exists(output), "Reviewed-axis output already exists.");
        byte[] binding = await ReadPinned(ModelBinding, ModelBindingSha, 1_000_000);
        using JsonDocument models = JsonDocument.Parse(binding);
        FrozenCandidateModel model = ReadModel(models.RootElement.GetProperty("ocr_recognition"), "ocr_recognition");
        Require(model.Payload.Sha256 == "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418",
            "Reviewed-axis recognizer identity differs.");
        using var modelLock = new FileStream(ProjectPath(model.Payload.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        nint native = NativeLibrary.Load(NativePath());
        NativeLibrary.SetDllImportResolver(typeof(Mat).Assembly,
            (name, _, _) => name == "OpenCvSharpExtern" ? native : nint.Zero);
        var identity = new ModelIdentity(model.ModelId, model.Version, model.Payload.Sha256, ProjectPath(model.Payload.RelativePath));
        (LocalOnnxTextRecognizerOptions recognizerOptions, OcrPipelineOptions pipelineOptions) =
            ProductionOcrAdapter.ReadRecognitionOptions(identity, ProjectPath(model.Manifest.RelativePath));
        Require(!ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(ProjectPath(model.Manifest.RelativePath)),
            "Reviewed-axis recognizer requires an unsupported spacing wrapper.");
        var cropOptions = new OcrCropBatcherOptions
        {
            TargetWidth = pipelineOptions.CropWidth, TargetHeight = pipelineOptions.CropHeight,
            WidthMode = pipelineOptions.CropWidthMode, MaximumTargetWidth = pipelineOptions.MaximumCropWidth,
            BatchSize = pipelineOptions.BatchSize, PaddingPixels = pipelineOptions.CropPaddingPixels,
            HorizontalPaddingPixels = pipelineOptions.CropHorizontalPaddingPixels,
            VerticalPaddingPixels = pipelineOptions.CropVerticalPaddingPixels,
            VerticalContentPaddingRatio = pipelineOptions.CropVerticalContentPaddingRatio,
            ResizeMode = pipelineOptions.CropResizeMode, PaddingValue = pipelineOptions.CropPaddingValue,
        };
        await using var runtime = new ProductionInferenceRuntimeHost(new OrtExecutionProviderDiscovery(),
            new WindowsExecutionProviderPolicy(), new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled),
            CpuThreadConfiguration.Create(2), [InferenceProvider.Cpu], output + ".runtime-cache", 1, 1, new NoPersistenceStageCache());
        var recognizer = new LocalOnnxTextRecognizer(runtime.Runtime, recognizerOptions);
        var outcomes = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            await using var sourceLock = new FileStream(ProjectPath(source.Key.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] bytes = await ReadPinned(source.Key.Path, source.Key.Sha, 64_000_000);
            ImageImportResult imported = await new ImageImportService().ImportBytesAsync(
                source.Key.Path, new ImmutableImageBytes(bytes), CancellationToken.None);
            ImportedImage sourceImage = imported.Image ?? throw new InvalidDataException("Reviewed-axis image import failed.");
            Require(sourceImage.Sha256 == source.Key.Sha && sourceImage.Metadata.FrameCount == 1,
                "Reviewed-axis image identity differs.");
            Guid project = ProductionWorkflowPanelStore.CreateStableId("reviewed-axis-project-v1", inputSha);
            Guid sourceId = ProductionWorkflowPanelStore.CreateStableId("reviewed-axis-source-v1", inputSha, source.Key.Sha);
            Guid panelId = ProductionWorkflowPanelStore.CreateStableId("reviewed-axis-panel-v1", inputSha, source.Key.Sha);
            var imageEvidence = new WorkflowImageEvidence("memory:reviewed-axis", source.Key.Sha,
                sourceImage.Metadata.Width, sourceImage.Metadata.Height, WorkflowImageVariant.Original);
            var panel = new WorkflowImportedPanel(panelId, sourceId, "reviewed-axis", imageEvidence);
            var request = new ProductionWorkflowDetectionRequest(new WorkflowPreparedPanel(panel, imageEvidence, null),
                imageEvidence, WorkflowImageVariant.Original, Guid.NewGuid(), project, bytes);
            OcrImage image = new ProductionRasterFrameDecoder().Decode(request, CancellationToken.None).CreateOcrImage();
            OcrDetectedRegion[] regions = source.Select(entry =>
            {
                double[] box = entry.Box;
                Require(box[2] <= image.Width && box[3] <= image.Height, "Reviewed-axis crop exceeds the image.");
                return new OcrDetectedRegion(entry.Id,
                    OcrPolygon.FromRectangle(new OcrRectangle(box[0], box[1], box[2] - box[0], box[3] - box[1])), 0, 1);
            }).ToArray();
            foreach (IReadOnlyList<OcrCrop> batch in OcrCropBatcher.CreateBatches(image, regions, cropOptions))
            {
                IReadOnlyList<OcrRecognition> readings = await recognizer.RecognizeBatchAsync(batch, CancellationToken.None);
                Require(readings.Count == batch.Count && readings.Select(static item => item.RegionId).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(batch.Select(static crop => crop.RegionId)), "Reviewed-axis recognition inventory differs.");
                foreach (OcrRecognition reading in readings)
                {
                    OcrRecognitionAlternative? best = reading.Alternatives.OrderByDescending(static item => item.Confidence)
                        .ThenBy(static item => item.Text, StringComparer.Ordinal).FirstOrDefault();
                    Require(outcomes.TryAdd(reading.RegionId, new { text = best?.Text, confidence = best?.Confidence,
                        alternatives = reading.Alternatives.Select(item =>
                        {
                            NumericParseResult parsed = GraphNumericParser.Parse(item.Text);
                            return new { item.Text, item.Confidence, numeric = parsed.IsSuccess, value = parsed.Value };
                        }).ToArray(), failure = reading.Failure?.Code }), "Repeated reviewed-axis recognition identity.");
                }
            }
            _ = await ReadPinned(source.Key.Path, source.Key.Sha, 64_000_000);
        }
        Require(outcomes.Count == entries.Count && entries.All(entry => outcomes.ContainsKey(entry.Id)),
            "Reviewed-axis output inventory differs.");
        Require(description.AsSpan().SequenceEqual(Describe(workflow: true)), "Runtime changed during reviewed-axis recognition.");
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { schema = "graphreader.manual-reviewed-server-ocr.v1",
            scope = "train-diagnostic-not-acceptance", input_sha256 = inputSha, runtime_description_sha256 = descriptionSha,
            model_sha256 = model.Payload.Sha256, source_split = "internal_train_build", source_count = sources.Length,
            crop_profile = "candidate-standard-padding", inference_provider = "CPUExecutionProvider", optimizer_steps = 0,
            truth_read = false, sealed_payload_reads = 0, rows = entries.Select(entry => new { id = entry.Id, server = outcomes[entry.Id] }).ToArray() }, JsonOptions);
        await WriteNew(output, report);
        Console.WriteLine($"completed rows={entries.Count}; sources={sources.Length}; report_sha256={Hash(report)}");
        return 0;
    }
}
