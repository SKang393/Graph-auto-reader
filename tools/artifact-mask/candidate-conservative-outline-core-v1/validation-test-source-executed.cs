// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
public sealed class ArtifactMaskConservativeOutlineCoreV1CandidateTests
{
    private const int Width = 200;
    private const int Height = 150;
    private const string DevelopmentProtocolRelativePath = "tools/artifact-mask/candidate-conservative-outline-core-v1/development-protocol.json";
    private const string ValidationProtocolRelativePath = "tools/artifact-mask/candidate-conservative-outline-core-v1/validation-protocol.json";
    private static readonly RasterResidualArtifactCategory[] Categories = Enum.GetValues<RasterResidualArtifactCategory>();

    [TestMethod]
    public Task DevelopmentFamilyComparesMarkerAndProhibitedStructureEvidence() =>
        RunFamilyAsync(DevelopmentProtocolRelativePath, "development");

    [TestMethod]
    public Task UnseenValidationFamilyComparesMarkerAndProhibitedStructureEvidence() =>
        RunFamilyAsync(ValidationProtocolRelativePath, "validation");

    private async Task RunFamilyAsync(string protocolRelativePath, string familyRole)
    {
        string root = FindRepositoryRoot();
        string protocolPath = Path.Combine(root, protocolRelativePath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(protocolPath));
        JsonElement protocol = document.RootElement;
        Assert.AreEqual("graphreader.artifact-mask-conservative-outline-core.v1", protocol.GetProperty("schema").GetString());
        Assert.AreEqual(familyRole, protocol.GetProperty("family_role").GetString());
        string status = protocol.GetProperty("status").GetString()!;
        bool familyConsumed = protocol.GetProperty("family_consumed").GetBoolean();
        if (familyRole == "development")
        {
            Assert.IsTrue(status is "preregistered-before-comparison" or "consumed-for-candidate-development");
            Assert.IsTrue(familyConsumed == (status == "consumed-for-candidate-development"));
        }
        else
        {
            Assert.AreEqual("preregistered-before-validation", status);
            Assert.IsFalse(familyConsumed);
        }
        int sourceCount = protocol.GetProperty("split").GetProperty("source_count").GetInt32();

        string candidateSource = Path.Combine(root, "tests", "GraphReader.App.Tests", "ArtifactMaskConservativeOutlineCoreV1CandidateTests.cs");
        string providerSource = Path.Combine(root,
            "src", "GraphReader.App", "Integration", "Workflow", "RasterResidualArtifactMaskProvider.cs");
        string structuralSource = Path.Combine(root,
            "src", "GraphReader.Ocr", "RasterPreOcrStructuralProbabilityProvider.cs");
        JsonElement candidateIdentity = protocol.GetProperty("candidate_identity");
        JsonElement baselineIdentity = protocol.GetProperty("baseline_identity");
        Assert.AreEqual(candidateIdentity.GetProperty("source_sha256").GetString(), HashFile(candidateSource));
        Assert.AreEqual(candidateIdentity.GetProperty("app_assembly_sha256").GetString(),
            HashFile(typeof(RasterResidualArtifactMaskProvider).Assembly.Location));
        Assert.AreEqual(candidateIdentity.GetProperty("ocr_assembly_sha256").GetString(),
            HashFile(typeof(RasterPreOcrStructuralProbabilityProvider).Assembly.Location));
        Assert.AreEqual(candidateIdentity.GetProperty("test_source_sha256").GetString(), HashFile(candidateSource));
        Assert.AreEqual(candidateIdentity.GetProperty("test_assembly_sha256").GetString(),
            HashFile(typeof(ArtifactMaskConservativeOutlineCoreV1CandidateTests).Assembly.Location));
        Assert.AreEqual(baselineIdentity.GetProperty("provider_source_sha256").GetString(), HashFile(providerSource));
        Assert.AreEqual(baselineIdentity.GetProperty("structural_provider_source_sha256").GetString(), HashFile(structuralSource));

        IPreOcrStructuralProbabilityProvider candidateStructuralProvider = CreateFrozenCandidateProvider();
        var baselineProvider = new RasterResidualArtifactMaskProvider();
        var candidateProvider = new RasterResidualArtifactMaskProvider(candidateStructuralProvider);
        List<CaseInput> inputs = protocol.GetProperty("split").GetProperty("cases")
            .EnumerateArray().Select(CreateInput).ToList();
        Assert.AreEqual(sourceCount, inputs.Count);

        // Warm both implementations on a separate synthetic input before timing the frozen cases.
        CaseInput warmup = CreateWarmupInput();
        _ = await baselineProvider.AnalyzeAsync(warmup.Raster, warmup.Axis, warmup.Ocr, warmup.Seed,
            CancellationToken.None, includeCategoryBits: true);
        _ = await candidateProvider.AnalyzeAsync(warmup.Raster, warmup.Axis, warmup.Ocr, warmup.Seed,
            CancellationToken.None, includeCategoryBits: true);

        var observed = new List<CaseObservation>(inputs.Count);
        foreach (CaseInput input in inputs)
        {
            var timer = Stopwatch.StartNew();
            RasterResidualArtifactMaskResult baseline = await baselineProvider.AnalyzeAsync(
                input.Raster, input.Axis, input.Ocr, input.Seed, CancellationToken.None, includeCategoryBits: true);
            timer.Stop();
            double baselineMilliseconds = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            RasterResidualArtifactMaskResult candidate = await candidateProvider.AnalyzeAsync(
                input.Raster, input.Axis, input.Ocr, input.Seed, CancellationToken.None, includeCategoryBits: true);
            timer.Stop();

            observed.Add(Observe(input, baseline, candidate, baselineMilliseconds, timer.Elapsed.TotalMilliseconds));
        }

        AggregateObservation aggregate = Aggregate(observed);
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "graphreader.artifact-mask-conservative-outline-core-result.v1",
            family_role = familyRole,
            cases = observed,
            aggregate,
            strict_identity_binding = true,
            model_sha256 = (string?)null,
            model_inference = false,
            production_approval = false,
        }));

        Assert.IsTrue(aggregate.CandidateSuppressedMarkerCenters < aggregate.BaselineSuppressedMarkerCenters,
            "Candidate must preserve more annotated marker centers than baseline.");
        Assert.IsLessThanOrEqualTo(aggregate.BaselineSuppressedMarkerCenters, aggregate.CandidateSuppressedMarkerCenters,
            "Candidate must not suppress additional marker centers.");
        Assert.IsLessThanOrEqualTo(aggregate.BaselineMaskedMarkerPixels, aggregate.CandidateMaskedMarkerPixels,
            "Candidate must not mask additional annotated marker pixels.");
        Assert.IsLessThanOrEqualTo(aggregate.BaselineMaskedClutterPixels, aggregate.CandidateMaskedClutterPixels,
            "Candidate must not mask additional annotated text-like clutter pixels.");
        Assert.IsLessThanOrEqualTo(aggregate.BaselineUnmaskedClearCrossingCenters,
            aggregate.CandidateUnmaskedClearCrossingCenters,
            "Candidate must not lose any baseline clear-cross or grid center mask.");
        Assert.IsGreaterThanOrEqualTo(aggregate.BaselineMaskedArrowPixels, aggregate.CandidateMaskedArrowPixels);
        Assert.IsGreaterThanOrEqualTo(aggregate.BaselineMaskedBracketPixels, aggregate.CandidateMaskedBracketPixels);
        Assert.IsGreaterThanOrEqualTo(aggregate.BaselineMaskedLegendPixels, aggregate.CandidateMaskedLegendPixels);
        foreach (RasterResidualArtifactCategory category in new[]
                     { RasterResidualArtifactCategory.AnnotationArrow, RasterResidualArtifactCategory.Bracket, RasterResidualArtifactCategory.LegendStructure })
        {
            Assert.AreEqual(aggregate.BaselineRegionCounts[category], aggregate.CandidateRegionCounts[category],
                $"Candidate changed non-intersection category count {category}.");
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static ConservativeOutlineCoreProbabilityProvider CreateFrozenCandidateProvider()
        => new ConservativeOutlineCoreProbabilityProvider();

    private static CaseInput CreateInput(JsonElement spec)
    {
        string id = spec.GetProperty("id").GetString()!;
        string type = spec.GetProperty("type").GetString()!;
        var gray = Enumerable.Repeat(byte.MaxValue, Width * Height).ToArray();
        bool[] markerTruth = new bool[gray.Length];
        bool[] crossTruth = new bool[gray.Length];
        bool[] arrowTruth = new bool[gray.Length];
        bool[] bracketTruth = new bool[gray.Length];
        bool[] legendTruth = new bool[gray.Length];
        bool[] clutterTruth = new bool[gray.Length];
        var markerCenters = new List<Point>();
        var crossingCenters = new List<Point>();

        if (type is "ordinary-cross" or "outlined-circle-over-cross" or "outlined-square-over-cross" or "filled-marker-over-cross" or "text-clutter-cross" or "circle-text-clutter-over-cross" or "square-text-clutter-over-cross")
        {
            Point center = ReadPoint(spec.GetProperty("center"));
            int arms = spec.GetProperty("arms").GetInt32();
            DrawCross(gray, crossTruth, center, arms, spec.TryGetProperty("topology", out JsonElement topology)
                ? topology.GetString()! : "plus");
            crossingCenters.Add(center);

            if (type is "outlined-circle-over-cross" or "circle-text-clutter-over-cross")
            {
                int radius = spec.GetProperty("radius").GetInt32();
                DrawCircle(gray, markerTruth, center, radius, filled: false);
                markerCenters.Add(center);
            }
            else if (type is "outlined-square-over-cross" or "square-text-clutter-over-cross")
            {
                int radius = spec.GetProperty("radius").GetInt32();
                DrawSquare(gray, markerTruth, center, radius, filled: false);
                markerCenters.Add(center);
            }
            else if (type == "filled-marker-over-cross")
            {
                int radius = spec.GetProperty("radius").GetInt32();
                bool square = spec.GetProperty("shape").GetString() == "square";
                if (square)
                    DrawSquare(gray, markerTruth, center, radius, filled: true);
                else
                    DrawCircle(gray, markerTruth, center, radius, filled: true);
                markerCenters.Add(center);
            }

            if (type is "text-clutter-cross" or "circle-text-clutter-over-cross" or "square-text-clutter-over-cross")
                DrawTextLikeClutter(gray, clutterTruth, center.X, center.Y);
        }
        else if (type == "grid")
        {
            int verticals = spec.GetProperty("verticals").GetInt32();
            int horizontals = spec.GetProperty("horizontals").GetInt32();
            int spacing = spec.GetProperty("spacing").GetInt32();
            int startX = spec.GetProperty("start")[0].GetInt32();
            int startY = spec.GetProperty("start")[1].GetInt32();
            int[] xs = Enumerable.Range(0, verticals).Select(i => startX + i * spacing).ToArray();
            int[] ys = Enumerable.Range(0, horizontals).Select(i => startY + i * spacing).ToArray();
            foreach (int x in xs)
                DrawLine(gray, crossTruth, x, ys[0] - spacing / 2, x, ys[^1] + spacing / 2);
            foreach (int y in ys)
                DrawLine(gray, crossTruth, xs[0] - spacing / 2, y, xs[^1] + spacing / 2, y);
            foreach (int y in ys)
            foreach (int x in xs)
                crossingCenters.Add(new Point(x, y));
        }
        else if (type is "outlined-circle-alone" or "outlined-square-alone")
        {
            Point center = ReadPoint(spec.GetProperty("center"));
            int radius = spec.GetProperty("radius").GetInt32();
            if (type == "outlined-circle-alone")
                DrawCircle(gray, markerTruth, center, radius, filled: false);
            else
                DrawSquare(gray, markerTruth, center, radius, filled: false);
            markerCenters.Add(center);
        }
        else if (type == "arrow-bracket-legend-and-cross")
        {
            DrawArrow(gray, arrowTruth);
            DrawBracket(gray, bracketTruth);
            DrawFilledRectangle(gray, legendTruth, 118, 20, 124, 26);
            Point crossing = new(55, 52);
            DrawCross(gray, crossTruth, crossing, 12, "plus");
            crossingCenters.Add(crossing);
        }
        else
        {
            throw new InvalidDataException("Protocol contains an unsupported synthetic case type.");
        }

        string hash = HashBytes(gray);
        var raster = new ProductionDecodedRaster(
            Width, Height, hash, WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity, OcrFrameTransform.Identity, Width, Height,
            gray, gray.Select(static value => 1f - (value / 255f)).ToArray());
        var geometry = new AxisGeometryResult(
            AxisGeometryCoordinateSpaces.OriginalPixels,
            new PlotPolygon(new PixelPoint(10, 140), new PixelPoint(110, 140), new PixelPoint(110, 10), new PixelPoint(10, 10)),
            new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 140), new PixelPoint(110, 140)), 0.98, 0, 1, ["x"]),
            new AxisLineFit(new GeometryLineSegment(new PixelPoint(10, 140), new PixelPoint(10, 10)), 0.98, 0, 1, ["y"]),
            [], [], [], 0.98, new AxisGeometryUncertainty(0, 0, 1, false, []),
            new AxisGeometryDiagnostics(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));
        var axis = new ProductionAxisGeometryEvidence(Envelope(hash, "axis", "broad-oracle-bounds-v1"), geometry);
        OcrRegion[] regions = type == "arrow-bracket-legend-and-cross"
            ? [
                MakeRegion("synthetic-annotation", 122, 75, 30, 9, OcrTextRole.Annotation, "change"),
                MakeRegion("synthetic-legend", 130, 19, 35, 9, OcrTextRole.LegendText, "Series A"),
            ]
            : [];
        var ocr = new OcrResult(
            OcrContract.Version,
            Guid.Parse("30000000-0000-0000-0000-000000000071").ToString("D"),
            Guid.Parse("40000000-0000-0000-0000-000000000071").ToString("D"),
            Guid.Parse("50000000-0000-0000-0000-000000000071").ToString("D"),
            OcrContract.Stage, "synthetic-broad-context-v1", hash,
            OcrContract.CoordinateSpace, regions,
            regions.Select(static region => new OcrMask(region.RegionId, region.Polygon, region.Confidence)).ToArray(),
            new OcrTiming(0, 0, 0, 0), 1, [],
            new OcrCacheDiagnostics(false, "artifact-mask-broad-v1", regions.Length, 0), null, []);
        float[] ocrMask = new float[gray.Length];
        foreach (OcrRegion region in regions)
        {
            OcrRectangle bounds = region.Polygon.Bounds;
            FillMask(ocrMask, (int)bounds.Left, (int)bounds.Top, (int)bounds.Right - 1, (int)bounds.Bottom - 1);
        }
        var seed = new ProductionDetectionMaskSeed(
            Width, Height, hash, WorkflowImageVariant.Original, ocrMask, new float[gray.Length]);
        return new CaseInput(id, type, markerTruth, crossTruth, arrowTruth, bracketTruth, legendTruth, clutterTruth,
            markerCenters, crossingCenters, raster, axis, ocr, seed);
    }

    private static CaseInput CreateWarmupInput()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"id\":\"warmup\",\"type\":\"ordinary-cross\",\"topology\":\"plus\",\"arms\":15,\"center\":[80,70]}");
        return CreateInput(doc.RootElement);
    }

    private static CaseObservation Observe(
        CaseInput input,
        RasterResidualArtifactMaskResult baseline,
        RasterResidualArtifactMaskResult candidate,
        double baselineMilliseconds,
        double candidateMilliseconds)
    {
        bool[] baselineMask = ToMask(baseline);
        bool[] candidateMask = ToMask(candidate);
        Dictionary<RasterResidualArtifactCategory, int> baselineCounts = CategoryCounts(baseline);
        Dictionary<RasterResidualArtifactCategory, int> candidateCounts = CategoryCounts(candidate);
        return new CaseObservation(
            input.Id,
            input.Type,
            input.MarkerCenters.Count,
            input.MarkerCenters.Count(center => IsMasked(baselineMask, center)),
            input.MarkerCenters.Count(center => IsMasked(candidateMask, center)),
            CountOverlap(input.MarkerTruth, baselineMask),
            CountOverlap(input.MarkerTruth, candidateMask),
            input.MarkerTruth.Count(static value => value),
            CountOverlap(input.ClutterTruth, baselineMask),
            CountOverlap(input.ClutterTruth, candidateMask),
            input.ClutterTruth.Count(static value => value),
            input.MarkerCenters.Count == 0
                ? input.CrossingCenters.Count(center => !IsMasked(baselineMask, center)) : 0,
            input.MarkerCenters.Count == 0
                ? input.CrossingCenters.Count(center => !IsMasked(candidateMask, center)) : 0,
            input.MarkerCenters.Count == 0 ? input.CrossingCenters.Count : 0,
            CountOverlap(input.CrossingTruth, baselineMask),
            CountOverlap(input.CrossingTruth, candidateMask),
            input.CrossingTruth.Count(static value => value),
            CountOverlap(input.ArrowTruth, baselineMask),
            CountOverlap(input.ArrowTruth, candidateMask),
            input.ArrowTruth.Count(static value => value),
            CountOverlap(input.BracketTruth, baselineMask),
            CountOverlap(input.BracketTruth, candidateMask),
            input.BracketTruth.Count(static value => value),
            CountOverlap(input.LegendTruth, baselineMask),
            CountOverlap(input.LegendTruth, candidateMask),
            input.LegendTruth.Count(static value => value),
            baselineCounts,
            candidateCounts,
            baselineMilliseconds,
            candidateMilliseconds);
    }

    private static AggregateObservation Aggregate(IReadOnlyList<CaseObservation> cases)
    {
        Dictionary<RasterResidualArtifactCategory, int> baseline = Categories.ToDictionary(
            static category => category, category => cases.Sum(item => item.BaselineRegionCounts[category]));
        Dictionary<RasterResidualArtifactCategory, int> candidate = Categories.ToDictionary(
            static category => category, category => cases.Sum(item => item.CandidateRegionCounts[category]));
        return new AggregateObservation(
            cases.Count,
            cases.Sum(static item => item.MarkerCenters),
            cases.Sum(static item => item.BaselineSuppressedMarkerCenters),
            cases.Sum(static item => item.CandidateSuppressedMarkerCenters),
            cases.Sum(static item => item.MarkerTruthPixels),
            cases.Sum(static item => item.BaselineMaskedMarkerPixels),
            cases.Sum(static item => item.CandidateMaskedMarkerPixels),
            cases.Sum(static item => item.ClutterTruthPixels),
            cases.Sum(static item => item.BaselineMaskedClutterPixels),
            cases.Sum(static item => item.CandidateMaskedClutterPixels),
            cases.Sum(static item => item.CrossingCenters),
            cases.Sum(static item => item.BaselineUnmaskedCrossingCenters),
            cases.Sum(static item => item.CandidateUnmaskedCrossingCenters),
            cases.Sum(static item => item.CrossingTruthPixels),
            cases.Sum(static item => item.BaselineMaskedCrossingPixels),
            cases.Sum(static item => item.CandidateMaskedCrossingPixels),
            cases.Sum(static item => item.ArrowTruthPixels),
            cases.Sum(static item => item.BaselineMaskedArrowPixels),
            cases.Sum(static item => item.CandidateMaskedArrowPixels),
            cases.Sum(static item => item.BracketTruthPixels),
            cases.Sum(static item => item.BaselineMaskedBracketPixels),
            cases.Sum(static item => item.CandidateMaskedBracketPixels),
            cases.Sum(static item => item.LegendTruthPixels),
            cases.Sum(static item => item.BaselineMaskedLegendPixels),
            cases.Sum(static item => item.CandidateMaskedLegendPixels),
            baseline,
            candidate,
            cases.Sum(static item => item.BaselineMilliseconds),
            cases.Sum(static item => item.CandidateMilliseconds),
            cases.Average(static item => item.BaselineMilliseconds),
            cases.Average(static item => item.CandidateMilliseconds));
    }

    private static Dictionary<RasterResidualArtifactCategory, int> CategoryCounts(RasterResidualArtifactMaskResult result) =>
        Categories.ToDictionary(static category => category,
            category => result.Regions.Count(region => region.Category == category));

    private static bool[] ToMask(RasterResidualArtifactMaskResult result) =>
        result.Mask.ToArray().Select(static value => value >= 0.5f).ToArray();

    private static bool IsMasked(bool[] mask, Point point) => mask[(point.Y * Width) + point.X];

    private static int CountOverlap(bool[] truth, bool[] mask)
    {
        int count = 0;
        for (int i = 0; i < truth.Length; i++)
            if (truth[i] && mask[i])
                count++;
        return count;
    }

    private static void DrawCross(byte[] gray, bool[] truth, Point center, int arms, string topology)
    {
        if (topology == "plus")
        {
            DrawLine(gray, truth, center.X - arms, center.Y, center.X + arms, center.Y);
            DrawLine(gray, truth, center.X, center.Y - arms, center.X, center.Y + arms);
        }
        else if (topology == "x")
        {
            DrawLine(gray, truth, center.X - arms, center.Y - arms, center.X + arms, center.Y + arms);
            DrawLine(gray, truth, center.X - arms, center.Y + arms, center.X + arms, center.Y - arms);
        }
        else
        {
            throw new InvalidDataException("Unsupported crossing topology.");
        }
    }

    private static void DrawCircle(byte[] gray, bool[] truth, Point center, int radius, bool filled)
    {
        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            int d2 = x * x + y * y;
            if (filled ? d2 <= radius * radius : d2 <= radius * radius && d2 >= (radius - 1) * (radius - 1))
                Mark(center.X + x, center.Y + y);
        }
        void Mark(int x, int y)
        {
            int index = (y * Width) + x;
            gray[index] = 0;
            truth[index] = true;
        }
    }

    private static void DrawSquare(byte[] gray, bool[] truth, Point center, int radius, bool filled)
    {
        for (int y = center.Y - radius; y <= center.Y + radius; y++)
        for (int x = center.X - radius; x <= center.X + radius; x++)
        {
            if (!filled && x != center.X - radius && x != center.X + radius &&
                y != center.Y - radius && y != center.Y + radius)
                continue;
            int index = (y * Width) + x;
            gray[index] = 0;
            truth[index] = true;
        }
    }

    private static void DrawArrow(byte[] gray, bool[] truth)
    {
        DrawLine(gray, truth, 82, 79, 118, 79);
        for (int x = 78; x <= 84; x++)
        {
            int halfHeight = x - 78;
            DrawLine(gray, truth, x, 79 - halfHeight, x, 79 + halfHeight);
        }
    }

    private static void DrawBracket(byte[] gray, bool[] truth)
    {
        DrawLine(gray, truth, 116, 42, 116, 64);
        DrawLine(gray, truth, 116, 42, 128, 42);
        DrawLine(gray, truth, 116, 64, 128, 64);
    }

    private static void DrawFilledRectangle(byte[] gray, bool[] truth, int left, int top, int right, int bottom)
    {
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            int index = (y * Width) + x;
            gray[index] = 0;
            truth[index] = true;
        }
    }

    private static void DrawLine(byte[] gray, bool[] truth, int x1, int y1, int x2, int y2)
    {
        int dx = Math.Abs(x2 - x1);
        int sx = x1 < x2 ? 1 : -1;
        int dy = -Math.Abs(y2 - y1);
        int sy = y1 < y2 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            int index = (y1 * Width) + x1;
            gray[index] = 0;
            truth[index] = true;
            if (x1 == x2 && y1 == y2)
                break;
            int twice = 2 * error;
            if (twice >= dy)
            {
                error += dy;
                x1 += sx;
            }
            if (twice <= dx)
            {
                error += dx;
                y1 += sy;
            }
        }
    }

    private static OcrRegion MakeRegion(string id, int x, int y, int width, int height, OcrTextRole role, string text) => new(
        id, OcrPolygon.FromRectangle(new OcrRectangle(x, y, width, height)), text,
        [new OcrRecognitionAlternative(text, 0.98, OcrSourceImage.Original)], role, 0.98,
        OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static void FillMask(float[] mask, int left, int top, int right, int bottom)
    {
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
            mask[(y * Width) + x] = 1f;
    }

    private static Point ReadPoint(JsonElement point) => new(point[0].GetInt32(), point[1].GetInt32());

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, DevelopmentProtocolRelativePath)) &&
                File.Exists(Path.Combine(directory.FullName, ValidationProtocolRelativePath)))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the preregistered conservative outline-core protocols.");
    }

    private static void DrawTextLikeClutter(byte[] gray, bool[] truth, int centerX, int centerY)
    {
        // Small disconnected block-letter strokes flank the crossing without forming a closed contour.
        DrawLine(gray, truth, centerX - 18, centerY - 5, centerX - 18, centerY + 5);
        DrawLine(gray, truth, centerX - 18, centerY - 5, centerX - 12, centerY - 5);
        DrawLine(gray, truth, centerX - 18, centerY, centerX - 13, centerY);
        DrawLine(gray, truth, centerX - 18, centerY + 5, centerX - 12, centerY + 5);
        DrawLine(gray, truth, centerX + 13, centerY - 5, centerX + 13, centerY + 5);
        DrawLine(gray, truth, centerX + 13, centerY - 5, centerX + 19, centerY - 5);
        DrawLine(gray, truth, centerX + 13, centerY, centerX + 17, centerY);
        DrawLine(gray, truth, centerX + 13, centerY + 5, centerX + 19, centerY + 5);
        DrawLine(gray, truth, centerX - 4, centerY - 19, centerX + 4, centerY - 19);
        DrawLine(gray, truth, centerX, centerY - 19, centerX, centerY - 12);
    }

    private static WorkflowVisionEnvelope Envelope(string hash, string stage, string version) => new(
        1,
        Guid.Parse("30000000-0000-0000-0000-000000000071"),
        Guid.Parse("40000000-0000-0000-0000-000000000071"),
        Guid.Parse("50000000-0000-0000-0000-000000000071"),
        stage, version, hash, model: null, new WorkflowVisionTiming(0, 0, 0, 0), 0.98);

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class ConservativeOutlineCoreProbabilityProvider : IPreOcrStructuralProbabilityProvider
    {
        private const byte ForegroundMaximum = 196;
        private const byte GeometryMaskMinimum = 128;
        private const int CoreRadius = 2;
        private const int MinimumRay = 5;
        private const int MaximumRay = 64;
        private const int RequiredRingSectors = 15;
        private const int NearbyCrossingRadius = 12;
        private static readonly RasterPreOcrStructuralProbabilityProvider BaseProvider = new();
        private static readonly (int X, int Y)[] Directions = [(1, 0), (0, 1), (1, 1), (1, -1)];

        public async ValueTask<PreOcrStructuralProbabilityResult> AnalyzeAsync(
            PreOcrStructuralFrame frame,
            CancellationToken cancellationToken)
        {
            PreOcrStructuralProbabilityResult baseline = await BaseProvider.AnalyzeAsync(frame, cancellationToken)
                .ConfigureAwait(false);
            float[] marker = baseline.MarkerLikeProbabilities.ToArray();
            byte[] gray = frame.Gray8Pixels.ToArray();
            byte[] geometry = frame.GeometryMask.ToArray();
            List<Point> crossings = FindCrossings(frame.Width, frame.Height, gray, geometry);
            for (int y = 10; y < frame.Height - 10; y++)
            for (int x = 10; x < frame.Width - 10; x++)
            {
                if ((x & 31) == 0 && (y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!HasLongCrossing(frame.Width, frame.Height, gray, geometry, x, y) ||
                    !HasClosedOutline(gray, geometry, frame.Width, frame.Height, x, y) ||
                    HasNearbyCrossing(crossings, x, y))
                    continue;

                for (int offsetY = -CoreRadius; offsetY <= CoreRadius; offsetY++)
                for (int offsetX = -CoreRadius; offsetX <= CoreRadius; offsetX++)
                {
                    int xx = x + offsetX;
                    int yy = y + offsetY;
                    int index = (yy * frame.Width) + xx;
                    if (gray[index] <= ForegroundMaximum && geometry[index] < GeometryMaskMinimum)
                        marker[index] = Math.Max(marker[index], 0.90f);
                }
            }

            return new PreOcrStructuralProbabilityResult(
                baseline.Width, baseline.Height, baseline.Stride, marker,
                baseline.ThinConnectorProbabilities, baseline.CoordinateSpace);
        }

        private static List<Point> FindCrossings(int width, int height, byte[] gray, byte[] geometry)
        {
            var crossings = new List<Point>();
            for (int y = 10; y < height - 10; y++)
            for (int x = 10; x < width - 10; x++)
                if (HasLongCrossing(width, height, gray, geometry, x, y)) crossings.Add(new Point(x, y));
            return crossings;
        }

        private static bool HasNearbyCrossing(IReadOnlyList<Point> crossings, int x, int y)
        {
            int maximumSquaredDistance = NearbyCrossingRadius * NearbyCrossingRadius;
            foreach (Point crossing in crossings)
            {
                int dx = crossing.X - x;
                int dy = crossing.Y - y;
                int squaredDistance = (dx * dx) + (dy * dy);
                if (squaredDistance > CoreRadius * CoreRadius && squaredDistance <= maximumSquaredDistance) return true;
            }
            return false;
        }

        private static bool HasLongCrossing(int width, int height, byte[] gray, byte[] geometry, int centerX, int centerY)
        {
            int extendedPairs = 0;
            foreach ((int dx, int dy) in Directions)
            {
                int forward = Ray(gray, geometry, width, height, centerX, centerY, dx, dy);
                int backward = Ray(gray, geometry, width, height, centerX, centerY, -dx, -dy);
                if (forward >= MinimumRay && backward >= MinimumRay && forward + backward + 1 > 13) extendedPairs++;
            }
            return extendedPairs >= 2;
        }

        private static int Ray(byte[] gray, byte[] geometry, int width, int height, int centerX, int centerY, int dx, int dy)
        {
            int length = 0;
            for (int x = centerX + dx, y = centerY + dy;
                 length < MaximumRay && x >= 0 && x < width && y >= 0 && y < height;
                 x += dx, y += dy)
            {
                int index = (y * width) + x;
                if (gray[index] > ForegroundMaximum || geometry[index] >= GeometryMaskMinimum) break;
                length++;
            }
            return length;
        }

        private static bool HasClosedOutline(byte[] gray, byte[] geometry, int width, int height, int centerX, int centerY)
        {
            const int sectors = 16;
            for (int radius = 4; radius <= 9; radius++)
            {
                var circleSupport = new bool[sectors];
                var squareSupport = new bool[sectors];
                for (int dy = -radius - 1; dy <= radius + 1; dy++)
                for (int dx = -radius - 1; dx <= radius + 1; dx++)
                {
                    int x = centerX + dx;
                    int y = centerY + dy;
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    int index = (y * width) + x;
                    if (gray[index] > ForegroundMaximum || geometry[index] >= GeometryMaskMinimum) continue;
                    double angle = Math.Atan2(dy, dx);
                    if (angle < 0) angle += Math.PI * 2;
                    int sector = ((int)Math.Floor((angle * sectors / (Math.PI * 2)) + 0.5)) % sectors;
                    double radialDistance = Math.Sqrt((dx * dx) + (dy * dy));
                    if (Math.Abs(radialDistance - radius) <= 0.75) circleSupport[sector] = true;
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius) squareSupport[sector] = true;
                }
                if (circleSupport.Count(static supported => supported) >= RequiredRingSectors ||
                    squareSupport.Count(static supported => supported) >= RequiredRingSectors)
                    return true;
            }
            return false;
        }
    }

    private sealed record Point(int X, int Y);

    private sealed record CaseInput(
        string Id,
        string Type,
        bool[] MarkerTruth,
        bool[] CrossingTruth,
        bool[] ArrowTruth,
        bool[] BracketTruth,
        bool[] LegendTruth,
        bool[] ClutterTruth,
        List<Point> MarkerCenters,
        List<Point> CrossingCenters,
        ProductionDecodedRaster Raster,
        ProductionAxisGeometryEvidence Axis,
        OcrResult Ocr,
        ProductionDetectionMaskSeed Seed);

    private sealed record CaseObservation(
        string Id,
        string Type,
        int MarkerCenters,
        int BaselineSuppressedMarkerCenters,
        int CandidateSuppressedMarkerCenters,
        int BaselineMaskedMarkerPixels,
        int CandidateMaskedMarkerPixels,
        int MarkerTruthPixels,
        int BaselineMaskedClutterPixels,
        int CandidateMaskedClutterPixels,
        int ClutterTruthPixels,
        int BaselineUnmaskedCrossingCenters,
        int CandidateUnmaskedCrossingCenters,
        int CrossingCenters,
        int BaselineMaskedCrossingPixels,
        int CandidateMaskedCrossingPixels,
        int CrossingTruthPixels,
        int BaselineMaskedArrowPixels,
        int CandidateMaskedArrowPixels,
        int ArrowTruthPixels,
        int BaselineMaskedBracketPixels,
        int CandidateMaskedBracketPixels,
        int BracketTruthPixels,
        int BaselineMaskedLegendPixels,
        int CandidateMaskedLegendPixels,
        int LegendTruthPixels,
        Dictionary<RasterResidualArtifactCategory, int> BaselineRegionCounts,
        Dictionary<RasterResidualArtifactCategory, int> CandidateRegionCounts,
        double BaselineMilliseconds,
        double CandidateMilliseconds);

    private sealed record AggregateObservation(
        int Cases,
        int MarkerCenters,
        int BaselineSuppressedMarkerCenters,
        int CandidateSuppressedMarkerCenters,
        int MarkerTruthPixels,
        int BaselineMaskedMarkerPixels,
        int CandidateMaskedMarkerPixels,
        int ClutterTruthPixels,
        int BaselineMaskedClutterPixels,
        int CandidateMaskedClutterPixels,
        int CrossingCenters,
        int BaselineUnmaskedClearCrossingCenters,
        int CandidateUnmaskedClearCrossingCenters,
        int CrossingTruthPixels,
        int BaselineMaskedCrossingPixels,
        int CandidateMaskedCrossingPixels,
        int ArrowTruthPixels,
        int BaselineMaskedArrowPixels,
        int CandidateMaskedArrowPixels,
        int BracketTruthPixels,
        int BaselineMaskedBracketPixels,
        int CandidateMaskedBracketPixels,
        int LegendTruthPixels,
        int BaselineMaskedLegendPixels,
        int CandidateMaskedLegendPixels,
        Dictionary<RasterResidualArtifactCategory, int> BaselineRegionCounts,
        Dictionary<RasterResidualArtifactCategory, int> CandidateRegionCounts,
        double BaselineTotalMilliseconds,
        double CandidateTotalMilliseconds,
        double BaselineAverageMilliseconds,
        double CandidateAverageMilliseconds);
}

