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
public sealed class ReviewedPhaseExportCliTests
{
    private static readonly double[] ExpectedSourceX = [30, 90];

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
        var original = SHA256.HashData(await File.ReadAllBytesAsync(project, token));
        await Check("b", "150", "210", "point-pending", "requires explicit point and phase evidence");
        await Check("a", "150", "210", "wrong-phase-ticks", "inside the selected phase");
        await Check("a", "30", "90", "accepted", null);

        string folder = temp.PathFor("accepted");
        string csv = Directory.GetFiles(folder, "*.csv").Single(p => !p.EndsWith(".audit.csv", StringComparison.Ordinal));
        Assert.AreEqual("x_value,y_value,phase\n1,2,a\n3,4,a\n", await File.ReadAllTextAsync(csv, token));
        using var audit = JsonDocument.Parse(await File.ReadAllBytesAsync(Directory.GetFiles(folder, "*.audit.json").Single(), token));
        using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(folder, "phase-scope.json"), token));
        var proof = receipt.RootElement;
        Assert.AreEqual(audit.RootElement.GetProperty("run_id").GetGuid(), proof.GetProperty("RunId").GetGuid());
        Assert.AreEqual(phases["a"].PhaseId.Value, proof.GetProperty("ConfirmedPhaseTicks").GetProperty("PhaseId").GetGuid());
        Assert.AreEqual(Convert.ToHexStringLower(original), proof.GetProperty("ProjectSha256").GetString());
        var rows = audit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(ExpectedSourceX, rows.Select(r => r.GetProperty("original_pixel_x").GetDouble()).ToArray());
        Assert.IsTrue(rows.All(r => r.GetProperty("phase_label_review").GetString() == "explicit_label" &&
            r.GetProperty("point_review_actions").GetString()!.Split('|').Contains("point_accepted") &&
            r.GetProperty("x_source").GetString() == "estimated"));
        CollectionAssert.AreEqual(original, SHA256.HashData(await File.ReadAllBytesAsync(project, token)));
        using var reopened = new ManualPreviewWorkspaceService();
        var full = (await reopened.OpenProjectAsync(project, token)).Single();
        Assert.HasCount(4, full.Points);
        var blocked = await reopened.ExportAsync(full.TabId, temp.PathFor("whole"), token);
        Assert.AreEqual(ExportContract.ObservationOrderReviewRequired, blocked.Failures.Single().Code);

        async Task Check(string code, string left, string right, string name, string? expectedError)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[]{ tool!, "export", project, tab.PanelId!, phases[code].PhaseId.Value.ToString("D"),
                         left, "1", right, "3", temp.PathFor(name), "--confirm-ticks", "--acknowledge-warnings" })
                start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var error = child.StandardError.ReadToEndAsync(token);
            var output = child.StandardOutput.ReadToEndAsync(token);
            await child.WaitForExitAsync(token);
            string message = await error + await output;
            Assert.AreEqual(expectedError is null ? 0 : 1, child.ExitCode, message);
            if (expectedError is not null) StringAssert.Contains(message, expectedError);
            Assert.AreEqual(expectedError is null, Directory.Exists(temp.PathFor(name)));
        }
    }
}
