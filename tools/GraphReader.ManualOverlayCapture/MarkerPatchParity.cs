// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class Program
{
    private static async Task<int> MarkerPatchParitySelfTest()
    {
        const int width = 40, height = 40, pixels = width * height;
        using var encoded = new MemoryStream();
        using (var gzip = new GZipStream(encoded, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(0x31465247u);
            writer.Write(width);
            writer.Write(height);
            for (int i = 0; i < 3 * pixels; i++) writer.Write(0f);
        }
        MarkerImageFrame frame = ReadParityFrame(encoded.ToArray(), width, height);
        Require(frame.ChannelsFirstPixels.Length == pixels && frame.OcrMask.Values.Length == pixels &&
            frame.ArtifactMask.Values.Length == pixels, "Parity frame self-test failed.");
        var runner = new ParityMarkerRunner([0]);
        var model = new ModelIdentity(ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateRevision,
            ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId,
            ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256, "candidate.onnx");
        var adapter = new ProductionProposalMarkerCenterAdapter(model, runner, multiradiusGeometry: true,
            maskPreservingCandidate: true, plotDomainProposalFiltering: true,
            enclosedGeometrySupport: true, balancedRingSupport: true,
            centerThreshold: ProductionProposalMarkerCenterAdapter.CascadeCenterThreshold);
        var polygon = new MarkerPolygon([new(0, 39), new(39, 39), new(39, 0), new(0, 0)]);
        ProposalMarkerCandidateDiagnosticResult result = await adapter.DetectCandidateWithDiagnosticsAsync(
            frame, polygon, CancellationToken.None);
        Require(result.EmittedProposalCenters.Count > 0 && runner.TotalPatches == result.EmittedProposalCenters.Count &&
            runner.Hashes.Count == 1 && runner.Hashes.ContainsKey(0), "Parity sampler self-test failed.");
        MarkerPoint first = result.EmittedProposalCenters[0];
        var expected = new float[3 * 33 * 33];
        for (int y = 0; y < 33; y++)
            for (int x = 0; x < 33; x++)
                if (first.X + x - 16 >= 0 && first.X + x - 16 < width &&
                    first.Y + y - 16 >= 0 && first.Y + y - 16 < height)
                    expected[(y * 33) + x] = 1;
        Require(runner.Hashes[0] == Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(expected.AsSpan()))),
            "Parity sampler patch self-test failed.");
        Console.WriteLine("Marker patch parity self-test passed.");
        return 0;
    }

    private static async Task<int> VerifyMarkerPatchParity(
        string inputPath, string inputSha, string capturePath, string captureSha,
        string descriptionPath, string descriptionSha, string outputName)
    {
        const string capturedRuntimePath = "artifacts/manual-marker-input-diagnostic-v1/runtime-description-full-stage-v1.json";
        const string capturedRuntimeSha = "3819dccb77614ac69681a203f5ebe359b3eeba9879645624c31ac216dd5606b0";
        const string rasterSha = "f19a0c38020cfaaf0c3e557193815f1710a07dc64bef9aca450178002a46d608";
        byte[] description = await ReadPinned(descriptionPath, descriptionSha, 1_000_000);
        Require(description.AsSpan().SequenceEqual(Describe(workflow: false)), "Parity runtime description differs.");
        using JsonDocument previousRuntime = JsonDocument.Parse(await ReadPinned(capturedRuntimePath, capturedRuntimeSha, 1_000_000));
        using JsonDocument currentRuntime = JsonDocument.Parse(description);
        Dictionary<string, string> AssemblyHashes(JsonElement value) => value.GetProperty("runtime_files").EnumerateArray()
            .Select(item => (Name: Path.GetFileName(item.GetProperty("file").GetString() ?? ""),
                Sha: item.GetProperty("sha256").GetString() ?? ""))
            .Where(item => item.Name.StartsWith("GraphReader.", StringComparison.Ordinal) &&
                item.Name.EndsWith(".dll", StringComparison.Ordinal) && item.Name != "GraphReader.RealAcceptance.Ocr.dll")
            .ToDictionary(item => item.Name, item => item.Sha, StringComparer.Ordinal);
        Dictionary<string, string> before = AssemblyHashes(previousRuntime.RootElement);
        Dictionary<string, string> now = AssemblyHashes(currentRuntime.RootElement);
        Require(before.Count > 0 && before.Count == now.Count && before.All(item =>
            now.TryGetValue(item.Key, out string? sha) && sha == item.Value), "Candidate assembly bytes differ from capture.");

        using JsonDocument input = JsonDocument.Parse(await ReadPinned(inputPath, inputSha, 16_000_000));
        using JsonDocument capture = JsonDocument.Parse(await ReadPinned(capturePath, captureSha, 64_000_000));
        JsonElement root = input.RootElement;
        Require(root.GetProperty("schema").GetString() == "graphreader.manual-overlay-patch-parity-input.v1" &&
            root.GetProperty("scope").GetString() == "private-train-only-controlled-positions" &&
            root.GetProperty("input_sha256").GetString() == rasterSha &&
            root.GetProperty("capture_sha256").GetString() == captureSha &&
            root.GetProperty("capture_runtime_sha256").GetString() == capturedRuntimeSha &&
            root.GetProperty("labels_emitted").ValueKind == JsonValueKind.False &&
            root.GetProperty("optimizer_steps").GetInt32() == 0 &&
            root.GetProperty("sealed_payload_reads").GetInt32() == 0 &&
            root.GetProperty("training_approved").ValueKind == JsonValueKind.False &&
            root.GetProperty("rows").GetArrayLength() + root.GetProperty("unavailable_images").GetInt32() +
                root.GetProperty("images_without_emitted_target").GetInt32() == 200 &&
            capture.RootElement.GetProperty("schema").GetString() == "graphreader.manual-marker-input-capture.v1" &&
            capture.RootElement.GetProperty("input_sha256").GetString() == rasterSha &&
            capture.RootElement.GetProperty("model_binding_sha256").GetString() == ModelBindingSha &&
            capture.RootElement.GetProperty("runtime_description_sha256").GetString() == capturedRuntimeSha &&
            capture.RootElement.GetProperty("truth_read").ValueKind == JsonValueKind.False &&
            capture.RootElement.GetProperty("classification_executed").ValueKind == JsonValueKind.False &&
            capture.RootElement.GetProperty("optimizer_steps").GetInt32() == 0 &&
            capture.RootElement.GetProperty("sealed_payload_reads").GetInt32() == 0 &&
            capture.RootElement.GetProperty("rows").GetArrayLength() == 200,
            "Parity input scope differs.");
        using JsonDocument binding = JsonDocument.Parse(await ReadPinned(ModelBinding, ModelBindingSha, 1_000_000));
        FrozenCandidateModel center = ReadModel(binding.RootElement.GetProperty("marker_center"), "marker_center");
        Require(center.Payload.Sha256 == "4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04",
            "Parity candidate model identity differs.");
        var descriptor = new FrozenCandidateMarkerCenterModelDescriptor(
            new ModelIdentity(center.ModelId, center.Version, center.Payload.Sha256, ProjectPath(center.Payload.RelativePath)),
            ProjectPath(center.Manifest.RelativePath), center.Manifest.Sha256);
        string output = OutputPath(outputName);
        Require(!File.Exists(output) && !Directory.Exists(output), "Parity output already exists.");
        var rows = new List<object>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int selected = 0, matched = 0;
        foreach (JsonElement item in root.GetProperty("rows").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString() ?? "";
            string panelId = item.GetProperty("panel_id").GetString() ?? "";
            Require(ids.Add(id), "Duplicate parity image identity.");
            JsonElement source = capture.RootElement.GetProperty("rows").EnumerateArray()
                .Single(row => row.GetProperty("id").GetString() == id);
            JsonElement panel = source.GetProperty("panels").EnumerateArray()
                .Single(row => row.GetProperty("panel_id").GetString() == panelId);
            string frameFile = item.GetProperty("frame_file").GetString() ?? "";
            string stageFile = item.GetProperty("stage_file").GetString() ?? "";
            string frameSha = item.GetProperty("frame_sha256").GetString() ?? "";
            string stageSha = item.GetProperty("stage_sha256").GetString() ?? "";
            int width = item.GetProperty("width").GetInt32(), height = item.GetProperty("height").GetInt32();
            Require(source.GetProperty("status").GetString() == "completed" &&
                Path.GetFileName(frameFile) == frameFile && frameFile.EndsWith(".bin.gz", StringComparison.Ordinal) &&
                Path.GetFileName(stageFile) == stageFile && stageFile.EndsWith("-stages.json.gz", StringComparison.Ordinal) &&
                panel.GetProperty("frame_file").GetString() == frameFile && panel.GetProperty("frame_sha256").GetString() == frameSha &&
                panel.GetProperty("stage_file").GetString() == stageFile && panel.GetProperty("stage_sha256").GetString() == stageSha &&
                panel.GetProperty("frame_width").GetInt32() == width && panel.GetProperty("frame_height").GetInt32() == height &&
                width > 0 && height > 0 && (long)width * height <= 40_000_000,
                "Parity panel identity differs.");
            byte[] encodedFrame = await ReadPinned($"{OutputRoot}/full-stage-capture-v2/{frameFile}", frameSha, 64_000_000);
            MarkerImageFrame frame = ReadParityFrame(encodedFrame, width, height);
            byte[] encodedStage = await ReadPinned($"{OutputRoot}/full-stage-capture-v2/{stageFile}", stageSha, 100_000_000);
            using var compressed = new MemoryStream(encodedStage, writable: false);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using JsonDocument stage = await JsonDocument.ParseAsync(gzip);
            Require(stage.RootElement.GetProperty("schema").GetString() == "graphreader.manual-marker-stage-evidence.v1",
                "Parity stage schema differs.");
            MarkerPoint[] polygon = stage.RootElement.GetProperty("plot").EnumerateArray()
                .Select(value => new MarkerPoint(value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble())).ToArray();
            MarkerPoint[] expectedCenters = stage.RootElement.GetProperty("emitted_proposals").EnumerateArray()
                .Select(value => new MarkerPoint(value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble())).ToArray();
            var centerIndex = new Dictionary<MarkerPoint, int>();
            for (int index = 0; index < expectedCenters.Length; index++)
                Require(centerIndex.TryAdd(expectedCenters[index], index), "Duplicate frozen proposal center.");
            var expected = new Dictionary<int, string>();
            foreach (JsonElement target in item.GetProperty("targets").EnumerateArray())
            {
                var point = new MarkerPoint(target.GetProperty("x").GetInt32(), target.GetProperty("y").GetInt32());
                string sha = target.GetProperty("python_patch_sha256").GetString() ?? "";
                Require(centerIndex.TryGetValue(point, out int index) && sha.Length == 64 &&
                    sha.All(Uri.IsHexDigit) && expected.TryAdd(index, sha), "Parity target differs from frozen proposals.");
            }
            Require(expected.Count > 0, "Parity panel has no selected proposals.");
            var runner = new ParityMarkerRunner(expected.Keys);
            ProductionProposalMarkerCenterAdapter adapter =
                ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateCascadeEvaluation(descriptor, runner);
            Require(!adapter.IsApproved, "Parity candidate unexpectedly approved.");
            ProposalMarkerCandidateDiagnosticResult result = await adapter.DetectCandidateWithDiagnosticsAsync(
                frame, new MarkerPolygon(polygon), CancellationToken.None);
            Require(result.EmittedProposalCenters.SequenceEqual(expectedCenters) && runner.TotalPatches == expectedCenters.Length &&
                runner.Hashes.Count == expected.Count, "Native proposal sequence differs from captured stage.");
            int panelMatched = expected.Count(pair => runner.Hashes.TryGetValue(pair.Key, out string? sha) &&
                string.Equals(sha, pair.Value, StringComparison.OrdinalIgnoreCase));
            selected += expected.Count;
            matched += panelMatched;
            rows.Add(new { id, panel_id = panelId, selected = expected.Count, matched = panelMatched });
        }
        Require(rows.Count > 0 && selected > 0, "Parity selection was empty.");
        Require(description.AsSpan().SequenceEqual(Describe(workflow: false)), "Parity runtime changed.");
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { schema = "graphreader.manual-overlay-patch-parity.v1",
            scope = "train-only-diagnostic-not-acceptance", input_sha256 = inputSha, capture_sha256 = captureSha,
            capture_runtime_sha256 = capturedRuntimeSha, runtime_description_sha256 = descriptionSha,
            selected_panels = rows.Count, selected_patches = selected, matching_patches = matched,
            optimizer_steps = 0, model_inference_steps = 0, sealed_payload_reads = 0, training_approved = false, rows }, JsonOptions);
        await WriteNew(output, report);
        Console.WriteLine($"panels={rows.Count}; selected={selected}; matched={matched}; report_sha256={Hash(report)}");
        return selected == matched ? 0 : 1;
    }

    private static MarkerImageFrame ReadParityFrame(byte[] encoded, int width, int height)
    {
        Require(BitConverter.IsLittleEndian, "Parity frame requires little endian.");
        using var input = new MemoryStream(encoded, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        Require(reader.ReadUInt32() == 0x31465247 && reader.ReadInt32() == width && reader.ReadInt32() == height,
            "Parity frame header differs.");
        int pixels = checked(width * height);
        float[] ReadPlane()
        {
            byte[] bytes = reader.ReadBytes(checked(pixels * sizeof(float)));
            Require(bytes.Length == pixels * sizeof(float), "Parity frame is truncated.");
            var values = new float[pixels];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
        float[] pixelsFirst = ReadPlane(), ocr = ReadPlane(), artifact = ReadPlane();
        Require(gzip.ReadByte() == -1, "Parity frame has extra data.");
        return new MarkerImageFrame(width, height, 1, pixelsFirst, MarkerSourceImage.Original,
            MarkerAffineTransform.Identity, new MarkerMask(width, height, ocr), new MarkerMask(width, height, artifact));
    }

    private sealed class ParityMarkerRunner(IEnumerable<int> selected) : IProposalMarkerInferenceRunner
    {
        private readonly HashSet<int> selectedIndices = selected.ToHashSet();
        public Dictionary<int, string> Hashes { get; } = [];
        public int TotalPatches { get; private set; }

        public ValueTask<InferenceResponse> RunAsync(InferenceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const int patchElements = 3 * ProductionProposalMarkerCenterAdapter.PatchSize * ProductionProposalMarkerCenterAdapter.PatchSize;
            int count = checked((int)request.Input.Shape[0]);
            Require(request.Input.Shape.SequenceEqual(new long[] { count, 3, 33, 33 }) &&
                request.Input.Values.Length == checked(count * patchElements), "Parity tensor shape differs.");
            for (int i = 0; i < count; i++)
            {
                int index = TotalPatches + i;
                if (selectedIndices.Contains(index))
                    Hashes.Add(index, Convert.ToHexStringLower(SHA256.HashData(
                        MemoryMarshal.AsBytes(request.Input.Values.Span.Slice(i * patchElements, patchElements)))));
            }
            TotalPatches += count;
            return ValueTask.FromResult(new InferenceResponse(true,
                new InferenceExecution(new float[checked(count * 4)], InferenceProvider.Cpu,
                    new StageTiming(0, 0, 0, 0, 0, false, false), new MemoryDiagnostics(0, 0, 0, 0, 0)),
                null, [new ProviderAttempt(InferenceProvider.Cpu, true, null)]));
        }
    }
}
