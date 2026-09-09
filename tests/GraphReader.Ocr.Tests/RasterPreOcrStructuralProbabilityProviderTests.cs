// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class RasterPreOcrStructuralProbabilityProviderTests
{
    private const float SuppressionThreshold = 0.5f;

    [TestMethod]
    public async Task AnalyzeAsyncReturnsOriginalPixelPlanesWithoutMutatingInputs()
    {
        var scene = new SyntheticScene(48, 32);
        scene.DrawFilledMarker(34, 8);
        scene.DrawHorizontalConnector(22, 36, 22);
        scene.DrawKnownGeometryLine(18);
        byte[] pixelsBefore = (byte[])scene.Pixels.Clone();
        byte[] geometryBefore = (byte[])scene.GeometryMask.Clone();
        var provider = new RasterPreOcrStructuralProbabilityProvider();

        PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
            scene.CreateFrame(),
            CancellationToken.None);

        Assert.AreEqual(scene.Width, result.Width);
        Assert.AreEqual(scene.Height, result.Height);
        Assert.AreEqual(scene.Width, result.Stride);
        Assert.AreEqual(OcrContract.CoordinateSpace, result.CoordinateSpace);
        Assert.AreEqual(scene.Width * scene.Height, result.MarkerLikeProbabilities.Length);
        Assert.AreEqual(scene.Width * scene.Height, result.ThinConnectorProbabilities.Length);
        Assert.IsTrue(result.MarkerLikeProbabilities.Span.ToArray().All(IsProbability));
        Assert.IsTrue(result.ThinConnectorProbabilities.Span.ToArray().All(IsProbability));
        Assert.IsTrue(result.MarkerLikeProbabilities.Span[(10 * scene.Width) + 36] >= SuppressionThreshold);
        Assert.IsTrue(result.ThinConnectorProbabilities.Span[(22 * scene.Width) + 28] >= SuppressionThreshold);
        Assert.AreEqual(0f, result.MarkerLikeProbabilities.Span[(12 * scene.Width) + 18]);
        Assert.AreEqual(0f, result.ThinConnectorProbabilities.Span[(12 * scene.Width) + 18]);
        CollectionAssert.AreEqual(pixelsBefore, scene.Pixels);
        CollectionAssert.AreEqual(geometryBefore, scene.GeometryMask);
    }

    [TestMethod]
    public async Task JoinedMarkersAndConnectorRemainDistinctInTheirProbabilityPlanes()
    {
        var scene = new SyntheticScene(56, 32);
        scene.DrawFilledMarker(8, 12);
        scene.DrawFilledMarker(40, 12);
        scene.DrawHorizontalConnector(12, 40, 14);
        var provider = new RasterPreOcrStructuralProbabilityProvider();

        PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
            scene.CreateFrame(),
            CancellationToken.None);

        foreach (int markerCenter in new[]
                 {
                     (14 * scene.Width) + 10,
                     (14 * scene.Width) + 42,
                 })
        {
            Assert.IsTrue(result.MarkerLikeProbabilities.Span[markerCenter] >= SuppressionThreshold);
            Assert.IsTrue(result.ThinConnectorProbabilities.Span[markerCenter] < SuppressionThreshold);
        }

        int connectorCenter = (14 * scene.Width) + 26;
        Assert.IsTrue(result.MarkerLikeProbabilities.Span[connectorCenter] < SuppressionThreshold);
        Assert.IsTrue(result.ThinConnectorProbabilities.Span[connectorCenter] >= SuppressionThreshold);
    }

    [TestMethod]
    public async Task FocusedSyntheticUnitCorpusMeetsPreservationBarAndLeavesAmbiguousOpenCircleUnsuppressed()
    {
        SyntheticScene[] scenes =
        [
            CreateMixedScene(0, 0, 0),
            CreateMixedScene(2, 1, 35),
            CreateMixedScene(1, 2, 70),
        ];
        var provider = new RasterPreOcrStructuralProbabilityProvider();
        long retainedText = 0;
        long retainedInk = 0;
        long textInk = 0;
        long suppressedStructure = 0;
        long unambiguousStructure = 0;

        foreach (SyntheticScene scene in scenes)
        {
            PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
                scene.CreateFrame(),
                CancellationToken.None);
            ReadOnlySpan<float> marker = result.MarkerLikeProbabilities.Span;
            ReadOnlySpan<float> connector = result.ThinConnectorProbabilities.Span;
            for (int index = 0; index < scene.Pixels.Length; index++)
            {
                if (scene.GeometryMask[index] != 0 || scene.Pixels[index] >= 230)
                {
                    continue;
                }

                bool suppressed = Math.Max(marker[index], connector[index]) >= SuppressionThreshold;
                if (!suppressed)
                {
                    retainedInk++;
                }

                if (scene.TextTruth[index])
                {
                    textInk++;
                    if (!suppressed)
                    {
                        retainedText++;
                    }
                }

                if (scene.UnambiguousStructureTruth[index])
                {
                    unambiguousStructure++;
                    if (suppressed)
                    {
                        suppressedStructure++;
                    }
                }
            }

            foreach (int openCirclePixel in scene.AmbiguousOpenCirclePixels)
            {
                Assert.IsTrue(
                    Math.Max(marker[openCirclePixel], connector[openCirclePixel]) < SuppressionThreshold,
                    "An isolated open circle is pixel-identical to an isolated O and must remain low-confidence.");
            }
        }

        double preservationPrecision = retainedText / (double)retainedInk;
        double preservationRecall = retainedText / (double)textInk;
        double unambiguousStructureRecall = suppressedStructure / (double)unambiguousStructure;
        TestContext?.WriteLine(
            $"text_preservation_precision={preservationPrecision:R}; " +
            $"text_preservation_recall={preservationRecall:R}; " +
            $"unambiguous_structure_recall={unambiguousStructureRecall:R}; " +
            $"text_pixels={textInk}; retained_ink_pixels={retainedInk}");

        Assert.IsGreaterThanOrEqualTo(0.95, preservationPrecision);
        Assert.IsGreaterThanOrEqualTo(0.95, preservationRecall);
        Assert.IsGreaterThanOrEqualTo(0.95, unambiguousStructureRecall);
    }

    [TestMethod]
    public async Task ScaledVariableGapAndRotatedLabelsRemainPreserved()
    {
        var horizontal = new SyntheticScene(360, 150);
        horizontal.DrawText("O01 -10.5%", 8, 8, 70, scale: 2, gap: 3);
        horizontal.DrawText("GENERALIZATION", 8, 44, 130, scale: 2, gap: 4, degraded: true);
        var rotated = new SyntheticScene(150, 180);
        rotated.DrawText("1IT", 16, 8, 115, scale: 3, gap: 5, rotatedClockwise: true);
        var provider = new RasterPreOcrStructuralProbabilityProvider();
        long preserved = 0;
        long text = 0;

        foreach (SyntheticScene scene in new[] { horizontal, rotated })
        {
            PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
                scene.CreateFrame(),
                CancellationToken.None);
            for (int index = 0; index < scene.TextTruth.Length; index++)
            {
                if (!scene.TextTruth[index])
                {
                    continue;
                }

                text++;
                bool suppressed = Math.Max(
                    result.MarkerLikeProbabilities.Span[index],
                    result.ThinConnectorProbabilities.Span[index]) >= SuppressionThreshold;
                if (!suppressed)
                {
                    preserved++;
                }
            }
        }

        double recall = preserved / (double)text;
        TestContext?.WriteLine($"scaled_rotated_text_preservation_recall={recall:R}; text_pixels={text}");
        Assert.IsGreaterThanOrEqualTo(0.95, recall);
    }

    [TestMethod]
    public async Task AnchoredDenseCompactGlyphRunsPreserveRepeatedDigitsAndLetters()
    {
        var scene = new SyntheticScene(64, 40);
        scene.DrawDenseText("ABB", 4, 4, 0);
        scene.DrawDenseText("100", 4, 16, 0);
        scene.DrawDenseText("80", 4, 28, 0);
        var provider = new RasterPreOcrStructuralProbabilityProvider();

        PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
            scene.CreateFrame(),
            CancellationToken.None);

        int textPixels = 0;
        for (int index = 0; index < scene.TextTruth.Length; index++)
        {
            if (!scene.TextTruth[index])
            {
                continue;
            }

            textPixels++;
            Assert.IsTrue(
                Math.Max(
                    result.MarkerLikeProbabilities.Span[index],
                    result.ThinConnectorProbabilities.Span[index]) < SuppressionThreshold,
                $"Dense compact glyph pixel {index} was classified as suppressible structure.");
        }

        Assert.IsGreaterThan(0, textPixels);
    }

    [TestMethod]
    public async Task UnanchoredDenseStandaloneGlyphRemainsMarkerAmbiguous()
    {
        var scene = new SyntheticScene(24, 20);
        scene.DrawDenseText("0", 8, 6, 0);
        var provider = new RasterPreOcrStructuralProbabilityProvider();

        PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
            scene.CreateFrame(),
            CancellationToken.None);

        foreach (int textPixel in scene.TextTruth
                     .Select((isText, index) => (isText, index))
                     .Where(item => item.isText)
                     .Select(item => item.index))
        {
            Assert.IsTrue(
                result.MarkerLikeProbabilities.Span[textPixel] >= SuppressionThreshold,
                "A standalone dense glyph remains indistinguishable from an isolated filled marker.");
        }
    }

    [TestMethod]
    public async Task EquivalentAlignedFilledMarkersRemainSuppressibleWithoutTextAnchor()
    {
        var scene = new SyntheticScene(40, 28);
        scene.DrawFilledMarker(6, 10);
        scene.DrawFilledMarker(12, 10);
        scene.DrawFilledMarker(18, 10);
        var provider = new RasterPreOcrStructuralProbabilityProvider();

        PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
            scene.CreateFrame(),
            CancellationToken.None);

        foreach (int markerCenter in new[]
                 {
                     (12 * scene.Width) + 8,
                     (12 * scene.Width) + 14,
                     (12 * scene.Width) + 20,
                 })
        {
            Assert.IsTrue(result.MarkerLikeProbabilities.Span[markerCenter] >= SuppressionThreshold);
            Assert.IsTrue(result.ThinConnectorProbabilities.Span[markerCenter] < SuppressionThreshold);
        }
    }

    [TestMethod]
    public async Task HeterogeneousAdjacentCompactMarkersRemainRetainedAsKnownLeakage()
    {
        var scene = new SyntheticScene(40, 36);
        scene.DrawAmbiguousFilledMarker(6, 4);
        scene.DrawAmbiguousDifferentFilledMarker(12, 4);
        scene.DrawAmbiguousFilledMarker(6, 22);
        scene.DrawAmbiguousDegradedFilledMarker(12, 22);
        var provider = new RasterPreOcrStructuralProbabilityProvider();

        PreOcrStructuralProbabilityResult result = await provider.AnalyzeAsync(
            scene.CreateFrame(),
            CancellationToken.None);

        foreach (int structurePixel in scene.AmbiguousCompactStructurePixels)
        {
            Assert.IsTrue(
                Math.Max(
                    result.MarkerLikeProbabilities.Span[structurePixel],
                    result.ThinConnectorProbabilities.Span[structurePixel]) < SuppressionThreshold,
                "Known retained-structure leakage: shape-different compact markers resemble a dense glyph run.");
        }
    }

    [TestMethod]
    public async Task AnalyzeAsyncRejectsInvalidCoordinateSpaceAndHonorsCancellation()
    {
        var scene = new SyntheticScene(16, 16);
        var provider = new RasterPreOcrStructuralProbabilityProvider();
        PreOcrStructuralFrame wrongSpace = scene.CreateFrame() with
        {
            CoordinateSpace = "enhanced_pixels",
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await provider.AnalyzeAsync(wrongSpace, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await provider.AnalyzeAsync(scene.CreateFrame(), cancellation.Token));
    }

    [TestMethod]
    public async Task AnalyzeAsyncCancelsPromptlyDuringLargeConnectedComponentTraversal()
    {
        const int sideLength = 4_096;
        var scene = new SyntheticScene(sideLength, sideLength);
        Array.Fill(scene.Pixels, (byte)0);
        var provider = new RasterPreOcrStructuralProbabilityProvider();
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Task operation = Task.Run(async () =>
        {
            started.Set();
            await provider.AnalyzeAsync(scene.CreateFrame(), cancellation.Token);
        });

        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.IsFalse(operation.IsCompleted, "The fixture must still be traversing when cancellation is requested.");

        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        Task completed = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromMilliseconds(100)));
        stopwatch.Stop();

        Assert.AreSame(operation, completed, "Cancellation was not observed within the bounded polling interval.");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await operation);
        TestContext?.WriteLine($"large_component_cancel_latency_ms={stopwatch.Elapsed.TotalMilliseconds:R}");
    }

    public TestContext? TestContext { get; set; }

    private static SyntheticScene CreateMixedScene(int offsetX, int offsetY, byte fade)
    {
        var scene = new SyntheticScene(176, 76);
        scene.DrawText("O01 -10.5%", 4 + offsetX, 4 + offsetY, (byte)(20 + fade));
        scene.DrawText("GENERALIZATION", 4 + offsetX, 18 + offsetY, (byte)(55 + fade));
        scene.DrawText("-1.0% 0 O 1", 4 + offsetX, 32 + offsetY, (byte)(85 + fade), degraded: true);
        scene.DrawFilledMarker(118 + offsetX, 47 + offsetY);
        scene.DrawFilledMarker(146 + offsetX, 47 + offsetY);
        scene.DrawHorizontalConnector(122 + offsetX, 146 + offsetX, 49 + offsetY);
        scene.DrawDiagonalConnector(112 + offsetX, 66 + offsetY, 24);
        scene.DrawAmbiguousOpenCircle(156 + offsetX, 60 + offsetY);
        scene.DrawKnownGeometryLine(104 + offsetX);
        return scene;
    }

    private static bool IsProbability(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    private sealed class SyntheticScene
    {
        private static readonly Dictionary<char, string[]> Glyphs =
            new Dictionary<char, string[]>
            {
                [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
                ['-'] = ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
                ['.'] = ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
                ['%'] = ["11001", "11010", "00100", "00100", "01000", "10110", "00110"],
                ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
                ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
                ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
                ['8'] = ["01110", "11011", "11011", "01110", "11011", "11011", "01110"],
                ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
                ['B'] = ["11110", "11011", "11011", "11110", "11011", "11011", "11110"],
                ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
                ['G'] = ["01110", "10001", "10000", "10111", "10001", "10001", "01110"],
                ['I'] = ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
                ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
                ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
                ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
                ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
                ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
                ['Z'] = ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
            };

        private static readonly Dictionary<char, string[]> DenseGlyphs =
            new Dictionary<char, string[]>
            {
                ['0'] = ["01110", "11011", "11011", "11101", "11011", "11011", "01110"],
                ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
                ['8'] = ["01110", "11011", "11011", "01110", "11011", "11011", "01110"],
                ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
                ['B'] = ["11110", "11011", "11011", "11110", "11011", "11011", "11110"],
            };

        public SyntheticScene(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = Enumerable.Repeat(byte.MaxValue, checked(width * height)).ToArray();
            GeometryMask = new byte[Pixels.Length];
            TextTruth = new bool[Pixels.Length];
            UnambiguousStructureTruth = new bool[Pixels.Length];
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Pixels { get; }

        public byte[] GeometryMask { get; }

        public bool[] TextTruth { get; }

        public bool[] UnambiguousStructureTruth { get; }

        public List<int> AmbiguousOpenCirclePixels { get; } = [];

        public List<int> AmbiguousCompactStructurePixels { get; } = [];

        public PreOcrStructuralFrame CreateFrame() => new(
            Width,
            Height,
            Width,
            Pixels,
            Width,
            GeometryMask);

        public void DrawText(
            string text,
            int left,
            int top,
            byte ink,
            bool degraded = false,
            int scale = 1,
            int gap = 1,
            bool rotatedClockwise = false)
        {
            Assert.IsGreaterThan(0, scale);
            Assert.IsGreaterThanOrEqualTo(0, gap);
            int cursor = rotatedClockwise ? top : left;
            int ordinal = 0;
            foreach (char character in text)
            {
                string[] glyph = Glyphs[character];
                for (int y = 0; y < glyph.Length; y++)
                {
                    for (int x = 0; x < glyph[y].Length; x++)
                    {
                        if (glyph[y][x] != '1' || (degraded && ((x + y + ordinal) % 13 == 0)))
                        {
                            continue;
                        }

                        for (int scaleY = 0; scaleY < scale; scaleY++)
                        {
                            for (int scaleX = 0; scaleX < scale; scaleX++)
                            {
                                int destinationX = rotatedClockwise
                                    ? left + ((glyph.Length - 1 - y) * scale) + scaleX
                                    : cursor + (x * scale) + scaleX;
                                int destinationY = rotatedClockwise
                                    ? cursor + (x * scale) + scaleY
                                    : top + (y * scale) + scaleY;
                                SetInk(destinationX, destinationY, ink, TextTruth);
                            }
                        }
                    }
                }

                cursor += (5 * scale) + gap;
                ordinal++;
            }
        }

        public void DrawDenseText(string text, int left, int top, byte ink)
        {
            int cursor = left;
            foreach (char character in text)
            {
                string[] glyph = DenseGlyphs[character];
                for (int y = 0; y < glyph.Length; y++)
                {
                    for (int x = 0; x < glyph[y].Length; x++)
                    {
                        if (glyph[y][x] == '1')
                        {
                            SetInk(cursor + x, top + y, ink, TextTruth);
                        }
                    }
                }

                cursor += 6;
            }
        }

        public void DrawFilledMarker(int left, int top)
        {
            string[] marker = ["01110", "11111", "11111", "11111", "01110"];
            DrawPattern(marker, left, top, 0, UnambiguousStructureTruth);
        }

        public void DrawAmbiguousFilledMarker(int left, int top)
        {
            string[] marker = ["01110", "11111", "11111", "11111", "01110"];
            DrawPattern(marker, left, top, 0, null, AmbiguousCompactStructurePixels);
        }

        public void DrawAmbiguousDifferentFilledMarker(int left, int top)
        {
            string[] marker = ["11111", "11011", "10101", "11011", "11111"];
            DrawPattern(marker, left, top, 0, null, AmbiguousCompactStructurePixels);
        }

        public void DrawAmbiguousDegradedFilledMarker(int left, int top)
        {
            string[] marker = ["01010", "11111", "11111", "11111", "01110"];
            DrawPattern(marker, left, top, 0, null, AmbiguousCompactStructurePixels);
        }

        public void DrawAmbiguousOpenCircle(int left, int top)
        {
            string[] marker = ["01110", "10001", "10001", "10001", "01110"];
            DrawPattern(marker, left, top, 0, null, AmbiguousOpenCirclePixels);
        }

        public void DrawHorizontalConnector(int left, int right, int y)
        {
            for (int x = left; x <= right; x++)
            {
                SetInk(x, y, 0, UnambiguousStructureTruth);
            }
        }

        public void DrawDiagonalConnector(int left, int top, int length)
        {
            for (int offset = 0; offset < length; offset++)
            {
                SetInk(left + offset, top - (offset / 2), 25, UnambiguousStructureTruth);
            }
        }

        public void DrawKnownGeometryLine(int x)
        {
            for (int y = 2; y < Height - 2; y++)
            {
                int index = (y * Width) + x;
                Pixels[index] = 0;
                GeometryMask[index] = byte.MaxValue;
            }
        }

        private void DrawPattern(
            string[] pattern,
            int left,
            int top,
            byte ink,
            bool[]? truth,
            List<int>? indices = null)
        {
            for (int y = 0; y < pattern.Length; y++)
            {
                for (int x = 0; x < pattern[y].Length; x++)
                {
                    if (pattern[y][x] != '1')
                    {
                        continue;
                    }

                    int index = SetInk(left + x, top + y, ink, truth);
                    indices?.Add(index);
                }
            }
        }

        private int SetInk(int x, int y, byte ink, bool[]? truth)
        {
            Assert.IsTrue(x >= 0 && x < Width && y >= 0 && y < Height);
            int index = (y * Width) + x;
            Pixels[index] = Math.Min(Pixels[index], ink);
            if (truth is not null)
            {
                truth[index] = true;
            }

            return index;
        }
    }
}
