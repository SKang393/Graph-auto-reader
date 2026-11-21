// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
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

        async Task VerifyExport(string code, string name, double left, double right, string expectedRows, double[] pixels)
        {
            string folder = temp.PathFor(name);
            string csv = Directory.GetFiles(folder, "*.csv").Single(p => !p.EndsWith(".audit.csv", StringComparison.Ordinal));
            Assert.AreEqual("x_value,y_value,phase\n" + expectedRows, await File.ReadAllTextAsync(csv, token));
            using var audit = JsonDocument.Parse(await File.ReadAllBytesAsync(Directory.GetFiles(folder, "*.audit.json").Single(), token));
            using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(folder, "phase-scope.json"), token));
            var proof = receipt.RootElement;
            Assert.AreEqual(audit.RootElement.GetProperty("run_id").GetGuid(), proof.GetProperty("RunId").GetGuid());
            var ticks = proof.GetProperty("ConfirmedPhaseTicks");
            Assert.AreEqual(phases[code].PhaseId.Value, ticks.GetProperty("PhaseId").GetGuid());
            Assert.AreEqual(left, ticks.GetProperty("LeftPixel").GetDouble());
            Assert.AreEqual(right, ticks.GetProperty("RightPixel").GetDouble());
            Assert.AreEqual(1d, ticks.GetProperty("LeftValue").GetDouble());
            Assert.AreEqual(3d, ticks.GetProperty("RightValue").GetDouble());
            var projectHash = SHA256.HashData(await File.ReadAllBytesAsync(project, token));
            Assert.AreEqual(Convert.ToHexStringLower(projectHash), proof.GetProperty("ProjectSha256").GetString());
            var rows = audit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(pixels, rows.Select(r => r.GetProperty("original_pixel_x").GetDouble()).ToArray());
            Assert.IsTrue(rows.All(r => r.GetProperty("phase_id").GetGuid() == phases[code].PhaseId.Value &&
                r.GetProperty("phase_label_review").GetString() == "explicit_label" &&
                r.GetProperty("point_review_actions").GetString()!.Split('|').Contains("point_accepted") &&
                r.GetProperty("x_source").GetString() == "estimated"));
            context.WriteLine($"Phase {code} verified CSV rows {expectedRows.TrimEnd().Replace("\n", " | ", StringComparison.Ordinal)}");
        }

        async Task Check(string code, string left, string right, string name, string? expectedError, bool confirmTicks = true)
        {
            var original = SHA256.HashData(await File.ReadAllBytesAsync(project, token));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[]{ tool!, "export", project, tab.PanelId!, phases[code].PhaseId.Value.ToString("D"),
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
            else StringAssert.Contains(message, $"from phase {code} ({phases[code].PhaseId.Value:D})");
            Assert.AreEqual(expectedError is null, Directory.Exists(temp.PathFor(name)));
            CollectionAssert.AreEqual(original, SHA256.HashData(await File.ReadAllBytesAsync(project, token)), name);
            context.WriteLine($"{name} exit {child.ExitCode}; saved project hash matched.");
        }
    }
}
