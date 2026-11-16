// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal sealed record OwnedContentSpanOutcome(object Report, bool Passed);
    private sealed record ContentGlyph(int Width, int Height, byte[] Pixels);
    private static readonly int[] ContentXTicks = [140, 260, 380];
    private static readonly int[] ContentYTicks = [280, 330, 380];

    // Manifest is frozen by the lead after the probe/runtime build. Its hash is
    // supplied independently; every App/loaded-probe library is verified before drawing.
    internal static async Task<OwnedContentSpanOutcome> RunOwnedContentSpanCalibrationAsync(string app, string assets,
        string rust, string outputRoot, string manifest, string manifestSha256, CpuBudgetJob budget, CancellationToken token)
    {
        string[] stageNames = ["runtime", "fixture", "import", "geometry", "ocr", "fit", "preservation"];
        var stages = stageNames.ToDictionary(s => s, _ => "unattempted", StringComparer.Ordinal);
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        string stage = "runtime", status = "failed"; string? failure = null, sourceHash = null, ocrFailureCode = null;
        string? geometryProvider = null, tickMeasurementError = null; bool? tickMeasurementAttempted = null;
        string? actualManifestHash = null, runtimeAppHash = null, runtimeRustHash = null, probeHash = null;
        int runtimeLibrariesVerified = 0;
        var total = Stopwatch.StartNew(); var step = Stopwatch.StartNew();
        var held = new List<FileStream>(); int geometryCalls = 0, ocrCalls = 0;
        ContentTraceAxis? diagnostic = null; ContentTraceOcr? countedOcr = null;
        int? literalCount = null, pairedCount = null; bool? sourceUnchanged = null, workspaceUnchanged = null;
        AxisDecisionTrace? trace = null; object? fitSummary = null, literalRows = null, fixtureProperties = null, preparedPanel = null;
        string? fixturePath = null; byte[]? fixtureBytes = null;
        void Next(string next) { stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds; stage = next; step.Restart(); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (budget.MaximumLogicalCores is <= 0 or > .5 || !budget.SilentBreakawayVerified ||
                !new[] { app, assets, rust, outputRoot, manifest }.All(Path.IsPathFullyQualified)) throw new InvalidDataException("PREFLIGHT");
            string artifacts = Path.GetFullPath("artifacts").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string output = Path.GetFullPath(outputRoot);
            if (!output.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output)) throw new InvalidDataException("OUTPUT_SCOPE");
            for (DirectoryInfo? dir = new(output); dir is not null; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || dir.Name.Equals("Data", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("OUTPUT_REPARSE");
            byte[] manifestBytes = Program.ReadBounded(manifest, 32768); actualManifestHash = Program.Hash(manifestBytes);
            Program.RequireHash(manifestBytes, manifestSha256);
            using JsonDocument document = JsonDocument.Parse(manifestBytes); JsonElement root = document.RootElement;
            if (root.GetProperty("schema").GetString() != "graphreader.owned-content-span-runtime.v1") throw new InvalidDataException("RUNTIME_SCHEMA");
            string appHash = root.GetProperty("app_exe_sha256").GetString() ?? throw new InvalidDataException();
            string rustHash = root.GetProperty("rust_sha256").GetString() ?? throw new InvalidDataException();
            if (rustHash != "eeb2515a48546c9b50f9c8221f334c028a9dadce34cb1fa32ca7c5d477e87430") throw new InvalidDataException("RUST_IDENTITY");
            void Pin(string path, string expected)
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                held.Add(stream); if (HashStream(stream, token) != expected) throw new InvalidDataException("RUNTIME_IDENTITY");
            }
            Pin(app, appHash); Pin(rust, rustHash); runtimeAppHash = appHash; runtimeRustHash = rustHash;
            probeHash = Program.Hash(Program.ReadBounded(typeof(RealDevCalibrationCheck).Assembly.Location, 16_777_216));
            JsonElement libraries = root.GetProperty("libraries");
            string[] names = OwnedAffineRuntimeNames.Order(StringComparer.Ordinal).ToArray();
            if (names.Length != 20 || libraries.EnumerateObject().Count() != 20) throw new InvalidDataException("RUNTIME_COUNT");
            string appDir = Path.GetDirectoryName(app)!, probeDir = Path.GetDirectoryName(typeof(RealDevCalibrationCheck).Assembly.Location)!;
            foreach (string name in names)
            {
                string hash = libraries.GetProperty(name).GetString() ?? throw new InvalidDataException();
                Pin(Path.Combine(appDir, name), hash); Pin(Path.Combine(probeDir, name), hash);
                if (name.StartsWith("GraphReader.", StringComparison.Ordinal) || name is "OpenCvSharp.dll" or "Microsoft.ML.OnnxRuntime.dll")
                    Pin(Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(name))).Location, hash);
                runtimeLibrariesVerified++;
            }
            if (libraries.GetProperty("OpenCvSharpExtern.dll").GetString() != "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31") throw new InvalidDataException("OPENCV_IDENTITY");
            Pin(Path.Combine(assets, "en_PP-OCRv5_mobile_rec.onnx"), Program.ModelHash);
            Pin(Path.Combine(assets, "recognizer.json"), Program.ManifestHash);
            Pin(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"), "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
            Pin(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Notice.txt"), "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729");
            Next("fixture");
            (byte[] image, bool premise, object properties) = RenderContentSpanFixture(token); fixtureProperties = properties;
            fixtureBytes = image; sourceHash = Program.Hash(image);
            if (!premise) throw new ContentUnexercisedException("FIXTURE_PREMISE");
            if (image.Length > 32768) throw new InvalidDataException("FIXTURE_SIZE");
            string path = Path.Combine(output, "content-span.png");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { fixturePath = path; stream.Write(image); }
            Next("import");
            diagnostic = new ContentTraceAxis(rust);
            var primary = new TraceAxis(diagnostic);
            countedOcr = new ContentTraceOcr(new CalibrationOcrWorkerClient(app, assets, () => CalibrationOcrDeviceSelection.Cpu));
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: primary,
                calibrationOcrSuggestionService: countedOcr);
            var tabs = await workspace.ImportImagesAsync([path], token);
            if (tabs.Count != 1) throw new ContentUnexercisedException("IMPORT_PANEL_COUNT");
            var tab = tabs[0]; var project = workspace.CurrentProject; var calibration = tab.Calibration;
            var panel = project.Panels.Single(p => p.PanelId.Value.ToString() == tab.PanelId);
            var crop = panel.Crop;
            preparedPanel = new { crop.X, crop.Y, crop.Width, crop.Height, tab.PixelWidth, tab.PixelHeight,
                mapping = "original_to_panel = original minus encoded crop origin" };
            if (crop.X != Math.Truncate(crop.X) || crop.Y != Math.Truncate(crop.Y) || crop.X < 0 || crop.Y < 0 ||
                crop.Width != tab.PixelWidth || crop.Height != tab.PixelHeight || crop.X + crop.Width > 512 || crop.Y + crop.Height > 512 ||
                crop.X > 20 || crop.Y > 0 || crop.X + crop.Width <= 440 || crop.Y + crop.Height <= 447)
                throw new ContentUnexercisedException("PREPARATION_REMOVED_REQUIRED_CONTENT");
            int offsetX = (int)crop.X, offsetY = (int)crop.Y;
            diagnostic.Width = tab.PixelWidth; diagnostic.Height = tab.PixelHeight;
            Next("geometry"); geometryCalls++;
            WorkspaceAxisSuggestion? suggestion = await workspace.SuggestAxesAsync(tab.TabId, token);
            geometryProvider = suggestion?.Provider ?? diagnostic.Last?.Proposal.Provider;
            trace = diagnostic.Last?.Trace;
            RustAxisGeometry? geometry = suggestion?.Geometry;
            bool exercised = primary.Calls == 1 && trace is { Route: AxisDecisionRoute.CONNECTED_RUNS } &&
                geometry is not null && geometry.Left == 80 - offsetX && geometry.Bottom == 420 - offsetY &&
                geometry.Right == 440 - offsetX && geometry.Top == 250 - offsetY &&
                geometry.XTickPositions.SequenceEqual(ContentXTicks.Select(x => x - offsetX)) &&
                geometry.YTickPositions.SequenceEqual(ContentYTicks.Select(y => y - offsetY));
            if (!exercised) { stages[stage] = "failed"; timings[stage] = step.Elapsed.TotalMilliseconds; status = "unexercised"; failure = "ROUTE_OR_GEOMETRY_MISMATCH"; }
            else
            {
                Next("ocr"); ocrCalls++;
                WorkspaceCalibrationLabelSuggestion? labels = await workspace.ReadCalibrationLabelsAsync(suggestion!, token);
                if (labels is null) throw new ContentStageFailureException("OCR_SUGGESTION_UNAVAILABLE");
                tickMeasurementAttempted = labels.TickMeasurementAttempted;
                tickMeasurementError = labels.TickMeasurementErrorCode;
                ocrFailureCode = labels.Response.FailureCode;
                literalCount = labels.Response.Proposals.Count(row => row.NumericLiteral && row.NumericValue is not null);
                literalRows = labels.Response.Proposals.Select(row => new { row.Text, row.Role, row.NumericLiteral, row.NumericValue, row.FailureCode }).ToArray();
                if (ocrFailureCode is not null) throw new ContentStageFailureException("OCR_REFUSED");
                if (labels.Response.Runtime is not { Provider: "Cpu", RequestedProvider: "Cpu" } runtime ||
                    runtime.ModelSha256 != Program.ModelHash || runtime.ManifestSha256 != Program.ManifestHash) throw new InvalidDataException("OCR_RUNTIME");
                Next("fit");
                CalibrationTickProposalResult fit = CalibrationTickProposal.CreateReviewDraft(labels.FitGeometry, labels.Response);
                pairedCount = fit.PairedTicks.Count;
                fitSummary = new { fit.IsComplete, fit.UsesEstimatedPositions, printed_x_review_available = fit.PrintedXReviewDraft is not null,
                    reasons = fit.Reasons, pairs = fit.PairedTicks.Select(p => new { p.Axis, p.Value, p.IsEstimated }).ToArray() };
                Next("preservation");
                status = "review_draft_recorded";
            }
            workspaceUnchanged = ReferenceEquals(project, workspace.CurrentProject) && ReferenceEquals(calibration, tab.Calibration);
            if (workspaceUnchanged != true) { status = "preservation_failed"; failure = "WORKSPACE_PRESERVATION"; }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { failure = error is ContentUnexercisedException or ContentStageFailureException ? error.Message : error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : error.GetType().Name;
            if (error is ContentUnexercisedException) status = "unexercised";
            stages[stage] = "failed"; timings[stage] = step.Elapsed.TotalMilliseconds; }
        finally
        {
            trace ??= diagnostic?.Last?.Trace;
            if (countedOcr?.Last is { } returned)
            {
                ocrFailureCode = returned.FailureCode;
                if (returned.Proposals is { } rows)
                {
                    literalCount = rows.Count(row => row is not null && row.NumericLiteral && row.NumericValue is not null);
                    literalRows = rows.Select(row => row is null ? null : new { row.Text, row.Role, row.NumericLiteral, row.NumericValue, row.FailureCode }).ToArray();
                }
            }
            if (fixturePath is not null && fixtureBytes is not null && sourceHash is not null)
            {
                var preservationClock = Stopwatch.StartNew();
                try { sourceUnchanged = Program.Hash(Program.ReadBounded(fixturePath, 32768)) == sourceHash && Program.Hash(fixtureBytes) == sourceHash; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { sourceUnchanged = false; }
                stages["preservation"] = sourceUnchanged == true ? "completed" : "failed";
                timings["preservation"] = preservationClock.Elapsed.TotalMilliseconds;
                if (sourceUnchanged != true) { status = "preservation_failed"; failure ??= "SOURCE_PRESERVATION"; }
            }
            foreach (FileStream stream in held) stream.Dispose();
        }
        bool passed = status == "review_draft_recorded" && failure is null && sourceUnchanged == true && workspaceUnchanged == true;
        return new(new { schema = "graphreader.owned-content-span-calibration.v1", status, failure, stages, timings,
            declared_cases = 1, workspace_geometry_attempts = geometryCalls, rust_client_attempts = diagnostic?.ClientAttempts ?? 0,
            native_fallback_calls = 0, workspace_label_attempts = ocrCalls, ocr_client_attempts = countedOcr?.ClientAttempts ?? 0,
            geometry_provider = geometryProvider, tick_measurement_attempted = tickMeasurementAttempted,
            tick_measurement_error = tickMeasurementError,
            tick_measurement_not_needed_for_rust = tickMeasurementAttempted is null ? (bool?)null : geometryProvider == "rust" && tickMeasurementAttempted == false,
            ocr_failure_code = ocrFailureCode, rust_trace = trace,
            prepared_panel = preparedPanel,
            source_sha256 = sourceHash, source_unchanged = sourceUnchanged, workspace_unchanged = workspaceUnchanged,
            fixture_properties = fixtureProperties, literal_numeric_count = literalCount, literal_rows = literalRows,
            paired_count = pairedCount, fit = fitSummary,
            expected_runtime_manifest_sha256 = manifestSha256, actual_runtime_manifest_sha256 = actualManifestHash,
            app_sha256 = runtimeAppHash, rust_sha256 = runtimeRustHash, probe_sha256 = probeHash,
            runtime_libraries_verified = runtimeLibrariesVerified, elapsed_milliseconds = total.Elapsed.TotalMilliseconds, budget.MaximumLogicalCores,
            scope = "owned automatic geometry and literal OCR; review draft only", apply_called = false, export_called = false,
            private_reads = 0, sealed_reads = 0, model_changes = false }, passed);
    }

    private sealed class ContentUnexercisedException(string message) : Exception(message);
    private sealed class ContentStageFailureException(string message) : Exception(message);

    private sealed class ContentTraceAxis(string worker) : IAxisProposalService
    {
        internal int Width { get; set; }
        internal int Height { get; set; }
        internal int ClientAttempts { get; private set; }
        internal AxisDecisionDiagnosticResult? Last { get; private set; }
        public async Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token)
        {
            var (gray, _, _) = Program.Decode(image, [0, 0, Width, Height], token);
            ClientAttempts++;
            Last = await AxisDecisionDiagnosticClient.RunAsync(worker, Width, Height, gray.Pixels, token).ConfigureAwait(false);
            return Last.Proposal;
        }
    }

    private sealed class ContentTraceOcr(CalibrationOcrWorkerClient inner) : ICalibrationOcrSuggestionService
    {
        internal int ClientAttempts { get; private set; }
        internal CalibrationOcrResponse? Last { get; private set; }
        public async Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        { ClientAttempts++; return Last = await inner.SuggestAsync(request, image, token).ConfigureAwait(false); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // Frozen one-case recipe: Rust's exact 512-square decoy raster, with six
    // separately rendered Segoe UI glyphs; no oracle geometry enters inference.
    private static (byte[] Encoded, bool Premise, object Properties) RenderContentSpanFixture(CancellationToken token)
    {
        const int side = 512; byte[] pixels = Enumerable.Repeat((byte)255, side * side).ToArray();
        for (int x = 80; x <= 440; x++) pixels[420 * side + x] = 0;
        for (int y = 250; y <= 420; y++) pixels[y * side + 80] = 0;
        foreach (int x in ContentXTicks) for (int y = 421; y <= 425; y++) pixels[y * side + x] = 0;
        foreach (int y in ContentYTicks) for (int x = 74; x <= 79; x++) pixels[y * side + x] = 0;
        for (int y = 0; y <= 398; y += 2) pixels[y * side + 20] = 0;
        pixels[420 * side + 20] = 0;
        byte[] originalLines = pixels.ToArray();
        string[] xLabels = ["1", "2", "3"], yLabels = ["10", "5", "0"]; bool labelLanesClear = true;
        for (int i = 0; i < 3; i++) { token.ThrowIfCancellationRequested(); Place(ContentRenderGlyph(xLabels[i]), ContentXTicks[i], 447, true); Place(ContentRenderGlyph(yLabels[i]), 52, ContentYTicks[i], false); }
        bool originalInkPreserved = Enumerable.Range(0, pixels.Length).All(i => originalLines[i] != 0 || pixels[i] == 0);
        int[] rows = new int[side], columns = new int[side];
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) if (pixels[y * side + x] < 128) { rows[y]++; columns[x]++; }
        int firstRow = Array.FindIndex(rows, n => n > 0), lastRow = Array.FindLastIndex(rows, n => n > 0);
        int firstColumn = Array.FindIndex(columns, n => n > 0), lastColumn = Array.FindLastIndex(columns, n => n > 0);
        bool UniqueSelection(int[] scores, int expected, int minimum)
        {
            int best = scores.Max(); return best >= minimum && Array.IndexOf(scores, best) == expected &&
                !scores.Where((n, i) => Math.Abs(i - expected) > 3 && n * 10 >= best * 9).Any();
        }
        bool primaryMissing = columns.Max() < side * 2 / 5;
        bool contentSelection = UniqueSelection(rows, 420, (lastColumn - firstColumn + 1) * 2 / 5) &&
            UniqueSelection(columns, 20, (lastRow - firstRow + 1) * 2 / 5);
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        return (output.ToArray(), labelLanesClear && originalInkPreserved && primaryMissing && contentSelection,
            new { label_lanes_clear = labelLanesClear, original_fixture_ink_preserved = originalInkPreserved,
                primary_column_below_existing_minimum = primaryMissing, content_projection_matches_fixed_fixture = contentSelection });
        void Place(ContentGlyph glyph, int cx, int cy, bool horizontal)
        {
            int left = cx - glyph.Width / 2, top = cy - glyph.Height / 2;
            labelLanesClear &= horizontal ? top > 425 && left >= 0 && left + glyph.Width <= side && top + glyph.Height <= side
                : left > 20 && left + glyph.Width <= 74 && top >= 0 && top + glyph.Height <= side;
            if (!labelLanesClear) return;
            for (int y = 0; y < glyph.Height; y++) glyph.Pixels.AsSpan(y * glyph.Width, glyph.Width).CopyTo(pixels.AsSpan((top + y) * side + left, glyph.Width));
        }
    }

    private static ContentGlyph ContentRenderGlyph(string text)
    {
        const int side = 64; var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, side, side));
            context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(8, 8));
        }
        var rendered = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
        var gray = new FormatConvertedBitmap(rendered, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[side * side]; gray.CopyPixels(pixels, side, 0);
        int left = side, top = side, right = -1, bottom = -1;
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) if (pixels[y * side + x] < 255)
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left || bottom < top) throw new InvalidDataException("GLYPH_EMPTY");
        int width = right - left + 1, height = bottom - top + 1; byte[] trimmed = new byte[width * height];
        for (int y = 0; y < height; y++) pixels.AsSpan((top + y) * side + left, width).CopyTo(trimmed.AsSpan(y * width, width));
        return new(width, height, trimmed);
    }
}
