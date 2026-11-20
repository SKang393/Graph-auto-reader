// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using PixelPoint = GraphReader.Domain.PixelPoint;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class ScanSessionCalibrationTests(TestContext context)
{
    private sealed record Reading(RustAxisGeometry Box, CalibrationOcrResponse Labels,
        CalibrationTickProposalResult Fit, PixelPoint[] Centers);
    private sealed record ExpectedRow(int X, int Y, string Phase, string Review);
    private sealed record Reference(int[] Plot, int[][] XTicks, int[][] YTicks, int[][] Anchors, ExpectedRow[] Rows);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FitFromScanPixels(bool rgbControl)
    {
        var token = CancellationToken.None;
        string spec = Path.Combine(RepositoryRoot.Find(), "tests", "GraphReader.Integration.Tests", "Validation", "Specifications");
        using var temp = new IntegrationSmokeTestEnvironment();
        string image = Path.Combine(spec, "scan-sessions.png");
        byte[] bytes = await File.ReadAllBytesAsync(image, token);
        if (rgbControl)
        {
            // The same grayscale samples encoded as a truecolor PNG.
            using var color = Cv2.ImDecode(bytes, ImreadModes.Color);
            bytes = color.ImEncode(".png");
            image = temp.PathFor("rgb-control.png");
            await File.WriteAllBytesAsync(image, bytes, token);
        }
        string? assets = Environment.GetEnvironmentVariable("GRAPHREADER_CALIBRATION_OCR_ASSET_ROOT");
        if (string.IsNullOrEmpty(assets)) Assert.Inconclusive("Set GRAPHREADER_CALIBRATION_OCR_ASSET_ROOT to the reviewed reader asset directory.");
        var reading = await ReadImageAsync(bytes, assets, token);
        context.WriteLine("Exemplar seed followed by matches = " + string.Join(", ", reading.Centers));
        var fit = reading.Fit;
        Assert.IsTrue(fit.IsComplete, string.Join(", ", fit.Reasons));
        Assert.IsNotNull(fit.PrintedXReviewDraft);
        var draft = fit.PrintedXReviewDraft;
        Assert.IsTrue(draft.UsesEstimatedPositions);

        string project = temp.PathFor("unreviewed.garproj");
        await ReviewedSyntheticExportTests.WriteUnreviewedDraftAsync(image, project, reading.Centers, token);
        using var workspace = new ManualPreviewWorkspaceService();
        var tab = (await workspace.OpenProjectAsync(project, token)).Single();
        Assert.IsNull(tab.Calibration);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => workspace.ExportAsync(tab.TabId, temp.PathFor("unconfirmed"), token));

        // Scripted review transcript. Export the accepted suggestion once to measure
        // its coordinate error, then review both endpoint columns at the visible ticks.
        void Confirm(CalibrationTickAnchors anchors)
        {
            workspace.Calibrate(tab.TabId, new(new(anchors.FirstSessionY0.X, anchors.FirstSessionY0.Y),
                new(anchors.FirstSessionYMaximum.X, anchors.FirstSessionYMaximum.Y), new(anchors.FinalSessionY0.X, anchors.FinalSessionY0.Y),
                draft.YMaximum, draft.FinalX, draft.FirstX) { UsesPrintedXCoordinates = true });
        }
        Confirm(draft.Anchors);
        workspace.AddPhaseDivider(tab.TabId, 186, "b", "Training");
        workspace.AddPhaseDivider(tab.TabId, 342, "m", "Follow-up");
        // Scripted clicks read from the displayed disks after matching. Track actual
        // placement IDs so a missing proposal never receives synthetic acceptance.
        PixelPoint[] visibleDisks = [new(102, 216), new(150, 180), new(198, 189), new(294, 126), new(366, 138), new(414, 96)];
        HashSet<string> placed = [];
        foreach (var disk in visibleDisks.Where(disk => !tab.Points.Any(p => p.PixelX == disk.X && p.PixelY == disk.Y)))
            placed.Add(workspace.AddPoint(tab.TabId, tab.SeriesCards.Single().SeriesId, disk.X, disk.Y).PointId);
        context.WriteLine($"Marker proposals beyond the seed = {reading.Centers.Length - 1}; manual point placements = {placed.Count}.");
        if (!rgbControl) Assert.IsEmpty(placed);
        var initial = await workspace.ExportAsync(tab.TabId, temp.PathFor("as-suggested"), token);
        Assert.IsTrue(initial.Succeeded, string.Join(", ", initial.Failures));
        var raw = draft.Anchors;
        var reviewed = raw with { FirstSessionY0 = raw.FirstSessionY0 with { X = 78 },
            FirstSessionYMaximum = raw.FirstSessionYMaximum with { X = 78 }, FinalSessionY0 = raw.FinalSessionY0 with { X = 438 } };
        int edits = new[] { raw.FirstSessionY0 != reviewed.FirstSessionY0, raw.FirstSessionYMaximum != reviewed.FirstSessionYMaximum,
            raw.FinalSessionY0 != reviewed.FinalSessionY0 }.Count(changed => changed);
        if (edits > 0) Confirm(reviewed);
        context.WriteLine($"Changed anchor coordinates = {edits}; changed label readings = 0.");
        foreach (var point in tab.Points.OrderBy(p => p.PixelX).SkipLast(1).Where(p => !placed.Contains(p.PointId)))
            workspace.SetPointReviewStatus(tab.TabId, point.PointId, ReviewStatus.Accepted);

        string saved = temp.PathFor("reviewed.garproj");
        Assert.IsTrue((await workspace.SaveProjectAsync(saved, token)).IsSuccess);
        using var reopened = new ManualPreviewWorkspaceService();
        var restored = (await reopened.OpenProjectAsync(saved, token)).Single();
        Assert.IsTrue(reopened.CurrentProject.Panels.Single().Calibration!.UserConfirmed);
        Assert.IsTrue(restored.Calibration!.UsesPrintedXCoordinates);
        var final = await reopened.ExportAsync(restored.TabId, temp.PathFor("reviewed"), token);
        Assert.IsTrue(final.Succeeded, string.Join(", ", final.Failures));
        Assert.AreEqual(ExportMode.PrintedSession, final.Mode);

        // Open the scorer's reference only after both exports have been written.
        var reference = JsonSerializer.Deserialize<Reference>(await File.ReadAllBytesAsync(
            Path.Combine(spec, "scan-sessions.reference.json"), token), JsonSerializerOptions.Web)!;
        ScoreCalibration(reading, reference);
        Assert.AreEqual(reference.Rows.Length, reading.Centers.Length + placed.Count);
        ScoreExports(reference.Rows, initial, final, placed);
    }

    private static async Task<Reading> ReadImageAsync(byte[] image, string assets, CancellationToken token)
    {
        var detected = await NativeAxisWorker.ProcessAsync(image, token);
        var box = detected.Geometry;
        Assert.IsNotNull(box, string.Join(", ", detected.ReviewReasons.Prepend(detected.ErrorCode)));
        using var gray = Cv2.ImDecode(image, ImreadModes.Grayscale);
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        var request = new CalibrationOcrRequest("scan", "panel", hash, hash, gray.Width, gray.Height,
            new(box.Left, box.Top, box.Right, box.Bottom));
        // Exercise the supported glyph-center fit when no Rust tick measurements are supplied.
        await using var session = new CalibrationOcrSession(assets, new(0, 0, 0, false));
        var response = await session.RecognizeAsync(new(request, image), token);
        Assert.IsNull(response.FailureCode);
        Assert.AreEqual("Cpu", response.Runtime?.Provider);
        Assert.AreEqual(CalibrationOcrSession.ModelSha256, response.Runtime?.ModelSha256);
        var fit = CalibrationTickProposal.CreateReviewDraft(box, response);
        return new(box, response, fit, FindMarkerCenters(image, gray, box, hash, token));
    }

    private static PixelPoint[] FindMarkerCenters(byte[] image, Mat gray, RustAxisGeometry box, string hash, CancellationToken token)
    {
        // Select an exemplar for this controlled image using the deepest interior
        // ink pixel. The application normally takes a user-selected exemplar.
        var bounds = new Rect(box.Left + 1, box.Top + 1, box.Right - box.Left - 1, box.Bottom - box.Top - 1);
        using var interior = new Mat(gray, bounds);
        using var binary = new Mat();
        Cv2.Threshold(interior, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var distances = binary.DistanceTransform(DistanceTypes.L2, DistanceTransformMasks.Mask5);
        distances.MinMaxLoc(out Point _, out Point peak);
        int x = bounds.X + peak.X, y = bounds.Y + peak.Y;
        // Match the UI default 17-pixel exemplar and its floor(center - size / 2) origin.
        var request = new MarkerExemplarRequest("scan", hash, hash, hash, "panel", gray.Width, gray.Height,
            new(x - 9, y - 9, 17, 17), new(box.Left, box.Top, box.Right - box.Left + 1, box.Bottom - box.Top + 1),
            "seed", "response", new(x, y));
        var response = MarkerExemplarWorker.Process(request, image, new(0, 0, 0, false), token);
        Assert.IsNull(response.FailureCode);
        return [new(x, y), .. response.Candidates.Select(c => new PixelPoint(c.Center.X, c.Center.Y))];
    }

    private void ScoreCalibration(Reading reading, Reference reference)
    {
        int[] box = [reading.Box.Left, reading.Box.Top, reading.Box.Right, reading.Box.Bottom];
        int[] edgeErrors = box.Zip(reference.Plot, (x, y) => x - y).ToArray();
        context.WriteLine($"Detected box L,T,R,B = [{string.Join(", ", box)}]; signed edge errors = [{string.Join(", ", edgeErrors)}] px.");
        Assert.IsTrue(edgeErrors.All(d => Math.Abs(d) <= 1));
        Assert.HasCount(reference.XTicks.Length + reference.YTicks.Length, reading.Labels.Proposals.Where(p => p.NumericLiteral).ToArray());
        foreach (var (axis, expected) in new[] { ("X", reference.XTicks), ("Y", reference.YTicks) })
        {
            var rows = reading.Labels.Proposals.Where(p => p.Role == axis + "Tick").ToArray();
            Assert.HasCount(expected.Length, rows);
            var pairs = reading.Fit.PairedTicks.Where(p => p.Axis == axis).OrderBy(p => p.PixelPosition).ToArray();
            Assert.HasCount(expected.Length, pairs);
            foreach (var (tick, pair) in expected.Zip(pairs))
            {
                Assert.AreEqual(tick[1].ToString(CultureInfo.InvariantCulture), rows.Single(p => p.RegionId == pair.Label.RegionId).Text);
                Assert.AreEqual((double)tick[1], pair.Value);
                Assert.IsTrue(Math.Abs(tick[0] - pair.PixelPosition) <= 1);
                Assert.IsTrue(pair.IsEstimated);
            }
            context.WriteLine($"{axis} labels and estimated pixel positions = {string.Join(", ", pairs.Select(p => $"{p.Value}@{p.PixelPosition}"))}.");
        }
        var a = reading.Fit.Anchors!;
        double[] errors = new[] { a.FirstSessionY0, a.FirstSessionYMaximum, a.FinalSessionY0 }.Zip(reference.Anchors,
            (p, xy) => Math.Sqrt(Math.Pow(p.X - xy[0], 2) + Math.Pow(p.Y - xy[1], 2))).ToArray();
        context.WriteLine($"Anchor errors (origin, upper Y, final X) = [{string.Join(", ", errors)}] px.");
        Assert.IsTrue(errors.All(e => e <= 1));
    }

    private void ScoreExports(ExpectedRow[] expected, ExportResult initial, ExportResult final, HashSet<string> placed)
    {
        string[] suggested = File.ReadAllLines(initial.MinimalArtifacts.Single().WrittenPath!).Skip(1).ToArray();
        Assert.HasCount(expected.Length, suggested);
        double maxX = 0, maxY = 0;
        foreach (var (line, r) in suggested.Zip(expected))
        {
            string[] values = line.Split(',');
            maxX = Math.Max(maxX, Math.Abs(double.Parse(values[0], CultureInfo.InvariantCulture) - r.X));
            maxY = Math.Max(maxY, Math.Abs(double.Parse(values[1], CultureInfo.InvariantCulture) - r.Y));
            Assert.AreEqual(r.Phase, values[2]);
        }
        context.WriteLine($"Before the anchor correction, max |X error| = {maxX:G8} sessions; max |Y error| = {maxY:G8}.");
        Assert.IsTrue(maxX <= 1d / 24 + 1e-10, "X values must fall within one fixture pixel of their session.");
        Assert.AreEqual(0d, maxY);
        string[] csv = File.ReadAllLines(final.MinimalArtifacts.Single().WrittenPath!);
        CollectionAssert.AreEqual(expected.Select(r => $"{r.X},{r.Y},{r.Phase}").Prepend("x_value,y_value,phase").ToArray(), csv);
        string path = final.AuditArtifacts.Single(a => a.Format == ExportAuditArtifactFormat.Json).WrittenPath!;
        using var audit = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement[] rows = audit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.HasCount(expected.Length, rows);
        foreach (var (row, r) in rows.Zip(expected))
        {
            Assert.AreEqual((double)r.X, row.GetProperty("x_value").GetDouble());
            Assert.AreEqual((double)r.Y, row.GetProperty("y_value").GetDouble());
            Assert.AreEqual(r.Phase, row.GetProperty("phase").GetString());
            Assert.AreEqual("estimated", row.GetProperty("x_source").GetString());
            var missing = new List<string>();
            if (row.GetProperty("review_status").GetString() != "accepted" ||
                !row.GetProperty("point_review_actions").GetString()!.Split('|').Contains("point_accepted")) missing.Add("missing point acceptance");
            if (row.GetProperty("phase_source").GetString() != "manual" ||
                row.GetProperty("phase_label_review").GetString() != "explicit_label") missing.Add("missing phase label");
            string disposition = missing.Count == 0 ? "strict" : string.Join(" and ", missing);
            string expectedReview = r.Review;
            if (placed.Contains(row.GetProperty("point_id").GetString()!))
            {
                Assert.AreEqual("corrected", row.GetProperty("review_status").GetString());
                CollectionAssert.Contains(row.GetProperty("point_review_actions").GetString()!.Split('|'), "point_placed");
                expectedReview = r.Phase == "a" ? "missing point acceptance and missing phase label" : "missing point acceptance";
            }
            Assert.AreEqual(expectedReview, disposition);
            context.WriteLine($"{r.X},{r.Y},{r.Phase}  {disposition}");
        }
    }
}
