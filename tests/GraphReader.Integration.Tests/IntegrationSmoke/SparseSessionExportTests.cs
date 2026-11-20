// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PixelPoint = GraphReader.Domain.PixelPoint;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class SparseSessionExportTests(TestContext context)
{
    // Hand-enumerated reference. No detector, fit, or export supplies these values.
    private sealed record Observation(int Px, int Py, int Session, int Y, string Phase, string Disposition);
    private static readonly Observation[] Reference =
    [
        new(68, 140, 2, 12, "a", "missing phase label"),
        new(100, 108, 4, 28, "a", "missing phase label"),
        new(148, 124, 7, 20, "b", "strict"),
        new(180, 80, 9, 42, "b", "strict"),
        new(244, 96, 13, 34, "m", "strict"),
        new(276, 60, 15, 52, "m", "missing point acceptance"),
    ];

    [TestMethod]
    public async Task ReviewedPrintedFitPreservesSparseSessionsAndPhaseEvidence()
    {
        var token = CancellationToken.None;
        using var temp = new IntegrationSmokeTestEnvironment();
        string image = Path.Combine(RepositoryRoot.Find(), "tests", "GraphReader.Integration.Tests",
            "Validation", "Specifications", "sparse-sessions.png");
        byte[] encoded = await File.ReadAllBytesAsync(image, token);
        string hash = Convert.ToHexStringLower(SHA256.HashData(encoded));
        PixelPoint[] draftCenters = DetectAndScoreMarkers(encoded, hash, token);

        string draft = temp.PathFor("draft.garproj");
        await ReviewedSyntheticExportTests.WriteUnreviewedDraftAsync(image, draft, draftCenters, token);
        using var workspace = new ManualPreviewWorkspaceService();
        var tab = (await workspace.OpenProjectAsync(draft, token)).Single();
        var fit = FitSyntheticReadings(hash);
        Assert.IsTrue(fit.IsComplete, string.Join(", ", fit.Reasons));
        Assert.HasCount(7, fit.PairedTicks);
        Assert.IsFalse(fit.UsesEstimatedPositions);
        workspace.Calibrate(tab.TabId, Calibration(fit.Anchors!, fit.FirstSession!.Value, fit.FinalSession!.Value, fit.YMaximum!.Value, false));
        workspace.AddPhaseDivider(tab.TabId, 128, "b", "Training");
        workspace.AddPhaseDivider(tab.TabId, 228, "m", "Follow-up");
        string ordinaryDirectory = temp.PathFor("ordinary");
        var ordinary = await workspace.ExportAsync(tab.TabId, ordinaryDirectory, token);
        Assert.AreEqual(ExportContract.ObservationOrderReviewRequired, ordinary.Failures.Single().Code);
        Assert.IsFalse(Directory.Exists(ordinaryDirectory));

        var printed = fit.PrintedXReviewDraft;
        Assert.IsNotNull(printed, "The operator needs a printed-X choice on a valid session-1 fit.");
        workspace.Calibrate(tab.TabId, Calibration(printed.Anchors, printed.FirstX, printed.FinalX, printed.YMaximum, true));
        var byX = tab.Points.OrderBy(p => p.PixelX).ToArray();
        foreach (var point in byX.SkipLast(1))
            workspace.SetPointReviewStatus(tab.TabId, point.PointId, ReviewStatus.Accepted);

        string project = temp.PathFor("reviewed.garproj");
        Assert.IsTrue((await workspace.SaveProjectAsync(project, token)).IsSuccess);
        using var reopened = new ManualPreviewWorkspaceService();
        var restored = (await reopened.OpenProjectAsync(project, token)).Single();
        var panel = reopened.CurrentProject.Panels.Single();
        Assert.IsTrue(panel.Calibration!.UserConfirmed);
        Assert.IsTrue(restored.Calibration!.UsesPrintedXCoordinates);
        CollectionAssert.AreEquivalent(new[] { (52d, 164d, 1d, 0d), (52d, 36d, 1d, 64d), (292d, 164d, 16d, 0d) },
            panel.Calibration.Anchors.Select(a => (a.Screen.X, a.Screen.Y, a.Graph.X, a.Graph.Y)).ToArray());
        CollectionAssert.AreEqual(new[] { ("a", 0d, 128d), ("b", 128d, 228d), ("m", 228d, 344d) },
            panel.Phases.Select(p => (p.Code, p.ScreenXMin, p.ScreenXMax)).ToArray());
        var result = await reopened.ExportAsync(restored.TabId, temp.PathFor("printed"), token);
        Assert.IsTrue(result.Succeeded, string.Join(", ", result.Failures));
        Assert.AreEqual(ExportMode.PrintedSession, result.Mode);
        string[] csv = await File.ReadAllLinesAsync(result.MinimalArtifacts.Single().WrittenPath!, token);
        CollectionAssert.AreEqual(Reference.Select(r => $"{r.Session},{r.Y},{r.Phase}").Prepend("x_value,y_value,phase").ToArray(), csv);

        await CheckWrittenReviewEvidenceAsync(result, token);
    }

    private PixelPoint[] DetectAndScoreMarkers(byte[] encoded, string hash, CancellationToken token)
    {
        var request = new MarkerExemplarRequest("sparse", hash, hash, hash, "panel", 344, 218,
            new(61, 133, 13, 13), new(52, 36, 241, 129), "seed", "response", new(68, 140));
        // The in-process worker decodes and matches pixels without a process-level CPU budget.
        var matches = MarkerExemplarWorker.Process(request, encoded, new(0, 0, 0, false), token);
        Assert.IsNull(matches.FailureCode);
        var centers = matches.Candidates.Select(c => new PixelPoint(c.Center.X, c.Center.Y))
            .OrderBy(p => p.X).ToArray();
        // Distinct reference centers are more than two tolerances apart. Require one
        // match per disk and account separately for extras (including duplicate hits).
        var distances = Reference.Skip(1).Select(row => centers.Select(p =>
            Math.Sqrt(Math.Pow(p.X - row.Px, 2) + Math.Pow(p.Y - row.Py, 2))).DefaultIfEmpty(double.PositiveInfinity).Min()).ToArray();
        int hits = distances.Count(d => d <= 1);
        int targets = distances.Length;
        context.WriteLine($"Seed excluded. Matched {hits}/{targets}, missed {targets - hits}, extra {centers.Length - hits}, maximum center error {distances.Max():G4} px.");
        Assert.AreEqual(targets, hits);
        Assert.HasCount(targets, centers);
        return [new(request.SelectedPoint.X, request.SelectedPoint.Y), .. centers];
    }

    private async Task CheckWrittenReviewEvidenceAsync(ExportResult result, CancellationToken token)
    {
        string path = result.AuditArtifacts.Single(a => a.Format == ExportAuditArtifactFormat.Json).WrittenPath!;
        using var audit = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
        JsonElement[] rows = audit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.HasCount(Reference.Length, rows);
        foreach (var (row, expected) in rows.Zip(Reference))
        {
            Assert.AreEqual((double)expected.Px, row.GetProperty("original_pixel_x").GetDouble());
            Assert.AreEqual((double)expected.Py, row.GetProperty("original_pixel_y").GetDouble());
            Assert.AreEqual((double)expected.Session, row.GetProperty("x_value").GetDouble());
            Assert.AreEqual((double)expected.Y, row.GetProperty("y_value").GetDouble());
            Assert.AreEqual(expected.Phase, row.GetProperty("phase").GetString());
            Assert.AreEqual("estimated", row.GetProperty("x_source").GetString());
            bool accepted = row.GetProperty("review_status").GetString() == "accepted" &&
                row.GetProperty("point_review_actions").GetString()!.Split('|').Contains("point_accepted");
            bool labeled = row.GetProperty("phase_source").GetString() == "manual" &&
                row.GetProperty("phase_label_review").GetString() == "explicit_label";
            var missing = new List<string>();
            if (!accepted) missing.Add("missing point acceptance");
            if (!labeled) missing.Add("missing phase label");
            string disposition = missing.Count == 0 ? "strict" : string.Join(" and ", missing);
            Assert.AreEqual(expected.Disposition, disposition);
            context.WriteLine($"{expected.Session},{expected.Y},{expected.Phase}  {disposition}");
        }
    }

    private static ManualCalibrationRequest Calibration(CalibrationTickAnchors a, double first, double last, double ymax, bool printed) =>
        new(new(a.FirstSessionY0.X, a.FirstSessionY0.Y), new(a.FirstSessionYMaximum.X, a.FirstSessionYMaximum.Y),
            new(a.FinalSessionY0.X, a.FinalSessionY0.Y), ymax, last, first) { UsesPrintedXCoordinates = printed };

    // Supplied tick positions and label polygons isolate fitting from OCR and geometry inference.
    private static CalibrationTickProposalResult FitSyntheticReadings(string hash)
    {
        var geometry = new RustAxisGeometry(52, 164, 292, 36, .95)
            { XTickPositions = [52, 100, 180, 292], YTickPositions = [36, 100, 164] };
        CalibrationOcrProposal[] labels =
        [
            Label("X", 52, 177, 1), Label("X", 100, 177, 4), Label("X", 180, 177, 9), Label("X", 292, 177, 16),
            Label("Y", 39, 36, 64), Label("Y", 39, 100, 32), Label("Y", 39, 164, 0),
        ];
        var response = new CalibrationOcrResponse(new("sparse", "panel", hash, hash, 344, 218, new(52, 36, 292, 164)),
            labels, new(false, 4, 4, 0, []), new(false, 3, 3, 0, []), null, null, null);
        return CalibrationTickProposal.CreateReviewDraft(geometry, response);
    }

    private static CalibrationOcrProposal Label(string axis, int x, int y, int value)
    {
        string literal = value.ToString(CultureInfo.InvariantCulture);
        return new($"{axis}-{literal}", [new(x - 6, y - 5), new(x + 6, y - 5), new(x + 6, y + 5), new(x - 6, y + 5)],
            literal, .95, value, literal, true, axis + "Tick", axis, null);
    }
}
