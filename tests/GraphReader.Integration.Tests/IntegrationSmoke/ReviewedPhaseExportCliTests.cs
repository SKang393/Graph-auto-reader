// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ReviewedPhaseExportCliTests(TestContext context)
{
    private static readonly double[] ExpectedBaselinePixels = [30, 90];
    private static readonly double[] ExpectedInterventionPixels = [150, 180];

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task RestartedScaleKeepsMissingVisitAndRequiresIndependentEvidence()
    {
        string? tool = Environment.GetEnvironmentVariable("GRAPHREADER_PHASE_EXPORT_DLL");
        if (string.IsNullOrEmpty(tool)) Assert.Inconclusive("Set GRAPHREADER_PHASE_EXPORT_DLL to the built phase-export tool.");
        using var temp = new IntegrationSmokeTestEnvironment();
        using var workspace = new ManualPreviewWorkspaceService();
        var token = CancellationToken.None;
        var image = Path.Combine(RepositoryRoot.Find(), "tests", "GraphReader.Integration.Tests",
            "Validation", "Specifications", "phase-restart-coverage.png");
        var tab = (await workspace.ImportImagesAsync([image], token)).Single();
        var series = workspace.AddSeries(tab.TabId, new("Response", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        workspace.Calibrate(tab.TabId, new(new(30, 100), new(30, 20), new(210, 100), 8, 7));
        workspace.AddPhaseDivider(tab.TabId, 120, "b", "Intervention");
        foreach (var (x, y) in new[] { (30, 80), (90, 60), (150, 40), (180, 20) })
        {
            var point = workspace.AddPoint(tab.TabId, series.SeriesId, x, y);
            if (x != 180) workspace.SetPointReviewStatus(tab.TabId, point.PointId, ReviewStatus.Accepted);
        }

        string project = temp.PathFor("observations.garproj");
        Assert.IsTrue((await workspace.SaveProjectAsync(project, token)).IsSuccess);
        var phases = workspace.CurrentProject.Panels.Single().Phases.ToDictionary(p => p.Code);
        await Check("a", "30", "90", "baseline-unconfirmed", "requires explicit point and phase evidence");
        workspace.ConfirmPhaseLabel(tab.TabId, phases["a"]);
        Assert.IsTrue((await workspace.SaveProjectAsync(project, token)).IsSuccess);
        await Check("b", "150", "210", "point-pending", "requires explicit point and phase evidence");
        await Check("a", "150", "210", "wrong-phase-ticks", "inside the selected phase");
        await Check("a", "30", "90", "accepted", null);

        await VerifyExport("a", "accepted", 30, 90, "1,2,a\n3,4,a\n", ExpectedBaselinePixels);
        workspace.SetPointReviewStatus(tab.TabId, tab.Points.Single(p => p.PixelX == 180).PointId, ReviewStatus.Accepted);
        Assert.IsTrue((await workspace.SaveProjectAsync(project, token)).IsSuccess);
        await Check("b", "150", "210", "ticks-unconfirmed", "--confirm-ticks", confirmTicks: false);
        await Check("b", "150", "210", "restarted", null);
        await VerifyExport("b", "restarted", 150, 210, "1,6,b\n2,8,b\n", ExpectedInterventionPixels);
        using var reopened = new ManualPreviewWorkspaceService();
        var full = (await reopened.OpenProjectAsync(project, token)).Single();
        Assert.HasCount(4, full.Points);
        var blocked = await reopened.ExportAsync(full.TabId, temp.PathFor("whole"), token);
        Assert.AreEqual(ExportContract.ObservationOrderReviewRequired, blocked.Failures.Single().Code);

        // Keep one independently reviewed observation in a second panel of the same project.
        var other = (await reopened.ImportImagesAsync([image], token)).Single();
        var otherSeries = reopened.AddSeries(other.TabId, new("Control", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        reopened.Calibrate(other.TabId, new(new(30, 100), new(30, 20), new(210, 100), 8, 7));
        var otherPoint = reopened.AddPoint(other.TabId, otherSeries.SeriesId, 30, 80);
        reopened.SetPointReviewStatus(other.TabId, otherPoint.PointId, ReviewStatus.Accepted);
        var control = reopened.CurrentProject.Panels.Single(p => p.PanelId.Value.ToString("D") == other.PanelId);
        reopened.ConfirmPhaseLabel(other.TabId, control.Phases.Single());
        using var reviewer = new MainWindowViewModel(reopened) { SelectedTab = full };
        // Repeating the same anchors preserves approval. Moving the right anchor leaves the first X unchanged.
        await SetAnchors(20, 210);
        Assert.IsFalse(reviewer.ReviewIssues.Any(issue => issue.PointStatusToAccept.HasValue));
        await SetAnchors(20, 150);
        CollectionAssert.AreEquivalent(full.Points.Where(p => p.PixelX != 30).Select(p => p.PointId).ToArray(),
            reviewer.ReviewIssues.Where(i => i.PointStatusToAccept.HasValue).Select(i => i.EntityId).ToArray());
        reviewer.UndoCommand.Execute(null);
        Assert.IsFalse(reviewer.ReviewIssues.Any(issue => issue.PointStatusToAccept.HasValue));

        await SetAnchors(60, 210);
        Assert.AreEqual(4d, full.Points.Single(p => p.PixelX == 30).GraphY);
        Assert.IsTrue((await reopened.SaveProjectAsync(project, token)).IsSuccess);
        await Check("a", "30", "90", "changed-anchor", "requires explicit point and phase evidence");
        await Check("a", "30", "90", "other-panel", null, controlPanel: control);
        await VerifyExport("a", "other-panel", 30, 90, "1,2,a\n", [30], control.Phases.Single().PhaseId.Value);

        using var saved = new ManualPreviewWorkspaceService();
        var edited = (await saved.OpenProjectAsync(project, token)).Single(t => t.PanelId == full.PanelId);
        using var review = new MainWindowViewModel(saved) { SelectedTab = edited };
        var pending = review.ReviewIssues.Where(i => i.PointStatusToAccept == ReviewStatus.Unreviewed).ToArray();
        CollectionAssert.AreEquivalent(full.Points.Select(p => p.PointId).ToArray(), pending.Select(i => i.EntityId).ToArray());
        Assert.IsFalse(review.ReviewIssues.Any(i => i.PhaseLabelToVerify is not null));
        review.OpenExportPreviewCommand.Execute(null);
        Assert.IsNotNull(review.ExportSummary);
        CollectionAssert.AreEquivalent(pending.Select(i => i.EntityId).ToArray(), review.ExportSummary.WarningIssues
            .Where(i => i.PointStatusToAccept.HasValue).Select(i => i.EntityId).ToArray());
        Assert.IsTrue(review.ExportSummary.RequiresWarningAcknowledgement);
        Assert.IsFalse(review.ExportSummary.CanExport);
        foreach (var point in edited.Points.Where(p => p.PhaseCode == "a"))
        {
            var issue = review.ReviewIssues.Single(i => i.EntityId == point.PointId && i.PointStatusToAccept.HasValue);
            review.SelectReviewIssueCommand.Execute(issue);
            Assert.IsTrue(review.AcceptReviewPointCommand.CanExecute(null));
            review.AcceptReviewPointCommand.Execute(null);
        }
        Assert.IsTrue((await saved.SaveProjectAsync(project, token)).IsSuccess);
        await Check("a", "30", "90", "reviewed-again", null);
        await VerifyExport("a", "reviewed-again", 30, 90, "1,4,a\n3,8,a\n", ExpectedBaselinePixels);
        await Check("b", "150", "210", "still-pending", "requires explicit point and phase evidence");

        // Reopen the same two-panel data with only the first panel's calibration confirmation withdrawn.
        var store = new ProjectFileStore();
        var document = (await store.LoadAsync(project, token)).Value!;
        var affected = document.Panels.Single(p => p.PanelId.Value.ToString("D") == full.PanelId);
        Assert.IsTrue(affected.Calibration!.UserConfirmed);
        project = temp.PathFor("one-unconfirmed.garproj");
        Assert.IsTrue((await store.SaveAsync(document with
        {
            Panels = document.Panels.Select(p => p.PanelId == affected.PanelId
                ? p with { Calibration = p.Calibration! with { UserConfirmed = false } } : p).ToArray(),
        }, project, token)).IsSuccess);
        await Check("a", "30", "90", "sibling-after-loss", null, controlPanel: control);
        await VerifyExport("a", "sibling-after-loss", 30, 90, "1,2,a\n", [30], control.Phases.Single().PhaseId.Value);
        await Check("a", "30", "90", "calibration-unconfirmed",
            $"{affected.DisplayName} ({affected.PanelId.Value:D}) requires a confirmed axis-aligned Y calibration");

        async Task SetAnchors(double upperY, double rightX)
        {
            reviewer.StartCalibrationCommand.Execute(null);
            await reviewer.HandleCanvasPointAsync(new(30, 100));
            await reviewer.HandleCanvasPointAsync(new(30, upperY));
            await reviewer.HandleCanvasPointAsync(new(rightX, 100));
        }

        async Task VerifyExport(string code, string name, double left, double right, string expectedRows, double[] pixels, Guid? expectedPhase = null)
        {
            Guid phaseId = expectedPhase ?? phases[code].PhaseId.Value;
            string folder = temp.PathFor(name);
            string csv = Directory.GetFiles(folder, "*.csv").Single(p => !p.EndsWith(".audit.csv", StringComparison.Ordinal));
            Assert.AreEqual("x_value,y_value,phase\n" + expectedRows, await File.ReadAllTextAsync(csv, token));
            using var audit = JsonDocument.Parse(await File.ReadAllBytesAsync(Directory.GetFiles(folder, "*.audit.json").Single(), token));
            using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(folder, "phase-scope.json"), token));
            var proof = receipt.RootElement;
            Assert.AreEqual(audit.RootElement.GetProperty("run_id").GetGuid(), proof.GetProperty("RunId").GetGuid());
            var ticks = proof.GetProperty("ConfirmedPhaseTicks");
            Assert.AreEqual(phaseId, ticks.GetProperty("PhaseId").GetGuid());
            Assert.AreEqual(left, ticks.GetProperty("LeftPixel").GetDouble());
            Assert.AreEqual(right, ticks.GetProperty("RightPixel").GetDouble());
            Assert.AreEqual(1d, ticks.GetProperty("LeftValue").GetDouble());
            Assert.AreEqual(3d, ticks.GetProperty("RightValue").GetDouble());
            var projectHash = SHA256.HashData(await File.ReadAllBytesAsync(project, token));
            Assert.AreEqual(Convert.ToHexStringLower(projectHash), proof.GetProperty("ProjectSha256").GetString());
            var rows = audit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(pixels, rows.Select(r => r.GetProperty("original_pixel_x").GetDouble()).ToArray());
            Assert.IsTrue(rows.All(r => r.GetProperty("phase_id").GetGuid() == phaseId &&
                r.GetProperty("phase_label_review").GetString() == "explicit_label" &&
                r.GetProperty("point_review_actions").GetString()!.Split('|').Contains("point_accepted") &&
                r.GetProperty("x_source").GetString() == "estimated"));
            context.WriteLine($"Phase {code} verified CSV rows {expectedRows.TrimEnd().Replace("\n", " | ", StringComparison.Ordinal)}");
        }

        async Task Check(string code, string left, string right, string name, string? expectedError, bool confirmTicks = true, PanelRecord? controlPanel = null)
        {
            Guid phase = controlPanel is null ? phases[code].PhaseId.Value : controlPanel.Phases.Single().PhaseId.Value;
            var original = SHA256.HashData(await File.ReadAllBytesAsync(project, token));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[]{ tool!, "export", project, controlPanel?.PanelId.Value.ToString("D") ?? tab.PanelId!, phase.ToString("D"),
                         left, "1", right, "3", temp.PathFor(name) })
                start.ArgumentList.Add(arg);
            if (confirmTicks) start.ArgumentList.Add("--confirm-ticks");
            start.ArgumentList.Add("--acknowledge-warnings");
            using var child = Process.Start(start)!;
            var error = child.StandardError.ReadToEndAsync(token);
            var output = child.StandardOutput.ReadToEndAsync(token);
            await child.WaitForExitAsync(token);
            string message = await error + await output;
            int expectedExit = !confirmTicks ? 2 : expectedError is null ? 0 : 1;
            Assert.AreEqual(expectedExit, child.ExitCode, message);
            if (expectedError is not null) StringAssert.Contains(message, expectedError);
            else StringAssert.Contains(message, $"from phase {code} ({phase:D})");
            Assert.AreEqual(expectedError is null, Directory.Exists(temp.PathFor(name)));
            CollectionAssert.AreEqual(original, SHA256.HashData(await File.ReadAllBytesAsync(project, token)), name);
            context.WriteLine($"{name} exit {child.ExitCode}; saved project hash matched.");
        }
    }
}
