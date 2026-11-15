// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.App.Models;
using GraphReader.App.Integration.Workflow;
using GraphReader.Export;
using GraphReader.Domain;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class RotatedManualCalibrationTests
{
    [TestMethod]
    [DataRow(-2d)]
    [DataRow(2d)]
    public void ActualRotatedAnchorsMapAnInteriorPoint(double degrees) => StaTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "owned-affine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string image = Path.Combine(directory, "owned.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 400, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 480 * 400).ToArray(), 480)));
            using (var stream = File.Create(image)) encoder.Save(stream);
            using var workspace = new ManualPreviewWorkspaceService();
            var tab = Pump(workspace.ImportImagesAsync([image], CancellationToken.None)).Single();
            GraphReader.Axis.PixelPoint origin = Rotate(70, 280, degrees);
            GraphReader.Axis.PixelPoint upper = Rotate(70, 60, degrees);
            GraphReader.Axis.PixelPoint final = Rotate(330, 280, degrees);
            workspace.Calibrate(tab.TabId, new(origin, upper, final, 10, 5));
            Assert.IsNull(tab.Calibration!.XTransform);
            Assert.IsNull(tab.Calibration.YTransform);
            string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
                MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
            GraphReader.Axis.PixelPoint pixel = Rotate(167.5, 192, degrees);
            var point = workspace.AddPoint(tab.TabId, series, pixel.X, pixel.Y);
            Assert.AreEqual(2.5, point.GraphX, 1e-9);
            Assert.AreEqual(4, point.GraphY, 1e-9);
            Assert.AreEqual(1d, ManualCalibrationMapper.Map(tab.Calibration, origin.X, origin.Y).X, 1e-9);
            Assert.AreEqual(10d, ManualCalibrationMapper.Map(tab.Calibration, upper.X, upper.Y).Y, 1e-9);
            Assert.AreEqual(5d, ManualCalibrationMapper.Map(tab.Calibration, final.X, final.Y).X, 1e-9);
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    [DataRow(-2d, false, 1d, false)]
    [DataRow(2d, true, 0d, false)]
    [DataRow(0.1d, true, 0d, false)]
    [DataRow(90d, false, 2d, false)]
    [DataRow(-90d, true, 0d, false)]
    [DataRow(0d, false, 1d, false)]
    [DataRow(0d, true, -1.25d, false)]
    [DataRow(2d, false, 2d, true)]
    [DataRow(-2d, true, 0d, true)]
    public void OriginalAnchorsAndMappedValuesSurviveCropSaveReopenAndExport(double degrees, bool printed, double firstX, bool crop) =>
        WithWorkspace((workspace, tab, directory) =>
    {
        if (crop)
            tab = Pump(workspace.CreatePanelCropAsync(workspace.GetPanelCropContext(tab.TabId)!, new(40, 20, 400, 360), CancellationToken.None));
        GraphReader.Axis.PixelPoint origin = Rotate(70, 280, degrees), upper = Rotate(70, 60, degrees), final = Rotate(330, 280, degrees);
        var request = new ManualCalibrationRequest(origin, upper, final, 10, firstX + 4, firstX)
            { UsesPrintedXCoordinates = printed, PreserveAffineGeometry = degrees is 0d or 0.1d };
        workspace.Calibrate(tab.TabId, request);
        string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
        GraphReader.Axis.PixelPoint firstPixel = Rotate(70, 192, degrees), secondPixel = Rotate(167.5, 192, degrees);
        workspace.AddPoint(tab.TabId, series, firstPixel.X, firstPixel.Y);
        workspace.AddPoint(tab.TabId, series, secondPixel.X, secondPixel.Y);
        string tabId = tab.TabId;
        var saved = workspace.CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString("D") == tab.PanelId).Calibration!;
        Assert.IsTrue(saved.UserConfirmed);
        Assert.IsTrue(saved.Anchors.All(anchor => anchor.EvidenceRegionId is null));
        Assert.AreEqual(printed ? CalibrationAnchorKind.Manual : firstX > 1 ? CalibrationAnchorKind.FirstVisibleSessionY0 : CalibrationAnchorKind.Session1Y0,
            saved.Anchors[0].Kind);
        Assert.AreEqual(origin.X + (crop ? 40 : 0), saved.Anchors[0].Screen.X, 1e-9);
        Assert.AreEqual(origin.Y + (crop ? 20 : 0), saved.Anchors[0].Screen.Y, 1e-9);
        Assert.AreEqual(degrees == 0 && !printed, saved.SessionLattice is not null);
        Assert.AreEqual(degrees != 0, saved.Reasons.Contains(ManualCalibrationMapper.AffineReason, StringComparer.Ordinal));
        Assert.IsEmpty(new ProjectValidator().Validate(workspace.CurrentProject));
        string path = Path.Combine(directory, "owned.garproj");
        Assert.IsTrue(Pump(workspace.SaveProjectAsync(path, CancellationToken.None)).IsSuccess);
        using var reopened = new ManualPreviewWorkspaceService();
        var restored = Pump(reopened.OpenProjectAsync(path, CancellationToken.None)).Single(item => item.TabId == tabId);
        Assert.AreEqual(printed, restored.Calibration!.UsesPrintedXCoordinates);
        AssertPoint(origin, restored.Calibration.Session1Y0);
        AssertPoint(upper, restored.Calibration.Session1YMaximum);
        AssertPoint(final, restored.Calibration.SessionMaximumY0);
        Assert.AreEqual(degrees == 0, restored.Calibration.XTransform is not null);
        CollectionAssert.AreEqual(saved.Reasons.ToArray(), reopened.CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString("D") == restored.PanelId).Calibration!.Reasons.ToArray());
        // Exercise the reopened mapping rather than relying on persisted point values.
        reopened.MovePoint(restored.TabId, restored.Points[1].PointId, secondPixel.X, secondPixel.Y);
        Assert.AreEqual(firstX + 1.5, restored.Points[1].GraphX, 1e-9);
        Assert.AreEqual(4d, restored.Points[1].GraphY, 1e-9);
        var exported = Pump(reopened.ExportAsync(restored.TabId, Path.Combine(directory, "export"), CancellationToken.None));
        Assert.IsTrue(exported.Succeeded, string.Join(" | ", exported.Failures.Select(failure => failure.TechnicalMessage)));
        Assert.AreEqual(printed || firstX > 1 ? ExportMode.PrintedSession : ExportMode.ObservationOrder, exported.Mode);
        Assert.HasCount(2, exported.AuditArtifacts);
        foreach (var audit in exported.AuditArtifacts)
        {
            var rows = audit.Rows.OrderBy(row => row.XValue).ToArray();
            Assert.HasCount(2, rows);
            Assert.AreEqual(4d, rows[0].YValue, 1e-9);
            Assert.AreEqual(4d, rows[1].YValue, 1e-9);
            Assert.AreEqual(printed || firstX > 1 ? firstX : 1d, rows[0].XValue, 1e-9);
            Assert.AreEqual(printed || firstX > 1 ? firstX + 1.5 : 2d, rows[1].XValue, 1e-9);
            Assert.AreEqual(firstPixel.X + (crop ? 40 : 0), rows[0].OriginalPixel.X, 1e-9);
            Assert.AreEqual(firstPixel.Y + (crop ? 20 : 0), rows[0].OriginalPixel.Y, 1e-9);
        }
        string minimal = exported.MinimalArtifacts.Single().WrittenPath!;
        string[] csvRows = File.ReadAllLines(minimal).Skip(1).ToArray();
        Assert.HasCount(2, csvRows);
        Assert.IsTrue(csvRows.All(row => Math.Abs(double.Parse(row.Split(',')[1], CultureInfo.InvariantCulture) - 4) < 1e-9));
        if (degrees != 0)
        {
            var blocked = Assert.ThrowsExactly<ProductionWorkflowStageException>(() => reopened.CreateConfirmedManualCalibrationBindings());
            Assert.AreEqual("WORKFLOW_ROTATED_CALIBRATION_DETECTION_UNSUPPORTED", blocked.Failure.Code);
            Assert.IsTrue(blocked.Failure.Recoverable);
            AssertPoint(origin, restored.Calibration.Session1Y0);
        }
    });

    private static void AssertPoint(GraphReader.Axis.PixelPoint expected, GraphReader.Axis.PixelPoint actual)
    {
        Assert.AreEqual(expected.X, actual.X, 1e-9);
        Assert.AreEqual(expected.Y, actual.Y, 1e-9);
    }

    [TestMethod]
    public void InvalidOrDegenerateAnchorsNeverChangeProject() => WithWorkspace((workspace, tab, _) =>
    {
        string before = JsonSerializer.Serialize(workspace.CurrentProject);
        ManualCalibrationRequest[] invalid =
        [
            new(new(70, 280), new(70, 280), new(330, 280), 10, 5),
            new(new(70, 280), new(80, 270), new(90, 260), 10, 5),
            new(new(double.NaN, 280), new(70, 60), new(330, 280), 10, 5),
            new(new(70, 280), new(70, 60), new(double.PositiveInfinity, 280), 10, 5),
            new(new(70, 280), new(70, 60), new(330, 280), 0, 5),
            new(new(70, 280), new(70, 60), new(330, 280), 10, 1),
        ];
        foreach (var request in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => workspace.Calibrate(tab.TabId, request));
            Assert.IsNull(tab.Calibration);
            Assert.AreEqual(before, JsonSerializer.Serialize(workspace.CurrentProject));
        }
    });

    [TestMethod]
    public void InvalidSavedAffineAnchorsAreNotRestored() => WithWorkspace((workspace, tab, _) =>
    {
        workspace.Calibrate(tab.TabId, new(Rotate(70, 280, 2), Rotate(70, 60, 2), Rotate(330, 280, 2), 10, 5));
        var saved = workspace.CurrentProject.Panels.Single().Calibration!;
        MethodInfo restore = typeof(ManualPreviewWorkspaceService).GetMethod("FromDomainCalibration", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNull(restore.Invoke(null, [saved with { UserConfirmed = false }]));
        Assert.IsNull(restore.Invoke(null, [saved with { Status = CalibrationStatus.NeedsReview }]));
        Assert.IsNull(restore.Invoke(null, [saved with { Reasons = [] }]));
        Assert.IsNull(restore.Invoke(null, [saved with { SessionLattice = new SessionLatticeRecord(90, 65, null, null, 1, "manual_three_anchor") }]));
        Assert.IsNull(restore.Invoke(null, [saved with { Anchors = saved.Anchors.Select(anchor => anchor with { Screen = saved.Anchors[0].Screen }).ToArray() }]));
        var legacyPrinted = saved with { Reasons = [], Anchors = saved.Anchors.Select((anchor, index) => anchor with
        {
            Kind = CalibrationAnchorKind.Manual,
            Screen = index switch
            {
                0 => new(90, 290),
                1 => new(91, 70),
                _ => new(90, 291),
            },
        }).ToArray() };
        Assert.IsNull(restore.Invoke(null, [legacyPrinted]), "A nonsingular affine basis must not reinterpret an invalid old scalar span.");
    });

    [TestMethod]
    public void RecalibrationOrdersNearbyObservationsByMappedXRatherThanScreenX() => WithWorkspace((workspace, tab, _) =>
    {
        workspace.Calibrate(tab.TabId, new(Rotate(70, 280, 0), Rotate(70, 60, 0), Rotate(330, 280, 0), 10, 5));
        string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
        var firstPixel = Rotate(200, 80, 2);
        var nextPixel = Rotate(201, 270, 2);
        Assert.IsGreaterThan(nextPixel.X, firstPixel.X, "The fixture must reverse screen-X and graph-X order.");
        var first = workspace.AddPoint(tab.TabId, series, firstPixel.X, firstPixel.Y);
        var next = workspace.AddPoint(tab.TabId, series, nextPixel.X, nextPixel.Y);
        Assert.AreEqual(2, first.ObservationIndex);
        Assert.AreEqual(1, next.ObservationIndex);
        workspace.Calibrate(tab.TabId, new(Rotate(70, 280, 2), Rotate(70, 60, 2), Rotate(330, 280, 2), 10, 5));
        Assert.AreEqual(3d, first.GraphX, 1e-9);
        Assert.AreEqual(3d + 4d / 260, next.GraphX, 1e-9);
        Assert.AreEqual(1, first.ObservationIndex);
        Assert.AreEqual(2, next.ObservationIndex);
        workspace.MovePoint(tab.TabId, first.PointId, firstPixel.X, firstPixel.Y);
        Assert.AreEqual(1, first.ObservationIndex);
        Assert.AreEqual(2, next.ObservationIndex);
    });

    [TestMethod]
    public void ExistingDividersBlockAffineCalibrationAndAffineStatesBlockDividerEditsWithoutMutation() => WithWorkspace((workspace, tab, directory) =>
    {
        var divider = workspace.AddPhaseDivider(tab.TabId, 200, "b", "Owned intervention");
        string before = JsonSerializer.Serialize(workspace.CurrentProject);
        var request = new ManualCalibrationRequest(Rotate(70, 280, 2), Rotate(70, 60, 2), Rotate(330, 280, 2), 10, 5);
        var rejected = Assert.ThrowsExactly<ProductionWorkflowStageException>(() => workspace.Calibrate(tab.TabId, request));
        Assert.AreEqual("WORKFLOW_AFFINE_PHASES_UNSUPPORTED", rejected.Failure.Code);
        Assert.IsTrue(rejected.Failure.Recoverable);
        Assert.AreEqual(before, JsonSerializer.Serialize(workspace.CurrentProject));
        Assert.IsNull(tab.Calibration);
        // Default human clicks retain the pre-existing two-pixel tolerance, including phase compatibility.
        workspace.Calibrate(tab.TabId, new(new(90, 290), new(92, 70), new(350, 291), 10, 5));
        Assert.IsTrue(ManualCalibrationMapper.IsAxisAligned(tab.Calibration!));
        Assert.IsNotNull(workspace.CurrentProject.Panels.Single().Calibration!.SessionLattice);
        Assert.IsEmpty(workspace.CurrentProject.Panels.Single().Calibration!.Reasons);
        workspace.MovePhaseDivider(tab.TabId, divider.DividerId, 205);
        workspace.DeletePhaseDivider(tab.TabId, divider.DividerId);
        workspace.Calibrate(tab.TabId, request);
        before = JsonSerializer.Serialize(workspace.CurrentProject);
        Assert.ThrowsExactly<ProductionWorkflowStageException>(() => workspace.AddPhaseDivider(tab.TabId, 200, "b", "Owned intervention"));
        Assert.ThrowsExactly<ProductionWorkflowStageException>(() => workspace.MovePhaseDivider(tab.TabId, divider.DividerId, 210));
        Assert.AreEqual(before, JsonSerializer.Serialize(workspace.CurrentProject));
        Assert.IsEmpty(tab.PhaseDividers);
        // Model a saved/externally supplied incompatible state. Export must fail before synchronization.
        tab.PhaseDividers.Add(new EditablePhaseDivider(divider.DividerId, 200, "b", "Owned intervention"));
        Assert.ThrowsExactly<ProductionWorkflowStageException>(() => Pump(workspace.ExportAsync(tab.TabId,
            Path.Combine(directory, "blocked-export"), CancellationToken.None)));
        Assert.AreEqual(before, JsonSerializer.Serialize(workspace.CurrentProject));
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "blocked-export")));
    });

    [TestMethod]
    public void ProductionManualBaselineOnlyAffineExportRetainsMappedY() => WithWorkspace((workspace, tab, directory) =>
    {
        workspace.Calibrate(tab.TabId, new(Rotate(70, 280, 2), Rotate(70, 60, 2), Rotate(330, 280, 2), 10, 5));
        string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
        var pixel = Rotate(167.5, 192, 2);
        workspace.AddPoint(tab.TabId, series, pixel.X, pixel.Y);
        var exported = Pump(workspace.ExportAsync(tab.TabId, Path.Combine(directory, "export"), CancellationToken.None));
        Assert.IsTrue(exported.Succeeded, string.Join(" | ", exported.Failures.Select(failure => failure.TechnicalMessage)));
        Assert.IsTrue(exported.AuditArtifacts.SelectMany(artifact => artifact.Rows).All(row => Math.Abs(row.YValue - 4) < 1e-9));
    }, production: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExistingValidatedNearAlignedRecordsRetainScalarMappingAndPhases(bool printed) => WithWorkspace((workspace, tab, directory) =>
    {
        workspace.Calibrate(tab.TabId, new(Rotate(70, 280, 0), Rotate(70, 60, 0), Rotate(330, 280, 0), 10, printed ? 4 : 5, printed ? 0 : 1)
            { UsesPrintedXCoordinates = printed });
        var divider = workspace.AddPhaseDivider(tab.TabId, 250, "b", "Owned intervention");
        string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
        var pixel = Rotate(167.5, 192, 0);
        workspace.AddPoint(tab.TabId, series, pixel.X, pixel.Y);
        PanelRecord original = workspace.CurrentProject.Panels.Single();
        CalibrationRecord oldCalibration = original.Calibration!;
        // Previous builds accepted these one-pixel cross-axis offsets while fitting X and Y independently.
        oldCalibration = oldCalibration with { Anchors = oldCalibration.Anchors.Select((anchor, index) => index switch
        {
            1 => anchor with { Screen = anchor.Screen with { X = anchor.Screen.X + 1 } },
            2 => anchor with { Screen = anchor.Screen with { Y = anchor.Screen.Y - 1 } },
            _ => anchor,
        }).ToArray() };
        string path = Path.Combine(directory, "legacy.garproj");
        Assert.IsTrue(Pump(new ProjectFileStore().SaveAsync(workspace.CurrentProject with
            { Panels = [original with { Calibration = oldCalibration }] }, path, CancellationToken.None)).IsSuccess);
        using var reopened = new ManualPreviewWorkspaceService();
        var restored = Pump(reopened.OpenProjectAsync(path, CancellationToken.None)).Single();
        Assert.IsTrue(ManualCalibrationMapper.IsAxisAligned(restored.Calibration!));
        Assert.IsNotNull(restored.Calibration!.XTransform);
        Assert.HasCount(1, restored.PhaseDividers);
        reopened.MovePoint(restored.TabId, restored.Points.Single().PointId, pixel.X, pixel.Y);
        Assert.AreEqual(printed ? 1.5 : 2.5, restored.Points.Single().GraphX, 1e-9);
        Assert.AreEqual(4, restored.Points.Single().GraphY, 1e-9);
        reopened.MovePhaseDivider(restored.TabId, divider.DividerId, 255);
        Assert.AreEqual("b", restored.PhaseDividers.Single().Code);
        Assert.IsTrue(Pump(reopened.SaveProjectAsync(Path.Combine(directory, "legacy-saved.garproj"), CancellationToken.None)).IsSuccess);
        var saved = reopened.CurrentProject.Panels.Single().Calibration!;
        Assert.AreEqual(!printed, saved.SessionLattice is not null);
        Assert.IsEmpty(saved.Reasons);
        CollectionAssert.AreEqual(oldCalibration.Anchors.ToArray(), saved.Anchors.ToArray());
        var exported = Pump(reopened.ExportAsync(restored.TabId, Path.Combine(directory, "export"), CancellationToken.None));
        Assert.IsTrue(exported.Succeeded, string.Join(" | ", exported.Failures.Select(failure => failure.TechnicalMessage)));
        Assert.HasCount(2, exported.AuditArtifacts);
        Assert.IsTrue(exported.AuditArtifacts.SelectMany(artifact => artifact.Rows).All(row => row.Phase == "a" && Math.Abs(row.YValue - 4) < 1e-9));
    });

    private static void WithWorkspace(Action<ManualPreviewWorkspaceService, GraphReader.App.ViewModels.WorkspaceTabViewModel, string> action,
        bool production = false) => StaTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "owned-affine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string image = Path.Combine(directory, "owned.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 400, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 480 * 400).ToArray(), 480)));
            using (var stream = File.Create(image)) encoder.Save(stream);
            using ManualPreviewWorkspaceService workspace = production ? new ProductionWorkspaceService() : new ManualPreviewWorkspaceService();
            action(workspace, Pump(workspace.ImportImagesAsync([image], CancellationToken.None)).Single(), directory);
        }
        finally { Directory.Delete(directory, true); }
    });

    private static GraphReader.Axis.PixelPoint Rotate(double x, double y, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cosine = Math.Cos(radians), sine = Math.Sin(radians);
        return new(220 + cosine * (x - 200) - sine * (y - 180),
            190 + sine * (x - 200) + cosine * (y - 180));
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}
