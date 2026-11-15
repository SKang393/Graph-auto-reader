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

internal interface IMiragePairedWorker
{
    internal sealed record Reply(int Status, double ElapsedMilliseconds, bool StrictJsonValid, string? ParseError, int RepairCount,
        bool RequiresReview, IReadOnlyList<MirageComparisonMapping.Row> Rows);
    Task<Reply> ReadAsync(byte[] encoded, int width, int height, CancellationToken token);
    bool IsAlive { get; }
    double StartupMilliseconds { get; }
}

internal static partial class RealDevCalibrationCheck
{
    private sealed class PairedCounts
    {
        internal int ProjectsAttempted, ProjectsComplete, ProjectsIncomplete, PreparationFailed, NoPanelFallback;
        internal int ReturnedPanels, PanelAttempts, PanelsFinished, PanelsUnsupported, PanelFailures, GeometryReturned;
        internal int BaselineResponses, BaselineComplete, MirageResponses, MirageStrictResponses, MirageComplete;
        internal int BaselineAttempts, MirageAttempts;
        internal int BaselineNumericRows, BaselineAssociatedPairs, MirageNumericRows, MirageAssociatedPairs;
        internal int BaselineEstimatedComplete, MirageEstimatedComplete;
        internal int MirageDiagnosticResponses, MirageDiagnosticRows, MirageDiagnosticNumericRows;
        internal int BaselineFailures, MirageFailures, UnknownOrInvalidRows;
        internal int OnePanel, ManyPanels, BothComplete, BaselineOnly, MirageOnly, NeitherComplete;
        internal readonly HashSet<string> ImageHashes = new(StringComparer.Ordinal);
        internal readonly List<double> GeometryMilliseconds = [], BaselineMilliseconds = [], MirageMilliseconds = [];
        internal readonly List<double> MirageWallMilliseconds = [], BaselineInitializationMilliseconds = [];
        internal readonly Dictionary<string, int> BaselineDraftReasons = new(StringComparer.Ordinal), MirageDraftReasons = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> Reasons = new(StringComparer.Ordinal);
        internal void Reason(string reason) => Reasons[reason] = Reasons.GetValueOrDefault(reason) + 1;
    }

