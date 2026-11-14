// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

/// <summary>One fixed real-dev diagnostic. Private payloads and observations never enter its report.</summary>
internal static class RealDevCalibrationCheck
{
    private const int Denominator = 120;
    private const string AppDllHash = "634d5121dc73d1c18282581b2623def039d86089712d124f9fb260a85c194d03";
    private const string AppExeHash = "3085c125bdaf9b6271363e3593341dc8d7cd2b9761e1be22282f04389c30c5f7";
    private const string RustHash = "71455f26d241aec90bf2b88b4267e272028a286dd2b8e7fbc5f86ab8d27fdfa0";
    // Exact delivered 0.4.46 libraries from the ZIP-entry-verified package, not caller-selected identities.
    private static readonly FrozenDictionary<string, string> DeliveredLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = AppDllHash,
        ["GraphReader.Axis.dll"] = "42562598e489d7558f282ab58207eca490019576366aad4f8b299be4d0223cee",
        ["GraphReader.Domain.dll"] = "631a22bd8c98a282f66d326048c8136f1c8029985f84b9b3d56e21cb743495cc",
        ["GraphReader.Export.dll"] = "4c4142341b4f69177989f4a7450f2b96848cdfcf2bcb995f4b8d3d5f16050251",
        ["GraphReader.Imaging.dll"] = "9f02bc56cc46e65a385b8aa72ed50fed1c3de8688b507c8f8557595d8c6242a0",
        ["GraphReader.Inference.dll"] = "e1030470781eb4b533f314910ee694167a2bc9d975ae05c9cd320ae4e6430686",
        ["GraphReader.Legends.dll"] = "0a6d4484ded97954f7a9eb48c027544db502c37d02551fae769b6bcf92b05353",
        ["GraphReader.Markers.dll"] = "0003e4f54e2b4ddf72176b96821b1c62c3e4e60f4601b5646c4253e871c52595",
        ["GraphReader.Ocr.dll"] = "05441a279f04c38452b8746c7ab2a8e2d1dda6237b45558c8355c2cca7e01b72",
        ["GraphReader.Pdf.dll"] = "cc92604446808a4fcf919f516584c84772357dd03562150455f8704a954a6a5e",
        ["GraphReader.Phases.dll"] = "8e2f27461d31f2b6eebbe9b055fcd205af0c53a80d89b971642285dc3443cf33",
        ["GraphReader.SuperResolution.dll"] = "af64f365fb82df4a7d587665330c12193dc1a08dfac4b983338383017f8e0a83",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        ["vcruntime140_cor3.dll"] = "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066",
    }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly string[] ForbiddenProperties = ["case_id", "study_id", "path", "image_path", "text",
        "boxes", "anchors", "proposals", "predictions", "per_case", "source_sha256", "project_sha256", "image_sha256"];

    internal static async Task<object> RunAsync(string corpusRoot, string appExe, string assetRoot, string rustExe,
        CpuBudgetJob parentBudget, CancellationToken token)
    {
        if (!parentBudget.SilentBreakawayVerified || !Path.IsPathFullyQualified(corpusRoot) || !Path.IsPathFullyQualified(appExe) ||
            !Path.IsPathFullyQualified(assetRoot) || !Path.IsPathFullyQualified(rustExe)) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify(appExe, assetRoot, rustExe, token);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        FrozenRealCorpusProject[] selected = selector.EnumerateSelectedProjects(token)
            .OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (selected.Length != Denominator || selected.Any(project => project.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var totals = new Totals();
        var wall = Stopwatch.StartNew();
        await using var ocr = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        foreach (FrozenRealCorpusProject project in selected)
        {
            if (token.IsCancellationRequested)
            {
                totals.Reason("OVERALL_DEADLINE_UNATTEMPTED", Denominator - totals.Attempted);
                break;
            }
            totals.Attempted++;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var elapsed = Stopwatch.StartNew();
            try
            {
                Payload payload = ReadLocked(selector, project, digest, totals, deadline.Token);
                if (payload.Width is < 32 or > 4096 || payload.Height is < 32 or > 4096 ||
                    (long)payload.Width * payload.Height > 16_777_216)
                { totals.Reason("UNSUPPORTED_IMAGE_DIMENSIONS"); continue; }
                // This helper has no manual anchors or values in its signature.
                var (proposal, draft) = await InferAsync(payload.Image, payload.Width, payload.Height,
                    payload.ProjectHash, payload.ImageHash, rustExe, ocr, candidate, totals, deadline.Token).ConfigureAwait(false);
                // Manual calibration is evaluator-only and first used AFTER inference proposals exist.
                if (proposal is { IsComplete: true })
                {
                    totals.Complete++;
                    AddResiduals(payload.Anchors, proposal, totals.XResiduals, totals.YResiduals);
                    totals.Scored++;
                }
                if (draft is { IsComplete: true, UsesEstimatedPositions: true })
                {
                    totals.EstimatedComplete++;
                    AddResiduals(payload.Anchors, draft, totals.EstimatedXResiduals, totals.EstimatedYResiduals);
                    totals.EstimatedScored++;
                }
            }
            catch (OperationCanceledException)
            { totals.Reason(token.IsCancellationRequested ? "OVERALL_DEADLINE_CASE" : "CASE_TIMEOUT"); }
            catch (EngaugeDigTruthException) { totals.Reason("DIG_PARSE_FAILED"); }
            catch (InvalidDataException) { totals.Reason("INPUT_OR_RUNTIME_IDENTITY_FAILED"); }
            catch (Exception) { totals.Reason("CASE_PROCESSING_FAILED"); }
            finally
            {
                totals.CaseMilliseconds.Add(elapsed.Elapsed.TotalMilliseconds);
                SampleMemory(totals, 0);
            }
        }
        _ = selector.EnumerateSelectedProjects(CancellationToken.None); // Metadata-only final identity check.
        wall.Stop();
        object report = Report(totals, Convert.ToHexStringLower(digest.GetHashAndReset()), metadata,
            candidate.Files, new(parentBudget.LogicalProcessors, parentBudget.Rate, parentBudget.MaximumLogicalCores, parentBudget.SilentBreakawayVerified),
            wall.Elapsed.TotalMilliseconds);
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(report, Program.Json));
        return report;
    }

    internal static async Task<object> RunGeometryAsync(string corpusRoot, string appExe, string assetRoot, string rustExe,
        CpuBudgetJob parentBudget, CancellationToken token)
    {
        if (!parentBudget.SilentBreakawayVerified || !Path.IsPathFullyQualified(corpusRoot) || !Path.IsPathFullyQualified(appExe) ||
            !Path.IsPathFullyQualified(assetRoot) || !Path.IsPathFullyQualified(rustExe)) throw new InvalidDataException();
        using Candidate candidate = Candidate.Verify(appExe, assetRoot, rustExe, token);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        FrozenRealCorpusProject[] selected = selector.EnumerateSelectedProjects(token)
            .OrderBy(project => project.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (selected.Length != Denominator || selected.Any(project => project.Split != FrozenRealCorpusInventory.RealDev))
            throw new InvalidDataException();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var totals = new Totals();
        var widths = new List<double>(); var heights = new List<double>();
        var plotWidths = new List<double>(); var plotHeights = new List<double>();
        int[] xTicks = new int[4], yTicks = new int[4];
        var wall = Stopwatch.StartNew();
        foreach (FrozenRealCorpusProject project in selected)
        {
            if (token.IsCancellationRequested)
            { totals.Reason("OVERALL_DEADLINE_UNATTEMPTED", Denominator - totals.Attempted); break; }
            totals.Attempted++;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                Payload payload = ReadLocked(selector, project, digest, totals, deadline.Token);
                widths.Add(payload.Width); heights.Add(payload.Height);
                if (payload.Width is < 32 or > 4096 || payload.Height is < 32 or > 4096 ||
                    (long)payload.Width * payload.Height > 16_777_216)
                { totals.Reason("UNSUPPORTED_IMAGE_DIMENSIONS"); continue; }
                var (image, _, _) = Program.Decode(payload.Image, [0, 0, payload.Width, payload.Height], deadline.Token);
                RustAxisProposalResult axis = await RustAxisProposal.RunAsync(rustExe, payload.Width, payload.Height, image.Pixels,
                    cancellationToken: deadline.Token).ConfigureAwait(false);
                if (axis.Geometry is not { } geometry)
                { totals.Reason(AxisReason(axis.ErrorCode)); continue; }
                totals.GeometryReturned++;
                plotWidths.Add(geometry.Right - geometry.Left); plotHeights.Add(geometry.Bottom - geometry.Top);
                xTicks[Math.Min(3, geometry.XTickPositions.Count)]++;
                yTicks[Math.Min(3, geometry.YTickPositions.Count)]++;
            }
            catch (OperationCanceledException)
            { totals.Reason(token.IsCancellationRequested ? "OVERALL_DEADLINE_CASE" : "CASE_TIMEOUT"); }
            catch (EngaugeDigTruthException) { totals.Reason("DIG_PARSE_FAILED"); }
            catch (InvalidDataException) { totals.Reason("INPUT_OR_RUNTIME_IDENTITY_FAILED"); }
            catch (Exception) { totals.Reason("CASE_PROCESSING_FAILED"); }
        }
        _ = selector.EnumerateSelectedProjects(CancellationToken.None);
        object report = GeometryReport(totals, Convert.ToHexStringLower(digest.GetHashAndReset()), metadata, candidate.Files,
            new(parentBudget.LogicalProcessors, parentBudget.Rate, parentBudget.MaximumLogicalCores, parentBudget.SilentBreakawayVerified),
            widths, heights, plotWidths, plotHeights, xTicks, yTicks, wall.Elapsed.TotalMilliseconds);
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(report, Program.Json));
        return report;
    }

    private static object GeometryReport(Totals totals, string digest, FrozenDevTrainingMetadata metadata,
        Dictionary<string, string> files, ParentBudget budget, List<double> widths, List<double> heights,
        List<double> plotWidths, List<double> plotHeights, int[] xTicks, int[] yTicks, double wall) => new
    {
        schema = "graphreader.real-dev-geometry-properties-diagnostic.v1",
        status = totals.Attempted == Denominator && !totals.Reasons.ContainsKey("OVERALL_DEADLINE_CASE") ? "completed" : "incomplete_deadline",
        protocol = new { version = "0.4.46", scope = "all_frozen_real_dev", sealed_payload_reads = 0,
            ocr_calls = 0, model_selection = false, review_only = true, production_approved = false,
            per_case_output = false, image_writes = false, persistent_cache = false,
            empty_ticks_mean = "unavailable_or_ambiguous_not_absence", case_deadline_seconds = 45, overall_deadline_seconds = 900 },
        candidate = new { app_exe_sha256 = AppExeHash, app_dll_sha256 = AppDllHash, rust_sha256 = RustHash, runtime_files = files },
        metadata.AssignmentSha256, metadata.SelectedInventorySha256, source_identity_digest = digest,
        source_identity_digest_complete = totals.PayloadsHashed == Denominator && totals.ImagesBound == Denominator,
        parent_cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            budget.SilentBreakawayVerified, priority = "BelowNormal", scope = "parent_preprocessing_only_children_independently_capped" },
        denominator = Denominator, attempted = totals.Attempted, payloads_hashed = totals.PayloadsHashed,
        embedded_images_bound = totals.ImagesBound, geometry_returned = totals.GeometryReturned,
        geometry_not_returned = Denominator - totals.GeometryReturned,
        reason_counts = totals.Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
        dimensions = new { image_width = Stats(widths), image_height = Stats(heights), plot_width = Stats(plotWidths), plot_height = Stats(plotHeights) },
        tick_counts = new { denominator = totals.GeometryReturned,
            x = new { zero = xTicks[0], one = xTicks[1], two = xTicks[2], three_or_more = xTicks[3] },
            y = new { zero = yTicks[0], one = yTicks[1], two = yTicks[2], three_or_more = yTicks[3] } },
        total_wall_milliseconds = wall,
    };

    private sealed record Payload(byte[] Image, int Width, int Height, string ProjectHash, string ImageHash,
        IReadOnlyList<EngaugeDigAxisAnchor> Anchors);

    private static Payload ReadLocked(FrozenDevTrainingSelector selector, FrozenRealCorpusProject project,
        IncrementalHash digest, Totals totals, CancellationToken token)
    {
        _ = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
        using var stream = new FileStream(project.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes) throw new InvalidDataException();
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
        string before = HashStream(stream, token);
        digest.AppendData("project"u8);
        digest.AppendData(Convert.FromHexString(before));
        totals.PayloadsHashed++;
        stream.Position = 0;
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(stream, project.AnonymizedCaseId, () =>
        {
            _ = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
        }, token);
        stream.Position = 0;
        string after = HashStream(stream, token);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
        if (before != truth.ProjectSha256 || before != after) throw new InvalidDataException();
        digest.AppendData("image"u8);
        digest.AppendData(Convert.FromHexString(truth.SourceImageSha256));
        totals.ImagesBound++;
        return new(truth.CopyImageBytes(), truth.SourceWidth, truth.SourceHeight, truth.ProjectSha256,
            truth.SourceImageSha256, truth.Anchors.ToArray());
    }

    private static async Task<(CalibrationTickProposalResult? Measured, CalibrationTickProposalResult? Draft)> InferAsync(byte[] encoded, int width, int height,
        string projectHash, string imageHash, string rustExe, CalibrationOcrWorkerClient ocr,
        Candidate candidate, Totals totals, CancellationToken token)
    {
        var stage = Stopwatch.StartNew();
        var (image, _, _) = Program.Decode(encoded, [0, 0, width, height], token);
        totals.DecodeMilliseconds.Add(stage.Elapsed.TotalMilliseconds);
        SampleMemory(totals, 0);
        stage.Restart();
        RustAxisProposalResult axis = await RustAxisProposal.RunAsync(rustExe, width, height, image.Pixels,
            cancellationToken: token).ConfigureAwait(false);
        totals.GeometryMilliseconds.Add(stage.Elapsed.TotalMilliseconds);
        if (axis.Geometry is not { } geometry)
        { totals.Reason(AxisReason(axis.ErrorCode)); return (null, null); }
        totals.GeometryReturned++;
        var request = new CalibrationOcrRequest("fixed-real-dev-assistance", projectHash, projectHash, imageHash,
            width, height, new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
        { MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null };
        CalibrationOcrResponse response = await ocr.SuggestAsync(request, encoded, token).ConfigureAwait(false);
        if (response.FailureCode is not null)
        { totals.Reason(response.FailureCode == CalibrationOcrReasonCodes.Timeout ? "OCR_TIMEOUT" : "OCR_READ_FAILED"); return (null, null); }
        CalibrationOcrRuntime runtime = response.Runtime!;
        if (runtime.Provider != "Cpu" || runtime.RequestedProvider != "Cpu" || runtime.ModelSha256 != Program.ModelHash ||
            runtime.ManifestSha256 != Program.ManifestHash || runtime.Files.Any(file => !candidate.Files.TryGetValue(file.Name, out string? expected) || file.Sha256 != expected))
            throw new InvalidDataException();
        totals.OcrReturned++;
        totals.NumericRows += response.Proposals.Count(row => row.NumericLiteral);
        CalibrationOcrTimings timings = response.Timings!;
        (timings.ReusedSession ? totals.ReusedOcrMilliseconds : totals.ColdOcrMilliseconds).Add(timings.TotalMilliseconds);
        totals.InitializationMilliseconds.Add(timings.InitializationMilliseconds);
        totals.RecognitionMilliseconds.Add(timings.RecognitionMilliseconds);
        SampleMemory(totals, Math.Max(runtime.Memory.PrivateBytesBefore, runtime.Memory.PrivateBytesAfter));
        stage.Restart();
        CalibrationTickProposalResult result = CalibrationTickProposal.Create(geometry, response);
        CalibrationTickProposalResult draft = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        CountPartialAxes(totals, draft.XReviewDraft is not null, draft.YReviewDraft is not null);
        totals.PairingMilliseconds.Add(stage.Elapsed.TotalMilliseconds);
        if (!result.IsComplete)
        {
            totals.Incomplete++;
            foreach (string reason in result.Reasons.Distinct(StringComparer.Ordinal)) totals.Reason(TickReason(reason));
        }
        if (!draft.IsComplete)
            foreach (string reason in draft.Reasons.Distinct(StringComparer.Ordinal))
            {
                string key = TickReason(reason);
                totals.DraftReasons[key] = totals.DraftReasons.GetValueOrDefault(key) + 1;
            }
        return (result, draft);
    }

    private static string AxisReason(string? reason) => reason switch
    {
        "NO_AXIS" => "AXIS_NOT_FOUND", "AMBIGUOUS_AXIS" => "AXIS_AMBIGUOUS", "NO_INTERSECTION" => "AXIS_NO_INTERSECTION",
        "WORKER_TIMEOUT" => "AXIS_TIMEOUT", "CPU_BUDGET_UNAVAILABLE" => "AXIS_CPU_BUDGET_UNAVAILABLE",
        _ => "AXIS_FAILED",
    };

    private static string TickReason(string reason) => reason switch
    {
        CalibrationTickProposalReasons.InvalidEvidence => "TICK_INVALID_EVIDENCE",
        CalibrationTickProposalReasons.InvalidMeasuredTicks => "TICK_INVALID_MEASUREMENTS",
        CalibrationTickProposalReasons.InsufficientPairs => "TICK_INSUFFICIENT_PAIRS",
        CalibrationTickProposalReasons.InvalidNumericLabel => "TICK_INVALID_NUMERIC_LABEL",
        CalibrationTickProposalReasons.WrongAxis => "TICK_WRONG_AXIS",
        CalibrationTickProposalReasons.UnpairedLabel => "TICK_UNPAIRED_LABEL",
        CalibrationTickProposalReasons.AmbiguousAssociation => "TICK_AMBIGUOUS_ASSOCIATION",
        CalibrationTickProposalReasons.DuplicateAssociation => "TICK_DUPLICATE_ASSOCIATION",
        CalibrationTickProposalReasons.MissingEndpoint => "TICK_MISSING_ENDPOINT",
        CalibrationTickProposalReasons.ObservedZeroRequired => "TICK_ZERO_NOT_OBSERVED",
        CalibrationTickProposalReasons.InvalidRange => "TICK_INVALID_RANGE",
        CalibrationTickProposalReasons.Nonmonotonic => "TICK_NONMONOTONIC",
        CalibrationTickProposalReasons.InconsistentSpacing => "TICK_INCONSISTENT_SPACING",
        _ => "TICK_PAIRING_INCOMPLETE",
    };

    private static void AddResiduals(IReadOnlyList<EngaugeDigAxisAnchor> anchors, CalibrationTickProposalResult proposal,
        List<double> xResiduals, List<double> yResiduals)
    {
        if (anchors.Count != 3 || proposal.Anchors is not { } points || proposal.FirstSession is not { } first ||
            proposal.FinalSession is not { } last || proposal.YMaximum is not { } maximum) throw new InvalidDataException();
        double xRange = anchors.Max(anchor => anchor.GraphX) - anchors.Min(anchor => anchor.GraphX);
        double yRange = anchors.Max(anchor => anchor.GraphY) - anchors.Min(anchor => anchor.GraphY);
        if (!double.IsFinite(xRange) || !double.IsFinite(yRange) || !(xRange > 0) || !(yRange > 0)) throw new InvalidDataException();
        var localX = new List<double>(3);
        var localY = new List<double>(3);
        (CalibrationOcrPoint Point, double X, double Y)[] evaluated =
            [(points.FirstSessionY0, first, 0), (points.FirstSessionYMaximum, first, maximum), (points.FinalSessionY0, last, 0)];
        foreach (var row in evaluated)
        {
            var a = anchors[0]; var b = anchors[1]; var c = anchors[2];
            double determinant = (b.ScreenX - a.ScreenX) * (c.ScreenY - a.ScreenY) - (c.ScreenX - a.ScreenX) * (b.ScreenY - a.ScreenY);
            double dx = row.Point.X - a.ScreenX, dy = row.Point.Y - a.ScreenY;
            double u = (dx * (c.ScreenY - a.ScreenY) - dy * (c.ScreenX - a.ScreenX)) / determinant;
            double v = ((b.ScreenX - a.ScreenX) * dy - (b.ScreenY - a.ScreenY) * dx) / determinant;
            double graphX = a.GraphX + u * (b.GraphX - a.GraphX) + v * (c.GraphX - a.GraphX);
            double graphY = a.GraphY + u * (b.GraphY - a.GraphY) + v * (c.GraphY - a.GraphY);
            double x = Math.Abs(graphX - row.X) / xRange, y = Math.Abs(graphY - row.Y) / yRange;
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException();
            localX.Add(x); localY.Add(y);
        }
        xResiduals.AddRange(localX); yResiduals.AddRange(localY);
    }

    private static void SampleMemory(Totals totals, long workerBytes)
    {
        using Process process = Process.GetCurrentProcess();
        totals.PeakSampledParentBytes = Math.Max(totals.PeakSampledParentBytes, process.PrivateMemorySize64);
        totals.PeakParentWorkingSetBytes = Math.Max(totals.PeakParentWorkingSetBytes, process.PeakWorkingSet64);
        totals.PeakSampledCombinedBytes = Math.Max(totals.PeakSampledCombinedBytes, process.PrivateMemorySize64 + workerBytes);
    }

    private sealed class Totals
    {
        internal int Attempted, PayloadsHashed, ImagesBound, GeometryReturned, OcrReturned, NumericRows, Complete, Incomplete, Scored, EstimatedComplete, EstimatedScored, AxisEvaluated, PartialX, PartialY, PartialBoth, PartialNeither;
        internal long PeakSampledParentBytes, PeakParentWorkingSetBytes, PeakSampledCombinedBytes;
        internal Dictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> DraftReasons { get; } = new(StringComparer.Ordinal);
        internal List<double> CaseMilliseconds { get; } = [];
        internal List<double> DecodeMilliseconds { get; } = [];
        internal List<double> GeometryMilliseconds { get; } = [];
        internal List<double> ColdOcrMilliseconds { get; } = [];
        internal List<double> ReusedOcrMilliseconds { get; } = [];
        internal List<double> InitializationMilliseconds { get; } = [];
        internal List<double> RecognitionMilliseconds { get; } = [];
        internal List<double> PairingMilliseconds { get; } = [];
        internal List<double> XResiduals { get; } = [];
        internal List<double> YResiduals { get; } = [];
        internal List<double> EstimatedXResiduals { get; } = [];
        internal List<double> EstimatedYResiduals { get; } = [];
        internal void Reason(string key, int count = 1) { if (count > 0) Reasons[key] = Reasons.GetValueOrDefault(key) + count; }
    }

    private static void CountPartialAxes(Totals totals, bool x, bool y)
    {
        totals.AxisEvaluated++;
        if (x && y) totals.PartialBoth++;
        else if (x) totals.PartialX++;
        else if (y) totals.PartialY++;
        else totals.PartialNeither++;
    }

    private static object Stats(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return new { count = sorted.Length, mean = sorted.Length == 0 ? (double?)null : sorted.Average(),
            median = sorted.Length == 0 ? (double?)null : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2,
            p95 = sorted.Length == 0 ? (double?)null : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95) - 1)],
            maximum = sorted.Length == 0 ? (double?)null : sorted[^1] };
    }

    private sealed record ParentBudget(uint LogicalProcessors, uint Rate, double MaximumLogicalCores, bool SilentBreakawayVerified = false);

    private static object Report(Totals totals, string digest, FrozenDevTrainingMetadata metadata,
        Dictionary<string, string> files, ParentBudget budget, double wall) => new
    {
        schema = "graphreader.real-dev-calibration-assistance-diagnostic.v1",
        status = totals.Attempted == Denominator && !totals.Reasons.ContainsKey("OVERALL_DEADLINE_CASE") ? "completed" : "incomplete_deadline",
        protocol = new { fixed_candidate = true, version = "0.4.46", denominator = Denominator,
            scope = "all_frozen_real_dev", sealed_payload_reads = 0, train_optimizer_steps = 0, model_selection = false,
            independent_accuracy = false, production_approved = false, review_only = true, applied = false,
            manual_truth_used_for_inference = false, case_deadline_seconds = 45, overall_deadline_seconds = 900,
            source_identity_policy = "first_observed_locked_payloads_not_preregistered_content",
            known_dev_exposure = true, imperfect_manual_truth = true, tick_transcription_truth_available = false,
            residual_definition = "absolute affine graph error divided by manual anchor graph range; no pass threshold",
            per_case_output = false, image_writes = false, persistent_cache = false,
            reason_counts_are_not_mutually_exclusive = true },
        candidate = new { app_exe_sha256 = AppExeHash, app_dll_sha256 = AppDllHash, rust_sha256 = RustHash,
            reader_sha256 = Program.ModelHash, manifest_sha256 = Program.ManifestHash, runtime_files = files },
        metadata.AssignmentSha256, metadata.SelectedInventorySha256, source_identity_digest = digest,
        source_identity_digest_complete = totals.PayloadsHashed == Denominator,
        parent_cpu_budget = new { budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            budget.SilentBreakawayVerified, priority = "BelowNormal", scope = "parent_preprocessing_only_children_independently_capped" },
        denominator = Denominator, attempted = totals.Attempted, payloads_hashed = totals.PayloadsHashed,
        embedded_images_bound = totals.ImagesBound, geometry_returned = totals.GeometryReturned, ocr_returned = totals.OcrReturned,
        numeric_reading_count = totals.NumericRows, complete_proposal_count = totals.Complete,
        complete_proposal_fraction = totals.Complete / (double)Denominator, incomplete_pairings = totals.Incomplete,
        scored_complete_proposals = totals.Scored, failure_or_incomplete_count = Denominator - totals.Complete,
        reason_counts = totals.Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
        normalized_x_residual = Stats(totals.XResiduals), normalized_y_residual = Stats(totals.YResiduals),
        axis_review_availability = new { scope = "ocr_responses_with_review_draft_evaluated_including_complete_proposals",
            denominator = Denominator, ocr_response_count = totals.OcrReturned, evaluated_count = totals.AxisEvaluated,
            unevaluated_ocr_responses = totals.OcrReturned - totals.AxisEvaluated,
            valid_x = totals.PartialX + totals.PartialBoth, valid_y = totals.PartialY + totals.PartialBoth,
            x_only = totals.PartialX, y_only = totals.PartialY, both = totals.PartialBoth, neither = totals.PartialNeither,
            accuracy_or_click_savings_claimed = false },
        estimated_review_draft = new { complete_count = totals.EstimatedComplete, scored_count = totals.EstimatedScored,
            scope = "complete_drafts_using_estimated_positions_only", denominator = Denominator,
            evaluated_ocr_response_count = totals.OcrReturned,
            incomplete_reason_scope = "all_ocr_responses_with_incomplete_review_drafts",
            incomplete_reason_counts = totals.DraftReasons.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            normalized_x_residual = Stats(totals.EstimatedXResiduals), normalized_y_residual = Stats(totals.EstimatedYResiduals),
            reason_counts_are_not_mutually_exclusive = true },
        timing_milliseconds = new { total_wall = wall, case_total = Stats(totals.CaseMilliseconds), decode = Stats(totals.DecodeMilliseconds),
            geometry = Stats(totals.GeometryMilliseconds), cold_ocr = Stats(totals.ColdOcrMilliseconds), reused_ocr = Stats(totals.ReusedOcrMilliseconds),
            initialization = Stats(totals.InitializationMilliseconds), recognition = Stats(totals.RecognitionMilliseconds), pairing = Stats(totals.PairingMilliseconds) },
        memory = new { totals.PeakSampledParentBytes, totals.PeakParentWorkingSetBytes, totals.PeakSampledCombinedBytes,
            sampling_is_not_a_hard_memory_ceiling = true },
    };

    private static string HashStream(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65_536];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed class Candidate : IDisposable
    {
        private readonly List<FileStream> held = [];
        internal Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        internal static Candidate Verify(string app, string assets, string rust, CancellationToken token)
        {
            var result = new Candidate();
            try
            {
                result.Pin(app, AppExeHash, token);
                string directory = Path.GetDirectoryName(app)!;
                result.Pin(typeof(CalibrationTickProposal).Assembly.Location, AppDllHash, token);
                // This adapter requires the verified white-background helper. Older DLLs
                // must fail before private payload selection, even if pins are rebound.
                RequireRasterCapability(typeof(CalibrationTickProposal).Assembly);
                RequireReviewDraftCapability(typeof(CalibrationTickProposal).Assembly);
                result.Pin(rust, RustHash, token);
                result.Pin(Path.Combine(assets, "en_PP-OCRv5_mobile_rec.onnx"), Program.ModelHash, token);
                result.Pin(Path.Combine(assets, "recognizer.json"), Program.ManifestHash, token);
                result.Pin(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt"), "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4", token);
                result.Pin(Path.Combine(assets, "PaddlePaddle-PP-OCRv5-Models-Notice.txt"), "8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729", token);
                foreach (var library in DeliveredLibraries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    result.Pin(Path.Combine(directory, library.Key), library.Value, token);
                    bool managed = library.Key.StartsWith("GraphReader.", StringComparison.Ordinal) ||
                        library.Key is "OpenCvSharp.dll" or "Microsoft.ML.OnnxRuntime.dll";
                    string name = managed ? Path.GetFileNameWithoutExtension(library.Key) : library.Key;
                    if (managed)
                    {
                        Assembly loaded = Assembly.Load(new AssemblyName(name));
                        result.Pin(loaded.Location, library.Value, token);
                    }
                    result.Files[name] = library.Value;
                }
                result.Files["diagnostic_adapter"] = result.Pin(typeof(RealDevCalibrationCheck).Assembly.Location, null, token);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        private string Pin(string path, string? expected, CancellationToken token)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan);
            held.Add(stream);
            string hash = HashStream(stream, token);
            if (expected is not null && expected != hash) throw new InvalidDataException();
            return hash;
        }
        public void Dispose() { foreach (FileStream stream in held) stream.Dispose(); GC.SuppressFinalize(this); }
    }

    private static void RequireRasterCapability(Assembly assembly)
    {
        Type? type = assembly.GetType("GraphReader.App.Integration.WhiteBackgroundRaster", throwOnError: false);
        MethodInfo? method = type?.GetMethod("Normalize", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, types: [typeof(System.Windows.Media.Imaging.BitmapSource), typeof(CancellationToken)], modifiers: null);
        if (method?.ReturnType != typeof(System.Windows.Media.Imaging.BitmapSource)) throw new InvalidDataException();
    }

    private static void RequireReviewDraftCapability(Assembly assembly)
    {
        Type? type = assembly.GetType("GraphReader.App.Services.CalibrationTickProposal", throwOnError: false);
        MethodInfo? method = type?.GetMethod("CreateReviewDraft", BindingFlags.Static | BindingFlags.Public,
            binder: null, types: [typeof(RustAxisGeometry), typeof(CalibrationOcrResponse)], modifiers: null);
        Type? result = assembly.GetType("GraphReader.App.Services.CalibrationTickProposalResult", throwOnError: false);
        Type? request = assembly.GetType("GraphReader.App.Services.CalibrationOcrRequest", throwOnError: false);
        if (result is null || method?.ReturnType != result || result.GetProperty("UsesEstimatedPositions")?.PropertyType != typeof(bool) ||
            result.GetProperty("XReviewDraft") is null || result.GetProperty("YReviewDraft") is null ||
            request?.GetProperty("MeasuredXTicks")?.PropertyType != typeof(int[]))
            throw new InvalidDataException();
    }

    private static void EnsureAggregateOnly(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (ForbiddenProperties.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException();
                EnsureAggregateOnly(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) EnsureAggregateOnly(item);
    }

    internal static object SelfTest()
    {
        RequireRasterCapability(typeof(CalibrationTickProposal).Assembly);
        RequireReviewDraftCapability(typeof(CalibrationTickProposal).Assembly);
        bool capabilityRejected = false;
        bool payloadSelectionReached = false;
        try
        {
            RequireRasterCapability(typeof(string).Assembly); // Owned assembly deliberately lacks the helper.
            payloadSelectionReached = true;
        }
        catch (InvalidDataException) { capabilityRejected = true; }
        if (!capabilityRejected || payloadSelectionReached) throw new InvalidDataException();
        EngaugeDigAxisAnchor[] anchors = [new(10, 90, 1, 0), new(10, 10, 1, 10), new(90, 90, 5, 0)];
        var proposal = new CalibrationTickProposalResult(new(new(10, 90), new(10, 10), new(90, 90)), 1, 5, 10, [], []);
        var x = new List<double>(); var y = new List<double>();
        AddResiduals(anchors, proposal, x, y);
        if (x.Count != 3 || y.Count != 3 || x.Any(value => value != 0) || y.Any(value => value != 0)) throw new InvalidDataException();
        x.Clear(); y.Clear();
        AddResiduals(anchors, proposal with { Anchors = proposal.Anchors! with { FirstSessionYMaximum = new(10, 14) } }, x, y);
        if (Math.Abs(y[1] - .05) > 1e-12) throw new InvalidDataException();
        bool rejected = false;
        try { EnsureAggregateOnly(JsonSerializer.SerializeToElement(new { text = "owned-sentinel" })); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidDataException();
        var totals = new Totals { Attempted = Denominator, PayloadsHashed = Denominator, Complete = 1, Scored = 1 };
        totals.XResiduals.AddRange(x); totals.YResiduals.AddRange(y);
        totals.Reason("OWNED_SELF_TEST", Denominator - 1);
        object aggregate = Report(totals, new string('0', 64), new(new string('1', 64), new string('2', 64), [], false, false),
            new Dictionary<string, string>(StringComparer.Ordinal), new(2, 2500, .5), 1);
        totals.OcrReturned = 4;
        CountPartialAxes(totals, true, false); CountPartialAxes(totals, false, true);
        CountPartialAxes(totals, true, true); CountPartialAxes(totals, false, false);
        totals.EstimatedComplete = 1; totals.EstimatedScored = 1;
        totals.EstimatedXResiduals.AddRange(x); totals.EstimatedYResiduals.AddRange(y);
        totals.DraftReasons["TICK_INSUFFICIENT_PAIRS"] = 2;
        aggregate = Report(totals, new string('0', 64), new(new string('1', 64), new string('2', 64), [], false, false),
            new Dictionary<string, string>(StringComparer.Ordinal), new(2, 2500, .5), 1);
        JsonElement serialized = JsonSerializer.SerializeToElement(aggregate, Program.Json);
        EnsureAggregateOnly(serialized);
        if (serialized.GetProperty("denominator").GetInt32() != Denominator ||
            serialized.GetProperty("complete_proposal_count").GetInt32() != 1 ||
            serialized.GetProperty("axis_review_availability").GetProperty("evaluated_count").GetInt32() != 4 ||
            serialized.GetProperty("axis_review_availability").GetProperty("valid_x").GetInt32() != 2 ||
            serialized.GetProperty("axis_review_availability").GetProperty("valid_y").GetInt32() != 2 ||
            serialized.GetProperty("axis_review_availability").GetProperty("x_only").GetInt32() != 1 ||
            serialized.GetProperty("axis_review_availability").GetProperty("y_only").GetInt32() != 1 ||
            serialized.GetProperty("axis_review_availability").GetProperty("both").GetInt32() != 1 ||
            serialized.GetProperty("axis_review_availability").GetProperty("neither").GetInt32() != 1 ||
            serialized.GetProperty("estimated_review_draft").GetProperty("complete_count").GetInt32() != 1 ||
            serialized.GetProperty("estimated_review_draft").GetProperty("normalized_x_residual").GetProperty("count").GetInt32() != 3 ||
            serialized.GetProperty("normalized_x_residual").GetProperty("count").GetInt32() != 3 ||
            JsonSerializer.SerializeToUtf8Bytes(aggregate, Program.Json).Length >= 1_000_000) throw new InvalidDataException();
        var geometryTotals = new Totals { Attempted = Denominator, PayloadsHashed = Denominator, ImagesBound = Denominator, GeometryReturned = 1 };
        object geometry = GeometryReport(geometryTotals, new string('0', 64), new(new string('1', 64), new string('2', 64), [], false, false),
            new Dictionary<string, string>(StringComparer.Ordinal), new(2, 2500, .5), [320], [200], [230], [130], [0, 1, 0, 0], [1, 0, 0, 0], 1);
        JsonElement geometryJson = JsonSerializer.SerializeToElement(geometry, Program.Json);
        EnsureAggregateOnly(geometryJson);
        if (geometryJson.GetProperty("denominator").GetInt32() != Denominator ||
            geometryJson.GetProperty("tick_counts").GetProperty("x").GetProperty("one").GetInt32() != 1 ||
            JsonSerializer.SerializeToUtf8Bytes(geometry, Program.Json).Length >= 1_000_000) throw new InvalidDataException();
        var output = new { schema = "graphreader.real-dev-calibration-assistance-self-test.v1", source_payload_reads = 0,
            affine_residual_checks = 2, forbidden_output_rejected = rejected, missing_capability_rejected_before_selection = capabilityRejected, fixed_denominator = Denominator };
        EnsureAggregateOnly(JsonSerializer.SerializeToElement(output, Program.Json));
        return output;
    }
}
