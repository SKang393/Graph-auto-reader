// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class Program
{
    private static async Task<int> CheckOrientationCrops(string descriptionPath, string descriptionSha, string outputName)
    {
        const string inputSha = "b9044372d5d6b37257ef0e1823935eaed840b99f9dc5a66a2c5359a1b7beeba6";
        const string reviewSha = "6d793b8887d82f334d8adabcdd8bf2e557749f95842c3fc1a617d17303447e78";
        const string directSha = "1658ac4b5532a2ab26188c90446b820d0ce566e37617085787d32d680cac0722";
        var cases = new[]
        {
            (Id: "workflow-y-0033", Source: "source-006", Trace: "artifacts/manual-marker-input-diagnostic-v1/trace-source-006-v1/report.json",
                Sha: "46da3ebe6045cce2d24570eae2f4c15fca1eb30936bfa81a0f9184675273e5c3"),
            (Id: "workflow-y-0109", Source: "source-019", Trace: "artifacts/manual-marker-input-diagnostic-v1/trace-source-019-v1/report.json",
                Sha: "130541a64335d3aed237f9569fda40a3cbf8bf7fa40126e91083c78cf4b92689"),
        };
        using var classifierLocks = new ClassifierInputLocks();
        byte[] description = await ReadPinned(descriptionPath, descriptionSha, 1_000_000);
        Require(description.AsSpan().SequenceEqual(Describe(workflow: true)), "Orientation crop runtime differs.");
        using JsonDocument input = JsonDocument.Parse(await ReadPinned(
            "artifacts/manual-marker-input-diagnostic-v1/original-raster-input-v1.json", inputSha, 1_000_000));
        using JsonDocument review = JsonDocument.Parse(await ReadPinned(
            "artifacts/manual-marker-input-diagnostic-v1/workflow-y-review-v1/manifest.json", reviewSha, 1_000_000));
        using JsonDocument direct = JsonDocument.Parse(await ReadPinned(
            "artifacts/manual-marker-input-diagnostic-v1/workflow-y-padded-trace-v1.json", directSha, 1_000_000));
        using JsonDocument binding = JsonDocument.Parse(await ReadPinned(ModelBinding, ModelBindingSha, 1_000_000));
        FrozenCandidateModel model = ReadModel(binding.RootElement.GetProperty("ocr_recognition"), "ocr_recognition");
        Require(model.Payload.Sha256 == "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418",
            "Orientation crop recognizer identity differs.");
        var identity = new ModelIdentity(model.ModelId, model.Version, model.Payload.Sha256, ProjectPath(model.Payload.RelativePath));
        (_, OcrPipelineOptions pipeline) = ProductionOcrAdapter.ReadRecognitionOptions(identity, ProjectPath(model.Manifest.RelativePath));
        var options = new OcrCropBatcherOptions
        {
            TargetWidth = pipeline.CropWidth, TargetHeight = pipeline.CropHeight, WidthMode = pipeline.CropWidthMode,
            MaximumTargetWidth = pipeline.MaximumCropWidth, BatchSize = pipeline.BatchSize, PaddingPixels = pipeline.CropPaddingPixels,
            HorizontalPaddingPixels = pipeline.CropHorizontalPaddingPixels, VerticalPaddingPixels = pipeline.CropVerticalPaddingPixels,
            VerticalContentPaddingRatio = pipeline.CropVerticalContentPaddingRatio, ResizeMode = pipeline.CropResizeMode,
            PaddingValue = pipeline.CropPaddingValue,
        };
        string output = OutputPath(outputName);
        Require(!File.Exists(output) && !Directory.Exists(output), "Orientation crop output exists.");
        var results = new List<object>();
        foreach (var item in cases)
        {
            JsonElement reviewRow = review.RootElement.GetProperty("rows").EnumerateArray().Single(
                row => row.GetProperty("id").GetString() == item.Id);
            Require(reviewRow.GetProperty("source_id").GetString() == item.Source, "Orientation crop source identity differs.");
            string panelId = reviewRow.GetProperty("panel_id").GetString() ?? "";
            string regionId = reviewRow.GetProperty("region_id").GetString() ?? "";
            JsonElement inputRow = input.RootElement.GetProperty("rows").EnumerateArray().Single(
                row => row.GetProperty("id").GetString() == item.Source);
            string sourcePath = inputRow.GetProperty("image_path").GetString() ?? "";
            string sourceSha = inputRow.GetProperty("image_sha256").GetString() ?? "";
            Require(sourcePath.StartsWith(OriginalRasterRoot, StringComparison.Ordinal) &&
                sourceSha == reviewRow.GetProperty("image_sha256").GetString(), "Orientation image binding differs.");
            await using var sourceLock = new FileStream(ProjectPath(sourcePath), FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] encoded = await ReadPinned(sourcePath, sourceSha, 64_000_000);
            ImageImportResult imported = await new ImageImportService().ImportBytesAsync(
                sourcePath, new ImmutableImageBytes(encoded), CancellationToken.None);
            ImportedImage image = imported.Image ?? throw new InvalidDataException("Orientation image import failed.");
            Require(image.Sha256 == sourceSha && image.Metadata.FrameCount == 1, "Orientation image identity differs.");
            Guid project = ProductionWorkflowPanelStore.CreateStableId("orientation-crop-project-v1", inputSha);
            Guid sourceId = ProductionWorkflowPanelStore.CreateStableId("orientation-crop-source-v1", inputSha, sourceSha);
            var evidence = new WorkflowImageEvidence("memory:orientation-crop", sourceSha,
                image.Metadata.Width, image.Metadata.Height, WorkflowImageVariant.Original);
            var panel = new WorkflowImportedPanel(Guid.Parse(panelId), sourceId, "orientation-crop", evidence);
            var request = new ProductionWorkflowDetectionRequest(new WorkflowPreparedPanel(panel, evidence, null),
                evidence, WorkflowImageVariant.Original, Guid.NewGuid(), project, encoded);
            OcrImage raster = new ProductionRasterFrameDecoder().Decode(request, CancellationToken.None).CreateOcrImage();
            using JsonDocument trace = JsonDocument.Parse(await ReadPinned(item.Trace, item.Sha, 8_000_000));
            JsonElement tracePanel = trace.RootElement.GetProperty("rows")[0].GetProperty("panels").EnumerateArray()
                .Single(value => value.GetProperty("panel_id").GetString() == panelId);
            Require(tracePanel.GetProperty("provenance").GetProperty("identity").GetBoolean(),
                "Orientation crop requires an unmodified full-image panel.");
            JsonElement detected = tracePanel.GetProperty("detection_trace").EnumerateArray()
                .SelectMany(value => value.GetProperty("raw_detector_regions").EnumerateArray())
                .Single(value => value.GetProperty("region_id").GetString() == regionId);
            JsonElement traceCrop = tracePanel.GetProperty("recognition_trace").EnumerateArray()
                .Single(value => value.GetProperty("region_id").GetString() == regionId);
            JsonElement bounds = detected.GetProperty("polygon").GetProperty("bounds");
            var rectangle = new OcrRectangle(bounds.GetProperty("x").GetDouble(), bounds.GetProperty("y").GetDouble(),
                bounds.GetProperty("width").GetDouble(), bounds.GetProperty("height").GetDouble());
            double orientation = detected.GetProperty("orientation_degrees").GetDouble();
            if (pipeline.InferVerticalOrientationForTallRegions && Math.Abs(orientation) <= double.Epsilon &&
                rectangle.Height > rectangle.Width * 1.4)
                orientation = -90d;
            var detectedRegion = new OcrDetectedRegion(regionId, OcrPolygon.FromRectangle(rectangle), orientation, 1);
            OcrCrop observed = OcrCropBatcher.CreateBatches(raster, [detectedRegion], options).Single().Single();
            OcrCrop horizontal = OcrCropBatcher.CreateBatches(raster, [detectedRegion with { OrientationDegrees = 0 }], options).Single().Single();
            JsonElement directRow = direct.RootElement.GetProperty("rows").EnumerateArray().Single(
                row => row.GetProperty("id").GetString() == item.Id).GetProperty("server");
            results.Add(new { id = item.Id, orientation_degrees = orientation, observed_width = observed.Width,
                observed_height = observed.Height, horizontal_width = horizontal.Width, horizontal_height = horizontal.Height,
                observed_matches_workflow = string.Equals(observed.CropSha256, traceCrop.GetProperty("crop_sha256").GetString(), StringComparison.OrdinalIgnoreCase),
                horizontal_matches_direct = string.Equals(horizontal.CropSha256, directRow.GetProperty("crop_sha256").GetString(), StringComparison.OrdinalIgnoreCase),
                observed_sha256 = observed.CropSha256, horizontal_sha256 = horizontal.CropSha256 });
            _ = await ReadPinned(sourcePath, sourceSha, 64_000_000);
        }
        Require(description.AsSpan().SequenceEqual(Describe(workflow: true)), "Orientation crop runtime changed.");
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { schema = "graphreader.orientation-crop-check.v1",
            scope = "exposed-train-diagnostic-not-acceptance", runtime_description_sha256 = descriptionSha,
            truth_read = false, inference_steps = 0, optimizer_steps = 0, sealed_payload_reads = 0, results }, JsonOptions);
        await WriteNew(output, report);
        Console.WriteLine($"completed cases={results.Count}; report_sha256={Hash(report)}");
        return 0;
    }
}
