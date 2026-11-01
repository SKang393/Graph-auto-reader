// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Integration.Workflow;
using GraphReader.Phases;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record PhaseValidationSummary(
    int Dividers,
    int InvalidOrDuplicateIds,
    int OutsidePlot,
    int TooClosePairs,
    int InvalidStyleOrSource,
    int InvalidConfidence,
    int InvalidSegmentIds,
    int InvalidPanelIds);

/// <summary>Diagnostic replay of phase validation, with no change to the wrapped adapter.</summary>
internal sealed class ObservedPhaseValidation : IProductionPhaseReasoningAdapter
{
    private readonly ProductionPhaseReasoningAdapter inner = new();
    private readonly Dictionary<Guid, PhaseValidationSummary> observations = [];

    public string AdapterId => inner.AdapterId;

    public bool IsApproved => inner.IsApproved;

    internal PhaseValidationSummary? Find(Guid panelId) => observations.GetValueOrDefault(panelId);

    public Task<ProductionPhaseReasoningEvidence> ResolveAsync(
        ProductionWorkflowDetectionRequest request, PhaseReasoningRequest phaseRequest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PhaseDivider> dividers = new PhaseDividerDetector().Detect(phaseRequest, cancellationToken);
        PhaseValidationSummary summary = Summarize(phaseRequest, dividers);
        if (!observations.TryAdd(request.Panel.ImportedPanel.PanelId, summary))
            throw new InvalidOperationException("Repeated phase diagnostic for the same panel.");
        return inner.ResolveAsync(request, phaseRequest, cancellationToken);
    }

    internal static PhaseValidationSummary Summarize(PhaseReasoningRequest request, IReadOnlyList<PhaseDivider> dividers)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> knownPanels = request.AlignedPanels.Select(static panel => panel.PanelId)
            .Append(request.PanelId).ToHashSet(StringComparer.Ordinal);
        int invalidIds = 0, outside = 0, close = 0, invalidEnum = 0, invalidConfidence = 0, invalidSegments = 0, invalidPanels = 0;
        double previous = double.NegativeInfinity;
        foreach (PhaseDivider divider in dividers.OrderBy(static divider => divider.OriginalX))
        {
            if (!Guid.TryParseExact(divider.DividerId, "D", out _) || !ids.Add(divider.DividerId)) invalidIds++;
            if (!double.IsFinite(divider.OriginalX) || divider.OriginalX <= request.PlotBounds.Left ||
                divider.OriginalX >= request.PlotBounds.Right) outside++;
            if (divider.OriginalX - previous < request.Options.DividerClusterTolerancePixels) close++;
            if (!Enum.IsDefined(divider.Style) || !Enum.IsDefined(divider.Source)) invalidEnum++;
            if (!double.IsFinite(divider.Confidence) || divider.Confidence is < 0 or > 1) invalidConfidence++;
            if (divider.SegmentIds.Any(string.IsNullOrWhiteSpace) ||
                divider.SegmentIds.Distinct(StringComparer.Ordinal).Count() != divider.SegmentIds.Count) invalidSegments++;
            if (divider.SourcePanelIds.Count == 0 ||
                divider.SourcePanelIds.Any(panelId => !Guid.TryParseExact(panelId, "D", out _) || !knownPanels.Contains(panelId)) ||
                divider.SourcePanelIds.Distinct(StringComparer.Ordinal).Count() != divider.SourcePanelIds.Count) invalidPanels++;
            previous = divider.OriginalX;
        }
        return new(dividers.Count, invalidIds, outside, close, invalidEnum, invalidConfidence, invalidSegments, invalidPanels);
    }
}

internal static partial class Program
{
    private static int PhaseValidationSelfTest()
    {
        string project = Guid.NewGuid().ToString("D"), panel = Guid.NewGuid().ToString("D");
        var request = new PhaseReasoningRequest(project, panel, new string('a', 64),
            new PhaseRectangle(0, 0, 200, 200), [], [], [], []);
        PhaseDivider[] dividers =
        [
            new(Guid.NewGuid().ToString("D"), 100, PhaseDividerStyle.Solid, [], [panel], 1, PhaseEvidenceSource.ProfilePrior),
            new(Guid.NewGuid().ToString("D"), 102, PhaseDividerStyle.Solid, [], [panel], 1, PhaseEvidenceSource.ProfilePrior),
        ];
        PhaseValidationSummary summary = ObservedPhaseValidation.Summarize(request, dividers);
        Require(summary.Dividers == 2 && summary.TooClosePairs == 1 && summary.OutsidePlot == 0 &&
            summary.InvalidOrDuplicateIds == 0 && summary.InvalidStyleOrSource == 0 && summary.InvalidConfidence == 0 &&
            summary.InvalidSegmentIds == 0 && summary.InvalidPanelIds == 0, "Synthetic phase validation diagnosis differs.");
        Console.WriteLine("phase_validation_self_test=passed; production_approval=false");
        return 0;
    }
}
