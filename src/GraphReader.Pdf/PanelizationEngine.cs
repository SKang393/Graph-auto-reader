// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Pdf;

/// <summary>
/// Proposes graph figures and vertically stacked panels from already extracted PDF geometry.
/// The engine does not perform OCR, raster inference, or semantic confirmation.
/// </summary>
public sealed class PanelizationEngine : IPdfPanelizationEngine
{
    // Fixed geometry thresholds. The only caller-tunable acceptance values are exposed by
    // PdfPanelizationOptions so identical inputs and options always produce identical proposals.
    private const double MinimumAxisLengthPoints = 24d;
    private const double MinimumHorizontalPageFraction = 0.12d;
    private const double MinimumVerticalPageFraction = 0.05d;
    private const double AxisEndpointTolerancePoints = 2.5d;
    private const double AxisColumnTolerancePoints = 4d;
    private const double MaximumPlotWidthDifferenceFraction = 0.12d;
    private const double MaximumPanelGapPoints = 72d;
    private const double MinimumDenseLineCount = 6d;
    private const double MinimumPanelHeightPixels = 1d;
    private const double DuplicateOverlapFraction = 0.80d;
    private const int MaximumRasterPixels = 40_000_000;
    private const byte RasterInkLumaThreshold = 200;
    private const int RasterLineGapTolerancePixels = 2;
    private const int RasterAxisEndpointTolerancePixels = 4;

    private const double EmbeddedImageWeight = 0.48d;
    private const double HorizontalAxisWeight = 0.20d;
    private const double VerticalAxisWeight = 0.20d;
    private const double DenseLineWeight = 0.16d;
    private const double CaptionWeight = 0.18d;
    private const double RepeatedAxesWeight = 0.08d;
    private const double AlignedPlotWidthsWeight = 0.05d;
    private const double AlignedYAxisColumnsWeight = 0.05d;
    private const double ParticipantLabelWeight = 0.05d;
    private const double WhitespaceValleyWeight = 0.04d;
    private const double SharedDividerWeight = 0.05d;
    private const double TableLikePenalty = 0.35d;
    private const double TextHeavyPenalty = 0.25d;

    /// <inheritdoc />
    public Task<PdfPanelizationResult> ProposeAsync(
        PdfPanelizationInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!TryCreateContext(input, out PanelizationContext? context, out PdfFailure? failure))
        {
            stopwatch.Stop();
            return Task.FromResult(new PdfPanelizationResult(
                [],
                [],
                [failure!],
                elapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds));
        }

        List<string> warnings = [];
        if (input.RenderedPage is not null)
        {
            if (TryAnalyzeRenderedPng(
                input.RenderedPage,
                context!.Transform,
                cancellationToken,
                out RasterAnalysis? raster,
                out string? rasterWarning))
            {
                context = context with { Raster = raster };
            }
            else if (context!.Page.VectorLines.Count == 0 &&
                context.Page.EmbeddedImages.Count == 0)
            {
                warnings.Add(rasterWarning!);
            }
        }

        List<AxisPair> axes = DetectAxes(context!, cancellationToken);
        int rejectedCount = 0;
        List<CandidateDraft> drafts = BuildEmbeddedCandidates(
            context!,
            axes,
            cancellationToken,
            ref rejectedCount);

        HashSet<AxisPair> embeddedAxes = drafts
            .Where(static draft => draft.Figure.SourceKind == PdfFigureSourceKind.EmbeddedImage)
            .SelectMany(static draft => draft.Axes)
            .ToHashSet();
        List<AxisPair> remainingAxes = axes
            .Where(axis => !embeddedAxes.Contains(axis))
            .ToList();

        drafts.AddRange(BuildVectorCandidates(
            context!,
            remainingAxes,
            cancellationToken,
            ref rejectedCount));

        List<CandidateDraft> selected = SelectNonOverlappingCandidates(drafts, cancellationToken);
        selected.Sort(CandidateLayoutComparer.Instance);

        List<PdfPanelRecord> panels = [];
        foreach (CandidateDraft draft in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            panels.AddRange(CreateAutomaticPanels(context!, draft, cancellationToken));
        }

        panels = RenumberPanels(panels);
        if (selected.Count == 0)
        {
            warnings.Add("No graph-like figure met the fixed evidence threshold.");
        }

        if (rejectedCount > 0)
        {
            warnings.Add(FormattableString.Invariant(
                $"Rejected {rejectedCount} false or low-confidence figure proposal(s)."));
        }

