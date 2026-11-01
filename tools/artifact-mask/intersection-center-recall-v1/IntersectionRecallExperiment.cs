// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;

if (args.Length != 1)
    throw new ArgumentException("Pass one project-relative output path under artifacts/.");

string root = FindRoot();
string outputPath = Path.GetFullPath(Path.Combine(root, args[0]));
if (!outputPath.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Output must remain under project artifacts/.");

const int width = 200;
const int height = 150;
string protocolPath = Path.Combine(root, "tools", "artifact-mask", "intersection-center-recall-v1", "protocol.json");
using JsonDocument protocol = JsonDocument.Parse(File.ReadAllBytes(protocolPath));
JsonElement p = protocol.RootElement;
if (p.GetProperty("status").GetString() != "predeclared-diagnostic" ||
    p.GetProperty("scope").GetProperty("private_or_real_dev_inputs").GetInt32() != 0 ||
    p.GetProperty("scope").GetProperty("sealed_inputs").GetInt32() != 0 ||
    p.GetProperty("scope").GetProperty("training").GetBoolean())
    throw new InvalidDataException("Protocol scope is not the expected synthetic-only diagnostic.");

var provider = new RasterResidualArtifactMaskProvider();
var structural = new RasterPreOcrStructuralProbabilityProvider();
var cases = new List<object>();
var totals = new Totals();
foreach (JsonElement family in new[] { p.GetProperty("development"), p.GetProperty("validation") })
{
    string role = family.GetProperty("role").GetString()!;
    foreach (JsonElement spec in family.GetProperty("cases").EnumerateArray())
    {
        int nx = spec.GetProperty("width").GetInt32();
        int ny = spec.GetProperty("height").GetInt32();
        int spacing = spec.GetProperty("spacing").GetInt32();
        int startX = spec.GetProperty("start")[0].GetInt32();
        int startY = spec.GetProperty("start")[1].GetInt32();
        byte[] gray = Enumerable.Repeat(byte.MaxValue, width * height).ToArray();
        for (int i = 0; i < nx; i++) DrawLine(gray, startX + i * spacing, startY - spacing / 2, startX + i * spacing, startY + (ny - 1) * spacing + spacing / 2);
        for (int i = 0; i < ny; i++) DrawLine(gray, startX - spacing / 2, startY + i * spacing, startX + (nx - 1) * spacing + spacing / 2, startY + i * spacing);

        string hash = Hash(gray);
        var raster = new ProductionDecodedRaster(width, height, hash, WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity, OcrFrameTransform.Identity, width, height, gray,
            gray.Select(static value => 1f - (value / 255f)).ToArray());
        var geometry = new AxisGeometryResult(AxisGeometryCoordinateSpaces.OriginalPixels,
            new PlotPolygon(new PixelPoint(10, 140), new PixelPoint(110, 140), new PixelPoint(110, 10), new PixelPoint(10, 10)),
            new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 140), new PixelPoint(110, 140)), 0.98, 0, 1, ["x"]),
            new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 140), new PixelPoint(10, 10)), 0.98, 0, 1, ["y"]),
            [], [], [], 0.98, new AxisGeometryUncertainty(0, 0, 1, false, []),
            new AxisGeometryDiagnostics(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));
        var axis = new ProductionAxisGeometryEvidence(Envelope(hash), geometry);
        var ocr = new OcrResult(OcrContract.Version, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), OcrContract.Stage, "synthetic-grid-only", hash,
            OcrContract.CoordinateSpace, [], [], new OcrTiming(0, 0, 0, 0), 1, [],
            new OcrCacheDiagnostics(false, "intersection-recall-v1", 0, 0), null, []);
        var seed = new ProductionDetectionMaskSeed(width, height, hash, WorkflowImageVariant.Original,
            new float[width * height], new float[width * height]);

        RasterResidualArtifactMaskResult result = await provider.AnalyzeAsync(raster, axis, ocr, seed,
            CancellationToken.None, includeCategoryBits: true);
        PreOcrStructuralProbabilityResult probabilities = await structural.AnalyzeAsync(
            new PreOcrStructuralFrame(width, height, width, gray, width, new byte[width * height]), CancellationToken.None);
        float[] marker = probabilities.MarkerLikeProbabilities.ToArray();
        bool[] masked = result.Mask.ToArray().Select(static value => value >= 0.5f).ToArray();
        var centers = new List<object>();
        int markerAt = 0, markerFootprint = 0, centerMasked = 0;
        for (int gy = 0; gy < ny; gy++)
        for (int gx = 0; gx < nx; gx++)
        {
            int x = startX + gx * spacing, y = startY + gy * spacing, idx = y * width + x;
            bool hasMarker = false;
            for (int oy = -2; oy <= 2; oy++)
            for (int ox = -2; ox <= 2; ox++)
                hasMarker |= marker[(y + oy) * width + x + ox] >= 0.5f;
            if (marker[idx] >= 0.5f) markerAt++;
            if (hasMarker) markerFootprint++;
            if (masked[idx]) centerMasked++;
            centers.Add(new { marker_center_probability = marker[idx], marker_probability_in_mask_footprint = hasMarker, baseline_center_masked = masked[idx] });
        }
        int regionCount = result.Regions.Count(region => region.Category == RasterResidualArtifactCategory.ConnectingLineIntersection);
        var item = new {
            family_role = role,
            case_id = spec.GetProperty("id").GetString(),
            grid_intersections = nx * ny,
            baseline_intersection_regions = regionCount,
            centers_masked = centerMasked,
            centers_with_marker_probability = markerAt,
            centers_with_marker_probability_in_5x5_footprint = markerFootprint,
            center_diagnostics = centers
        };
        cases.Add(item);
        totals.Add(nx * ny, regionCount, centerMasked, markerAt, markerFootprint);
    }
}

