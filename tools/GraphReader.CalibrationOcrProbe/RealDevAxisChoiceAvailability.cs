// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal sealed record ChoiceAvailabilityOutcome(object Report, bool Completed);
    private static readonly string[] ChoiceStatuses = ["COMPLETE_CHOICES", "INCOMPLETE_CHOICES", "UNAVAILABLE", "PROVIDER_ERROR", "TIMEOUT", "CANCELLED", "UNSUPPORTED", "FAILED"];

    internal static void RequireAxisChoiceArguments(string[] args)
    {
        if (args is not ["--real-dev-axis-choice-availability-check", _, _, _, "--explicit-opt-in"])
            throw new InvalidDataException("CHOICE_EXPLICIT_OPT_IN_REQUIRED");
    }

    internal static object AxisChoiceAvailabilityBlocked(bool executionStarted) => new
    {
        schema = "graphreader.real-dev-axis-choice-availability.v1", status = "blocked_preflight_or_runtime",
        failure = "AXIS_CHOICE_AVAILABILITY_BLOCKED", execution_started = executionStarted,
        declared_projects = TraceSampleCount, projects_attempted = executionStarted ? (int?)null : 0,
        projects_unattempted = executionStarted ? (int?)null : TraceSampleCount,
        source_payload_reads = executionStarted ? (int?)null : 0, counts_unavailable_after_unreported_runtime_failure = executionStarted,
        reserved_payload_reads = 0, truth_reads = 0, normal_suggest_calls = 0, ocr_calls = 0, tick_calls = 0,
        candidate_selected = false, image_writes = false, accuracy_or_baseline_gain_claimed = false,
    };

    private static void RequireChoiceMetadata(string assignment, string inventory, int count, IEnumerable<string> splits)
    {
        string[] selectedSplits = splits.ToArray();
        if (assignment != CachedAxisAssignmentHash || inventory != CachedAxisInventoryHash || count != Denominator ||
            selectedSplits.Length != count || selectedSplits.Any(split => split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException("CHOICE_SELECTION_CHANGED");
    }

    private sealed class ChoiceObservedService(IAxisProposalService inner) : IAxisProposalService, IAxisCandidateReviewService
    {
        internal int Calls { get; private set; }
        internal AxisCandidateReviewResult? Last { get; private set; }
        public bool CanReviewCandidates => ((IAxisCandidateReviewService)inner).CanReviewCandidates;
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token) => throw new InvalidOperationException("NORMAL_SUGGEST_FORBIDDEN");
        public async Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] image, CancellationToken token)
        {
            Calls++; Last = null;
            return Last = await ((IAxisCandidateReviewService)inner).ReviewCandidatesAsync(image, token).ConfigureAwait(false);
        }
    }

    private static string ClassifyChoiceResult(AxisCandidateReviewResult result, int width, int height)
    {
        if (result.ErrorCode is null)
        {
            if (!OpenCvAxisProposalService.ValidCandidates(result, width, height)) throw new InvalidDataException("CHOICE_INVALID_RESPONSE");
            return result.EnumerationCompleted ? "COMPLETE_CHOICES" : "INCOMPLETE_CHOICES";
        }
        if (result.Candidates is not { Count: 0 } || !result.EnumerationCompleted || result.Provider is not ("rust-review" or "opencv-review"))
            throw new InvalidDataException("CHOICE_INVALID_ERROR_RESPONSE");
        return result.ErrorCode switch
        {
            "NO_AXIS" or "NO_INTERSECTION" or "AMBIGUOUS_AXIS" or "OPENCV_AXIS_AMBIGUOUS" => "UNAVAILABLE",
            "WORKER_TIMEOUT" or "OPENCV_AXIS_TIMEOUT" => "TIMEOUT",
            _ => "PROVIDER_ERROR",
        };
    }

    internal static async Task<ChoiceAvailabilityOutcome> RunAxisChoiceAvailabilityAsync(string corpus, string app, string rust,
        CpuBudgetJob budget, CancellationToken token)
    {
        int projectsAttempted = 0, projectsCompleted = 0, projectsFailed = 0;
        int sourcesAttempted = 0, sourcesCompleted = 0, preparationAttempted = 0, preparationCompleted = 0, fallbacks = 0;
        int plannedPanels = 0, attemptedPanels = 0, finishedPanels = 0, rectangles = 0, affine = 0;
        long imageBytes = 0, preparedBytes = 0;
        var terminal = ChoiceStatuses.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var rustErrors = new Dictionary<string, int>(StringComparer.Ordinal);
        var nativeErrors = new Dictionary<string, int>(StringComparer.Ordinal);
        var providers = new Dictionary<string, int>(StringComparer.Ordinal);
        var times = new Dictionary<string, double>(StringComparer.Ordinal);
        string stage = "runtime", overall = "failed";
        string? failure = null, actualDigest = null;
        bool digestVerified = false, metadataVerified = false;
        Candidate? lease = null;
        ChoiceObservedService? primary = null, native = null;
        var total = Stopwatch.StartNew();
        long allocationBefore = GC.GetTotalAllocatedBytes();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Count(Dictionary<string, int> counts, string code) => counts[code] = counts.GetValueOrDefault(code) + 1;
        void AddTime(string name, Stopwatch clock) => times[name] = times.GetValueOrDefault(name) + clock.Elapsed.TotalMilliseconds;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!budget.SilentBreakawayVerified || budget.MaximumLogicalCores > .5 ||
                !Path.IsPathFullyQualified(corpus) || !Path.IsPathFullyQualified(app) || !Path.IsPathFullyQualified(rust)) throw new InvalidDataException("CHOICE_PREFLIGHT");
            lease = Candidate.VerifyChoiceAvailability(app, rust, token); // Before even metadata selection.
            var imageReader = ResolveEmbeddedImageReader();
            var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
            stage = "selection";
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token);
            FrozenDevTrainingMetadata metadata = selector.Describe();
            FrozenRealCorpusProject[] projects = selector.EnumerateSelectedProjects(token)
                .OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
            RequireChoiceMetadata(metadata.AssignmentSha256, metadata.SelectedInventorySha256, projects.Length, projects.Select(project => project.Split));
            primary = new(new RustAxisProposalService(rust)); native = new(new OpenCvAxisProposalService(app));
            var review = new RustFirstAxisProposalService(primary, native);
            for (int index = 0; index < TraceSampleCount && !token.IsCancellationRequested; index++)
            {
                projectsAttempted++; bool projectFailed = false;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                var clock = Stopwatch.StartNew();
                stage = "source_read";
                try
                {
                    sourcesAttempted++;
                    ImageOnlyPayload payload = ReadLockedImageOnly(selector, projects[index], digest, imageReader, deadline.Token);
                    sourcesCompleted++; imageBytes += payload.Image.Length; AddTime(stage, clock); clock.Restart();
                    if (payload.Width is < 32 or > 4096 || payload.Height is < 32 or > 4096 || (long)payload.Width * payload.Height > 16_777_216)
                    { plannedPanels++; attemptedPanels++; finishedPanels++; terminal["UNSUPPORTED"]++; projectsCompleted++; continue; }
                    stage = "preparation"; preparationAttempted++;
                    IReadOnlyList<object>? panels;
                    try { panels = await PrepareAsync(panelizer, payload.Image, Program.Hash(payload.Image), payload.Width, payload.Height, deadline.Token).ConfigureAwait(false); }
                    catch (Exception error) when (IsGenuineNoPanel(error)) { panels = null; fallbacks++; }
                    if (panels is { Count: 0 }) throw new InvalidDataException("CHOICE_EMPTY_PREPARATION");
                    preparationCompleted++; AddTime(stage, clock); clock.Restart();
                    int count = panels?.Count ?? 1; plannedPanels += count;
                    for (int panelIndex = 0; panelIndex < count; panelIndex++)
                    {
                        if (deadline.IsCancellationRequested)
                        { projectFailed = true; Count(failures, token.IsCancellationRequested ? "panel:CANCELLED_UNATTEMPTED" : "panel:PROJECT_TIMEOUT_UNATTEMPTED"); break; }
                        attemptedPanels++; stage = "panel_review"; clock.Restart();
                        int priorPrimary = primary.Calls, priorNative = native.Calls;
                        try
                        {
                            var panel = panels is null ? (payload.Image, payload.Width, payload.Height) :
                                PreparedChoicePanel(panels[panelIndex], payload.Width, payload.Height);
                            preparedBytes += panel.Item1.Length;
                            if (panel.Item2 is < 32 or > 4096 || panel.Item3 is < 32 or > 4096 || (long)panel.Item2 * panel.Item3 > 16_777_216)
                            { terminal["UNSUPPORTED"]++; continue; }
                            AxisCandidateReviewResult result = await review.ReviewCandidatesAsync(panel.Item1, deadline.Token).ConfigureAwait(false);
                            deadline.Token.ThrowIfCancellationRequested();
                            terminal[ClassifyChoiceResult(result, panel.Item2, panel.Item3)]++;
                            Count(providers, result.Provider);
                            rectangles += result.Candidates.Count(candidate => candidate.Geometry is not null);
                            affine += result.Candidates.Count(candidate => candidate.AffineGeometry is not null);
                        }
                        catch (OperationCanceledException)
                        { projectFailed = true; terminal[token.IsCancellationRequested ? "CANCELLED" : "TIMEOUT"]++; }
                        catch (TimeoutException)
                        { projectFailed = true; terminal["TIMEOUT"]++; Count(failures, "panel_review:TIMEOUT"); }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        { projectFailed = true; terminal["FAILED"]++; Count(failures, "panel_review:" + TraceExceptionCategory(error)); }
                        finally
                        {
                            finishedPanels++; AddTime(stage, clock);
                            if (primary.Calls > priorPrimary) Count(rustErrors, SafeReviewError(primary.Last?.ErrorCode) ?? (primary.Last is null ? "NO_RESPONSE" : "NONE"));
                            if (native.Calls > priorNative) Count(nativeErrors, SafeReviewError(native.Last?.ErrorCode) ?? (native.Last is null ? "NO_RESPONSE" : "NONE"));
                        }
                    }
                    if (projectFailed) projectsFailed++; else projectsCompleted++;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    projectsFailed++; AddTime(stage, clock);
                    Count(failures, stage + ":" + (error is OperationCanceledException ? token.IsCancellationRequested ? "CANCELLED" : "PROJECT_TIMEOUT" : TraceExceptionCategory(error)));
                }
            }
            stage = "final_identity";
            actualDigest = Convert.ToHexStringLower(digest.GetHashAndReset());
            digestVerified = sourcesCompleted == TraceSampleCount && actualDigest == CachedAxisSourceDigest;
            var final = selector.EnumerateSelectedProjects(token);
            RequireChoiceMetadata(metadata.AssignmentSha256, metadata.SelectedInventorySha256, final.Count, final.Select(project => project.Split));
            metadataVerified = true;
            if (!digestVerified || projectsAttempted != TraceSampleCount || projectsFailed != 0 || finishedPanels != plannedPanels)
                failure = token.IsCancellationRequested ? "CANCELLED" : "INCOMPLETE_OR_CHANGED_INPUTS";
            overall = failure is null ? "completed" : "failed";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is OperationCanceledException ? "CANCELLED" : TraceExceptionCategory(error);
            Count(failures, stage + ":" + failure);
        }
        finally { lease?.Dispose(); }
        if (actualDigest is null) actualDigest = Convert.ToHexStringLower(digest.GetHashAndReset());
        object report = new
        {
            schema = "graphreader.real-dev-axis-choice-availability.v1", status = overall, failure,
            source_content_commit = ChoiceAvailabilitySource, source_runtime_manifest_sha256 = ChoiceAvailabilityManifest,
            runtime_kind = "unpackaged framework-dependent Windows Desktop source, not packaged 457",
            app_exe_sha256 = ChoiceAvailabilityApp, rust_sha256 = ChoiceAvailabilityRust, runtime_files = lease?.Files,
            selection = "first_12_sorted_anonymized_case_id", frozen_development_count = Denominator, excluded_reserved_count = 51,
            expected_assignment_sha256 = CachedAxisAssignmentHash, expected_inventory_sha256 = CachedAxisInventoryHash,
            expected_source_digest = CachedAxisSourceDigest, actual_source_digest = actualDigest, source_digest_verified = digestVerified, final_metadata_verified = metadataVerified,
            declared_projects = TraceSampleCount, projects_attempted = projectsAttempted, projects_completed = projectsCompleted,
            projects_failed = projectsFailed, projects_unattempted = TraceSampleCount - projectsAttempted,
            sources_attempted = sourcesAttempted, sources_completed = sourcesCompleted, sources_failed = sourcesAttempted - sourcesCompleted,
            preparation_attempted = preparationAttempted, preparation_completed = preparationCompleted,
            preparation_failed = preparationAttempted - preparationCompleted, preparation_unattempted = projectsAttempted - preparationAttempted,
            full_image_fallbacks = fallbacks, planned_panels = plannedPanels, panels_attempted = attemptedPanels,
            panels_finished = finishedPanels, panels_unattempted = plannedPanels - attemptedPanels, terminal_status_counts = terminal,
            rectangle_choices = rectangles, affine_choices = affine, primary_review_calls = primary?.Calls ?? 0, native_review_calls = native?.Calls ?? 0,
            rust_error_counts = rustErrors, native_error_counts = nativeErrors, failure_counts = failures, stage_milliseconds = times,
            returned_provider_counts = providers,
            availability_counts_verified = digestVerified && metadataVerified && overall == "completed",
            prepared_image_bytes = preparedBytes, source_image_bytes = imageBytes,
            allocated_bytes = GC.GetTotalAllocatedBytes() - allocationBefore, elapsed_milliseconds = total.Elapsed.TotalMilliseconds,
            budget.MaximumLogicalCores, overall_logical_seconds = 240, hard_watchdog_seconds = 245, project_seconds = 45,
            normal_suggest_calls = 0, ocr_calls = 0, tick_calls = 0, fit_calls = 0, truth_reads = 0, reserved_payload_reads = 0,
            candidate_selected = false, apply_called = false, export_called = false, image_writes = false,
            accuracy_or_baseline_gain_claimed = false,
        };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(report, Program.Json));
        return new(report, overall == "completed");
    }

    private static (byte[], int, int) PreparedChoicePanel(object panel, int width, int height)
    {
        var prepared = ReadPreparedPanel(panel, width, height);
        return (prepared.Bytes, prepared.Width, prepared.Height);
    }

    private sealed class ChoiceFake(AxisCandidateReviewResult result) : IAxisProposalService, IAxisCandidateReviewService
    {
        internal bool Timeout { get; init; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] image, CancellationToken token) => throw new InvalidOperationException();
        public Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] image, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Timeout ? Task.FromException<AxisCandidateReviewResult>(new TimeoutException()) : Task.FromResult(result); }
    }

    internal static async Task<object> AxisChoiceAvailabilitySelfTestAsync(CancellationToken token)
    {
        string[] optedIn = ["--real-dev-axis-choice-availability-check", "unused-corpus", "unused-app", "unused-rust", "--explicit-opt-in"];
        RequireAxisChoiceArguments(optedIn);
        bool missingOptInRefused = false;
        try { RequireAxisChoiceArguments(optedIn[..^1]); }
        catch (InvalidDataException) { missingOptInRefused = true; }
        if (!missingOptInRefused) throw new InvalidDataException();
        RequireChoicePins(ChoiceAvailabilityApp, ChoiceAvailabilityRust, ChoiceAvailabilityLibraries);
        int refusals = 0;
        try { RequireChoicePins("changed", ChoiceAvailabilityRust, ChoiceAvailabilityLibraries); } catch (InvalidDataException) { refusals++; }
        var changed = ChoiceAvailabilityLibraries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        changed["GraphReader.Axis.dll"] = new string('0', 64);
        try { RequireChoicePins(ChoiceAvailabilityApp, ChoiceAvailabilityRust, changed); } catch (InvalidDataException) { refusals++; }
        RequireChoiceMetadata(CachedAxisAssignmentHash, CachedAxisInventoryHash, 120, Enumerable.Repeat(FrozenRealCorpusInventory.RealDev, 120));
        try { RequireChoiceMetadata("changed", CachedAxisInventoryHash, 120, []); } catch (InvalidDataException) { refusals++; }
        try { RequireChoiceMetadata(CachedAxisAssignmentHash, CachedAxisInventoryHash, 119, []); } catch (InvalidDataException) { refusals++; }
        try { RequireChoiceMetadata(CachedAxisAssignmentHash, CachedAxisInventoryHash, 120, [FrozenRealCorpusInventory.RealSealed]); } catch (InvalidDataException) { refusals++; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Gray8, null, new byte[32 * 32], 32)));
        using var png = new MemoryStream(); encoder.Save(png);
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes("<Engauge><Image>" + Convert.ToBase64String(png.ToArray()) + "</Image><Point><PositionGraph>must-not-be-parsed</PositionGraph></Point></Engauge>"));
        ImageOnlyPayload extracted = ExtractImageOnly(xml, ResolveEmbeddedImageReader(), token);
        if (extracted.Width != 32 || extracted.Height != 32 || !extracted.Image.AsSpan().SequenceEqual(png.ToArray()) || refusals != 5) throw new InvalidDataException();
        var first = new ChoiceObservedService(new ChoiceFake(new([], "NO_AXIS") { Provider = "rust-review" }));
        var rectangle = new AxisReviewCandidate(0, new(2, 28, 28, 2, 0));
        var counted = ChoiceStatuses.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
        for (int index = 0; index < 2; index++)
        {
            bool complete = index == 0;
            var native = new ChoiceObservedService(new ChoiceFake(new([rectangle], null) { EnumerationCompleted = complete }));
            AxisCandidateReviewResult observed = await new RustFirstAxisProposalService(first, native).ReviewCandidatesAsync([1], token).ConfigureAwait(false);
            string status = ClassifyChoiceResult(observed, 32, 32); counted[status]++;
            if (status != (complete ? "COMPLETE_CHOICES" : "INCOMPLETE_CHOICES") || native.Calls != 1) throw new InvalidDataException();
        }
        if (ClassifyChoiceResult(new([], "OPENCV_AXIS_AMBIGUOUS"), 32, 32) != "UNAVAILABLE" ||
            ClassifyChoiceResult(new([], "OPENCV_AXIS_TIMEOUT"), 32, 32) != "TIMEOUT") throw new InvalidDataException();
        counted[ClassifyChoiceResult(new([], "OPENCV_AXIS_AMBIGUOUS"), 32, 32)]++;
        counted[ClassifyChoiceResult(new([], "WORKER_TIMEOUT") { Provider = "rust-review" }, 32, 32)]++;
        if (counted.Values.Sum() != 4 || counted["COMPLETE_CHOICES"] != 1 || counted["INCOMPLETE_CHOICES"] != 1 ||
            counted["UNAVAILABLE"] != 1 || counted["TIMEOUT"] != 1) throw new InvalidDataException();
        try { _ = ClassifyChoiceResult(new([], null), 32, 32); throw new InvalidOperationException(); } catch (InvalidDataException) { }
        var throwing = new ChoiceObservedService(new ChoiceFake(new([], "OPENCV_AXIS_UNAVAILABLE")) { Timeout = true });
        try { await new RustFirstAxisProposalService(first, throwing).ReviewCandidatesAsync([1], token).ConfigureAwait(false); throw new InvalidOperationException(); }
        catch (TimeoutException) { if (throwing.Calls != 1 || throwing.Last is not null) throw new InvalidDataException(); }
        return new { schema = "graphreader.axis-choice-availability-self-test.v1", pin_refusals = 2, split_refusals = 3,
            missing_explicit_opt_in_refused_before_runtime_or_selection = missingOptInRefused,
            image_only_extraction = true, complete_incomplete_unavailable_timeout_accounting = true, normal_suggest_calls = 0,
            private_payload_reads = 0, worker_calls = 0, model_calls = 0 };
    }
}
