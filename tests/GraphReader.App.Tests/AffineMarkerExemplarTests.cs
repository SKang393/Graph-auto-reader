// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class AffineMarkerExemplarTests
{
    private static readonly int[] ExpectedObservationIndices = [1, 2, 3];
    [TestMethod]
    [DataRow(2d, false)]
    [DataRow(-2d, false)]
    [DataRow(0d, true)]
    public void OriginalPixelMatcherExcludesFalseEnclosingBoxMatchAndPreservesFractionalSeed(double degrees, bool shear) =>
        WithFixture(degrees, shear, false, false, fixture =>
        {
            int count = fixture.Tab.Points.Count;
            var result = Pump(fixture.Workspace.FindMarkerExamplesAsync(fixture.Tab.TabId, fixture.SeedId,
                fixture.SeriesId, 13, CancellationToken.None));
            Assert.IsNotNull(result, "A reviewed affine calibration needs a marker-only path without a scalar lattice.");
            Assert.IsNotNull(result.Request.AffinePlot);
            Assert.IsTrue(fixture.Workspace.IsMarkerExemplarSuggestionCurrent(result));
            Assert.AreEqual(count, fixture.Tab.Points.Count);
            Assert.AreEqual(new MarkerExemplarRectangle(103, 144, 13, 13), result.Request.Exemplar);
            Assert.AreEqual(new MarkerExemplarPoint(110.25, 150.5), result.Request.SelectedPoint);
            CollectionAssert.AreEqual(fixture.PanelBytes, fixture.Worker.Image!);
            Assert.AreEqual(Hash(fixture.PanelBytes), result.Request.PanelImageSha256);
            byte[] gray = fixture.Pixels;
            byte[] before = gray.ToArray();
            var unmasked = MarkerExemplarMatcher.Find(result.Request with { AffinePlot = null }, gray, CancellationToken.None);
            Assert.HasCount(3, unmasked, "The same raw enclosing rectangle admits the identical outside distractor.");
            Assert.HasCount(2, result.Response.Candidates);
            Assert.IsTrue(unmasked.Any(item => item.Center == fixture.Distractor));
            Assert.IsFalse(result.Response.Candidates.Any(item => item.Center == fixture.Distractor));
            foreach (var item in result.Response.Candidates)
            {
                Assert.IsTrue(item.Center == new MarkerExemplarPoint(170.25, 140.5) || item.Center == new MarkerExemplarPoint(220.25, 130.5));
                Assert.AreEqual(unmasked.Single(raw => raw.Center == item.Center).Similarity, item.Similarity);
                Assert.IsTrue(MarkerExemplarGeometry.ContainsPlot(result.Request, item.Center));
            }
            CollectionAssert.AreEqual(before, gray);
            CollectionAssert.AreEqual(fixture.SourceBytes, File.ReadAllBytes(fixture.ImagePath));
            using var wire = new MemoryStream();
            Pump(MarkerExemplarWire.WriteRequestAsync(wire, result.Request, fixture.PanelBytes, CancellationToken.None));
            wire.Position = 0;
            var decoded = Pump(MarkerExemplarWire.ReadRequestAsync(wire, CancellationToken.None));
            Assert.AreEqual(result.Request, decoded.Request);
            CollectionAssert.AreEqual(fixture.PanelBytes, decoded.Image);
            using var alignedWire = new MemoryStream();
            Pump(MarkerExemplarWire.WriteRequestAsync(alignedWire, result.Request with { AffinePlot = null }, fixture.PanelBytes, CancellationToken.None));
            Assert.IsFalse(Encoding.UTF8.GetString(alignedWire.ToArray()).Contains("affine_plot", StringComparison.Ordinal),
                "Aligned requests retain their existing JSON shape.");
            Assert.ThrowsExactly<ProductionWorkflowStageException>(() => fixture.Workspace.CreateConfirmedManualCalibrationBindings());
        });

    [TestMethod]
    public void AffineSearchOnlyPreviewsThenExplicitAcceptAndBatchRemainUndoable() =>
        WithFixture(2, false, false, false, fixture =>
        {
            using var vm = fixture.CreateViewModel();
            string before = JsonSerializer.Serialize(fixture.Workspace.CurrentProject);
            Assert.IsTrue(vm.FindMarkerExemplarsCommand.CanExecute(null));
            Pump(vm.FindMarkerExemplarsAsync(CancellationToken.None));
            Assert.AreEqual(before, JsonSerializer.Serialize(fixture.Workspace.CurrentProject));
            Assert.HasCount(2, vm.MarkerExemplarCandidates);
            Assert.HasCount(1, fixture.Tab.Points);
            var row = vm.MarkerExemplarCandidates[0];
            var expected = ManualCalibrationMapper.Map(fixture.Tab.Calibration!, row.Candidate.Center.X, row.Candidate.Center.Y);
            vm.AcceptMarkerExemplarCandidateCommand.Execute(row);
            var added = fixture.Tab.Points.Single(point => point.PointId != fixture.SeedId);
            Assert.AreEqual(row.Candidate.Center.X, added.PixelX);
            Assert.AreEqual(row.Candidate.Center.Y, added.PixelY);
            Assert.AreEqual(expected.X, added.GraphX, 1e-9);
            Assert.AreEqual(expected.Y, added.GraphY, 1e-9);
            vm.UndoCommand.Execute(null);
            Assert.HasCount(1, fixture.Tab.Points);
            vm.RedoCommand.Execute(null);
            Assert.HasCount(2, fixture.Tab.Points);
            vm.SelectedSeriesId = fixture.SeriesId; vm.SelectedPointId = fixture.SeedId;
            Pump(vm.FindMarkerExemplarsAsync(CancellationToken.None));
            Assert.IsTrue(vm.MarkerExemplarCandidates.Any(item => item.IsDuplicate));
            vm.AcceptAllMarkerExemplarCandidatesCommand.Execute(null);
            Assert.HasCount(3, fixture.Tab.Points);
            vm.UndoCommand.Execute(null);
            Assert.HasCount(2, fixture.Tab.Points, "The remaining batch is one ordinary undoable edit.");
            vm.RedoCommand.Execute(null);
            Assert.HasCount(3, fixture.Tab.Points);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConfirmedPrintedXWorksInAlignedAndAffineMarkerPathsAndExportsOnlyEstimatedX(bool affine) =>
        WithFixture(affine ? 2 : 0, false, true, true, fixture =>
        {
            using var vm = fixture.CreateViewModel();
            Pump(vm.FindMarkerExemplarsAsync(CancellationToken.None));
            Assert.HasCount(2, vm.MarkerExemplarCandidates);
            Assert.AreEqual(affine, fixture.Worker.Request!.AffinePlot is not null);
            vm.AcceptAllMarkerExemplarCandidatesCommand.Execute(null);
            Assert.HasCount(3, fixture.Tab.Points);
            var originalPoints = fixture.Tab.Points.ToDictionary(point => point.PointId,
                point => (point.PixelX, point.PixelY, point.GraphX, point.GraphY, point.ObservationIndex));
            string project = Path.Combine(fixture.Directory, "reviewed.garproj");
            Assert.IsTrue(Pump(fixture.Workspace.SaveProjectAsync(project, CancellationToken.None)).IsSuccess);
            using var reopened = new ManualPreviewWorkspaceService();
            var restored = Pump(reopened.OpenProjectAsync(project, CancellationToken.None)).Single(tab => tab.TabId == fixture.Tab.TabId);
            Assert.IsTrue(restored.Calibration!.UsesPrintedXCoordinates);
            foreach (var point in restored.Points)
            {
                var expected = originalPoints[point.PointId];
                Assert.AreEqual(expected.PixelX, point.PixelX, 1e-9); Assert.AreEqual(expected.PixelY, point.PixelY, 1e-9);
                Assert.AreEqual(expected.GraphX, point.GraphX, 1e-9); Assert.AreEqual(expected.GraphY, point.GraphY, 1e-9);
                Assert.AreEqual(expected.ObservationIndex, point.ObservationIndex);
            }
            var panel = reopened.CurrentProject.Panels.Single(item => item.PanelId.Value.ToString("D") == restored.PanelId);
            foreach (var point in panel.Points)
            {
                var local = restored.Points.Single(item => item.PointId == point.PointId.Value.ToString("D"));
                Assert.AreEqual(local.PixelX + 20, point.OriginalPixel.X, 1e-9);
                Assert.AreEqual(local.PixelY + 10, point.OriginalPixel.Y, 1e-9);
                Assert.IsNull(point.PrintedXValue);
                Assert.AreEqual(local.GraphX, point.EstimatedXValue!.Value, 1e-9);
            }
            CollectionAssert.AreEqual(ExpectedObservationIndices, restored.Points.Select(point => point.ObservationIndex).Order().ToArray());
            var export = Pump(reopened.ExportAsync(restored.TabId, Path.Combine(fixture.Directory, "export"), CancellationToken.None));
            Assert.IsTrue(export.Succeeded);
            Assert.AreEqual(ExportMode.PrintedSession, export.Mode);
            foreach (var artifact in export.AuditArtifacts)
                foreach (var row in artifact.Rows)
                {
                    Assert.AreEqual(ExportXValueSource.Estimated, row.XSource);
                    var original = panel.Points.Single(point => point.OriginalPixel == new GraphReader.Domain.PixelPoint(row.OriginalPixel.X, row.OriginalPixel.Y));
                    Assert.AreEqual(original.EstimatedXValue!.Value, row.XValue, 1e-9);
                }
            Assert.ThrowsExactly<ProductionWorkflowStageException>(() => reopened.CreateConfirmedManualCalibrationBindings());
            CollectionAssert.AreEqual(fixture.SourceBytes, File.ReadAllBytes(fixture.ImagePath));
        });

    [TestMethod]
    public void UnconfirmedOrMismatchedPersistedCalibrationCannotStartMarkerWorker() =>
        WithFixture(2, false, false, false, fixture =>
        {
            var accepted = fixture.Tab.Calibration!;
            fixture.Tab.Calibration = accepted with { YMaximum = accepted.YMaximum + 1 };
            Assert.IsNull(Pump(fixture.Workspace.FindMarkerExamplesAsync(fixture.Tab.TabId, fixture.SeedId, fixture.SeriesId, 13, CancellationToken.None)));
            fixture.Tab.Calibration = accepted;
            fixture.Workspace.SetConfirmation(false);
            Assert.IsNull(Pump(fixture.Workspace.FindMarkerExamplesAsync(fixture.Tab.TabId, fixture.SeedId, fixture.SeriesId, 13, CancellationToken.None)));
            Assert.AreEqual(0, fixture.Worker.Calls);
            fixture.Workspace.SetConfirmation(true);
            var result = Pump(fixture.Workspace.FindMarkerExamplesAsync(fixture.Tab.TabId, fixture.SeedId, fixture.SeriesId, 13, CancellationToken.None));
            Assert.IsNotNull(result);
            fixture.Workspace.Calibrate(fixture.Tab.TabId, fixture.Calibration with { YMaximum = 11 });
            Assert.IsFalse(fixture.Workspace.IsMarkerExemplarSuggestionCurrent(result));
        });

    private static MarkerExemplarAffinePlot Frame(double degrees, bool shear)
    {
        double a = degrees * Math.PI / 180;
        const double ox = 50, oy = 200, lengthX = 220, lengthY = 150;
        return new(new(ox, oy), new(ox + lengthX * Math.Cos(a), oy + lengthX * Math.Sin(a)),
            new(ox + lengthY * Math.Sin(a) + (shear ? 30 : 0), oy - lengthY * Math.Cos(a)));
    }
    private static string Hash(byte[] image) => Convert.ToHexStringLower(SHA256.HashData(image));

    private static void WithFixture(double degrees, bool shear, bool printed, bool crop, Action<Fixture> action) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(degrees, shear, printed, crop);
        action(fixture);
    });

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "affine-markers-" + Guid.NewGuid().ToString("N"));
        internal string ImagePath { get; }
        internal byte[] SourceBytes { get; }
        internal byte[] PanelBytes { get; }
        internal byte[] Pixels { get; }
        internal Worker Worker { get; } = new();
        internal Workspace Workspace { get; }
        internal WorkspaceTabViewModel Tab { get; }
        internal ManualCalibrationRequest Calibration { get; }
        internal string SeriesId { get; }
        internal string SeedId { get; }
        internal MarkerExemplarPoint Distractor { get; }

        internal Fixture(double degrees, bool shear, bool printed, bool crop)
        {
            System.IO.Directory.CreateDirectory(Directory);
            ImagePath = Path.Combine(Directory, "owned.png");
            Pixels = Enumerable.Repeat((byte)255, 320 * 240).ToArray();
            var outside = shear ? (63, 80) : degrees < 0 ? (47, 170) : degrees > 0 ? (52, 80) : (30, 80);
            Distractor = new(outside.Item1 + .25, outside.Item2 + .5);
            foreach (var p in new[] { (110, 150), (170, 140), (220, 130), outside })
                for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                    if (dx * dx + dy * dy is >= 9 and <= 16) Pixels[(p.Item2 + dy) * 320 + p.Item1 + dx] = 0;
            int width = crop ? 360 : 320, height = crop ? 270 : 240;
            byte[] source = Enumerable.Repeat((byte)255, width * height).ToArray();
            for (int y = 0; y < 240; y++) Pixels.AsSpan(y * 320, 320).CopyTo(source.AsSpan((y + (crop ? 10 : 0)) * width + (crop ? 20 : 0), 320));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, source, width)));
            using (var output = File.Create(ImagePath)) encoder.Save(output);
            SourceBytes = File.ReadAllBytes(ImagePath);
            Workspace = new(Worker);
            var parent = Pump(Workspace.ImportImagesAsync([ImagePath], CancellationToken.None)).Single();
            Tab = crop ? Pump(Workspace.CreatePanelCropAsync(Workspace.GetPanelCropContext(parent.TabId)!, new(20, 10, 320, 240), CancellationToken.None)) : parent;
            var frame = Frame(degrees, shear);
            Calibration = new(new(frame.Origin.X, frame.Origin.Y), new(frame.YEnd.X, frame.YEnd.Y),
                new(frame.XEnd.X, frame.XEnd.Y), 10, printed ? 10 : 3, printed ? 0 : 1)
                { PreserveAffineGeometry = true, UsesPrintedXCoordinates = printed };
            Workspace.Calibrate(Tab.TabId, Calibration);
            SeriesId = Workspace.AddSeries(Tab.TabId, new("Owned", "●", MarkerShape.Circle, MarkerFill.Open, SemanticRole.Intervention)).SeriesId;
            SeedId = Workspace.AddPoint(Tab.TabId, SeriesId, 110.25, 150.5).PointId;
            var originals = (Dictionary<string, GraphReader.Imaging.ImmutableImageBytes>)typeof(ManualPreviewWorkspaceService)
                .GetField("_originalPanelBytesByTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Workspace)!;
            PanelBytes = originals[Tab.TabId].Copy();
        }
        internal MainWindowViewModel CreateViewModel() => new(Workspace)
            { SelectedTab = Tab, SelectedSeriesId = SeriesId, SelectedPointId = SeedId, MarkerExemplarExampleSizeText = "13" };
        public void Dispose() { Workspace.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
    }
    private sealed class Workspace(Worker worker) : ManualPreviewWorkspaceService(markerExemplarSuggestionService: worker)
    {
        internal void SetConfirmation(bool confirmed) => CurrentProject = CurrentProject with
        {
            Panels = CurrentProject.Panels.Select(panel => panel.Calibration is null ? panel
                : panel with { Calibration = panel.Calibration with { UserConfirmed = confirmed } }).ToArray(),
        };
    }
    // Runs the actual matcher in-process as existing backend tests do. The supplied
    // runtime receipt is test metadata, not evidence that this test host has a job cap.
    private sealed class Worker : IMarkerExemplarSuggestionService
    {
        internal int Calls { get; private set; }
        internal byte[]? Image { get; private set; }
        internal MarkerExemplarRequest? Request { get; private set; }
        public Task<MarkerExemplarResponse> FindAsync(MarkerExemplarRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            Calls++; Request = request; Image = image.ToArray();
            return Task.FromResult(MarkerExemplarWorker.Process(request, Image, new(2, 2500, .5, true), token));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
