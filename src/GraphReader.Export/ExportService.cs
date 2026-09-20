// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Text;

namespace GraphReader.Export;

/// <summary>
/// Produces deterministic intervention-specific exports from an immutable input snapshot.
/// </summary>
public sealed class ExportService : IExportService
{
    private const double SessionOne = 1d;

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var total = Stopwatch.StartNew();
        var stage = Stopwatch.StartNew();
        List<ExportFailure> failures = Validate(request, cancellationToken);
        double validationMilliseconds = stage.Elapsed.TotalMilliseconds;
        bool invalidOrigin = IsInvalidSessionOrigin(request);
        bool originBlocked = invalidOrigin && !request.SessionOriginPolicy.HasExplicitOverride;

        if (failures.Any(IsError))
        {
            return FailureResult(
                request,
                failures,
                originBlocked,
                validationMilliseconds,
                total.Elapsed.TotalMilliseconds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        stage.Restart();
        PreparedExport prepared = Prepare(request, cancellationToken);
        double preparationMilliseconds = stage.Elapsed.TotalMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();
        stage.Restart();
        SerializedExport serialized = Serialize(request, prepared, cancellationToken);
        double serializationMilliseconds = stage.Elapsed.TotalMilliseconds;

        double writeMilliseconds = 0d;
        if (request.Operation == ExportOperation.WriteFiles)
        {
            stage.Restart();
            try
            {
                serialized = await WriteFilesAsync(
                        request.OutputDirectory,
                        serialized,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExportFileCollisionException exception)
            {
                failures.Add(Failure(
                    "EXPORT_FILE_EXISTS",
                    "Export.FileExists",
                    exception.Message,
                    recoverable: true,
                    "Choose another output directory or remove the existing destination deliberately."));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Add(Failure(
                    "EXPORT_WRITE_FAILED",
                    "Export.WriteFailed",
                    $"The export files could not be written: {exception.Message}",
                    recoverable: true,
                    "Choose a writable output directory and retry."));
            }

            writeMilliseconds = stage.Elapsed.TotalMilliseconds;
        }

        var timing = new ExportTiming(
            validationMilliseconds,
            preparationMilliseconds,
            serializationMilliseconds,
            writeMilliseconds,
            total.Elapsed.TotalMilliseconds);

        return new ExportResult(
            request.RunId,
            request.ProjectId,
            request.PanelId,
            request.Mode,
            serialized.Preview,
            serialized.MinimalArtifacts,
            serialized.AuditArtifacts,
            serialized.Determinism,
            timing,
            prepared.Warnings,
            failures);
    }

    private static List<ExportFailure> Validate(
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var failures = new List<ExportFailure>();

        ValidateRequestValues(request, failures);
        ValidateCalibration(request, failures);
        ValidateEntityIdentifiers(request, failures);

        if (failures.Any(IsError))
        {
            return failures;
        }

        Dictionary<Guid, ExportPhase> phases = request.Phases.ToDictionary(static phase => phase.PhaseId);
        Dictionary<Guid, ExportSeries> series = request.Series.ToDictionary(static item => item.SeriesId);
        Dictionary<Guid, ExportPoint> points = request.Points.ToDictionary(static point => point.PointId);
        Dictionary<Guid, ExportSeriesRelation> relations =
            request.Relations.ToDictionary(static relation => relation.InterventionSeriesId);

        foreach (ExportPhase phase in request.Phases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(phase.Code) ||
                !double.IsFinite(phase.OriginalXMinimum) ||
                !double.IsFinite(phase.OriginalXMaximum) ||
                phase.OriginalXMinimum > phase.OriginalXMaximum ||
                !IsConfidence(phase.Confidence) ||
                !Enum.IsDefined(phase.NormalizedType))
            {
                failures.Add(Failure(
                    "INVALID_PHASE",
                    "Export.InvalidPhase",
                    $"Phase '{phase.PhaseId}' has invalid bounds, code, type, or confidence.",
                    recoverable: true,
                    "Review phase definitions before exporting.",
                    phase.PhaseId));
            }
        }

        foreach (ExportSeries item in request.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.DisplayName) ||
                !IsConfidence(item.Confidence) ||
                !Enum.IsDefined(item.SemanticRole))
            {
                failures.Add(Failure(
                    "INVALID_SERIES",
                    "Export.InvalidSeries",
                    $"Series '{item.SeriesId}' has invalid metadata.",
                    recoverable: true,
                    "Review the series metadata before exporting.",
                    item.SeriesId));
            }

            foreach (Guid pointId in item.PointIds.Distinct())
            {
                if (!points.TryGetValue(pointId, out ExportPoint? point))
                {
                    failures.Add(InvalidReference(
                        item.SeriesId,
                        $"Series '{item.SeriesId}' references missing point '{pointId}'."));
                }
                else if (point.SeriesId != item.SeriesId)
                {
                    failures.Add(InvalidReference(
                        point.PointId,
                        $"Point '{point.PointId}' does not identify series '{item.SeriesId}' as its owner."));
                }
            }
        }