    internal static async Task<object> RunMiragePairedAsync(string corpusRoot, string appExe, string assetRoot,
        string rustExe, CpuBudgetJob budget, IMiragePairedWorker mirage, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(corpusRoot) ||
            !Path.IsPathFullyQualified(appExe) || !Path.IsPathFullyQualified(assetRoot) ||
            !Path.IsPathFullyQualified(rustExe)) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify451(appExe, assetRoot, rustExe, token);
        // The caller must authenticate the worker identity and budget before calling this method.
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        FrozenRealCorpusProject[] selected = selector.EnumerateSelectedProjects(token)
            .OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (selected.Length != Denominator || selected.Any(project => project.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException();
        IAxisProposalService axes = new RustFirstAxisProposalService(new RustAxisProposalService(rustExe), new OpenCvAxisProposalService(appExe));
        await using var baseline = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var panelDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sourceTotals = new Totals(); var counts = new PairedCounts(); var elapsed = Stopwatch.StartNew();
        foreach (FrozenRealCorpusProject project in selected)
        {
            if (token.IsCancellationRequested || !mirage.IsAlive) break;
            counts.ProjectsAttempted++;
            int baselineBefore = counts.BaselineComplete, mirageBefore = counts.MirageComplete;
            bool finished = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                Payload payload = ReadLocked(selector, project, digest, sourceTotals, deadline.Token);
                counts.ImageHashes.Add(payload.ImageHash);
                IReadOnlyList<object>? panels = null;
                try { panels = await PrepareAsync(panelizer, payload.Image, payload.ImageHash, payload.Width, payload.Height, deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsGenuineNoPanel(error)) { counts.NoPanelFallback++; }
                if (panels is null)
                {
                    finished = await ComparePanelAsync(payload.Image, payload.Width, payload.Height, new PdfRectD(0, 0, payload.Width, payload.Height),
                        payload.Width, payload.Height, payload.ProjectHash, candidate, axes, baseline, mirage, counts, panelDigest, deadline.Token).ConfigureAwait(false);
                }
                else
                {
                    if (panels.Count == 0) throw new InvalidDataException();
                    if (panels.Count == 1) counts.OnePanel++; else counts.ManyPanels++;
                    counts.ReturnedPanels += panels.Count;
                    bool allPanelsProcessed = true;
                    foreach (object panel in panels)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        try
                        {
                            var (bytes, width, height, crop) = ReadPreparedPanel(panel, payload.Width, payload.Height);
                            if (crop.X < 0 || crop.Y < 0 || crop.X + width > payload.Width || crop.Y + height > payload.Height)
                                throw new InvalidDataException();
                            allPanelsProcessed &= await ComparePanelAsync(bytes, width, height, crop, payload.Width, payload.Height, payload.ProjectHash,
                                candidate, axes, baseline, mirage, counts, panelDigest, deadline.Token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                        { counts.PanelFailures++; counts.Reason("PANEL_PROCESSING_FAILED"); allPanelsProcessed = false; }
                    }
                    finished = allPanelsProcessed;
                }
            }
            catch (OperationCanceledException) { finished = false; counts.Reason(token.IsCancellationRequested ? "OVERALL_DEADLINE" : "PROJECT_TIMEOUT"); }
            catch (Exception error) when (error is not OutOfMemoryException) { finished = false; counts.PreparationFailed++; counts.Reason("PROJECT_PROCESSING_FAILED"); }
            FinishPairedProject(counts, finished, baselineBefore, mirageBefore);
        }
        if (!token.IsCancellationRequested) _ = selector.EnumerateSelectedProjects(CancellationToken.None);
        var report = new { schema = "graphreader.mirage-paired-dev-comparison.v1",
            status = counts.ProjectsAttempted == Denominator && counts.ProjectsIncomplete == 0 ? "completed" : "incomplete",
            protocol = new { denominator = Denominator, project_deadline_seconds = 45, overall_deadline_seconds = 900,
                geometry = "shared_451_rust_first", selection = "all_prepared_panels_no_truth_selection",
                sealed_payload_reads = 0, optimizer_steps = 0, image_writes = false, per_case_output = false,
                review_only = true, applied = false, accuracy_claim = false, production_approved = false },
            metadata.AssignmentSha256, metadata.SelectedInventorySha256,
            source_identity_digest = Convert.ToHexStringLower(digest.GetHashAndReset()),
            attempted_panel_identity_digest = Convert.ToHexStringLower(panelDigest.GetHashAndReset()),
            projects = new { attempted = counts.ProjectsAttempted, fully_processed = counts.ProjectsComplete,
                incomplete = counts.ProjectsIncomplete, unattempted = Denominator - counts.ProjectsAttempted,
                distinct_embedded_images = counts.ImageHashes.Count, one_panel = counts.OnePanel, many_panels = counts.ManyPanels,
                preparation_or_project_errors = counts.PreparationFailed, both_any_complete = counts.BothComplete,
                baseline_only_any_complete = counts.BaselineOnly, mirage_only_any_complete = counts.MirageOnly,
                neither_any_complete = counts.NeitherComplete },
            panels = new { returned = counts.ReturnedPanels, effective_including_fallback = counts.ReturnedPanels + counts.NoPanelFallback,
                attempted = counts.PanelAttempts, finished = counts.PanelsFinished,
                unattempted = counts.ReturnedPanels + counts.NoPanelFallback - counts.PanelAttempts, unsupported = counts.PanelsUnsupported,
                failures = counts.PanelFailures, interrupted_or_failed_after_start = counts.PanelAttempts - counts.PanelsFinished - counts.PanelsUnsupported,
                geometry_returned = counts.GeometryReturned, no_panel_fallback = counts.NoPanelFallback },
            identities = new { baseline_app_sha256 = App451ExeHash, baseline_model_sha256 = Program.ModelHash,
                baseline_manifest_sha256 = Program.ManifestHash, mirage = MiragePairedWorker.IdentityPins },
            baseline = new { attempted = counts.BaselineAttempts, responses = counts.BaselineResponses,
                interrupted = counts.BaselineAttempts - counts.BaselineResponses - counts.BaselineFailures,
                not_attempted_after_geometry = counts.GeometryReturned - counts.BaselineAttempts, literal_numeric_rows = counts.BaselineNumericRows,
                associated_pairs = counts.BaselineAssociatedPairs, complete = counts.BaselineComplete,
                estimated_complete = counts.BaselineEstimatedComplete, failures = counts.BaselineFailures, draft_reason_counts = counts.BaselineDraftReasons },
            mirage = new { attempted = counts.MirageAttempts, responses = counts.MirageResponses,
                interrupted = counts.MirageAttempts - counts.MirageResponses - counts.MirageFailures,
                not_attempted_after_geometry = counts.GeometryReturned - counts.MirageAttempts, strict_responses = counts.MirageStrictResponses,
                literal_numeric_rows = counts.MirageNumericRows, associated_pairs = counts.MirageAssociatedPairs,
                complete = counts.MirageComplete, estimated_complete = counts.MirageEstimatedComplete,
                failures = counts.MirageFailures, invalid_or_unknown_rows = counts.UnknownOrInvalidRows,
                recovered_diagnostic = new { responses = counts.MirageDiagnosticResponses, rows = counts.MirageDiagnosticRows,
                    literal_numeric_rows = counts.MirageDiagnosticNumericRows, eligible_for_calibration = false }, draft_reason_counts = counts.MirageDraftReasons },
            reason_counts = counts.Reasons, wall_milliseconds = elapsed.Elapsed.TotalMilliseconds,
            timing_milliseconds = new { geometry = Stats(counts.GeometryMilliseconds), baseline_wall = Stats(counts.BaselineMilliseconds),
                baseline_initialization = Stats(counts.BaselineInitializationMilliseconds), mirage_initialization = mirage.StartupMilliseconds,
                mirage_wall = Stats(counts.MirageWallMilliseconds), mirage_native = Stats(counts.MirageMilliseconds) },
            parent_cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores, budget.SilentBreakawayVerified } };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(report, Program.Json));
        return report;
    }

