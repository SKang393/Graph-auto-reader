// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using GraphReader.Markers.Classification;
using GraphReader.Ocr;

namespace GraphReader.Legends;

public sealed class LegendReasoningService : ILegendReasoningService
{
    private const string EmptySemanticText = "";

    private readonly ILegendRegionResolver _regionResolver;
    private readonly IAnnotationArrowResolver _arrowResolver;

    public LegendReasoningService()
        : this(new LegendRegionResolver(), new AnnotationArrowResolver())
    {
    }

    public LegendReasoningService(
        ILegendRegionResolver regionResolver,
        IAnnotationArrowResolver arrowResolver)
    {
        _regionResolver = regionResolver ?? throw new ArgumentNullException(nameof(regionResolver));
        _arrowResolver = arrowResolver ?? throw new ArgumentNullException(nameof(arrowResolver));
    }

    public Task<LegendReasoningResult> ResolveAsync(
        LegendReasoningRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string runId = Guid.NewGuid().ToString();
        var total = Stopwatch.StartNew();
        LegendReasoningFailure? validationFailure = Validate(request);
        if (validationFailure is not null)
        {
            total.Stop();
            return Task.FromResult(Failed(request, runId, validationFailure, total.Elapsed.TotalMilliseconds));
        }

        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        IReadOnlyList<LegendRegion> regions;
        var regionTimer = Stopwatch.StartNew();
        try
        {
            regions = _regionResolver.Resolve(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            regionTimer.Stop();
            total.Stop();
            return Task.FromResult(Failed(
                request,
                runId,
                Error(
                    "LEGEND_REGION_RESOLUTION_FAILED",
                    $"Legend region resolution failed: {exception.GetType().Name}: {exception.Message}"),
                total.Elapsed.TotalMilliseconds,
                regionMilliseconds: regionTimer.Elapsed.TotalMilliseconds));
        }

        regionTimer.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<LegendAnnotationCallout> callouts;
        IReadOnlyList<LegendArtifact> artifacts;
        var arrowTimer = Stopwatch.StartNew();
        try
        {
            (callouts, artifacts) = _arrowResolver.Resolve(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            arrowTimer.Stop();
            total.Stop();
            return Task.FromResult(Failed(
                request,
                runId,
                Error(
                    "LEGEND_ARROW_RESOLUTION_FAILED",
                    $"Annotation arrow resolution failed: {exception.GetType().Name}: {exception.Message}"),
                total.Elapsed.TotalMilliseconds,
                regionMilliseconds: regionTimer.Elapsed.TotalMilliseconds,
                arrowMilliseconds: arrowTimer.Elapsed.TotalMilliseconds));
        }

        arrowTimer.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        var metadataTimer = Stopwatch.StartNew();
        HashSet<string> artifactMarkerIds = ResolveArtifactMarkerIds(
            request.PanelId,
            request.PlotMarkers,
            request.Glyphs,
            artifacts,
            regions);
        MatchResult match = MatchSeries(request, regions, artifactMarkerIds, warnings, cancellationToken);
        LegendRegion[] normalizedRegions = NormalizeRegions(regions, match.SeriesIdByEntryId);
        LegendParticipantMetadata[] participants = ResolveParticipants(
            request,
            normalizedRegions,
            callouts,
            warnings);
        metadataTimer.Stop();
        total.Stop();

        if (normalizedRegions.Length == 0)
        {
            warnings.Add("legend_region_not_detected");
        }

        double confidence = OverallConfidence(
            normalizedRegions,
            match.Resolutions,
            callouts,
            participants);
        return Task.FromResult(new LegendReasoningResult(
            LegendReasoningContract.Version,
            runId,
            request.ProjectId,
            request.PanelId,
            LegendReasoningContract.Stage,
            request.Options.StageVersion,
            request.InputSha256,
            LegendReasoningContract.CoordinateSpace,
            new LegendReasoningPayload(
                normalizedRegions,
                match.Resolutions,
                callouts,
                artifacts,
                participants,
                artifactMarkerIds.Order(StringComparer.Ordinal)),
            new LegendReasoningTiming(
                regionTimer.Elapsed.TotalMilliseconds,
                arrowTimer.Elapsed.TotalMilliseconds,
                metadataTimer.Elapsed.TotalMilliseconds,
                total.Elapsed.TotalMilliseconds),
            confidence,
            warnings,
            null,
            null));
    }

    private static MatchResult MatchSeries(
        LegendReasoningRequest request,
        IReadOnlyList<LegendRegion> regions,
        HashSet<string> artifactMarkerIds,
        SortedSet<string> warnings,
        CancellationToken cancellationToken)
    {
        var glyphs = request.Glyphs.ToDictionary(static glyph => glyph.GlyphId, StringComparer.Ordinal);
        var localEntries = regions
            .SelectMany(static region => region.Entries)
            .Select(entry => new EntryCandidate(entry, glyphs.GetValueOrDefault(entry.GlyphId), IsLocal: true))
            .ToArray();
        var peerEntries = request.PeerPanels
            .OrderBy(static panel => panel.PanelId, StringComparer.Ordinal)
            .SelectMany(panel => panel.Entries.Select(entry => new EntryCandidate(
                entry with
                {
                    SourcePanelId = panel.PanelId,
                    Source = LegendEvidenceSource.CrossPanel,
                },
                Glyph: null,
                IsLocal: false)))
            .ToArray();
        EntryCandidate[] entries = localEntries.Concat(peerEntries).ToArray();

        var proposals = new List<MatchProposal>();
        foreach (LegendSeriesCandidate series in request.Series.OrderBy(static item => item.SeriesId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (series.MarkerIds.Count > 0 && series.MarkerIds.All(artifactMarkerIds.Contains))
            {
                warnings.Add("artifact_series_excluded");
                continue;
            }

            if (series.UserConfirmedName)
            {
                continue;
            }

            foreach (EntryCandidate entry in entries)
            {
                MatchProposal? proposal = Score(series, entry, request, artifactMarkerIds);
                if (proposal is not null)
                {
                    proposals.Add(proposal);
                }
            }
        }

        var bySeries = new Dictionary<string, MatchProposal>(StringComparer.Ordinal);
        var usedEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (MatchProposal proposal in proposals
                     .OrderByDescending(static item => item.LocalPriority)
                     .ThenByDescending(static item => item.Score)
                     .ThenBy(static item => item.Series.SeriesId, StringComparer.Ordinal)
                     .ThenBy(static item => item.Entry.Entry.SourcePanelId, StringComparer.Ordinal)
                     .ThenBy(static item => item.Entry.Entry.EntryId, StringComparer.Ordinal))
        {
            string entryIdentity = EntryIdentity(proposal.Entry.Entry);
            if (bySeries.ContainsKey(proposal.Series.SeriesId) || usedEntries.Contains(entryIdentity))
            {
                continue;
            }

            bySeries.Add(proposal.Series.SeriesId, proposal);
            usedEntries.Add(entryIdentity);
        }

        var resolutions = new List<LegendSeriesResolution>(request.Series.Count);
        var seriesIdByEntryId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (LegendSeriesCandidate series in request.Series.OrderBy(static item => item.SeriesId, StringComparer.Ordinal))
        {
            if (series.MarkerIds.Count > 0 && series.MarkerIds.All(artifactMarkerIds.Contains))
            {
                continue;
            }

            bySeries.TryGetValue(series.SeriesId, out MatchProposal? proposal);
            if (proposal?.Entry.IsLocal == true)
            {
                seriesIdByEntryId[proposal.Entry.Entry.EntryId] = series.SeriesId;
            }

            LegendSemanticEvidence semantic = proposal is null
                ? UnknownSemantic()
                : proposal.Entry.Entry.Semantic;
            string symbol = string.IsNullOrWhiteSpace(series.Symbol)
                ? MarkerSymbolMap.GetSymbol(series.Shape, series.Fill)
                : series.Symbol.Trim();
            string accessibleName = string.IsNullOrWhiteSpace(series.AccessibleName)
                ? MarkerSymbolMap.GetAccessibleName(series.Shape, series.Fill)
                : series.AccessibleName.Trim();

            if (series.UserConfirmedName)
            {
                resolutions.Add(new LegendSeriesResolution(
                    series.SeriesId,
                    series.CurrentName!.Trim(),
                    symbol,
                    accessibleName,
                    LegendEvidenceSource.UserConfirmed,
                    proposal?.Entry.Entry.EntryId,
                    proposal?.Entry.Entry.SourcePanelId,
                    1,
                    semantic,
                    UserConfirmedPreserved: true));
                continue;
            }

            if (proposal is not null)
            {
                LegendEntry entry = proposal.Entry.Entry;
                resolutions.Add(new LegendSeriesResolution(
                    series.SeriesId,
                    entry.Text.Trim(),
                    symbol,
                    accessibleName,
                    proposal.Entry.IsLocal ? LegendEvidenceSource.DetectedLegend : LegendEvidenceSource.CrossPanel,
                    entry.EntryId,
                    entry.SourcePanelId,
                    Math.Clamp(proposal.Score, 0, 1),
                    semantic,
                    UserConfirmedPreserved: false));
                if (!proposal.Entry.IsLocal)
                {
                    warnings.Add("cross_panel_legend_propagated");
                }

                continue;
            }

            warnings.Add("series_name_used_symbol_fallback");
            resolutions.Add(new LegendSeriesResolution(
                series.SeriesId,
                $"{symbol} {accessibleName}",
                symbol,
                accessibleName,
                LegendEvidenceSource.SymbolFallback,
                null,
                null,
                0.5,
                UnknownSemantic(),
                UserConfirmedPreserved: false));
        }

        return new MatchResult(resolutions.ToArray(), seriesIdByEntryId);
    }

    private static MatchProposal? Score(
        LegendSeriesCandidate series,
        EntryCandidate candidate,
        LegendReasoningRequest request,
        HashSet<string> artifactMarkerIds)
    {
        LegendEntry entry = candidate.Entry;
        if (!string.IsNullOrWhiteSpace(entry.NormalizedSeriesId) &&
            !string.Equals(entry.NormalizedSeriesId, series.SeriesId, StringComparison.Ordinal))
        {
            return null;
        }

        if (entry.Confidence < request.Options.MinimumPairConfidence ||
            (!candidate.IsLocal && string.IsNullOrWhiteSpace(entry.NormalizedSeriesId)))
        {
            return null;
        }

        if (entry.Shape != series.Shape ||
            (entry.Fill != series.Fill && entry.Fill != MarkerFill.Unknown && series.Fill != MarkerFill.Unknown))
        {
            return null;
        }

        double shapeScore = 1;
        double fillScore = entry.Fill == series.Fill ? 1 : 0.65;
        double embeddingScore = 0.65;
        if (candidate.Glyph is not null && candidate.Glyph.Embedding.Count > 0 && series.Embedding.Count > 0)
        {
            embeddingScore = Cosine(candidate.Glyph.Embedding, series.Embedding);
            if (embeddingScore < request.Options.MinimumEmbeddingSimilarity)
            {
                return null;
            }

            embeddingScore = Math.Clamp(embeddingScore, 0, 1);
        }

        double geometryScore = candidate.IsLocal
            ? GeometryScore(series, candidate.Glyph, request, artifactMarkerIds)
            : 0.5;
        double evidenceConfidence = Math.Clamp(entry.Confidence, 0, 1);
        double score = (0.30 * shapeScore) + (0.20 * fillScore) +
            (0.35 * embeddingScore) + (0.05 * geometryScore) +
            (0.10 * evidenceConfidence);
        if (string.Equals(entry.NormalizedSeriesId, series.SeriesId, StringComparison.Ordinal))
        {
            score = Math.Max(score, 0.99);
        }

        return new MatchProposal(series, candidate, score, candidate.IsLocal ? 1 : 0);
    }

    private static double GeometryScore(
        LegendSeriesCandidate series,
        LegendGlyphCandidate? glyph,
        LegendReasoningRequest request,
        HashSet<string> artifactMarkerIds)
    {
        if (glyph is null)
        {
            return 0.5;
        }

        HashSet<string> markerIds = series.MarkerIds.ToHashSet(StringComparer.Ordinal);
        LegendPlotMarker[] markers = request.PlotMarkers
            .Where(marker => markerIds.Contains(marker.MarkerId) && !artifactMarkerIds.Contains(marker.MarkerId))
            .ToArray();
        if (markers.Length == 0)
        {
            return 0.5;
        }

        double meanY = markers.Average(static marker => marker.Center.Y);
        double distance = Math.Abs(meanY - glyph.Bounds.Center.Y);
        return Math.Clamp(1 - (distance / request.PanelBounds.Height), 0, 1);
    }

    private static HashSet<string> ResolveArtifactMarkerIds(
        string panelId,
        IReadOnlyList<LegendPlotMarker> markers,
        IReadOnlyList<LegendGlyphCandidate> glyphs,
        IReadOnlyList<LegendArtifact> artifacts,
        IReadOnlyList<LegendRegion> regions)
    {
        LegendRectangle[] arrowheads = artifacts
            .Where(static artifact => artifact.Kind == LegendArtifactKind.Arrowhead)
            .Select(static artifact => artifact.Bounds)
            .ToArray();
        HashSet<string> artifactMarkerIds = markers
            .Where(marker => arrowheads.Any(bounds => bounds.Contains(marker.Center)))
            .Select(static marker => marker.MarkerId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> plotMarkerIds = markers
            .Select(static marker => marker.MarkerId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> localGlyphIds = glyphs
            .Select(static glyph => glyph.GlyphId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (LegendEntry entry in regions.SelectMany(static region => region.Entries))
        {
            if (entry.Source == LegendEvidenceSource.DetectedLegend &&
                string.Equals(entry.SourcePanelId, panelId, StringComparison.Ordinal) &&
                localGlyphIds.Contains(entry.GlyphId) &&
                plotMarkerIds.Contains(entry.GlyphId))
            {
                artifactMarkerIds.Add(entry.GlyphId);
            }
        }

        return artifactMarkerIds;
    }

    private static LegendRegion[] NormalizeRegions(
        IReadOnlyList<LegendRegion> regions,
        IReadOnlyDictionary<string, string> seriesIdByEntryId) =>
        regions
            .Select(region => new LegendRegion(
                region.RegionId,
                region.Bounds,
                region.Location,
                region.Entries.Select(entry => entry with
                {
                    NormalizedSeriesId = seriesIdByEntryId.GetValueOrDefault(entry.EntryId),
                }),
                region.Confidence))
            .ToArray();

    private static LegendParticipantMetadata[] ResolveParticipants(
        LegendReasoningRequest request,
        IReadOnlyList<LegendRegion> regions,
        IReadOnlyList<LegendAnnotationCallout> callouts,
        SortedSet<string> warnings)
    {
        var excludedTextIds = regions
            .SelectMany(static region => region.Entries)
            .Select(static entry => entry.TextRegionId)
            .Concat(callouts.Select(static callout => callout.TextRegionId))
            .ToHashSet(StringComparer.Ordinal);
        double rightBandStart = request.PanelBounds.Right -
            (request.PanelBounds.Width * request.Options.ParticipantBandFraction);
        var participants = new List<LegendParticipantMetadata>();
        foreach (LegendTextRegion text in request.TextRegions
                     .OrderBy(static item => item.Bounds.Top)
                     .ThenBy(static item => item.RegionId, StringComparer.Ordinal))
        {
            if (excludedTextIds.Contains(text.RegionId) ||
                text.ReviewStatus == OcrReviewStatus.Rejected ||
                text.Confidence < request.Options.MinimumParticipantConfidence ||
                string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            bool inRightBand = text.Bounds.Center.X >= rightBandStart ||
                text.Bounds.Left >= request.PlotBounds.Right;
            bool explicitParticipant = text.Role == OcrTextRole.Participant && inRightBand;
            bool positionalParticipant = text.Role == OcrTextRole.Other && inRightBand && LooksLikeName(text.Text);
            if (!explicitParticipant && !positionalParticipant)
            {
                continue;
            }

            double confidence = Math.Clamp(
                (text.Confidence * 0.85) + ((inRightBand ? 1 : 0.5) * 0.15),
                0,
                1);
            participants.Add(new LegendParticipantMetadata(
                text.RegionId,
                text.Text.Trim(),
                text.Bounds,
                confidence));
            if (positionalParticipant)
            {
                warnings.Add("participant_inferred_from_right_band");
            }
        }

        return participants.ToArray();
    }

    private static LegendSemanticEvidence UnknownSemantic() =>
        new(LegendSemanticHint.Unknown, EmptySemanticText, 0);

    private static bool LooksLikeName(string text)
    {
        string trimmed = text.Trim();
        string[] tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 4 ||
            LegendSemanticNormalizer.Normalize(trimmed, 1).Hint != LegendSemanticHint.Unknown)
        {
            return false;
        }

        return trimmed.Any(char.IsLetter) && trimmed.All(character =>
            char.IsLetter(character) || char.IsWhiteSpace(character) || character is '\'' or '-' or '.');
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count || left.Count == 0)
        {
            return -1;
        }

        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * (double)right[index];
            leftNorm += left[index] * (double)left[index];
            rightNorm += right[index] * (double)right[index];
        }

        return leftNorm <= 0 || rightNorm <= 0
            ? -1
            : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private static LegendReasoningFailure? Validate(LegendReasoningRequest? request)
    {
        if (request is null)
        {
            return Error("LEGEND_INVALID_REQUEST", "Legend reasoning request is required.");
        }

        if (request.ContractVersion != LegendReasoningContract.Version)
        {
            return Error("LEGEND_CONTRACT_UNSUPPORTED", "The legend reasoning contract version is unsupported.");
        }

        if (!IsUuid(request.ProjectId) || !IsUuid(request.PanelId) ||
            !request.PanelBounds.IsValid || !request.PlotBounds.IsValid)
        {
            return Error("LEGEND_INVALID_REQUEST", "Project ID, panel ID, panel bounds, and plot bounds are required.");
        }

        if (!IsSha256(request.InputSha256))
        {
            return Error("LEGEND_INVALID_INPUT_HASH", "Input SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        LegendReasoningOptions options = request.Options;
        if (!IsPositiveFinite(options.MaximumGlyphTextHorizontalGap) ||
            !IsPositiveFinite(options.MaximumGlyphTextVerticalOffset) ||
            !IsProbability(options.MinimumPairConfidence) ||
            !IsProbability(options.MinimumEmbeddingSimilarity) ||
            !IsPositiveFinite(options.MaximumArrowJoinDistance) ||
            !IsPositiveFinite(options.MaximumArrowTargetDistance) ||
            !IsPositiveFinite(options.MaximumAnnotationTextDistance) ||
            !double.IsFinite(options.ParticipantBandFraction) || options.ParticipantBandFraction is <= 0 or > 1 ||
            !IsProbability(options.MinimumParticipantConfidence) ||
            string.IsNullOrWhiteSpace(options.StageVersion))
        {
            return Error("LEGEND_INVALID_OPTIONS", "Legend thresholds and stage version must be finite and valid.");
        }

        var textIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LegendTextRegion text in request.TextRegions)
        {
            if (text is null || string.IsNullOrWhiteSpace(text.RegionId) || !textIds.Add(text.RegionId) ||
                !text.Bounds.IsValid || text.Text is null || !Enum.IsDefined(text.Role) ||
                !Enum.IsDefined(text.ReviewStatus) || !IsProbability(text.Confidence))
            {
                return Error("LEGEND_INVALID_TEXT", "OCR text evidence must have unique IDs, valid bounds, roles, and confidence.");
            }
        }

        var glyphIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LegendGlyphCandidate glyph in request.Glyphs)
        {
            if (glyph is null || string.IsNullOrWhiteSpace(glyph.GlyphId) || !glyphIds.Add(glyph.GlyphId) ||
                !glyph.Bounds.IsValid || !Enum.IsDefined(glyph.Shape) || !Enum.IsDefined(glyph.Fill) ||
                glyph.Embedding.Any(static value => !float.IsFinite(value)) || !IsProbability(glyph.Confidence))
            {
                return Error("LEGEND_INVALID_GLYPH", "Legend glyph evidence must be unique, finite, and normalized.");
            }
        }

        var seriesIds = new HashSet<string>(StringComparer.Ordinal);
        var assignedMarkerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LegendSeriesCandidate series in request.Series)
        {
            if (series is null || !IsUuid(series.SeriesId) || !seriesIds.Add(series.SeriesId) ||
                !Enum.IsDefined(series.Shape) || !Enum.IsDefined(series.Fill) ||
                series.Embedding.Any(static value => !float.IsFinite(value)) ||
                series.MarkerIds.Any(markerId => !IsUuid(markerId) || !assignedMarkerIds.Add(markerId)) ||
                (series.UserConfirmedName && string.IsNullOrWhiteSpace(series.CurrentName)))
            {
                return Error("LEGEND_INVALID_SERIES", "Series evidence must be unique, finite, and preserve valid confirmed names.");
            }
        }

        var plotMarkerIds = new HashSet<string>(StringComparer.Ordinal);
        var seriesById = request.Series.ToDictionary(static series => series.SeriesId, StringComparer.Ordinal);
        foreach (LegendPlotMarker marker in request.PlotMarkers)
        {
            if (marker is null || !IsUuid(marker.MarkerId) || !plotMarkerIds.Add(marker.MarkerId) ||
                !IsUuid(marker.SeriesId) || !marker.Center.IsFinite ||
                !Enum.IsDefined(marker.Shape) || !Enum.IsDefined(marker.Fill))
            {
                return Error("LEGEND_INVALID_MARKER", "Plot markers must be unique and finite in original pixels.");
            }

            if (!seriesById.TryGetValue(marker.SeriesId, out LegendSeriesCandidate? series) ||
                !series.MarkerIds.Contains(marker.MarkerId, StringComparer.Ordinal) ||
                marker.Shape != series.Shape || marker.Fill != series.Fill)
            {
                return Error(
                    "LEGEND_INVALID_MARKER",
                    "Every plot marker must reference its owning series with matching shape and fill evidence.");
            }
        }

        if (assignedMarkerIds.Any(markerId => !plotMarkerIds.Contains(markerId)))
        {
            return Error("LEGEND_INVALID_SERIES", "Every series marker ID must reference a supplied plot marker.");
        }

        var strokeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LegendStrokeCandidate stroke in request.Strokes)
        {
            if (stroke is null || string.IsNullOrWhiteSpace(stroke.StrokeId) || !strokeIds.Add(stroke.StrokeId) ||
                !stroke.Start.IsFinite || !stroke.End.IsFinite || !double.IsFinite(stroke.Thickness) ||
                stroke.Thickness <= 0 || !IsProbability(stroke.Confidence))
            {
                return Error("LEGEND_INVALID_ARROW_EVIDENCE", "Arrow strokes must be unique and finite.");
            }
        }

        var triangleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LegendTriangleCandidate triangle in request.Triangles)
        {
            if (triangle is null || string.IsNullOrWhiteSpace(triangle.TriangleId) ||
                !triangleIds.Add(triangle.TriangleId) || triangle.Points.Count != 3 ||
                triangle.Points.Any(static point => !point.IsFinite) || !IsProbability(triangle.Confidence))
            {
                return Error("LEGEND_INVALID_ARROW_EVIDENCE", "Arrowhead triangles must contain three finite points.");
            }
        }

        var panelIds = new HashSet<string>(StringComparer.Ordinal) { request.PanelId };
        foreach (LegendPeerPanelEvidence panel in request.PeerPanels)
        {
            if (panel is null || !IsUuid(panel.PanelId) || !panelIds.Add(panel.PanelId) ||
                panel.Entries.Any(entry => !ValidPeerEntry(entry, panel.PanelId, seriesIds)))
            {
                return Error("LEGEND_INVALID_PEER_PANEL", "Peer-panel legend evidence must be valid and have unique source panels.");
            }
        }

        return null;
    }

    private static bool ValidPeerEntry(
        LegendEntry? entry,
        string panelId,
        HashSet<string> seriesIds) =>
        entry is not null &&
        !string.IsNullOrWhiteSpace(entry.EntryId) &&
        !string.IsNullOrWhiteSpace(entry.GlyphId) &&
        !string.IsNullOrWhiteSpace(entry.TextRegionId) &&
        !string.IsNullOrWhiteSpace(entry.Text) &&
        !string.IsNullOrWhiteSpace(entry.SourcePanelId) &&
        string.Equals(entry.SourcePanelId, panelId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(entry.NormalizedSeriesId) &&
        seriesIds.Contains(entry.NormalizedSeriesId) &&
        Enum.IsDefined(entry.Shape) && Enum.IsDefined(entry.Fill) && IsProbability(entry.Confidence) &&
        Enum.IsDefined(entry.Semantic.Hint) && entry.Semantic.NormalizedText is not null &&
        IsProbability(entry.Semantic.Confidence);

    private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out _);

    private static bool IsSha256(string value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsProbability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static double OverallConfidence(
        IReadOnlyList<LegendRegion> regions,
        IReadOnlyList<LegendSeriesResolution> series,
        IReadOnlyList<LegendAnnotationCallout> callouts,
        IReadOnlyList<LegendParticipantMetadata> participants)
    {
        double[] values = regions.Select(static item => item.Confidence)
            .Concat(series.Select(static item => item.Confidence))
            .Concat(callouts.Select(static item => item.Confidence))
            .Concat(participants.Select(static item => item.Confidence))
            .Where(IsProbability)
            .ToArray();
        return values.Length == 0 ? 0 : Math.Clamp(values.Average(), 0, 1);
    }

    private static LegendReasoningResult Failed(
        LegendReasoningRequest? request,
        string runId,
        LegendReasoningFailure failure,
        double totalMilliseconds,
        double regionMilliseconds = 0,
        double arrowMilliseconds = 0) =>
        new(
            LegendReasoningContract.Version,
            runId,
            IsUuid(request?.ProjectId ?? string.Empty) ? request!.ProjectId : Guid.Empty.ToString(),
            IsUuid(request?.PanelId ?? string.Empty) ? request!.PanelId : Guid.Empty.ToString(),
            LegendReasoningContract.Stage,
            string.IsNullOrWhiteSpace(request?.Options?.StageVersion)
                ? LegendReasoningContract.StageVersion
                : request.Options.StageVersion,
            IsSha256(request?.InputSha256 ?? string.Empty) ? request!.InputSha256 : new string('0', 64),
            LegendReasoningContract.CoordinateSpace,
            new LegendReasoningPayload(
                Array.Empty<LegendRegion>(),
                Array.Empty<LegendSeriesResolution>(),
                Array.Empty<LegendAnnotationCallout>(),
                Array.Empty<LegendArtifact>(),
                Array.Empty<LegendParticipantMetadata>(),
                Array.Empty<string>()),
            new LegendReasoningTiming(regionMilliseconds, arrowMilliseconds, 0, totalMilliseconds),
            0,
            Array.Empty<string>(),
            null,
            failure);

    private static LegendReasoningFailure Error(string code, string technicalMessage) =>
        new(code, "error", "Errors." + code, technicalMessage, true, "review_legend_evidence");

    private static string EntryIdentity(LegendEntry entry) => entry.SourcePanelId + "\n" + entry.EntryId;

    private sealed record EntryCandidate(
        LegendEntry Entry,
        LegendGlyphCandidate? Glyph,
        bool IsLocal);

    private sealed record MatchProposal(
        LegendSeriesCandidate Series,
        EntryCandidate Entry,
        double Score,
        int LocalPriority);

    private sealed record MatchResult(
        IReadOnlyList<LegendSeriesResolution> Resolutions,
        IReadOnlyDictionary<string, string> SeriesIdByEntryId);
}
