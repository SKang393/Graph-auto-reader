// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Imaging;
using GraphReader.Pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RasterPanelRestoreTests
{
    [TestMethod]
    public async Task SavedCropsRestoreExactBytesAndIdsWithoutCallingCurrentPanelizer()
    {
        using var fixture = new RasterFixture();
        Guid projectId = Guid.NewGuid(), sourceId = Guid.NewGuid();
        var store = new ProductionWorkflowPanelStore();
        var stage = new ProductionWorkflowImportStage(store, new ImageImportService(), null,
            new ProductionRasterPanelizer(new ForbiddenPanelizer()));
        RetainedRasterPanelReference[] saved = [fixture.Panel, fixture.SecondPanel];

        var source = new WorkflowSourceRequest(sourceId, WorkflowSourceKind.Image, fixture.Path)
        {
            RetainedRaster = new RetainedRasterSourceRequest(fixture.SourceSha, saved),
        };
        WorkflowImportSnapshot restored = await stage.ImportAsync(new WorkflowImportRequest(projectId, [source]),
            CancellationToken.None);

        CollectionAssert.AreEqual(saved.Select(static panel => panel.PanelId).ToArray(),
            restored.Panels.Select(static panel => panel.PanelId).ToArray());
        foreach (WorkflowImportedPanel panel in restored.Panels)
        {
            ProductionPanelEvidence evidence = store.Get(panel.PanelId);
            CollectionAssert.AreEqual(panel.PanelId == fixture.Panel.PanelId ? fixture.CropBytes : fixture.SecondCropBytes,
                evidence.CopyOriginalBytes());
            CollectionAssert.AreEqual(fixture.SourceBytes, evidence.RasterPanelSource!.Source.CopyBytes());
            Assert.AreEqual(new PdfPointD(7, panel.PanelId == fixture.Panel.PanelId ? 11 : 27),
                evidence.RasterPanelSource.MapPanelPixelToSource(new PdfPointD(2, 3)));
        }
        Assert.AreSame(store.Get(saved[0].PanelId).RasterPanelSource!.Source,
            store.Get(saved[1].PanelId).RasterPanelSource!.Source);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("panel")]
    [DataRow("dimensions")]
    [DataRow("bounds")]
    [DataRow("overlap")]
    public async Task ChangedSavedIdentityFailsBeforeRegisteringAnyPanels(string changed)
    {
        using var fixture = new RasterFixture();
        var store = new ProductionWorkflowPanelStore();
        var stage = new ProductionWorkflowImportStage(store, new ImageImportService());
        RetainedRasterPanelReference panel = changed switch
        {
            "panel" => fixture.Panel with { PanelImageSha256 = new string('a', 64) },
            "dimensions" => fixture.Panel with { SourceWidth = 31 },
            "bounds" => fixture.Panel with { EncodedCropInSourcePixels = new PdfRectD(5, 8, 40, 14) },
            _ => fixture.Panel,
        };
        RetainedRasterPanelReference[] retained = changed == "overlap"
            ? [panel, panel with { PanelId = Guid.NewGuid() }] : [panel];
        await Assert.ThrowsExactlyAsync<ProductionWorkflowStageException>(() => stage.RestoreRasterPanelsAsync(
            Guid.NewGuid(), new WorkflowSourceRequest(Guid.NewGuid(), WorkflowSourceKind.Image, fixture.Path),
            changed == "source" ? new string('b', 64) : fixture.SourceSha, retained, CancellationToken.None));
        Assert.HasCount(0, store.PanelIds);
    }

    private sealed class RasterFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "GraphReader.App.Tests", Guid.NewGuid().ToString("N"));
        public RasterFixture()
        {
            var pixels = new byte[30 * 40 * 3];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index % 251);
            BitmapSource source = BitmapSource.Create(30, 40, 96, 96, PixelFormats.Rgb24, null, pixels, 90);
            source.Freeze();
            SourceBytes = Encode(source);
            var crop = new CroppedBitmap(source, new Int32Rect(5, 8, 10, 14));
            crop.Freeze();
            CropBytes = Encode(crop);
            var secondCrop = new CroppedBitmap(source, new Int32Rect(5, 24, 10, 14));
            secondCrop.Freeze();
            SecondCropBytes = Encode(secondCrop);
            SourceSha = Hash(SourceBytes);
            Panel = new RetainedRasterPanelReference(Guid.NewGuid(), "retained.png", Hash(CropBytes), 30, 40,
                new PdfRectD(5.2, 8.3, 9.4, 13.4), new PdfRectD(5, 8, 10, 14));
            SecondPanel = new RetainedRasterPanelReference(Guid.NewGuid(), "retained2.png", Hash(SecondCropBytes), 30, 40,
                new PdfRectD(5.2, 24.3, 9.4, 13.4), new PdfRectD(5, 24, 10, 14));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "source.png");
            File.WriteAllBytes(Path, SourceBytes);
        }
        public string Path { get; }
        public byte[] SourceBytes { get; }
        public byte[] CropBytes { get; }
        public byte[] SecondCropBytes { get; }
        public string SourceSha { get; }
        public RetainedRasterPanelReference Panel { get; }
        public RetainedRasterPanelReference SecondPanel { get; }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private sealed class ForbiddenPanelizer : IPdfPanelizationEngine
    {
        public Task<PdfPanelizationResult> ProposeAsync(PdfPanelizationInput input, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Restoring saved pixels must not rerun panel detection.");
        public PdfPanelizationResult ApplySplit(PdfPanelizationResult current, PdfManualSplitCommand command) =>
            throw new NotSupportedException();
        public PdfPanelizationResult ApplyMerge(PdfPanelizationResult current, PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }
}
