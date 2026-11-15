// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal static IEnumerable<string> OwnedAffineRuntimeNames => IntervalClusterRuntimeLibraries.Keys;
}

// Four fixed project-owned cases, rendered before any geometry or OCR call.
internal static class OwnedAffineCalibrationCheck
{
    private sealed record Fixture(string Name, double Angle, bool PrintedX, string Path, string Sha256,
        long Bytes, RustAffineAxisGeometry SuppliedGeometry, CalibrationTickAnchors ExpectedAnchors);
    private sealed record Glyph(int Width, int Height, byte[] Pixels);
    private static readonly int[] XPositions = [140, 340, 540];
    private static readonly int[] YPositions = [80, 230, 380];
    private static readonly int[] SessionXValues = [1, 2, 3];
    private static readonly int[] PrintedXValues = [0, 10, 20];
    private static readonly int[] YValues = [10, 5, 0];

    internal static async Task<object> RunAsync(string outputRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(outputRoot) ||
            !Path.IsPathFullyQualified(app) || !File.Exists(app) || !Path.IsPathFullyQualified(assets) ||
            !Path.IsPathFullyQualified(rust) || !File.Exists(rust)) throw new InvalidDataException();
        string appDirectory = Path.GetDirectoryName(app)!;
        string probeDirectory = Path.GetDirectoryName(typeof(OwnedAffineCalibrationCheck).Assembly.Location)!;
        string[] runtimeNames = RealDevCalibrationCheck.OwnedAffineRuntimeNames.ToArray();
        if (runtimeNames.Length != 20) throw new InvalidDataException();
        foreach (string name in runtimeNames)
        {
            string source = Path.Combine(appDirectory, name), probe = Path.Combine(probeDirectory, name);
            if (!File.Exists(source) || !File.Exists(probe) || Program.Hash(File.ReadAllBytes(source)) != Program.Hash(File.ReadAllBytes(probe)))
                throw new InvalidDataException();
        }
        foreach (var assembly in new[] { typeof(CalibrationTickProposal).Assembly, typeof(RustAxisGeometry).Assembly,
            typeof(GraphReader.Ocr.OcrImage).Assembly, typeof(GraphReader.Imaging.CanvasCoordinateMapper).Assembly })
        {
            string expected = Path.Combine(appDirectory, assembly.GetName().Name + ".dll");
            if (!File.Exists(expected) || Program.Hash(File.ReadAllBytes(expected)) != Program.Hash(File.ReadAllBytes(assembly.Location))) throw new InvalidDataException();
        }
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "en_PP-OCRv5_mobile_rec.onnx"), 64 * 1024 * 1024), Program.ModelHash);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "recognizer.json"), 131_072), Program.ManifestHash);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"), 131_072), "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Notice.txt"), 131_072), "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729");
        string directory = Path.Combine(outputRoot, "input");
        if (!Directory.Exists(outputRoot) || Directory.Exists(directory)) throw new InvalidDataException();
        Directory.CreateDirectory(directory);
        // Fixed before inference, including cases that may legitimately fail later.
        Fixture[] fixtures = [CreateFixture(directory, "upright", 0, false),
            CreateFixture(directory, "positive_tilt", 2, false), CreateFixture(directory, "negative_tilt", -2, false),
            CreateFixture(directory, "positive_tilt_printed_x", 2, true)];
        var results = new List<object>();
        foreach (Fixture fixture in fixtures)
        {
            if (token.IsCancellationRequested) break;
            var timer = Stopwatch.StartNew();
            string? error = null; string status = "not_started"; object? automatic = null; object? supplied = null;
            bool? workspaceUnchanged = null;
            try
            {
                var axisService = new SelectableAxis(new RustFirstAxisProposalService(new RustAxisProposalService(rust), new OpenCvAxisProposalService(app)), fixture.SuppliedGeometry);
                using var workspace = new ManualPreviewWorkspaceService(axisProposalService: axisService,
                    axisTickMeasurementService: new RustAxisTickMeasurementService(rust),
                    calibrationOcrSuggestionService: new CalibrationOcrWorkerClient(app, assets, () => CalibrationOcrDeviceSelection.Cpu));
                var tabs = await workspace.ImportImagesAsync([fixture.Path], token);
                if (tabs.Count != 1) { status = "import_failed_or_multiple_panels"; }
                else
                {
                    var tab = tabs[0];
                    var originalProject = workspace.CurrentProject;
                    var originalCalibration = tab.Calibration;
                    automatic = await RunPathAsync(workspace, tab.TabId, fixture, "automatic", token);
                    if (tab.PixelWidth == 640 && tab.PixelHeight == 480)
                    {
                        axisService.UseSupplied = true;
                        supplied = await RunPathAsync(workspace, tab.TabId, fixture, "supplied", token);
                    }
                    else supplied = new { path = "supplied", status = "unsupported_imported_panel_dimensions" };
                    workspaceUnchanged = ReferenceEquals(originalProject, workspace.CurrentProject) && ReferenceEquals(originalCalibration, tab.Calibration);
                    status = workspaceUnchanged == true ? "paths_finished" : "unexpected_workspace_mutation";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { status = "cancelled"; error = "CANCELLED"; }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            { status = "case_failed"; error = exception.GetType().Name; }
            bool unchanged = File.Exists(fixture.Path) && Program.Hash(File.ReadAllBytes(fixture.Path)) == fixture.Sha256;
            results.Add(new { fixture.Name, fixture.Angle, fixture.PrintedX, fixture.Sha256, fixture.Bytes,
                declared_x_values = fixture.PrintedX ? PrintedXValues : SessionXValues, declared_y_values = YValues,
                status, error, source_unchanged = unchanged, workspace_unchanged = workspaceUnchanged, automatic, supplied, total_milliseconds = timer.Elapsed.TotalMilliseconds });
            if (token.IsCancellationRequested) break;
        }
        return new { schema = "graphreader.owned-affine-calibration-check.v1", scope = "owned_synthetic_only", review_only = true,
            apply_called = false, production_approved = false, private_payload_reads = 0,
            app_sha256 = Program.Hash(File.ReadAllBytes(app)), rust_sha256 = Program.Hash(File.ReadAllBytes(rust)),
            declared_cases = fixtures.Length, attempted_cases = results.Count, unattempted_cases = fixtures.Length - results.Count, cases = results,
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores };
    }

    private sealed class SelectableAxis(IAxisProposalService automatic, RustAffineAxisGeometry supplied) : IAxisProposalService
    {
        internal bool UseSupplied { get; set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return UseSupplied
                ? Task.FromResult(new RustAxisProposalResult(null, null) { Provider = "opencv-review", AffineGeometry = supplied,
                    ReviewReasons = ["native_affine_geometry"] })
                : automatic.SuggestAsync(image, token);
        }
    }

    private static async Task<object> RunPathAsync(ManualPreviewWorkspaceService workspace, string tabId, Fixture fixture,
        string path, CancellationToken token)
    {
        var stage = Stopwatch.StartNew(); WorkspaceAxisSuggestion? suggestion;
        try { suggestion = await workspace.SuggestAxesAsync(tabId, token); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { return new { path, status = "geometry_failed", error = error.GetType().Name, error_code = FailureCode(error), geometry_milliseconds = stage.Elapsed.TotalMilliseconds }; }
        double geometryMs = stage.Elapsed.TotalMilliseconds;
        if (suggestion is null) return new { path, status = "geometry_unavailable", geometry_milliseconds = geometryMs };
        stage.Restart(); WorkspaceCalibrationLabelSuggestion? labels;
        try { labels = await workspace.ReadCalibrationLabelsAsync(suggestion, token); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { return new { path, status = "read_failed", error = error.GetType().Name, error_code = FailureCode(error), geometry_milliseconds = geometryMs, read_milliseconds = stage.Elapsed.TotalMilliseconds,
            suggestion.Provider, suggestion.ReviewReasons, affine_geometry = suggestion.AffineGeometry is not null }; }
        double readMs = stage.Elapsed.TotalMilliseconds;
        if (labels is null) return new { path, status = "read_unavailable", geometry_milliseconds = geometryMs, read_milliseconds = readMs,
            suggestion.Provider, suggestion.ReviewReasons, affine_geometry = suggestion.AffineGeometry is not null };
        stage.Restart();
        try
        {
        CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(labels.FitGeometry, labels.Response);
        double fitMs = stage.Elapsed.TotalMilliseconds;
        CalibrationTickAnchors? rawAnchors = proposal.Anchors ?? proposal.PrintedXReviewDraft?.Anchors;
        CalibrationTickAnchors? originalAnchors = rawAnchors is null ? null : labels.OriginalAnchors(rawAnchors);
        var ticks = proposal.PairedTicks.Select(pair => new { pair.Axis, pair.PixelPosition, pair.Value, pair.IsEstimated,
            original = labels.OriginalTickPoint(pair.Axis, pair.PixelPosition) }).ToArray();
        var rows = labels.Response.Proposals.Select(row => new { row.Text, row.NumericLiteral, row.NumericValue, row.Role,
            row.SuggestedAxis, row.FailureCode, original_polygon = labels.OriginalPolygon(row) }).ToArray();
        bool runtimeVerified = labels.Response.Runtime is { Provider: "Cpu", RequestedProvider: "Cpu" } runtime &&
            runtime.ModelSha256 == Program.ModelHash && runtime.ManifestSha256 == Program.ManifestHash;
        bool? anchorsInsideOriginal = rawAnchors is null ? null : labels.ContainsOriginal(rawAnchors);
        bool expected = runtimeVerified && anchorsInsideOriginal == true && proposal.PairedTicks.Count == 6 && proposal.PairedTicks.All(pair => !pair.IsEstimated) &&
            proposal.PairedTicks.Where(pair => pair.Axis == "Y").Select(pair => pair.Value).Order().SequenceEqual([0d, 5d, 10d]) &&
            proposal.PairedTicks.Where(pair => pair.Axis == "X").Select(pair => pair.Value).Order().SequenceEqual(fixture.PrintedX ? [0d, 10d, 20d] : [1d, 2d, 3d]) &&
            (fixture.PrintedX ? proposal.PrintedXReviewDraft is not null : proposal.IsComplete);
        object? anchorResiduals = originalAnchors is null ? null : AnchorResiduals(originalAnchors, fixture.ExpectedAnchors);
        return new { path, status = expected ? "expected_review_draft" : "review_incomplete_or_mismatch", suggestion.Provider,
            suggestion.ReviewReasons, affine_geometry = suggestion.AffineGeometry is not null, labels.IsRectified,
            geometry_milliseconds = geometryMs, read_milliseconds = readMs, fit_milliseconds = fitMs,
            tick_measurement_attempted = labels.TickMeasurementAttempted, tick_measurement_error = labels.TickMeasurementErrorCode,
            measured_x_ticks = labels.FitGeometry.XTickPositions, measured_y_ticks = labels.FitGeometry.YTickPositions,
            response_error = labels.Response.FailureCode, runtime_verified = runtimeVerified, anchors_inside_original = anchorsInsideOriginal,
            labels.Response.Timings, labels.Response.Runtime,
            proposal.Reasons, proposal.IsComplete, proposal.UsesEstimatedPositions, printed_x_available = proposal.PrintedXReviewDraft is not null,
            original_anchors = originalAnchors, original_anchor_residual_pixels = anchorResiduals, ticks, rows, apply_called = false };
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { return new { path, status = "fit_or_mapping_failed", error = error.GetType().Name, error_code = FailureCode(error), geometry_milliseconds = geometryMs,
            read_milliseconds = readMs, fit_milliseconds = stage.Elapsed.TotalMilliseconds, suggestion.Provider, labels.IsRectified }; }
    }

    private static string? FailureCode(Exception error) => error is ProductionWorkflowStageException failure ? failure.Failure.Code : null;

    private static object AnchorResiduals(CalibrationTickAnchors actual, CalibrationTickAnchors expected)
    {
        static double Distance(CalibrationOcrPoint a, CalibrationOcrPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        return new { first = Distance(actual.FirstSessionY0, expected.FirstSessionY0),
            upper = Distance(actual.FirstSessionYMaximum, expected.FirstSessionYMaximum),
            final = Distance(actual.FinalSessionY0, expected.FinalSessionY0) };
    }

    private static Fixture CreateFixture(string directory, string name, double angle, bool printed)
    {
        const int width = 640, height = 480;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 100; x <= 580; x++) pixels[380 * width + x] = 0;
        for (int y = 80; y <= 380; y++) pixels[y * width + 100] = 0;
        string[] xLabels = printed ? ["0", "10", "20"] : ["1", "2", "3"];
        string[] yLabels = ["10", "5", "0"];
        for (int i = 0; i < 3; i++)
        {
            for (int y = 381; y <= 384; y++) pixels[y * width + XPositions[i]] = 0;
            for (int x = 96; x < 100; x++) pixels[YPositions[i] * width + x] = 0;
            Place(RenderGlyph(xLabels[i]), XPositions[i], 406);
            Place(RenderGlyph(yLabels[i]), 74, YPositions[i] + (i == 2 ? 1 : 0));
        }
        var original = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.PushTransform(new RotateTransform(angle, width / 2d, height / 2d));
            drawing.DrawImage(original, new Rect(0, 0, width, height));
            drawing.Pop();
        }
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var output = new MemoryStream(); encoder.Save(output); byte[] encoded = output.ToArray();
        string path = Path.Combine(directory, name + ".png");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(encoded);
        Matrix rotation = Matrix.Identity; rotation.RotateAt(angle, width / 2d, height / 2d);
        RustAxisPoint Map(double x, double y) { Point point = rotation.Transform(new Point(x, y)); return new(point.X, point.Y); }
        CalibrationOcrPoint CalibrationAnchor(double x, double y)
        {
            RustAxisPoint point = Map(x, y);
            return new(point.X, point.Y);
        }
        return new(name, angle, printed, path, Convert.ToHexStringLower(SHA256.HashData(encoded)), encoded.Length,
            new(Map(100, 380), Map(580, 380), Map(100, 80), 0),
            new(CalibrationAnchor(140, 380), CalibrationAnchor(140, 80), CalibrationAnchor(540, 380)));

        void Place(Glyph glyph, int centerX, int centerY)
        {
            int left = centerX - glyph.Width / 2, top = centerY - glyph.Height / 2;
            for (int y = 0; y < glyph.Height; y++)
                glyph.Pixels.AsSpan(y * glyph.Width, glyph.Width).CopyTo(pixels.AsSpan((top + y) * width + left, glyph.Width));
        }
    }

    private static Glyph RenderGlyph(string text)
    {
        const int side = 64;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, side, side));
            drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(8, 8));
        }
        var rendered = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
        var gray = new FormatConvertedBitmap(rendered, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[side * side]; gray.CopyPixels(pixels, side, 0);
        int left = side, top = side, right = -1, bottom = -1;
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) if (pixels[y * side + x] < 255)
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left || bottom < top) throw new InvalidDataException();
        int width = right - left + 1, height = bottom - top + 1; byte[] trimmed = new byte[width * height];
        for (int y = 0; y < height; y++) pixels.AsSpan((top + y) * side + left, width).CopyTo(trimmed.AsSpan(y * width, width));
        return new(width, height, trimmed);
    }
}
