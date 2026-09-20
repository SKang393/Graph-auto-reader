// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record SyntheticPanelDiagnosticAttempt(
    Guid PanelId, bool ObservationWritten, string? FailureType, string? Error);

internal sealed record SyntheticDiagnosticContinuationResult(
    string? SkippedReason, IReadOnlyList<SyntheticPanelDiagnosticAttempt> Attempts);

/// <summary>
/// Completes synthetic diagnostic observations after a source workflow fails.
/// It does not retry the failing panel, export data, or change source outcomes.
/// </summary>
internal static class FrozenSyntheticDiagnosticContinuation
{
    internal static async Task<SyntheticDiagnosticContinuationResult> RunAsync(
        IReadOnlyList<Guid> sourcePanelIds,
        IReadOnlySet<Guid> observedPanelIds,
        Guid? failedPanelId,
        Func<Guid, CancellationToken, Task> detect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePanelIds);
        ArgumentNullException.ThrowIfNull(observedPanelIds);
        ArgumentNullException.ThrowIfNull(detect);
        cancellationToken.ThrowIfCancellationRequested();
        if (sourcePanelIds.Any(static id => id == Guid.Empty) ||
            sourcePanelIds.Distinct().Count() != sourcePanelIds.Count ||
            failedPanelId is { } failed && !sourcePanelIds.Contains(failed))
        {
            throw new InvalidDataException("Synthetic diagnostic panel identities are inconsistent.");
        }

        Guid[] remaining = sourcePanelIds.Where(id => !observedPanelIds.Contains(id)).ToArray();
        if (remaining.Length > 0 && failedPanelId is null)
            return new("failed_panel_identity_unavailable", []);

        var attempts = new List<SyntheticPanelDiagnosticAttempt>();
        foreach (Guid panelId in remaining.Where(id => id != failedPanelId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = null;
            try
            {
                await detect(panelId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                failure = exception;
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool observed = observedPanelIds.Contains(panelId);
            attempts.Add(new(panelId, observed,
                failure?.GetType().Name ?? (observed ? null : "MissingObservation"),
                failure?.Message ?? (observed ? null : "Detection completed without the expected stage observation.")));
        }
        return new(null, attempts.AsReadOnly());
    }
}
