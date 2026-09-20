// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static partial class OfficialHeadCandidateEvaluation
{
    private const string HistoricalLayoutRequestSha =
        "5875c407ac3bcacc1d9f843f72fb12ca7b61d08484227e468d906dc50f4bb615";

    internal static void ValidateLayoutClearanceScope(JsonElement request)
    {
        RequireProperties(request, "schema", "synthetic_only", "private_data", "sealed_data",
            "truth_included", "production_approved", "training_input_ready", "historical_request",
            "generator_versions", "generator_sources", "sources", "panels");
        string schema = Text(request, "schema");
        if ((schema != "graphreader.layout-clearance-ocr-inputs.v1" &&
             schema != "graphreader.layout-clearance-ocr-inputs.v2") ||
            !request.GetProperty("synthetic_only").GetBoolean() ||
            request.GetProperty("private_data").GetBoolean() || request.GetProperty("sealed_data").GetBoolean() ||
            request.GetProperty("truth_included").GetBoolean() ||
            request.GetProperty("production_approved").GetBoolean() ||
            request.GetProperty("training_input_ready").GetBoolean())
        {
            throw new InvalidDataException("Layout diagnostic requires unapproved, truth-free synthetic train/dev inputs.");
        }
        string?[] versions = request.GetProperty("generator_versions").EnumerateArray()
            .Select(static item => item.GetString()).ToArray();
        string[] expectedVersions = schema == "graphreader.layout-clearance-ocr-inputs.v2"
            ? ["synthetic-arrow-label-clearance-v1", "synthetic-legend-clearance-v1", "synthetic-peripheral-text-clearance-v1"]
            : ["synthetic-arrow-label-clearance-v1", "synthetic-legend-clearance-v1"];
        if (!versions.SequenceEqual(expectedVersions) ||
            request.GetProperty("sources").GetArrayLength() != 23 ||
            request.GetProperty("panels").GetArrayLength() != ExpectedPanelCount)
        {
            throw new InvalidDataException("Layout diagnostic generator or inventory differs from its fixed profile.");
        }
    }

    private static EvaluationPanel[] ReadLayoutClearancePanels(
        JsonElement request, string root, CancellationToken cancellationToken)
    {
        ValidateLayoutClearanceScope(request);
        JsonElement historical = request.GetProperty("historical_request");
        RequireProperties(historical, "path", "sha256");
        if (RequireSha(Text(historical, "sha256"), "historical request") != HistoricalLayoutRequestSha)
        {
            throw new InvalidDataException("Layout diagnostic requires the authenticated historical train/dev inventory.");
        }
        using var originalDocument = JsonDocument.Parse(ReadVerified(
            RepositoryPath(root, Text(historical, "path")), HistoricalLayoutRequestSha, null, "historical request"));
        JsonElement original = originalDocument.RootElement;
        ValidateCaptureScope(original, supplemental: false);
        VerifyDescriptor(original.GetProperty("binding"), root, "historical generator binding");
        VerifyDescriptor(original.GetProperty("capture_source"), root, "historical capture source");
        Dictionary<string, ReportBinding> reports = ReadReports(original, root, supplemental: false);
        EvaluationPanel[] parents = ReadPanels(original, reports, root, supplemental: false, insidePlot: true);

        string[] requiredSources = ["ml/synthetic/annotation_clearance.py", "ml/synthetic/legend_clearance.py",
            "ml/synthetic/renderer.py", "ml/synthetic/runtime_graph_visible_content_v3.py",
            "ml/synthetic/templates.py", "ml/synthetic/fonts.py"];
        if (Text(request, "schema") == "graphreader.layout-clearance-ocr-inputs.v2")
        {
            requiredSources = [.. requiredSources, "ml/synthetic/text_layout_clearance.py"];
        }
        JsonElement[] generatorSources = request.GetProperty("generator_sources").EnumerateArray().ToArray();
        if (!generatorSources.Select(item => Text(item, "path")).Order(StringComparer.Ordinal)
                .SequenceEqual(requiredSources.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("Layout generator source inventory is incomplete or duplicated.");
        }
        foreach (JsonElement source in generatorSources)
        {
            RequireProperties(source, "path", "sha256");
            VerifyDescriptor(source, root, "layout generator source");
        }
        var sources = new Dictionary<string, (string Sha, OcrImage Image)>(StringComparer.Ordinal);
        var sourceHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in request.GetProperty("sources").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireProperties(item, "historical_sha256", "split", "png");
            string priorSha = RequireSha(Text(item, "historical_sha256"), "historical source");
            EvaluationPanel parent = parents.FirstOrDefault(p => p.SourceSha256 == priorSha)
                ?? throw new InvalidDataException("Derived source is absent from the historical inventory.");
            if (Text(item, "split") != parent.Split || sources.ContainsKey(priorSha))
            {
                throw new InvalidDataException("Derived source is duplicated or crosses a split.");
            }
            var png = ReadLayoutPng(item.GetProperty("png"), root);
            if (!sourceHashes.Add(png.Sha))
            {
                throw new InvalidDataException("Derived sources repeat the same pixels across distinct cases.");
            }
            EvaluationPanel whole = parent with { SourceSha256 = png.Sha, PanelSha256 = png.Sha,
                PanelPngPath = png.Path, Width = parent.SourceWidth, Height = parent.SourceHeight };
            OcrImage image = new ProductionRasterFrameDecoder().Decode(
                CreateDetectionRequest(whole, png.Bytes), cancellationToken).CreateOcrImage();
            sources.Add(priorSha, (png.Sha, image));
        }
        if (!parents.Select(p => p.SourceSha256).Distinct().Order(StringComparer.Ordinal)
                .SequenceEqual(sources.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("Derived source inventory omits a historical case.");
        }
        var parentById = parents.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EvaluationPanel>();
        foreach (JsonElement record in request.GetProperty("panels").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireProperties(record, "panel_id", "png", "gray_sha256", "bgr_sha256");
            string id = Text(record, "panel_id");
            if (!seen.Add(id) || !parentById.TryGetValue(id, out EvaluationPanel? parent))
            {
                throw new InvalidDataException("Derived panel is duplicated or absent from the historical inventory.");
            }
            var png = ReadLayoutPng(record.GetProperty("png"), root);
            var source = sources[parent.SourceSha256];
            EvaluationPanel panel = parent with { SourceSha256 = source.Sha, PanelSha256 = png.Sha,
                PanelPngPath = png.Path, PanelPngSha256 = png.Sha, PanelPngByteCount = png.Bytes.Length,
                RecordedGraySha256 = RequireSha(Text(record, "gray_sha256"), "derived Gray8"),
                RecordedBgrSha256 = RequireSha(Text(record, "bgr_sha256"), "derived BGR24") };
            OcrImage cropped = new ProductionRasterFrameDecoder().Decode(
                CreateDetectionRequest(panel, png.Bytes), cancellationToken).CreateOcrImage();
            ValidateDerivedCrop(source.Image, cropped, panel.Crop);
            if (Hash(cropped.Pixels.Span) != panel.RecordedGraySha256 ||
                cropped.BgrPixels is not { } bgr || Hash(bgr.Pixels.Span) != panel.RecordedBgrSha256)
            {
                throw new InvalidDataException("Derived decoded pixel identity differs from its request.");
            }
            result.Add(panel);
        }
        return result.OrderBy(p => p.Split, StringComparer.Ordinal).ThenBy(p => p.PanelId, StringComparer.Ordinal).ToArray();
    }

    private static (string Path, string Sha, byte[] Bytes) ReadLayoutPng(JsonElement descriptor, string root)
    {
        RequireProperties(descriptor, "path", "sha256", "byte_count");
        string path = RepositoryPath(root, Text(descriptor, "path"));
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Derived raster must be a PNG.");
        }
        string sha = RequireSha(Text(descriptor, "sha256"), "derived PNG");
        return (path, sha, ReadVerified(path, sha, descriptor.GetProperty("byte_count").GetInt32(), "derived PNG"));
    }

    internal static void ValidateDerivedCrop(OcrImage source, OcrImage cropped, int[] box)
    {
        if (box.Length != 4 || box[0] < 0 || box[1] < 0 || box[2] != cropped.Width || box[3] != cropped.Height ||
            (long)box[0] + box[2] > source.Width || (long)box[1] + box[3] > source.Height ||
            source.BgrPixels is not { } sourceBgr || cropped.BgrPixels is not { } cropBgr)
        {
            throw new InvalidDataException("Derived crop has invalid bounds or lacks original color pixels.");
        }
        for (int y = 0; y < cropped.Height; y++)
        {
            if (!source.Pixels.Span.Slice((y + box[1]) * source.Stride + box[0], cropped.Width)
                    .SequenceEqual(cropped.Pixels.Span.Slice(y * cropped.Stride, cropped.Width)) ||
                !sourceBgr.Pixels.Span.Slice((y + box[1]) * sourceBgr.Stride + box[0] * 3, cropped.Width * 3)
                    .SequenceEqual(cropBgr.Pixels.Span.Slice(y * cropBgr.Stride, cropped.Width * 3)))
            {
                throw new InvalidDataException("Derived panel pixels are not the declared crop of its source.");
            }
        }
    }
}
