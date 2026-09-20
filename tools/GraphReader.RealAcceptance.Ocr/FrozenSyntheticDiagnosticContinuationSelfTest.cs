// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenSyntheticDiagnosticContinuationSelfTest
{
    internal static async Task<object> RunAsync()
    {
        Guid[] panels = Enumerable.Range(1, 4)
            .Select(value => new Guid(value, 0, 0, new byte[8])).ToArray();
        var observed = new HashSet<Guid> { panels[0] };
        var visited = new List<Guid>();
        SyntheticDiagnosticContinuationResult result = await FrozenSyntheticDiagnosticContinuation.RunAsync(
            panels, observed, panels[0], (id, _) =>
            {
                visited.Add(id);
                if (id == panels[1]) throw new InvalidDataException("fixture failure before observation");
                observed.Add(id);
                if (id == panels[2]) throw new InvalidOperationException("fixture calibration rejection after observation");
                return Task.CompletedTask;
            }, CancellationToken.None);
        Require(visited.SequenceEqual(panels.Skip(1)), "later_panels_run_once_despite_intermediate_failure");
        Require(result.Attempts.Count == 3 && !result.Attempts[0].ObservationWritten &&
            result.Attempts[1].ObservationWritten && result.Attempts[2].ObservationWritten,
            "observations_and_failures_are_reported_separately");
        Require(result.Attempts[0].FailureType == nameof(InvalidDataException) &&
            result.Attempts[1].FailureType == nameof(InvalidOperationException) && result.Attempts[2].FailureType is null,
            "a_recorded_observation_does_not_erase_a_stage_failure");

        var noObservations = new HashSet<Guid>();
        visited.Clear();
        result = await FrozenSyntheticDiagnosticContinuation.RunAsync(panels, noObservations, panels[0],
            (id, _) => { visited.Add(id); return Task.CompletedTask; }, CancellationToken.None);
        Require(!visited.Contains(panels[0]) && result.Attempts.All(static row =>
            !row.ObservationWritten && row.FailureType == "MissingObservation"),
            "unobserved_failing_panel_is_not_retried_and_absent_observations_are_explicit");

        result = await FrozenSyntheticDiagnosticContinuation.RunAsync(panels, noObservations, null,
            (_, _) => throw new InvalidOperationException("must not retry an unidentified failure"), CancellationToken.None);
        Require(result.SkippedReason == "failed_panel_identity_unavailable" && result.Attempts.Count == 0,
            "unknown_failure_identity_does_not_trigger_blind_retries");
        await ExpectAsync<InvalidDataException>(() => FrozenSyntheticDiagnosticContinuation.RunAsync(
            [panels[0], panels[0]], noObservations, panels[0], (_, _) => Task.CompletedTask, CancellationToken.None));
        await ExpectAsync<InvalidDataException>(() => FrozenSyntheticDiagnosticContinuation.RunAsync(
            panels, noObservations, Guid.NewGuid(), (_, _) => Task.CompletedTask, CancellationToken.None));
        await ExpectAsync<OperationCanceledException>(() => FrozenSyntheticDiagnosticContinuation.RunAsync(
            panels, noObservations, panels[0], (_, _) => throw new OperationCanceledException(), CancellationToken.None));
#pragma warning disable CA2201 // Deliberately inject a runtime fault to verify that diagnostics do not swallow it.
        await ExpectAsync<OutOfMemoryException>(() => FrozenSyntheticDiagnosticContinuation.RunAsync(
            panels, noObservations, panels[0], (_, _) => throw new OutOfMemoryException(), CancellationToken.None));
#pragma warning restore CA2201
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => FrozenSyntheticDiagnosticContinuation.RunAsync(
            panels, noObservations, panels[0], (_, _) => Task.CompletedTask, cancellation.Token));
        return new
        {
            status = "pass",
            scope = "fictitious synthetic diagnostic continuation only",
            checks_passed = 10,
            model_inference = false,
            private_corpus_access = false,
            sealed_corpus_access = false,
        };
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidDataException($"Diagnostic continuation check failed: {check}");
    }

    private static async Task ExpectAsync<T>(Func<Task<SyntheticDiagnosticContinuationResult>> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidDataException($"Expected {typeof(T).Name} from diagnostic continuation.");
    }
}
