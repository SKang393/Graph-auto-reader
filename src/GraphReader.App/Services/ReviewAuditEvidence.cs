// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using GraphReader.Domain;
using GraphReader.Export;

namespace GraphReader.App.Services;

internal static class ReviewAuditEvidence
{
    public static ExportReviewEvidence ForPoint(PointRecord point, PhaseRecord? phase,
        IEnumerable<AuditEvent> pointEvents, IEnumerable<AuditEvent> phaseEvents)
    {
        var actions = new List<string>();
        var edits = pointEvents.Where(entry => entry.Kind == DomainEventKind.PointEdited).ToArray();
        if (edits.FirstOrDefault(entry => entry.Note == "Manual point added") is { } added)
            actions.Add(added.Details is { } detail && Text(detail, "promoted_marker_id") is not null ? "marker_promoted" : "point_placed");
        foreach (PointModification change in point.ModificationHistory)
        {
            if (change.Reason.StartsWith("Manual point reassigned from series '", StringComparison.Ordinal))
            {
                actions.Add("series_reassigned");
                continue;
            }
            if (change.Reason != "Manual point moved")
            {
                actions.Add("other_recorded_edit");
                continue;
            }

            // Older moves have no operation-bound axis flags. Keep the move without
            // attributing later coordinate changes to it.
            JsonElement? move = edits.LastOrDefault(entry => entry.Details is { } details &&
                Text(details, "modification_event_id") == change.EventId.Value.ToString("D"))?.Details;
            if (move is not { } record || !Boolean(record, "moved_pixel_x", out bool x) ||
                !Boolean(record, "moved_pixel_y", out bool y) || (!x && !y))
                actions.Add("point_moved_axes_unspecified");
            else
            {
                if (x) actions.Add("pixel_x_moved");
                if (y) actions.Add("pixel_y_moved");
            }
        }
        if (point.ReviewStatus == ReviewStatus.Accepted &&
            edits.LastOrDefault(entry => entry.Note is "Point accepted by user" or "Point acceptance undone")?.Note == "Point accepted by user")
            actions.Add("point_accepted");
        string description = actions.Count != 0 ? string.Join("|", actions.Distinct(StringComparer.Ordinal)) :
            point.ReviewStatus == ReviewStatus.Unreviewed ? "none_recorded" : "recorded_status_only";
        return ForPhase(description, phase, phaseEvents);
    }

    public static ExportReviewEvidence ForPhase(string pointActions, PhaseRecord? phase, IEnumerable<AuditEvent> events)
    {
        if (phase is null) return new(pointActions, "unknown", "unknown");
        string source = phase.Source switch
        {
            PhaseSource.ProfilePrior => "profile_prior",
            PhaseSource.CrossPanel => "cross_panel",
            PhaseSource.Ocr => "ocr",
            PhaseSource.Manual => "manual",
            _ => "unknown",
        };
        JsonElement? label = events.LastOrDefault(entry => entry.Kind == DomainEventKind.PhaseEdited &&
            entry.Details is { } details && Text(details, "action") is "add" or "label")?.Details;
        bool explicitLabel = phase.Source == PhaseSource.Manual && phase.UserConfirmed &&
            label is { } evidence && Text(evidence, "code") == phase.Code && Text(evidence, "label") == phase.LabelText;
        return new(pointActions, source, !phase.UserConfirmed ? "unconfirmed" :
            explicitLabel ? "explicit_label" : "recorded_confirmation_only");
    }

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Boolean(JsonElement element, string key, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(key, out JsonElement field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = field.GetBoolean();
        return true;
    }
}
