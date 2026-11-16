// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private sealed record SpanSelection(int Index, int Width, int Height, PdfRectD Crop,
        string SourceImageHash, string PanelImageHash, RustAffineAxisGeometry Frame, int ExpectedSeed, int ExpectedWork);
    private sealed record SpanWindow(int From, int To, int Radius, int Left, int Right, int Top, int Bottom,
        bool LeftTruncated, bool RightTruncated, bool TopTruncated, bool BottomTruncated);
    private sealed record SpanReplay(bool Observed, string Kind, int Work, int GapLength, int MaxSpan, SpanWindow? Window);
    private readonly record struct SpanSearch(bool Connected, bool BudgetExhausted);
    private static readonly string[] SpanPropertyStages = ["source_read", "preparation", "rectification", "property_replay"];

    private static SpanSelection ParseSpanSelection(byte[] selected, byte[] previous,
        string selectorHash = AffineSpanSelectorHash, string reportHash = AffineSpanReportHash)
    {
        Program.RequireHash(selected, selectorHash); Program.RequireHash(previous, reportHash);
        using JsonDocument selector = JsonDocument.Parse(selected), report = JsonDocument.Parse(previous);
        JsonElement s = selector.RootElement, r = report.RootElement;
        if (s.GetProperty("schema").GetString() != "graphreader.affine-tick-refusal-selector.v1" ||
            s.GetProperty("selection").GetString() != "unique_affine_in_first_12_sorted_anonymized_case_id" ||
            s.GetProperty("assignment_sha256").GetString() != CachedAxisAssignmentHash ||
            s.GetProperty("selected_inventory_sha256").GetString() != CachedAxisInventoryHash ||
            s.GetProperty("source_digest").GetString() != CachedAxisSourceDigest ||
            s.GetProperty("rectifier_assembly_sha256").GetString() != AffineTickRefusalLibraries["GraphReader.Imaging.dll"] ||
            s.GetProperty("baseline_geometry_rust_sha256").GetString() != AffineTickRefusalGeometryRustHash ||
            s.GetProperty("diagnostic_rust_sha256").GetString() != AffineTickRefusalDiagnosticRustHash ||
            !s.GetProperty("local_only_active_diagnosis_input").GetBoolean() ||
            r.GetProperty("schema").GetString() != AffineTickRefusalSchema || r.GetProperty("status").GetString() != "completed" ||
            r.GetProperty("selector_sha256").GetString() != selectorHash || !r.GetProperty("source_digest_verified").GetBoolean() ||
            r.GetProperty("source_digest").GetString() != CachedAxisSourceDigest ||
            r.GetProperty("assignment_sha256").GetString() != CachedAxisAssignmentHash ||
            r.GetProperty("selected_inventory_sha256").GetString() != CachedAxisInventoryHash ||
            r.GetProperty("affine_geometry").GetInt32() != 1 || r.GetProperty("ocr_calls").GetInt32() != 0 ||
            r.GetProperty("association_calls").GetInt32() != 0 || r.GetProperty("sealed_payload_reads").GetInt32() != 0 ||
            r.GetProperty("truth_scoring").GetBoolean() || r.GetProperty("image_writes").GetBoolean()) throw new InvalidDataException();
        foreach (string stage in new[] { "projects", "source", "preparation", "panels" })
        {
            if (r.GetProperty(stage + "_completed").GetInt32() != TraceSampleCount ||
                r.GetProperty(stage + "_failed").GetInt32() != 0 || r.GetProperty(stage + "_unattempted").GetInt32() != 0)
                throw new InvalidDataException();
        }
        JsonElement diagnostic = r.GetProperty("diagnostic");
        if (diagnostic.GetProperty("result").GetString() != "AXIS_BOX_NOT_OBSERVED" ||
            diagnostic.GetProperty("stage").GetString() != "X_SPAN_UNOBSERVED" ||
            diagnostic.GetProperty("channels").GetInt32() != 1 || diagnostic.GetProperty("exit_code").GetInt32() != 1 ||
            diagnostic.GetProperty("bridge_work").GetInt32() != 427) throw new InvalidDataException();
        int index = s.GetProperty("project_index").GetInt32(), width = s.GetProperty("width").GetInt32(), height = s.GetProperty("height").GetInt32();
        JsonElement crop = s.GetProperty("crop");
        var rect = new PdfRectD(crop.GetProperty("x").GetDouble(), crop.GetProperty("y").GetDouble(),
            crop.GetProperty("width").GetDouble(), crop.GetProperty("height").GetDouble());
        var frame = s.GetProperty("original_panel_affine").Deserialize<RustAffineAxisGeometry>(Program.Json);
        string sourceHash = s.GetProperty("source_image_sha256").GetString() ?? string.Empty;
        string panelHash = s.GetProperty("panel_image_sha256").GetString() ?? string.Empty;
        if (index is < 0 or >= TraceSampleCount || width is < 32 or > 4096 || height is < 32 or > 4096 ||
            !rect.IsValid || rect.X < 0 || rect.Y < 0 || rect.Width != width || rect.Height != height ||
            sourceHash.Length != 64 || panelHash.Length != 64 || frame is null || !OpenCvAxisProposalService.ValidAffineGeometry(frame, width, height)) throw new InvalidDataException();
        int seed = diagnostic.GetProperty("seed_gray_sum").GetInt32();
        if (seed is < 0 or >= 200) throw new InvalidDataException();
        return new(index, width, height, rect, sourceHash, panelHash, frame, seed, 427);
    }

    internal static async Task<AffineTickRefusalOutcome> RunAffineSpanPropertiesAsync(string selected, string previous,
        string corpus, string app, string assets, string geometryRust, string diagnosticRust, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !new[] { selected, previous, corpus, app, assets, geometryRust, diagnosticRust }.All(Path.IsPathFullyQualified))
            throw new InvalidDataException();
        using Candidate pins = Candidate.VerifyAffineTickRefusal(app, assets, geometryRust, diagnosticRust, token);
        SpanSelection target = ParseSpanSelection(Program.ReadBounded(selected, 16_384), Program.ReadBounded(previous, 32_768));
        var reader = ResolveEmbeddedImageReader(); var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.AssignmentSha256 != CachedAxisAssignmentHash || metadata.SelectedInventorySha256 != CachedAxisInventoryHash) throw new InvalidDataException();
        FrozenRealCorpusProject[] projects = selector.EnumerateSelectedProjects(token).OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (projects.Length != Denominator || projects.Any(project => project.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        var stages = SpanPropertyStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        string stage = "source_read"; string? failure = null; object? properties = null;
        bool sourceMatches = false, panelMatches = false, replayMatches = false;
        int sourceBytes = 0, panelBytes = 0, derivedBytes = 0, grayBytes = 0;
        long allocations = GC.GetTotalAllocatedBytes(); var total = Stopwatch.StartNew(); var clock = Stopwatch.StartNew();
        void Begin(string next) { stage = next; stages[stage] = "attempted"; clock.Restart(); }
        void Complete() { stages[stage] = "completed"; timings[stage] = clock.Elapsed.TotalMilliseconds; }
        try
        {
            Begin("source_read");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ImageOnlyPayload image = ReadLockedImageOnly(selector, projects[target.Index], digest, reader, token);
            sourceBytes = image.Image.Length; sourceMatches = Program.Hash(image.Image) == target.SourceImageHash;
            if (!sourceMatches) throw new InvalidDataException();
            Complete(); Begin("preparation");
            IReadOnlyList<object>? panels = null;
            try { panels = await PrepareAsync(panelizer, image.Image, target.SourceImageHash, image.Width, image.Height, token).ConfigureAwait(false); }
            catch (Exception error) when (IsGenuineNoPanel(error)) { }
            var prepared = panels is null ? [(image.Image, image.Width, image.Height, new PdfRectD(0, 0, image.Width, image.Height))]
                : panels.Select(panel => ReadPreparedPanel(panel, image.Width, image.Height)).ToArray();
            var matches = prepared.Where(panel => panel.Item2 == target.Width && panel.Item3 == target.Height && panel.Item4 == target.Crop).ToArray();
            if (matches.Length != 1 || Program.Hash(matches[0].Item1) != target.PanelImageHash) throw new InvalidDataException();
            panelMatches = true; panelBytes = matches[0].Item1.Length;
            Complete(); Begin("rectification");
            var (frame, encoded, _) = CalibrationWorkingFrame.Create(matches[0].Item1, target.Width, target.Height, target.Frame, target.PanelImageHash, token);
            byte[] gray = DecodeRefusalGray(encoded, frame.Width, frame.Height, token);
            derivedBytes = encoded.Length; grayBytes = gray.Length;
            Complete(); Begin("property_replay");
            RustAxisGeometry box = frame.Geometry;
            SpanReplay replay = ReplayHorizontalSpan(gray, frame.Width, frame.Height, box, token);
            replayMatches = !replay.Observed && (replay.Kind is "TERMINAL_GAP" or "OVERLONG_GAP" or "DISCONNECTED") &&
                replay.Work == target.ExpectedWork && gray[box.Bottom * frame.Width + box.Left] == target.ExpectedSeed;
            if (!replayMatches) { failure = "REPLAY_MISMATCH"; stages[stage] = "failed"; timings[stage] = clock.Elapsed.TotalMilliseconds; }
            else
            {
                bool? connectsPerpendicular = null; int counterfactualWork = 0; string classification = replay.Kind;
                if (replay.Kind == "DISCONNECTED" && replay.Window is { } window)
                {
                    int work = replay.Work;
                    SpanSearch search = SearchSpanBridge(gray, frame.Width, box.Bottom,
                        MakeSpanWindow(window.From, window.To, window.Radius, frame.Width, frame.Height, box, relaxPerpendicular: true), ref work, token);
                    connectsPerpendicular = search.Connected;
                    counterfactualWork = work - replay.Work;
                    classification = search.BudgetExhausted ? "DIAGNOSTIC_SEARCH_BUDGET" :
                        connectsPerpendicular == true ? "PATH_ONLY_OUTSIDE_PERPENDICULAR_CLIP" : "DISCONNECTED_WITH_PERPENDICULAR_RELAXATION";
                }
                properties = new
                {
                    classification, first_failure = replay.Kind, replay_work = replay.Work,
                    first_gap_length = replay.GapLength, maximum_span = replay.MaxSpan, maximum_gap_length = replay.MaxSpan + 4,
                    bridge_radius = replay.Window?.Radius,
                    plot_clip_truncated = replay.Window is null ? null : new
                    { left = replay.Window.LeftTruncated, right = replay.Window.RightTruncated, top = replay.Window.TopTruncated, bottom = replay.Window.BottomTruncated },
                    diagnostic_perpendicular_searches = connectsPerpendicular is null ? 0 : 1,
                    same_radius_perpendicular_connects = connectsPerpendicular, diagnostic_extra_work = counterfactualWork,
                    along_bounds_preserved = true,
                    counterfactual_never_used_for_acceptance = true,
                };
                Complete();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            stages[stage] = "failed"; timings[stage] = clock.Elapsed.TotalMilliseconds;
            failure = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : TraceExceptionCategory(error);
        }
        total.Stop();
        object report = new
        {
            schema = AffineSpanPropertiesSchema, status = failure is null ? "completed" : "refused_or_failed", failure,
            local_selector_sha256 = AffineSpanSelectorHash, prior_report_sha256 = AffineSpanReportHash,
            assignment_sha256 = CachedAxisAssignmentHash, selected_inventory_sha256 = CachedAxisInventoryHash,
            prior_first12_digest_verified_from_pinned_report = true, entire_first12_source_digest_recomputed = false,
            current_selected_source_matches = sourceMatches, current_prepared_panel_matches = panelMatches, replay_matches_prior_typed_result_and_work = replayMatches,
            stages, stages_completed = stages.Values.Count(value => value == "completed"), stages_failed = stages.Values.Count(value => value == "failed"),
            stages_unattempted = stages.Values.Count(value => value == "unattempted"), properties,
            timings_milliseconds = timings, wall_seconds = total.Elapsed.TotalSeconds,
            memory = new { source_encoded_bytes = sourceBytes, panel_encoded_bytes = panelBytes, derived_png_bytes = derivedBytes,
                derived_gray_bytes = grayBytes, cumulative_parent_allocated_bytes = GC.GetTotalAllocatedBytes() - allocations },
            parent_maximum_logical_cores = budget.MaximumLogicalCores, overall_deadline_seconds = 30,
            source_payloads_requested = 1, axis_worker_calls = 0, ocr_calls = 0, fit_calls = 0, truth_scoring = false,
            reserved_or_sealed_payload_reads = 0, image_writes = false, apply_called = false, export_called = false,
        };
        return new(report, failure is null);
    }

    // Exact Rust observed_axis_span_clipped arithmetic/order. Coordinates stay private.
    private static SpanReplay ReplayHorizontalSpan(byte[] gray, int width, int height, RustAxisGeometry box, CancellationToken token)
    {
        if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height != gray.Length ||
            (long)width * height > 16_777_216 || box.Left < 0 || box.Top < 0 || box.Right >= width || box.Bottom >= height ||
            box.Left >= box.Right || box.Top >= box.Bottom) throw new InvalidDataException();
        int maxSpan = Math.Max(24, (int)Math.Ceiling(Math.Min(Math.Min(width, height) * .08, 80.0)));
        int work = 0, index = box.Left;
        while (index <= box.Right)
        {
            token.ThrowIfCancellationRequested();
            if (gray[box.Bottom * width + index] < 200) { index++; continue; }
            int gapStart = index;
            while (index <= box.Right && gray[box.Bottom * width + index] >= 200) { token.ThrowIfCancellationRequested(); index++; }
            int length = index - gapStart;
            if (gapStart == box.Left || index > box.Right) return new(false, "TERMINAL_GAP", work, length, maxSpan, null);
            if (length > maxSpan + 4) return new(false, "OVERLONG_GAP", work, length, maxSpan, null);
            int from = gapStart - 1, to = index, radius = Math.Min(maxSpan, Math.Max(24, (to - from + 1) / 2 + 2));
            SpanWindow window = MakeSpanWindow(from, to, radius, width, height, box, relaxPerpendicular: false);
            SpanSearch search = SearchSpanBridge(gray, width, box.Bottom, window, ref work, token);
            if (!search.Connected)
                return new(false, search.BudgetExhausted ? "BRIDGE_BUDGET" : "DISCONNECTED", work, length, maxSpan, window);
        }
        return new(true, "CONTINUOUS_OR_BRIDGED", work, 0, maxSpan, null);
    }

    private static SpanWindow MakeSpanWindow(int from, int to, int radius, int width, int height, RustAxisGeometry box, bool relaxPerpendicular)
    {
        int rawLeft = Math.Max(0, from - radius), rawRight = Math.Min(width - 1, to + radius);
        int rawTop = Math.Max(0, box.Bottom - radius), rawBottom = Math.Min(height - 1, box.Bottom + radius);
        // Both modes preserve the production along-axis limits and the same radius.
        return new(from, to, radius, Math.Max(rawLeft, box.Left), Math.Min(rawRight, box.Right),
            relaxPerpendicular ? rawTop : Math.Max(rawTop, box.Top), relaxPerpendicular ? rawBottom : Math.Min(rawBottom, box.Bottom),
            rawLeft < box.Left, rawRight > box.Right, rawTop < box.Top, rawBottom > box.Bottom);
    }

    private static SpanSearch SearchSpanBridge(byte[] gray, int width, int baseline, SpanWindow window, ref int work, CancellationToken token)
    {
        int acrossLength = window.Bottom - window.Top + 1;
        bool[] visited = new bool[checked((window.Right - window.Left + 1) * acrossLength)];
        int Index(int along, int across) => (along - window.Left) * acrossLength + across - window.Top;
        int first = Index(window.From, baseline), target = Index(window.To, baseline);
        visited[first] = true; var queue = new Queue<int>(); queue.Enqueue(first);
        while (queue.TryDequeue(out int current))
        {
            token.ThrowIfCancellationRequested();
            if (work >= 16_777_216) return new(false, true);
            work++;
            if (current == target) break;
            int along = window.Left + current / acrossLength, across = window.Top + current % acrossLength;
            for (int da = -1; da <= 1; da++) for (int db = -1; db <= 1; db++)
            {
                int a = along + da, b = across + db;
                if (a < window.Left || a > window.Right || b < window.Top || b > window.Bottom) continue;
                int next = Index(a, b);
                if (!visited[next] && gray[b * width + a] < 200) { visited[next] = true; queue.Enqueue(next); }
            }
        }
        return new(visited[target], false);
    }

    internal static object AffineSpanPropertiesSelfTest()
    {
        var box = new RustAxisGeometry(20, 100, 115, 10, 0);
        byte[] Continuous()
        {
            byte[] pixels = Enumerable.Repeat((byte)255, 128 * 128).ToArray();
            for (int x = box.Left; x <= box.Right; x++) pixels[box.Bottom * 128 + x] = 0;
            return pixels;
        }
        SpanReplay Check(byte[] pixels, string expected)
        {
            string hash = Program.Hash(pixels); SpanReplay result = ReplayHorizontalSpan(pixels, 128, 128, box, CancellationToken.None);
            if (result.Kind != expected || Program.Hash(pixels) != hash) throw new InvalidDataException(); return result;
        }
        Check(Continuous(), "CONTINUOUS_OR_BRIDGED");
        byte[] Bridge(int row)
        {
            byte[] pixels = Continuous(); for (int x = 50; x <= 58; x++) pixels[100 * 128 + x] = 255;
            for (int x = 49; x <= 59; x++) pixels[row * 128 + x] = 0;
            return pixels;
        }
        Check(Bridge(99), "CONTINUOUS_OR_BRIDGED");
        byte[] outside = Bridge(101); SpanReplay clipped = Check(outside, "DISCONNECTED");
        if (clipped.Window is not { BottomTruncated: true } window || clipped.Observed) throw new InvalidDataException();
        int work = clipped.Work;
        if (!SearchSpanBridge(outside, 128, box.Bottom, MakeSpanWindow(window.From, window.To, window.Radius, 128, 128, box, true), ref work, CancellationToken.None).Connected)
            throw new InvalidDataException();
        byte[] disconnected = Continuous(); for (int x = 50; x <= 58; x++) disconnected[100 * 128 + x] = 255;
        SpanReplay absent = Check(disconnected, "DISCONNECTED"); work = absent.Work;
        SpanWindow absentWindow = absent.Window!;
        if (SearchSpanBridge(disconnected, 128, box.Bottom, MakeSpanWindow(absentWindow.From, absentWindow.To, absentWindow.Radius, 128, 128, box, true), ref work, CancellationToken.None).Connected)
            throw new InvalidDataException();
        byte[] alongExcursion = Continuous();
        for (int x = 26; x <= 28; x++) alongExcursion[100 * 128 + x] = 255;
        // The only connecting route first leaves the supplied left endpoint.
        alongExcursion[101 * 128 + 19] = 0; alongExcursion[101 * 128 + 29] = 0;
        for (int x = 19; x <= 29; x++) alongExcursion[102 * 128 + x] = 0;
        SpanReplay along = Check(alongExcursion, "DISCONNECTED");
        if (along.Window is not { LeftTruncated: true } alongWindow) throw new InvalidDataException();
        SpanWindow perpendicular = MakeSpanWindow(alongWindow.From, alongWindow.To, alongWindow.Radius, 128, 128, box, true);
        work = along.Work;
        if (perpendicular.Left != alongWindow.Left || perpendicular.Right != alongWindow.Right ||
            SearchSpanBridge(alongExcursion, 128, box.Bottom, perpendicular, ref work, CancellationToken.None).Connected) throw new InvalidDataException();
        byte[] terminal = Continuous(); terminal[100 * 128 + box.Right] = 255; Check(terminal, "TERMINAL_GAP");
        byte[] longGap = Continuous(); for (int x = 50; x <= 90; x++) longGap[100 * 128 + x] = 255; Check(longGap, "OVERLONG_GAP");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { _ = ReplayHorizontalSpan(Continuous(), 128, 128, box, cancel.Token); throw new InvalidOperationException(); }
        catch (OperationCanceledException error) when (error.CancellationToken == cancel.Token) { }
        try { _ = ParseSpanSelection("tampered"u8.ToArray(), "tampered"u8.ToArray()); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        return new { schema = "graphreader.real-dev-affine-span-properties-self-test.v1", owned_controls = 7,
            inside_bridge_accepted = true, below_baseline_production_refusal_preserved = true, same_radius_perpendicular_search_connects = true,
            along_endpoint_excursion_remains_refused = true,
            disconnected_terminal_overlong_refused = true, cancellation_checked = true, input_unchanged = true,
            metadata_tamper_refused = true, source_payload_reads = 0, worker_calls = 0 };
    }
}
