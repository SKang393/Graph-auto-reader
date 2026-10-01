// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RasterResidualArtifactMaskProviderTests
{
    [TestMethod]
    public async Task AnalyzeAsyncFindsFourResidualCategoriesFromRasterAndSemanticContext()
    {
        TestInputs inputs = CreateCompositeInputs();
        var provider = new RasterResidualArtifactMaskProvider();

        RasterResidualArtifactMaskResult result = await provider.AnalyzeAsync(
            inputs.Raster,
            inputs.Axis,
            inputs.Ocr,
            inputs.Seed,
            CancellationToken.None);

        float[] mask = result.Mask.ToArray();
        Assert.AreEqual(inputs.Raster.Width, result.Width);
        Assert.AreEqual(inputs.Raster.Height, result.Height);
        Assert.IsGreaterThan(0.5f, mask[Index(inputs.Raster.Width, 80, 79)], "Arrowhead should be residual artifact evidence.");
        Assert.IsGreaterThan(0.5f, mask[Index(inputs.Raster.Width, 116, 52)], "Bracket spine should be residual artifact evidence.");
        Assert.IsGreaterThan(0.5f, mask[Index(inputs.Raster.Width, 118, 23)], "Legend glyph should be residual artifact evidence.");
        Assert.IsGreaterThan(0.5f, mask[Index(inputs.Raster.Width, 55, 52)], "Connecting-line intersection should be residual artifact evidence.");

        CollectionAssert.AreEquivalent(
            new[]
            {
                RasterResidualArtifactCategory.AnnotationArrow,
                RasterResidualArtifactCategory.Bracket,
                RasterResidualArtifactCategory.LegendStructure,
                RasterResidualArtifactCategory.ConnectingLineIntersection,
            },
            result.Regions.Select(static region => region.Category).Distinct().ToArray());
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("unapproved", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task AnalyzeAsyncLeavesAmbiguousMarkersAndOcrTextReviewable()
    {
        TestInputs inputs = CreateCompositeInputs();
        var provider = new RasterResidualArtifactMaskProvider();

        RasterResidualArtifactMaskResult result = await provider.AnalyzeAsync(
            inputs.Raster,
            inputs.Axis,
            inputs.Ocr,
            inputs.Seed,
            CancellationToken.None);

        float[] mask = result.Mask.ToArray();
        Assert.AreEqual(0f, mask[Index(inputs.Raster.Width, 35, 35)], "Isolated triangle marker must stay reviewable.");
        Assert.AreEqual(0f, mask[Index(inputs.Raster.Width, 42, 72)], "Compact plus marker must stay reviewable.");
        Assert.AreEqual(0f, mask[Index(inputs.Raster.Width, 75, 72)], "Open-ring marker must stay reviewable.");
        Assert.AreEqual(0f, mask[Index(inputs.Raster.Width, 24, 18)], "OCR-covered text must not become residual artifact evidence.");
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("reviewable", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [DataRow(5, false, false)]
    [DataRow(6, false, false)]
    [DataRow(5, true, false)]
    [DataRow(6, true, false)]
    [DataRow(10, false, true)]
    [DataRow(10, true, false)]
    public async Task CrossingMasksRespectCompactAndMarkerLikeConflicts(int armLength, bool connected, bool expectedMasked)
    {
        TestInputs inputs = CreateCompositeInputs(armLength, connected);
        RasterResidualArtifactMaskResult result = await new RasterResidualArtifactMaskProvider().AnalyzeAsync(
            inputs.Raster, inputs.Axis, inputs.Ocr, inputs.Seed, CancellationToken.None);

        // The third line makes a dense core that the existing morphology
        // provider marks as marker-like. Keep that conflict reviewable too.
        Assert.AreEqual(expectedMasked, result.Mask.Span[Index(inputs.Raster.Width, 80, 32)] >= 0.5f);
        Assert.IsTrue(result.Mask.Span[Index(inputs.Raster.Width, 55, 52)] >= 0.5f,
            "The existing long connecting-line intersection must remain excluded.");
        if (!expectedMasked)
            Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("crossing candidate(s)", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(3, 0, false)]
    [DataRow(3, 0, true)]
    [DataRow(3, -2, false)]
    [DataRow(3, 2, true)]
    [DataRow(4, 0, false)]
    [DataRow(4, 0, true)]
    [DataRow(4, -2, true)]
    [DataRow(4, 2, false)]
    public async Task FilledMarkerCoreAtLongCrossingRemainsReviewable(int radius, int offset, bool connected)
    {
        TestInputs inputs = CreateCompositeInputs(
            crossArmLength: 10, connectedCross: connected, centerBlobRadius: radius, centerBlobOffset: offset);
        byte[] original = inputs.Raster.CreateOcrImage().Pixels.ToArray();

        RasterResidualArtifactMaskResult result = await new RasterResidualArtifactMaskProvider().AnalyzeAsync(
            inputs.Raster, inputs.Axis, inputs.Ocr, inputs.Seed, CancellationToken.None);

        Assert.AreEqual(0f, result.Mask.Span[Index(inputs.Raster.Width, 80, 32)],
            "Long connecting strokes must not hide conflicting marker-like ink.");
        Assert.AreEqual(0f, result.Mask.Span[Index(inputs.Raster.Width, 80 + offset, 32)]);
        Assert.IsTrue(result.Mask.Span[Index(inputs.Raster.Width, 55, 52)] >= 0.5f,
            "A separate thin line intersection must remain masked.");
        Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("marker-like ink", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(original, inputs.Raster.CreateOcrImage().Pixels.ToArray());
    }

    [TestMethod]
    [DataRow(0, "aligned", true)]
    [DataRow(1, "aligned", true)]
    [DataRow(2, "aligned", true)]
    [DataRow(3, "aligned", true)]
    [DataRow(0, "perpendicular", false)]
    [DataRow(1, "perpendicular", false)]
    [DataRow(2, "perpendicular", false)]
    [DataRow(3, "perpendicular", false)]
    [DataRow(0, "beyond-head", false)]
    [DataRow(1, "beyond-head", false)]
    [DataRow(2, "beyond-head", false)]
    [DataRow(3, "beyond-head", false)]
    [DataRow(0, "competing", true)]
    [DataRow(1, "competing", true)]
    [DataRow(2, "competing", true)]
    [DataRow(3, "competing", true)]
    public async Task AnnotationArrowRequiresLabelBehindAndAlongShaft(
        int quarterTurns, string annotationLayout, bool expectedArrow)
    {
        TestInputs inputs = CreateCompositeInputs(quarterTurns: quarterTurns, annotationLayout: annotationLayout);
        byte[] original = inputs.Raster.CreateOcrImage().Pixels.ToArray();

        RasterResidualArtifactMaskResult result = await new RasterResidualArtifactMaskProvider().AnalyzeAsync(
            inputs.Raster, inputs.Axis, inputs.Ocr, inputs.Seed, CancellationToken.None);

        Assert.AreEqual(expectedArrow, result.Regions.Any(static region =>
            region.Category == RasterResidualArtifactCategory.AnnotationArrow));
        CollectionAssert.AreEqual(original, inputs.Raster.CreateOcrImage().Pixels.ToArray());
        if (!expectedArrow)
            Assert.IsTrue(result.Warnings.Any(static warning => warning.Contains("reviewable", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AnalyzeAsyncHonorsPreCanceledToken()
    {
        TestInputs inputs = CreateCompositeInputs();
        var provider = new RasterResidualArtifactMaskProvider();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => provider.AnalyzeAsync(
            inputs.Raster,
            inputs.Axis,
            inputs.Ocr,
            inputs.Seed,
            source.Token));
    }

    private static TestInputs CreateCompositeInputs(
        int crossArmLength = 0, bool connectedCross = false, int quarterTurns = 0, string annotationLayout = "aligned",
        int centerBlobRadius = 0, int centerBlobOffset = 0)
    {
        int width = 160;
        int height = 110;
        var gray = Enumerable.Repeat((byte)255, width * height).ToArray();

        DrawArrow(gray, width);
        DrawBracket(gray, width);
        DrawFilledRectangle(gray, width, 115, 20, 121, 26);
        DrawLine(gray, width, 45, 52, 65, 52);
        DrawLine(gray, width, 55, 42, 55, 62);
        DrawTriangle(gray, width, 32, 32, 38, 38);
        DrawLine(gray, width, 39, 72, 45, 72);
        DrawLine(gray, width, 42, 69, 42, 75);
        DrawRing(gray, width, 72, 69, 78, 75);
        DrawFilledRectangle(gray, width, 20, 15, 28, 21);
        if (crossArmLength > 0)
        {
            DrawLine(gray, width, 80 - crossArmLength, 32 - crossArmLength, 80 + crossArmLength, 32 + crossArmLength);
            DrawLine(gray, width, 80 - crossArmLength, 32 + crossArmLength, 80 + crossArmLength, 32 - crossArmLength);
            if (connectedCross)
                DrawLine(gray, width, 65, 32, 95, 32);
            if (centerBlobRadius > 0)
                DrawFilledRectangle(gray, width,
                    80 + centerBlobOffset - centerBlobRadius, 32 - centerBlobRadius,
                    80 + centerBlobOffset + centerBlobRadius, 32 + centerBlobRadius);
        }

        PixelPoint Rotate(double x, double y)
        {
            int currentWidth = 160, currentHeight = 110;
            for (int turn = 0; turn < quarterTurns; turn++)
            {
                (x, y) = (currentHeight - 1 - y, x);
                (currentWidth, currentHeight) = (currentHeight, currentWidth);
            }
            return new PixelPoint(x, y);
        }
        if (quarterTurns != 0)
        {
            width = quarterTurns % 2 == 0 ? 160 : 110;
            height = quarterTurns % 2 == 0 ? 110 : 160;
            var rotated = new byte[gray.Length];
            for (int y = 0; y < 110; y++)
            for (int x = 0; x < 160; x++)
            {
                PixelPoint point = Rotate(x, y);
                rotated[Index(width, (int)point.X, (int)point.Y)] = gray[Index(160, x, y)];
            }
            gray = rotated;
        }
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(gray));
        var raster = new ProductionDecodedRaster(
            width,
            height,
            sha256,
            WorkflowImageVariant.Original,
            MarkerAffineTransform.Identity,
            OcrFrameTransform.Identity,
            width,
            height,
            gray,
            gray.Select(static value => 1f - (value / 255f)).ToArray());

        var geometry = new AxisGeometryResult(
            AxisGeometryCoordinateSpaces.OriginalPixels,
            new PlotPolygon(
                Rotate(10, 95),
                Rotate(105, 95),
                Rotate(105, 10),
                Rotate(10, 10)),
            new AxisLineFit(
                new GeometryLineSegment(Rotate(10, 95), Rotate(105, 95)),
                0.98,
                0,
                1,
                ["x"]),
            new AxisLineFit(
                new GeometryLineSegment(Rotate(10, 95), Rotate(10, 10)),
                0.98,
                0,
                1,
                ["y"]),
            [],
            [],
            [],
            0.98,
            new AxisGeometryUncertainty(0, 0, 1, false, []),
            new AxisGeometryDiagnostics(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));
        var axis = new ProductionAxisGeometryEvidence(
            Envelope(sha256, "axis", "axis-test-v1"),
            geometry);

        OcrRegion annotation = Region("annotation", 122, 75, 30, 9, OcrTextRole.Annotation, "change");
        OcrRegion legend = Region("legend", 130, 19, 24, 9, OcrTextRole.LegendText, "Series A");
        OcrRegion other = Region("other", 20, 15, 9, 7, OcrTextRole.Other, "note");
        OcrRegion[] labels = annotationLayout switch
        {
            "aligned" => [annotation],
            "perpendicular" => [Region("annotation", 112, 40, 40, 9, OcrTextRole.Annotation, "caption")],
            "beyond-head" => [Region("annotation", 0, 25, 10, 75, OcrTextRole.Annotation, "caption")],
            "competing" => [Region("nearer", 115, 68, 40, 9, OcrTextRole.Annotation, "caption"), annotation],
            _ => throw new ArgumentException("Unknown annotation fixture.", nameof(annotationLayout)),
        };
        OcrRegion[] regions = [.. labels, legend, other];
        regions = regions.Select(region => region with
        {
            Polygon = new OcrPolygon(region.Polygon.Points.Select(point =>
            {
                PixelPoint rotated = Rotate(point.X, point.Y);
                return new OcrPoint(rotated.X, rotated.Y);
            }).ToArray()),
        }).ToArray();
        var ocr = new OcrResult(
            OcrContract.Version,
            Guid.Parse("30000000-0000-0000-0000-000000000022").ToString("D"),
            Guid.Parse("40000000-0000-0000-0000-000000000022").ToString("D"),
            Guid.Parse("50000000-0000-0000-0000-000000000022").ToString("D"),
            OcrContract.Stage,
            "ocr-test-v1",
            sha256,
            OcrContract.CoordinateSpace,
            regions,
            regions.Select(static region => new OcrMask(region.RegionId, region.Polygon, region.Confidence)).ToArray(),
            new OcrTiming(0, 0, 0, 0),
            0.98,
            [],
            new OcrCacheDiagnostics(false, "residual-test", regions.Length, 1),
            null,
            []);

        var ocrSeed = new float[width * height];
        foreach (OcrRegion region in regions)
        {
            OcrRectangle bounds = region.Polygon.Bounds;
            FillMask(ocrSeed, width, (int)bounds.Left, (int)bounds.Top, (int)bounds.Right - 1, (int)bounds.Bottom - 1);
        }

        var seed = new ProductionDetectionMaskSeed(
            width,
            height,
            sha256,
            WorkflowImageVariant.Original,
            ocrSeed,
            new float[width * height]);
        return new TestInputs(raster, axis, ocr, seed);
    }

    private static WorkflowVisionEnvelope Envelope(string sha256, string stage, string stageVersion) => new(
        1,
        Guid.Parse("30000000-0000-0000-0000-000000000022"),
        Guid.Parse("40000000-0000-0000-0000-000000000022"),
        Guid.Parse("50000000-0000-0000-0000-000000000022"),
        stage,
        stageVersion,
        sha256,
        model: null,
        new WorkflowVisionTiming(0, 0, 0, 0),
        0.98);

    private static OcrRegion Region(
        string id,
        int x,
        int y,
        int width,
        int height,
        OcrTextRole role,
        string text) => new(
            id,
            OcrPolygon.FromRectangle(new OcrRectangle(x, y, width, height)),
            text,
            [new OcrRecognitionAlternative(text, 0.98, OcrSourceImage.Original)],
            role,
            0.98,
            OcrSourceImage.Original,
            OcrReviewStatus.Unreviewed);

    private static void DrawArrow(byte[] pixels, int width)
    {
        DrawLine(pixels, width, 82, 79, 118, 79);
        for (int x = 78; x <= 84; x++)
        {
            int halfHeight = x - 78;
            DrawLine(pixels, width, x, 79 - halfHeight, x, 79 + halfHeight);
        }
    }

    private static void DrawBracket(byte[] pixels, int width)
    {
        DrawLine(pixels, width, 116, 42, 116, 64);
        DrawLine(pixels, width, 116, 42, 128, 42);
        DrawLine(pixels, width, 116, 64, 128, 64);
    }

    private static void DrawTriangle(byte[] pixels, int width, int left, int top, int right, int bottom)
    {
        int centerY = (top + bottom) / 2;
        for (int x = left; x <= right; x++)
        {
            int halfHeight = Math.Min(centerY - top, x - left);
            DrawLine(pixels, width, x, centerY - halfHeight, x, centerY + halfHeight);
        }
    }

    private static void DrawRing(byte[] pixels, int width, int left, int top, int right, int bottom)
    {
        DrawLine(pixels, width, left, top, right, top);
        DrawLine(pixels, width, left, bottom, right, bottom);
        DrawLine(pixels, width, left, top, left, bottom);
        DrawLine(pixels, width, right, top, right, bottom);
    }

    private static void DrawFilledRectangle(byte[] pixels, int width, int left, int top, int right, int bottom)
    {
        for (int y = top; y <= bottom; y++)
        {
            DrawLine(pixels, width, left, y, right, y);
        }
    }

    private static void FillMask(float[] mask, int width, int left, int top, int right, int bottom)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                mask[Index(width, x, y)] = 1f;
            }
        }
    }

    private static void DrawLine(byte[] pixels, int width, int x1, int y1, int x2, int y2)
    {
        int deltaX = Math.Abs(x2 - x1);
        int stepX = x1 < x2 ? 1 : -1;
        int deltaY = -Math.Abs(y2 - y1);
        int stepY = y1 < y2 ? 1 : -1;
        int error = deltaX + deltaY;
        while (true)
        {
            pixels[Index(width, x1, y1)] = 0;
            if (x1 == x2 && y1 == y2)
            {
                return;
            }

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

    private static int Index(int width, int x, int y) => (y * width) + x;

    private sealed record TestInputs(
        ProductionDecodedRaster Raster,
        ProductionAxisGeometryEvidence Axis,
        OcrResult Ocr,
        ProductionDetectionMaskSeed Seed);
}
