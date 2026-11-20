// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ReviewedSyntheticExportTests(TestContext context)
{
    private static readonly string[] ExpectedCoverage =
    [
        "11 omitted, missing phase label",
        "13 strict",
        "15 omitted, missing point acceptance",
        "16 omitted, missing point acceptance",
    ];

    [TestMethod]
    public async Task CorrectedDraftRoundTripsToExpectedCsvWithScopedReviewEvidence()
    {
        using var temp = new IntegrationSmokeTestEnvironment();
        string spec = Path.Combine(RepositoryRoot.Find(), "tests", "GraphReader.Integration.Tests", "Validation", "Specifications");
        string image = temp.PathFor("reviewed-export.png");
        File.Copy(Path.Combine(spec, "reviewed-export.png"), image);
        string draft = temp.PathFor("draft.garproj");
        var token = CancellationToken.None;
        await WriteUnreviewedDraftAsync(image, draft, [new(72, 120), new(136, 88), new(168, 72), new(232, 104)], token);

        using var review = new ManualPreviewWorkspaceService();
        var tab = (await review.OpenProjectAsync(draft, token)).Single();
        string id = tab.TabId;
        string series = tab.SeriesCards.Single().SeriesId;
        string blocked = temp.PathFor("uncalibrated");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => review.ExportAsync(id, blocked, token));
        Assert.IsFalse(Directory.Exists(blocked));
        review.Calibrate(id, new(new(40, 152), new(40, 24), new(296, 152), YMaximum: 64, XMaximum: 18, FirstVisibleSession: 10));
        review.AddPhaseDivider(id, 120, "b", "Training");
        var followUp = review.AddPhaseDivider(id, 240, "m", "Follow-up");

        // Same row count as the final export. Session 14 is spurious, 15 is absent,
        // and the marker at 16 is on the wrong side of the draft divider.
        var before = await review.ExportAsync(id, temp.PathFor("before"), token);
        Assert.IsTrue(before.Succeeded, string.Join(" | ", before.Failures));
        Assert.AreEqual("x_value,y_value,phase\n11,16,a\n13,32,b\n14,40,b\n16,24,b\n",
            await File.ReadAllTextAsync(before.MinimalArtifacts.Single().WrittenPath!, token));

        var extra = tab.Points.Single(p => p.PixelX == 168);
        review.DeletePoint(id, extra.PointId);
        var placed = review.AddPoint(id, series, 200, 56);
        review.MovePhaseDivider(id, followUp.DividerId, 232);
        var baseline = tab.Points.Single(p => p.PixelX == 72);
        var training = tab.Points.Single(p => p.PixelX == 136);
        var last = tab.Points.Single(p => p.PixelX == 232);
        review.SetPointReviewStatus(id, baseline.PointId, ReviewStatus.Accepted);
        review.SetPointReviewStatus(id, training.PointId, ReviewStatus.Accepted);

        string reviewed = temp.PathFor("reviewed.garproj");
        var saved = await review.SaveProjectAsync(reviewed, token);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors));
        using var reopened = new ManualPreviewWorkspaceService();
        var restored = (await reopened.OpenProjectAsync(reviewed, token)).Single();
        var panel = reopened.CurrentProject.Panels.Single();
        var calibration = panel.Calibration!;
        Assert.IsTrue(calibration.UserConfirmed);
        Assert.AreEqual(CalibrationStatus.Valid, calibration.Status);
        Assert.HasCount(3, calibration.Anchors);
        AssertAnchor(CalibrationAnchorKind.FirstVisibleSessionY0, 40, 152, 10, 0);
        AssertAnchor(CalibrationAnchorKind.FirstVisibleSessionYmax, 40, 24, 10, 64);
        AssertAnchor(CalibrationAnchorKind.SessionmaxY0, 296, 152, 18, 0);
        CollectionAssert.AreEqual(new[] { ("a", "Baseline", 0d, 120d), ("b", "Training", 120d, 232d), ("m", "Follow-up", 232d, 320d) },
            panel.Phases.Select(p => (p.Code, p.LabelText, p.ScreenXMin, p.ScreenXMax)).ToArray());
        Assert.IsFalse(panel.Points.Any(p => p.PointId.Value == Guid.Parse(extra.PointId)));

        var result = await reopened.ExportAsync(restored.TabId, temp.PathFor("after"), token);
        Assert.IsTrue(result.Succeeded, string.Join(" | ", result.Failures));
        CollectionAssert.AreEqual(await File.ReadAllLinesAsync(Path.Combine(spec, "reviewed-export.expected.csv"), token),
            await File.ReadAllLinesAsync(result.MinimalArtifacts.Single().WrittenPath!, token));
        string audit = result.AuditArtifacts.Single(a => a.Format == ExportAuditArtifactFormat.Json).WrittenPath!;
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(audit, token));
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.HasCount(4, rows);
        // A strict row here requires a separate point acceptance and an explicitly
        // labeled phase. Keep every row in the scientific CSV and check each omission.
        CheckRow(0, baseline.PointId, 72, 120, 11, 16, "a", "accepted", "point_accepted", "profile_prior", "unconfirmed");
        CheckRow(1, training.PointId, 136, 88, 13, 32, "b", "accepted", "point_accepted", "manual", "explicit_label");
        CheckRow(2, placed.PointId, 200, 56, 15, 48, "b", "corrected", "point_placed", "manual", "explicit_label");
        CheckRow(3, last.PointId, 232, 104, 16, 24, "m", "unreviewed", "none_recorded", "manual", "explicit_label");

        string[] coverage = rows.Select(row =>
        {
            var missing = new List<string>();
            if (row.GetProperty("review_status").GetString() != "accepted" ||
                !row.GetProperty("point_review_actions").GetString()!.Split('|').Contains("point_accepted", StringComparer.Ordinal))
                missing.Add("point acceptance");
            if (row.GetProperty("phase_source").GetString() != "manual" ||
                row.GetProperty("phase_label_review").GetString() != "explicit_label")
                missing.Add("phase label");
            string disposition = missing.Count == 0 ? "strict" : "omitted, missing " + string.Join(" and ", missing);
            return $"{row.GetProperty("x_value").GetRawText()} {disposition}";
        }).ToArray();
        CollectionAssert.AreEqual(ExpectedCoverage, coverage);
        context.WriteLine(string.Join(Environment.NewLine, coverage));

        void AssertAnchor(CalibrationAnchorKind kind, double px, double py, double x, double y)
        {
            var anchor = calibration.Anchors.Single(a => a.Kind == kind);
            Assert.AreEqual(new PixelPoint(px, py), anchor.Screen);
            Assert.AreEqual(new GraphPoint(x, y), anchor.Graph);
        }

        void CheckRow(int index, string pointId, double px, double py, double x, double y, string phase,
            string status, string actions, string source, string labelReview)
        {
            var row = rows[index];
            Assert.AreEqual(pointId, row.GetProperty("point_id").GetString());
            Assert.AreEqual(series, row.GetProperty("source_series_id").GetString());
            Assert.AreEqual(px, row.GetProperty("original_pixel_x").GetDouble());
            Assert.AreEqual(py, row.GetProperty("original_pixel_y").GetDouble());
            Assert.AreEqual(x, row.GetProperty("x_value").GetDouble());
            Assert.AreEqual(y, row.GetProperty("y_value").GetDouble());
            Assert.AreEqual(phase, row.GetProperty("phase").GetString());
            Assert.AreEqual("estimated", row.GetProperty("x_source").GetString());
            Assert.IsTrue(row.GetProperty("session_origin_override_applied").GetBoolean());
            Assert.AreEqual(status, row.GetProperty("review_status").GetString());
            Assert.AreEqual(actions, row.GetProperty("point_review_actions").GetString());
            Assert.AreEqual(source, row.GetProperty("phase_source").GetString());
            Assert.AreEqual(labelReview, row.GetProperty("phase_label_review").GetString());
        }
    }

    internal static async Task WriteUnreviewedDraftAsync(string image, string draft, IReadOnlyList<PixelPoint> centers, CancellationToken token)
    {
        // Supply only an imperfect overlay in original pixels. The review path must
        // establish every numeric value, phase assignment and review action.
        using var workspace = new ManualPreviewWorkspaceService();
        var tab = (await workspace.ImportImagesAsync([image], token)).Single();
        workspace.AddSeries(tab.TabId, new("Response", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        var project = workspace.CurrentProject;
        var panel = project.Panels.Single();
        var series = panel.Series.Single();
        var points = centers.Select((pixel, i) => new PointRecord(
            PointId.New(), null, series.SeriesId, panel.Phases.Single().PhaseId, pixel, null, null, i + 1,
            null, null, PointXSource.Unknown, 0, 0, 0, "synthetic_draft", null, ReviewStatus.Unreviewed, [])).ToArray();
        var saved = await new ProjectFileStore().SaveNewAsync(project with
        {
            Panels = [panel with { Points = points, Series = [series with { PointIds = points.Select(p => p.PointId).ToArray() }] }],
        }, draft, token);
        Assert.IsTrue(saved.IsSuccess, string.Join(" | ", saved.Errors));
    }
}
