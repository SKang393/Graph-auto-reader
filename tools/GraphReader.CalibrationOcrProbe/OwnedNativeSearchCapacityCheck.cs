// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class OwnedNativeSearchWorkCheck
{
    internal static async Task<object> RunCapacityAsync(CpuBudgetJob budget, CancellationToken token)
    {
        if (budget.MaximumLogicalCores is <= 0 or > .5 || !budget.SilentBreakawayVerified)
            throw new InvalidOperationException("Independent parent CPU budget required.");
        GeometryLineCandidate[] regular = Fixture(256, 4000, 3000, 1000);
        GeometryLineCandidate[] irregular = IrregularCapacityFixture();
        Plan[] plans = [
            new("regular_256_reference", 4096, 4096, regular, null, 256, false),
            new("regular_256_bounded", 4096, 4096, regular, 256, 256, false),
            new("irregular_256_reference", 4096, 4096, irregular, null, 256, false),
            new("irregular_256_bounded", 4096, 4096, irregular, 256, 256, false),
            new("dense_257_bound_256", 4096, 4096, Fixture(257, 4000, 3000, 1000), 256, 257, true),
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
        object[] comparisons = [CapacityParity(plans[0], plans[1], outcomes, violations),
            CapacityParity(plans[2], plans[3], outcomes, violations)];
        if (outcomes.Count != plans.Length) violations.Add("unattempted_cases");
        using Process process = Process.GetCurrentProcess();
        return new {
            schema = "graphreader.owned-native-search-capacity.v1", scope = "owned_solver_only",
            predeclared_diagnostic_bound = 256, production_cap_changed = false,
            safety_passed = violations.Count == 0, violations, comparisons,
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
            actual_child_watchdog_used = false, normal_review_cap_separation_verified = false,
            limitations = "Solver and observers only; no OpenCV extraction, decode, IPC or child process. Cooperative cancellation does not prove the native child hard watchdog or active child disposal. Allocation counters are process-wide; peak working set is a process-lifetime high-water mark. B256 is diagnostic only, with no sweep or production budget/choice-cap change.",
            axis_dll_sha256 = HashFile(typeof(AxisGeometryDetector).Assembly.Location),
            cases = outcomes.Select(Describe).ToArray(), private_reads = 0, sealed_reads = 0,
        };
    }

    private static object CapacityParity(Plan referencePlan, Plan boundedPlan, List<Outcome> outcomes, List<string> violations)
    {
        Outcome? reference = outcomes.Find(item => item.Plan.Name == referencePlan.Name);
        Outcome? bounded = outcomes.Find(item => item.Plan.Name == boundedPlan.Name);
        bool complete = reference is { Status: "completed", Result: not null } &&
            bounded is { Status: "completed", Result: not null } &&
            reference.Observer.Work is { EnumerationCompleted: true } && bounded.Observer.Work is { EnumerationCompleted: true };
        bool inputParity = Hash(referencePlan.Lines) == Hash(boundedPlan.Lines);
        bool candidateParity = complete && Hash(reference!.Observer.Candidates) == Hash(bounded!.Observer.Candidates);
        bool resultParity = complete && ResultIdentityHash(reference!.Result!) == ResultIdentityHash(bounded!.Result!);
        bool uncertaintyParity = complete && Hash(reference!.Result!.Uncertainty) == Hash(bounded!.Result!.Uncertainty);
        bool diagnosticsParity = complete &&
            Hash(reference!.Result!.Diagnostics with { Elapsed = TimeSpan.Zero }) ==
            Hash(bounded!.Result!.Diagnostics with { Elapsed = TimeSpan.Zero });
        // Bounded enumeration additionally charges envelope work. Every other
        // immutable counter must agree for these fully contacting fixtures.
        bool workParity = complete &&
            (reference!.Observer.Work! with { EnvelopeComparisons = 0 }) == (bounded!.Observer.Work! with { EnvelopeComparisons = 0 });
        string lateId = referencePlan.Lines[^1].CandidateId;
        bool lateWinner = complete && reference!.Observer.Candidates.Length == 256 &&
            reference.Observer.Candidates[0].YSupportingCandidateIds.Count == 1 &&
            reference.Observer.Candidates[0].YSupportingCandidateIds[0] == lateId &&
            Array.FindIndex(referencePlan.Lines, line => line.CandidateId == lateId) > 128;
        if (lateWinner)
        {
            var firstIds = referencePlan.Lines.Skip(1).Take(128).Select(line => line.CandidateId).ToHashSet(StringComparer.Ordinal);
            AxisGeometryCandidateSnapshot[] early = reference!.Observer.Candidates
                .Where(pair => pair.YSupportingCandidateIds.Any(firstIds.Contains)).ToArray();
            double winnerScore = reference!.Observer.Candidates[0].Score;
            lateWinner = early.Length == 128 && early.All(pair => pair.Score < winnerScore);
        }
        if (!inputParity || !candidateParity || !resultParity || !uncertaintyParity || !diagnosticsParity || !workParity || !lateWinner)
            violations.Add(referencePlan.Name + ":complete_parity_or_late_winner_failed");
        return new {
            reference = referencePlan.Name, bounded = boundedPlan.Name, complete,
            input_sha256 = Hash(referencePlan.Lines), input_parity = inputParity,
            candidate_parity = candidateParity, result_parity = resultParity,
            uncertainty_parity = uncertaintyParity, diagnostics_parity_except_elapsed = diagnosticsParity,
            work_parity_except_envelope_comparisons = workParity,
            late_global_winner_after_first_128 = lateWinner,
            uncertainty_reference_sha256 = complete ? Hash(reference!.Result!.Uncertainty) : null,
            uncertainty_bounded_sha256 = complete ? Hash(bounded!.Result!.Uncertainty) : null,
            normalized_work_reference_sha256 = complete ? Hash(reference!.Observer.Work! with { EnvelopeComparisons = 0 }) : null,
            normalized_work_bounded_sha256 = complete ? Hash(bounded!.Observer.Work! with { EnvelopeComparisons = 0 }) : null,
        };
    }

    private static GeometryLineCandidate[] IrregularCapacityFixture()
    {
        var lines = new GeometryLineCandidate[257];
        lines[0] = new("baseline", new(new(100, 3000), new(4000, 3000)), LineCandidateSource.RecordedFixture);
        // Two regular 128-line clusters separated by a 1230px gap. This forces
        // the existing irregular-window fallback rather than the full-run shortcut.
        for (int i = 0; i < 256; i++)
        {
            int x = i < 128 ? 2600 + 10 * (127 - i) : 100 + 10 * (255 - i);
            lines[i + 1] = new($"vertical-{i:D3}", new(new(x, 3000), new(x, 1000)), LineCandidateSource.RecordedFixture);
        }
        return lines;
    }
}
