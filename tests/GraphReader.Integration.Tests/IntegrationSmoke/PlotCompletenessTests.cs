// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GraphReader.App;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class PlotCompletenessTests(TestContext context)
{
    [STATestMethod(UseSTASynchronizationContext = true)]
    [DataRow("a", 30, 80, 90, 60, "1,2,a", "3,4,a")]
    [DataRow("b", 150, 40, 180, 20, "1,6,b", "2,8,b")]
    public async Task PreviewSeparatesPlacedRowsFromSourcePlotCompleteness(
        string phase, int firstX, int firstY, int lastX, int lastY, string firstRow, string lastRow)
    {
        // The two phases both print ticks 1–3. A2 and B3 have no observation.
        // Use the printed X anchors for one phase at a time, as in phase-copy export.
        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        using var temp = new IntegrationSmokeTestEnvironment();
        using var workspace = new ManualPreviewWorkspaceService();
        var token = CancellationToken.None;
        string fixture = Path.Combine(RepositoryRoot.Find(), "tests", "GraphReader.Integration.Tests",
            "Validation", "Specifications", "phase-restart-coverage.png");
        var tab = (await workspace.ImportImagesAsync([fixture], token)).Single();
        var series = workspace.AddSeries(tab.TabId, new("Response", "●", MarkerShape.Circle,
            MarkerFill.Filled, SemanticRole.Intervention));
        workspace.Calibrate(tab.TabId, new(new(firstX, 100), new(firstX, 20), new(firstX + 60, 100), 8, 3, 1)
            { UsesPrintedXCoordinates = true });
        workspace.AddPhaseDivider(tab.TabId, 120, "b", "Intervention");
        var first = workspace.AddPoint(tab.TabId, series.SeriesId, firstX, firstY);
        workspace.SetPointReviewStatus(tab.TabId, first.PointId, ReviewStatus.Accepted);
        if (phase == "a")
            workspace.ConfirmPhaseLabel(tab.TabId, workspace.CurrentProject.Panels.Single().Phases.Single(p => p.Code == "a"));

        var resources = new ResourceDictionary();
        foreach (string name in new[] { "DesignTokens", "LightTheme", "Controls" })
            resources.MergedDictionaries.Add(new() { Source = new Uri($"/GraphReader.App;component/Themes/{name}.xaml", UriKind.Relative) });
        var localizer = new LocalizationService(resources);
        using var vm = new MainWindowViewModel(workspace, localizationService: localizer) { SelectedTab = tab };
        var window = new MainWindow(null, resources);
        ((MainWindowViewModel)window.DataContext).Dispose();
        window.DataContext = vm;
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            await CheckPreview("partial", [firstRow], 1);
            var last = workspace.AddPoint(tab.TabId, series.SeriesId, lastX, lastY);
            await CheckPreview("pending", [firstRow, lastRow], 1);
            workspace.SetPointReviewStatus(tab.TabId, last.PointId, ReviewStatus.Accepted);
            await CheckPreview("accepted", [firstRow, lastRow], 2);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }

        async Task CheckPreview(string stage, string[] expectedRows, int strictRows)
        {
            vm.OpenExportPreviewCommand.Execute(null);
            window.UpdateLayout();
            Assert.AreEqual(expectedRows.Length, vm.ExportSummary!.PointCount);
            Assert.AreEqual(0, vm.ExportSummary.WarningCount);
            Assert.IsTrue(vm.ConfirmExportCommand.CanExecute(null));
            var result = await workspace.ExportAsync(tab.TabId, temp.PathFor(stage), token);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual("x_value,y_value,phase\n" + string.Join('\n', expectedRows) + "\n",
                await File.ReadAllTextAsync(result.MinimalArtifacts.Single().WrittenPath!, token));
            var rows = result.AuditArtifacts.Single(a => a.Format == ExportAuditArtifactFormat.Json).Rows;
            Assert.HasCount(expectedRows.Length, rows);
            Assert.IsTrue(workspace.CurrentProject.Panels.Single().Calibration!.UserConfirmed);
            foreach (var row in rows)
            {
                Assert.AreEqual(ExportMode.PrintedSession, row.ExportMode);
                Assert.AreEqual(ExportCalibrationStatus.Valid, row.CalibrationStatus);
                Assert.AreEqual("manual", row.ReviewEvidence!.PhaseSource);
                Assert.AreEqual("explicit_label", row.ReviewEvidence.PhaseLabelReview);
            }
            Assert.AreEqual(strictRows, rows.Count(r => r.ReviewStatus == ExportReviewStatus.Accepted
                && r.ReviewEvidence!.PointActions.Split('|').Contains("point_accepted")));
            var note = (TextBlock?)window.FindName("ExportPlotCompleteness");
            Assert.IsNotNull(note, "The export preview needs a source plot completeness statement.");
            StringAssert.Contains(note.Text, "Source plot completeness is unverified");
            AssertReadable(note);
            var caption = (TextBlock)window.FindName("ExportPlacedPointsLabel");
            Assert.AreEqual("Placed points", caption.Text);
            AssertReadable(caption);
            var confirm = (Button)window.FindName("ExportConfirmButton");
            Point center = confirm.TranslatePoint(new Point(confirm.ActualWidth / 2, confirm.ActualHeight / 2), window);
            var hit = VisualTreeHelper.HitTest(window, center)?.VisualHit;
            Assert.IsTrue(hit == confirm || hit is not null && confirm.IsAncestorOf(hit), "Export button is covered or clipped.");
            context.WriteLine($"{phase}/{stage}: {strictRows}/{expectedRows.Length} placed rows meet strict evidence. " +
                $"Notice {note.ActualWidth} × {note.ActualHeight} visible at {window.ActualWidth} × {window.ActualHeight}.");
        }

        void AssertReadable(TextBlock text)
        {
            Assert.IsTrue(text.IsVisible);
            Assert.AreEqual(TextWrapping.Wrap, text.TextWrapping);
            Assert.AreEqual(TextTrimming.None, text.TextTrimming);
            // IsVisible alone also passes for a label clipped by its containing pane.
            var start = text.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            Assert.AreEqual(text.Text, start.GetTextInRun(LogicalDirection.Forward));
            for (int i = 0; i < text.Text.Length; i++)
            {
                if (char.IsWhiteSpace(text.Text[i])) continue;
                Rect glyph = start.GetPositionAtOffset(i)!.GetCharacterRect(LogicalDirection.Forward);
                Point at = text.TranslatePoint(new Point(glyph.Left + 1, glyph.Top + glyph.Height / 2), window);
                Assert.AreSame(text, VisualTreeHelper.HitTest(window, at)?.VisualHit,
                    $"{text.Name} character {i + 1} is clipped or covered at {at}.");
            }
        }
    }
}
