// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record EngaugeDevLabelCounts(int Anchors, int Curves, long Points);

internal sealed record EngaugeDevLabelAuditResult(
    string AssignmentSha256,
    string SelectedInventorySha256,
    int SelectedProjects,
    int ParsedProjects,
    int FailedProjects,
    long Anchors,
    long Curves,
    long Points,
    IReadOnlyDictionary<string, int> FailureCodes,
    bool LabelsQualityApproved,
    bool SealedSourceOverlapVerified,
    bool CandidateAuthorized,
    bool ModelInference,
    int OptimizerSteps);

/// <summary>
/// Aggregate format and count audit for the selected development split. This
/// does not grade label correctness or make a model or training admission.
/// </summary>
internal static class EngaugeDevLabelAudit
{
    // Parser error strings must remain fixed. Neither exception details nor
    // source names may be returned in the aggregate audit.
    private static readonly HashSet<string> SafeParserCodes = new(StringComparer.Ordinal)
    {
        "DIG_PROJECT_SIZE_UNSUPPORTED", "DIG_XML_INVALID", "DIG_ROOT_MISSING",
        "DIG_CURVES_MISSING", "DIG_NESTED_CURVE_UNSUPPORTED", "DIG_CURVE_IDENTITY_DUPLICATE",
        "DIG_CURVE_POINTS_MISSING", "DIG_NESTED_POINT_UNSUPPORTED", "DIG_UNOWNED_CURVE_POINT",
        "DIG_MIXED_AXIS_AND_CURVE_POINTS", "DIG_AXIS_SCREEN_POSITION", "DIG_AXIS_GRAPH_POSITION",
        "DIG_AXIS_ANCHOR_COUNT", "DIG_AXIS_SCREEN_OUTSIDE_IMAGE", "DIG_CURVE_SCREEN_POSITION",
        "DIG_CURVE_GRAPH_POSITION_AMBIGUOUS", "DIG_CURVE_POINT_OUTSIDE_IMAGE",
        "DIG_POINT_INVENTORY_MISMATCH", "DIG_CURVE_IDENTITY_INVALID", "DIG_AXIS_POINT_FLAG_INVALID",
        "DIG_AXIS_ANCHORS_DEGENERATE", "DIG_GRAPH_COORDINATE_INVALID", "DIG_NONLINEAR_SCALE_UNSUPPORTED",
        "DIG_IMAGE_COUNT_UNSUPPORTED", "DIG_IMAGE_BASE64_INVALID", "DIG_IMAGE_PNG_INVALID",
        "DIG_IMAGE_DIMENSIONS_INVALID", "DIG_IMAGE_PIXEL_COUNT_UNSUPPORTED", "DIG_IMAGE_FORMAT_MISMATCH",
        "DIG_IMAGE_DIMENSION_MISMATCH",
    };

    internal static EngaugeDevLabelAuditResult Run(FrozenDevTrainingSelector selector, CancellationToken token) =>
        RunCore(selector, ReadCounts, token);

    // Injects a fake reader for synthetic tests. It does not authorize a split.
    internal static EngaugeDevLabelAuditResult RunForTest(
        FrozenDevTrainingSelector selector,
        Func<string, string, CancellationToken, EngaugeDevLabelCounts> reader,
        CancellationToken token) => RunCore(selector, reader, token);

    private static EngaugeDevLabelAuditResult RunCore(
        FrozenDevTrainingSelector selector,
        Func<string, string, CancellationToken, EngaugeDevLabelCounts> reader,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(reader);
        IReadOnlyList<FrozenRealCorpusProject> projects = selector.EnumerateSelectedProjects(token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int parsed = 0;
        long anchors = 0;
        long curves = 0;
        long points = 0;
        foreach (FrozenRealCorpusProject project in projects)
        {
            token.ThrowIfCancellationRequested();
            // A changed assignment, altered path, or changed study stops the
            // entire audit before the associated payload is handed to a reader.
            FrozenRealCorpusProject selected = selector.RequireProject(
                project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
            try
            {
                EngaugeDevLabelCounts result = reader(selected.GetProjectPath(), selected.AnonymizedCaseId, token);
                if (result.Anchors < 0 || result.Curves < 0 || result.Points < 0)
                {
                    throw new InvalidDataException("DIG_NEGATIVE_COUNT");
                }
                checked
                {
                    long nextAnchors = anchors + result.Anchors;
                    long nextCurves = curves + result.Curves;
                    long nextPoints = points + result.Points;
                    int nextParsed = parsed + 1;
                    anchors = nextAnchors;
                    curves = nextCurves;
                    points = nextPoints;
                    parsed = nextParsed;
                }
            }
            catch (EngaugeDigTruthException exception)
            {
                string code = SafeParserCodes.Contains(exception.Code) ? exception.Code : "DIG_UNCLASSIFIED_FORMAT_ERROR";
                failures[code] = failures.GetValueOrDefault(code) + 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                const string code = "DIG_UNEXPECTED_READ_FAILURE";
                failures[code] = failures.GetValueOrDefault(code) + 1;
            }
        }

        // Do not publish an aggregate against an inventory that changed while
        // reading the final selected project.
        _ = selector.EnumerateSelectedProjects(token);

        return new EngaugeDevLabelAuditResult(
            metadata.AssignmentSha256, metadata.SelectedInventorySha256,
            projects.Count, parsed, projects.Count - parsed, anchors, curves, points,
            new ReadOnlyDictionary<string, int>(failures),
            LabelsQualityApproved: false, SealedSourceOverlapVerified: false,
            CandidateAuthorized: false, ModelInference: false, OptimizerSteps: 0);
    }

    private static EngaugeDevLabelCounts ReadCounts(string path, string caseId, CancellationToken token)
    {
        EngaugeDigWholeWorkflowTruth truth = EngaugeDigWholeWorkflowTruthAdapter.Read(path, caseId, token);
        return new EngaugeDevLabelCounts(truth.Anchors.Count, truth.Curves.Count,
            truth.Curves.Sum(static curve => (long)curve.Points.Count));
    }
}
