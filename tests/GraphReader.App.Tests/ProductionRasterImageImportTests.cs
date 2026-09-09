// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionRasterImageImportTests
{
    private const int SourceWidth = 120;
    private const int SourceHeight = 180;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly PdfRectD[] StackedCrops =
    [
        new PdfRectD(8.25d, 10.5d, 103.5d, 43.25d),
        new PdfRectD(8.25d, 68.5d, 103.5d, 43.25d),
        new PdfRectD(8.25d, 126.5d, 103.5d, 43.25d),
    ];

    [TestMethod]
    public async Task SyntheticStackedImageImportsOwnedRuntimeCropsWithOneSharedSource()
    {
        using var directory = new TemporaryDirectory();
        byte[] sourceBytes = CreateImageBytes(ImageFileFormat.Png);
        string sourcePath = await WriteAsync(directory.Path, "stacked.png", sourceBytes);
        var store = new ProductionWorkflowPanelStore();
        var engine = new SourceBoundPanelizationEngine(StackedCrops);
        var stage = CreateStage(store, engine);
        Guid projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid sourceId = Guid.Parse("21111111-1111-1111-1111-111111111111");

        WorkflowImportSnapshot snapshot = await stage.ImportAsync(
            Request(projectId, sourceId, sourcePath),
            CancellationToken.None);

        Assert.HasCount(3, snapshot.Panels);
        Assert.AreEqual(3, snapshot.Panels.Select(static panel => panel.PanelId).Distinct().Count());
        RasterSourceImageEvidence? sharedSource = null;
        for (var index = 0; index < snapshot.Panels.Count; index++)
        {
            WorkflowImportedPanel panel = snapshot.Panels[index];
            ProductionPanelEvidence evidence = store.Get(panel.PanelId);
            Assert.IsNull(panel.PageNumber);
            Assert.AreEqual(sourceId, panel.SourceId);
            Assert.IsNull(evidence.PdfPanelSource);
            Assert.IsNotNull(evidence.RasterPanelSource);
            RasterPanelSourceProvenance provenance = evidence.RasterPanelSource;
            sharedSource ??= provenance.Source;
            Assert.AreSame(sharedSource, provenance.Source);
            CollectionAssert.AreEqual(sourceBytes, provenance.Source.CopyBytes());
            Assert.AreEqual(Path.GetFullPath(sourcePath), provenance.Source.Image.Reference);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(sourceBytes)), provenance.Source.Image.Sha256);
            Assert.AreEqual(StackedCrops[index], provenance.RequestedCropInSourcePixels);
            Assert.AreEqual(new PdfRectD(8d, 10d + (58d * index), 104d, 44d), provenance.EncodedCropInSourcePixels);
            byte[] cropBytes = evidence.CopyOriginalBytes();
            Assert.AreEqual(panel.Original.Sha256, Convert.ToHexStringLower(SHA256.HashData(cropBytes)));
            AssertPngDimensions(cropBytes, panel.Original.Width, panel.Original.Height);
        }
    }

    [TestMethod]
    public async Task IdenticalImageSourcesReceiveSourceBoundWorkflowPanelIds()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = await WriteAsync(
            directory.Path,
            "duplicate.png",
            CreateImageBytes(ImageFileFormat.Png));
        var store = new ProductionWorkflowPanelStore();
        var stage = CreateStage(
            store,
            new SourceBoundPanelizationEngine([new PdfRectD(0d, 0d, SourceWidth, SourceHeight)]));
        Guid projectId = Guid.Parse("12222222-2222-2222-2222-222222222222");
        Guid firstSourceId = Guid.Parse("22222222-2222-2222-2222-222222222221");
        Guid secondSourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        WorkflowImportSnapshot snapshot = await stage.ImportAsync(
            new WorkflowImportRequest(
                projectId,
                [
                    new WorkflowSourceRequest(firstSourceId, WorkflowSourceKind.Image, sourcePath),
                    new WorkflowSourceRequest(secondSourceId, WorkflowSourceKind.Image, sourcePath),
                ]),
            CancellationToken.None);

        Assert.HasCount(2, snapshot.Panels);
        Assert.AreNotEqual(snapshot.Panels[0].PanelId, snapshot.Panels[1].PanelId);
        Assert.AreEqual(firstSourceId, snapshot.Panels[0].SourceId);
        Assert.AreEqual(secondSourceId, snapshot.Panels[1].SourceId);
        Assert.AreEqual(
            store.Get(snapshot.Panels[0].PanelId).Panel.Original.Sha256,
            store.Get(snapshot.Panels[1].PanelId).Panel.Original.Sha256);
    }

    [TestMethod]
    public async Task PrepareRetainsExactCropBytesAndRasterSourceProvenance()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = await WriteAsync(
            directory.Path,
            "prepare.png",
            CreateImageBytes(ImageFileFormat.Png));
        var store = new ProductionWorkflowPanelStore();
        var import = CreateStage(
            store,
            new SourceBoundPanelizationEngine([new PdfRectD(12d, 20d, 70d, 80d)]));
        WorkflowImportedPanel panel = (await import.ImportAsync(
            Request(
                Guid.Parse("13333333-3333-3333-3333-333333333333"),
                Guid.Parse("23333333-3333-3333-3333-333333333333"),
                sourcePath),
            CancellationToken.None)).Panels.Single();
        ProductionPanelEvidence before = store.Get(panel.PanelId);
        byte[] expectedCrop = before.CopyOriginalBytes();
        RasterPanelSourceProvenance? provenance = before.RasterPanelSource;

        WorkflowPreparedPanel prepared = await new ProductionWorkflowPrepareStage(store).PrepareAsync(
            panel,
            enhancementEnabled: false,
            CancellationToken.None);

        ProductionPanelEvidence after = store.Get(panel.PanelId);
        Assert.AreSame(panel.Original, prepared.Original);
        Assert.AreSame(provenance, after.RasterPanelSource);
        CollectionAssert.AreEqual(expectedCrop, after.CopyOriginalBytes());
        byte[] mutableCopy = after.CopyOriginalBytes();
        mutableCopy[0] = 0;
        CollectionAssert.AreEqual(expectedCrop, store.Get(panel.PanelId).CopyOriginalBytes());
    }

    [TestMethod]
    public async Task GenuineNoPanelResultRetainsFullImageForManualReview()
    {
        using var directory = new TemporaryDirectory();
        byte[] sourceBytes = CreateImageBytes(ImageFileFormat.Png);
        string sourcePath = await WriteAsync(directory.Path, "manual.png", sourceBytes);
        var store = new ProductionWorkflowPanelStore();
        var stage = CreateStage(store, new SourceBoundPanelizationEngine([]));

        WorkflowImportedPanel panel = (await stage.ImportAsync(
            Request(
                Guid.Parse("14444444-4444-4444-4444-444444444444"),
                Guid.Parse("24444444-4444-4444-4444-444444444444"),
                sourcePath),
            CancellationToken.None)).Panels.Single();

        ProductionPanelEvidence evidence = store.Get(panel.PanelId);
        CollectionAssert.AreEqual(sourceBytes, evidence.CopyOriginalBytes());
        Assert.AreEqual(Path.GetFullPath(sourcePath), panel.Original.Reference);
        Assert.AreEqual(SourceWidth, panel.Original.Width);
        Assert.AreEqual(SourceHeight, panel.Original.Height);
        Assert.IsNotNull(evidence.RasterPanelSource);
        Assert.AreEqual(
            new PdfRectD(0d, 0d, SourceWidth, SourceHeight),
            evidence.RasterPanelSource.EncodedCropInSourcePixels);
        CollectionAssert.AreEqual(sourceBytes, evidence.RasterPanelSource.Source.CopyBytes());
    }

    [TestMethod]
    public async Task BmpImportRetainsOriginalBytesAndUsesCoordinatePreservingCanonicalPng()
    {
        using var directory = new TemporaryDirectory();
        byte[] bmpBytes = CreateImageBytes(ImageFileFormat.Bmp);
        string sourcePath = await WriteAsync(directory.Path, "source.bmp", bmpBytes);
        var store = new ProductionWorkflowPanelStore();
        var engine = new SourceBoundPanelizationEngine([new PdfRectD(10.25d, 20.5d, 60.1d, 70.2d)]);
        var stage = CreateStage(store, engine);

        WorkflowImportedPanel panel = (await stage.ImportAsync(
            Request(
                Guid.Parse("15555555-5555-5555-5555-555555555555"),
                Guid.Parse("25555555-5555-5555-5555-555555555555"),
                sourcePath),
            CancellationToken.None)).Panels.Single();

        ProductionPanelEvidence evidence = store.Get(panel.PanelId);
        Assert.IsNotNull(evidence.RasterPanelSource);
        CollectionAssert.AreEqual(bmpBytes, evidence.RasterPanelSource.Source.CopyBytes());
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bmpBytes)), evidence.RasterPanelSource.Source.Image.Sha256);
        byte[] canonicalBytes = engine.LastInputBytes ?? throw new AssertFailedException("Panelizer input was not captured.");
        Assert.IsFalse(bmpBytes.AsSpan().SequenceEqual(canonicalBytes));
        CollectionAssert.AreEqual(PngSignature, canonicalBytes[..8]);
        Assert.AreEqual(SourceWidth, engine.LastInputWidth);
        Assert.AreEqual(SourceHeight, engine.LastInputHeight);
        RasterPanelSourceProvenance provenance = evidence.RasterPanelSource;
        Assert.AreEqual(new PdfRectD(10d, 20d, 61d, 71d), provenance.EncodedCropInSourcePixels);
        Assert.AreEqual(new PdfPointD(10d, 20d), provenance.MapPanelPixelToSource(new PdfPointD(0d, 0d)));
        Assert.AreEqual(new PdfPointD(0d, 0d), provenance.MapSourcePixelToPanel(new PdfPointD(10d, 20d)));
        AssertPngDimensions(evidence.CopyOriginalBytes(), 61, 71);
    }

    [TestMethod]
    public async Task InvalidPanelizerGeometryFailsWithoutManualReviewFallback()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = await WriteAsync(
            directory.Path,
            "invalid-geometry.png",
            CreateImageBytes(ImageFileFormat.Png));
        var store = new ProductionWorkflowPanelStore();
        var stage = CreateStage(
            store,
            new SourceBoundPanelizationEngine(
            [
                new PdfRectD(5d, 5d, 80d, 80d),
                new PdfRectD(50d, 50d, 60d, 60d),
            ]));

        ProductionWorkflowStageException exception = await Assert.ThrowsExactlyAsync<ProductionWorkflowStageException>(
            () => stage.ImportAsync(
                Request(
                    Guid.Parse("16666666-6666-6666-6666-666666666666"),
                    Guid.Parse("26666666-6666-6666-6666-666666666666"),
                    sourcePath),
                CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.ImageImportFailed, exception.Failure.Code);
        StringAssert.Contains(exception.Failure.TechnicalMessage, "overlapping requested source crops");
        Assert.HasCount(0, store.PanelIds);
    }

    private static ProductionWorkflowImportStage CreateStage(
        ProductionWorkflowPanelStore store,
        IPdfPanelizationEngine engine) =>
        new(store, new ImageImportService(), pdfImportService: null, new ProductionRasterPanelizer(engine));

    private static WorkflowImportRequest Request(Guid projectId, Guid sourceId, string path) =>
        new(projectId, [new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, path)]);

    private static async Task<string> WriteAsync(string directory, string fileName, byte[] bytes)
    {
        string path = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static byte[] CreateImageBytes(ImageFileFormat format)
    {
        var pixels = new byte[SourceWidth * SourceHeight * 4];
        for (var y = 0; y < SourceHeight; y++)
        {
            for (var x = 0; x < SourceWidth; x++)
            {
                int offset = ((y * SourceWidth) + x) * 4;
                byte value = checked((byte)((x + (y * 3)) % 256));
                pixels[offset] = value;
                pixels[offset + 1] = checked((byte)(255 - value));
                pixels[offset + 2] = checked((byte)((x * 5 + y) % 256));
                pixels[offset + 3] = checked((byte)(64 + ((x + y) % 192)));
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            SourceWidth,
            SourceHeight,
            96d,
            96d,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            SourceWidth * 4);
        BitmapEncoder encoder = format switch
        {
            ImageFileFormat.Png => new PngBitmapEncoder(),
            ImageFileFormat.Bmp => new BmpBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static void AssertPngDimensions(byte[] bytes, int expectedWidth, int expectedHeight)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        BitmapFrame frame = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        Assert.AreEqual(expectedWidth, frame.PixelWidth);
        Assert.AreEqual(expectedHeight, frame.PixelHeight);
    }

    private sealed class SourceBoundPanelizationEngine : IPdfPanelizationEngine
    {
        private readonly PdfRectD[] crops;

        public SourceBoundPanelizationEngine(IEnumerable<PdfRectD> crops) =>
            this.crops = crops.ToArray();

        public byte[]? LastInputBytes { get; private set; }
        public int LastInputWidth { get; private set; }
        public int LastInputHeight { get; private set; }

        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfRenderedPage rendered = input.RenderedPage ?? throw new InvalidOperationException("Rendered input required.");
            byte[] source = rendered.PngBytes.ToArray();
            LastInputBytes = source;
            LastInputWidth = rendered.Width;
            LastInputHeight = rendered.Height;
            if (crops.Length == 0)
            {
                return Task.FromResult(new PdfPanelizationResult([], []));
            }

            Guid figureId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var evidence = new PdfPanelEvidence(
                PdfPanelEvidenceKind.DenseLineStructure,
                0.75d,
                "synthetic runtime crop fixture");
            var figure = new PdfFigureCandidate(
                figureId,
                pageNumber: 1,
                PdfFigureSourceKind.RenderedPage,
                embeddedImageId: null,
                new PdfRectD(0d, 0d, rendered.Width, rendered.Height),
                new PdfRectD(0d, 0d, rendered.Width / 2d, rendered.Height / 2d),
                rendered.Width,
                rendered.Height,
                new ImmutableByteBuffer(source),
                "image/png",
                caption: null,
                [evidence],
                confidence: 0.75d);
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
                [evidence],
                confidence: 0.75d)).ToArray();
            return Task.FromResult(new PdfPanelizationResult([figure], panels));
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "GraphReader.App.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
