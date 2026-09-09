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

    private static TestInputs CreateCompositeInputs()
    {
        const int width = 160;
        const int height = 110;
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
                new PixelPoint(10, 95),
                new PixelPoint(105, 95),
                new PixelPoint(105, 10),
                new PixelPoint(10, 10)),
            new AxisLineFit(
                new GeometryLineSegment(new PixelPoint(10, 95), new PixelPoint(105, 95)),
                0.98,
                0,
                1,
                ["x"]),
            new AxisLineFit(
                new GeometryLineSegment(new PixelPoint(10, 95), new PixelPoint(10, 10)),
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
        OcrRegion[] regions = [annotation, legend, other];
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
