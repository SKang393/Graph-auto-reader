// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GraphReader.Export;

internal static class ExportSerialization
{
    private const string AuditCsvHeader =
        "x_value,y_value,phase,point_id,source_series_id,target_intervention_series_id," +
        "phase_id,original_pixel_x,original_pixel_y,x_source,x_confidence,y_confidence," +
        "point_confidence,review_status,inclusion,export_mode,calibration_status," +
        "session_origin_override_applied,session_origin_override_reason," +
        "session_origin_override_confirmed_at_utc,series_symbol,series_name,source_stage,model_version," +
        "point_review_actions,phase_source,phase_label_review";

    public static string MinimalCsv(
        IReadOnlyList<MinimalExportRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var csv = new StringBuilder(ExportContract.MinimalCsvHeader.Length + (rows.Count * 32));
        csv.Append(ExportContract.MinimalCsvHeader).Append('\n');

        foreach (MinimalExportRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendDouble(csv, row.XValue);
            csv.Append(',');
            AppendDouble(csv, row.YValue);
            csv.Append(',');
            AppendCsvField(csv, row.Phase);
            csv.Append('\n');
        }

        return csv.ToString();
    }

    public static string AuditCsv(
        IReadOnlyList<ExtendedAuditRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var csv = new StringBuilder(AuditCsvHeader.Length + (rows.Count * 256));
        csv.Append(AuditCsvHeader).Append('\n');

        foreach (ExtendedAuditRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendDouble(csv, row.XValue);
            csv.Append(',');
            AppendDouble(csv, row.YValue);
            csv.Append(',');
            AppendCsvField(csv, row.Phase);
            csv.Append(',').Append(row.PointId.ToString("D"));
            csv.Append(',').Append(row.SourceSeriesId.ToString("D"));
            csv.Append(',').Append(row.TargetInterventionSeriesId.ToString("D"));
            csv.Append(',').Append(row.PhaseId.ToString("D"));
            csv.Append(',');
            AppendDouble(csv, row.OriginalPixel.X);
            csv.Append(',');
            AppendDouble(csv, row.OriginalPixel.Y);
            csv.Append(',').Append(XSource(row.XSource));
            csv.Append(',');
            AppendDouble(csv, row.XConfidence);
            csv.Append(',');
            AppendDouble(csv, row.YConfidence);
            csv.Append(',');
            AppendDouble(csv, row.PointConfidence);
            csv.Append(',').Append(ReviewStatus(row.ReviewStatus));
            csv.Append(',').Append(Inclusion(row.Inclusion));
            csv.Append(',').Append(ExportModeName(row.ExportMode));
            csv.Append(',').Append(CalibrationStatus(row.CalibrationStatus));
            csv.Append(',').Append(row.SessionOriginOverrideApplied ? "true" : "false");
            csv.Append(',');
            AppendCsvField(csv, row.SessionOriginOverrideReason);
            csv.Append(',');
            AppendCsvField(
                csv,
                row.SessionOriginOverrideConfirmedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            csv.Append(',');
            AppendCsvField(csv, row.SeriesSymbol);
            csv.Append(',');
            AppendCsvField(csv, row.SeriesName);
            csv.Append(',');
            AppendCsvField(csv, row.SourceStage);
            csv.Append(',');
            AppendCsvField(csv, row.ModelVersion);
            ExportReviewEvidence evidence = ReviewEvidence(row);
            csv.Append(',');
            AppendCsvField(csv, evidence.PointActions);
            csv.Append(',');
            AppendCsvField(csv, evidence.PhaseSource);
            csv.Append(',');
            AppendCsvField(csv, evidence.PhaseLabelReview);
            csv.Append('\n');
        }

        return csv.ToString();
    }

    public static string AuditJson(
        Guid runId,
        Guid projectId,
        Guid panelId,
        Guid interventionSeriesId,
        ExportMode mode,
        bool observationOrderConfirmed,
        string seriesSymbol,
        string seriesName,
        IReadOnlyList<ExtendedAuditRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesSymbol);
        ArgumentNullException.ThrowIfNull(seriesName);
        ArgumentNullException.ThrowIfNull(rows);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       SkipValidation = false,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contract_version", ExportContract.Version);
            writer.WriteString("run_id", runId);
            writer.WriteString("project_id", projectId);
            writer.WriteString("panel_id", panelId);
            writer.WriteString("intervention_series_id", interventionSeriesId);
            writer.WriteString("export_mode", ExportModeName(mode));
            if (observationOrderConfirmed)
                writer.WriteBoolean("observation_order_confirmed_for_export", true);
            writer.WriteString("series_symbol", seriesSymbol);
            writer.WriteString("series_name", seriesName);
            writer.WriteString("coordinate_space", ExportContract.CoordinateSpace);
            writer.WriteNumber("row_count", rows.Count);
            writer.WriteStartArray("rows");

            foreach (ExtendedAuditRow row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteAuditJsonRow(writer, row);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    public static string Sha256(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public static string ArtifactSetSha256(IEnumerable<(string FileName, string Sha256)> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var canonical = new StringBuilder();
        foreach ((string fileName, string sha256) in artifacts
                     .Select(static artifact =>
                         (
                             FileName: NormalizeFileName(artifact.FileName),
                             Sha256: NormalizeSha256(artifact.Sha256)))
                     .OrderBy(static artifact => artifact.FileName, StringComparer.Ordinal)
                     .ThenBy(static artifact => artifact.Sha256, StringComparer.Ordinal))
        {
            canonical
                .Append(fileName.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(fileName)
                .Append(':')
                .Append(sha256)
                .Append(';');
        }

        return Sha256(canonical.ToString());
    }

    private static void WriteAuditJsonRow(Utf8JsonWriter writer, ExtendedAuditRow row)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x_value", row.XValue);
        writer.WriteNumber("y_value", row.YValue);
        writer.WriteString("phase", row.Phase);
        writer.WriteString("point_id", row.PointId);
        writer.WriteString("source_series_id", row.SourceSeriesId);
        writer.WriteString("target_intervention_series_id", row.TargetInterventionSeriesId);
        writer.WriteString("phase_id", row.PhaseId);
        writer.WriteNumber("original_pixel_x", row.OriginalPixel.X);
        writer.WriteNumber("original_pixel_y", row.OriginalPixel.Y);
        writer.WriteString("x_source", XSource(row.XSource));
        writer.WriteNumber("x_confidence", row.XConfidence);
        writer.WriteNumber("y_confidence", row.YConfidence);
        writer.WriteNumber("point_confidence", row.PointConfidence);
        writer.WriteString("review_status", ReviewStatus(row.ReviewStatus));
        ExportReviewEvidence evidence = ReviewEvidence(row);
        writer.WriteString("point_review_actions", evidence.PointActions);
        writer.WriteString("phase_source", evidence.PhaseSource);
        writer.WriteString("phase_label_review", evidence.PhaseLabelReview);
        writer.WriteString("inclusion", Inclusion(row.Inclusion));
        writer.WriteString("export_mode", ExportModeName(row.ExportMode));
        writer.WriteString("calibration_status", CalibrationStatus(row.CalibrationStatus));
        writer.WriteBoolean("session_origin_override_applied", row.SessionOriginOverrideApplied);
        if (row.SessionOriginOverrideReason is null)
        {
            writer.WriteNull("session_origin_override_reason");
        }
        else
        {
            writer.WriteString("session_origin_override_reason", row.SessionOriginOverrideReason);
        }

        if (row.SessionOriginOverrideConfirmedAtUtc is null)
        {
            writer.WriteNull("session_origin_override_confirmed_at_utc");
        }
        else
        {
            writer.WriteString(
                "session_origin_override_confirmed_at_utc",
                row.SessionOriginOverrideConfirmedAtUtc.Value.ToUniversalTime());
        }

        writer.WriteString("series_symbol", row.SeriesSymbol);
        writer.WriteString("series_name", row.SeriesName);
        writer.WriteString("source_stage", row.SourceStage);
        if (row.ModelVersion is null)
        {
            writer.WriteNull("model_version");
        }
        else
        {
            writer.WriteString("model_version", row.ModelVersion);
        }

        writer.WriteEndObject();
    }

    private static ExportReviewEvidence ReviewEvidence(ExtendedAuditRow row) => row.ReviewEvidence ??
        new(row.ReviewStatus == ExportReviewStatus.Unreviewed ? "none_recorded" : "recorded_status_only", "unknown", "unknown");

    private static void AppendDouble(StringBuilder destination, double value) =>
        destination.Append(value.ToString("R", CultureInfo.InvariantCulture));

    private static void AppendCsvField(StringBuilder destination, string? value)
    {
        if (value is null)
        {
            return;
        }

        string normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (HasSpreadsheetFormulaPrefix(normalized))
        {
            normalized = "'" + normalized;
        }

        bool requiresQuotes = normalized.IndexOfAny([',', '"', '\n']) >= 0;
        if (!requiresQuotes)
        {
            destination.Append(normalized);
            return;
        }

        destination.Append('"');
        foreach (char character in normalized)
        {
            if (character == '"')
            {
                destination.Append("\"\"");
            }
            else
            {
                destination.Append(character);
            }
        }

        destination.Append('"');
    }

    private static bool HasSpreadsheetFormulaPrefix(string value)
    {
        int index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return index < value.Length && value[index] is '=' or '+' or '-' or '@';
    }

    private static string NormalizeFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Replace('\\', '/');
    }

    private static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "SHA-256 values must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static string ExportModeName(ExportMode value) => value switch
    {
        ExportMode.PrintedSession => "printed_session",
        ExportMode.ObservationOrder => "observation_order",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported export mode."),
    };

    private static string CalibrationStatus(ExportCalibrationStatus value) => value switch
    {
        ExportCalibrationStatus.Missing => "missing",
        ExportCalibrationStatus.NeedsReview => "needs_review",
        ExportCalibrationStatus.Valid => "valid",
        ExportCalibrationStatus.InvalidSessionOrigin => "invalid_session_origin",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported calibration status."),
    };

    private static string XSource(ExportXValueSource value) => value switch
    {
        ExportXValueSource.Printed => "printed",
        ExportXValueSource.Estimated => "estimated",
        ExportXValueSource.ObservationOrder => "observation_order",
        ExportXValueSource.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported x-value source."),
    };

    private static string ReviewStatus(ExportReviewStatus value) => value switch
    {
        ExportReviewStatus.Unreviewed => "unreviewed",
        ExportReviewStatus.Accepted => "accepted",
        ExportReviewStatus.Corrected => "corrected",
        ExportReviewStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported review status."),
    };

    private static string Inclusion(ExportRowInclusion value) => value switch
    {
        ExportRowInclusion.Intervention => "intervention",
        ExportRowInclusion.SharedBaseline => "shared_baseline",
        ExportRowInclusion.ApplicableProbe => "applicable_probe",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported row inclusion."),
    };
}
