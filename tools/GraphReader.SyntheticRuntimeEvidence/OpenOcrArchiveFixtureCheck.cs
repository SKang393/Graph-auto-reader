// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Pdf;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Cross-language archive plumbing on explicitly open, newly generated development only.</summary>
internal static class OpenOcrArchiveFixtureCheck
{
    internal const string Command = "--check-open-ocr-archive-fixture";

    internal static object Run(string root, string requestPath, string expectedRequestSha256)
    {
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources = ReadFixture(root, requestPath, expectedRequestSha256);
        return new
        {
            schema = "graphreader.open-ocr-archive-fixture-result.v1", status = "passed", source_count = sources.Count,
            open_development_fixture = true, archive_format_uses_sealed_schema = true,
            registered_reserve = false, sealed_reads = 0, private_reads = 0,
            model_inference = false, production_approved = false,
        };
    }

    internal static async Task<object> RunImportAsync(string root, string requestPath, string expectedRequestSha256)
    {
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources = ReadFixture(root, requestPath, expectedRequestSha256);
        var failures = new List<object>();
        int successfulSources = 0, panelCount = 0;
        foreach (OriginalDbOcrSealedSourcePayload source in sources)
        {
            try
            {
                var image = new WorkflowInMemoryImageSource(source.ImageSha256, source.ImageBytes);
                Guid projectId = ProductionWorkflowPanelStore.CreateStableId("composed-ocr-project-v1", image.Sha256);
                Guid sourceId = ProductionWorkflowPanelStore.CreateStableId("composed-ocr-source-v1", image.Sha256);
                var request = new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, "source.png")
                {
                    InMemoryImageSource = image,
                };
                var store = new ProductionWorkflowPanelStore();
                var importer = new ProductionWorkflowImportStage(store, new ImageImportService());
                WorkflowImportSnapshot imported = await importer.ImportAsync(
                    new WorkflowImportRequest(projectId, [request], enhancementEnabled: false), CancellationToken.None)
                    .ConfigureAwait(false);
                panelCount += imported.Panels.Count;
                successfulSources++;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
            {
                // This command authenticates an explicitly open development fixture before import.
                // No registered reserve, private source, or inference stage enters this route.
                var page = new PdfPageSnapshot(1, source.Width / 2d, source.Height / 2d, [], [], []);
                PdfPanelizationResult proposals = await PanelizationEngine.CreateForStandaloneRasterSource().ProposeAsync(
                    new PdfPanelizationInput(source.ImageSha256, page,
                        new PdfRenderedPage(new ImmutableByteBuffer(source.ImageBytes), source.Width, source.Height),
                        new PdfPanelizationOptions(RenderDpi: 144)), CancellationToken.None).ConfigureAwait(false);
                failures.Add(new
                {
                    source_ordinal = source.Ordinal, error_type = error.GetType().Name, error.Message,
                    figures = proposals.Figures.Select(static figure => new { figure.BoundsPagePixels, figure.Confidence }),
                    panels = proposals.Panels.Select(static panel => panel.CropInSourcePixels),
                });
            }
        }
        return new
        {
            schema = "graphreader.open-ocr-import-fixture-result.v1", status = failures.Count == 0 ? "passed" : "failed",
            source_count = sources.Count, successful_sources = successfulSources, panel_count = panelCount,
            failures, open_development_fixture = true, registered_reserve = false, sealed_reads = 0,
            private_reads = 0, model_inference = false, production_approved = false,
        };
    }

    private static IReadOnlyList<OriginalDbOcrSealedSourcePayload> ReadFixture(
        string root, string requestPath, string expectedRequestSha256)
    {
        string path = Path.GetFullPath(requestPath, root);
        string boundary = Path.Combine(root, "artifacts", "goal22-runs") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OPEN_OCR_FIXTURE_PATH_INVALID");
        string directory = Path.GetDirectoryName(path)!;
        using JsonDocument requestDocument = JsonDocument.Parse(Read(path, expectedRequestSha256, 1024 * 1024));
        JsonElement request = requestDocument.RootElement;
        if (request.GetProperty("schema").GetString() != "graphreader.open-ocr-archive-fixture.v1" ||
            !request.GetProperty("open_development_fixture").GetBoolean() ||
            request.GetProperty("registered_reserve").GetBoolean())
            throw new InvalidDataException("OPEN_OCR_FIXTURE_SCOPE_INVALID");
        JsonElement generatorReference = request.GetProperty("generator");
        string generatorPath = LocalFile(generatorReference.GetProperty("path").GetString()!);
        using JsonDocument generatorDocument = JsonDocument.Parse(Read(generatorPath,
            generatorReference.GetProperty("sha256").GetString()!, 4 * 1024 * 1024));
        JsonElement generator = generatorDocument.RootElement;
        int count = generator.GetProperty("source_count").GetInt32();
        if (generator.GetProperty("schema").GetString() != "graphreader.owned-open-ocr-coverage-fixture.v1" ||
            generator.GetProperty("scope").GetString() != "owned-synthetic-development" ||
            generator.GetProperty("private_reads").GetInt32() != 0 ||
            generator.GetProperty("sealed_reads").GetInt32() != 0 ||
            generator.GetProperty("production_approved").GetBoolean() || count is <= 0 or > 128)
            throw new InvalidDataException("OPEN_OCR_FIXTURE_SCOPE_INVALID");
        var expected = new HashSet<(string Image, string Annotation)>();
        foreach (JsonElement source in generator.GetProperty("sources").EnumerateArray())
        {
            if (source.GetProperty("split").GetString() != "dev")
                throw new InvalidDataException("OPEN_OCR_FIXTURE_SPLIT_INVALID");
            string Bind(string name)
            {
                JsonElement reference = source.GetProperty(name);
                string digest = reference.GetProperty("sha256").GetString()!;
                _ = Read(LocalFile(reference.GetProperty("path").GetString()!), digest, 64 * 1024 * 1024);
                return digest;
            }
            if (!expected.Add((Bind("image"), Bind("annotation"))))
                throw new InvalidDataException("OPEN_OCR_FIXTURE_DUPLICATE");
        }
        if (expected.Count != count) throw new InvalidDataException("OPEN_OCR_FIXTURE_COUNT_INVALID");
        JsonElement archive = request.GetProperty("archive");
        byte[] archiveBytes = Read(LocalFile(archive.GetProperty("path").GetString()!),
            archive.GetProperty("sha256").GetString()!, 256 * 1024 * 1024);
        using var stream = new MemoryStream(archiveBytes, writable: false);
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources = OriginalDbOcrSealedArchive.Read(stream,
            archive.GetProperty("sha256").GetString()!, request.GetProperty("manifest_sha256").GetString()!,
            count, OriginalDbOcrSealedArchive.CoverageProtocolSha256, static _ => { }, CancellationToken.None);
        if (!expected.SetEquals(sources.Select(static source => (source.ImageSha256, source.AnnotationSha256))))
            throw new InvalidDataException("OPEN_OCR_FIXTURE_PAYLOAD_MISMATCH");
        return sources;

        string LocalFile(string relative)
        {
            string full = Path.GetFullPath(relative, root);
            if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OPEN_OCR_FIXTURE_PATH_INVALID");
            return full;
        }
    }

    private static byte[] Read(string path, string expected, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximum)
            throw new InvalidDataException("OPEN_OCR_FIXTURE_SIZE_INVALID");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
            throw new InvalidDataException("OPEN_OCR_FIXTURE_IDENTITY_INVALID");
        return bytes;
    }
}
