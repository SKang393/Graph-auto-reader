// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GraphReader.App.Integration.Workflow;
using GraphReader.Pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionRasterPanelizerTests
{
    private static readonly int[] ExpectedPanelOrder = [1, 2, 3];

    [TestMethod]
    public async Task StackedPngProducesStableOwnedCropsAndExactSourceMappings()
    {
        byte[] mutableSource = CreateStackedGraphPng(width: 640, height: 900);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(mutableSource));
        var immutableSource = new ImmutableByteBuffer(mutableSource);
        mutableSource[0] = 0;
        var panelizer = new ProductionRasterPanelizer();

        ProductionRasterPanelizationResult first = await panelizer.PanelizeAsync(
            immutableSource,
            sourceSha256,
            sourceWidth: 640,
            sourceHeight: 900,
            CancellationToken.None);
        ProductionRasterPanelizationResult second = await panelizer.PanelizeAsync(
            immutableSource,
            sourceSha256,
            sourceWidth: 640,
            sourceHeight: 900,
            CancellationToken.None);

        Assert.HasCount(3, first.Panels);
        CollectionAssert.AreEqual(ExpectedPanelOrder, first.Panels.Select(static panel => panel.Order).ToArray());
        CollectionAssert.AreEqual(
            first.Panels.Select(static panel => panel.PanelId).ToArray(),
            second.Panels.Select(static panel => panel.PanelId).ToArray());
        Assert.AreEqual(3, first.Panels.Select(static panel => panel.PanelId).Distinct().Count());
        Assert.AreEqual(0d, first.Panels[0].EncodedCropInSourcePixels.Y);
        Assert.AreEqual(900d, first.Panels[^1].EncodedCropInSourcePixels.Bottom);

        for (int index = 0; index < first.Panels.Count; index++)
        {
            ProductionRasterPanel panel = first.Panels[index];
            byte[] cropBytes = panel.CopyEncodedBytes();
            Assert.AreEqual(panel.Sha256, Convert.ToHexStringLower(SHA256.HashData(cropBytes)));
            Assert.AreEqual(panel.Width, panel.EncodedCropInSourcePixels.Width);
            Assert.AreEqual(panel.Height, panel.EncodedCropInSourcePixels.Height);
            Assert.IsGreaterThanOrEqualTo(0d, panel.EncodedCropInSourcePixels.X);
            Assert.IsGreaterThanOrEqualTo(0d, panel.EncodedCropInSourcePixels.Y);
            Assert.IsLessThanOrEqualTo(640d, panel.EncodedCropInSourcePixels.Right);
            Assert.IsLessThanOrEqualTo(900d, panel.EncodedCropInSourcePixels.Bottom);
            Assert.AreEqual(0d, panel.EncodedCropInSourcePixels.X);
            Assert.AreEqual(640d, panel.EncodedCropInSourcePixels.Width);

            var sourcePoint = new PdfPointD(
                panel.EncodedCropInSourcePixels.X + 10.25d,
                panel.EncodedCropInSourcePixels.Y + 12.5d);
            PdfPointD panelPoint = panel.MapSourceToPanel(sourcePoint);
            Assert.AreEqual(10.25d, panelPoint.X, 1e-9);
            Assert.AreEqual(12.5d, panelPoint.Y, 1e-9);
            Assert.AreEqual(sourcePoint, panel.MapPanelToSource(panelPoint));
            CollectionAssert.AreEqual(
                new[]
                {
                    1d, 0d, -panel.EncodedCropInSourcePixels.X,
                    0d, 1d, -panel.EncodedCropInSourcePixels.Y,
                    0d, 0d, 1d,
                },
                panel.SourceToPanelMatrix.ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    1d, 0d, panel.EncodedCropInSourcePixels.X,
                    0d, 1d, panel.EncodedCropInSourcePixels.Y,
                    0d, 0d, 1d,
                },
                panel.PanelToSourceMatrix.ToArray());

            cropBytes[0] = 0;
            Assert.AreNotEqual(0, panel.CopyEncodedBytes()[0]);
            if (index > 0)
            {
                Assert.AreEqual(
                    first.Panels[index - 1].EncodedCropInSourcePixels.Bottom,
                    panel.EncodedCropInSourcePixels.Y);
            }
        }
    }

    [TestMethod]
    public async Task SingleRasterFigureRetainsDisconnectedTopBottomAndRightMarginGlyphs()
    {
        const int width = 1200;
        const int height = 350;
        byte[] source = CreateGraphWithDisconnectedMarginGlyphsPng(width, height);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));

        ProductionRasterPanelizationResult result = await new ProductionRasterPanelizer().PanelizeAsync(
            new ImmutableByteBuffer(source),
            sourceSha256,
            width,
            height,
            CancellationToken.None);

        Assert.HasCount(1, result.Panels);
        ProductionRasterPanel panel = result.Panels[0];
        Assert.AreEqual(new PdfRectD(0d, 0d, width, height), panel.RequestedCropInSourcePixels);
        Assert.AreEqual(new PdfRectD(0d, 0d, width, height), panel.EncodedCropInSourcePixels);
        Assert.AreEqual(new PdfPointD(1170d, 150d), panel.MapPanelToSource(new PdfPointD(1170d, 150d)));
        Assert.AreEqual(new PdfPointD(350d, 330d), panel.MapSourceToPanel(new PdfPointD(350d, 330d)));
        CollectionAssert.AreEqual(source, panel.CopyEncodedBytes(),
            "A full-source crop must preserve the exact immutable source PNG bytes.");
    }

    [TestMethod]
    public async Task OddPixelPlotGapUsesOneSharedNonoverlappingEncodedBoundary()
    {
        const int width = 640;
        const int height = 600;
        byte[] source = CreateOddGapStackedGraphPng(width, height);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));

        ProductionRasterPanelizationResult result = await new ProductionRasterPanelizer().PanelizeAsync(
            new ImmutableByteBuffer(source),
            sourceSha256,
            width,
            height,
            CancellationToken.None);

        Assert.HasCount(2, result.Panels);
        Assert.AreEqual(0d, result.Panels[0].EncodedCropInSourcePixels.Y);
        Assert.AreEqual(height, result.Panels[1].EncodedCropInSourcePixels.Bottom);
        Assert.AreEqual(
            result.Panels[0].EncodedCropInSourcePixels.Bottom,
            result.Panels[1].EncodedCropInSourcePixels.Y,
            "Both encoded crops must use the same integer source-pixel boundary.");
        Assert.AreEqual(
            result.Panels[0].RequestedCropInSourcePixels.Bottom,
            result.Panels[1].RequestedCropInSourcePixels.Y);
    }

    [TestMethod]
    public async Task ChecksumMismatchFailsBeforePanelization()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900);
        var panelizer = new ProductionRasterPanelizer();

        ProductionRasterPanelizationException exception = await Assert.ThrowsExactlyAsync<ProductionRasterPanelizationException>(
            () => panelizer.PanelizeAsync(
                new ImmutableByteBuffer(source),
                new string('0', 64),
                640,
                900,
                CancellationToken.None));

        Assert.AreEqual(ProductionRasterPanelizationFailureCodes.ChecksumMismatch, exception.Code);
    }

    [TestMethod]
    public async Task DeclaredDimensionMismatchFailsBeforePanelization()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        var panelizer = new ProductionRasterPanelizer();

        ProductionRasterPanelizationException exception = await Assert.ThrowsExactlyAsync<ProductionRasterPanelizationException>(
            () => panelizer.PanelizeAsync(
                new ImmutableByteBuffer(source),
                sourceSha256,
                sourceWidth: 639,
                sourceHeight: 900,
                CancellationToken.None));

        Assert.AreEqual(ProductionRasterPanelizationFailureCodes.DimensionMismatch, exception.Code);
    }

    [TestMethod]
    public async Task BlankRasterFailsVisiblyWithoutWholeImageFallback()
    {
        byte[] source = EncodeGrayscalePng(640, 900, CreateWhiteScanlines(640, 900));
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        var panelizer = new ProductionRasterPanelizer();

        ProductionRasterPanelizationException exception = await Assert.ThrowsExactlyAsync<ProductionRasterPanelizationException>(
            () => panelizer.PanelizeAsync(
                new ImmutableByteBuffer(source),
                sourceSha256,
                640,
                900,
                CancellationToken.None));

        Assert.AreEqual(ProductionRasterPanelizationFailureCodes.NoPanelDetected, exception.Code);
        Assert.IsTrue(exception.Warnings.Any(static warning =>
            warning.Contains("No graph-like figure", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TruncatedSourceFailsCompleteDecodeBeforeInjectedProposalIsRead()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900)[..^12];
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        var engine = new FakePanelizationEngine(CreatePanelizationResult(source, 640, 900));
        var panelizer = new ProductionRasterPanelizer(engine);

        ProductionRasterPanelizationException exception = await Assert.ThrowsExactlyAsync<ProductionRasterPanelizationException>(
            () => panelizer.PanelizeAsync(
                new ImmutableByteBuffer(source),
                sourceSha256,
                640,
                900,
                CancellationToken.None));

        Assert.AreEqual(ProductionRasterPanelizationFailureCodes.InvalidSource, exception.Code);
        Assert.AreEqual(0, engine.CallCount);
    }

    [TestMethod]
    public async Task CorruptSourceFailsCompleteDecodeBeforeInjectedProposalIsRead()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900);
        int idatType = FindChunkType(source, "IDAT");
        source[idatType + 4] ^= 0xff;
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        var engine = new FakePanelizationEngine(CreatePanelizationResult(source, 640, 900));
        var panelizer = new ProductionRasterPanelizer(engine);

        ProductionRasterPanelizationException exception = await Assert.ThrowsExactlyAsync<ProductionRasterPanelizationException>(
            () => panelizer.PanelizeAsync(
                new ImmutableByteBuffer(source),
                sourceSha256,
                640,
                900,
                CancellationToken.None));

        Assert.AreEqual(ProductionRasterPanelizationFailureCodes.InvalidSource, exception.Code);
        Assert.AreEqual(0, engine.CallCount);
    }

    [TestMethod]
    public async Task FractionalRequestedCropReportsActualEncodedBoundsAndBackMapping()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        var requested = new PdfRectD(10.25d, 20.5d, 100.1d, 50.2d);
        var panelizer = new ProductionRasterPanelizer(
            new FakePanelizationEngine(CreatePanelizationResult(source, 640, 900, requested)));

        ProductionRasterPanelizationResult result = await panelizer.PanelizeAsync(
            new ImmutableByteBuffer(source),
            sourceSha256,
            640,
            900,
            CancellationToken.None);

        Assert.HasCount(1, result.Panels);
        ProductionRasterPanel panel = result.Panels[0];
        Assert.AreEqual(requested, panel.RequestedCropInSourcePixels);
        Assert.AreEqual(new PdfRectD(10d, 20d, 101d, 51d), panel.EncodedCropInSourcePixels);
        Assert.AreEqual(new PdfPointD(10d, 20d), panel.MapPanelToSource(new PdfPointD(0d, 0d)));
        Assert.AreEqual(new PdfPointD(0d, 0d), panel.MapSourceToPanel(new PdfPointD(10d, 20d)));
    }

    [TestMethod]
    public async Task OverlappingInjectedPanelCropsAreRejected()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        var panelizer = new ProductionRasterPanelizer(new FakePanelizationEngine(
            CreatePanelizationResult(
                source,
                640,
                900,
                new PdfRectD(10d, 10d, 300d, 300d),
                new PdfRectD(250d, 250d, 300d, 300d))));

        ProductionRasterPanelizationException exception = await Assert.ThrowsExactlyAsync<ProductionRasterPanelizationException>(
            () => panelizer.PanelizeAsync(
                new ImmutableByteBuffer(source),
                sourceSha256,
                640,
                900,
                CancellationToken.None));

        Assert.AreEqual(ProductionRasterPanelizationFailureCodes.PanelUnavailable, exception.Code);
        StringAssert.Contains(exception.Message, "overlapping requested source crops");
    }

    [TestMethod]
    public async Task PanelReferencingForeignFigureIsRejected()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        PdfPanelizationResult valid = CreatePanelizationResult(source, 640, 900);
        PdfPanelRecord panel = valid.Panels[0];
        var foreignPanel = new PdfPanelRecord(
            panel.PanelId,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            panel.PageNumber,
            panel.Order,
            panel.CropInSourcePixels,
            panel.BoundsPagePixels,
            panel.BoundsPagePoints,
            panel.ParticipantLabel,
            panel.Caption,
            panel.SemanticSuggestions,
            panel.Evidence,
            panel.Confidence,
            panel.CropInSourcePixelsQuadrilateral);
        var panelizer = new ProductionRasterPanelizer(new FakePanelizationEngine(
            new PdfPanelizationResult(valid.Figures, [foreignPanel])));

        ProductionRasterPanelizationException exception = await Assert.ThrowsExactlyAsync<ProductionRasterPanelizationException>(
            () => panelizer.PanelizeAsync(
                new ImmutableByteBuffer(source),
                sourceSha256,
                640,
                900,
                CancellationToken.None));

        Assert.AreEqual(ProductionRasterPanelizationFailureCodes.PanelUnavailable, exception.Code);
        StringAssert.Contains(exception.Message, "without its figure");
    }

    [TestMethod]
    public async Task CancellationAfterProposalStopsBeforeCropEncoding()
    {
        byte[] source = CreateStackedGraphPng(width: 640, height: 900);
        string sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source));
        using var cancellation = new CancellationTokenSource();
        var engine = new CancelingPanelizationEngine(
            CreatePanelizationResult(source, 640, 900),
            cancellation);
        var panelizer = new ProductionRasterPanelizer(engine);

        await Assert.ThrowsAsync<OperationCanceledException>(() => panelizer.PanelizeAsync(
            new ImmutableByteBuffer(source),
            sourceSha256,
            640,
            900,
            cancellation.Token));

        Assert.AreEqual(1, engine.CallCount);
    }

    private static PdfPanelizationResult CreatePanelizationResult(
        byte[] source,
        int width,
        int height,
        params PdfRectD[] requestedCrops)
    {
        Guid figureId = Guid.Parse("81111111-1111-1111-1111-111111111111");
        var figure = new PdfFigureCandidate(
            figureId,
            pageNumber: 1,
            PdfFigureSourceKind.RenderedPage,
            embeddedImageId: null,
            new PdfRectD(0d, 0d, width, height),
            new PdfRectD(0d, 0d, width / 2d, height / 2d),
            width,
            height,
            new ImmutableByteBuffer(source),
            "image/png",
            caption: null,
            [new PdfPanelEvidence(PdfPanelEvidenceKind.DenseLineStructure, 0.56d, "synthetic fixture")],
            confidence: 0.56d);
        PdfRectD[] crops = requestedCrops.Length == 0
            ? [new PdfRectD(0d, 0d, width, height)]
            : requestedCrops;
        PdfPanelRecord[] panels = crops.Select((crop, index) => new PdfPanelRecord(
            Guid.Parse($"82222222-2222-2222-2222-{index + 1:D12}"),
            figureId,
            pageNumber: 1,
            order: index + 1,
            crop,
            crop,
            new PdfRectD(crop.X / 2d, crop.Y / 2d, crop.Width / 2d, crop.Height / 2d),
            participantLabel: null,
            caption: null,
            semanticSuggestions: [],
            figure.Evidence,
            figure.Confidence)).ToArray();
        return new PdfPanelizationResult([figure], panels);
    }

    private static int FindChunkType(byte[] png, string type)
    {
        byte[] target = Encoding.ASCII.GetBytes(type);
        int offset = 8;
        while (offset <= png.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > int.MaxValue || (long)offset + 12L + length > png.Length)
            {
                break;
            }

            int typeOffset = offset + 4;
            if (png.AsSpan(typeOffset, target.Length).SequenceEqual(target))
            {
                return typeOffset;
            }

            offset = checked(offset + 12 + (int)length);
        }

        throw new InvalidOperationException($"PNG chunk '{type}' was not found.");
    }

    private static byte[] CreateStackedGraphPng(int width, int height)
    {
        byte[] scanlines = CreateWhiteScanlines(width, height);
        int[] baselines = [270, 530, 790];
        foreach (int baseline in baselines)
        {
            DrawHorizontal(scanlines, width, height, 80, 580, baseline, thickness: 2);
            DrawVertical(scanlines, width, height, 80, baseline - 180, baseline, thickness: 2);
            DrawHorizontal(scanlines, width, height, 120, 220, baseline - 60, thickness: 1);
            DrawHorizontal(scanlines, width, height, 220, 340, baseline - 110, thickness: 1);
            DrawHorizontal(scanlines, width, height, 340, 460, baseline - 80, thickness: 1);
            DrawVertical(scanlines, width, height, 300, baseline - 150, baseline, thickness: 1);
        }

        return EncodeGrayscalePng(width, height, scanlines);
    }

    private static byte[] CreateGraphWithDisconnectedMarginGlyphsPng(int width, int height)
    {
        byte[] scanlines = CreateWhiteScanlines(width, height);
        DrawHorizontal(scanlines, width, height, 105, 960, 260, thickness: 2);
        DrawVertical(scanlines, width, height, 105, 96, 260, thickness: 2);
        DrawHorizontal(scanlines, width, height, 220, 420, 205, thickness: 1);
        DrawHorizontal(scanlines, width, height, 450, 690, 160, thickness: 1);
        DrawHorizontal(scanlines, width, height, 400, 520, 24, thickness: 2);
        DrawHorizontal(scanlines, width, height, 350, 470, 330, thickness: 2);
        DrawVertical(scanlines, width, height, 1170, 105, 150, thickness: 2);
        return EncodeGrayscalePng(width, height, scanlines);
    }

    private static byte[] CreateOddGapStackedGraphPng(int width, int height)
    {
        byte[] scanlines = CreateWhiteScanlines(width, height);
        (int Top, int Baseline)[] plots = [(80, 250), (331, 501)];
        foreach ((int top, int baseline) in plots)
        {
            DrawHorizontal(scanlines, width, height, 80, 580, baseline, thickness: 2);
            DrawVertical(scanlines, width, height, 80, top, baseline, thickness: 2);
            DrawHorizontal(scanlines, width, height, 140, 330, baseline - 70, thickness: 2);
        }

        return EncodeGrayscalePng(width, height, scanlines);
    }

    private static byte[] CreateWhiteScanlines(int width, int height)
    {
        byte[] scanlines = new byte[height * (width + 1)];
        Array.Fill(scanlines, byte.MaxValue);
        for (var y = 0; y < height; y++)
        {
            scanlines[y * (width + 1)] = 0;
        }

        return scanlines;
    }

    private static void DrawHorizontal(
        byte[] scanlines,
        int width,
        int height,
        int xMinimum,
        int xMaximum,
        int y,
        int thickness)
    {
        for (var offset = 0; offset < thickness; offset++)
        {
            int row = Math.Clamp(y + offset, 0, height - 1);
            for (int x = Math.Max(0, xMinimum); x <= Math.Min(width - 1, xMaximum); x++)
            {
                scanlines[(row * (width + 1)) + x + 1] = 0;
            }
        }
    }

    private static void DrawVertical(
        byte[] scanlines,
        int width,
        int height,
        int x,
        int yMinimum,
        int yMaximum,
        int thickness)
    {
        for (var offset = 0; offset < thickness; offset++)
        {
            int column = Math.Clamp(x + offset, 0, width - 1);
            for (int y = Math.Max(0, yMinimum); y <= Math.Min(height - 1, yMaximum); y++)
            {
                scanlines[(y * (width + 1)) + column + 1] = 0;
            }
        }
    }

    private static byte[] EncodeGrayscalePng(int width, int height, byte[] scanlines)
    {
        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(scanlines);
            }

            compressed = compressedStream.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 0;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed);
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        target.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        byte[] checksumInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(checksumInput, 0);
        data.CopyTo(checksumInput.AsSpan(typeBytes.Length));
        Span<byte> checksum = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ComputePngCrc32(checksumInput));
        target.Write(checksum);
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xedb88320u & mask);
            }
        }

        return ~crc;
    }

    private sealed class FakePanelizationEngine : IPdfPanelizationEngine
    {
        private readonly PdfPanelizationResult result;

        public FakePanelizationEngine(PdfPanelizationResult result) => this.result = result;

        public int CallCount { get; private set; }

        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }

        public PdfPanelizationResult ApplySplit(
            PdfPanelizationResult current,
            PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(
            PdfPanelizationResult current,
            PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }

    private sealed class CancelingPanelizationEngine : IPdfPanelizationEngine
    {
        private readonly PdfPanelizationResult result;
        private readonly CancellationTokenSource cancellation;

        public CancelingPanelizationEngine(
            PdfPanelizationResult result,
            CancellationTokenSource cancellation)
        {
            this.result = result;
            this.cancellation = cancellation;
        }

        public int CallCount { get; private set; }

        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellation.Cancel();
            return Task.FromResult(result);
        }

        public PdfPanelizationResult ApplySplit(
            PdfPanelizationResult current,
            PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(
            PdfPanelizationResult current,
            PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }
}
