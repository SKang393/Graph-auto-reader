// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Pdf;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ManualPanelCropWorkspaceTests
{
    private static readonly double[] ExpectedNestedCrop = [30, 20, 60, 60];
    private static readonly double[] ExpectedPdfTransform = [.5, 0, 0, -.5, 10, 80];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NestedRasterAndPdfCropsPreserveParentsAndReopenExportTrace(bool pdf) => WithFixture(pdf, fixture =>
    {
        using var workspace = fixture.Workspace();
        WorkspaceTabViewModel parent = Pump(workspace.ImportImagesAsync([fixture.SourcePath], CancellationToken.None)).Single();
        string parentState = JsonSerializer.Serialize(workspace.CurrentProject.Panels.Single());
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        WorkspaceTabViewModel child = Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(parent.TabId)!,
            new(20, 10, 100, 90), CancellationToken.None));
        WorkspaceTabViewModel nested = Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(child.TabId)!,
            new(10, 10, 60, 60), CancellationToken.None));
        elapsed.Stop();
        Console.WriteLine("Manual crop {0}: two nested crops {1:F3} ms", pdf ? "PDF" : "raster", elapsed.Elapsed.TotalMilliseconds);
        Assert.AreEqual(parentState, JsonSerializer.Serialize(workspace.CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString("D") == parent.PanelId)));
        Assert.HasCount(3, workspace.CreateWorkspace());
        Assert.IsNull(child.Calibration);
        Assert.IsNull(nested.Calibration);
        Assert.IsEmpty(child.Points);
        Assert.IsEmpty(child.SeriesCards);
        Assert.IsTrue(child.IsDirty);
        Assert.AreEqual(parent.SourceId, nested.SourceId);
        Assert.AreEqual(fixture.SourceHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.SourcePath))));
        Assert.AreEqual(0, Pixel(nested, 30, 30), "Nested crop must apply the full-source offset exactly once.");
        Assert.AreNotEqual(0, Pixel(nested, 31, 30));

        workspace.Calibrate(nested.TabId, new(new(5, 55), new(5, 5), new(55, 55), 10, 3));
        string seriesId = workspace.AddSeries(nested.TabId, new("Owned", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
        var point = workspace.AddPoint(nested.TabId, seriesId, 30, 30);
        Assert.AreEqual(2d, point.GraphX);
        Assert.AreEqual(5d, point.GraphY);
        PanelRecord panel = workspace.CurrentProject.Panels.Single(item => item.PanelId.Value.ToString("D") == nested.PanelId);
        if (pdf)
        {
            Assert.AreEqual(2, nested.PageNumber);
            JsonElement provenance = panel.Validation!.Value.GetProperty("pdf_panel_source_binding").GetProperty("pdf_panel_source");
            double[] crop = provenance.GetProperty("encoded_crop_source_pixels").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            CollectionAssert.AreEqual(ExpectedNestedCrop, crop);
            double[] transform = provenance.GetProperty("source_pixels_to_page_points").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            CollectionAssert.AreEqual(ExpectedPdfTransform, transform);
            Assert.AreEqual(40d, (30 + crop[0]) * transform[0] + transform[4]);
            Assert.AreEqual(55d, (30 + crop[1]) * transform[3] + transform[5]);
        }
        else
        {
            Assert.AreEqual(new CropRectangle(30, 20, 60, 60), panel.Crop);
            Assert.AreEqual(new GraphReader.Domain.PixelPoint(60, 50), panel.Points.Single().OriginalPixel);
        }
        string state = JsonSerializer.Serialize(panel);
        string path = Path.Combine(fixture.Directory, "cropped.garproj");
        Assert.IsTrue(Pump(workspace.SaveProjectAsync(path, CancellationToken.None)).IsSuccess);
        using var reopened = fixture.Workspace();
        WorkspaceTabViewModel restored = Pump(reopened.OpenProjectAsync(path, CancellationToken.None)).Single(tab => tab.TabId == nested.TabId);
        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(state),
            JsonSerializer.SerializeToNode(reopened.CurrentProject.Panels.Single(item => item.PanelId.Value.ToString("D") == nested.PanelId))),
            "Saving canonicalizes JSON property order; all persisted panel values must stay equal.");
        Assert.HasCount(3, reopened.CreateWorkspace(), "Both overlapping parents remain available after reopen.");
        Assert.AreEqual(30d, restored.Points.Single().PixelX);
        Assert.AreEqual(30d, restored.Points.Single().PixelY);
        Assert.AreEqual(0, Pixel(restored, 30, 30));
        var exported = Pump(reopened.ExportAsync(restored.TabId, Path.Combine(fixture.Directory, "export"), CancellationToken.None));
        Assert.IsTrue(exported.Succeeded, string.Join(" | ", exported.Failures.Select(failure => failure.TechnicalMessage)));
        Assert.IsTrue(System.IO.Directory.EnumerateFiles(Path.Combine(fixture.Directory, "export"), "*.csv", SearchOption.AllDirectories).Any());
    });

    [TestMethod]
    public void StaleContextSourceMutationAndInvalidBoundsNeverCreateTabs() => WithFixture(false, fixture =>
    {
        using var workspace = fixture.Workspace();
        WorkspaceTabViewModel tab = Pump(workspace.ImportImagesAsync([fixture.SourcePath], CancellationToken.None)).Single();
        PanelCropContext context = workspace.GetPanelCropContext(tab.TabId)!;
        MarkerExemplarRectangle[] invalid = [new(-1, 0, 32, 32), new(0, 0, 31, 32), new(150, 100, 32, 32), new(0, 0, 4096, 4096)];
        foreach (var rectangle in invalid)
            Assert.ThrowsExactly<InvalidOperationException>(() => Pump(workspace.CreatePanelCropAsync(context, rectangle, CancellationToken.None)));
        Assert.ThrowsExactly<InvalidOperationException>(() => Pump(workspace.CreatePanelCropAsync(context with { Binding = "stale" }, new(20, 10, 60, 60), CancellationToken.None)));
        Assert.ThrowsExactly<OperationCanceledException>(() => Pump(workspace.CreatePanelCropAsync(context, new(20, 10, 60, 60), new(canceled: true))));
        File.AppendAllText(fixture.SourcePath, "changed");
        Assert.ThrowsExactly<InvalidOperationException>(() => Pump(workspace.CreatePanelCropAsync(context, new(20, 10, 60, 60), CancellationToken.None)));
        Assert.HasCount(1, workspace.CreateWorkspace());
        Assert.HasCount(1, workspace.CurrentProject.Panels);
    });

    [TestMethod]
    public void UndeclaredOverlappingAutomaticPanelsAreStillRejectedOnReopen() => WithFixture(false, fixture =>
    {
        using var workspace = fixture.Workspace();
        WorkspaceTabViewModel tab = Pump(workspace.ImportImagesAsync([fixture.SourcePath], CancellationToken.None)).Single();
        Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(tab.TabId)!, new(20, 10, 60, 60), CancellationToken.None));
        string path = Path.Combine(fixture.Directory, "overlap.garproj");
        Assert.IsTrue(Pump(workspace.SaveProjectAsync(path, CancellationToken.None)).IsSuccess);
        var file = new ProjectFileStore();
        var loaded = Pump(file.LoadAsync(path, CancellationToken.None));
        Assert.IsTrue(loaded.IsSuccess);
        ProjectDocument project = loaded.Value!;
        PanelRecord child = project.Panels[^1];
        var validation = System.Text.Json.Nodes.JsonNode.Parse(child.Validation!.Value.GetRawText())!.AsObject();
        validation.Remove("manual_panel_crop");
        ProjectDocument changed = project with { Panels = project.Panels.Select(panel => panel.PanelId == child.PanelId
            ? panel with { Validation = JsonSerializer.SerializeToElement(validation) } : panel).ToArray() };
        Assert.IsTrue(Pump(file.SaveAsync(changed, path, CancellationToken.None)).IsSuccess);
        using var reopened = fixture.Workspace();
        ProductionWorkflowStageException failure = Assert.ThrowsExactly<ProductionWorkflowStageException>(() =>
            Pump(reopened.OpenProjectAsync(path, CancellationToken.None)));
        StringAssert.Contains(failure.Message.ToLowerInvariant(), "overlap");
    });

    [TestMethod]
    public void LegacyFullImageUpgradePreservesCoordinatesAndSupportsRepeatedCropReopen() => WithFixture(false, fixture =>
    {
        string path = Path.Combine(fixture.Directory, "legacy.garproj");
        using (var initial = fixture.Workspace())
        {
            WorkspaceTabViewModel tab = Pump(initial.ImportImagesAsync([fixture.SourcePath], CancellationToken.None)).Single();
            initial.Calibrate(tab.TabId, new(new(10, 100), new(10, 10), new(150, 100), 10, 3));
            string series = initial.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
            initial.AddPoint(tab.TabId, series, 60, 50);
            ProjectDocument legacy = initial.CurrentProject with { Panels = initial.CurrentProject.Panels.Select(panel => panel with { Transforms = [] }).ToArray() };
            Assert.IsTrue(Pump(new ProjectFileStore().SaveAsync(legacy, path, CancellationToken.None)).IsSuccess);
        }
        using var workspace = fixture.Workspace();
        WorkspaceTabViewModel parent = Pump(workspace.OpenProjectAsync(path, CancellationToken.None)).Single();
        string points = JsonSerializer.Serialize(workspace.CurrentProject.Panels.Single().Points);
        Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(parent.TabId)!, new(20, 10, 100, 90), CancellationToken.None));
        Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(parent.TabId)!, new(40, 20, 80, 80), CancellationToken.None));
        Assert.AreEqual(points, JsonSerializer.Serialize(workspace.CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString("D") == parent.PanelId).Points));
        Assert.IsTrue(Pump(workspace.SaveProjectAsync(path, CancellationToken.None)).IsSuccess);
        using var reopened = fixture.Workspace();
        Assert.HasCount(3, Pump(reopened.OpenProjectAsync(path, CancellationToken.None)));
        Assert.AreEqual(points, JsonSerializer.Serialize(reopened.CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString("D") == parent.PanelId).Points));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ParentsRefuseCloseUntilNestedChildrenCloseLeafFirst(bool pdf) => WithFixture(pdf, fixture =>
    {
        using var workspace = fixture.Workspace();
        WorkspaceTabViewModel parent = Pump(workspace.ImportImagesAsync([fixture.SourcePath], CancellationToken.None)).Single();
        WorkspaceTabViewModel child = Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(parent.TabId)!,
            new(20, 10, 100, 90), CancellationToken.None));
        WorkspaceTabViewModel nested = Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(child.TabId)!,
            new(10, 10, 60, 60), CancellationToken.None));
        PanelCropContext parentContext = workspace.GetPanelCropContext(parent.TabId)!;
        PanelCropContext childContext = workspace.GetPanelCropContext(child.TabId)!;
        string before = JsonSerializer.Serialize(workspace.CurrentProject);
        Assert.IsTrue(workspace.HasDependentPanelCrops(parent.TabId));
        Assert.IsTrue(workspace.HasDependentPanelCrops(child.TabId));
        Assert.IsFalse(workspace.HasDependentPanelCrops(nested.TabId));
        Assert.IsFalse(workspace.CloseTab(parent.TabId));
        Assert.IsFalse(workspace.CloseTab(child.TabId));
        Assert.HasCount(3, workspace.CreateWorkspace());
        Assert.AreEqual(before, JsonSerializer.Serialize(workspace.CurrentProject), "Refused closes cannot mutate saved scientific state.");
        Assert.IsTrue(workspace.IsPanelCropContextCurrent(parentContext));
        Assert.IsTrue(workspace.IsPanelCropContextCurrent(childContext));

        Assert.IsTrue(workspace.CloseTab(nested.TabId));
        Assert.IsFalse(workspace.HasDependentPanelCrops(child.TabId), "Closed child records are not live dependencies.");
        Assert.IsTrue(workspace.HasDependentPanelCrops(parent.TabId));
        Assert.IsTrue(workspace.CloseTab(child.TabId));
        Assert.IsFalse(workspace.HasDependentPanelCrops(parent.TabId));
        string path = Path.Combine(fixture.Directory, "closed-children.garproj");
        Assert.IsTrue(Pump(workspace.SaveProjectAsync(path, CancellationToken.None)).IsSuccess);
        using var reopened = fixture.Workspace();
        Assert.AreEqual(parent.TabId, Pump(reopened.OpenProjectAsync(path, CancellationToken.None)).Single().TabId);
        Assert.IsTrue(workspace.CloseTab(parent.TabId));
        Assert.IsEmpty(workspace.CreateWorkspace());
    });

    [TestMethod]
    [DataRow("child-hash")]
    [DataRow("missing-parent")]
    [DataRow("cycle")]
    public void MalformedSavedLineageCannotReplaceAnOpenWorkspace(string corruption) => WithFixture(false, fixture =>
    {
        string path = Path.Combine(fixture.Directory, "malformed.garproj");
        string childId;
        string nestedId;
        using (var producer = fixture.Workspace())
        {
            WorkspaceTabViewModel parent = Pump(producer.ImportImagesAsync([fixture.SourcePath], CancellationToken.None)).Single();
            WorkspaceTabViewModel child = Pump(producer.CreatePanelCropAsync(producer.GetPanelCropContext(parent.TabId)!,
                new(20, 10, 100, 90), CancellationToken.None));
            WorkspaceTabViewModel nested = Pump(producer.CreatePanelCropAsync(producer.GetPanelCropContext(child.TabId)!,
                new(10, 10, 60, 60), CancellationToken.None));
            childId = child.TabId;
            nestedId = nested.TabId;
            Assert.IsTrue(Pump(producer.SaveProjectAsync(path, CancellationToken.None)).IsSuccess);
        }
        var store = new ProjectFileStore();
        var loaded = Pump(store.LoadAsync(path, CancellationToken.None));
        Assert.IsTrue(loaded.IsSuccess);
        ProjectDocument project = loaded.Value!;
        PanelRecord target = project.Panels.Single(panel => panel.PanelId.Value.ToString("D") == childId);
        var validation = System.Text.Json.Nodes.JsonNode.Parse(target.Validation!.Value.GetRawText())!.AsObject();
        var lineage = validation["manual_panel_crop"]!.AsObject();
        switch (corruption)
        {
            case "child-hash": lineage["ChildImageSha256"] = new string('0', 64); break;
            case "missing-parent": lineage.Remove("ParentPanelId"); break;
            case "cycle": lineage["ParentPanelId"] = nestedId; break;
            default: Assert.Fail("Unknown owned corruption case."); break;
        }
        ProjectDocument changed = project with { Panels = project.Panels.Select(panel => panel.PanelId == target.PanelId
            ? panel with { Validation = JsonSerializer.SerializeToElement(validation) } : panel).ToArray() };
        Assert.IsTrue(Pump(store.SaveAsync(changed, path, CancellationToken.None)).IsSuccess);

        using var existing = fixture.Workspace();
        WorkspaceTabViewModel retained = Pump(existing.ImportImagesAsync([fixture.SourcePath], CancellationToken.None)).Single();
        string before = JsonSerializer.Serialize(existing.CurrentProject);
        PanelCropContext context = existing.GetPanelCropContext(retained.TabId)!;
        Assert.ThrowsExactly<InvalidOperationException>(() => Pump(existing.OpenProjectAsync(path, CancellationToken.None)));
        Assert.AreEqual(before, JsonSerializer.Serialize(existing.CurrentProject));
        Assert.AreSame(retained, existing.CreateWorkspace().Single());
        Assert.IsTrue(existing.IsPanelCropContextCurrent(context), "A failed open must retain the original immutable panel binding.");
    });

    private static byte Pixel(WorkspaceTabViewModel tab, int x, int y)
    {
        var bitmap = (BitmapSource)tab.ImageSource!;
        byte[] pixel = new byte[Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8)];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, pixel.Length, 0);
        return pixel[0];
    }

    private static void WithFixture(bool pdf, Action<Fixture> action) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(pdf);
        action(fixture);
    });

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "manual-crop-" + Guid.NewGuid().ToString("N"));
        internal string SourcePath { get; }
        internal string SourceHash { get; }
        private readonly OwnedPdfImporter? importer;
        internal Fixture(bool pdf)
        {
            System.IO.Directory.CreateDirectory(Directory);
            byte[] pixels = Enumerable.Repeat((byte)255, 160 * 120).ToArray();
            pixels[50 * 160 + 60] = 0;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null, pixels, 160)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            byte[] image = stream.ToArray();
            SourcePath = Path.Combine(Directory, pdf ? "owned.pdf" : "owned.png");
            File.WriteAllBytes(SourcePath, pdf ? "%PDF-1.7\n% owned crop fixture\n"u8.ToArray() : image);
            SourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(SourcePath)));
            importer = pdf ? new OwnedPdfImporter(image) : null;
        }
        internal ManualPreviewWorkspaceService Workspace() => new(pdfImportService: importer);
        public void Dispose() { System.IO.Directory.Delete(Directory, true); GC.SuppressFinalize(this); }
    }

    private sealed class OwnedPdfImporter(byte[] bytes) : IPdfImportService
    {
        public Task<PdfImportResult> ImportAsync(PdfImportRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string hash = Convert.ToHexStringLower(SHA256.HashData(request.PdfBytes.ToArray()));
            Guid figureId = Guid.Parse("50000000-0000-0000-0000-000000000093");
            var pixels = new PdfRectD(0, 0, 160, 120);
            var points = new PdfRectD(10, 20, 80, 60);
            var figure = new PdfFigureCandidate(figureId, 2, PdfFigureSourceKind.EmbeddedImage, null,
                pixels, points, 160, 120, new ImmutableByteBuffer(bytes), "image/png", null, [], 1,
                sourcePixelsToPagePoints: new PdfAffineTransform(.5, 0, 0, -.5, 10, 80));
            var panel = new PdfPanelRecord(Guid.Parse("60000000-0000-0000-0000-000000000093"), figureId,
                2, 0, pixels, pixels, points, null, null, [], [], 1);
            return Task.FromResult(new PdfImportResult(request.RunId, request.ProjectId,
                new PdfDocumentSnapshot(hash, new(null, null, null, null, null, null), []), [figure], [panel], [], [], new(1, 0, 1, 2)));
        }
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}
