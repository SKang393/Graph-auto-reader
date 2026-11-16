// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static class OwnedAffinePartialReviewCheck
{
    private const string AppHash = "89b399ca27411af9d34f8eaf9dec4d094a268e4c767f34c32d5cd48836dbcd19";
    private const string RustHash = "19e24a703907281341b183ffc0c16a6a7ab12bb1c4a2b4e97f41e16a76268347";
    // Independently verified against the frozen 457 inner ZIP, not inferred from probe/App agreement.
    private const string RuntimeFingerprint = "795c2d3bee675747a0a4d3d61fbf446a0f18ad62d80b0baea30689d59319ea2a";
    private static readonly int[] TickXs = [140, 340, 540];
    private static readonly double[] XValues = [1, 2, 3];
    private static readonly string[] Reasons = ["native_affine_geometry", "native_multiple_ranked_candidates"];
    private static readonly string[] Stages = ["preflight", "render", "import", "geometry", "label_read", "review_guards", "preservation"];
    private static readonly string[] IndexedStages = ["preflight", "render", "retained_span_preflight", "import", "geometry", "label_read", "review_guards", "preservation"];
    internal sealed record Outcome(object Report, bool Passed);

    internal static Task<Outcome> RunAsync(string outputRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) => StartAsync(outputRoot, app, assets, rust, budget, false, token);

    internal static Task<Outcome> RunIndexedAsync(string outputRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token) => StartAsync(outputRoot, app, assets, rust, budget, true, token);

    private static Task<Outcome> StartAsync(string outputRoot, string app, string assets, string rust,
        CpuBudgetJob budget, bool indexed, CancellationToken token)
    {
        var completion = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { completion.SetResult(await RunCoreAsync(outputRoot, app, assets, rust, budget, indexed, token)); }
                    catch (Exception error) { completion.SetException(error); }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }));
                Dispatcher.Run();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "owned-affine-partial-review" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task<Outcome> RunCoreAsync(string outputRoot, string app, string assets, string rust,
        CpuBudgetJob budget, bool indexed, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var stages = (indexed ? IndexedStages : Stages).ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        string stage = "preflight", status = "failed";
        string? error = null, fixtureHash = null, imagePath = null;
        long fixtureBytes = 0;
        SuppliedAutomaticAxis? axis = null; CountedTicks? ticks = null; CountedOcr? ocr = null;
        object? view = null, preRefitView = null;
        RealDevCalibrationCheck.OwnedRetainedSpanPreflight? retainedSpan = null;
        var step = Stopwatch.StartNew();
        void FinishStage() { stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds; step.Restart(); }
        void Check(string name, bool value) { checks[name] = value; }
        try
        {
            token.ThrowIfCancellationRequested();
            VerifyRuntime(outputRoot, app, assets, rust, budget, identities);
            FinishStage(); stage = "render";
            var fixture = indexed ? RenderIndexed(outputRoot, token) : Render(outputRoot);
            imagePath = fixture.Path; byte[] image = fixture.Image; RustAffineAxisGeometry frame = fixture.Frame;
            fixtureHash = Program.Hash(image); fixtureBytes = image.Length;
            if (indexed)
            {
                FinishStage(); stage = "retained_span_preflight";
                retainedSpan = await Task.Run(() => RealDevCalibrationCheck.CheckOwnedRetainedSpan(image, frame, token), CancellationToken.None);
                if (!retainedSpan.Passed) throw new InvalidDataException("RETAINED_SPAN_PRECONDITION_FAILED");
            }
            axis = new(frame, fixtureHash);
            ticks = new(new RustAxisTickMeasurementService(rust));
            ocr = new(new CalibrationOcrWorkerClient(app, assets, () => CalibrationOcrDeviceSelection.Cpu));
            FinishStage(); stage = "import";
            Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: axis,
                axisTickMeasurementService: ticks, calibrationOcrSuggestionService: ocr);
            var tabs = await workspace.ImportImagesAsync([imagePath], token);
            if (tabs.Count != 1 || tabs[0].PixelWidth != 640 || tabs[0].PixelHeight != 480) throw new InvalidDataException("Unexpected import dimensions/panels.");
            using var vm = new MainWindowViewModel(workspace);
            var original = workspace.CurrentProject;
            var originalCalibration = tabs[0].Calibration;
            FinishStage(); stage = "geometry";
            await vm.SuggestAxesAsync(token);
            if (!vm.HasAffineAxisSuggestion || !vm.HasAxisGeometryReviewWarning || vm.IsManualAxisDraft)
                throw new InvalidDataException("Supplied automatic-tag affine geometry was not retained.");
            string warning = vm.AxisGeometryReviewWarningText;
            FinishStage(); stage = "label_read";
            await vm.ReadCalibrationLabelsAsync(token);
            Check("one_automatic_tag_geometry_call", axis.Calls == 1);
            Check("one_review_partial_tick_call_no_strict_call", ticks.PartialCalls == 1 && ticks.StrictCalls == 0);
            Check("one_cpu_ocr_call", ocr.Calls == 1);
            Check("measured_x_three_y_empty", ticks.Result is { ErrorCode: null, Evidence: { } evidence } &&
                evidence.XTickPositions.Count == 3 && evidence.YTickPositions.Count == 0);
            Check("same_derived_buffer_tick_and_ocr", ticks.Image is not null && ocr.InputHash == Program.Hash(ticks.Image));
            if (indexed) Check("preflight_derived_matches_actual_receipt", retainedSpan is not null && ocr.InputHash == retainedSpan.DerivedSha256);
            Check("tick_array_echo", ocr.Request is { MeasuredXTicks: { } xs, MeasuredYTicks: null } &&
                ticks.Result?.Evidence is { } measured && xs.SequenceEqual(measured.XTickPositions));
            Check("literal_x_values_unchanged", ocr.Response is { FailureCode: null } response &&
                response.Proposals.Where(row => row.Role == "XTick" && row.NumericLiteral && row.NumericValue is not null)
                    .Select(row => row.NumericValue!.Value).Order().SequenceEqual(XValues));
            Check("reviewed_cpu_runtime", ValidRuntime(ocr.Response?.Runtime, identities));
            FinishStage(); stage = "review_guards";
            Check("native_warning_and_automatic_kind_preserved", vm.HasAffineAxisSuggestion && !vm.IsManualAxisDraft &&
                vm.HasAxisGeometryReviewWarning && warning == vm.AxisGeometryReviewWarningText);
            preRefitView = new { vm.SuggestedFirstSession, vm.SuggestedFinalSession, vm.SuggestedYMaximum,
                paired_row_count = vm.CalibrationPairedTickRows.Count, vm.HasEstimatedCalibrationPositions };
            Check("missing_y_and_unreviewed_apply_blocked", vm.SuggestedYMaximum.Length == 0 && !vm.AxisSuggestionReviewed &&
                !vm.ApplyAxisSuggestionCommand.CanExecute(null));
            vm.AxisSuggestionReviewed = true;
            Check("review_does_not_invent_missing_value", !vm.ApplyAxisSuggestionCommand.CanExecute(null));
            string rawReceiptHash = Program.Hash(JsonSerializer.SerializeToUtf8Bytes(ocr.Response, Program.Json));
            vm.RefitSelectedCalibrationLabels();
            Check("measured_partial_x_values", vm.SuggestedFirstSession == "1" && vm.SuggestedFinalSession == "3" &&
                vm.CalibrationPairedTickRows.Count == 3 && !vm.HasEstimatedCalibrationPositions);
            Check("refit_requires_fresh_review_without_reocr", !vm.AxisSuggestionReviewed && !vm.ApplyAxisSuggestionCommand.CanExecute(null) &&
                ocr.Calls == 1 && ticks.PartialCalls == 1 && axis.Calls == 1 && vm.SuggestedYMaximum.Length == 0);
            Check("raw_ocr_receipt_unchanged_by_refit", rawReceiptHash == Program.Hash(JsonSerializer.SerializeToUtf8Bytes(ocr.Response, Program.Json)));
            view = new { vm.SuggestedFirstSession, vm.SuggestedFinalSession, vm.SuggestedYMaximum, vm.AxisSuggestionReviewed,
                vm.HasEstimatedCalibrationPositions, paired_rows = vm.CalibrationPairedTickRows.ToArray(),
                warning = vm.AxisGeometryReviewWarningText, apply_enabled = vm.ApplyAxisSuggestionCommand.CanExecute(null) };
            FinishStage(); stage = "preservation";
            Check("scientific_state_unchanged", ReferenceEquals(original, workspace.CurrentProject) && ReferenceEquals(originalCalibration, tabs[0].Calibration));
            Check("original_png_unchanged", Program.Hash(File.ReadAllBytes(imagePath)) == fixtureHash);
            token.ThrowIfCancellationRequested(); FinishStage();
            status = checks.Values.All(value => value) ? "passed" : "expectation_failed";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            stages[stage] = "failed"; timings[stage] = step.Elapsed.TotalMilliseconds;
            status = failure is OperationCanceledException ? "cancelled" : "failed";
            error = failure is InvalidDataException ? failure.Message : failure is ProductionWorkflowStageException known ? known.Failure.Code :
                failure is TimeoutException ? failure.Message : failure.GetType().Name;
        }
        bool? sourcePreserved = null;
        string? preservationError = null;
        if (imagePath is not null && fixtureHash is not null)
        {
            var preservationTimer = Stopwatch.StartNew();
            try { sourcePreserved = Program.Hash(File.ReadAllBytes(imagePath)) == fixtureHash; }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                sourcePreserved = false;
                preservationError = failure.GetType().Name;
            }
            timings["preservation_postcheck"] = preservationTimer.Elapsed.TotalMilliseconds;
        }
        if (sourcePreserved == false)
        {
            checks["original_png_unchanged"] = false;
            stages["preservation"] = "failed";
            if (status == "passed") status = "expectation_failed";
        }
        bool passed = status == "passed";
        return new(new
        {
            schema = "graphreader.owned-affine-partial-review.v1", status, error,
            fixture_mode = indexed ? "indexed_pixel_center_segments" : "original_wpf_vector_segments", retained_span_preflight = retainedSpan,
            declared_cases = 1, completed_cases = stages["preservation"] == "completed" ? 1 : 0,
            failed_cases = stages.ContainsValue("failed") ? 1 : 0, expectation_failed_cases = status == "expectation_failed" ? 1 : 0,
            stages, stage_counts = new { completed = stages.Values.Count(value => value == "completed"),
                failed = stages.Values.Count(value => value == "failed"), unattempted = stages.Values.Count(value => value == "unattempted") },
            timings, checks, runtime_files = identities, required_runtime_fingerprint = RuntimeFingerprint,
            actual_runtime_fingerprint = identities.Count == 20 ? Fingerprint(identities) : null, preservation_error = preservationError,
            fixture = new { source_sha256 = fixtureHash, source_bytes = fixtureBytes, width = 640, height = 480, angle_degrees = 2,
                renderer = indexed ? "Indexed-pixel segment-distance<=1.5 for ALL axes/ticks; same WPF SegoeUI16 labels1/2/3; Y90px geometric gap" : "Black3px axes; X ticks140/340/540 with SegoeUI16 labels1/2/3; Y interior90px gap; no Y labels or ticks",
                geometry_source = "Explicit source-bound automatic-tag frame, not detector inference", original_pixels_preserved = sourcePreserved },
            automatic_geometry_calls = axis?.Calls ?? 0, strict_tick_calls = ticks?.StrictCalls ?? 0,
            partial_tick_calls = ticks?.PartialCalls ?? 0, ocr_calls = ocr?.Calls ?? 0,
            tick_receipt = ticks?.Result, ocr_request = ocr?.Request, ocr_response = ocr?.Response, pre_refit_view = preRefitView, view,
            failed_checks = checks.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray(),
            apply_called = false, export_called = false, detector_accuracy_evaluated = false, production_approved = false,
            model_status = "Existing reviewed CPU OCR asset; not production-approved", private_reads = 0,
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores, elapsed_milliseconds = timer.Elapsed.TotalMilliseconds,
        }, passed);
    }

    private static void VerifyRuntime(string output, string app, string assets, string rust, CpuBudgetJob budget,
        Dictionary<string, string> identities)
    {
        string artifactPrefix = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar;
        if (!budget.SilentBreakawayVerified || budget.MaximumLogicalCores > .5 || !Path.IsPathFullyQualified(output) ||
            !Path.GetFullPath(output).StartsWith(artifactPrefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(output) ||
            (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0 || !Path.IsPathFullyQualified(app) ||
            !Path.IsPathFullyQualified(assets) || !Path.IsPathFullyQualified(rust)) throw new InvalidDataException();
        Program.RequireHash(Program.ReadBounded(app, 16_777_216), AppHash);
        Program.RequireHash(Program.ReadBounded(rust, 16_777_216), RustHash);
        string appRoot = Path.GetDirectoryName(app)!, probeRoot = Path.GetDirectoryName(typeof(OwnedAffinePartialReviewCheck).Assembly.Location)!;
        string[] names = RealDevCalibrationCheck.OwnedAffineRuntimeNames.ToArray();
        if (names.Length != 20) throw new InvalidDataException();
        foreach (string name in names)
        {
            string hash = Program.Hash(Program.ReadBounded(Path.Combine(appRoot, name), 128 * 1024 * 1024));
            Program.RequireHash(Program.ReadBounded(Path.Combine(probeRoot, name), 128 * 1024 * 1024), hash);
            identities.Add(name, hash);
        }
        if (Fingerprint(identities) != RuntimeFingerprint) throw new InvalidDataException("Frozen 457 runtime fingerprint mismatch.");
        foreach (var assembly in new[] { typeof(MainWindowViewModel).Assembly, typeof(RustAxisGeometry).Assembly,
            typeof(GraphReader.Imaging.AffineRasterRectifier).Assembly, typeof(GraphReader.Ocr.OcrImage).Assembly })
            Program.RequireHash(Program.ReadBounded(assembly.Location, 16_777_216), identities[assembly.GetName().Name + ".dll"]);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "en_PP-OCRv5_mobile_rec.onnx"), 64 * 1024 * 1024), Program.ModelHash);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "recognizer.json"), 131_072), Program.ManifestHash);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"), 131_072), "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4");
        Program.RequireHash(Program.ReadBounded(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Notice.txt"), 131_072), "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729");
    }

    private static string Fingerprint(Dictionary<string, string> identities) => Program.Hash(Encoding.UTF8.GetBytes(
        string.Concat(identities.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "\t" + pair.Value + "\n"))));

    private static bool ValidRuntime(CalibrationOcrRuntime? runtime, Dictionary<string, string> identities) =>
        runtime is { Provider: "Cpu", RequestedProvider: "Cpu", IntraOperationThreads: 1, InterOperationThreads: 1,
            WorkerCount: 1, SessionCreationCount: 1, PersistentCache: false, CpuBudget: { Verified: true, MaximumLogicalCores: <= .5, PriorityClass: "BelowNormal" } } &&
        runtime.ModelSha256 == Program.ModelHash && runtime.ManifestSha256 == Program.ManifestHash && runtime.Files.Count == 4 &&
        runtime.Files.All(file => identities.TryGetValue(file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file.Name : file.Name + ".dll", out string? hash) && hash == file.Sha256);

    private static (string Path, byte[] Image, RustAffineAxisGeometry Frame) RenderIndexed(string output, CancellationToken token)
    {
        // Preserve the original label rendering. Axis/tick pixels use the existing
        // OwnedAffineTickObservationCheck indexed-center distance convention below.
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 480));
            drawing.PushTransform(new RotateTransform(2, 320, 240));
            for (int index = 0; index < TickXs.Length; index++)
            {
                var text = new FormattedText((index + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1);
                drawing.DrawText(text, new(TickXs[index] - text.Width / 2, 397));
            }
            drawing.Pop();
        }
        var labels = new RenderTargetBitmap(640, 480, 96, 96, PixelFormats.Pbgra32); labels.Render(visual);
        var gray = new FormatConvertedBitmap(labels, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[640 * 480]; gray.CopyPixels(pixels, 640, 0);
        Matrix matrix = Matrix.Identity; matrix.RotateAt(2, 320, 240);
        Point Map(double x, double y) => matrix.Transform(new Point(x, y));
        var segments = new List<(Point A, Point B)>
        {
            (Map(100, 380), Map(580, 380)), (Map(100, 80), Map(100, 140)), (Map(100, 230), Map(100, 380)),
        };
        segments.AddRange(TickXs.Select(x => (Map(x, 380), Map(x, 386))));
        for (int row = 0; row < 480; row++)
        {
            token.ThrowIfCancellationRequested();
            for (int column = 0; column < 640; column++)
                if (segments.Any(segment => IndexedSegmentDistance(column, row, segment.A, segment.B) <= 1.5)) pixels[row * 640 + column] = 0;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(640, 480, 96, 96, PixelFormats.Gray8, null, pixels, 640)));
        using var stream = new MemoryStream(); encoder.Save(stream); byte[] image = stream.ToArray();
        if (image.Length > 131_072) throw new InvalidDataException("Owned PNG exceeds its declared allowance.");
        string path = Path.Combine(output, "owned-partial-affine-indexed.png");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(image);
        RustAxisPoint AxisPoint(double x, double y) { Point p = Map(x, y); return new(p.X, p.Y); }
        return (path, image, new(AxisPoint(100, 380), AxisPoint(580, 380), AxisPoint(100, 80), 0));
    }

    // Identical arithmetic to OwnedAffineTickObservationCheck.Distance; no pixel shift.
    private static double IndexedSegmentDistance(double x, double y, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2));
    }

    private static (string Path, byte[] Image, RustAffineAxisGeometry Frame) Render(string output)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 480));
            drawing.PushTransform(new RotateTransform(2, 320, 240));
            var pen = new Pen(Brushes.Black, 3);
            drawing.DrawLine(pen, new(100, 380), new(580, 380));
            drawing.DrawLine(pen, new(100, 80), new(100, 140));
            drawing.DrawLine(pen, new(100, 230), new(100, 380));
            for (int index = 0; index < TickXs.Length; index++)
            {
                drawing.DrawLine(pen, new(TickXs[index], 380), new(TickXs[index], 386));
                var text = new FormattedText((index + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1);
                drawing.DrawText(text, new(TickXs[index] - text.Width / 2, 397));
            }
            drawing.Pop();
        }
        var raster = new RenderTargetBitmap(640, 480, 96, 96, PixelFormats.Pbgra32); raster.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(raster));
        using var stream = new MemoryStream(); encoder.Save(stream); byte[] image = stream.ToArray();
        if (image.Length > 131_072) throw new InvalidDataException("Owned PNG exceeds its declared allowance.");
        string path = Path.Combine(output, "owned-partial-affine.png");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(image);
        Matrix matrix = Matrix.Identity; matrix.RotateAt(2, 320, 240);
        RustAxisPoint Map(double x, double y) { Point p = matrix.Transform(new Point(x, y)); return new(p.X, p.Y); }
        return (path, image, new(Map(100, 380), Map(580, 380), Map(100, 80), 0));
    }

    private sealed class SuppliedAutomaticAxis(RustAffineAxisGeometry frame, string imageHash) : IAxisProposalService
    {
        internal int Calls { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Program.RequireHash(image, imageHash);
            return Task.FromResult(new RustAxisProposalResult(null, null) { Provider = "opencv-review", AffineGeometry = frame, ReviewReasons = Reasons });
        }
    }

    private sealed class CountedTicks(IAxisTickMeasurementService inner) : IAxisTickMeasurementService
    {
        internal int StrictCalls { get; private set; }
        internal int PartialCalls { get; private set; }
        internal byte[]? Image { get; private set; }
        internal AxisTickMeasurementResult? Result { get; private set; }
        public Task<AxisTickMeasurementResult> MeasureAsync(byte[] image, RustAxisGeometry geometry, CancellationToken token)
        { StrictCalls++; throw new InvalidOperationException("This affine review control must not use strict measurement."); }
        public async Task<AxisTickMeasurementResult> MeasureManualPartialAsync(byte[] image, RustAxisGeometry geometry, CancellationToken token)
        {
            PartialCalls++; Image = image;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { return Result = await inner.MeasureManualPartialAsync(image, geometry, deadline.Token); }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new TimeoutException("TICK_SERVICE_TIMEOUT", error); }
        }
    }

    private sealed class CountedOcr(CalibrationOcrWorkerClient inner) : ICalibrationOcrSuggestionService
    {
        internal int Calls { get; private set; }
        internal string? InputHash { get; private set; }
        internal CalibrationOcrRequest? Request { get; private set; }
        internal CalibrationOcrResponse? Response { get; private set; }
        public async Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image, CancellationToken token)
        {
            Calls++; Request = request; InputHash = Program.Hash(image.ToArray());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try { return Response = await inner.SuggestAsync(request, image, deadline.Token); }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new TimeoutException("OCR_SERVICE_TIMEOUT", error); }
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