object report = new {
    schema = "graphreader.artifact-mask-intersection-center-recall-result.v1",
    scope = new { synthetic_only = true, training = false, private_reads = 0, sealed_reads = 0, production_changes = false },
    protocol_sha256 = HashFile(protocolPath),
    source_sha256 = HashFile(Path.Combine(root, "tools", "artifact-mask", "intersection-center-recall-v1", "IntersectionRecallExperiment.cs")),
    project_sha256 = HashFile(Path.Combine(root, "tools", "artifact-mask", "intersection-center-recall-v1", "IntersectionRecallExperiment.csproj")),
    provider_source_sha256 = HashFile(Path.Combine(root, "src", "GraphReader.App", "Integration", "Workflow", "RasterResidualArtifactMaskProvider.cs")),
    structural_source_sha256 = HashFile(Path.Combine(root, "src", "GraphReader.Ocr", "RasterPreOcrStructuralProbabilityProvider.cs")),
    app_assembly_sha256 = HashFile(typeof(RasterResidualArtifactMaskProvider).Assembly.Location),
    ocr_assembly_sha256 = HashFile(typeof(RasterPreOcrStructuralProbabilityProvider).Assembly.Location),
    cases,
    aggregate = totals.ToObject(),
    model_inference = false,
    production_approval = false,
    note = "Per-center detail is synthetic-only. The diagnostic records the actual marker-like structural plane and actual provider output; it does not assert any synthetic grid cell is a true marker."
};
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(Path.GetRelativePath(root, outputPath));
Console.WriteLine(JsonSerializer.Serialize(totals.ToObject()));

static void DrawLine(byte[] pixels, int x1, int y1, int x2, int y2)
{
    int dx = Math.Abs(x2 - x1), sx = x1 < x2 ? 1 : -1;
    int dy = -Math.Abs(y2 - y1), sy = y1 < y2 ? 1 : -1, error = dx + dy;
    while (true)
    {
        pixels[y1 * 200 + x1] = 0;
        if (x1 == x2 && y1 == y2) break;
        int twice = 2 * error;
        if (twice >= dy) { error += dy; x1 += sx; }
        if (twice <= dx) { error += dx; y1 += sy; }
    }
}

static WorkflowVisionEnvelope Envelope(string hash) => new(1,
    Guid.Parse("30000000-0000-0000-0000-000000000071"), Guid.Parse("40000000-0000-0000-0000-000000000071"),
    Guid.Parse("50000000-0000-0000-0000-000000000071"), "axis", "intersection-recall-v1", hash,
    model: null, new WorkflowVisionTiming(0, 0, 0, 0), 0.98);
static string FindRoot()
{
    for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) return d.FullName;
    throw new DirectoryNotFoundException("Project root not found.");
}
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashFile(string path) => Hash(File.ReadAllBytes(path));

sealed class Totals
{
    public int GridIntersections { get; private set; }
    public int BaselineIntersectionRegions { get; private set; }
    public int CentersMasked { get; private set; }
    public int CentersWithMarkerProbability { get; private set; }
    public int CentersWithMarkerProbabilityIn5x5Footprint { get; private set; }
    public void Add(int count, int regions, int masked, int marker, int footprint)
    { GridIntersections += count; BaselineIntersectionRegions += regions; CentersMasked += masked; CentersWithMarkerProbability += marker; CentersWithMarkerProbabilityIn5x5Footprint += footprint; }
    public object ToObject() => new { GridIntersections, BaselineIntersectionRegions, CentersMasked, CentersWithMarkerProbability, CentersWithMarkerProbabilityIn5x5Footprint };
}
