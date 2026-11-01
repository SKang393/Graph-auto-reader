// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ArtifactMaskReadinessFamilyTests
{
    private const int Width = 160;
    private const int Height = 110;

    [TestMethod]
    public async Task PreregisteredSyntheticConflictFamilyReportsOnlyExpectedComponentBehavior()
    {
        string repositoryRoot = FindRepositoryRoot();
        string protocolPath = Path.Combine(repositoryRoot, "tools", "artifact-mask", "readiness-family-v1.json");
        using JsonDocument protocol = JsonDocument.Parse(File.ReadAllBytes(protocolPath));
        JsonElement root = protocol.RootElement;
        Assert.AreEqual("graphreader.artifact-mask-readiness-family.v1", root.GetProperty("schema").GetString());
        Assert.AreEqual("preregistered-before-component-diagnostic", root.GetProperty("status").GetString());
        Assert.AreEqual(0, root.GetProperty("split").GetProperty("train").GetProperty("source_count").GetInt32());
        JsonElement dev = root.GetProperty("split").GetProperty("dev");
        Assert.AreEqual("artifact-mask-crossing-conflict-v1", dev.GetProperty("family").GetString());
        Assert.AreEqual(6, dev.GetProperty("source_count").GetInt32());
        int seed = dev.GetProperty("seed").GetInt32();
        bool strictIdentityBinding = string.Equals(
            Environment.GetEnvironmentVariable("GRAPHREADER_ARTIFACT_MASK_STRICT_BINDING"), "1", StringComparison.Ordinal);
        Assert.IsFalse(dev.GetProperty("uses_existing_reserved_test_family").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("scope").GetProperty("sealed_reads").GetInt32());
        Assert.IsFalse(root.GetProperty("scope").GetProperty("training_performed").GetBoolean());
        Assert.IsFalse(root.GetProperty("scope").GetProperty("model_inference_performed").GetBoolean());
        Assert.AreEqual("none; provider and morphology implementation are deterministic code, not model inference",
            root.GetProperty("provider_identity").GetProperty("model_runtime").GetString());

        string providerPath = Path.Combine(repositoryRoot,
            "src", "GraphReader.App", "Integration", "Workflow", "RasterResidualArtifactMaskProvider.cs");
        string structuralPath = Path.Combine(repositoryRoot,
            "src", "GraphReader.Ocr", "RasterPreOcrStructuralProbabilityProvider.cs");
        string adapterPath = Path.Combine(repositoryRoot,
            "src", "GraphReader.App", "Integration", "Workflow", "RasterResidualArtifactMaskAdapter.cs");
        string diagnosticTestPath = Path.Combine(repositoryRoot,
            "tests", "GraphReader.App.Tests", "ArtifactMaskReadinessFamilyTests.cs");
        JsonElement identity = root.GetProperty("provider_identity");
        Assert.AreEqual(
            expected: RasterResidualArtifactMaskProvider.CompositionVersion,
            actual: identity.GetProperty("composition_version").GetString());
        Assert.AreEqual(
            expected: RasterResidualArtifactMaskProvider.ConfigurationFingerprint,
            actual: identity.GetProperty("configuration_fingerprint").GetString());
        if (strictIdentityBinding)
        {
            Assert.AreEqual(identity.GetProperty("provider_source_sha256").GetString(), HashFile(providerPath));
            Assert.AreEqual(identity.GetProperty("structural_provider_source_sha256").GetString(), HashFile(structuralPath));
            Assert.AreEqual(identity.GetProperty("adapter_source_sha256").GetString(), HashFile(adapterPath));
            Assert.AreEqual(identity.GetProperty("diagnostic_test_source_sha256").GetString(), HashFile(diagnosticTestPath));
            Assert.AreEqual(
                expected: identity.GetProperty("app_assembly_sha256").GetString(),
                actual: HashFile(typeof(RasterResidualArtifactMaskProvider).Assembly.Location));
            Assert.AreEqual(
                expected: identity.GetProperty("ocr_assembly_sha256").GetString(),
                actual: HashFile(typeof(RasterPreOcrStructuralProbabilityProvider).Assembly.Location));
            Assert.AreEqual(
                expected: identity.GetProperty("diagnostic_test_assembly_sha256").GetString(),
                actual: HashFile(typeof(ArtifactMaskReadinessFamilyTests).Assembly.Location));
        }

        var observed = new List<ObservedCase>();
        uint placement = unchecked((uint)seed);
        foreach (JsonElement caseSpec in dev.GetProperty("cases").EnumerateArray())
        {
            observed.Add(await AnalyzeAsync(caseSpec,
                30 + Next(ref placement, 56),
                28 + Next(ref placement, 36)));
        }

        Assert.AreEqual(dev.GetProperty("source_count").GetInt32(), observed.Count);
        Assert.AreEqual(observed.Count, observed.Select(static item => item.SourceId).Distinct().Count());
        Assert.AreEqual(2, observed.Count(static item => item.Expectation == "mask_center"));
        Assert.AreEqual(3, observed.Count(static item =>
            (item.Expectation is "review_center_as_ambiguous" or "preserve_marker_center")));
        Assert.AreEqual(1, observed.Count(static item => item.Expectation == "measure_only_no_acceptance_threshold"));

        Assert.AreEqual(2, observed.Count(static item => item.Expectation == "mask_center" && item.CenterMasked));
        Assert.AreEqual(3, observed.Count(static item =>
            (item.Expectation is "review_center_as_ambiguous" or "preserve_marker_center") && !item.CenterMasked));

        int maskedMarkerPixels = observed.Sum(static item => item.MaskedMarkerPixels);
        int markerPixels = observed.Sum(static item => item.MarkerPixels);
        int intersectionRegions = observed.Sum(static item => item.IntersectionRegions);
        Assert.AreEqual(0, maskedMarkerPixels,
            "The predeclared diagnostic family must not mask any annotated marker ink.");
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "graphreader.artifact-mask-readiness-family-result.v1",
            source_count = observed.Count,
            clear_cross_centers_masked = observed.Count(static item =>
                item.Expectation == "mask_center" && item.CenterMasked),
            dense_or_filled_conflict_centers_preserved = observed.Count(static item =>
                (item.Expectation is "review_center_as_ambiguous" or "preserve_marker_center") && !item.CenterMasked),
            outlined_boundary_center_masked = observed.Single(static item =>
                item.Expectation == "measure_only_no_acceptance_threshold").CenterMasked,
            intersection_regions = intersectionRegions,
            marker_truth_pixels = markerPixels,
            masked_marker_truth_pixels = maskedMarkerPixels,
            model_sha256 = (string?)null,
            model_inference = false,
            strict_identity_binding = strictIdentityBinding,
            production_approval = false,
        }));
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<ObservedCase> AnalyzeAsync(JsonElement caseSpec, int centerX, int centerY)
    {
        string sourceId = caseSpec.GetProperty("case_id").GetString()!;
        string topology = caseSpec.GetProperty("topology").GetString()!;
        int armLength = caseSpec.GetProperty("arm_length").GetInt32();
        string marker = caseSpec.GetProperty("marker").GetString()!;
        string expectation = caseSpec.GetProperty("expectation").GetString()!;
        if (!CaseIds.Contains(sourceId, StringComparer.Ordinal))
            throw new InvalidDataException("Protocol includes an unknown generated case.");
        var gray = Enumerable.Repeat(byte.MaxValue, Width * Height).ToArray();
        bool[] markerTruth = new bool[Width * Height];

        if (topology == "horizontal-and-vertical-crossing")
        {
            DrawLine(gray, centerX - armLength, centerY, centerX + armLength, centerY);
            DrawLine(gray, centerX, centerY - armLength, centerX, centerY + armLength);
        }
        else if (topology == "two-diagonal-crossing" ||
                 topology == "two-diagonal-crossing-with-third-connector")
        {
            DrawLine(gray, centerX - armLength, centerY - armLength, centerX + armLength, centerY + armLength);
            DrawLine(gray, centerX - armLength, centerY + armLength, centerX + armLength, centerY - armLength);
            if (topology == "two-diagonal-crossing-with-third-connector")
                DrawLine(gray, centerX - armLength, centerY, centerX + armLength, centerY);
        }
        else
        {
            throw new InvalidDataException("Protocol includes an unsupported crossing topology.");
        }

        if (marker == "filled-square-radius-3-at-center")
            DrawFilledSquare(gray, markerTruth, centerX, centerY, radius: 3);
        else if (marker == "filled-square-radius-3-offset-two-pixels")
            DrawFilledSquare(gray, markerTruth, centerX + 2, centerY, radius: 3);
        else if (marker == "outlined-square-radius-6")
            DrawOutlinedSquare(gray, markerTruth, centerX, centerY, radius: 6);
        else if (marker != "none")
            throw new InvalidDataException("Protocol includes an unsupported marker geometry.");

        string sourceSha256 = HashBytes(gray);
        var raster = new ProductionDecodedRaster(
            Width,
            Height,
            sourceSha256,
            WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            Width,
            Height,
            gray,
            gray.Select(static value => 1f - (value / 255f)).ToArray());
        var geometry = new AxisGeometryResult(
            AxisGeometryCoordinateSpaces.OriginalPixels,
            new PlotPolygon(new PixelPoint(10, 95), new PixelPoint(105, 95),
                new PixelPoint(105, 10), new PixelPoint(10, 10)),
            new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 95), new PixelPoint(105, 95)), 0.98, 0, 1, ["x"]),
            new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 95), new PixelPoint(10, 10)), 0.98, 0, 1, ["y"]),
            [], [], [], 0.98,
            new AxisGeometryUncertainty(0, 0, 1, false, []),
            new AxisGeometryDiagnostics(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));
        var axis = new ProductionAxisGeometryEvidence(Envelope(sourceSha256, "axis", "oracle-plot-bounds-v1"), geometry);
        var seed = new ProductionDetectionMaskSeed(
            Width,
            Height,
            sourceSha256,
            WorkflowImageVariant.Original,
            new float[Width * Height],
            new float[Width * Height]);
        var ocr = new OcrResult(
            OcrContract.Version,
            Guid.Parse("30000000-0000-0000-0000-000000000031").ToString("D"),
            Guid.Parse("40000000-0000-0000-0000-000000000031").ToString("D"),
            Guid.Parse("50000000-0000-0000-0000-000000000031").ToString("D"),
            OcrContract.Stage,
            "oracle-empty-ocr-v1",
            sourceSha256,
            OcrContract.CoordinateSpace,
            [], [], new OcrTiming(0, 0, 0, 0), 1, [],
            new OcrCacheDiagnostics(false, "artifact-mask-readiness-v1", 0, 0), null, []);

        RasterResidualArtifactMaskResult result = await new RasterResidualArtifactMaskProvider().AnalyzeAsync(
            raster, axis, ocr, seed, CancellationToken.None);
        bool centerMasked = result.Mask.Span[(centerY * Width) + centerX] >= 0.5f;
        int markerPixels = 0;
        int maskedMarkerPixels = 0;
        for (int index = 0; index < markerTruth.Length; index++)
        {
            if (!markerTruth[index])
                continue;
            markerPixels++;
            if (result.Mask.Span[index] >= 0.5f)
                maskedMarkerPixels++;
        }

        return new ObservedCase(
            sourceId,
            expectation,
            centerMasked,
            markerPixels,
            maskedMarkerPixels,
            result.Regions.Count(static region => region.Category == RasterResidualArtifactCategory.ConnectingLineIntersection));
    }

    private static string[] CaseIds =>
    [
        "clear-plus-short",
        "clear-x-short",
        "connected-dense-crossing",
        "filled-centered-core",
        "filled-offset-core",
        "outlined-core-boundary",
    ];

    private static void DrawFilledSquare(byte[] gray, bool[] truth, int centerX, int centerY, int radius)
    {
        for (int y = centerY - radius; y <= centerY + radius; y++)
        for (int x = centerX - radius; x <= centerX + radius; x++)
        {
            int index = (y * Width) + x;
            gray[index] = 0;
            truth[index] = true;
        }
    }

    private static void DrawOutlinedSquare(byte[] gray, bool[] truth, int centerX, int centerY, int radius)
    {
        for (int offset = -radius; offset <= radius; offset++)
        {
            Mark(centerX + offset, centerY - radius);
            Mark(centerX + offset, centerY + radius);
            Mark(centerX - radius, centerY + offset);
            Mark(centerX + radius, centerY + offset);
        }

        void Mark(int x, int y)
        {
            int index = (y * Width) + x;
            gray[index] = 0;
            truth[index] = true;
        }
    }

    private static void DrawLine(byte[] gray, int x1, int y1, int x2, int y2)
    {
        int deltaX = Math.Abs(x2 - x1);
        int stepX = x1 < x2 ? 1 : -1;
        int deltaY = -Math.Abs(y2 - y1);
        int stepY = y1 < y2 ? 1 : -1;
        int error = deltaX + deltaY;
        while (true)
        {
            gray[(y1 * Width) + x1] = 0;
            if (x1 == x2 && y1 == y2)
                return;
            int doubledError = 2 * error;
            if (doubledError >= deltaY)
            {
                error += deltaY;
                x1 += stepX;
            }
            if (doubledError <= deltaX)
            {
                error += deltaX;
                y1 += stepY;
            }
        }
    }

    private static int Next(ref uint state, int exclusiveMaximum)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (int)(state % (uint)exclusiveMaximum);
    }

    private static WorkflowVisionEnvelope Envelope(string inputSha256, string stage, string version) => new(
        1,
        Guid.Parse("30000000-0000-0000-0000-000000000031"),
        Guid.Parse("40000000-0000-0000-0000-000000000031"),
        Guid.Parse("50000000-0000-0000-0000-000000000031"),
        stage,
        version,
        inputSha256,
        model: null,
        new WorkflowVisionTiming(0, 0, 0, 0),
        0.98);

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "artifact-mask", "readiness-family-v1.json")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the preregistered artifact-mask diagnostic protocol.");
    }

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record ObservedCase(
        string SourceId,
        string Expectation,
        bool CenterMasked,
        int MarkerPixels,
        int MaskedMarkerPixels,
        int IntersectionRegions);
}
