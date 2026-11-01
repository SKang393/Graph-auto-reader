// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Imaging;
using GraphReader.Ocr;
using AxisPixelPoint = GraphReader.Axis.PixelPoint;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class Program
{
    private static readonly string[] ConnectivityParityPartitions = ["train:", "dev:"];

    private static async Task<int> CheckAxisConnectivityParity(string inputPath, string inputSha,
        string descriptionPath, string descriptionSha, string outputName)
    {
        const string expectedInput = "7e5e8dc0bf40521bdbc66fa91a370a7613eb8da65d9cd128bd9fca1af2151dd5";
        const string devRoot = "artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1/images/";
        Require(inputSha == expectedInput, "Connectivity parity input is not pinned.");
        using var classifierLocks = new ClassifierInputLocks();
        byte[] description = await ReadPinned(descriptionPath, descriptionSha, 1_000_000);
        Require(description.AsSpan().SequenceEqual(Describe(workflow: true)), "Frozen runtime description differs.");
        byte[] bytes = await ReadPinned(inputPath, inputSha, 1_000_000);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Require(root.GetProperty("schema").GetString() == "graphreader.axis-connectivity-parity-input.v1" &&
            root.GetProperty("scope").GetString() == "train-and-dev-no-label-truth" &&
            !root.GetProperty("truth_read").GetBoolean() && root.GetProperty("optimizer_steps").GetInt32() == 0 &&
            root.GetProperty("sealed_payload_reads").GetInt32() == 0, "Connectivity parity scope differs.");
        var entries = new List<(string Id, string Path, string Sha, int Width, int Height,
            OcrRectangle Box, GeometryLineSegment Axis, bool Expected)>();
        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            Exact(row, "id", "image_path", "image_sha256", "width", "height", "box", "axis", "expected_connected");
            string id = row.GetProperty("id").GetString() ?? "";
            string path = row.GetProperty("image_path").GetString() ?? "";
            string sha = row.GetProperty("image_sha256").GetString() ?? "";
            int width = row.GetProperty("width").GetInt32(), height = row.GetProperty("height").GetInt32();
            double[] box = row.GetProperty("box").EnumerateArray().Select(static item => item.GetDouble()).ToArray();
            double[] axis = row.GetProperty("axis").EnumerateArray().Select(static item => item.GetDouble()).ToArray();
            Require((id.StartsWith("train:", StringComparison.Ordinal) && path.StartsWith(OriginalRasterRoot, StringComparison.Ordinal) ||
                     id.StartsWith("dev:", StringComparison.Ordinal) && path.StartsWith(devRoot, StringComparison.Ordinal)) &&
                width > 0 && height > 0 && box.Length == 4 && axis.Length == 4 &&
                box.All(double.IsFinite) && axis.All(double.IsFinite) && box[0] >= 0 && box[1] >= 0 &&
                box[2] > box[0] && box[3] > box[1] && box[2] <= width && box[3] <= height && axis[1] != axis[3],
                "Connectivity parity row differs.");
            entries.Add((id, path, sha, width, height, new OcrRectangle(box[0], box[1], box[2] - box[0], box[3] - box[1]),
                new GeometryLineSegment(new AxisPixelPoint(axis[0], axis[1]), new AxisPixelPoint(axis[2], axis[3])),
                row.GetProperty("expected_connected").GetBoolean()));
        }
        var sources = entries.GroupBy(static entry => (entry.Path, entry.Sha, entry.Width, entry.Height)).ToArray();
        Require(entries.Count == 196 && entries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == 196 &&
            sources.Length == 20 && sources.Select(static source => source.Key.Sha).Distinct(StringComparer.Ordinal).Count() == 20,
            "Connectivity parity inventory differs.");
        string output = OutputPath(outputName);
        Require(!File.Exists(output) && !Directory.Exists(output), "Connectivity parity output exists.");
        var results = new List<(string Id, bool Expected, bool Actual)>();
        foreach (var source in sources)
        {
            await using var sourceLock = new FileStream(ProjectPath(source.Key.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] encoded = await ReadPinned(source.Key.Path, source.Key.Sha, 64_000_000);
            ImageImportResult imported = await new ImageImportService().ImportBytesAsync(
                source.Key.Path, new ImmutableImageBytes(encoded), CancellationToken.None);
            ImportedImage image = imported.Image ?? throw new InvalidDataException("Connectivity parity image import failed.");
            Require(image.Sha256 == source.Key.Sha && image.Metadata.FrameCount == 1 &&
                image.Metadata.Width == source.Key.Width && image.Metadata.Height == source.Key.Height,
                "Connectivity parity source identity differs.");
            Guid project = ProductionWorkflowPanelStore.CreateStableId("axis-connectivity-parity-project-v1", inputSha);
            Guid sourceId = ProductionWorkflowPanelStore.CreateStableId("axis-connectivity-parity-source-v1", inputSha, source.Key.Sha);
            Guid panelId = ProductionWorkflowPanelStore.CreateStableId("axis-connectivity-parity-panel-v1", inputSha, source.Key.Sha);
            var evidence = new WorkflowImageEvidence("memory:connectivity-parity", source.Key.Sha,
                source.Key.Width, source.Key.Height, WorkflowImageVariant.Original);
            var panel = new WorkflowImportedPanel(panelId, sourceId, "connectivity-parity", evidence);
            var request = new ProductionWorkflowDetectionRequest(new WorkflowPreparedPanel(panel, evidence, null),
                evidence, WorkflowImageVariant.Original, Guid.NewGuid(), project, encoded);
            OcrImage raster = new ProductionRasterFrameDecoder().Decode(request, CancellationToken.None).CreateOcrImage();
            foreach (var row in source)
            {
                bool actual = ProductionAxisConnectedTextReview.HasConnectedInk(raster, row.Box, row.Axis, CancellationToken.None);
                results.Add((row.Id, row.Expected, actual));
            }
            _ = await ReadPinned(source.Key.Path, source.Key.Sha, 64_000_000);
        }
        Require(results.Count == entries.Count && description.AsSpan().SequenceEqual(Describe(workflow: true)),
            "Connectivity parity runtime or inventory changed.");
        object[] partitions = ConnectivityParityPartitions.Select(prefix =>
        {
            var group = results.Where(row => row.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return (object)new { split = prefix[..^1], rows = group.Length,
                matched = group.Count(static row => row.Expected == row.Actual),
                python_only = group.Count(static row => row.Expected && !row.Actual),
                native_only = group.Count(static row => !row.Expected && row.Actual) };
        }).ToArray();
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { schema = "graphreader.axis-connectivity-parity-result.v1",
            scope = "train-and-dev-diagnostic-not-acceptance", input_sha256 = inputSha, runtime_description_sha256 = descriptionSha,
            truth_read = false, optimizer_steps = 0, sealed_payload_reads = 0, partitions,
            rows = results.Select(static row => new { id = row.Id, expected = row.Expected, actual = row.Actual }).ToArray() }, JsonOptions);
        await WriteNew(output, report);
        Console.WriteLine($"completed rows={results.Count}; mismatches={results.Count(static row => row.Expected != row.Actual)}; report_sha256={Hash(report)}");
        return 0;
    }
}