        foreach (ExportPoint point in request.Points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePoint(point, series, phases, request.Mode, failures);
        }

        ValidateCompleteMembership(request, failures, cancellationToken);

        foreach (ExportSeriesRelation relation in request.Relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRelation(relation, series, failures);
        }

        foreach (ExportSeries intervention in request.Series.Where(
                     static item => item.SemanticRole == ExportSeriesRole.Intervention))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!relations.ContainsKey(intervention.SeriesId))
            {
                failures.Add(InvalidReference(
                    intervention.SeriesId,
                    $"Intervention series '{intervention.SeriesId}' has no export relation."));
            }
        }

        IEnumerable<Guid> selected = request.SelectedInterventionSeriesIds.Count == 0
            ? relations.Keys
            : request.SelectedInterventionSeriesIds;
        Guid[] selectedIds = selected.Distinct().ToArray();
        if (selectedIds.Length == 0)
            failures.Add(Failure(
                "NO_EXPORTABLE_SERIES", "Export.InvalidSeries",
                "No intervention series is available for export.", recoverable: true,
                "Review the series roles before exporting."));
        foreach (Guid selectedId in selectedIds)
        {
            if (!relations.ContainsKey(selectedId))
            {
                failures.Add(InvalidReference(
                    selectedId,
                    $"Selected intervention series '{selectedId}' has no export relation."));
            }
            else if (series.TryGetValue(selectedId, out ExportSeries? target) && target.PointIds.Count == 0)
                failures.Add(Failure(
                    "EMPTY_SELECTED_SERIES", "Export.InvalidSeries",
                    $"Selected intervention series '{selectedId}' contains no points.", recoverable: true,
                    "Review detected points and the export selection.", selectedId));
        }

        return failures;
    }

    private static void ValidateRequestValues(
        ExportRequest request,
        List<ExportFailure> failures)
    {
        if (request.ContractVersion != ExportContract.Version)
        {
            failures.Add(Failure(
                "UNSUPPORTED_CONTRACT_VERSION",
                "Export.UnsupportedContractVersion",
                $"Contract version {request.ContractVersion} is not supported.",
                recoverable: false,
                $"Upgrade the project to export contract version {ExportContract.Version}."));
        }

        if (!Enum.IsDefined(request.Mode) ||
            !Enum.IsDefined(request.Operation) ||
            (request.AuditMode & ~ExportAuditMode.ExtendedCsvAndJson) != 0)
        {
            failures.Add(Failure(
                "INVALID_EXPORT_OPTION",
                "Export.InvalidOption",
                "The request contains an unsupported export mode, operation, or audit option.",
                recoverable: true,
                "Select a supported export option."));
        }

        foreach ((Guid id, string name) in new[]
                 {
                     (request.RunId, "run"),
                     (request.ProjectId, "project"),
                     (request.PanelId, "panel"),
                 })
        {
            if (id == Guid.Empty)
            {
                failures.Add(InvalidId(id, $"The {name} identifier is empty."));
            }
        }

        if (request.Operation == ExportOperation.WriteFiles &&
            string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            failures.Add(Failure(
                "INVALID_OUTPUT_DIRECTORY",
                "Export.InvalidOutputDirectory",
                "A non-empty output directory is required when writing files.",
                recoverable: true,
                "Choose an output directory."));
        }
    }

    private static void ValidateCalibration(
        ExportRequest request,
        List<ExportFailure> failures)
    {
        ExportCalibration calibration = request.Calibration;
        if (!Enum.IsDefined(calibration.Status) || !IsConfidence(calibration.Confidence))
        {
            failures.Add(Failure(
                "INVALID_CALIBRATION",
                "Export.InvalidCalibration",
                "The calibration status or confidence is invalid.",
                recoverable: true,
                "Review the axis calibration."));
            return;
        }

        if (calibration.Status is ExportCalibrationStatus.Missing or ExportCalibrationStatus.NeedsReview ||
            !calibration.HasYCalibration)
        {
            failures.Add(Failure(
                "CALIBRATION_REQUIRED",
                "Export.CalibrationRequired",
                "Reviewed y-axis calibration is required for export.",
                recoverable: true,
                "Complete and review the y-axis calibration."));
        }

        if (request.Mode == ExportMode.PrintedSession && !calibration.HasPrintedSessionCalibration)
        {
            failures.Add(Failure(
                "PRINTED_SESSION_CALIBRATION_REQUIRED",
                "Export.PrintedSessionCalibrationRequired",
                "Printed-session export requires reviewed session calibration.",
                recoverable: true,
                "Complete and review the printed-session calibration."));
        }

        if (calibration.FirstObservedSession is double firstSession && !double.IsFinite(firstSession))
        {
            failures.Add(Failure(
                "INVALID_CALIBRATION",
                "Export.InvalidCalibration",
                "The first observed session is not finite.",
                recoverable: true,
                "Correct the session calibration."));
        }

        if (request.Operation == ExportOperation.WriteFiles &&
            IsInvalidSessionOrigin(request) &&
            !request.SessionOriginPolicy.HasExplicitOverride)
        {
            failures.Add(Failure(
                "INVALID_SESSION_ORIGIN",
                "Export.InvalidSessionOrigin",
                "The printed session origin is invalid and no complete explicit override was supplied.",
                recoverable: true,
                "Confirm the session origin or provide an explicit reviewed override."));
        }
    }

    private static void ValidateEntityIdentifiers(
        ExportRequest request,
        List<ExportFailure> failures)
    {
        ValidateIds(
            request.Phases.Select(static value => value.PhaseId),
            "phase",
            failures);
        ValidateIds(
            request.Series.Select(static value => value.SeriesId),
            "series",
            failures);
        ValidateIds(
            request.Points.Select(static value => value.PointId),
            "point",
            failures);
        ValidateIds(
            request.Relations.Select(static value => value.InterventionSeriesId),
            "intervention relation",
            failures);
        ValidateIds(request.SelectedInterventionSeriesIds, "selected intervention", failures);
    }

    private static void ValidateIds(
        IEnumerable<Guid> identifiers,
        string entityName,
        List<ExportFailure> failures)
    {
        var seen = new HashSet<Guid>();
        foreach (Guid id in identifiers)
        {
            if (id == Guid.Empty)
            {
                failures.Add(InvalidId(id, $"A {entityName} identifier is empty."));
            }
            else if (!seen.Add(id))
            {
                failures.Add(Failure(
                    "DUPLICATE_ID",
                    "Export.DuplicateId",
                    $"The {entityName} identifier '{id}' appears more than once.",
                    recoverable: true,
                    "Remove the duplicate entity.",
                    id));
            }
        }
    }

    private static void ValidatePoint(
        ExportPoint point,
        Dictionary<Guid, ExportSeries> series,
        Dictionary<Guid, ExportPhase> phases,
        ExportMode mode,
        List<ExportFailure> failures)
    {
        if (point.MarkerId == Guid.Empty || point.SeriesId is null || point.SeriesId == Guid.Empty ||
            point.PhaseId is null || point.PhaseId == Guid.Empty)
        {
            failures.Add(InvalidReference(
                point.PointId,
                $"Point '{point.PointId}' is missing marker, series, or phase provenance."));
            return;
        }

        if (!series.ContainsKey(point.SeriesId.Value) || !phases.ContainsKey(point.PhaseId.Value))
        {
            failures.Add(InvalidReference(
                point.PointId,
                $"Point '{point.PointId}' references a missing series or phase."));
        }

        bool invalidNumber =
            !double.IsFinite(point.OriginalPixel.X) ||
            !double.IsFinite(point.OriginalPixel.Y) ||
            point.GraphY is not double y ||
            !double.IsFinite(y) ||
            point.ObservationIndex < 1 ||
            !IsConfidence(point.XConfidence) ||
            !IsConfidence(point.YConfidence) ||
            !IsConfidence(point.PointConfidence);
        if (mode == ExportMode.PrintedSession && ResolveSessionX(point) is null)
        {
            invalidNumber = true;
        }

        if (point.GraphX is double graphX && !double.IsFinite(graphX) ||
            point.EstimatedXValue is double estimatedX && !double.IsFinite(estimatedX))
        {
            invalidNumber = true;
        }

        if (invalidNumber || !Enum.IsDefined(point.XSource) ||
            !Enum.IsDefined(point.ReviewStatus) || string.IsNullOrWhiteSpace(point.SourceStage))
        {
            failures.Add(Failure(
                "INVALID_POINT",
                "Export.InvalidPoint",
                $"Point '{point.PointId}' has incomplete or invalid scientific values or provenance.",
                recoverable: true,
                "Review the point before exporting.",
                point.PointId));
        }
    }

    private static void ValidateRelation(
        ExportSeriesRelation relation,
        Dictionary<Guid, ExportSeries> series,
        List<ExportFailure> failures)
    {
        if (!series.TryGetValue(relation.InterventionSeriesId, out ExportSeries? intervention) ||
            intervention.SemanticRole != ExportSeriesRole.Intervention)
        {
            failures.Add(InvalidReference(
                relation.InterventionSeriesId,
                $"Intervention relation '{relation.InterventionSeriesId}' does not reference an intervention series."));
        }

        if (relation.SharedBaselineSeriesId is Guid baselineId &&
            (!series.TryGetValue(baselineId, out ExportSeries? baseline) ||
             baseline.SemanticRole != ExportSeriesRole.Baseline))
        {
            failures.Add(InvalidReference(
                baselineId,
                $"Shared baseline '{baselineId}' does not reference a baseline series."));
        }

        foreach (Guid probeId in relation.ApplicableProbeSeriesIds.Distinct())
        {
            if (!series.TryGetValue(probeId, out ExportSeries? probe) ||
                probe.SemanticRole is not (ExportSeriesRole.Maintenance or ExportSeriesRole.Generalization))
            {
                failures.Add(InvalidReference(
                    probeId,
                    $"Applicable probe '{probeId}' is not a maintenance or generalization series."));
            }
        }
    }

    private static void ValidateCompleteMembership(
        ExportRequest request,
        List<ExportFailure> failures,
        CancellationToken cancellationToken)
    {
        var memberships = new Dictionary<Guid, List<Guid>>();
        foreach (ExportSeries item in request.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Guid pointId in item.PointIds)
            {
                if (!memberships.TryGetValue(pointId, out List<Guid>? owners))
                {
                    owners = [];
                    memberships.Add(pointId, owners);
                }

                owners.Add(item.SeriesId);
            }
        }

        foreach (ExportPoint point in request.Points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!memberships.TryGetValue(point.PointId, out List<Guid>? owners) || owners.Count == 0)
            {
                failures.Add(InvalidReference(
                    point.PointId,
                    $"Point '{point.PointId}' is not listed by its owning series."));
            }
            else if (owners.Count != 1 || owners[0] != point.SeriesId)
            {
                failures.Add(InvalidReference(
                    point.PointId,
                    $"Point '{point.PointId}' must be listed exactly once by its owning series."));
            }
        }
    }

    private static PreparedExport Prepare(
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, ExportPhase> phases = request.Phases.ToDictionary(static phase => phase.PhaseId);
        Dictionary<Guid, ExportSeries> series = request.Series.ToDictionary(static item => item.SeriesId);
        Dictionary<Guid, ExportPoint> points = request.Points.ToDictionary(static point => point.PointId);
        Dictionary<Guid, ExportSeriesRelation> relations =
            request.Relations.ToDictionary(static relation => relation.InterventionSeriesId);
        IEnumerable<Guid> selected = request.SelectedInterventionSeriesIds.Count == 0
            ? relations.Keys
            : request.SelectedInterventionSeriesIds;
        Guid[] selectedIds = selected
            .Distinct()
            .Order()
            .ToArray();
        ExportSeries[] selectedSeries = selectedIds.Select(id => series[id]).ToArray();
        IReadOnlyDictionary<Guid, ExportFileNames> fileNames =
            ExportFileNamePlanner.Plan(request.Participant, selectedSeries, request.FileNamePrefix);
        var exports = new List<PreparedIntervention>(selectedIds.Length);

        foreach (Guid interventionId in selectedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportSeries target = series[interventionId];
            ExportSeriesRelation relation = relations[interventionId];
            var sourceSeries = new List<(ExportSeries Series, ExportRowInclusion Inclusion)>
            {
                (target, ExportRowInclusion.Intervention),
            };
            if (relation.SharedBaselineSeriesId is Guid baselineId)
            {
                sourceSeries.Add((series[baselineId], ExportRowInclusion.SharedBaseline));
            }

            sourceSeries.AddRange(
                relation.ApplicableProbeSeriesIds
                    .Distinct()
                    .Order()
                    .Select(id => (series[id], ExportRowInclusion.ApplicableProbe)));

            var pointIds = new HashSet<Guid>();
            var rows = new List<PreparedRow>();
            foreach ((ExportSeries source, ExportRowInclusion inclusion) in sourceSeries)
            {
                foreach (Guid pointId in source.PointIds.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!pointIds.Add(pointId))
                    {
                        continue;
                    }

                    ExportPoint point = points[pointId];
                    ExportPhase phase = phases[point.PhaseId!.Value];
                    (double xValue, ExportXValueSource xSource) = request.Mode == ExportMode.ObservationOrder
                        ? (point.ObservationIndex, ExportXValueSource.ObservationOrder)
                        : ResolveSessionX(point)!.Value;
                    var minimal = new MinimalExportRow(xValue, point.GraphY!.Value, phase.Code);
                    var audit = new ExtendedAuditRow(
                        xValue,
                        point.GraphY.Value,
                        phase.Code,
                        point.PointId,
                        source.SeriesId,
                        interventionId,
                        phase.PhaseId,
                        point.OriginalPixel,
                        xSource,
                        point.XConfidence,
                        point.YConfidence,
                        point.PointConfidence,
                        point.ReviewStatus,
                        inclusion,
                        request.Mode,
                        request.Calibration.Status,
                        IsInvalidSessionOrigin(request) && request.SessionOriginPolicy.HasExplicitOverride,
                        IsInvalidSessionOrigin(request) && request.SessionOriginPolicy.HasExplicitOverride
                            ? request.SessionOriginPolicy.OverrideReason
                            : null,
                        IsInvalidSessionOrigin(request) && request.SessionOriginPolicy.HasExplicitOverride
                            ? request.SessionOriginPolicy.OverrideConfirmedAtUtc
                            : null,
                        source.Symbol,
                        source.DisplayName,
                        point.SourceStage,
                        point.ModelVersion);
                    rows.Add(new PreparedRow(
                        phase.Order,
                        xValue,
                        point.ObservationIndex,
                        source.SeriesId,
                        point.PointId,
                        minimal,
                        audit));
                }
            }

            PreparedRow[] orderedRows = rows
                .OrderBy(static row => row.PhaseOrder)
                .ThenBy(static row => row.XValue)
                .ThenBy(static row => row.ObservationIndex)
                .ThenBy(static row => row.SourceSeriesId)
                .ThenBy(static row => row.PointId)
                .ToArray();
            if (request.Mode == ExportMode.ObservationOrder)
            {
                orderedRows = orderedRows
                    .Select(static (row, index) => row with
                    {
                        XValue = index + 1d,
                        Minimal = row.Minimal with { XValue = index + 1d },
                        Audit = row.Audit with { XValue = index + 1d },
                    })
                    .ToArray();
            }

            exports.Add(new PreparedIntervention(target, fileNames[interventionId], orderedRows));
        }

        bool invalidSessionOrigin = IsInvalidSessionOrigin(request);
        string[] warnings = invalidSessionOrigin
            ? request.SessionOriginPolicy.HasExplicitOverride
                ? ["The invalid session origin was exported using an explicit reviewed override."]
                : ["Final export is blocked until the invalid session origin is corrected or explicitly overridden."]
            : [];
        return new PreparedExport(
            exports,
            warnings,
            invalidSessionOrigin && !request.SessionOriginPolicy.HasExplicitOverride);
    }

    private static SerializedExport Serialize(
        ExportRequest request,
        PreparedExport prepared,
        CancellationToken cancellationToken)
    {
        var previews = new List<ExportPreviewFile>(prepared.Interventions.Count);
        var minimalArtifacts = new List<MinimalCsvArtifact>(prepared.Interventions.Count);
        var auditArtifacts = new List<ExtendedAuditArtifact>();

        foreach (PreparedIntervention item in prepared.Interventions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinimalExportRow[] minimalRows = item.Rows.Select(static row => row.Minimal).ToArray();
            ExtendedAuditRow[] auditRows = item.Rows.Select(static row => row.Audit).ToArray();
            string minimalContent = ExportSerialization.MinimalCsv(minimalRows, cancellationToken);
            minimalArtifacts.Add(new MinimalCsvArtifact(
                item.Series.SeriesId,
                item.Series.Symbol,
                item.Series.DisplayName,
                item.FileNames.MinimalCsv,
                minimalContent,
                ExportSerialization.Sha256(minimalContent),
                minimalRows));

            var auditFileNames = new List<string>(2);
            if (request.AuditMode.HasFlag(ExportAuditMode.ExtendedCsv))
            {
                string content = ExportSerialization.AuditCsv(auditRows, cancellationToken);
                auditFileNames.Add(item.FileNames.ExtendedAuditCsv);
                auditArtifacts.Add(new ExtendedAuditArtifact(
                    item.Series.SeriesId,
                    ExportAuditArtifactFormat.Csv,
                    item.FileNames.ExtendedAuditCsv,
                    content,
                    ExportSerialization.Sha256(content),
                    auditRows));
            }

            if (request.AuditMode.HasFlag(ExportAuditMode.Json))
            {
                string content = ExportSerialization.AuditJson(
                    request.RunId,
                    request.ProjectId,
                    request.PanelId,
                    item.Series.SeriesId,
                    request.Mode,
                    item.Series.Symbol,
                    item.Series.DisplayName,
                    auditRows,
                    cancellationToken);
                auditFileNames.Add(item.FileNames.AuditJson);
                auditArtifacts.Add(new ExtendedAuditArtifact(
                    item.Series.SeriesId,
                    ExportAuditArtifactFormat.Json,
                    item.FileNames.AuditJson,
                    content,
                    ExportSerialization.Sha256(content),
                    auditRows));
            }

            previews.Add(new ExportPreviewFile(
                item.Series.SeriesId,
                item.Series.Symbol,
                item.Series.DisplayName,
                item.FileNames.MinimalCsv,
                auditFileNames,
                minimalRows));
        }

        string artifactSetHash = ExportSerialization.ArtifactSetSha256(
            minimalArtifacts.Select(static artifact => (artifact.FileName, artifact.Sha256))
                .Concat(auditArtifacts.Select(static artifact => (artifact.FileName, artifact.Sha256))));
        return new SerializedExport(
            new ExportPreview(previews, prepared.FinalExportBlocked, prepared.Warnings),
            minimalArtifacts,
            auditArtifacts,
            new ExportDeterminism(ExportContract.DeterministicOrdering, artifactSetHash));
    }

    private static async Task<SerializedExport> WriteFilesAsync(
        string outputDirectory,
        SerializedExport serialized,
        CancellationToken cancellationToken)
    {
        string fullDirectory = Path.GetFullPath(outputDirectory);
        IEnumerable<(string FileName, string Content)> artifactValues =
            serialized.MinimalArtifacts.Select(static artifact => (artifact.FileName, artifact.Content))
                .Concat(serialized.AuditArtifacts.Select(static artifact => (artifact.FileName, artifact.Content)));
        (string FileName, string Content)[] artifacts = artifactValues.ToArray();
        foreach ((string fileName, _) in artifacts)
        {
            string destination = SafeOutputPath(fullDirectory, fileName);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new ExportFileCollisionException(
                    $"The export destination '{fileName}' already exists; existing exports are never overwritten.");
            }
        }

        Directory.CreateDirectory(fullDirectory);
        var staged = new List<(string TemporaryPath, string FinalPath)>();
        var committed = new List<string>();

        try
        {
            foreach ((string fileName, string content) in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string finalPath = SafeOutputPath(fullDirectory, fileName);
                string temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(
                        temporaryPath,
                        content,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        cancellationToken)
                    .ConfigureAwait(false);
                staged.Add((temporaryPath, finalPath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach ((string temporaryPath, string finalPath) in staged)
                {
                    File.Move(temporaryPath, finalPath, overwrite: false);
                    committed.Add(finalPath);
                }
            }
            catch
            {
                foreach (string committedPath in committed.AsEnumerable().Reverse())
                {
                    try
                    {
                        File.Delete(committedPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                throw;
            }

            MinimalCsvArtifact[] minimal = serialized.MinimalArtifacts
                .Select(artifact => new MinimalCsvArtifact(
                    artifact.InterventionSeriesId,
                    artifact.SeriesSymbol,
                    artifact.SeriesName,
                    artifact.FileName,
                    artifact.Content,
                    artifact.Sha256,
                    artifact.Rows,
                    SafeOutputPath(fullDirectory, artifact.FileName)))
                .ToArray();
            ExtendedAuditArtifact[] audit = serialized.AuditArtifacts
                .Select(artifact => new ExtendedAuditArtifact(
                    artifact.InterventionSeriesId,
                    artifact.Format,
                    artifact.FileName,
                    artifact.Content,
                    artifact.Sha256,
                    artifact.Rows,
                    SafeOutputPath(fullDirectory, artifact.FileName)))
                .ToArray();
            return serialized with { MinimalArtifacts = minimal, AuditArtifacts = audit };
        }
        finally
        {
            foreach ((string temporaryPath, _) in staged)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string SafeOutputPath(string fullDirectory, string fileName)
    {
        string candidate = Path.GetFullPath(Path.Combine(fullDirectory, fileName));
        string directoryPrefix = Path.TrimEndingDirectorySeparator(fullDirectory) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The planned export filename escapes the output directory.", nameof(fileName));
        }

        return candidate;
    }

    private static ExportResult FailureResult(
        ExportRequest request,
        IReadOnlyList<ExportFailure> failures,
        bool finalExportBlocked,
        double validationMilliseconds,
        double totalMilliseconds)
    {
        string emptyHash = ExportSerialization.ArtifactSetSha256(
            Array.Empty<(string FileName, string Sha256)>());
        return new ExportResult(
            request.RunId,
            request.ProjectId,
            request.PanelId,
            request.Mode,
            new ExportPreview(Array.Empty<ExportPreviewFile>(), finalExportBlocked),
            Array.Empty<MinimalCsvArtifact>(),
            Array.Empty<ExtendedAuditArtifact>(),
            new ExportDeterminism(ExportContract.DeterministicOrdering, emptyHash),
            new ExportTiming(validationMilliseconds, 0d, 0d, 0d, totalMilliseconds),
            failures: failures);
    }

    private static bool IsInvalidSessionOrigin(ExportRequest request)
    {
        ExportCalibration calibration = request.Calibration;
        if (calibration.Status == ExportCalibrationStatus.InvalidSessionOrigin)
        {
            return true;
        }

        if (!request.SessionOriginPolicy.RequireFirstObservedSessionOne)
        {
            return false;
        }

        return !calibration.HasAbsoluteSessionOrigin ||
            calibration.FirstObservedSession is not double firstSession ||
            firstSession != SessionOne;
    }

    private static (double Value, ExportXValueSource Source)? ResolveSessionX(ExportPoint point)
    {
        if (point.PrintedXValue is double printed)
            return double.IsFinite(printed) ? (printed, ExportXValueSource.Printed) : null;

        // Printed-session mode preserves calibrated gaps. An inferred session
        // stays explicitly estimated in the audit; neither GraphX nor ordinal
        // order supplies a missing scientific value at the export boundary.
        return point.XSource == ExportXValueSource.Estimated &&
            point.EstimatedXValue is double estimated && double.IsFinite(estimated)
            ? (estimated, ExportXValueSource.Estimated)
            : null;
    }

    private static bool IsConfidence(double value) =>
        double.IsFinite(value) && value is >= 0d and <= 1d;

    private static bool IsError(ExportFailure failure) =>
        failure.Severity == ExportFailureSeverity.Error;

    private static ExportFailure InvalidId(Guid id, string message) => Failure(
        "INVALID_ID",
        "Export.InvalidId",
        message,
        recoverable: true,
        "Assign a stable non-empty identifier.",
        id == Guid.Empty ? null : id);

    private static ExportFailure InvalidReference(Guid id, string message) => Failure(
        "INVALID_REFERENCE",
        "Export.InvalidReference",
        message,
        recoverable: true,
        "Repair the missing or inconsistent reference.",
        id == Guid.Empty ? null : id);

    private static ExportFailure Failure(
        string code,
        string userMessageKey,
        string technicalMessage,
        bool recoverable,
        string suggestedAction,
        Guid? entityId = null) => new(
            code,
            ExportFailureSeverity.Error,
            userMessageKey,
            technicalMessage,
            recoverable,
            suggestedAction,
            entityId);

    private sealed record PreparedRow(
        int PhaseOrder,
        double XValue,
        int ObservationIndex,
        Guid SourceSeriesId,
        Guid PointId,
        MinimalExportRow Minimal,
        ExtendedAuditRow Audit);

    private sealed record PreparedIntervention(
        ExportSeries Series,
        ExportFileNames FileNames,
        IReadOnlyList<PreparedRow> Rows);

    private sealed record PreparedExport(
        IReadOnlyList<PreparedIntervention> Interventions,
        IReadOnlyList<string> Warnings,
        bool FinalExportBlocked);

    private sealed record SerializedExport(
        ExportPreview Preview,
        IReadOnlyList<MinimalCsvArtifact> MinimalArtifacts,
        IReadOnlyList<ExtendedAuditArtifact> AuditArtifacts,
        ExportDeterminism Determinism);

    private sealed class ExportFileCollisionException(string message) : IOException(message);
}
