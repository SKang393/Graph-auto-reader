// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Domain;
using GraphReader.Export;

namespace GraphReader.CalibrationOcrProbe;

internal static class MarkerWorkflowCheck
{
    private static readonly double[] ExpectedXValues = [1, 2, 3];
    private static readonly double[] ExpectedExportX = [1, 2, 3];

    internal static async Task<object> RunAsync(Program.ProbeRequest source, string appExe, string assetRoot,
        string rustExe, string outputRoot, bool preparedCrop, CancellationToken cancellationToken)
    {
        if (source.Scope != "owned-synthetic-review-only" || !Path.IsPathFullyQualified(source.ImagePath) ||
            !Path.IsPathFullyQualified(appExe) || !File.Exists(appExe) || !Path.IsPathFullyQualified(rustExe) || !File.Exists(rustExe) ||
            !Path.IsPathFullyQualified(assetRoot) || !Directory.Exists(assetRoot) || !Path.IsPathFullyQualified(outputRoot) ||
            !Directory.Exists(outputRoot) || Directory.EnumerateFileSystemEntries(outputRoot).Any() ||
            source.SourceCropXywh is not [0, 0, 640, 480])
            throw new InvalidDataException("Expected the 640x480 owned calibration control, absolute workers, and an empty output directory.");
        byte[] sourceBytes = Program.ReadBounded(source.ImagePath, 64 * 1024 * 1024);
        Program.RequireHash(sourceBytes, source.ImageSha256);
        byte[] pixels;
        using (var stream = new MemoryStream(sourceBytes, writable: false))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != 640 || decoder.Frames[0].PixelHeight != 480)
                throw new InvalidDataException("Owned control dimensions differ.");
            var gray = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Gray8, null, 0);
            pixels = new byte[640 * 480]; gray.CopyPixels(pixels, 640, 0);
        }
        for (int x = 130; x <= 550; x++) pixels[230 * 640 + x] = 0;
        foreach (int center in new[] { 140, 340, 540 })
            for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
                if (dx * dx + dy * dy <= 9) pixels[(230 + dy) * 640 + center + dx] = 0;
        int imageWidth = preparedCrop ? 768 : 640, imageHeight = preparedCrop ? 608 : 480;
        byte[] sourcePixels = pixels;
        if (preparedCrop)
        {
            sourcePixels = new byte[imageWidth * imageHeight];
            Array.Fill(sourcePixels, (byte)255);
            for (int row = 0; row < 480; row++)
                Array.Copy(pixels, row * 640, sourcePixels, (row + 64) * imageWidth + 64, 640);
        }
        string imagePath = Path.Combine(outputRoot, "owned-marker-workflow.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(imageWidth, imageHeight, 96, 96, PixelFormats.Gray8, null, sourcePixels, imageWidth)));
        using (var file = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(file);
        string generatedSourceHash = Program.Hash(File.ReadAllBytes(imagePath));
        var elapsed = Stopwatch.StartNew();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new RustAxisProposalService(rustExe),
            calibrationOcrSuggestionService: new CalibrationOcrWorkerClient(appExe, assetRoot),
            markerExemplarSuggestionService: new MarkerExemplarWorkerClient(appExe));
        var parent = (await workspace.ImportImagesAsync([imagePath], cancellationToken)).Single();
        string? parentPanelHash = parent.SourceSha256;
        var tab = parent;
        double? cropMilliseconds = null;
        if (preparedCrop)
        {
            PanelCropContext context = workspace.GetPanelCropContext(parent.TabId) ?? throw new InvalidDataException("Crop context unavailable.");
            var crop = Stopwatch.StartNew();
            tab = await workspace.CreatePanelCropAsync(context, new(64, 64, 640, 480), cancellationToken);
            crop.Stop();
            cropMilliseconds = crop.Elapsed.TotalMilliseconds;
            if (parent.Calibration is not null || parent.Points.Count != 0 || parent.SeriesCards.Count != 0 ||
                !workspace.IsPanelCropContextCurrent(context) || parent.SourceSha256 != parentPanelHash ||
                workspace.CurrentProject.Sources.Single().Sha256 != generatedSourceHash)
                throw new InvalidDataException("Preparing a crop changed the original parent.");
        }
        WorkspaceAxisSuggestion? axis = await workspace.SuggestAxesAsync(tab.TabId, cancellationToken);
        if (axis is null) return new { completed = false, reason = "AXIS_PROPOSAL_UNAVAILABLE", review_only = true };
        WorkspaceCalibrationLabelSuggestion? labels = await workspace.ReadCalibrationLabelsAsync(axis, cancellationToken);
        if (labels is null) return new { completed = false, reason = "OCR_UNAVAILABLE_OR_STALE", review_only = true };
        CalibrationTickProposalResult proposal = CalibrationTickProposal.Create(axis.Geometry, labels.Response);
        if (!proposal.IsComplete) return new { completed = false, reason = "CALIBRATION_REQUIRES_REVIEW", proposal.Reasons, review_only = true };
        var anchors = proposal.Anchors!;
        // The controlled fixture has observed labels 1, 2, 3 and 0, 5, 10; reject any differing OCR proposal.
        if (proposal.FirstSession != 1 || proposal.FinalSession != 3 || proposal.YMaximum != 10 ||
            Math.Abs(anchors.FirstSessionY0.X - 140) > 1 || Math.Abs(anchors.FirstSessionY0.Y - 380) > 1 ||
            Math.Abs(anchors.FirstSessionYMaximum.Y - 80) > 1 || Math.Abs(anchors.FinalSessionY0.X - 540) > 1)
            return new { completed = false, reason = "CALIBRATION_CONTROL_MISMATCH", proposal, review_only = true };
        // Explicitly simulated review accepts the observed proposal; nothing is applied automatically by the worker.
        workspace.Calibrate(tab.TabId, new(new(anchors.FirstSessionY0.X, anchors.FirstSessionY0.Y),
            new(anchors.FirstSessionYMaximum.X, anchors.FirstSessionYMaximum.Y),
            new(anchors.FinalSessionY0.X, anchors.FinalSessionY0.Y), proposal.YMaximum!.Value,
            proposal.FinalSession!.Value, proposal.FirstSession!.Value));
        var series = workspace.AddSeries(tab.TabId, new("Owned control", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
        // The first and last markers are on the confirmed plot's horizontal boundaries.
        var seed = workspace.AddPoint(tab.TabId, series.SeriesId, 140, 230);
        WorkspaceMarkerExemplarSuggestion? matches = await workspace.FindMarkerExamplesAsync(tab.TabId, seed.PointId, series.SeriesId, 13, cancellationToken);
        if (matches is null || matches.Response.FailureCode is not null)
            return new { completed = false, reason = matches?.Response.FailureCode ?? "MARKER_UNAVAILABLE_OR_STALE", review_only = true };
        if (tab.Points.Count != 1) throw new InvalidDataException("Worker unexpectedly changed points.");
        foreach (int expected in new[] { 340, 540 })
        {
            var candidate = matches.Response.Candidates.SingleOrDefault(item => Math.Abs(item.Center.X - expected) <= 1 && Math.Abs(item.Center.Y - 230) <= 1);
            if (candidate is null) return new { completed = false, reason = "MARKER_CONTROL_NOT_FOUND", matches.Response.Candidates, review_only = true };
            workspace.AddPoint(tab.TabId, series.SeriesId, candidate.Center.X, candidate.Center.Y);
        }
        if (tab.Points.Count != 3 || tab.Points.OrderBy(point => point.GraphX).Zip(ExpectedXValues)
            .Any(pair => Math.Abs(pair.First.GraphX - pair.Second) > .02 || Math.Abs(pair.First.GraphY - 5) > .05))
            throw new InvalidDataException("Calibrated point coordinates differ from the owned control.");
        var result = await workspace.ExportAsync(tab.TabId, Path.Combine(outputRoot, "export"), cancellationToken);
        if (!result.Succeeded || result.MinimalArtifacts.Count != 1) throw new InvalidDataException("CSV export failed.");
        var csv = result.MinimalArtifacts[0];
        if (result.Mode != ExportMode.ObservationOrder || result.AuditArtifacts.Count == 0 ||
            result.AuditArtifacts.Any(artifact => artifact.Rows.Count != 3 ||
                artifact.Rows.Any(row => row.XSource != ExportXValueSource.ObservationOrder || row.ExportMode != ExportMode.ObservationOrder)))
            throw new InvalidDataException("Export audit does not record observation-order X values.");
        if (csv.Rows.Count != 3 || csv.Rows.OrderBy(row => row.XValue).Zip(ExpectedExportX)
            .Any(pair => Math.Abs(pair.First.XValue - pair.Second) > .02 || Math.Abs(pair.First.YValue - 5) > .05))
            throw new InvalidDataException("CSV coordinates differ from the owned control.");
        string project = Path.Combine(outputRoot, "owned-marker-workflow.garproj");
        if (!(await workspace.SaveProjectAsync(project, cancellationToken)).IsSuccess) throw new InvalidDataException("Project save failed.");
        using var reopened = new ManualPreviewWorkspaceService();
        var reopenedTabs = await reopened.OpenProjectAsync(project, cancellationToken);
        if (reopenedTabs.Count != (preparedCrop ? 2 : 1)) throw new InvalidDataException("Unexpected reopened panel count.");
        var restored = reopenedTabs.Single(item => item.TabId == tab.TabId);
        if (restored.Points.Count != 3 || restored.Calibration is null || restored.Calibration != tab.Calibration ||
            restored.SeriesCards.Count != 1 || restored.Points.Any(point => point.SeriesId != restored.SeriesCards[0].SeriesId) ||
            restored.Points.OrderBy(point => point.GraphX).Zip(ExpectedXValues)
                .Any(pair => Math.Abs(pair.First.GraphX - pair.Second) > .02 || Math.Abs(pair.First.GraphY - 5) > .05))
            throw new InvalidDataException("Reopened project differs from the accepted control.");
        if (preparedCrop)
        {
            var restoredParent = reopenedTabs.Single(item => item.TabId == parent.TabId);
            var panel = reopened.CurrentProject.Panels.Single(item => item.PanelId.Value.ToString("D") == tab.PanelId);
            if (parent.Calibration is not null || parent.Points.Count != 0 || parent.SeriesCards.Count != 0 ||
                restoredParent.Calibration is not null || restoredParent.Points.Count != 0 || restoredParent.SeriesCards.Count != 0 ||
                Program.Hash(File.ReadAllBytes(imagePath)) != generatedSourceHash ||
                restoredParent.SourceSha256 != parentPanelHash || reopened.CurrentProject.Sources.Single().Sha256 != generatedSourceHash ||
                panel.Points.Count != restored.Points.Count)
                throw new InvalidDataException("Original parent or source changed during the cropped workflow.");
            foreach (var point in restored.Points)
            {
                var retained = panel.Points.Single(item => item.PointId.Value.ToString("D") == point.PointId);
                if (Math.Abs(retained.OriginalPixel.X - point.PixelX - 64) > 1e-9 ||
                    Math.Abs(retained.OriginalPixel.Y - point.PixelY - 64) > 1e-9)
                    throw new InvalidDataException("Saved point did not preserve the single source offset.");
            }
            var savedAnchors = panel.Calibration?.Anchors ?? throw new InvalidDataException("Saved calibration missing.");
            if (savedAnchors.Count != 3) throw new InvalidDataException("Saved calibration anchor count differs.");
            foreach (var anchor in savedAnchors)
            {
                double localX = anchor.Graph.X == 1 ? tab.Calibration!.Session1Y0.X : tab.Calibration!.SessionMaximumY0.X;
                double localY = anchor.Graph.Y == 0 ? tab.Calibration!.Session1Y0.Y : tab.Calibration!.Session1YMaximum.Y;
                if (Math.Abs(anchor.Screen.X - localX - 64) > 1e-9 || Math.Abs(anchor.Screen.Y - localY - 64) > 1e-9)
                    throw new InvalidDataException("Saved calibration did not preserve the single source offset.");
            }
        }
        return new { completed = true, review_only = true, production_approved = false,
            source_sha256 = source.ImageSha256, generated_source_sha256 = generatedSourceHash,
            scope = preparedCrop ? "prepared-crop" : "full-panel", crop_milliseconds = cropMilliseconds,
            panel_sha256 = tab.SourceSha256, proposal,
            marker_proposals = matches.Response.Candidates.Count, explicitly_accepted = 2,
            remaining_unapplied = matches.Response.Candidates.Count - 2, csv_path = csv.WrittenPath,
            ocr_timings = labels.Response.Timings, marker_timings = matches.Response.Timings,
            marker_runtime = matches.Response.Runtime,
            csv_sha256 = csv.Sha256, csv_rows = csv.Rows, export_mode = "observation_order", x_source = "observation_order",
            project_path = project, reopened_point_count = restored.Points.Count,
            elapsed_milliseconds = elapsed.Elapsed.TotalMilliseconds,
            note = "Owned control with simulated explicit review; no production approval or accuracy claim." };
    }
}
