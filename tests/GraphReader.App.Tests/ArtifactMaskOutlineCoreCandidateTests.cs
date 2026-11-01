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
public sealed class ArtifactMaskOutlineCoreCandidateTests
{
    private const int Width = 160;
    private const int Height = 110;

    [TestMethod]
    public async Task OutlineCoreCandidatePreservesHeldOutCircleCentersWithoutLosingCrossings()
    {
        string repositoryRoot = FindRepositoryRoot();
        string protocolPath = Path.Combine(repositoryRoot,
            "tools", "artifact-mask", "candidate-outline-core-v1", "protocol-v2.json");
        using JsonDocument protocol = JsonDocument.Parse(File.ReadAllBytes(protocolPath));
        JsonElement root = protocol.RootElement;
        Assert.AreEqual("graphreader.artifact-mask-outline-core-candidate.v2", root.GetProperty("schema").GetString());
        Assert.IsTrue(root.GetProperty("status").GetString() is
            "preregistered-before-validation-comparison" or "consumed-for-candidate-development");
        Assert.AreEqual(0, root.GetProperty("validation_split").GetProperty("train_sources").GetInt32());
        JsonElement validation = root.GetProperty("validation_split");
        Assert.AreEqual("artifact-mask-round-outline-heldout-v2", validation.GetProperty("dev_family").GetString());
        Assert.AreEqual(8, validation.GetProperty("dev_sources").GetInt32());
        Assert.IsFalse(root.GetProperty("scope").GetProperty("production_source_modified").GetBoolean());
        Assert.IsFalse(root.GetProperty("scope").GetProperty("production_gate_modified").GetBoolean());
        Assert.AreEqual(0, root.GetProperty("scope").GetProperty("sealed_reads").GetInt32());

        string providerPath = Path.Combine(repositoryRoot,
            "src", "GraphReader.App", "Integration", "Workflow", "RasterResidualArtifactMaskProvider.cs");
        string structuralPath = Path.Combine(repositoryRoot,
            "src", "GraphReader.Ocr", "RasterPreOcrStructuralProbabilityProvider.cs");
        string testPath = Path.Combine(repositoryRoot,
            "tests", "GraphReader.App.Tests", "ArtifactMaskOutlineCoreCandidateTests.cs");
        JsonElement baselineIdentity = root.GetProperty("baseline_identity");
        Assert.AreEqual(baselineIdentity.GetProperty("provider_source_sha256").GetString(), HashFile(providerPath));
        Assert.AreEqual(baselineIdentity.GetProperty("structural_provider_source_sha256").GetString(), HashFile(structuralPath));

        JsonElement candidateIdentity = root.GetProperty("candidate_identity");
        bool strictBinding = string.Equals(
            Environment.GetEnvironmentVariable("GRAPHREADER_ARTIFACT_MASK_STRICT_BINDING"), "1", StringComparison.Ordinal);
        if (strictBinding)
        {
            Assert.AreEqual(candidateIdentity.GetProperty("source_sha256").GetString(), HashFile(testPath));
            Assert.AreEqual(candidateIdentity.GetProperty("app_assembly_sha256").GetString(),
                HashFile(typeof(RasterResidualArtifactMaskProvider).Assembly.Location));
            Assert.AreEqual(candidateIdentity.GetProperty("ocr_assembly_sha256").GetString(),
                HashFile(typeof(RasterPreOcrStructuralProbabilityProvider).Assembly.Location));
            Assert.AreEqual(candidateIdentity.GetProperty("diagnostic_test_assembly_sha256").GetString(),
                HashFile(typeof(ArtifactMaskOutlineCoreCandidateTests).Assembly.Location));
        }

        var baseline = new RasterResidualArtifactMaskProvider();
        var candidate = new RasterResidualArtifactMaskProvider(new OutlineCoreCandidateProbabilityProvider());
        uint placement = unchecked((uint)validation.GetProperty("seed").GetInt32());
        var cases = new List<ObservedCase>();
        foreach (JsonElement caseSpec in validation.GetProperty("cases").EnumerateArray())
        {
            int centerX = 30 + Next(ref placement, 56);
            int centerY = 28 + Next(ref placement, 36);
            TestInput input = CreateInput(caseSpec, centerX, centerY);
            RasterResidualArtifactMaskResult before = await baseline.AnalyzeAsync(
                input.Raster, input.Axis, input.Ocr, input.Seed, CancellationToken.None);
            RasterResidualArtifactMaskResult after = await candidate.AnalyzeAsync(
                input.Raster, input.Axis, input.Ocr, input.Seed, CancellationToken.None);
            cases.Add(Observe(caseSpec, input, before, after));
        }

        Assert.AreEqual(validation.GetProperty("dev_sources").GetInt32(), cases.Count);
        int expectedMaskCenters = cases.Count(static item => item.ExpectedCenter is "mask" or "mask-crossing-center");
        int candidateFalseNegatives = cases.Count(static item =>
            (item.ExpectedCenter is "mask" or "mask-crossing-center") && !item.CandidateCrossingCenterMasked);
        int candidateMarkerSuppression = cases.Count(static item => item.HasMarker && item.CandidateMarkerCenterMasked);
        int candidateStandaloneRingRegions = cases.Single(static item => item.ExpectedCenter == "no-intersection-region")
            .CandidateIntersectionRegions;
        int candidateMaskedRingPixels = cases.Sum(static item => item.CandidateMaskedRingPixels);

        TestContext.WriteLine(JsonSerializer.Serialize(cases.Select(static item => new
        {
            item.SourceId,
            item.ExpectedCenter,
            item.BaselineCrossingCenterMasked,
            item.CandidateCrossingCenterMasked,
            item.BaselineMarkerCenterMasked,
            item.CandidateMarkerCenterMasked,
            item.BaselineIntersectionRegions,
            item.CandidateIntersectionRegions,
        })));

        Assert.AreEqual(4, expectedMaskCenters);
        Assert.AreEqual(0, candidateFalseNegatives,
            "The candidate must retain center masking for every predeclared clear or offset crossing.");
        Assert.AreEqual(0, candidateMarkerSuppression,
            "The candidate must leave every predeclared outlined-marker center available for review.");
        Assert.AreEqual(0, candidateStandaloneRingRegions,
            "A standalone outlined marker must not create a line-intersection artifact region.");
        Assert.AreEqual(0, candidateMaskedRingPixels,
            "The candidate must preserve all annotated outline ink in this family.");

        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "graphreader.artifact-mask-outline-core-comparison.v1",
            validation_cases = cases.Count,
            baseline_crossing_center_false_negatives = cases.Count(static item =>
                (item.ExpectedCenter is "mask" or "mask-crossing-center") && !item.BaselineCrossingCenterMasked),
            candidate_crossing_center_false_negatives = candidateFalseNegatives,
            baseline_marker_center_suppression = cases.Count(static item => item.HasMarker && item.BaselineMarkerCenterMasked),
            candidate_marker_center_suppression = candidateMarkerSuppression,
            baseline_standalone_ring_intersections = cases.Single(static item => item.ExpectedCenter == "no-intersection-region")
                .BaselineIntersectionRegions,
            candidate_standalone_ring_intersections = candidateStandaloneRingRegions,
            baseline_intersection_regions = cases.Sum(static item => item.BaselineIntersectionRegions),
            candidate_intersection_regions = cases.Sum(static item => item.CandidateIntersectionRegions),
            outline_truth_pixels = cases.Sum(static item => item.RingTruthPixels),
            baseline_masked_outline_truth_pixels = cases.Sum(static item => item.BaselineMaskedRingPixels),
            candidate_masked_outline_truth_pixels = candidateMaskedRingPixels,
            model_sha256 = (string?)null,
            model_inference = false,
            strict_identity_binding = strictBinding,
            production_approval = false,
        }));
    }

    public TestContext TestContext { get; set; } = null!;

    private static TestInput CreateInput(JsonElement caseSpec, int centerX, int centerY)
    {
        string sourceId = caseSpec.GetProperty("case_id").GetString()!;
        string topology = caseSpec.GetProperty("topology").GetString()!;
        int armLength = caseSpec.GetProperty("arm_length").GetInt32();
        string outline = caseSpec.GetProperty("outline").GetString()!;
        string expectedCenter = caseSpec.GetProperty("expected_center").GetString()!;
        var gray = Enumerable.Repeat(byte.MaxValue, Width * Height).ToArray();
        bool[] ringTruth = new bool[Width * Height];

        if (topology == "horizontal-and-vertical-crossing")
        {
            DrawLine(gray, centerX - armLength, centerY, centerX + armLength, centerY);
            DrawLine(gray, centerX, centerY - armLength, centerX, centerY + armLength);
        }
        else if (topology == "two-diagonal-crossing")
        {
            DrawLine(gray, centerX - armLength, centerY - armLength, centerX + armLength, centerY + armLength);
            DrawLine(gray, centerX - armLength, centerY + armLength, centerX + armLength, centerY - armLength);
        }
        else if (topology != "none")
        {
            throw new InvalidDataException("Protocol specifies an unsupported topology.");
        }

        int markerCenterX = centerX;
        int markerCenterY = centerY;
        int ringRadius = 0;
        if (outline.StartsWith("circle-radius-", StringComparison.Ordinal))
        {
            string[] parts = outline.Split('-', StringSplitOptions.RemoveEmptyEntries);
            ringRadius = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            if (outline.Contains("offset-12px-horizontal", StringComparison.Ordinal))
                markerCenterX += 12;
            DrawCircle(gray, ringTruth, markerCenterX, markerCenterY, ringRadius);
        }
        else if (outline != "none")
        {
            throw new InvalidDataException("Protocol specifies an unsupported outline.");
        }

        string sha256 = HashBytes(gray);
        var raster = new ProductionDecodedRaster(
            Width,
            Height,
            sha256,
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
        var axis = new ProductionAxisGeometryEvidence(Envelope(sha256, "axis", "oracle-plot-bounds-v1"), geometry);
        var seed = new ProductionDetectionMaskSeed(
            Width, Height, sha256, WorkflowImageVariant.Original,
            new float[Width * Height], new float[Width * Height]);
        var ocr = new OcrResult(
            OcrContract.Version,
            Guid.Parse("30000000-0000-0000-0000-000000000041").ToString("D"),
            Guid.Parse("40000000-0000-0000-0000-000000000041").ToString("D"),
            Guid.Parse("50000000-0000-0000-0000-000000000041").ToString("D"),
            OcrContract.Stage,
            "oracle-empty-ocr-v1",
            sha256,
            OcrContract.CoordinateSpace,
            [], [], new OcrTiming(0, 0, 0, 0), 1, [],
            new OcrCacheDiagnostics(false, "artifact-mask-outline-candidate-v1", 0, 0), null, []);
        return new TestInput(sourceId, expectedCenter, centerX, centerY, markerCenterX, markerCenterY, ringRadius,
            ringTruth, raster, axis, ocr, seed);
    }

    private static ObservedCase Observe(
        JsonElement caseSpec,
        TestInput input,
        RasterResidualArtifactMaskResult baseline,
        RasterResidualArtifactMaskResult candidate)
    {
        bool baselineCrossCenter = IsMasked(baseline, input.CenterX, input.CenterY);
        bool candidateCrossCenter = IsMasked(candidate, input.CenterX, input.CenterY);
        bool hasMarker = input.RingRadius > 0;
        bool baselineMarkerCenter = hasMarker && IsMasked(baseline, input.MarkerCenterX, input.MarkerCenterY);
        bool candidateMarkerCenter = hasMarker && IsMasked(candidate, input.MarkerCenterX, input.MarkerCenterY);
        int baselineRingOverlap = CountMaskedTruth(baseline, input.RingTruth);
        int candidateRingOverlap = CountMaskedTruth(candidate, input.RingTruth);
        return new ObservedCase(
            input.SourceId,
            input.ExpectedCenter,
            hasMarker,
            baselineCrossCenter,
            candidateCrossCenter,
            baselineMarkerCenter,
            candidateMarkerCenter,
            input.RingTruth.Count(static pixel => pixel),
            baselineRingOverlap,
            candidateRingOverlap,
            baseline.Regions.Count(static region => region.Category == RasterResidualArtifactCategory.ConnectingLineIntersection),
            candidate.Regions.Count(static region => region.Category == RasterResidualArtifactCategory.ConnectingLineIntersection));
    }

    private static bool IsMasked(RasterResidualArtifactMaskResult result, int x, int y) =>
        result.Mask.Span[(y * Width) + x] >= 0.5f;

    private static int CountMaskedTruth(RasterResidualArtifactMaskResult result, bool[] truth)
    {
        int count = 0;
        for (int index = 0; index < truth.Length; index++)
            if (truth[index] && result.Mask.Span[index] >= 0.5f)
                count++;
        return count;
    }

    private static void DrawCircle(byte[] gray, bool[] truth, int centerX, int centerY, int radius)
    {
        int x = radius;
        int y = 0;
        int error = 1 - radius;
        while (x >= y)
        {
            Mark(centerX + x, centerY + y);
            Mark(centerX + y, centerY + x);
            Mark(centerX - y, centerY + x);
            Mark(centerX - x, centerY + y);
            Mark(centerX - x, centerY - y);
            Mark(centerX - y, centerY - x);
            Mark(centerX + y, centerY - x);
            Mark(centerX + x, centerY - y);
            y++;
            if (error < 0)
            {
                error += (2 * y) + 1;
            }
            else
            {
                x--;
                error += (2 * (y - x)) + 1;
            }
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

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "tools", "artifact-mask", "candidate-outline-core-v1", "protocol-v2.json")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the preregistered outline-core candidate protocol.");
    }

    private static WorkflowVisionEnvelope Envelope(string inputSha256, string stage, string version) => new(
        1,
        Guid.Parse("30000000-0000-0000-0000-000000000041"),
        Guid.Parse("40000000-0000-0000-0000-000000000041"),
        Guid.Parse("50000000-0000-0000-0000-000000000041"),
        stage,
        version,
        inputSha256,
        model: null,
        new WorkflowVisionTiming(0, 0, 0, 0),
        0.98);

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record TestInput(
        string SourceId,
        string ExpectedCenter,
        int CenterX,
        int CenterY,
        int MarkerCenterX,
        int MarkerCenterY,
        int RingRadius,
        bool[] RingTruth,
        ProductionDecodedRaster Raster,
        ProductionAxisGeometryEvidence Axis,
        OcrResult Ocr,
        ProductionDetectionMaskSeed Seed);

    private sealed record ObservedCase(
        string SourceId,
        string ExpectedCenter,
        bool HasMarker,
        bool BaselineCrossingCenterMasked,
        bool CandidateCrossingCenterMasked,
        bool BaselineMarkerCenterMasked,
        bool CandidateMarkerCenterMasked,
        int RingTruthPixels,
        int BaselineMaskedRingPixels,
        int CandidateMaskedRingPixels,
        int BaselineIntersectionRegions,
        int CandidateIntersectionRegions);

    private sealed class OutlineCoreCandidateProbabilityProvider : IPreOcrStructuralProbabilityProvider
    {
        private const byte ForegroundMaximum = 196;
        private const byte GeometryMaskMinimum = 128;
        private const int MinimumRay = 5;
        private const int MaximumRay = 64;
        private const int ConflictRadius = 2;
        private const int RequiredAnnularSectors = 15;
        private static readonly RasterPreOcrStructuralProbabilityProvider BaseProvider = new();
        private static readonly (int X, int Y)[] Directions =
        [
            (1, 0), (0, 1), (1, 1), (1, -1),
        ];

        public async ValueTask<PreOcrStructuralProbabilityResult> AnalyzeAsync(
            PreOcrStructuralFrame frame,
            CancellationToken cancellationToken)
        {
            PreOcrStructuralProbabilityResult baseResult = await BaseProvider.AnalyzeAsync(
                frame, cancellationToken).ConfigureAwait(false);
            float[] marker = baseResult.MarkerLikeProbabilities.ToArray();
            float[] connector = baseResult.ThinConnectorProbabilities.ToArray();
            byte[] gray = frame.Gray8Pixels.ToArray();
            byte[] geometry = frame.GeometryMask.ToArray();
            for (int y = 10; y < frame.Height - 10; y++)
            for (int x = 10; x < frame.Width - 10; x++)
            {
                if ((x & 31) == 0 && (y & 31) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!HasLongCrossing(frame, gray, geometry, x, y))
                    continue;
                bool annularSupport = false;
                for (int radius = 4; radius <= 9 && !annularSupport; radius++)
                    annularSupport = HasCircularAnnularSupport(frame, gray, geometry, x, y, radius);
                if (!annularSupport)
                    continue;

                for (int offsetY = -ConflictRadius; offsetY <= ConflictRadius; offsetY++)
                for (int offsetX = -ConflictRadius; offsetX <= ConflictRadius; offsetX++)
                {
                    int xx = x + offsetX;
                    int yy = y + offsetY;
                    int index = (yy * frame.Width) + xx;
                    if (gray[index] <= ForegroundMaximum && geometry[index] < GeometryMaskMinimum)
                        marker[index] = Math.Max(marker[index], 0.90f);
                }
            }

            return new PreOcrStructuralProbabilityResult(
                baseResult.Width,
                baseResult.Height,
                baseResult.Stride,
                marker,
                connector,
                baseResult.CoordinateSpace);
        }

        private static bool HasLongCrossing(
            PreOcrStructuralFrame frame,
            byte[] gray,
            byte[] geometry,
            int centerX,
            int centerY)
        {
            int extendedPairs = 0;
            foreach ((int dx, int dy) in Directions)
            {
                int forward = Ray(gray, geometry, frame.Width, frame.Height, centerX, centerY, dx, dy);
                int backward = Ray(gray, geometry, frame.Width, frame.Height, centerX, centerY, -dx, -dy);
                if (forward >= MinimumRay && backward >= MinimumRay && forward + backward + 1 > 13)
                    extendedPairs++;
            }
            return extendedPairs >= 2;
        }

        private static int Ray(
            byte[] gray,
            byte[] geometry,
            int width,
            int height,
            int centerX,
            int centerY,
            int dx,
            int dy)
        {
            int length = 0;
            for (int x = centerX + dx, y = centerY + dy;
                 length < MaximumRay && x >= 0 && x < width && y >= 0 && y < height;
                 x += dx, y += dy)
            {
                int index = (y * width) + x;
                if (gray[index] > ForegroundMaximum || geometry[index] >= GeometryMaskMinimum)
                    break;
                length++;
            }
            return length;
        }

        private static bool HasCircularAnnularSupport(
            PreOcrStructuralFrame frame,
            byte[] gray,
            byte[] geometry,
            int centerX,
            int centerY,
            int radius)
        {
            int support = 0;
            const int sectors = 16;
            for (int sector = 0; sector < sectors; sector++)
            {
                double angle = (Math.PI * 2 * sector) / sectors;
                int targetX = centerX + (int)Math.Round(radius * Math.Cos(angle), MidpointRounding.AwayFromZero);
                int targetY = centerY + (int)Math.Round(radius * Math.Sin(angle), MidpointRounding.AwayFromZero);
                bool found = false;
                for (int offsetY = -1; offsetY <= 1 && !found; offsetY++)
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int x = targetX + offsetX;
                    int y = targetY + offsetY;
                    if (x < 0 || x >= frame.Width || y < 0 || y >= frame.Height)
                        continue;
                    int index = (y * frame.Width) + x;
                    if (gray[index] <= ForegroundMaximum && geometry[index] < GeometryMaskMinimum)
                    {
                        found = true;
                        break;
                    }
                }
                if (found)
                    support++;
            }
            return support >= RequiredAnnularSectors;
        }
    }
}
