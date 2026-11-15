// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class PrintedXWorkspaceTests
{
    [TestMethod]
    public void PrintedZeroAndFractionalXSurviveSaveReopenAndExportAsEstimated() => StaTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "printed-x-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string image = Path.Combine(directory, "owned.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
            using (var stream = File.Create(image)) encoder.Save(stream);
            using var workspace = new ManualPreviewWorkspaceService();
            var tab = Pump(workspace.ImportImagesAsync([image], CancellationToken.None)).Single();
            Assert.ThrowsExactly<ArgumentException>(() => workspace.Calibrate(tab.TabId,
                new(new(10, 100), new(10, 10), new(110, 100), 10, 0, 0) { UsesPrintedXCoordinates = true }));
            Assert.IsNull(tab.Calibration);
            workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 10), new(110, 100), 10, 10, 0)
                { UsesPrintedXCoordinates = true });
            string series = workspace.AddSeries(tab.TabId, new("Owned", "●", MarkerShape.Circle,
                MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
            var zero = workspace.AddPoint(tab.TabId, series, 10, 55);
            var fractional = workspace.AddPoint(tab.TabId, series, 35, 55);
            Assert.AreEqual(0d, zero.GraphX, 1e-9);
            Assert.AreEqual(2.5d, fractional.GraphX, 1e-9);
            Assert.AreEqual(1, zero.ObservationIndex);
            Assert.AreEqual(2, fractional.ObservationIndex);
            CalibrationRecord calibration = workspace.CurrentProject.Panels.Single().Calibration!;
            Assert.IsNull(calibration.SessionLattice);
            Assert.IsTrue(calibration.UserConfirmed);
            Assert.IsTrue(calibration.Anchors.All(anchor => anchor.Kind == CalibrationAnchorKind.Manual && anchor.EvidenceRegionId is null));
            ProductionWorkflowStageException blocked = Assert.ThrowsExactly<ProductionWorkflowStageException>(() => workspace.CreateConfirmedManualCalibrationBindings());
            Assert.AreEqual("WORKFLOW_PRINTED_X_DETECTION_UNSUPPORTED", blocked.Failure.Code);
            Assert.AreEqual(LocalizationKeys.ManualPrintedXDetectionUnsupported, blocked.Failure.UserMessageKey);
            Assert.IsTrue(blocked.Failure.Recoverable);
            MethodInfo restore = typeof(ManualPreviewWorkspaceService).GetMethod("FromDomainCalibration", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.IsNull(restore.Invoke(null, [calibration with { Anchors = calibration.Anchors.Take(2).ToArray() }]));
            Assert.IsNull(restore.Invoke(null, [calibration with { Anchors = calibration.Anchors.Select((anchor, index) =>
                index == 0 ? anchor with { Kind = CalibrationAnchorKind.Session1Y0 } : anchor).ToArray() }]));
            string project = Path.Combine(directory, "owned.garproj");
            Assert.IsTrue(Pump(workspace.SaveProjectAsync(project, CancellationToken.None)).IsSuccess);
            using var reopened = new ManualPreviewWorkspaceService();
            var restored = Pump(reopened.OpenProjectAsync(project, CancellationToken.None)).Single();
            Assert.IsTrue(restored.Calibration!.UsesPrintedXCoordinates);
            Assert.AreEqual(0d, restored.Calibration.FirstVisibleSession);
            using (var vm = new MainWindowViewModel(reopened))
            {
                Assert.IsTrue(vm.ManualUsesPrintedXCoordinates);
                Assert.AreEqual("0", vm.ManualFirstVisibleSessionText);
                vm.ManualFirstVisibleSessionText = "-1.25";
                Assert.IsTrue(vm.IsManualFirstVisibleSessionInputValid);
                vm.SelectedTab = null;
                vm.SelectedTab = restored;
                Assert.IsTrue(vm.ManualUsesPrintedXCoordinates);
                Assert.AreEqual("-1.25", vm.ManualFirstVisibleSessionText);
                vm.ManualFirstVisibleSessionText = "NaN";
                Assert.IsFalse(vm.IsManualFirstVisibleSessionInputValid);
                vm.ManualUsesPrintedXCoordinates = false;
                vm.ManualFirstVisibleSessionText = "0";
                Assert.IsFalse(vm.IsManualFirstVisibleSessionInputValid);
            }
            var result = Pump(reopened.ExportAsync(restored.TabId, Path.Combine(directory, "export"), CancellationToken.None));
            Assert.IsTrue(result.Succeeded, string.Join(" | ", result.Failures.Select(failure => failure.TechnicalMessage)));
            Assert.AreEqual(ExportMode.PrintedSession, result.Mode);
            AssertAuditArtifacts(result.AuditArtifacts);
            using (var production = new ProductionWorkspaceService())
            {
                var productionTab = Pump(production.ImportImagesAsync([image], CancellationToken.None)).Single();
                production.Calibrate(productionTab.TabId, new(new(10, 100), new(10, 10), new(110, 100), 10, 10, 0)
                    { UsesPrintedXCoordinates = true });
                string productionSeries = production.AddSeries(productionTab.TabId, new("Owned", "●", MarkerShape.Circle,
                    MarkerFill.Filled, SemanticRole.Intervention)).SeriesId;
                production.AddPoint(productionTab.TabId, productionSeries, 10, 55);
                production.AddPoint(productionTab.TabId, productionSeries, 35, 55);
                var productionResult = Pump(production.ExportAsync(productionTab.TabId, Path.Combine(directory, "production-export"), CancellationToken.None));
                Assert.IsTrue(productionResult.Succeeded, string.Join(" | ", productionResult.Failures.Select(failure => failure.TechnicalMessage)));
                Assert.AreEqual(ExportMode.PrintedSession, productionResult.Mode);
                AssertAuditArtifacts(productionResult.AuditArtifacts);
                ProductionWorkflowStageException productionBlocked = Assert.ThrowsExactly<ProductionWorkflowStageException>(
                    () => Pump(Complete(production.RunAutomaticDetectionAsync(CancellationToken.None))));
                Assert.AreEqual("WORKFLOW_PRINTED_X_DETECTION_UNSUPPORTED", productionBlocked.Failure.Code);
            }
            using var manualVm = new MainWindowViewModel(reopened);
            manualVm.ManualUsesPrintedXCoordinates = true;
            manualVm.ManualFirstVisibleSessionText = "-1.25";
            manualVm.ManualXMaximum = 10;
            manualVm.ManualYMaximum = 10;
            manualVm.StartCalibrationCommand.Execute(null);
            Pump(Complete(manualVm.HandleCanvasPointAsync(new Point(10, 100))));
            Pump(Complete(manualVm.HandleCanvasPointAsync(new Point(10, 10))));
            Pump(Complete(manualVm.HandleCanvasPointAsync(new Point(110, 100))));
            Assert.IsTrue(restored.Calibration!.UsesPrintedXCoordinates);
            Assert.AreEqual(-1.25, restored.Calibration.FirstVisibleSession, 1e-9);
        }
        finally { Directory.Delete(directory, true); }
    });

    private static void AssertAuditArtifacts(IReadOnlyList<ExtendedAuditArtifact> artifacts)
    {
        Assert.HasCount(2, artifacts);
        foreach (ExtendedAuditArtifact artifact in artifacts)
        {
            var rows = artifact.Rows.OrderBy(row => row.XValue).ToArray();
            Assert.HasCount(2, rows);
            Assert.AreEqual(0d, rows[0].XValue, 1e-9);
            Assert.AreEqual(2.5d, rows[1].XValue, 1e-9);
            Assert.IsTrue(rows.All(row => row.XSource == ExportXValueSource.Estimated));
        }
    }

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static async Task<bool> Complete(Task task)
    {
        await task;
        return true;
    }
}