    private static void FinishPairedProject(PairedCounts counts, bool finished, int baselineBefore, int mirageBefore)
    {
        if (!finished) { counts.ProjectsIncomplete++; return; }
        counts.ProjectsComplete++;
        bool b = counts.BaselineComplete > baselineBefore, m = counts.MirageComplete > mirageBefore;
        if (b && m) counts.BothComplete++; else if (b) counts.BaselineOnly++;
        else if (m) counts.MirageOnly++; else counts.NeitherComplete++;
    }

    private static async Task<bool> ComparePanelAsync(byte[] encoded, int width, int height, PdfRectD crop, int sourceWidth, int sourceHeight, string projectHash,
        Candidate candidate, IAxisProposalService axes, ICalibrationOcrSuggestionService baseline, IMiragePairedWorker mirage,
        PairedCounts counts, IncrementalHash panelDigest, CancellationToken token)
    {
        counts.PanelAttempts++;
        panelDigest.AppendData(SHA256.HashData(encoded));
        if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216)
        { counts.PanelsUnsupported++; return false; }
        var timer = Stopwatch.StartNew();
        RustAxisProposalResult axis = await axes.SuggestAsync(encoded, token).ConfigureAwait(false);
        counts.GeometryMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
        if (axis.Geometry is not { } geometry) { counts.Reason(AxisReason(axis.ErrorCode)); counts.PanelsFinished++; return true; }
        counts.GeometryReturned++;
        string panelHash = Convert.ToHexStringLower(SHA256.HashData(encoded));
        var request = new CalibrationOcrRequest("paired-comparison", projectHash, projectHash, panelHash, width, height,
            new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
        { MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null };
        try
        {
            counts.BaselineAttempts++;
            timer.Restart();
            CalibrationOcrResponse response = await baseline.SuggestAsync(request, encoded, token).ConfigureAwait(false);
            counts.BaselineMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            if (response.FailureCode is not null || response.Runtime is not { } runtime || runtime.Provider != "Cpu" ||
                runtime.RequestedProvider != "Cpu" || runtime.ModelSha256 != Program.ModelHash || runtime.ManifestSha256 != Program.ManifestHash ||
                runtime.Files.Any(file => !candidate.Files.TryGetValue(file.Name, out string? expected) || file.Sha256 != expected))
                throw new InvalidDataException();
            CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(geometry, response);
            ValidatePanelMapping(response.Proposals, proposal, geometry, crop, width, height, sourceWidth, sourceHeight);
            if (proposal.IsComplete) ValidatePreparedMapping(proposal, crop, sourceWidth, sourceHeight);
            if (response.Timings is { } baselineTiming) counts.BaselineInitializationMilliseconds.Add(baselineTiming.InitializationMilliseconds);
            counts.BaselineNumericRows += response.Proposals.Count(row => row.NumericLiteral);
            CountDraftReasons(counts.BaselineDraftReasons, proposal);
            counts.BaselineAssociatedPairs += proposal.PairedTicks.Count;
            if (proposal.IsComplete) { counts.BaselineComplete++; if (proposal.UsesEstimatedPositions) counts.BaselineEstimatedComplete++; }
            counts.BaselineResponses++;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { counts.BaselineFailures++; counts.Reason("BASELINE_OCR_FAILED"); }
        try
        {
            counts.MirageAttempts++;
            timer.Restart();
            IMiragePairedWorker.Reply reply = await mirage.ReadAsync(encoded, width, height, token).ConfigureAwait(false);
            counts.MirageWallMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            counts.MirageMilliseconds.Add(reply.ElapsedMilliseconds);
            MirageComparisonMapping.Result mapped = MirageComparisonMapping.Map(reply.Rows, width, height, request.Plot);
            counts.UnknownOrInvalidRows += mapped.RejectedRows;
            if (!reply.StrictJsonValid && reply.RepairCount > 0)
            {
                counts.MirageDiagnosticResponses++;
                counts.MirageDiagnosticRows += reply.Rows.Count;
                counts.MirageDiagnosticNumericRows += reply.Rows.Count(row => CalibrationNumericLiteral.IsLiteral(row.Text));
            }
            if (!MirageComparisonMapping.StrictUsable(reply.Status, reply.StrictJsonValid, reply.ParseError,
                reply.RepairCount, reply.RequiresReview, mapped)) { counts.Reason("MIRAGE_RESPONSE_NOT_STRICT"); }
            else
            {
                var response = new CalibrationOcrResponse(request, mapped.Rows, null, null, null, null, null);
                CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(geometry, response);
                ValidatePanelMapping(response.Proposals, proposal, geometry, crop, width, height, sourceWidth, sourceHeight);
                if (proposal.IsComplete) ValidatePreparedMapping(proposal, crop, sourceWidth, sourceHeight);
                counts.MirageNumericRows += mapped.Rows.Count(row => row.NumericLiteral);
                CountDraftReasons(counts.MirageDraftReasons, proposal);
                counts.MirageAssociatedPairs += proposal.PairedTicks.Count;
                if (proposal.IsComplete) { counts.MirageComplete++; if (proposal.UsesEstimatedPositions) counts.MirageEstimatedComplete++; }
                counts.MirageStrictResponses++;
            }
            if (!MirageComparisonMapping.StrictUsable(reply.Status, reply.StrictJsonValid, reply.ParseError,
                reply.RepairCount, reply.RequiresReview, mapped))
                ValidatePanelRows(mapped.Rows, crop, width, height, sourceWidth, sourceHeight);
            counts.MirageResponses++;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { counts.MirageFailures++; counts.Reason("MIRAGE_OCR_FAILED"); }
        counts.PanelsFinished++;
        return true;
    }

    private static void CountDraftReasons(Dictionary<string, int> target, CalibrationTickProposalResult proposal)
    {
        foreach (string reason in proposal.Reasons.Select(TickReason).Distinct(StringComparer.Ordinal))
            target[reason] = target.GetValueOrDefault(reason) + 1;
    }

    private static void ValidatePanelMapping(IReadOnlyList<CalibrationOcrProposal> rows, CalibrationTickProposalResult proposal,
        RustAxisGeometry geometry, PdfRectD crop, int width, int height, int sourceWidth, int sourceHeight)
    {
        int x = checked((int)crop.X), y = checked((int)crop.Y);
        ValidatePanelRows(rows, crop, width, height, sourceWidth, sourceHeight);
        foreach (CalibrationPairedTick pair in proposal.PairedTicks)
        {
            var point = pair.Axis == "X" ? new CalibrationOcrPoint(pair.PixelPosition, geometry.Bottom) :
                new CalibrationOcrPoint(geometry.Left, pair.PixelPosition);
            _ = MirageComparisonMapping.ToSource(point, x, y, width, height, sourceWidth, sourceHeight);
        }
    }

    private static void ValidatePanelRows(IReadOnlyList<CalibrationOcrProposal> rows, PdfRectD crop,
        int width, int height, int sourceWidth, int sourceHeight)
    {
        int x = checked((int)crop.X), y = checked((int)crop.Y);
        foreach (CalibrationOcrPoint point in rows.SelectMany(row => row.Polygon))
            _ = MirageComparisonMapping.ToSource(point, x, y, width, height, sourceWidth, sourceHeight);
    }

    internal static async Task<object> MirageOwnedControlAsync(string appExe, string assetRoot, string rustExe,
        CpuBudgetJob budget, IMiragePairedWorker mirage, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify451(appExe, assetRoot, rustExe, token);
        string sourcePath = Path.Combine(@"E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\mirage-comparison-20261115\supplied", "synthetic.png");
        byte[] source = Program.ReadBounded(sourcePath, 1024 * 1024);
        string hash = Convert.ToHexStringLower(SHA256.HashData(source));
        if (hash != "6f1e161799715d031cd8391263df264030dec2a1baecf3b56fff8ea146c00ca2") throw new InvalidDataException();
        var (decoded, sourceWidth, sourceHeight) = Program.Decode(source, [0, 0, 600, 400], token);
        if (sourceWidth != 600 || sourceHeight != 400 || decoded.Width != 600 || decoded.Height != 400) throw new InvalidDataException();
        await using var baseline = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        var panelizer = ResolvePanelizer(typeof(CalibrationTickProposal).Assembly);
        IReadOnlyList<object>? panels = null;
        try { panels = await PrepareAsync(panelizer, source, hash, 600, 400, token).ConfigureAwait(false); }
        catch (Exception error) when (IsGenuineNoPanel(error)) { }
        var automatic = new PairedCounts(); using var autoDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IAxisProposalService axes = new RustFirstAxisProposalService(new RustAxisProposalService(rustExe), new OpenCvAxisProposalService(appExe));
        bool fallback = panels is null;
        if (fallback)
        {
            automatic.NoPanelFallback++;
            _ = await ComparePanelAsync(source, 600, 400, new PdfRectD(0, 0, 600, 400), 600, 400, hash,
                candidate, axes, baseline, mirage, automatic, autoDigest, token).ConfigureAwait(false);
        }
        else
        {
            if (panels!.Count == 0) throw new InvalidDataException();
            automatic.ReturnedPanels = panels.Count;
            foreach (object panel in panels)
            {
                var (bytes, width, height, crop) = ReadPreparedPanel(panel, 600, 400);
                _ = await ComparePanelAsync(bytes, width, height, crop, 600, 400, hash,
                    candidate, axes, baseline, mirage, automatic, autoDigest, token).ConfigureAwait(false);
            }
        }
        var supplied = new PairedCounts(); using var suppliedDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var geometry = new RustAxisGeometry(80, 320, 550, 40, 1, 160, 480)
        { XTickPositions = [160, 320, 480], YTickPositions = [60, 190, 320] };
        _ = await ComparePanelAsync(source, 600, 400, new PdfRectD(0, 0, 600, 400), 600, 400, hash,
            candidate, new PairedSuppliedAxes(geometry), baseline, mirage, supplied, suppliedDigest, token).ConfigureAwait(false);
        return new { schema = "graphreader.mirage-paired-owned-control.v1", scope = "owned_synthetic",
            source_sha256 = hash, automatic_geometry = OwnedCounts(automatic), supplied_geometry = OwnedCounts(supplied),
            no_panel_fallback = fallback, private_payload_reads = 0, review_only = true, applied = false, accuracy_claim = false,
            mirage_startup_milliseconds = mirage.StartupMilliseconds,
            note = "Supplied geometry tests OCR and association only; automatic geometry uses prepared pixels." };
    }

    private static object OwnedCounts(PairedCounts value) => new
    {
        returned_panels = value.ReturnedPanels, attempted_panels = value.PanelAttempts, geometry_returned = value.GeometryReturned,
        baseline = new { attempts = value.BaselineAttempts, responses = value.BaselineResponses, failures = value.BaselineFailures,
            numeric_rows = value.BaselineNumericRows, pairs = value.BaselineAssociatedPairs,
            complete = value.BaselineComplete, reasons = value.BaselineDraftReasons,
            wall_milliseconds = Stats(value.BaselineMilliseconds), initialization_milliseconds = Stats(value.BaselineInitializationMilliseconds) },
        mirage = new { attempts = value.MirageAttempts, responses = value.MirageResponses, failures = value.MirageFailures, strict_responses = value.MirageStrictResponses,
            numeric_rows = value.MirageNumericRows, pairs = value.MirageAssociatedPairs, complete = value.MirageComplete,
            diagnostic_rows = value.MirageDiagnosticRows, diagnostic_numeric_rows = value.MirageDiagnosticNumericRows,
            invalid_or_unknown_rows = value.UnknownOrInvalidRows, reasons = value.MirageDraftReasons,
            wall_milliseconds = Stats(value.MirageWallMilliseconds), native_milliseconds = Stats(value.MirageMilliseconds) },
        stage_failures = value.Reasons,
    };

    private sealed class PairedSuppliedAxes(RustAxisGeometry geometry) : IAxisProposalService
    {
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RustAxisProposalResult(geometry, null));
        }
    }

