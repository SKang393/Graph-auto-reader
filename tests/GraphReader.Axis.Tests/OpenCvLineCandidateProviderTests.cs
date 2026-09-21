// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class OpenCvLineCandidateProviderTests
{
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public async Task NativeEdgeGapNeedsOriginalInkConnection(bool horizontal, bool connected)
    {
        GrayscaleLineCandidateFrame frame = CreateOccludedAxisFrame(horizontal, connected);
        byte[] original = frame.Pixels.ToArray();
        IReadOnlyList<GeometryLineCandidate> candidates = await new OpenCvLineCandidateProvider()
            .DetectLinesAsync(frame, CancellationToken.None);
        bool hasConnection = candidates.Any(candidate =>
            candidate.Source == LineCandidateSource.Other &&
            (horizontal
                ? Math.Abs(candidate.Segment.Midpoint.Y - 30.5) < 3 &&
                    Math.Min(candidate.Segment.Start.X, candidate.Segment.End.X) <= 100 &&
                    Math.Max(candidate.Segment.Start.X, candidate.Segment.End.X) >= 104
                : Math.Abs(candidate.Segment.Midpoint.X - 30.5) < 3 &&
                    Math.Min(candidate.Segment.Start.Y, candidate.Segment.End.Y) <= 100 &&
                    Math.Max(candidate.Segment.Start.Y, candidate.Segment.End.Y) >= 104));

        Assert.AreEqual(connected, hasConnection,
            "A local outline may connect native line edges; an equally sized empty gap cannot.");
        CollectionAssert.AreEqual(original, frame.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow(false, true, 114)]
    [DataRow(true, true, 114)]
    [DataRow(false, false, 114)]
    [DataRow(true, false, 114)]
    [DataRow(false, true, 120)]
    [DataRow(true, true, 120)]
    [DataRow(false, false, 120)]
    [DataRow(true, false, 120)]
    public async Task LargerSymbolOutlineConnectsAxesButAnEmptyGapDoesNot(
        bool horizontal, bool connected, int gapEnd)
    {
        GrayscaleLineCandidateFrame frame = CreateOccludedAxisFrame(horizontal, connected, gapEnd);
        byte[] original = frame.Pixels.ToArray();
        IReadOnlyList<GeometryLineCandidate> candidates = await new OpenCvLineCandidateProvider()
            .DetectLinesAsync(frame, CancellationToken.None);
        bool crossesGap = candidates.Any(candidate => candidate.Source == LineCandidateSource.Other &&
            (horizontal
                ? Math.Abs(candidate.Segment.Midpoint.Y - 30.5) < 3 &&
                    Math.Min(candidate.Segment.Start.X, candidate.Segment.End.X) <= 100 &&
                    Math.Max(candidate.Segment.Start.X, candidate.Segment.End.X) >= gapEnd
                : Math.Abs(candidate.Segment.Midpoint.X - 30.5) < 3 &&
                    Math.Min(candidate.Segment.Start.Y, candidate.Segment.End.Y) <= 100 &&
                    Math.Max(candidate.Segment.Start.Y, candidate.Segment.End.Y) >= gapEnd));

        Assert.AreEqual(connected, crossesGap);
        CollectionAssert.AreEqual(original, frame.Pixels.ToArray());
        if (connected && !horizontal)
        {
            AxisGeometryResult geometry = await new AxisGeometryDetector().DetectAsync(
                new AxisGeometryRequest(frame.Width, frame.Height, candidates));
            Assert.AreEqual(20d, geometry.PlotPolygon.TopLeft.Y, 4d,
                "An open symbol must not collapse the plot to the terminal axis stub.");
            Assert.AreEqual(130.5d, geometry.PlotPolygon.BottomLeft.Y, 4d);
        }
    }

    [TestMethod]
    public async Task NativeProviderReturnsLsdAndHoughCandidatesForCleanAxes()
    {
        GrayscaleLineCandidateFrame frame = CreateCleanAxisFrame();
        var provider = new OpenCvLineCandidateProvider(new OpenCvLineCandidateOptions
        {
            HoughThreshold = 12,
            HoughMinimumLineLengthPixels = 20,
            HoughMaximumLineGapPixels = 2,
        });

        IReadOnlyList<GeometryLineCandidate> candidates =
            await provider.DetectLinesAsync(frame, CancellationToken.None);

        Assert.IsTrue(candidates.Any(candidate => candidate.Source == LineCandidateSource.OpenCvLsd));
        Assert.IsTrue(candidates.Any(candidate => candidate.Source == LineCandidateSource.OpenCvHough));
        Assert.IsTrue(candidates.All(candidate => candidate.Segment.Start.IsFinite));
        Assert.IsTrue(candidates.All(candidate => candidate.Segment.End.IsFinite));
        Assert.IsTrue(candidates.All(candidate => candidate.Segment.Length > 0));
    }

    [TestMethod]
    public async Task NativeCandidatesFeedGeometryDetectorInOriginalPixels()
    {
        GrayscaleLineCandidateFrame frame = CreateCleanAxisFrame();
        var provider = new OpenCvLineCandidateProvider(new OpenCvLineCandidateOptions
        {
            HoughThreshold = 12,
            HoughMinimumLineLengthPixels = 20,
            HoughMaximumLineGapPixels = 2,
        });

        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(frame, provider);

        Assert.AreEqual(AxisGeometryCoordinateSpaces.OriginalPixels, result.CoordinateSpace);
        Assert.AreEqual(30.5d, result.PlotPolygon.BottomLeft.X, 4d);
        Assert.AreEqual(130.5d, result.PlotPolygon.BottomLeft.Y, 4d);
        Assert.IsTrue(result.Diagnostics.AcceptedCandidateCount > 0);
    }

    [TestMethod]
    public async Task NativeProviderValidatesOriginalPixelsBufferAndCancellation()
    {
        GrayscaleLineCandidateFrame valid = CreateCleanAxisFrame();
        var provider = new OpenCvLineCandidateProvider();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await provider.DetectLinesAsync(
                valid with { CoordinateSpace = "enhanced_pixels" },
                CancellationToken.None));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await provider.DetectLinesAsync(
                valid with { Pixels = valid.Pixels[..^1] },
                CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.DetectLinesAsync(valid, cancellation.Token));
    }

    private static GrayscaleLineCandidateFrame CreateCleanAxisFrame()
    {
        const int width = 256;
        const int height = 160;
        var pixels = new byte[width * height];
        Array.Fill(pixels, byte.MaxValue);

        for (int x = 30; x <= 225; x++)
        {
            SetBlack(pixels, width, x, 130);
            SetBlack(pixels, width, x, 131);
        }

        for (int y = 20; y <= 131; y++)
        {
            SetBlack(pixels, width, 30, y);
            SetBlack(pixels, width, 31, y);
        }

        return new GrayscaleLineCandidateFrame(width, height, width, pixels);
    }

    private static GrayscaleLineCandidateFrame CreateOccludedAxisFrame(
        bool horizontal, bool connected, int gapEnd = 104)
    {
        GrayscaleLineCandidateFrame original = CreateCleanAxisFrame();
        byte[] pixels = original.Pixels.ToArray();
        for (int y = 100; y <= gapEnd; y++)
        {
            pixels[(y * original.Stride) + 30] = byte.MaxValue;
            pixels[(y * original.Stride) + 31] = byte.MaxValue;
        }

        if (connected)
        {
            for (int x = 26; x <= 35; x++)
            {
                SetBlack(pixels, original.Stride, x, 99);
                SetBlack(pixels, original.Stride, x, gapEnd + 1);
            }

            for (int y = 99; y <= gapEnd + 1; y++)
            {
                SetBlack(pixels, original.Stride, 26, y);
                SetBlack(pixels, original.Stride, 35, y);
            }
        }

        if (!horizontal)
        {
            return original with { Pixels = pixels };
        }

        byte[] transposed = new byte[pixels.Length];
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                transposed[(x * original.Height) + y] = pixels[(y * original.Stride) + x];
            }
        }

        return new GrayscaleLineCandidateFrame(original.Height, original.Width, original.Height, transposed);
    }

    private static void SetBlack(byte[] pixels, int stride, int x, int y) =>
        pixels[(y * stride) + x] = 0;
}
