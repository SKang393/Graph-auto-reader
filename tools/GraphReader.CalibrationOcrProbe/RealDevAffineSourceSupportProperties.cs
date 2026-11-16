// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Imaging;
using GraphReader.Pdf;
using GraphReader.RealAcceptance.Ocr;
using ImagePoint = GraphReader.Imaging.PixelPoint;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private sealed record EndpointContributors(int[] Ink, int OutsideWindow, int WhiteSamples, int OutsideOriginalSamples);
    private sealed record OriginalSupportProof(string Conclusion, bool Uncertain, int LeftInkContributors, int RightInkContributors,
        int LeftOutsideWindow, int RightOutsideWindow, int LeftWhiteSamples, int RightWhiteSamples,
        int PositiveWeightOutsideOriginalSamples, bool SearchAttempted, bool SearchComplete, bool? AnySupportConnection,
        int ReachableRightContributors, int Work, int ProjectionChecks, bool LeftBoundary, bool RightBoundary,
        bool TopBoundary, bool BottomBoundary, bool OriginalRasterBoundary);
    private static readonly string[] SourceSupportStages = ["source_read", "preparation", "rectification", "gap_replay", "original_support"];

    private static JsonElement ReadSourceSupportProperty(byte[] bytes)
    {
        Program.RequireHash(bytes, AffineSourcePropertyHash);
        using JsonDocument document = JsonDocument.Parse(bytes); JsonElement root = document.RootElement;
        if (root.GetProperty("schema").GetString() != AffineSpanPropertiesSchema || root.GetProperty("status").GetString() != "completed" ||
            root.GetProperty("local_selector_sha256").GetString() != AffineSpanSelectorHash || root.GetProperty("prior_report_sha256").GetString() != AffineSpanReportHash ||
            !root.GetProperty("current_selected_source_matches").GetBoolean() || !root.GetProperty("current_prepared_panel_matches").GetBoolean() ||
            !root.GetProperty("replay_matches_prior_typed_result_and_work").GetBoolean() || root.GetProperty("stages_completed").GetInt32() != 4 ||
            root.GetProperty("stages_failed").GetInt32() != 0 || root.GetProperty("stages_unattempted").GetInt32() != 0)
            throw new InvalidDataException();
        JsonElement properties = root.GetProperty("properties");
        if (properties.GetProperty("classification").GetString() != "DISCONNECTED_WITH_PERPENDICULAR_RELAXATION" ||
            properties.GetProperty("first_failure").GetString() != "DISCONNECTED" || properties.GetProperty("replay_work").GetInt32() != 427 ||
            properties.GetProperty("first_gap_length").GetInt32() != 3 || properties.GetProperty("maximum_span").GetInt32() != 32 ||
            properties.GetProperty("bridge_radius").GetInt32() != 24 || !properties.GetProperty("along_bounds_preserved").GetBoolean() ||
            properties.GetProperty("diagnostic_perpendicular_searches").GetInt32() != 1 || properties.GetProperty("same_radius_perpendicular_connects").GetBoolean())
            throw new InvalidDataException();
        return properties.Clone();
    }

    internal static async Task<AffineTickRefusalOutcome> RunAffineSourceSupportAsync(string selected, string refusal, string property,
        string corpus, string app, string assets, string geometryRust, string diagnosticRust, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !new[] { selected, refusal, property, corpus, app, assets, geometryRust, diagnosticRust }.All(Path.IsPathFullyQualified))
            throw new InvalidDataException();
        using Candidate pins = Candidate.VerifyAffineTickRefusal(app, assets, geometryRust, diagnosticRust, token);
        SpanSelection target = ParseSpanSelection(Program.ReadBounded(selected, 16_384), Program.ReadBounded(refusal, 32_768));
        JsonElement previous = ReadSourceSupportProperty(Program.ReadBounded(property, 32_768));
        var reader = ResolveEmbeddedImageReader(); var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token); FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.AssignmentSha256 != CachedAxisAssignmentHash || metadata.SelectedInventorySha256 != CachedAxisInventoryHash) throw new InvalidDataException();
        FrozenRealCorpusProject[] projects = selector.EnumerateSelectedProjects(token).OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (projects.Length != Denominator || projects.Any(project => project.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        var stages = SourceSupportStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var times = new Dictionary<string, double>(StringComparer.Ordinal);
        string stage = "source_read"; string? failure = null; OriginalSupportProof? proof = null;
        bool sourceMatches = false, panelMatches = false, replayMatches = false;
        int sourceBytes = 0, panelBytes = 0, originalGrayBytes = 0, derivedGrayBytes = 0;
        var total = Stopwatch.StartNew(); var clock = Stopwatch.StartNew(); long allocations = GC.GetTotalAllocatedBytes();
        void Begin(string next) { stage = next; stages[stage] = "attempted"; clock.Restart(); }
        void Complete() { stages[stage] = "completed"; times[stage] = clock.Elapsed.TotalMilliseconds; }
        try
        {
            Begin("source_read"); using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ImageOnlyPayload image = ReadLockedImageOnly(selector, projects[target.Index], digest, reader, token);
            sourceBytes = image.Image.Length; sourceMatches = Program.Hash(image.Image) == target.SourceImageHash;
            if (!sourceMatches) throw new InvalidDataException();
            Complete(); Begin("preparation"); IReadOnlyList<object>? panels = null;
            try { panels = await PrepareAsync(panelizer, image.Image, target.SourceImageHash, image.Width, image.Height, token).ConfigureAwait(false); }
            catch (Exception error) when (IsGenuineNoPanel(error)) { }
            var prepared = panels is null ? [(image.Image, image.Width, image.Height, new PdfRectD(0, 0, image.Width, image.Height))] :
                panels.Select(panel => ReadPreparedPanel(panel, image.Width, image.Height)).ToArray();
            var matches = prepared.Where(panel => panel.Item2 == target.Width && panel.Item3 == target.Height && panel.Item4 == target.Crop).ToArray();
            if (matches.Length != 1 || Program.Hash(matches[0].Item1) != target.PanelImageHash) throw new InvalidDataException();
            panelMatches = true; panelBytes = matches[0].Item1.Length;
            Complete(); Begin("rectification");
            byte[] original = DecodeRefusalGray(matches[0].Item1, target.Width, target.Height, token); string originalHash = Program.Hash(original);
            originalGrayBytes = original.Length;
            var (frame, encoded, _) = CalibrationWorkingFrame.Create(matches[0].Item1, target.Width, target.Height, target.Frame, target.PanelImageHash, token);
            byte[] derived = DecodeRefusalGray(encoded, frame.Width, frame.Height, token); derivedGrayBytes = derived.Length;
            Complete(); Begin("gap_replay");
            SpanReplay replay = ReplayHorizontalSpan(derived, frame.Width, frame.Height, frame.Geometry, token);
            JsonElement sides = previous.GetProperty("plot_clip_truncated");
            replayMatches = !replay.Observed && replay.Kind == "DISCONNECTED" && replay.Work == target.ExpectedWork && replay.GapLength == previous.GetProperty("first_gap_length").GetInt32() &&
                replay.MaxSpan == previous.GetProperty("maximum_span").GetInt32() && replay.Window is { } w && w.Radius == previous.GetProperty("bridge_radius").GetInt32() &&
                w.LeftTruncated == sides.GetProperty("left").GetBoolean() && w.RightTruncated == sides.GetProperty("right").GetBoolean() &&
                w.TopTruncated == sides.GetProperty("top").GetBoolean() && w.BottomTruncated == sides.GetProperty("bottom").GetBoolean() &&
                derived[frame.Geometry.Bottom * frame.Width + frame.Geometry.Left] == target.ExpectedSeed;
            if (!replayMatches || replay.Window is not { } clipped) { failure = "REPLAY_MISMATCH"; throw new InvalidDataException(); }
            SpanWindow allowed = MakeSpanWindow(clipped.From, clipped.To, clipped.Radius, frame.Width, frame.Height, frame.Geometry, relaxPerpendicular: true);
            Complete(); Begin("original_support");
            proof = ObserveOriginalSupport(original, target.Width, target.Height, frame.TransformChain, allowed, frame.Geometry.Bottom, token);
            if (Program.Hash(original) != originalHash) throw new InvalidDataException();
            Complete();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            stages[stage] = "failed"; times[stage] = clock.Elapsed.TotalMilliseconds;
            failure ??= error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : TraceExceptionCategory(error);
        }
        total.Stop();
        object report = new
        {
            schema = AffineSourceSupportSchema, status = failure is null ? "completed" : "refused_or_failed", failure,
            selector_sha256 = AffineSpanSelectorHash, refusal_report_sha256 = AffineSpanReportHash, property_report_sha256 = AffineSourcePropertyHash,
            current_source_matches = sourceMatches, current_panel_matches = panelMatches, gap_replay_matches = replayMatches,
            stages, stages_completed = stages.Values.Count(value => value == "completed"), stages_failed = stages.Values.Count(value => value == "failed"),
            stages_unattempted = stages.Values.Count(value => value == "unattempted"), original_support = proof,
            support_connection_definition = "Any 8-connected source-ink path between at least one positive-weight contributor at each endpoint; all contributors enumerated",
            boundary_definition = "Reachable source ink has an 8-neighbor center outside the allowed window; outside-window ink is not read or searched",
            coordinate_scope = "Original prepared-panel pixel centers; crop translation is not reapplied",
            cached_derived_window_disconnected = true, derived_perpendicular_search_reexecuted = false, along_bounds_preserved = true,
            prior_first12_digest_trusted_from_pinned_report = true, first12_digest_recomputed = false,
            timings_milliseconds = times, wall_seconds = total.Elapsed.TotalSeconds,
            memory = new { source_encoded_bytes = sourceBytes, panel_encoded_bytes = panelBytes, original_gray_bytes = originalGrayBytes,
                derived_gray_bytes = derivedGrayBytes, cumulative_parent_allocated_bytes = GC.GetTotalAllocatedBytes() - allocations },
            parent_maximum_logical_cores = budget.MaximumLogicalCores, overall_deadline_seconds = 30,
            source_payloads_requested = 1, axis_worker_calls = 0, ocr_calls = 0, fit_calls = 0, truth_scoring = false,
            reserved_or_sealed_payload_reads = 0, image_writes = false, apply_called = false, export_called = false,
        };
        return new(report, failure is null);
    }

    private static bool SupportInside(ImagePoint point, SpanWindow window) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= window.Left && point.X <= window.Right && point.Y >= window.Top && point.Y <= window.Bottom;

    private static EndpointContributors CollectEndpointSupport(byte[] pixels, int width, int height, TransformChain chain,
        ImagePoint endpoint, SpanWindow window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ImagePoint point = chain.ToOriginal(endpoint);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X <= -1 || point.Y <= -1 || point.X >= width || point.Y >= height)
            return new([], 0, 0, 0); // The frozen sampler returns constant white before inspecting a lattice here.
        int x0 = (int)Math.Floor(point.X), y0 = (int)Math.Floor(point.Y); double fx = point.X - x0, fy = point.Y - y0;
        var ink = new List<int>(4); int outside = 0, white = 0, outsideOriginal = 0;
        for (int dy = 0; dy <= 1; dy++) for (int dx = 0; dx <= 1; dx++)
        {
            double weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy); if (weight <= 0) continue;
            int x = x0 + dx, y = y0 + dy;
            if (x < 0 || y < 0 || x >= width || y >= height) { outsideOriginal++; continue; }
            if (pixels[y * width + x] >= 200) { white++; continue; }
            ink.Add(y * width + x);
            if (!SupportInside(chain.ToDerived(new(x, y)), window)) outside++;
        }
        return new(ink.ToArray(), outside, white, outsideOriginal);
    }

    private static OriginalSupportProof ObserveOriginalSupport(byte[] pixels, int width, int height, TransformChain chain,
        SpanWindow window, int baseline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height != pixels.Length || pixels.Length > 16_777_216)
            throw new InvalidDataException();
        EndpointContributors left = CollectEndpointSupport(pixels, width, height, chain, new(window.From, baseline), window, token);
        EndpointContributors right = CollectEndpointSupport(pixels, width, height, chain, new(window.To, baseline), window, token);
        OriginalSupportProof Early(string conclusion) => new(conclusion, true, left.Ink.Length, right.Ink.Length,
            left.OutsideWindow, right.OutsideWindow, left.WhiteSamples, right.WhiteSamples, left.OutsideOriginalSamples + right.OutsideOriginalSamples,
            false, false, null, 0, 0, 0, false, false, false, false, false);
        if (left.Ink.Length == 0 || right.Ink.Length == 0) return Early("UNCERTAIN_MISSING_ENDPOINT_SUPPORT");
        if (left.OutsideWindow != 0 || right.OutsideWindow != 0) return Early("UNCERTAIN_ENDPOINT_SUPPORT_OUTSIDE_WINDOW");
        bool[] visited = new bool[pixels.Length]; var queue = new Queue<int>();
        foreach (int index in left.Ink) { if (!visited[index]) { visited[index] = true; queue.Enqueue(index); } }
        int work = 0, checks = 0; bool leftBoundary = false, rightBoundary = false, topBoundary = false, bottomBoundary = false, rasterBoundary = false;
        while (queue.TryDequeue(out int current))
        {
            token.ThrowIfCancellationRequested(); work++;
            int x = current % width, y = current / width;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) { rasterBoundary = true; continue; }
                int next = ny * width + nx; if (visited[next]) continue;
                ImagePoint mapped = chain.ToDerived(new(nx, ny)); checks++;
                if (!SupportInside(mapped, window))
                {
                    leftBoundary |= mapped.X < window.Left; rightBoundary |= mapped.X > window.Right;
                    topBoundary |= mapped.Y < window.Top; bottomBoundary |= mapped.Y > window.Bottom;
                    continue;
                }
                if (pixels[next] < 200) { visited[next] = true; queue.Enqueue(next); }
            }
        }
        int reached = right.Ink.Count(index => visited[index]); bool connected = reached > 0;
        bool boundary = leftBoundary || rightBoundary || topBoundary || bottomBoundary || rasterBoundary;
        return new(connected ? "SOURCE_SUPPORT_CONNECTED_IN_WINDOW" : "NO_SOURCE_CONNECTION_IN_WINDOW", !connected && boundary,
            left.Ink.Length, right.Ink.Length, left.OutsideWindow, right.OutsideWindow, left.WhiteSamples, right.WhiteSamples,
            left.OutsideOriginalSamples + right.OutsideOriginalSamples, true, true, connected, reached, work, checks,
            leftBoundary, rightBoundary, topBoundary, bottomBoundary, rasterBoundary);
    }

    internal static object AffineSourceSupportSelfTest()
    {
        static TransformChain Chain(Matrix3x3 matrix) => new([ImageTransform.Create(ImageTransformKind.Affine,
            ImageCoordinateSpace.OriginalPixels, ImageCoordinateSpace.DeskewedPixels, matrix)]);
        var identity = Chain(new(1, 0, 0, 0, 1, 0, 0, 0, 1));
        var window = new SpanWindow(3, 7, 2, 2, 8, 2, 8, false, false, false, false);
        byte[] Line() { byte[] image = Enumerable.Repeat((byte)255, 12 * 12).ToArray(); for (int x = 3; x <= 7; x++) image[4 * 12 + x] = 128; return image; }
        byte[] connected = Line(); string unchanged = Program.Hash(connected);
        var shear = Chain(new(1, .25, .5, 0, 1, 0, 0, 0, 1));
        var shearWindow = new SpanWindow(4, 8, 2, 2, 10, 2, 8, false, false, false, false);
        OriginalSupportProof yes = ObserveOriginalSupport(connected, 12, 12, shear, shearWindow, 4, CancellationToken.None);
        if (yes.AnySupportConnection != true || yes.Uncertain || yes.LeftInkContributors != 1 || yes.LeftWhiteSamples != 1 || yes.RightInkContributors != 2)
            throw new InvalidDataException();
        byte[] disconnected = Line(); disconnected[4 * 12 + 5] = 255;
        var no = ObserveOriginalSupport(disconnected, 12, 12, identity, window, 4, CancellationToken.None);
        if (no.AnySupportConnection != false || !no.SearchComplete || no.Uncertain) throw new InvalidDataException();
        byte[] missing = Line(); missing[4 * 12 + 3] = 255;
        var absent = ObserveOriginalSupport(missing, 12, 12, identity, window, 4, CancellationToken.None);
        if (absent.Conclusion != "UNCERTAIN_MISSING_ENDPOINT_SUPPORT" || absent.SearchAttempted) throw new InvalidDataException();
        byte[] outside = Line(); outside[4 * 12 + 2] = 128;
        var translated = Chain(new(1, 0, .5, 0, 1, 0, 0, 0, 1));
        var narrow = window with { Left = 3 };
        var excluded = ObserveOriginalSupport(outside, 12, 12, translated, narrow, 4, CancellationToken.None);
        if (excluded.Conclusion != "UNCERTAIN_ENDPOINT_SUPPORT_OUTSIDE_WINDOW" || excluded.LeftInkContributors != 2 || excluded.LeftOutsideWindow != 1 || excluded.SearchAttempted)
            throw new InvalidDataException();
        byte[] detour = Enumerable.Repeat((byte)255, 12 * 12).ToArray();
        for (int y = 1; y <= 4; y++) { detour[y * 12 + 3] = 128; detour[y * 12 + 7] = 128; }
        for (int x = 3; x <= 7; x++) detour[12 + x] = 128;
        var bounded = ObserveOriginalSupport(detour, 12, 12, identity, window, 4, CancellationToken.None);
        if (bounded.AnySupportConnection != false || !bounded.TopBoundary || !bounded.Uncertain) throw new InvalidDataException();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { _ = ObserveOriginalSupport(connected, 12, 12, shear, shearWindow, 4, cancel.Token); throw new InvalidOperationException(); }
        catch (OperationCanceledException error) when (error.CancellationToken == cancel.Token) { }
        if (Program.Hash(connected) != unchanged) throw new InvalidDataException();
        try { _ = ReadSourceSupportProperty("tampered"u8.ToArray()); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        return new { schema = "graphreader.real-dev-affine-source-support-self-test.v1", owned_controls = 5,
            nonidentity_shear_and_white_partner_checked = true, connected_disconnected_checked = true, missing_support_uncertain = true,
            all_positive_ink_contributors_included = true, outside_support_uncertain = true, mapped_window_detour_excluded = true,
            cancellation_and_immutability_checked = true, property_tamper_refused = true, private_reads = 0, worker_calls = 0 };
    }
}
