// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class OwnedNativeSearchWorkCheck
{
    private sealed class Observer : IAxisGeometryCandidateObserver, IAxisGeometryWorkObserver
    {
        internal AxisGeometryCandidateSnapshot[] Candidates { get; private set; } = [];
        internal AxisGeometryWorkSnapshot? Work { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates.ToArray();
        public void OnWorkObserved(AxisGeometryWorkSnapshot snapshot) => Work = snapshot;
    }

    private sealed record Plan(string Name, int Width, int Height, GeometryLineCandidate[] Lines,
        int? Bound, int ExpectedPairs, bool ExpectRefusal);
    private sealed record Outcome(Plan Plan, string Status, AxisGeometryResult? Result, Observer Observer,
        string? ExceptionType, AxisGeometryFailureReason? FailureReason, AxisGeometryBudgetKind BudgetKind,
        double WallMilliseconds, double CpuMilliseconds, long AllocatedBytes,
        long WorkingSetBefore, long WorkingSetAfter, long LifetimePeakBefore, long LifetimePeakAfter);

    internal static async Task<object> RunAsync(CpuBudgetJob budget, CancellationToken token)
    {
        if (budget.MaximumLogicalCores is <= 0 or > .5 || !budget.SilentBreakawayVerified)
            throw new InvalidOperationException("Independent parent CPU budget required.");

        // Families retain their observation insertion order. The leftmost line is
        // deliberately last; it has the largest forward span and meeting score.
        GeometryLineCandidate[] full = Fixture(129, 1700, 700, 100);
        GeometryLineCandidate[] prefix = full.Take(129).ToArray();
        Plan[] plans = [
            new("prefix_128_complete_reference", 1800, 800, prefix, null, 128, false),
            new("late_winner_129_bound_128", 1800, 800, full, 128, 129, true),
            new("late_winner_129_complete_reference", 1800, 800, full, null, 129, false),
            new("late_winner_129_complete_bounded", 1800, 800, full, 129, 129, false),
            new("dense_257_bound_129", 3200, 1200, Fixture(257, 3000, 1000, 100), 129, 257, true),
        ];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var outcomes = new List<Outcome>(plans.Length);
        var violations = new List<string>();
        foreach (Plan plan in plans)
        {
            if (deadline.IsCancellationRequested) break;
            Outcome outcome = await MeasureAsync(plan, deadline.Token).ConfigureAwait(false);
            outcomes.Add(outcome);
            ValidateOutcome(outcome, violations);
            if (outcome.Status == "cancelled") break;
        }

        bool lateWinnerVerified = false, completeParity = false;
        Outcome? prefixResult = outcomes.Find(item => item.Plan.Name == plans[0].Name);
        Outcome? reference = outcomes.Find(item => item.Plan.Name == plans[2].Name);
        Outcome? bounded = outcomes.Find(item => item.Plan.Name == plans[3].Name);
        if (prefixResult?.Status == "completed" && reference?.Status == "completed")
        {
            AxisGeometryCandidateSnapshot[] ranked = reference.Observer.Candidates;
            // Rank changes between the prefix and complete oracle; compare the
            // runner-up's identity independently of its ordinal.
            lateWinnerVerified = ranked.Length == 129 && prefixResult.Observer.Candidates.Length == 128 &&
                ranked[0].YSupportingCandidateIds.Count == 1 && ranked[0].YSupportingCandidateIds[0] == full[^1].CandidateId &&
                prefix.All(line => line.CandidateId != full[^1].CandidateId) &&
                ranked[0].Score > prefixResult.Observer.Candidates[0].Score &&
                PairIdentityHash(ranked[1]) == PairIdentityHash(prefixResult.Observer.Candidates[0]);
            if (!lateWinnerVerified) violations.Add("late_winner_not_proven");
        }
        else violations.Add("late_winner_reference_unavailable");
        if (reference?.Status == "completed" && bounded?.Status == "completed")
        {
            completeParity = Hash(reference.Observer.Candidates) == Hash(bounded.Observer.Candidates) &&
                ResultIdentityHash(reference.Result!) == ResultIdentityHash(bounded.Result!) &&
                Hash(reference.Result!.Uncertainty) == Hash(bounded.Result!.Uncertainty);
            if (!completeParity) violations.Add("complete_bounded_reference_mismatch");
        }
        else violations.Add("complete_parity_unavailable");
        if (outcomes.Count != plans.Length) violations.Add("unattempted_cases");

        using Process process = Process.GetCurrentProcess();
        return new {
            schema = "graphreader.owned-native-search-work.v1", scope = "owned_solver_only",
            safety_passed = violations.Count == 0, violations, late_winner_verified = lateWinnerVerified,
            complete_reference_parity = completeParity,
            planned = plans.Length, attempted = outcomes.Count,
            completed = outcomes.Count(item => item.Status == "completed"),
            refused = outcomes.Count(item => item.Status == "refused"),
            failed = outcomes.Count(item => item.Status == "exception"),
            cancelled = outcomes.Count(item => item.Status == "cancelled"),
            unattempted = plans.Skip(outcomes.Count).Select(item => item.Name).ToArray(),
            solver_wall_milliseconds = outcomes.Sum(item => item.WallMilliseconds),
            process_cpu_milliseconds = outcomes.Sum(item => item.CpuMilliseconds),
            process_allocated_bytes = outcomes.Sum(item => item.AllocatedBytes),
            budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
            priority_class = process.PriorityClass.ToString(),
            per_case_cooperative_deadline_seconds = 12, overall_cooperative_deadline_seconds = 60,
            actual_child_watchdog_used = false,
            limitations = "Solver and observers only; excludes OpenCV extraction, decoding, IPC and native end-to-end latency. Cooperative cancellation is not the child process hard watchdog. Allocation deltas are process-wide; peak working set is a process-lifetime high-water mark, not an isolated per-case peak. Bounds129 are diagnostic only; production budgets and choice caps are unchanged.",
            axis_dll_sha256 = HashFile(typeof(AxisGeometryDetector).Assembly.Location),
            cases = outcomes.Select(Describe).ToArray(), private_reads = 0, sealed_reads = 0,
        };
    }

    private static async Task<Outcome> MeasureAsync(Plan plan, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var observer = new Observer();
        var request = new AxisGeometryRequest(plan.Width, plan.Height, plan.Lines) {
            MaximumExaminedPairs = plan.Bound, CandidateObserver = observer, WorkObserver = observer,
        };
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingBefore = process.WorkingSet64, peakBefore = process.PeakWorkingSet64;
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        AxisGeometryResult? result = null;
        string status = "completed";
        string? exception = null;
        AxisGeometryFailureReason? reason = null;
        AxisGeometryBudgetKind kind = AxisGeometryBudgetKind.None;
        var timer = Stopwatch.StartNew();
        try { result = await new AxisGeometryDetector().DetectAsync(request, deadline.Token).ConfigureAwait(false); }
        catch (AxisGeometryDetectionException error)
        { status = "refused"; exception = error.GetType().Name; reason = error.Reason; kind = error.BudgetKind; }
        catch (OperationCanceledException) { status = "cancelled"; }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { status = "exception"; exception = error.GetType().Name; }
        timer.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        process.Refresh();
        return new(plan, status, result, observer, exception, reason, kind,
            timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, allocated,
            workingBefore, process.WorkingSet64, peakBefore, process.PeakWorkingSet64);
    }

    private static void ValidateOutcome(Outcome outcome, List<string> violations)
    {
        string prefix = outcome.Plan.Name + ":";
        AxisGeometryWorkSnapshot? work = outcome.Observer.Work;
        if (outcome.Plan.ExpectRefusal)
        {
            int bound = outcome.Plan.Bound!.Value;
            if (outcome.Status != "refused" || outcome.Result is not null || outcome.Observer.Candidates.Length != 0 ||
                outcome.FailureReason != AxisGeometryFailureReason.PairBudgetExceeded || outcome.BudgetKind != AxisGeometryBudgetKind.ExactPairs ||
                work is not { EnumerationCompleted: false } || work.ExactPairAttempts != bound ||
                work.ExactPairRequests != bound + 1 || work.AdmittedPairs != bound)
                violations.Add(prefix + "expected_exact_budget_refusal_without_prefix");
        }
        else if (outcome.Status != "completed" || outcome.Result is null ||
            outcome.Observer.Candidates.Length != outcome.Plan.ExpectedPairs || work is not { EnumerationCompleted: true } ||
            work.ExactPairAttempts != outcome.Plan.ExpectedPairs || work.AdmittedPairs != outcome.Plan.ExpectedPairs)
            violations.Add(prefix + "expected_complete_all_contacting_pairs");
    }

    private static object Describe(Outcome outcome) => new {
        name = outcome.Plan.Name, bound = outcome.Plan.Bound, line_count = outcome.Plan.Lines.Length,
        input_sha256 = Hash(outcome.Plan.Lines), status = outcome.Status,
        exception_type = outcome.ExceptionType, failure_reason = outcome.FailureReason?.ToString(),
        budget_kind = outcome.BudgetKind.ToString(), outcome.Observer.Work,
        candidate_count = outcome.Observer.Candidates.Length,
        candidate_identity_sha256 = outcome.Result is null ? null : Hash(outcome.Observer.Candidates),
        result_identity_sha256 = outcome.Result is null ? null : ResultIdentityHash(outcome.Result),
        best = outcome.Observer.Candidates.Length > 0 ? PairSummary(outcome.Observer.Candidates[0]) : null,
        runner_up = outcome.Observer.Candidates.Length > 1 ? PairSummary(outcome.Observer.Candidates[1]) : null,
        uncertainty = outcome.Result?.Uncertainty,
        wall_milliseconds = outcome.WallMilliseconds, process_cpu_milliseconds = outcome.CpuMilliseconds,
        process_allocated_bytes = outcome.AllocatedBytes, working_set_before_bytes = outcome.WorkingSetBefore,
        working_set_after_bytes = outcome.WorkingSetAfter,
        process_lifetime_peak_before_bytes = outcome.LifetimePeakBefore,
        process_lifetime_peak_after_bytes = outcome.LifetimePeakAfter,
    };

    private static object PairSummary(AxisGeometryCandidateSnapshot pair) => new {
        pair.Rank, pair.Score, pair.Intersection, pair.BottomRight, pair.TopLeft,
        pair.XSupportingCandidateIds, pair.YSupportingCandidateIds,
    };

    private static string PairIdentityHash(AxisGeometryCandidateSnapshot pair) => Hash(new {
        pair.Score, pair.Intersection, pair.XAxisLine, pair.YAxisLine, pair.BottomRight, pair.TopRight, pair.TopLeft,
        pair.XSpan, pair.YSpan, pair.XCoverageFraction, pair.YCoverageFraction,
        pair.XRootMeanSquareError, pair.YRootMeanSquareError, pair.XSupportingCandidateIds, pair.YSupportingCandidateIds,
    });

    private static string ResultIdentityHash(AxisGeometryResult result) => Hash(new {
        result.CoordinateSpace, result.PlotPolygon, result.XAxis, result.YAxis, result.Ticks,
        result.PhaseDividers, result.AmbiguousGridOrDividers, result.Confidence, result.Uncertainty,
    });

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Program.Json)));
    private static string HashFile(string path) { using Stream stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }

    private static GeometryLineCandidate[] Fixture(int count, int right, int bottom, int top)
    {
        var lines = new GeometryLineCandidate[count + 1];
        lines[0] = new("baseline", new(new(100, bottom), new(right, bottom)), LineCandidateSource.RecordedFixture);
        for (int i = 0; i < count; i++)
        {
            int x = 100 + 10 * (count - 1 - i);
            lines[i + 1] = new($"vertical-{i:D3}", new(new(x, bottom), new(x, top)), LineCandidateSource.RecordedFixture);
        }
        return lines;
    }
}
