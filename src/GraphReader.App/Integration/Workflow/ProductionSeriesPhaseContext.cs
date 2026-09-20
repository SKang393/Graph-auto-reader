// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Legends;
using GraphReader.Markers.Grouping;
using GraphReader.Phases;

namespace GraphReader.App.Integration.Workflow;

/// <summary>Reconciles provisional series roles without changing point grouping.</summary>
internal static class ProductionSeriesPhaseContext
{
    internal const string Version = "series-roles-from-final-phase-evidence-v1";

    internal static SeriesPhaseContextResult Resolve(
        MarkerGroupingState grouping, PhaseReasoningPayload phases, LegendReasoningPayload legend,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var phaseTypes = phases.Phases.ToDictionary(static phase => phase.PhaseId,
            static phase => phase.NormalizedType, StringComparer.Ordinal);
        var assigned = phases.Assignments.ToDictionary(static item => item.PointId, static item => item.PhaseId, StringComparer.Ordinal);
        var hints = legend.Series.ToDictionary(static item => item.SeriesId, static item => item.Semantic.Hint, StringComparer.Ordinal);
        var excluded = legend.ExcludedArtifactMarkerIds.ToHashSet(StringComparer.Ordinal);
        var roles = new Dictionary<string, MarkerSeriesRole>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (MarkerSeries series in grouping.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhaseNormalizedType[] observed = series.MarkerIds.Where(id => !excluded.Contains(id)).Select(id =>
            {
                if (!assigned.TryGetValue(id, out string? phaseId) || !phaseTypes.TryGetValue(phaseId, out PhaseNormalizedType type))
                    throw new ArgumentException("Every series point requires an existing final phase assignment.", nameof(phases));
                return type;
            }).Distinct().ToArray();
            MarkerSeriesRole role = hints.GetValueOrDefault(series.SeriesId) switch
            {
                LegendSemanticHint.Maintenance => MarkerSeriesRole.Maintenance,
                LegendSemanticHint.Generalization => MarkerSeriesRole.Generalization,
                _ when series.SemanticRole is MarkerSeriesRole.Maintenance or MarkerSeriesRole.Generalization => series.SemanticRole,
                _ when observed.Contains(PhaseNormalizedType.Intervention) => MarkerSeriesRole.Intervention,
                _ when observed.Length == 1 => observed[0] switch
                {
                    PhaseNormalizedType.Baseline => MarkerSeriesRole.Baseline,
                    PhaseNormalizedType.Maintenance => MarkerSeriesRole.Maintenance,
                    PhaseNormalizedType.Generalization => MarkerSeriesRole.Generalization,
                    _ => MarkerSeriesRole.Unknown,
                },
                _ => MarkerSeriesRole.Unknown,
            };
            roles.Add(series.SeriesId, role);
            if (role != series.SemanticRole)
                warnings.Add($"series_role_reconciled_with_phases:{series.SeriesId}:{series.SemanticRole}:{role}");
            if (role == MarkerSeriesRole.Unknown) warnings.Add($"series_phase_role_requires_review:{series.SeriesId}");
        }
        string[] baselines = roles.Where(static item => item.Value == MarkerSeriesRole.Baseline)
            .Select(static item => item.Key).ToArray();
        if (baselines.Length > 1 && roles.ContainsValue(MarkerSeriesRole.Intervention))
            warnings.Add("shared_baseline_relation_ambiguous");
        MarkerSeries[] reconciled = grouping.Series.Select(series =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkerSeriesRole role = roles[series.SeriesId];
            // Match the phase reasoner's unique-baseline rule. A UUID is never evidence.
            string? shared = role == MarkerSeriesRole.Intervention && baselines.Length == 1 ? baselines[0] : null;
            if (shared != series.SharedBaselineSeriesId)
                warnings.Add($"series_shared_baseline_reconciled:{series.SeriesId}");
            string[] probes = role == MarkerSeriesRole.Intervention
                ? series.ApplicableProbeSeriesIds.Where(id => roles.TryGetValue(id, out MarkerSeriesRole probeRole) &&
                    probeRole is MarkerSeriesRole.Maintenance or MarkerSeriesRole.Generalization).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            if (!probes.SequenceEqual(series.ApplicableProbeSeriesIds, StringComparer.Ordinal))
                warnings.Add($"series_probe_relation_requires_review:{series.SeriesId}");
            return new MarkerSeries(series.SeriesId, series.Symbol, series.Shape, series.Fill,
                series.DisplayName, role, series.MarkerIds, series.Confidence, series.LegendText, shared, probes);
        }).ToArray();
        return new(new MarkerGroupingState(grouping.Markers, grouping.Connections, reconciled, grouping.AuditEvents),
            Array.AsReadOnly(warnings.ToArray()));
    }
}

internal sealed record SeriesPhaseContextResult(
    MarkerGroupingState Grouping, IReadOnlyList<string> Warnings);