    internal static async Task<object> MiragePairedFakeSelfTestAsync(CancellationToken token)
    {
        byte[] panel = [1, 2, 3];
        var axes = new PairedFakeAxes();
        await using var baseline = new PairedFakeBaseline();
        var mirage = new PairedFakeMirage();
        using var candidate = new Candidate();
        using var panelDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var counts = new PairedCounts();
        if (!await ComparePanelAsync(panel, 100, 80, new PdfRectD(0, 0, 100, 80), 100, 80, new string('a', 64), candidate, axes, baseline, mirage, counts, panelDigest, token) ||
            !ReferenceEquals(panel, axes.Input) || !ReferenceEquals(panel, baseline.Input) || !ReferenceEquals(panel, mirage.Input) ||
            counts.PanelAttempts != 1 || counts.PanelsFinished != 1 || counts.GeometryReturned != 1 ||
            counts.BaselineResponses != 1 || counts.MirageResponses != 1 || counts.MirageStrictResponses != 1 ||
            counts.BaselineComplete != 0 || counts.MirageComplete != 0) throw new InvalidDataException();
        mirage.Invalid = true;
        if (!await ComparePanelAsync(panel, 100, 80, new PdfRectD(0, 0, 100, 80), 100, 80, new string('a', 64), candidate, axes, baseline, mirage, counts, panelDigest, token) ||
            counts.MirageResponses != 2 || counts.MirageStrictResponses != 1 || counts.UnknownOrInvalidRows != 1 ||
            counts.Reasons.GetValueOrDefault("MIRAGE_RESPONSE_NOT_STRICT") != 1) throw new InvalidDataException();
        if (await ComparePanelAsync(panel, 2, 80, new PdfRectD(0, 0, 2, 80), 2, 80, new string('a', 64), candidate, axes, baseline, mirage, counts, panelDigest, token) ||
            counts.PanelAttempts != 3 || counts.PanelsUnsupported != 1 || counts.PanelsFinished != 2) throw new InvalidDataException();
        FinishPairedProject(counts, false, 0, 0);
        if (counts.ProjectsIncomplete != 1 || counts.ProjectsComplete != 0 || counts.NeitherComplete != 0)
            throw new InvalidDataException();
        return new { schema = "graphreader.mirage-paired-owned-fake-selftest.v1", status = "passed",
            same_panel_instance = true, incomplete_not_complete = true, unknown_score_not_strict = true,
            unsupported_counted = true, cancelled_project_incomplete = true, private_payload_reads = 0, review_only = true };
    }

