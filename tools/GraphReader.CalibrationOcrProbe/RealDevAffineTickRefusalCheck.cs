// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private sealed record RefusalTarget(int Index, byte[] Image, int Width, int Height, PdfRectD Crop,
        string SourceImageHash, RustAffineAxisGeometry Frame);
    private sealed record RefusalWire(string Result, string Stage, int? SeedGraySum, int? Channels,
        int? BridgeWork, int XTicks, int YTicks, int ExitCode, int RawBytes, string RawSha256);
    internal sealed record AffineTickRefusalOutcome(object Report, bool Completed);
    private static string? RefusalSelectionFailure(int count, RefusalTarget? target) =>
        count == 1 && target is not null ? null : count == 0 ? "NO_UNIQUE_AFFINE_TARGET" : "MULTIPLE_AFFINE_TARGETS";

    internal static async Task<AffineTickRefusalOutcome> RunAffineTickRefusalAsync(string corpus, string app, string assets,
        string geometryRust, string diagnosticRust, string selectorOutput, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !new[] { corpus, app, assets, geometryRust, diagnosticRust, selectorOutput }.All(Path.IsPathFullyQualified) ||
            File.Exists(selectorOutput) || Directory.Exists(selectorOutput) || !Directory.Exists(Path.GetDirectoryName(selectorOutput)))
            throw new InvalidDataException();
        using Candidate pins = Candidate.VerifyAffineTickRefusal(app, assets, geometryRust, diagnosticRust, token);
        var reader = ResolveEmbeddedImageReader();
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.AssignmentSha256 != CachedAxisAssignmentHash || metadata.SelectedInventorySha256 != CachedAxisInventoryHash)
            throw new InvalidDataException();
        FrozenRealCorpusProject[] projects = selector.EnumerateSelectedProjects(token)
            .OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (projects.Length != Denominator || projects.Any(project => project.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();

        int projectAttempted = 0, projectCompleted = 0, projectFailed = 0;
        int sourceAttempted = 0, sourceCompleted = 0, prepAttempted = 0, prepCompleted = 0;
        int panelsPlanned = 0, panelAttempted = 0, panelCompleted = 0, panelFailed = 0;
        int rectangles = 0, affineCount = 0, noGeometry = 0, unsupported = 0, fallbacks = 0;
        int nativeAttempted = 0, nativeReturned = 0, nativeUnavailable = 0;
        long sourceBytes = 0, preparedBytes = 0, selectedBytes = 0, derivedBytes = 0, grayBytes = 0;
        int rectificationAttempted = 0, rectificationCompleted = 0, diagnosticAttempted = 0, diagnosticCompleted = 0;
        bool digestVerified = false; string? selectorHash = null, failure = null; RefusalWire? wire = null;
        var errors = new Dictionary<string, int>(StringComparer.Ordinal);
        var geometryErrors = new Dictionary<string, int>(StringComparer.Ordinal);
        var times = new Dictionary<string, double>(StringComparer.Ordinal);
        RefusalTarget? retained = null;
        long allocations = GC.GetTotalAllocatedBytes(); var total = Stopwatch.StartNew();
        void Error(string name) => errors[name] = errors.GetValueOrDefault(name) + 1;
        void AddTime(string name, Stopwatch clock) => times[name] = times.GetValueOrDefault(name) + clock.Elapsed.TotalMilliseconds;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int index = 0; index < TraceSampleCount && !token.IsCancellationRequested; index++)
        {
            projectAttempted++; string stage = "source_read"; bool panelError = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var clock = Stopwatch.StartNew();
            try
            {
                sourceAttempted++;
                ImageOnlyPayload payload = ReadLockedImageOnly(selector, projects[index], digest, reader, deadline.Token);
                sourceCompleted++; sourceBytes += payload.Image.Length; AddTime(stage, clock); clock.Restart();
                stage = "preparation"; prepAttempted++;
                string imageHash = Program.Hash(payload.Image); IReadOnlyList<object>? panels = null;
                try { panels = await PrepareAsync(panelizer, payload.Image, imageHash, payload.Width, payload.Height, deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsGenuineNoPanel(error)) { fallbacks++; }
                if (panels is { Count: 0 }) throw new InvalidDataException();
                prepCompleted++; AddTime(stage, clock); clock.Restart(); stage = "geometry";
                int count = panels?.Count ?? 1; panelsPlanned += count;
                for (int panelIndex = 0; panelIndex < count; panelIndex++)
                {
                    if (deadline.IsCancellationRequested)
                    {
                        panelError = true;
                        Error(token.IsCancellationRequested ? "geometry:CANCELLED" : "geometry:PROJECT_TIMEOUT");
                        break;
                    }
                    panelAttempted++; clock.Restart();
                    TraceAxis? secondary = null;
                    try
                    {
                        var panel = panels is null
                            ? (payload.Image, payload.Width, payload.Height, new PdfRectD(0, 0, payload.Width, payload.Height))
                            : ReadPreparedPanel(panels[panelIndex], payload.Width, payload.Height);
                        preparedBytes += panel.Item1.Length;
                        if (panel.Item2 is < 32 or > 4096 || panel.Item3 is < 32 or > 4096 || (long)panel.Item2 * panel.Item3 > 16_777_216)
                        { unsupported++; panelCompleted++; continue; }
                        var primary = new TraceRustAxis(geometryRust) { Width = panel.Item2, Height = panel.Item3 };
                        secondary = new TraceAxis(new OpenCvAxisProposalService(app));
                        RustAxisProposalResult proposal = await new RustFirstAxisProposalService(primary, secondary)
                            .SuggestAsync(panel.Item1, deadline.Token).ConfigureAwait(false);
                        if (NativeAffineTraceAccepted(proposal, panel.Item2, panel.Item3))
                        {
                            affineCount++;
                            if (retained is null) retained = new(index, panel.Item1, panel.Item2, panel.Item3, panel.Item4,
                                imageHash, proposal.AffineGeometry!);
                        }
                        else if (proposal.Geometry is not null && proposal.ErrorCode is null) rectangles++;
                        else
                        {
                            noGeometry++;
                            string code = proposal.ErrorCode is "NO_AXIS" or "NO_INTERSECTION" or "AMBIGUOUS_AXIS" or "OPENCV_AXIS_AMBIGUOUS" or "WORKER_TIMEOUT"
                                ? proposal.ErrorCode : "OTHER_FAILURE";
                            geometryErrors[code] = geometryErrors.GetValueOrDefault(code) + 1;
                        }
                        panelCompleted++;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        panelFailed++; panelError = true;
                        Error(error is OperationCanceledException ? token.IsCancellationRequested ? "CANCELLED" : "PROJECT_TIMEOUT" : TraceExceptionCategory(error));
                    }
                    finally
                    {
                        if (secondary is { Calls: > 0 })
                        {
                            nativeAttempted += secondary.Calls;
                            if (secondary.Last is { ErrorCode: null } last && (last.Geometry is not null || last.AffineGeometry is not null)) nativeReturned++;
                            else nativeUnavailable++;
                        }
                        AddTime(stage, clock);
                    }
                }
                if (panelError) projectFailed++; else projectCompleted++;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                AddTime(stage, clock); projectFailed++;
                Error(stage + ":" + (error is OperationCanceledException ? token.IsCancellationRequested ? "CANCELLED" : "PROJECT_TIMEOUT" : TraceExceptionCategory(error)));
            }
        }

        try
        {
            digestVerified = sourceCompleted == TraceSampleCount &&
                Convert.ToHexStringLower(digest.GetHashAndReset()) == CachedAxisSourceDigest;
            if (!digestVerified || projectCompleted != TraceSampleCount || panelCompleted != panelsPlanned || panelFailed != 0)
            { failure = token.IsCancellationRequested ? "CANCELLED" : "INCOMPLETE_OR_CHANGED_PREFIX"; }
            else if (RefusalSelectionFailure(affineCount, retained) is { } selectionFailure) { failure = selectionFailure; }
            else
            {
                if (retained is null) throw new InvalidDataException();
                token.ThrowIfCancellationRequested(); selectedBytes = retained.Image.Length;
                byte[] selectorBytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = "graphreader.affine-tick-refusal-selector.v1", selection = "unique_affine_in_first_12_sorted_anonymized_case_id",
                    assignment_sha256 = CachedAxisAssignmentHash, selected_inventory_sha256 = CachedAxisInventoryHash,
                    source_digest = CachedAxisSourceDigest, project_index = retained.Index, width = retained.Width, height = retained.Height,
                    crop = retained.Crop, source_image_sha256 = retained.SourceImageHash, panel_image_sha256 = Program.Hash(retained.Image),
                    original_panel_affine = retained.Frame,
                    rectifier_assembly_sha256 = AffineTickRefusalLibraries["GraphReader.Imaging.dll"],
                    baseline_geometry_rust_sha256 = AffineTickRefusalGeometryRustHash,
                    diagnostic_rust_sha256 = AffineTickRefusalDiagnosticRustHash, local_only_active_diagnosis_input = true,
                }, Program.Json);
                if (selectorBytes.Length > 16_384) throw new InvalidDataException();
                using (var file = new FileStream(selectorOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(selectorBytes); file.Flush(flushToDisk: true); }
                selectorHash = Program.Hash(selectorBytes);
                rectificationAttempted++; var clock = Stopwatch.StartNew();
                var (frame, image, _) = CalibrationWorkingFrame.Create(retained.Image, retained.Width, retained.Height,
                    retained.Frame, Program.Hash(retained.Image), token);
                rectificationCompleted++; derivedBytes = image.Length; AddTime("rectification", clock);
                byte[] gray = DecodeRefusalGray(image, frame.Width, frame.Height, token); grayBytes = gray.Length;
                clock.Restart(); diagnosticAttempted++;
                wire = await RunRefusalWireAsync(diagnosticRust, gray, frame.Width, frame.Height, frame.Geometry, token).ConfigureAwait(false);
                diagnosticCompleted++; AddTime("diagnostic", clock);
                if (wire.Stage == "UNATTEMPTED") { failure = wire.Result; Error("diagnostic:" + failure); }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is OperationCanceledException ? token.IsCancellationRequested ? "CANCELLED" : "WORKER_TIMEOUT" : TraceExceptionCategory(error);
            Error(failure);
        }
        total.Stop();
        object report = new
        {
            schema = AffineTickRefusalSchema, scope = "development_only_new_information_refusal_diagnosis",
            status = failure is null ? "completed" : "refused_or_failed", failure,
            selection = "unique_affine_in_first_12_sorted_anonymized_case_id", sample_denominator = TraceSampleCount, frozen_dev_denominator = Denominator,
            assignment_sha256 = CachedAxisAssignmentHash, selected_inventory_sha256 = CachedAxisInventoryHash,
            source_digest_verified = digestVerified, source_digest = digestVerified ? CachedAxisSourceDigest : null,
            projects_attempted = projectAttempted, projects_completed = projectCompleted, projects_failed = projectFailed, projects_unattempted = TraceSampleCount - projectAttempted,
            source_attempted = sourceAttempted, source_completed = sourceCompleted, source_failed = sourceAttempted - sourceCompleted, source_unattempted = TraceSampleCount - sourceAttempted,
            preparation_attempted = prepAttempted, preparation_completed = prepCompleted, preparation_failed = prepAttempted - prepCompleted, preparation_unattempted = TraceSampleCount - prepAttempted,
            panels_planned = panelsPlanned, panels_attempted = panelAttempted, panels_completed = panelCompleted, panels_failed = panelFailed,
            panels_unattempted = panelsPlanned - panelAttempted, panel_denominator_complete = prepCompleted == TraceSampleCount,
            rectangular_geometry = rectangles, affine_geometry = affineCount, no_geometry = noGeometry, unsupported_dimensions = unsupported, genuine_whole_image_fallbacks = fallbacks,
            native_attempted = nativeAttempted, native_returned = nativeReturned, native_unavailable = nativeUnavailable,
            rectification_attempted = rectificationAttempted, rectification_completed = rectificationCompleted, rectification_failed = rectificationAttempted - rectificationCompleted,
            rectification_unattempted = 1 - rectificationAttempted, diagnostic_attempted = diagnosticAttempted, diagnostic_completed = diagnosticCompleted,
            diagnostic_failed = diagnosticAttempted - diagnosticCompleted, diagnostic_unattempted = 1 - diagnosticAttempted,
            geometry_reason_counts = geometryErrors, failure_counts = errors, diagnostic = wire, selector_sha256 = selectorHash,
            prefix_is_newly_executed_not_cached = true, accuracy_comparison = false, geometry_source_parity_checkpoint = "4fcf0b69",
            parent_cpu_maximum_logical_cores = budget.MaximumLogicalCores, diagnostic_deadline_seconds = 5, project_deadline_seconds = 45, overall_deadline_seconds = 120,
            timings_milliseconds = times, wall_seconds = total.Elapsed.TotalSeconds,
            memory = new { source_encoded_bytes_read = sourceBytes, prepared_encoded_bytes = preparedBytes, selected_encoded_bytes = selectedBytes,
                derived_png_bytes = derivedBytes, derived_gray_bytes = grayBytes, parent_allocated_bytes = GC.GetTotalAllocatedBytes() - allocations },
            ocr_calls = 0, association_calls = 0, truth_scoring = false, sealed_payload_reads = 0, image_writes = false, apply_called = false, export_called = false,
            runtime = new { app_exe_sha256 = AffineTickRefusalExeHash, app_dll_sha256 = AffineTickRefusalDllHash,
                geometry_rust_sha256 = AffineTickRefusalGeometryRustHash, diagnostic_rust_sha256 = AffineTickRefusalDiagnosticRustHash },
        };
        return new(report, failure is null);
    }

    private static byte[] DecodeRefusalGray(byte[] image, int width, int height, CancellationToken token)
    {
        using var input = new MemoryStream(image, writable: false);
        BitmapFrame frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth != width || frame.PixelHeight != height || width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
            throw new InvalidDataException();
        var converted = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(frame, token), PixelFormats.Gray8, null, 0);
        byte[] gray = new byte[checked(width * height)]; converted.CopyPixels(gray, width, 0); token.ThrowIfCancellationRequested(); return gray;
    }

    private static async Task<RefusalWire> RunRefusalWireAsync(string worker, byte[] gray, int width, int height,
        RustAxisGeometry box, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new ProcessStartInfo(worker)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--measure-ticks"); process.StartInfo.ArgumentList.Add("--measure-ticks-diagnostics");
        if (!process.Start()) throw new InvalidDataException();
        using var kill = deadline.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        try
        {
            Task<byte[]> stdout = ReadRefusalOutputAsync(process.StandardOutput.BaseStream, 2048, deadline.Token);
            Task<byte[]> stderr = ReadRefusalOutputAsync(process.StandardError.BaseStream, 8192, deadline.Token);
            byte[] header = new byte[36]; "GRT1"u8.CopyTo(header);
            int[] fields = [width, height, 1, gray.Length, box.Left, box.Bottom, box.Right, box.Top];
            for (int index = 0; index < fields.Length; index++) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4 + index * 4, 4), checked((uint)fields[index]));
            await process.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(gray, deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if ((await stderr.ConfigureAwait(false)).Length != 0) throw new InvalidDataException();
            return ParseRefusalWire(await stdout.ConfigureAwait(false), process.ExitCode, box);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); _ = process.WaitForExit(1000); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<byte[]> ReadRefusalOutputAsync(Stream input, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream(); byte[] buffer = new byte[1024]; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { if (output.Length + read > maximum) throw new InvalidDataException(); output.Write(buffer, 0, read); }
        return output.ToArray();
    }

    private static RefusalWire ParseRefusalWire(byte[] bytes, int exit, RustAxisGeometry box)
    {
        if (bytes.Length is < 2 or > 2048 || bytes[^1] != 10 || bytes.Contains((byte)13) || bytes.Any(value => value > 127)) throw new InvalidDataException();
        string[] lines = Encoding.ASCII.GetString(bytes).Split('\n');
        if (lines.Length != 3 || lines[2].Length != 0) throw new InvalidDataException();
        string[] result = lines[0].Split(' '), trace = lines[1].Split(' ');
        if (result.Any(string.IsNullOrEmpty) || trace.Length != 5 || trace[0] != "TICKDIAG1" || trace.Any(string.IsNullOrEmpty)) throw new InvalidDataException();
        int? Field(int index, string name, int maximum)
        {
            if (!trace[index].StartsWith(name + "=", StringComparison.Ordinal)) throw new InvalidDataException();
            string value = trace[index][(name.Length + 1)..]; if (value == "-") return null;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed <= maximum ? parsed : throw new InvalidDataException();
        }
        int? seed = Field(2, "seed_gray_sum", 255), channels = Field(3, "channels", 3), work = Field(4, "bridge_work", 16_777_216);
        string stage = trace[1], code; int x = 0, y = 0;
        if (exit == 0 && result.Length == 7 && result[0] == "TICKS1")
        {
            int[] expected = [box.Left, box.Bottom, box.Right, box.Top];
            for (int index = 0; index < expected.Length; index++)
                if (!int.TryParse(result[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int actual) || actual != expected[index]) throw new InvalidDataException();
            x = RefusalTickCount(result[5], box.Left, box.Right); y = RefusalTickCount(result[6], box.Top, box.Bottom);
            if (stage != "TICKS_MEASURED" || seed is null or >= 200 || channels != 1 || work is null) throw new InvalidDataException();
            code = "TICKS_RETURNED";
        }
        else if (exit == 1 && result.Length == 2 && result[0] == "ERR")
        {
            code = result[1];
            if (code is "INVALID_INPUT" or "INVALID_PROTOCOL" or "CPU_BUDGET_UNAVAILABLE")
            { if (stage != "UNATTEMPTED" || seed is not null || channels is not null || work is not null) throw new InvalidDataException(); }
            else if (code == "AXIS_BOX_NOT_OBSERVED")
            {
                if (channels != 1 || seed is null) throw new InvalidDataException();
                if (stage == "ORIGIN_NOT_INK") { if (seed < 200 || work is not null) throw new InvalidDataException(); }
                else if (stage is "X_BRIDGE_BUDGET" or "Y_BRIDGE_BUDGET")
                { if (seed >= 200 || work != 16_777_216) throw new InvalidDataException(); }
                else if (stage is "X_SPAN_UNOBSERVED" or "Y_SPAN_UNOBSERVED")
                { if (seed >= 200 || work is null) throw new InvalidDataException(); }
                else throw new InvalidDataException();
            }
            else throw new InvalidDataException();
        }
        else throw new InvalidDataException();
        return new(code, stage, seed, channels, work, x, y, exit, bytes.Length, Program.Hash(bytes));
    }

    private static int RefusalTickCount(string text, int minimum, int maximum)
    {
        if (text == "-") return 0;
        string[] values = text.Split(','); if (values.Length is < 3 or > 128) throw new InvalidDataException();
        int previous = -1;
        foreach (string value in values)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < minimum || parsed > maximum || parsed <= previous) throw new InvalidDataException();
            previous = parsed;
        }
        return values.Length;
    }

    internal static object AffineTickRefusalSelfTest()
    {
        var box = new RustAxisGeometry(20, 100, 115, 10, 0);
        var good = ParseRefusalWire("TICKS1 20 100 115 10 40,70,100 20,50,80\nTICKDIAG1 TICKS_MEASURED seed_gray_sum=0 channels=1 bridge_work=0\n"u8.ToArray(), 0, box);
        if (good.XTicks != 3 || good.YTicks != 3) throw new InvalidDataException();
        var origin = ParseRefusalWire("ERR AXIS_BOX_NOT_OBSERVED\nTICKDIAG1 ORIGIN_NOT_INK seed_gray_sum=255 channels=1 bridge_work=-\n"u8.ToArray(), 1, box);
        if (origin.Stage != "ORIGIN_NOT_INK") throw new InvalidDataException();
        var malformed = ParseRefusalWire("ERR INVALID_PROTOCOL\nTICKDIAG1 UNATTEMPTED seed_gray_sum=- channels=- bridge_work=-\n"u8.ToArray(), 1, box);
        if (malformed.Stage != "UNATTEMPTED") throw new InvalidDataException();
        (byte[] Bytes, int Exit)[] invalidWires =
        [
            ("TICKS1 21 100 115 10 - -\nTICKDIAG1 TICKS_MEASURED seed_gray_sum=0 channels=1 bridge_work=0\n"u8.ToArray(), 0),
            ("ERR AXIS_BOX_NOT_OBSERVED\nTICKDIAG1 ORIGIN_NOT_INK seed_gray_sum=0 channels=1 bridge_work=-\n"u8.ToArray(), 1),
            ("ERR INVALID_PROTOCOL\nTICKDIAG1 UNATTEMPTED seed_gray_sum=- channels=- bridge_work=-\nextra\n"u8.ToArray(), 1),
        ];
        foreach (var bad in invalidWires)
        { try { _ = ParseRefusalWire(bad.Bytes, bad.Exit, box); throw new InvalidOperationException(); } catch (InvalidDataException) { } }
        var target = new RefusalTarget(0, [], 128, 128, new(0, 0, 128, 128), new string('a', 64), new(new(20, 100), new(115, 95), new(25, 10), 0));
        if (RefusalSelectionFailure(1, target) is not null || RefusalSelectionFailure(0, null) != "NO_UNIQUE_AFFINE_TARGET" ||
            RefusalSelectionFailure(2, target) != "MULTIPLE_AFFINE_TARGETS" || RefusalSelectionFailure(1, null) is null) throw new InvalidDataException();
        CheckNativeAffineGeometryControl();
        if (AffineTickRefusalLibraries.Count != 20 || AffineTickRefusalLibraries["GraphReader.App.dll"] != AffineTickRefusalDllHash) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-affine-tick-refusal-self-test.v1", selection_guards = true,
            strict_wire_guards = true, immutable_historical_modes = true, private_reads = 0, worker_calls = 0 };
    }
}