        stopwatch.Stop();
        return Task.FromResult(new PdfPanelizationResult(
            selected.Select(static draft => draft.Figure),
            panels,
            warnings: warnings,
            elapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds));
    }

    /// <inheritdoc />
    public PdfPanelizationResult ApplySplit(
        PdfPanelizationResult current,
        PdfManualSplitCommand command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);

        PdfFigureCandidate? figure = current.Figures.FirstOrDefault(
            candidate => candidate.FigureId == command.FigureId);
        if (figure is null)
        {
            return WithFailure(
                current,
                "PDF_PANEL_FIGURE_NOT_FOUND",
                "The requested figure does not exist in the current panelization result.");
        }

        if (command.HorizontalBoundariesPagePixels is null ||
            command.HorizontalBoundariesPagePixels.Count == 0)
        {
            return WithFailure(
                current,
                "PDF_PANEL_SPLIT_INVALID",
                "A manual split requires at least one horizontal page-pixel boundary.");
        }

        double minimum = figure.BoundsPagePixels.Y;
        double maximum = figure.BoundsPagePixels.Bottom;
        double[] boundaries = command.HorizontalBoundariesPagePixels
            .Where(double.IsFinite)
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();

        if (boundaries.Length != command.HorizontalBoundariesPagePixels.Count ||
            boundaries.Any(value => value <= minimum + MinimumPanelHeightPixels ||
                value >= maximum - MinimumPanelHeightPixels))
        {
            return WithFailure(
                current,
                "PDF_PANEL_SPLIT_INVALID",
                "Manual split boundaries must be unique, finite, and strictly inside the figure.");
        }

        List<PdfRectD> segments = CreateHorizontalSegments(figure.BoundsPagePixels, boundaries);
        if (segments.Any(static segment => segment.Height < MinimumPanelHeightPixels))
        {
            return WithFailure(
                current,
                "PDF_PANEL_SPLIT_INVALID",
                "Manual split boundaries would create an empty panel.");
        }

        IReadOnlyList<PdfPanelRecord> previous = current.Panels
            .Where(panel => panel.FigureId == figure.FigureId)
            .ToArray();
        List<PdfPanelRecord> replacement = [];
        for (int index = 0; index < segments.Count; index++)
        {
            PdfRectD segment = segments[index];
            PdfPanelRecord? source = previous
                .OrderByDescending(panel => IntersectionArea(panel.BoundsPagePixels, segment))
                .ThenBy(static panel => panel.Order)
                .FirstOrDefault();
            IReadOnlyList<PdfPanelEvidence> evidence = AddManualEvidence(
                source?.Evidence ?? figure.Evidence,
                "Panel boundary supplied by a user split command.");
            replacement.Add(new PdfPanelRecord(
                CreateDeterministicGuid(FormatIdentity(
                    "manual-split",
                    figure.FigureId,
                    segment)),
                figure.FigureId,
                figure.PageNumber,
                index + 1,
                PageBoundsToSourceCrop(figure, segment),
                segment,
                PagePixelsToFigurePoints(figure, segment),
                source?.ParticipantLabel,
                source?.Caption ?? figure.Caption,
                source?.SemanticSuggestions ?? [],
                evidence,
                source?.Confidence ?? figure.Confidence,
                PageBoundsToSourceCropQuadrilateral(figure, segment)));
        }

        List<PdfPanelRecord> combined = current.Panels
            .Where(panel => panel.FigureId != figure.FigureId)
            .Concat(replacement)
            .ToList();
        return new PdfPanelizationResult(
            current.Figures,
            RenumberPanels(combined),
            current.Failures,
            current.Warnings,
            current.ElapsedMilliseconds);
    }

    /// <inheritdoc />
    public PdfPanelizationResult ApplyMerge(
        PdfPanelizationResult current,
        PdfManualMergeCommand command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);

        if (command.PanelIds is null || command.PanelIds.Count < 2)
        {
            return WithFailure(
                current,
                "PDF_PANEL_MERGE_INVALID",
                "A manual merge requires at least two panel identifiers.");
        }

        Guid[] requestedIds = command.PanelIds.Distinct().ToArray();
        if (requestedIds.Length != command.PanelIds.Count)
        {
            return WithFailure(
                current,
                "PDF_PANEL_MERGE_INVALID",
                "Manual merge panel identifiers must be unique.");
        }

        List<PdfPanelRecord> selected = current.Panels
            .Where(panel => requestedIds.Contains(panel.PanelId))
            .OrderBy(static panel => panel.BoundsPagePixels.Y)
            .ThenBy(static panel => panel.BoundsPagePixels.X)
            .ToList();
        if (selected.Count != requestedIds.Length)
        {
            return WithFailure(
                current,
                "PDF_PANEL_NOT_FOUND",
                "At least one requested panel does not exist in the current result.");
        }

        Guid figureId = selected[0].FigureId;
        int pageNumber = selected[0].PageNumber;
        if (selected.Any(panel => panel.FigureId != figureId || panel.PageNumber != pageNumber))
        {
            return WithFailure(
                current,
                "PDF_PANEL_MERGE_INVALID",
                "Only panels from the same figure and page can be merged.");
        }

        List<PdfPanelRecord> figurePanels = current.Panels
            .Where(panel => panel.FigureId == figureId)
            .OrderBy(static panel => panel.BoundsPagePixels.Y)
            .ThenBy(static panel => panel.BoundsPagePixels.X)
            .ToList();
        int[] selectedIndices = selected
            .Select(panel => figurePanels.FindIndex(candidate => candidate.PanelId == panel.PanelId))
            .OrderBy(static index => index)
            .ToArray();
        if (selectedIndices.Zip(selectedIndices.Skip(1), static (left, right) => right - left)
            .Any(static difference => difference != 1))
        {
            return WithFailure(
                current,
                "PDF_PANEL_MERGE_NONADJACENT",
                "Manual merge panels must be adjacent in layout order.");
        }

        PdfRectD boundsPixels = Union(selected.Select(static panel => panel.BoundsPagePixels));
        PdfRectD boundsPoints = Union(selected.Select(static panel => panel.BoundsPagePoints));
        PdfRectD crop = Union(selected.Select(static panel => panel.CropInSourcePixels));
        PdfFigureCandidate figure = current.Figures.Single(candidate => candidate.FigureId == figureId);
        string? participant = CommonValue(selected.Select(static panel => panel.ParticipantLabel));
        string? caption = CommonValue(selected.Select(static panel => panel.Caption));
        IReadOnlyList<PdfSemanticSuggestion> suggestions = MergeSuggestions(
            selected.SelectMany(static panel => panel.SemanticSuggestions));
        IReadOnlyList<PdfPanelEvidence> evidence = AddManualEvidence(
            selected.SelectMany(static panel => panel.Evidence),
            "Adjacent panels combined by a user merge command.");
        double confidence = selected.Average(static panel => panel.Confidence);
        string panelIdentity = string.Join(
            ",",
            selected.Select(static panel => panel.PanelId).OrderBy(static id => id));

        PdfPanelRecord merged = new(
            CreateDeterministicGuid(FormattableString.Invariant($"manual-merge|{figureId}|{panelIdentity}")),
            figureId,
            pageNumber,
            selected.Min(static panel => panel.Order),
            crop,
            boundsPixels,
            boundsPoints,
            participant,
            caption,
            suggestions,
            evidence,
            Clamp01(confidence),
            PageBoundsToSourceCropQuadrilateral(figure, boundsPixels));

        HashSet<Guid> removed = selected.Select(static panel => panel.PanelId).ToHashSet();
        List<PdfPanelRecord> combined = current.Panels
            .Where(panel => !removed.Contains(panel.PanelId))
            .Append(merged)
            .ToList();
        return new PdfPanelizationResult(
            current.Figures,
            RenumberPanels(combined),
            current.Failures,
            current.Warnings,
            current.ElapsedMilliseconds);
    }

    private static bool TryCreateContext(
        PdfPanelizationInput input,
        out PanelizationContext? context,
        out PdfFailure? failure)
    {
        context = null;
        failure = null;
        if (input.Page is null || input.Options is null)
        {
            failure = CreateFailure(
                "PDF_PANEL_INPUT_INVALID",
                "The page snapshot and panelization options are required.");
            return false;
        }

        PdfPageSnapshot page = input.Page;
        PdfPanelizationOptions options = input.Options;
        if (page.PageNumber < 1 ||
            !double.IsFinite(page.WidthPoints) ||
            !double.IsFinite(page.HeightPoints) ||
            page.WidthPoints <= 0d ||
            page.HeightPoints <= 0d ||
            options.RenderDpi <= 0 ||
            !double.IsFinite(options.MinimumFigureConfidence) ||
            options.MinimumFigureConfidence < 0d ||
            options.MinimumFigureConfidence > 1d ||
            !double.IsFinite(options.MinimumWhitespaceFraction) ||
            options.MinimumWhitespaceFraction < 0d ||
            options.MinimumWhitespaceFraction >= 1d ||
            options.MaximumPanelsPerFigure < 1)
        {
            failure = CreateFailure(
                "PDF_PANEL_INPUT_INVALID",
                "Page geometry and panelization options must be finite and within their documented ranges.",
                page.PageNumber);
            return false;
        }

        int pixelWidth;
        int pixelHeight;
        if (input.RenderedPage is not null)
        {
            pixelWidth = input.RenderedPage.Width;
            pixelHeight = input.RenderedPage.Height;
        }
        else
        {
            double width = Math.Ceiling(page.WidthPoints * options.RenderDpi / 72d);
            double height = Math.Ceiling(page.HeightPoints * options.RenderDpi / 72d);
            if (!double.IsFinite(width) ||
                !double.IsFinite(height) ||
                width < 1d ||
                height < 1d ||
                width > int.MaxValue ||
                height > int.MaxValue)
            {
                failure = CreateFailure(
                    "PDF_PANEL_INPUT_INVALID",
                    "The requested page raster dimensions are invalid.",
                    page.PageNumber);
                return false;
            }

            pixelWidth = (int)width;
            pixelHeight = (int)height;
        }

        if (pixelWidth < 1 || pixelHeight < 1)
        {
            failure = CreateFailure(
                "PDF_PANEL_RENDER_INVALID",
                "The rendered page dimensions must be positive.",
                page.PageNumber);
            return false;
        }

        PdfPageCoordinateTransform transform = new(
            page.WidthPoints,
            page.HeightPoints,
            pixelWidth,
            pixelHeight);
        context = new PanelizationContext(input, transform);
        return true;
    }

    private static List<AxisPair> DetectAxes(
        PanelizationContext context,
        CancellationToken cancellationToken)
    {
        double horizontalMinimum = Math.Max(
            MinimumAxisLengthPoints,
            context.Page.WidthPoints * MinimumHorizontalPageFraction);
        double verticalMinimum = Math.Max(
            MinimumAxisLengthPoints,
            context.Page.HeightPoints * MinimumVerticalPageFraction);

        List<NormalizedLine> horizontal = [];
        List<NormalizedLine> vertical = [];
        foreach (PdfVectorLine line in context.Page.VectorLines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartPagePoints.IsFinite ||
                !line.EndPagePoints.IsFinite ||
                !double.IsFinite(line.WidthPoints))
            {
                continue;
            }

            if (line.IsHorizontal(AxisEndpointTolerancePoints))
            {
                NormalizedLine normalized = NormalizeHorizontal(line);
                if (normalized.Length >= horizontalMinimum)
                {
                    horizontal.Add(normalized);
                }
            }

            if (line.IsVertical(AxisEndpointTolerancePoints))
            {
                NormalizedLine normalized = NormalizeVertical(line);
                if (normalized.Length >= verticalMinimum)
                {
                    vertical.Add(normalized);
                }
            }
        }

        List<AxisPair> candidates = [];
        foreach (NormalizedLine xAxis in horizontal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (NormalizedLine yAxis in vertical)
            {
                double tolerance = AxisEndpointTolerancePoints +
                    (Math.Max(xAxis.Width, yAxis.Width) / 2d);
                if (Math.Abs(yAxis.FixedCoordinate - xAxis.Start) > tolerance ||
                    Math.Abs(xAxis.FixedCoordinate - yAxis.Start) > tolerance)
                {
                    continue;
                }

                double width = xAxis.End - yAxis.FixedCoordinate;
                double height = yAxis.End - xAxis.FixedCoordinate;
                if (width < horizontalMinimum || height < verticalMinimum)
                {
                    continue;
                }

                candidates.Add(new AxisPair(new PdfRectD(
                    yAxis.FixedCoordinate,
                    xAxis.FixedCoordinate,
                    width,
                    height),
                    FromRaster: false));
            }
        }

        if (context.Raster is not null)
        {
            candidates.AddRange(context.Raster.Axes);
        }

        List<AxisPair> deduplicated = [];
        foreach (AxisPair candidate in candidates
            .OrderByDescending(static pair => pair.PlotBoundsPagePoints.Y)
            .ThenBy(static pair => pair.PlotBoundsPagePoints.X)
            .ThenByDescending(static pair => pair.PlotBoundsPagePoints.Area))
        {
            if (deduplicated.Any(existing => AreDuplicateAxes(existing, candidate)))
            {
                continue;
            }

            deduplicated.Add(candidate);
        }

        return deduplicated;
    }

    private static List<CandidateDraft> BuildEmbeddedCandidates(
        PanelizationContext context,
        IReadOnlyList<AxisPair> allAxes,
        CancellationToken cancellationToken,
        ref int rejectedCount)
    {
        List<CandidateDraft> candidates = [];
        PdfRectD pageBounds = new(0d, 0d, context.Page.WidthPoints, context.Page.HeightPoints);
        foreach (PdfEmbeddedImage image in context.Page.EmbeddedImages
            .OrderByDescending(static image => image.BoundsPagePoints.Y)
            .ThenBy(static image => image.BoundsPagePoints.X)
            .ThenBy(static image => image.ImageId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!image.BoundsPagePoints.IsValid ||
                !pageBounds.Contains(image.BoundsPagePoints) ||
                image.PixelWidth < 1 ||
                image.PixelHeight < 1)
            {
                rejectedCount++;
                continue;
            }

            List<AxisPair> axes = allAxes
                .Where(axis => ContainsMostly(image.BoundsPagePoints, axis.PlotBoundsPagePoints))
                .ToList();
            CandidateEvidence evidence = EvaluateEvidence(
                context,
                image.BoundsPagePoints,
                axes,
                isEmbedded: true);
            if (evidence.Confidence < context.Options.MinimumFigureConfidence)
            {
                rejectedCount++;
                continue;
            }

            PdfRectD boundsPixels = context.Transform.PagePointsToPixels(image.BoundsPagePoints);
            PdfFigureCandidate figure = new(
                CreateDeterministicGuid(FormatIdentity(
                    context.Input.DocumentSha256,
                    context.Page.PageNumber,
                    PdfFigureSourceKind.EmbeddedImage,
                    image.ImageId,
                    boundsPixels)),
                context.Page.PageNumber,
                PdfFigureSourceKind.EmbeddedImage,
                image.ImageId,
                boundsPixels,
                image.BoundsPagePoints,
                image.PixelWidth,
                image.PixelHeight,
                image.EncodedBytes,
                image.MediaType,
                evidence.Caption?.Text,
                evidence.Items,
                evidence.Confidence,
                context.Transform.PagePointsToPagePixelsMatrix,
                image.SourcePixelsToPagePoints);
            candidates.Add(new CandidateDraft(figure, axes));
        }

        return candidates;
    }

    private static List<CandidateDraft> BuildVectorCandidates(
        PanelizationContext context,
        IReadOnlyList<AxisPair> axes,
        CancellationToken cancellationToken,
        ref int rejectedCount)
    {
        List<CandidateDraft> candidates = [];
        foreach (IReadOnlyList<AxisPair> group in GroupAlignedAxes(
            axes,
            context.Options.MaximumPanelsPerFigure,
            cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfRectD plotUnion = Union(group.Select(static axis => axis.PlotBoundsPagePoints));
            PdfRectD boundsPoints = ExpandAndClip(
                plotUnion,
                Math.Max(12d, plotUnion.Width * 0.12d),
                Math.Max(8d, plotUnion.Width * 0.05d),
                Math.Max(10d, plotUnion.Height * 0.10d),
                Math.Max(10d, plotUnion.Height * 0.08d),
                context.Page.WidthPoints,
                context.Page.HeightPoints);
            CandidateEvidence evidence = EvaluateEvidence(
                context,
                boundsPoints,
                group,
                isEmbedded: false);
            if (evidence.Confidence < context.Options.MinimumFigureConfidence)
            {
                rejectedCount++;
                continue;
            }

            PdfRectD boundsPixels = context.Transform.PagePointsToPixels(boundsPoints);
            PdfFigureSourceKind sourceKind = group.Any(static axis => axis.FromRaster)
                ? PdfFigureSourceKind.RenderedPage
                : PdfFigureSourceKind.VectorPageRegion;
            PdfFigureCandidate figure = new(
                CreateDeterministicGuid(FormatIdentity(
                    context.Input.DocumentSha256,
                    context.Page.PageNumber,
                    sourceKind,
                    Guid.Empty,
                    boundsPixels)),
                context.Page.PageNumber,
                sourceKind,
                null,
                boundsPixels,
                boundsPoints,
                context.Transform.PixelWidth,
                context.Transform.PixelHeight,
                context.Input.RenderedPage?.PngBytes,
                context.Input.RenderedPage?.MediaType,
                evidence.Caption?.Text,
                evidence.Items,
                evidence.Confidence,
                context.Transform.PagePointsToPagePixelsMatrix,
                context.Transform.PagePixelsToPagePointsMatrix);
            candidates.Add(new CandidateDraft(figure, group));
        }

        return candidates;
    }

    private static CandidateEvidence EvaluateEvidence(
        PanelizationContext context,
        PdfRectD bounds,
        IReadOnlyList<AxisPair> axes,
        bool isEmbedded)
    {
        List<PdfPanelEvidence> evidence = [];
        if (isEmbedded)
        {
            evidence.Add(new PdfPanelEvidence(
                PdfPanelEvidenceKind.EmbeddedImage,
                EmbeddedImageWeight,
                "The original embedded image is preserved as the preferred figure source."));
        }

        if (axes.Count > 0)
        {
            evidence.Add(new PdfPanelEvidence(
                PdfPanelEvidenceKind.HorizontalAxis,
                HorizontalAxisWeight,
                "A long horizontal axis terminates at a vertical axis."));
            evidence.Add(new PdfPanelEvidence(
                PdfPanelEvidenceKind.VerticalAxis,
                VerticalAxisWeight,
                "A long vertical axis originates at a horizontal axis."));
        }

        PdfRectD boundsPixels = context.Transform.PagePointsToPixels(bounds);
        int denseLines = CountLinesInBounds(context.Page.VectorLines, bounds) +
            (context.Raster?.CountStructuralLines(boundsPixels) ?? 0);
        if (denseLines >= MinimumDenseLineCount)
        {
            evidence.Add(new PdfPanelEvidence(
                PdfPanelEvidenceKind.DenseLineStructure,
                DenseLineWeight,
                FormattableString.Invariant($"The region contains {denseLines} vector line segments.")));
        }

        PdfTextBlock? caption = FindCaption(context.Page.TextBlocks, bounds);
        bool tableCaption = caption is not null && IsTableCaption(caption.Text);
        if (caption is not null && !tableCaption)
        {
            evidence.Add(new PdfPanelEvidence(
                PdfPanelEvidenceKind.CaptionProximity,
                CaptionWeight,
                "A nearby non-table caption supports the figure proposal."));
        }

        if (axes.Count >= 2)
        {
            evidence.Add(new PdfPanelEvidence(
                PdfPanelEvidenceKind.RepeatedAxes,
                RepeatedAxesWeight,
                FormattableString.Invariant($"Detected {axes.Count} repeated plot-axis pairs.")));
            if (HaveAlignedWidths(axes))
            {
                evidence.Add(new PdfPanelEvidence(
                    PdfPanelEvidenceKind.AlignedPlotWidths,
                    AlignedPlotWidthsWeight,
                    "Repeated plot widths agree within the fixed 12 percent tolerance."));
            }

            if (HaveAlignedYAxisColumns(axes))
            {
                evidence.Add(new PdfPanelEvidence(
                    PdfPanelEvidenceKind.AlignedYAxisColumns,
                    AlignedYAxisColumnsWeight,
                    "Repeated y-axis columns agree within the fixed geometric tolerance."));
            }

            if (HasWhitespaceValley(context, axes, bounds))
            {
                evidence.Add(new PdfPanelEvidence(
                    PdfPanelEvidenceKind.WhitespaceValley,
                    WhitespaceValleyWeight,
                    "A low-occupancy whitespace valley separates adjacent plots."));
            }

            if (HasSharedDivider(context.Page.VectorLines, axes))
            {
                evidence.Add(new PdfPanelEvidence(
                    PdfPanelEvidenceKind.SharedDivider,
                    SharedDividerWeight,
                    "A repeated relative divider position is shared across panels."));
            }
        }

        if (FindParticipantLabel(context.Page.TextBlocks, bounds) is not null)
        {
            evidence.Add(new PdfPanelEvidence(
                PdfPanelEvidenceKind.ParticipantLabel,
                ParticipantLabelWeight,
                "A participant-labeled text block is associated with the plot region."));
        }

        double confidence = evidence.Sum(static item => item.Weight);
        if (tableCaption ||
            IsTableLike(context.Page.VectorLines, bounds) ||
            (context.Raster?.IsTableLike(boundsPixels) ?? false))
        {
            confidence -= TableLikePenalty;
        }

        if (IsTextHeavy(context.Page.TextBlocks, bounds))
        {
            confidence -= TextHeavyPenalty;
        }

        return new CandidateEvidence(
            evidence,
            caption,
            Clamp01(confidence));
    }

    private static List<IReadOnlyList<AxisPair>> GroupAlignedAxes(
        IReadOnlyList<AxisPair> axes,
        int maximumPanels,
        CancellationToken cancellationToken)
    {
        List<AxisPair> ordered = axes
            .OrderByDescending(static axis => axis.PlotBoundsPagePoints.Y)
            .ThenBy(static axis => axis.PlotBoundsPagePoints.X)
            .ToList();
        bool[] visited = new bool[ordered.Count];
        List<IReadOnlyList<AxisPair>> output = [];
        for (int index = 0; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited[index])
            {
                continue;
            }

            Queue<int> queue = new();
            List<AxisPair> component = [];
            visited[index] = true;
            queue.Enqueue(index);
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int current = queue.Dequeue();
                component.Add(ordered[current]);
                for (int candidate = 0; candidate < ordered.Count; candidate++)
                {
                    if (visited[candidate] || !CanShareFigure(ordered[current], ordered[candidate]))
                    {
                        continue;
                    }

                    visited[candidate] = true;
                    queue.Enqueue(candidate);
                }
            }

            AxisPair[] sorted = component
                .OrderByDescending(static axis => axis.PlotBoundsPagePoints.Y)
                .ThenBy(static axis => axis.PlotBoundsPagePoints.X)
                .ToArray();
            for (int start = 0; start < sorted.Length; start += maximumPanels)
            {
                output.Add(sorted.Skip(start).Take(maximumPanels).ToArray());
            }
        }

        return output;
    }

    private static List<CandidateDraft> SelectNonOverlappingCandidates(
        IEnumerable<CandidateDraft> candidates,
        CancellationToken cancellationToken)
    {
        List<CandidateDraft> selected = [];
        foreach (CandidateDraft candidate in candidates
            .OrderBy(static draft => draft.Figure.SourceKind == PdfFigureSourceKind.EmbeddedImage ? 0 : 1)
            .ThenByDescending(static draft => draft.Figure.Confidence)
            .ThenByDescending(static draft => draft.Figure.BoundsPagePixels.Area)
            .ThenBy(static draft => draft.Figure.FigureId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Any(existing => HasMaterialOverlap(
                existing.Figure.BoundsPagePixels,
                candidate.Figure.BoundsPagePixels)))
            {
                continue;
            }

            selected.Add(candidate);
        }

        return selected;
    }

    private static List<PdfPanelRecord> CreateAutomaticPanels(
        PanelizationContext context,
        CandidateDraft draft,
        CancellationToken cancellationToken)
    {
        List<PdfRectD> segments;
        if (draft.Axes.Count < 2)
        {
            segments = [draft.Figure.BoundsPagePixels];
        }
        else
        {
            PdfRectD[] plots = draft.Axes
                .Select(axis => context.Transform.PagePointsToPixels(axis.PlotBoundsPagePoints))
                .OrderBy(static bounds => bounds.Y)
                .ThenBy(static bounds => bounds.X)
                .ToArray();
            List<double> boundaries = [];
            for (int index = 0; index < plots.Length - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double lowerEdge = plots[index].Bottom;
                double upperEdge = plots[index + 1].Y;
                double boundary = upperEdge >= lowerEdge
                    ? (lowerEdge + upperEdge) / 2d
                    : ((plots[index].Y + (plots[index].Height / 2d)) +
                        (plots[index + 1].Y + (plots[index + 1].Height / 2d))) / 2d;
                boundary = Math.Clamp(
                    boundary,
                    draft.Figure.BoundsPagePixels.Y + MinimumPanelHeightPixels,
                    draft.Figure.BoundsPagePixels.Bottom - MinimumPanelHeightPixels);
                if (boundaries.Count == 0 ||
                    boundary - boundaries[^1] >= MinimumPanelHeightPixels)
                {
                    boundaries.Add(boundary);
                }
            }

            segments = CreateHorizontalSegments(draft.Figure.BoundsPagePixels, boundaries);
        }

        List<PdfPanelRecord> panels = [];
        for (int index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfRectD boundsPixels = segments[index];
            PdfRectD boundsPoints = PagePixelsToFigurePoints(draft.Figure, boundsPixels);
            PdfTextBlock? participant = FindParticipantLabel(context.Page.TextBlocks, boundsPoints);
            IReadOnlyList<PdfSemanticSuggestion> suggestions = CreateSemanticSuggestions(
                context.Page.TextBlocks,
                boundsPoints,
                draft.Figure.Caption);
            panels.Add(new PdfPanelRecord(
                CreateDeterministicGuid(FormatIdentity(
                    "auto-panel",
                    draft.Figure.FigureId,
                    boundsPixels)),
                draft.Figure.FigureId,
                draft.Figure.PageNumber,
                index + 1,
                PageBoundsToSourceCrop(draft.Figure, boundsPixels),
                boundsPixels,
                boundsPoints,
                NormalizeText(participant?.Text),
                draft.Figure.Caption,
                suggestions,
                draft.Figure.Evidence,
                draft.Figure.Confidence,
                PageBoundsToSourceCropQuadrilateral(draft.Figure, boundsPixels)));
        }

        return panels;
    }

    private static PdfSemanticSuggestion[] CreateSemanticSuggestions(
        IReadOnlyList<PdfTextBlock> textBlocks,
        PdfRectD panelBounds,
        string? caption)
    {
        List<PdfSemanticSuggestion> suggestions = [];
        List<PdfTextBlock> axisTitles = textBlocks
            .Where(block => block.Role == PdfTextRole.AxisTitle &&
                !string.IsNullOrWhiteSpace(block.Text) &&
                DistanceBetween(block.BoundsPagePoints, panelBounds) <= 18d)
            .OrderByDescending(static block => block.Confidence)
            .ThenBy(static block => block.BlockId)
            .ToList();

        PdfTextBlock? dependent = axisTitles
            .Where(block => CenterX(block.BoundsPagePoints) <= panelBounds.X + (panelBounds.Width * 0.20d))
            .OrderBy(block => Math.Abs(CenterX(block.BoundsPagePoints) - panelBounds.X))
            .ThenByDescending(static block => block.Confidence)
            .FirstOrDefault();
        if (dependent is not null)
        {
            suggestions.Add(CreateSuggestion(
                PdfSemanticField.DependentVariable,
                dependent.Text,
                dependent.Text,
                dependent.Confidence * 0.75d));
        }

        PdfTextBlock? independent = axisTitles
            .Where(block => block.BlockId != dependent?.BlockId &&
                CenterY(block.BoundsPagePoints) <= panelBounds.Y + (panelBounds.Height * 0.20d) &&
                CenterX(block.BoundsPagePoints) >= panelBounds.X &&
                CenterX(block.BoundsPagePoints) <= panelBounds.Right)
            .OrderBy(block => Math.Abs(CenterY(block.BoundsPagePoints) - panelBounds.Y))
            .ThenByDescending(static block => block.Confidence)
            .FirstOrDefault();
        if (independent is not null)
        {
            suggestions.Add(CreateSuggestion(
                PdfSemanticField.IndependentVariable,
                independent.Text,
                independent.Text,
                independent.Confidence * 0.75d));
        }

        AddExplicitSemanticSuggestion(
            suggestions,
            PdfSemanticField.DependentVariable,
            caption,
            "dependent variable");
        AddExplicitSemanticSuggestion(
            suggestions,
            PdfSemanticField.IndependentVariable,
            caption,
            "independent variable");

        return MergeSuggestions(suggestions);
    }

    private static void AddExplicitSemanticSuggestion(
        List<PdfSemanticSuggestion> suggestions,
        PdfSemanticField field,
        string? sourceText,
        string label)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return;
        }

        int index = sourceText.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return;
        }

        int valueStart = index + label.Length;
        while (valueStart < sourceText.Length &&
            (char.IsWhiteSpace(sourceText[valueStart]) ||
                sourceText[valueStart] is ':' or '-' or '='))
        {
            valueStart++;
        }

        if (valueStart >= sourceText.Length)
        {
            return;
        }

        int valueEnd = sourceText.IndexOfAny([';', '.', '\r', '\n'], valueStart);
        if (valueEnd < 0)
        {
            valueEnd = sourceText.Length;
        }

        string value = sourceText[valueStart..valueEnd].Trim();
        if (value.Length == 0 || value.Length > 160)
        {
            return;
        }

        suggestions.Add(CreateSuggestion(field, value, sourceText, 0.55d));
    }

    private static PdfSemanticSuggestion CreateSuggestion(
        PdfSemanticField field,
        string value,
        string sourceText,
        double confidence) =>
        new(
            field,
            value.Trim(),
            sourceText,
            Clamp01(confidence),
            PdfSuggestionReviewState.Suggested);

    private static PdfSemanticSuggestion[] MergeSuggestions(
        IEnumerable<PdfSemanticSuggestion> suggestions) =>
        suggestions
            .Where(static suggestion => !string.IsNullOrWhiteSpace(suggestion.Value))
            .GroupBy(
                static suggestion => (suggestion.Field, suggestion.Value),
                SemanticSuggestionKeyComparer.Instance)
            .Select(static group => group
                .OrderByDescending(static suggestion => ReviewStatePriority(suggestion.ReviewState))
                .ThenByDescending(static suggestion => suggestion.Confidence)
                .ThenBy(static suggestion => suggestion.SourceText, StringComparer.Ordinal)
                .First())
            .OrderBy(static suggestion => suggestion.Field)
            .ThenByDescending(static suggestion => suggestion.Confidence)
            .ThenBy(static suggestion => suggestion.Value, StringComparer.Ordinal)
            .ToArray();

    private static PdfPanelEvidence[] AddManualEvidence(
        IEnumerable<PdfPanelEvidence> source,
        string detail) =>
        source
            .Append(new PdfPanelEvidence(PdfPanelEvidenceKind.Manual, 1d, detail))
            .GroupBy(static item => (item.Kind, item.Detail))
            .Select(static group => group.OrderByDescending(static item => item.Weight).First())
            .OrderBy(static item => item.Kind)
            .ThenBy(static item => item.Detail, StringComparer.Ordinal)
            .ToArray();

    private static bool TryAnalyzeRenderedPng(
        PdfRenderedPage renderedPage,
        PdfPageCoordinateTransform transform,
        CancellationToken cancellationToken,
        out RasterAnalysis? analysis,
        out string? warning)
    {
        analysis = null;
        warning = null;
        ReadOnlySpan<byte> png = renderedPage.PngBytes.Memory.Span;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < signature.Length || !png[..signature.Length].SequenceEqual(signature))
        {
            warning = "Rendered page raster evidence was unavailable because the PNG signature is invalid.";
            return false;
        }

        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        bool sawHeader = false;
        bool sawEnd = false;
        using MemoryStream compressed = new();
        int offset = signature.Length;
        while (offset <= png.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint chunkLengthValue = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (chunkLengthValue > int.MaxValue)
            {
                warning = "Rendered page raster evidence was unavailable because a PNG chunk is too large.";
                return false;
            }

            int chunkLength = (int)chunkLengthValue;
            int dataStart = offset + 8;
            long chunkEnd = (long)dataStart + chunkLength + 4L;
            if (chunkEnd > png.Length)
            {
                warning = "Rendered page raster evidence was unavailable because the PNG is truncated.";
                return false;
            }

            ReadOnlySpan<byte> chunkType = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> chunkData = png.Slice(dataStart, chunkLength);
            if (chunkType.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || chunkLength != 13)
                {
                    warning = "Rendered page raster evidence was unavailable because the PNG header is invalid.";
                    return false;
                }

                uint widthValue = BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                uint heightValue = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                if (widthValue > int.MaxValue || heightValue > int.MaxValue)
                {
                    warning = "Rendered page raster evidence was unavailable because its dimensions are too large.";
                    return false;
                }

                width = (int)widthValue;
                height = (int)heightValue;
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                interlace = chunkData[12];
                if (chunkData[10] != 0 || chunkData[11] != 0)
                {
                    warning = "Rendered page raster evidence was unavailable because the PNG compression or filter method is unsupported.";
                    return false;
                }

                sawHeader = true;
            }
            else if (chunkType.SequenceEqual("PLTE"u8))
            {
                palette = chunkData.ToArray();
            }
            else if (chunkType.SequenceEqual("tRNS"u8))
            {
                transparency = chunkData.ToArray();
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                compressed.Write(chunkData);
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
                break;
            }

            offset = (int)chunkEnd;
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        long pixelCount = (long)width * height;
        if (!sawHeader ||
            !sawEnd ||
            compressed.Length == 0 ||
            width != renderedPage.Width ||
            height != renderedPage.Height ||
            width < 1 ||
            height < 1 ||
            pixelCount > MaximumRasterPixels ||
            bitDepth != 8 ||
            interlace != 0 ||
            channels == 0 ||
            (colorType == 3 &&
                (palette is null || palette.Length == 0 || palette.Length % 3 != 0)))
        {
            warning = "Rendered page raster evidence requires a dimension-matched, 8-bit, non-interlaced grayscale, indexed, RGB, or RGBA PNG.";
            return false;
        }

        int rowBytes;
        int expectedLength;
        try
        {
            rowBytes = checked(width * channels);
            expectedLength = checked((rowBytes + 1) * height);
        }
        catch (OverflowException)
        {
            warning = "Rendered page raster evidence was unavailable because its decoded size is too large.";
            return false;
        }

        byte[] filtered = new byte[expectedLength];
        try
        {
            compressed.Position = 0;
            using ZLibStream decoder = new(compressed, CompressionMode.Decompress, leaveOpen: true);
            int totalRead = 0;
            while (totalRead < filtered.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = decoder.Read(filtered.AsSpan(totalRead));
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead != filtered.Length || decoder.ReadByte() != -1)
            {
                warning = "Rendered page raster evidence was unavailable because the PNG scanline payload has an unexpected size.";
                return false;
            }
        }
        catch (InvalidDataException)
        {
            warning = "Rendered page raster evidence was unavailable because the PNG deflate stream is invalid.";
            return false;
        }
        catch (IOException)
        {
            warning = "Rendered page raster evidence was unavailable because the PNG scanlines could not be decoded.";
            return false;
        }

        byte[] ink = new byte[(int)pixelCount];
        byte[] previous = new byte[rowBytes];
        byte[] current = new byte[rowBytes];
        int filteredOffset = 0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte filter = filtered[filteredOffset++];
            if (filter > 4)
            {
                warning = "Rendered page raster evidence was unavailable because the PNG uses an invalid scanline filter.";
                return false;
            }

            for (int index = 0; index < rowBytes; index++)
            {
                int left = index >= channels ? current[index - channels] : 0;
                int above = previous[index];
                int upperLeft = index >= channels ? previous[index - channels] : 0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => PaethPredictor(left, above, upperLeft),
                    _ => 0,
                };
                current[index] = unchecked((byte)(filtered[filteredOffset++] + predictor));
            }

            for (int x = 0; x < width; x++)
            {
                int pixelOffset = x * channels;
                int luma = ReadLuma(current, pixelOffset, colorType, palette!, transparency);
                if (luma < RasterInkLumaThreshold)
                {
                    ink[(y * width) + x] = 1;
                }
            }

            (previous, current) = (current, previous);
        }

        analysis = RasterAnalysis.Create(width, height, ink, transform, cancellationToken);
        return true;
    }

    private static int ReadLuma(
        byte[] row,
        int offset,
        byte colorType,
        byte[] palette,
        byte[]? transparency)
    {
        int red;
        int green;
        int blue;
        int alpha;
        switch (colorType)
        {
            case 0:
                red = green = blue = row[offset];
                alpha = 255;
                break;
            case 2:
                red = row[offset];
                green = row[offset + 1];
                blue = row[offset + 2];
                alpha = 255;
                break;
            case 3:
                int paletteIndex = row[offset];
                int paletteOffset = paletteIndex * 3;
                if (paletteOffset > palette.Length - 3)
                {
                    return 255;
                }

                red = palette[paletteOffset];
                green = palette[paletteOffset + 1];
                blue = palette[paletteOffset + 2];
                alpha = transparency is not null && paletteIndex < transparency.Length
                    ? transparency[paletteIndex]
                    : 255;
                break;
            case 4:
                red = green = blue = row[offset];
                alpha = row[offset + 1];
                break;
            case 6:
                red = row[offset];
                green = row[offset + 1];
                blue = row[offset + 2];
                alpha = row[offset + 3];
                break;
            default:
                return 255;
        }

        int luma = ((299 * red) + (587 * green) + (114 * blue) + 500) / 1000;
        return ((luma * alpha) + (255 * (255 - alpha)) + 127) / 255;
    }

    private static int PaethPredictor(int left, int above, int upperLeft)
    {
        int estimate = left + above - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int aboveDistance = Math.Abs(estimate - above);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
        {
            return left;
        }

        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static PdfTextBlock? FindCaption(
        IReadOnlyList<PdfTextBlock> textBlocks,
        PdfRectD bounds)
    {
        double maximumDistance = Math.Max(54d, bounds.Height * 0.20d);
        return textBlocks
            .Where(block => block.Role == PdfTextRole.Caption &&
                !string.IsNullOrWhiteSpace(block.Text) &&
                block.BoundsPagePoints.IsValid &&
                DistanceBetween(block.BoundsPagePoints, bounds) <= maximumDistance)
            .OrderBy(block => DistanceBetween(block.BoundsPagePoints, bounds))
            .ThenByDescending(static block => block.Confidence)
            .ThenBy(static block => block.BlockId)
            .FirstOrDefault();
    }

    private static PdfTextBlock? FindParticipantLabel(
        IReadOnlyList<PdfTextBlock> textBlocks,
        PdfRectD bounds) =>
        textBlocks
            .Where(block => block.Role == PdfTextRole.ParticipantLabel &&
                !string.IsNullOrWhiteSpace(block.Text) &&
                block.BoundsPagePoints.IsValid &&
                DistanceBetween(block.BoundsPagePoints, bounds) <= 18d)
            .OrderBy(block => DistanceBetween(block.BoundsPagePoints, bounds))
            .ThenByDescending(static block => block.Confidence)
            .ThenBy(static block => block.BlockId)
            .FirstOrDefault();

    private static bool HasWhitespaceValley(
        PanelizationContext context,
        IReadOnlyList<AxisPair> axes,
        PdfRectD figureBounds)
    {
        AxisPair[] ordered = axes
            .OrderByDescending(static axis => axis.PlotBoundsPagePoints.Y)
            .ToArray();
        for (int index = 0; index < ordered.Length - 1; index++)
        {
            PdfRectD upper = ordered[index].PlotBoundsPagePoints;
            PdfRectD lower = ordered[index + 1].PlotBoundsPagePoints;
            double gap = upper.Y - lower.Bottom;
            if (gap <= 0d || gap / figureBounds.Height < context.Options.MinimumWhitespaceFraction)
            {
                continue;
            }

            PdfRectD valley = new(figureBounds.X, lower.Bottom, figureBounds.Width, gap);
            int occupants = context.Page.VectorLines.Count(line => LineMidpointInside(line, valley)) +
                context.Page.TextBlocks.Count(block =>
                    block.Role is not PdfTextRole.ParticipantLabel and not PdfTextRole.Caption &&
                    IntersectionArea(block.BoundsPagePoints, valley) > 0d);
            bool rasterValley = context.Raster is null ||
                context.Raster.InkFraction(context.Transform.PagePointsToPixels(valley)) <= 0.015d;
            if (occupants <= 2 && rasterValley)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSharedDivider(
        IReadOnlyList<PdfVectorLine> lines,
        IReadOnlyList<AxisPair> axes)
    {
        if (axes.Count < 2)
        {
            return false;
        }

        List<IReadOnlyList<double>> normalizedPositions = [];
        foreach (AxisPair axis in axes)
        {
            PdfRectD plot = axis.PlotBoundsPagePoints;
            double minimumLength = plot.Height * 0.45d;
            double[] positions = lines
                .Where(line => line.IsVertical(AxisEndpointTolerancePoints) &&
                    SegmentLength(line) >= minimumLength &&
                    CenterX(line) > plot.X + AxisColumnTolerancePoints &&
                    CenterX(line) < plot.Right - AxisColumnTolerancePoints &&
                    CenterY(line) >= plot.Y &&
                    CenterY(line) <= plot.Bottom)
                .Select(line => (CenterX(line) - plot.X) / plot.Width)
                .OrderBy(static value => value)
                .ToArray();
            normalizedPositions.Add(positions);
        }

        foreach (double position in normalizedPositions[0])
        {
            if (normalizedPositions.Skip(1).All(
                values => values.Any(candidate => Math.Abs(candidate - position) <= 0.03d)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTableLike(
        IReadOnlyList<PdfVectorLine> lines,
        PdfRectD bounds)
    {
        int horizontal = lines.Count(line =>
            line.IsHorizontal(AxisEndpointTolerancePoints) &&
            SegmentLength(line) >= bounds.Width * 0.60d &&
            LineMidpointInside(line, bounds));
        int vertical = lines.Count(line =>
            line.IsVertical(AxisEndpointTolerancePoints) &&
            SegmentLength(line) >= bounds.Height * 0.60d &&
            LineMidpointInside(line, bounds));
        return horizontal >= 3 && vertical >= 3;
    }

    private static bool IsTextHeavy(
        IReadOnlyList<PdfTextBlock> textBlocks,
        PdfRectD bounds)
    {
        PdfTextBlock[] body = textBlocks
            .Where(block => block.Role == PdfTextRole.Body &&
                IntersectionArea(block.BoundsPagePoints, bounds) > 0d)
            .ToArray();
        if (body.Length < 8)
        {
            return false;
        }

        double occupiedArea = body.Sum(block =>
            IntersectionArea(block.BoundsPagePoints, bounds));
        return occupiedArea / bounds.Area >= 0.20d;
    }

    private static int CountLinesInBounds(
        IReadOnlyList<PdfVectorLine> lines,
        PdfRectD bounds) =>
        lines.Count(line =>
            line.StartPagePoints.IsFinite &&
            line.EndPagePoints.IsFinite &&
            SegmentLength(line) >= 2d &&
            LineMidpointInside(line, bounds));

    private static bool CanShareFigure(AxisPair left, AxisPair right)
    {
        PdfRectD a = left.PlotBoundsPagePoints;
        PdfRectD b = right.PlotBoundsPagePoints;
        double columnTolerance = Math.Max(
            AxisColumnTolerancePoints,
            Math.Max(a.Width, b.Width) * 0.03d);
        double widthDifference = Math.Abs(a.Width - b.Width) / Math.Max(a.Width, b.Width);
        double gap = IntervalGap(a.Y, a.Bottom, b.Y, b.Bottom);
        double permittedGap = Math.Max(
            MaximumPanelGapPoints,
            Math.Max(a.Height, b.Height) * 0.75d);
        return Math.Abs(a.X - b.X) <= columnTolerance &&
            widthDifference <= MaximumPlotWidthDifferenceFraction &&
            gap <= permittedGap;
    }

    private static bool HaveAlignedWidths(IReadOnlyList<AxisPair> axes)
    {
        double maximum = axes.Max(static axis => axis.PlotBoundsPagePoints.Width);
        double minimum = axes.Min(static axis => axis.PlotBoundsPagePoints.Width);
        return (maximum - minimum) / maximum <= MaximumPlotWidthDifferenceFraction;
    }

    private static bool HaveAlignedYAxisColumns(IReadOnlyList<AxisPair> axes)
    {
        double maximumWidth = axes.Max(static axis => axis.PlotBoundsPagePoints.Width);
        double tolerance = Math.Max(AxisColumnTolerancePoints, maximumWidth * 0.03d);
        double minimum = axes.Min(static axis => axis.PlotBoundsPagePoints.X);
        double maximum = axes.Max(static axis => axis.PlotBoundsPagePoints.X);
        return maximum - minimum <= tolerance;
    }

    private static bool AreDuplicateAxes(AxisPair left, AxisPair right)
    {
        PdfRectD a = left.PlotBoundsPagePoints;
        PdfRectD b = right.PlotBoundsPagePoints;
        return Math.Abs(a.X - b.X) <= AxisEndpointTolerancePoints &&
            Math.Abs(a.Y - b.Y) <= AxisEndpointTolerancePoints &&
            Math.Abs(a.Width - b.Width) <= AxisEndpointTolerancePoints * 2d &&
            Math.Abs(a.Height - b.Height) <= AxisEndpointTolerancePoints * 2d;
    }

    private static bool HasMaterialOverlap(PdfRectD left, PdfRectD right)
    {
        double intersection = IntersectionArea(left, right);
        if (intersection <= 0.5d)
        {
            return false;
        }

        return intersection / Math.Min(left.Area, right.Area) >= DuplicateOverlapFraction;
    }

    private static int ReviewStatePriority(PdfSuggestionReviewState reviewState) =>
        reviewState switch
        {
            PdfSuggestionReviewState.RejectedByUser => 2,
            PdfSuggestionReviewState.ConfirmedByUser => 1,
            _ => 0,
        };

    private static List<PdfRectD> CreateHorizontalSegments(
        PdfRectD figureBounds,
        IEnumerable<double> boundaries)
    {
        List<PdfRectD> output = [];
        double top = figureBounds.Y;
        foreach (double boundary in boundaries.OrderBy(static value => value))
        {
            output.Add(new PdfRectD(figureBounds.X, top, figureBounds.Width, boundary - top));
            top = boundary;
        }

        output.Add(new PdfRectD(
            figureBounds.X,
            top,
            figureBounds.Width,
            figureBounds.Bottom - top));
        return output;
    }

    private static PdfRectD PageBoundsToSourceCrop(
        PdfFigureCandidate figure,
        PdfRectD pageBounds) =>
        PageBoundsToSourceCropQuadrilateral(figure, pageBounds).Bounds;

    private static PdfQuadrilateralD PageBoundsToSourceCropQuadrilateral(
        PdfFigureCandidate figure,
        PdfRectD pageBounds)
    {
        PdfQuadrilateralD pagePoints = figure.PagePixelsToPagePoints.Transform(pageBounds);
        return figure.PagePointsToSourcePixels.Transform(pagePoints);
    }

    private static PdfRectD PagePixelsToFigurePoints(
        PdfFigureCandidate figure,
        PdfRectD pageBounds) =>
        figure.PagePixelsToPagePoints.Transform(pageBounds).Bounds;

    private static List<PdfPanelRecord> RenumberPanels(IEnumerable<PdfPanelRecord> panels)
    {
        List<PdfPanelRecord> output = [];
        int order = 1;
        foreach (PdfPanelRecord panel in panels
            .OrderBy(static panel => panel.PageNumber)
            .ThenBy(static panel => panel.BoundsPagePixels.Y)
            .ThenBy(static panel => panel.BoundsPagePixels.X)
            .ThenBy(static panel => panel.PanelId))
        {
            output.Add(new PdfPanelRecord(
                panel.PanelId,
                panel.FigureId,
                panel.PageNumber,
                order++,
                panel.CropInSourcePixels,
                panel.BoundsPagePixels,
                panel.BoundsPagePoints,
                panel.ParticipantLabel,
                panel.Caption,
                panel.SemanticSuggestions,
                panel.Evidence,
                panel.Confidence,
                panel.CropInSourcePixelsQuadrilateral));
        }

        return output;
    }

    private static PdfPanelizationResult WithFailure(
        PdfPanelizationResult current,
        string code,
        string technicalMessage) =>
        new(
            current.Figures,
            current.Panels,
            current.Failures.Append(CreateFailure(code, technicalMessage)),
            current.Warnings,
            current.ElapsedMilliseconds);

    private static PdfFailure CreateFailure(
        string code,
        string technicalMessage,
        int? pageNumber = null) =>
        new(
            code,
            PdfFailureSeverity.Error,
            "Errors.PdfPanelizationInvalid",
            technicalMessage,
            true,
            "adjust_manual_panel_command",
            pageNumber);

    private static string? CommonValue(IEnumerable<string?> values)
    {
        string?[] distinct = values
            .Select(NormalizeText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTableCaption(string text) =>
        text.TrimStart().StartsWith("Table", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsMostly(PdfRectD outer, PdfRectD inner) =>
        IntersectionArea(outer, inner) / inner.Area >= 0.90d;

    private static double IntersectionArea(PdfRectD left, PdfRectD right)
    {
        double width = Math.Max(0d, Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X));
        double height = Math.Max(0d, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));
        return width * height;
    }

    private static PdfRectD Union(IEnumerable<PdfRectD> rectangles)
    {
        PdfRectD[] values = rectangles.ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("At least one rectangle is required.");
        }

        double x = values.Min(static value => value.X);
        double y = values.Min(static value => value.Y);
        double right = values.Max(static value => value.Right);
        double bottom = values.Max(static value => value.Bottom);
        return new PdfRectD(x, y, right - x, bottom - y);
    }

    private static PdfRectD ExpandAndClip(
        PdfRectD value,
        double left,
        double right,
        double bottom,
        double top,
        double pageWidth,
        double pageHeight)
    {
        double x = Math.Max(0d, value.X - left);
        double y = Math.Max(0d, value.Y - bottom);
        double maximumX = Math.Min(pageWidth, value.Right + right);
        double maximumY = Math.Min(pageHeight, value.Bottom + top);
        return new PdfRectD(x, y, maximumX - x, maximumY - y);
    }

    private static double DistanceBetween(PdfRectD left, PdfRectD right)
    {
        double horizontal = IntervalGap(left.X, left.Right, right.X, right.Right);
        double vertical = IntervalGap(left.Y, left.Bottom, right.Y, right.Bottom);
        return Math.Sqrt((horizontal * horizontal) + (vertical * vertical));
    }

    private static double IntervalGap(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd)
    {
        if (firstEnd < secondStart)
        {
            return secondStart - firstEnd;
        }

        return secondEnd < firstStart ? firstStart - secondEnd : 0d;
    }

    private static bool LineMidpointInside(PdfVectorLine line, PdfRectD bounds)
    {
        double x = CenterX(line);
        double y = CenterY(line);
        return x >= bounds.X && x <= bounds.Right && y >= bounds.Y && y <= bounds.Bottom;
    }

    private static double CenterX(PdfVectorLine line) =>
        (line.StartPagePoints.X + line.EndPagePoints.X) / 2d;

    private static double CenterY(PdfVectorLine line) =>
        (line.StartPagePoints.Y + line.EndPagePoints.Y) / 2d;

    private static double CenterX(PdfRectD value) => value.X + (value.Width / 2d);

    private static double CenterY(PdfRectD value) => value.Y + (value.Height / 2d);

    private static double SegmentLength(PdfVectorLine line)
    {
        double x = line.EndPagePoints.X - line.StartPagePoints.X;
        double y = line.EndPagePoints.Y - line.StartPagePoints.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static NormalizedLine NormalizeHorizontal(PdfVectorLine line) =>
        new(
            Math.Min(line.StartPagePoints.X, line.EndPagePoints.X),
            Math.Max(line.StartPagePoints.X, line.EndPagePoints.X),
            (line.StartPagePoints.Y + line.EndPagePoints.Y) / 2d,
            Math.Abs(line.WidthPoints));

    private static NormalizedLine NormalizeVertical(PdfVectorLine line) =>
        new(
            Math.Min(line.StartPagePoints.Y, line.EndPagePoints.Y),
            Math.Max(line.StartPagePoints.Y, line.EndPagePoints.Y),
            (line.StartPagePoints.X + line.EndPagePoints.X) / 2d,
            Math.Abs(line.WidthPoints));

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    private static string FormatIdentity(
        string prefix,
        Guid figureId,
        PdfRectD bounds) =>
        FormattableString.Invariant(
            $"{prefix}|{figureId}|{bounds.X:R}|{bounds.Y:R}|{bounds.Width:R}|{bounds.Height:R}");

    private static string FormatIdentity(
        string documentSha256,
        int pageNumber,
        PdfFigureSourceKind sourceKind,
        Guid sourceId,
        PdfRectD bounds) =>
        FormattableString.Invariant(
            $"{documentSha256}|{pageNumber}|{sourceKind}|{sourceId}|{bounds.X:R}|{bounds.Y:R}|{bounds.Width:R}|{bounds.Height:R}");

    private static Guid CreateDeterministicGuid(string identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, guidBytes.Length).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private sealed record PanelizationContext(
        PdfPanelizationInput Input,
        PdfPageCoordinateTransform Transform,
        RasterAnalysis? Raster = null)
    {
        public PdfPageSnapshot Page => Input.Page;

        public PdfPanelizationOptions Options => Input.Options;
    }

    private sealed record NormalizedLine(
        double Start,
        double End,
        double FixedCoordinate,
        double Width)
    {
        public double Length => End - Start;
    }

    private sealed record AxisPair(PdfRectD PlotBoundsPagePoints, bool FromRaster);

    private sealed class RasterAnalysis
    {
        private const int MaximumLinesPerOrientation = 1_024;

        private readonly byte[] _ink;
        private readonly List<RasterStructuralLine> _lines;

        private RasterAnalysis(
            int width,
            int height,
            byte[] ink,
            List<RasterStructuralLine> lines,
            List<AxisPair> axes)
        {
            Width = width;
            Height = height;
            _ink = ink;
            _lines = lines;
            Axes = axes;
        }

        public int Width { get; }

        public int Height { get; }

        public List<AxisPair> Axes { get; }

        public static RasterAnalysis Create(
            int width,
            int height,
            byte[] ink,
            PdfPageCoordinateTransform transform,
            CancellationToken cancellationToken)
        {
            int horizontalMinimum = Math.Max(
                (int)Math.Ceiling(MinimumAxisLengthPoints * transform.ScaleX),
                (int)Math.Ceiling(width * MinimumHorizontalPageFraction));
            int verticalMinimum = Math.Max(
                (int)Math.Ceiling(MinimumAxisLengthPoints * transform.ScaleY),
                (int)Math.Ceiling(height * MinimumVerticalPageFraction));
            List<RasterStructuralLine> horizontal = DetectLines(
                width,
                height,
                ink,
                isHorizontal: true,
                horizontalMinimum,
                cancellationToken);
            List<RasterStructuralLine> vertical = DetectLines(
                width,
                height,
                ink,
                isHorizontal: false,
                verticalMinimum,
                cancellationToken);

            List<AxisPair> axes = [];
            foreach (RasterStructuralLine xAxis in horizontal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (RasterStructuralLine yAxis in vertical)
                {
                    if (Math.Abs(yAxis.Fixed - xAxis.Start) > RasterAxisEndpointTolerancePixels ||
                        Math.Abs(xAxis.Fixed - yAxis.End) > RasterAxisEndpointTolerancePixels)
                    {
                        continue;
                    }

                    double plotWidth = xAxis.End - yAxis.Fixed;
                    double plotHeight = xAxis.Fixed - yAxis.Start;
                    if (plotWidth < horizontalMinimum || plotHeight < verticalMinimum)
                    {
                        continue;
                    }

                    PdfRectD plotPixels = new(
                        yAxis.Fixed,
                        yAxis.Start,
                        plotWidth,
                        plotHeight);
                    axes.Add(new AxisPair(
                        transform.PagePixelsToPoints(plotPixels),
                        FromRaster: true));
                }
            }

            List<RasterStructuralLine> lines = horizontal.Concat(vertical).ToList();
            return new RasterAnalysis(width, height, ink, lines, axes);
        }

        public int CountStructuralLines(PdfRectD boundsPixels)
        {
            int count = _lines.Count(line => line.MidpointInside(boundsPixels));
            double inkFraction = InkFraction(boundsPixels);
            return inkFraction is >= 0.001d and <= 0.35d
                ? Math.Max(count, (int)MinimumDenseLineCount)
                : count;
        }

        public bool IsTableLike(PdfRectD boundsPixels)
        {
            int horizontal = _lines.Count(line =>
                line.IsHorizontal &&
                line.Length >= boundsPixels.Width * 0.60d &&
                line.MidpointInside(boundsPixels));
            int vertical = _lines.Count(line =>
                !line.IsHorizontal &&
                line.Length >= boundsPixels.Height * 0.60d &&
                line.MidpointInside(boundsPixels));
            return horizontal >= 3 && vertical >= 3;
        }

        public double InkFraction(PdfRectD boundsPixels)
        {
            int xMinimum = Math.Clamp((int)Math.Floor(boundsPixels.X), 0, Width);
            int yMinimum = Math.Clamp((int)Math.Floor(boundsPixels.Y), 0, Height);
            int xMaximum = Math.Clamp((int)Math.Ceiling(boundsPixels.Right), 0, Width);
            int yMaximum = Math.Clamp((int)Math.Ceiling(boundsPixels.Bottom), 0, Height);
            if (xMaximum <= xMinimum || yMaximum <= yMinimum)
            {
                return 1d;
            }

            long inkCount = 0;
            for (int y = yMinimum; y < yMaximum; y++)
            {
                int rowOffset = y * Width;
                for (int x = xMinimum; x < xMaximum; x++)
                {
                    inkCount += _ink[rowOffset + x];
                }
            }

            long area = (long)(xMaximum - xMinimum) * (yMaximum - yMinimum);
            return inkCount / (double)area;
        }

        private static List<RasterStructuralLine> DetectLines(
            int width,
            int height,
            byte[] ink,
            bool isHorizontal,
            int minimumLength,
            CancellationToken cancellationToken)
        {
            int fixedLimit = isHorizontal ? height : width;
            int varyingLimit = isHorizontal ? width : height;
            List<RasterStructuralLine> candidates = [];
            for (int fixedCoordinate = 0; fixedCoordinate < fixedLimit; fixedCoordinate++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = -1;
                int lastInk = -1;
                for (int varying = 0; varying < varyingLimit; varying++)
                {
                    int pixelIndex = isHorizontal
                        ? (fixedCoordinate * width) + varying
                        : (varying * width) + fixedCoordinate;
                    if (ink[pixelIndex] != 0)
                    {
                        start = start < 0 ? varying : start;
                        lastInk = varying;
                        continue;
                    }

                    if (start >= 0 && varying - lastInk > RasterLineGapTolerancePixels)
                    {
                        AddRun(candidates, isHorizontal, start, lastInk, fixedCoordinate, minimumLength);
                        start = -1;
                        lastInk = -1;
                    }
                }

                if (start >= 0)
                {
                    AddRun(candidates, isHorizontal, start, lastInk, fixedCoordinate, minimumLength);
                }
            }

            List<RasterStructuralLine> deduplicated = [];
            foreach (RasterStructuralLine candidate in candidates
                .OrderBy(static line => line.Fixed)
                .ThenBy(static line => line.Start)
                .ThenByDescending(static line => line.Length))
            {
                int duplicateIndex = deduplicated.FindLastIndex(existing =>
                    candidate.Fixed - existing.Fixed <= RasterLineGapTolerancePixels &&
                    Math.Abs(candidate.Start - existing.Start) <= RasterAxisEndpointTolerancePixels &&
                    Math.Abs(candidate.End - existing.End) <= RasterAxisEndpointTolerancePixels);
                if (duplicateIndex >= 0)
                {
                    if (candidate.Length > deduplicated[duplicateIndex].Length)
                    {
                        deduplicated[duplicateIndex] = candidate;
                    }

                    continue;
                }

                deduplicated.Add(candidate);
                if (deduplicated.Count >= MaximumLinesPerOrientation)
                {
                    break;
                }
            }

            return deduplicated;
        }

        private static void AddRun(
            List<RasterStructuralLine> output,
            bool isHorizontal,
            int start,
            int end,
            int fixedCoordinate,
            int minimumLength)
        {
            if (end - start + 1 >= minimumLength)
            {
                output.Add(new RasterStructuralLine(
                    isHorizontal,
                    start,
                    end,
                    fixedCoordinate));
            }
        }
    }

    private sealed record RasterStructuralLine(
        bool IsHorizontal,
        int Start,
        int End,
        int Fixed)
    {
        public int Length => End - Start + 1;

        public bool MidpointInside(PdfRectD bounds)
        {
            double x = IsHorizontal ? (Start + End) / 2d : Fixed;
            double y = IsHorizontal ? Fixed : (Start + End) / 2d;
            return x >= bounds.X && x <= bounds.Right && y >= bounds.Y && y <= bounds.Bottom;
        }
    }

    private sealed record CandidateEvidence(
        IReadOnlyList<PdfPanelEvidence> Items,
        PdfTextBlock? Caption,
        double Confidence);

    private sealed record CandidateDraft(
        PdfFigureCandidate Figure,
        IReadOnlyList<AxisPair> Axes);

    private sealed class CandidateLayoutComparer : IComparer<CandidateDraft>
    {
        public static CandidateLayoutComparer Instance { get; } = new();

        public int Compare(CandidateDraft? left, CandidateDraft? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int page = left.Figure.PageNumber.CompareTo(right.Figure.PageNumber);
            if (page != 0)
            {
                return page;
            }

            int y = left.Figure.BoundsPagePixels.Y.CompareTo(right.Figure.BoundsPagePixels.Y);
            if (y != 0)
            {
                return y;
            }

            int x = left.Figure.BoundsPagePixels.X.CompareTo(right.Figure.BoundsPagePixels.X);
            return x != 0 ? x : left.Figure.FigureId.CompareTo(right.Figure.FigureId);
        }
    }

    private sealed class SemanticSuggestionKeyComparer :
        IEqualityComparer<(PdfSemanticField Field, string Value)>
    {
        public static SemanticSuggestionKeyComparer Instance { get; } = new();

        public bool Equals(
            (PdfSemanticField Field, string Value) left,
            (PdfSemanticField Field, string Value) right) =>
            left.Field == right.Field &&
            string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((PdfSemanticField Field, string Value) value) =>
            HashCode.Combine(value.Field, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value));
    }
}