    private sealed class PairedFakeAxes : IAxisProposalService
    {
        internal byte[]? Input { get; private set; }
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Input = originalImage;
            return Task.FromResult(new RustAxisProposalResult(new(10, 70, 80, 10, .8), null));
        }
    }

    private sealed class PairedFakeBaseline : ICalibrationOcrSuggestionService
    {
        internal byte[]? Input { get; private set; }
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(encodedOriginalPanel, out ArraySegment<byte> segment)) throw new InvalidDataException();
            Input = segment.Array;
            var runtime = new CalibrationOcrRuntime(Program.ModelHash, Program.ManifestHash, "Cpu", 1, 1, 1, 1, false,
                new(12, 416, .4992, true), new(0, 0, 0, 0, 0, 0), []);
            return Task.FromResult(new CalibrationOcrResponse(request, [], null, null, null, runtime, null));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PairedFakeMirage : IMiragePairedWorker
    {
        public bool IsAlive => true;
        public double StartupMilliseconds => 0;
        internal byte[]? Input { get; private set; }
        internal bool Invalid { get; set; }
        public Task<IMiragePairedWorker.Reply> ReadAsync(byte[] encoded, int width, int height, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Input = encoded;
            IReadOnlyList<MirageComparisonMapping.Row> rows = Invalid ? [new("1", [200, 750, 250, 875], null)] : [];
            return Task.FromResult(new IMiragePairedWorker.Reply(0, 1, true, null, 0, true, rows));
        }
    }
}
