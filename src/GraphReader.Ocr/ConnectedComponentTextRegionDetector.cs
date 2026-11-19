// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace GraphReader.Ocr;

public sealed record ConnectedComponentTextRegionDetectorOptions
{
    public int MinimumComponentArea { get; init; } = 2;

    public double MaximumComponentWidthFraction { get; init; } = 0.15;

    public double MaximumComponentHeightFraction { get; init; } = 0.20;

    public double MaximumLineGapHeightRatio { get; init; } = 2.5;

    public double MinimumVerticalOverlapFraction { get; init; } = 0.35;

    public byte? ForegroundThreshold { get; init; }

    public bool GroupComponentsIntoLines { get; init; } = true;

    /// <summary>Infers quarter turns only from observed vertical glyph grouping, not a single tall glyph.</summary>
    public bool UseObservedVerticalGroupingOrientation { get; init; }
}

/// <summary>Optional original-pixel ticks and plot edges for conservative axis-label grouping.</summary>
public sealed record OcrMeasuredTickGrouping(IReadOnlyList<int> XTicks, double PlotBottom)
{
    public IReadOnlyList<int>? YTicks { get; init; }
    public double? PlotLeft { get; init; }
}

/// <summary>
/// Finds bounded dark connected components and groups nearby glyphs into text
/// lines. Recognition remains a separate region-first stage.
/// </summary>
public sealed class ConnectedComponentTextRegionDetector : ITextRegionDetector
{
    internal const string IntervalXLabelReason = "x_label_between_measured_ticks";
    internal const string AmbiguousIntervalXLabelReason = "x_label_spans_measured_tick_intervals";
    private readonly ConnectedComponentTextRegionDetectorOptions _options;

    public ConnectedComponentTextRegionDetector(ConnectedComponentTextRegionDetectorOptions? options = null)
    {
        _options = options ?? new ConnectedComponentTextRegionDetectorOptions();
        ValidateOptions(_options);
    }

    public string ConfigurationFingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"cc-v3:{_options.MinimumComponentArea}:{_options.MaximumComponentWidthFraction:R}:{_options.MaximumComponentHeightFraction:R}:{_options.MaximumLineGapHeightRatio:R}:{_options.MinimumVerticalOverlapFraction:R}:{_options.ForegroundThreshold?.ToString(CultureInfo.InvariantCulture) ?? "auto"}") +
        (_options.GroupComponentsIntoLines ? ":interval-x-labels-v2" : ":ungrouped") +
        (_options.UseObservedVerticalGroupingOrientation ? ":observed-vertical-orientation-v1" : string.Empty);

    public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken)
        => DetectAsync(image, null, cancellationToken);

    public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image, OcrMeasuredTickGrouping? grouping, CancellationToken cancellationToken)
        => DetectAsync(image, grouping, null, cancellationToken);

    internal ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(OcrImage image, OcrMeasuredTickGrouping? grouping,
        Dictionary<string, OcrDetectedRegion[]>? reviewPairs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateImage(image);
        cancellationToken.ThrowIfCancellationRequested();

        var threshold = _options.ForegroundThreshold ?? EstimateThreshold(image, cancellationToken);
        var components = FindComponents(image, threshold, cancellationToken);
        var lines = (_options.GroupComponentsIntoLines ? GroupIntoLines(components, image, grouping, reviewPairs, cancellationToken) : components).ToArray();
        var regions = lines
            .OrderBy(static line => line.Top)
            .ThenBy(static line => line.Left)
            .Select(line => ToRegion(image, line))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<OcrDetectedRegion>>(Array.AsReadOnly(regions));
    }

    private List<ComponentBounds> FindComponents(
        OcrImage image,
        byte threshold,
        CancellationToken cancellationToken)
    {
        var visited = new bool[checked(image.Width * image.Height)];
        var components = new List<ComponentBounds>();
        var maximumWidth = Math.Max(2, image.Width * _options.MaximumComponentWidthFraction);
        var maximumHeight = Math.Max(2, image.Height * _options.MaximumComponentHeightFraction);
        var queue = new Queue<int>();

        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                var visitIndex = (y * image.Width) + x;
                if (visited[visitIndex])
                {
                    continue;
                }

                visited[visitIndex] = true;
                if (!IsForeground(image, x, y, threshold))
                {
                    continue;
                }

                queue.Enqueue(visitIndex);
                var componentPixels = new List<int>();
                var bounds = ComponentBounds.Start(x, y);
                var traversed = 0;
                while (queue.Count > 0)
                {
                    if ((traversed++ & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var current = queue.Dequeue();
                    componentPixels.Add(current);
                    var currentX = current % image.Width;
                    var currentY = current / image.Width;
                    bounds = bounds.Include(currentX, currentY);
                    VisitNeighbor(currentX - 1, currentY);
                    VisitNeighbor(currentX + 1, currentY);
                    VisitNeighbor(currentX, currentY - 1);
                    VisitNeighbor(currentX, currentY + 1);
                }

                bounds = AddMorphology(image, bounds, componentPixels);

                if (bounds.Area >= _options.MinimumComponentArea &&
                    bounds.Width <= maximumWidth &&
                    bounds.Height <= maximumHeight)
                {
                    components.Add(bounds);
                }

                void VisitNeighbor(int neighborX, int neighborY)
                {
                    if (neighborX < 0 || neighborY < 0 || neighborX >= image.Width || neighborY >= image.Height)
                    {
                        return;
                    }

                    var neighborIndex = (neighborY * image.Width) + neighborX;
                    if (visited[neighborIndex])
                    {
                        return;
                    }

                    visited[neighborIndex] = true;
                    if (IsForeground(image, neighborX, neighborY, threshold))
                    {
                        queue.Enqueue(neighborIndex);
                    }
                }
            }
        }

        return components;
    }

    private List<ComponentBounds> GroupIntoLines(List<ComponentBounds> components, OcrImage image, OcrMeasuredTickGrouping? grouping,
        Dictionary<string, OcrDetectedRegion[]>? reviewPairs, CancellationToken cancellationToken)
    {
        // Invalid or non-identity context cannot safely describe these pixel components.
        (int Left, int Right, double Position)[] cuts = grouping is not null && image.OriginalToImage == OcrFrameTransform.Identity &&
            grouping.XTicks is { Count: >= 2 and <= 128 } ticks && double.IsFinite(grouping.PlotBottom) &&
            grouping.PlotBottom >= 0 && grouping.PlotBottom < image.Height &&
            ticks.All(tick => tick >= 0 && tick < image.Width) &&
            ticks.Zip(ticks.Skip(1)).All(pair => pair.First < pair.Second)
            ? ticks.Zip(ticks.Skip(1), (a, b) => (Left: a, Right: b, Position: (a + (double)b) / 2)).ToArray() : [];
        // Cache the relevant sets once per cut; lower caption rows must not veto a label row.
        var evidence = new List<(double Position, int LeftTick, int RightTick, ComponentBounds[] Crossing, ComponentBounds[] Left, ComponentBounds[] Right)>();
        for (int cutIndex = 0; cutIndex < cuts.Length; cutIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cut = cuts[cutIndex];
            double leftBoundary = cutIndex > 0 ? cuts[cutIndex - 1].Position : 0;
            double rightBoundary = cutIndex + 1 < cuts.Length ? cuts[cutIndex + 1].Position : image.Width;
            var crossing = new List<ComponentBounds>(); var left = new List<ComponentBounds>(); var right = new List<ComponentBounds>();
            foreach (ComponentBounds component in components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Top <= grouping!.PlotBottom) continue;
                if (component.Left < cut.Position && component.Right + 1 > cut.Position) crossing.Add(component);
                if ((component.Left <= cut.Left && component.Right >= cut.Left) ||
                    (component.Left >= leftBoundary && component.Right + 1 <= cut.Position)) left.Add(component);
                if ((component.Left <= cut.Right && component.Right >= cut.Right) ||
                    (component.Left >= cut.Position && component.Right + 1 <= rightBoundary)) right.Add(component);
            }
            evidence.Add((cut.Position, cut.Left, cut.Right, crossing.ToArray(), left.ToArray(), right.ToArray()));
        }
        bool RowOverlaps(ComponentBounds[] items, int top, int bottom)
        {
            foreach (ComponentBounds component in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Top <= bottom && component.Bottom >= top) return true;
            }
            return false;
        }
        ComponentBounds? RowTickSupport(ComponentBounds[] items, int tick, ComponentBounds first, ComponentBounds second)
        {
            int top = Math.Max(first.Top, second.Top), bottom = Math.Min(first.Bottom, second.Bottom);
            int unionTop = Math.Min(first.Top, second.Top), unionBottom = Math.Max(first.Bottom, second.Bottom);
            ComponentBounds? before = null, after = null;
            foreach (ComponentBounds component in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Top > top || component.Bottom < bottom) continue;
                // Baseline variation may shift full glyphs without changing their text row.
                // Require the shared row, plus the existing row-overlap fraction over
                // its union height, so short punctuation cannot establish a tick lane.
                int overlap = Math.Min(component.Bottom, unionBottom) - Math.Max(component.Top, unionTop) + 1;
                if (overlap / (double)(unionBottom - unionTop + 1) < _options.MinimumVerticalOverlapFraction) continue;
                if (component.Left <= tick && component.Right >= tick) return component;
                if (component.Right < tick && (before is null || component.Right > before.Value.Right)) before = component;
                if (component.Left > tick && (after is null || component.Left < after.Value.Left)) after = component;
            }
            // A centered multi-glyph label can put its tick in white space.
            // Only row-supporting components in that tick's lane may bracket it;
            // reuse the existing component-width gap bound, not a wider OCR gate.
            return before is { } left && after is { } right &&
                right.Left - left.Right - 1 <= Math.Max(left.Width, right.Width) ? left.Merge(right) : null;
        }
        bool SeparateSupportedLanes(ComponentBounds first, ComponentBounds second, int rowTop, int rowBottom)
        {
            if (evidence.Count < 2) return false;
            ComponentBounds left = first.Left < second.Left ? first : second;
            ComponentBounds right = first.Left < second.Left ? second : first;
            int? leftTick = null, rightTick = null;
            for (int index = 0; index <= evidence.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lane = evidence[Math.Min(index, evidence.Count - 1)];
                int tick = index < evidence.Count ? lane.LeftTick : lane.RightTick;
                ComponentBounds[] items = index < evidence.Count ? lane.Left : lane.Right;
                if (RowTickSupport(items, tick, first, second) is not { } support) continue;
                bool onLeft = support.Left <= left.Right && support.Right >= left.Left;
                bool onRight = support.Left <= right.Right && support.Right >= right.Left;
                if (onLeft && onRight) return false;
                if (onLeft)
                {
                    if (leftTick is not null) return false;
                    leftTick = tick;
                }
                if (onRight)
                {
                    if (rightTick is not null) return false;
                    rightTick = tick;
                }
            }
            // Unlabelled minor ticks do not establish text lanes. Both actual groups
            // must own unique observed support, and intervening ink still vetoes a cut.
            if (leftTick is not { } a || rightTick is not { } b || a >= b) return false;
            double boundary = (a + (double)b) / 2;
            if (left.Right >= boundary || right.Left < boundary) return false;
            foreach (ComponentBounds component in components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Top <= rowBottom && component.Bottom >= rowTop &&
                    component.Left < boundary && component.Right + 1 > boundary) return false;
            }
            return true;
        }
        var remaining = components
            .OrderBy(static component => component.Top)
            .ThenBy(static component => component.Left)
            .ToList();
        var lines = new List<ComponentBounds>();
        while (remaining.Count > 0)
        {
            var line = remaining[0];
            var members = new List<ComponentBounds> { line };
            remaining.RemoveAt(0);
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var index = remaining.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = remaining[index];
                    var verticalOverlap = Math.Max(0, Math.Min(line.Bottom, candidate.Bottom) - Math.Max(line.Top, candidate.Top) + 1);
                    var overlapFraction = verticalOverlap / (double)Math.Max(1, Math.Min(line.Height, candidate.Height));
                    var horizontalGap = candidate.Left > line.Right
                        ? candidate.Left - line.Right - 1
                        : line.Left > candidate.Right
                            ? line.Left - candidate.Right - 1
                            : 0;
                    var maximumGap = Math.Max(line.Height, candidate.Height) * _options.MaximumLineGapHeightRatio;
                    cancellationToken.ThrowIfCancellationRequested();
                    int rowTop = Math.Max(line.Top, candidate.Top), rowBottom = Math.Min(line.Bottom, candidate.Bottom);
                    bool separated = grouping is not null && rowTop <= rowBottom && line.Top > grouping.PlotBottom && candidate.Top > grouping.PlotBottom &&
                        horizontalGap > Math.Max(line.Width, candidate.Width) &&
                        (evidence.Any(cut =>
                            ((line.Right < cut.Position && candidate.Left >= cut.Position) ||
                             (candidate.Right < cut.Position && line.Left >= cut.Position)) &&
                            !RowOverlaps(cut.Crossing, rowTop, rowBottom) &&
                            RowTickSupport(cut.Left, cut.LeftTick, line, candidate) is not null &&
                            RowTickSupport(cut.Right, cut.RightTick, line, candidate) is not null) ||
                         SeparateSupportedLanes(line, candidate, rowTop, rowBottom));
                    if (!separated && overlapFraction >= _options.MinimumVerticalOverlapFraction && horizontalGap <= maximumGap)
                    {
                        line = line.Merge(candidate);
                        members.Add(candidate);
                        remaining.RemoveAt(index);
                        changed = true;
                    }
                }
            }

            if (TrySplitIntervalLabels(line, members, grouping, cuts.Length > 0, image, reviewPairs, cancellationToken) is { } split)
                lines.AddRange(split);
            else
                lines.Add(line);
        }

        return GroupVerticalGlyphs(lines, image, grouping, cancellationToken);
    }

    // Work only with the original members of this exact merged line. A single
    // unsupported or boundary-crossing component leaves the original line intact.
    // An uncertain gap joins neighboring bins without suppressing independent bins.
    private List<ComponentBounds>? TrySplitIntervalLabels(ComponentBounds line, List<ComponentBounds> members,
        OcrMeasuredTickGrouping? grouping, bool validContext, OcrImage image,
        Dictionary<string, OcrDetectedRegion[]>? reviewPairs, CancellationToken token)
    {
        if (!validContext || grouping is null || line.Top <= grouping.PlotBottom || members.Count < 3) return null;
        IReadOnlyList<int> ticks = grouping.XTicks;
        var intervals = new SortedDictionary<int, List<ComponentBounds>>();
        foreach (ComponentBounds member in members)
        {
            token.ThrowIfCancellationRequested();
            int interval = -1;
            for (int i = 0; i + 1 < ticks.Count; i++)
            {
                if (member.Left > ticks[i] && member.Right < ticks[i + 1]) { interval = i; break; }
            }
            if (interval < 0) return null;
            if (!intervals.TryGetValue(interval, out List<ComponentBounds>? group)) intervals.Add(interval, group = []);
            group.Add(member);
        }
        if (intervals.Count < 3) return null;
        int LeftWithCancellation(ComponentBounds member) { token.ThrowIfCancellationRequested(); return member.Left; }
        int RightWithCancellation(ComponentBounds member) { token.ThrowIfCancellationRequested(); return member.Right; }
        var comparer = Comparer<ComponentBounds>.Create((first, second) =>
        {
            token.ThrowIfCancellationRequested();
            int horizontal = first.Left.CompareTo(second.Left);
            return horizontal != 0 ? horizontal : first.Top.CompareTo(second.Top);
        });
        var ordered = intervals.Values.Select(group =>
        {
            token.ThrowIfCancellationRequested();
            try { return group.OrderBy(member => member, comparer).ToArray(); }
            catch (InvalidOperationException) when (token.IsCancellationRequested)
            {
                // Sorting may wrap comparer cancellation; preserve cancellation semantics.
                token.ThrowIfCancellationRequested();
                throw;
            }
        }).ToArray();
        var clusters = new List<(ComponentBounds Bounds, int IntervalCount)>(ordered.Length);
        var parts = reviewPairs is null ? null : new ComponentBounds[ordered.Length];
        for (int groupIndex = 0; groupIndex < ordered.Length; groupIndex++)
        {
            token.ThrowIfCancellationRequested();
            ComponentBounds[] group = ordered[groupIndex];
            ComponentBounds part = group[0];
            for (int i = 1; i < group.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                part = part.Merge(group[i]);
            }
            int overlap = Math.Min(part.Bottom, line.Bottom) - Math.Max(part.Top, line.Top) + 1;
            int union = Math.Max(part.Bottom, line.Bottom) - Math.Min(part.Top, line.Top) + 1;
            if (overlap <= 0 || overlap / (double)union < _options.MinimumVerticalOverlapFraction) return null;
            if (parts is not null) parts[groupIndex] = part;
            if (groupIndex > 0)
            {
                ComponentBounds previous = ordered[groupIndex - 1].MaxBy(RightWithCancellation);
                ComponentBounds next = group.MinBy(LeftWithCancellation);
                if (next.Left - previous.Right - 1 <= Math.Max(previous.Width, next.Width))
                {
                    var last = clusters[^1];
                    clusters[^1] = (last.Bounds.Merge(part), last.IntervalCount + 1);
                    continue;
                }
            }
            clusters.Add((part, 1));
        }
        // There is measured interval evidence for the whole row, but too few
        // independent labels to split it. Preserve both its pixels and ambiguity.
        if (clusters.Count(static cluster => cluster.IntervalCount == 1) < 3)
            return [line with { AmbiguousIntervalXLabel = true }];
        if (parts is not null && reviewPairs is not null)
        {
            int[] cells = intervals.Keys.ToArray();
            int index = 0;
            foreach (var cluster in clusters)
            {
                token.ThrowIfCancellationRequested();
                if (cluster.IntervalCount == 2 && cells[index + 1] == cells[index] + 1 &&
                    CoversMidpoint(index) && CoversMidpoint(index + 1) &&
                    Math.Max(parts[index].Top, parts[index + 1].Top) <= Math.Min(parts[index].Bottom, parts[index + 1].Bottom))
                {
                    // Keep the parent as the detected region. Only the review reader may
                    // compare these two alternative crops against the uncut reading.
                    reviewPairs[ToRegion(image, cluster.Bounds).RegionId] =
                    [
                        ToRegion(image, parts[index] with { IntervalXLabel = true }),
                        ToRegion(image, parts[index + 1] with { IntervalXLabel = true }),
                    ];
                }
                index += cluster.IntervalCount;
            }

            bool CoversMidpoint(int partIndex)
            {
                int cell = cells[partIndex];
                double middle = (ticks[cell] + (double)ticks[cell + 1]) / 2;
                return parts[partIndex].Left <= middle && middle < parts[partIndex].Right + 1;
            }
        }
        return clusters.Select(static cluster => cluster.Bounds with
        {
            IntervalXLabel = cluster.IntervalCount == 1,
            AmbiguousIntervalXLabel = cluster.IntervalCount > 1,
        }).ToList();
    }

    private List<ComponentBounds> GroupVerticalGlyphs(List<ComponentBounds> horizontalLines,
        OcrImage image, OcrMeasuredTickGrouping? grouping, CancellationToken cancellationToken)
    {
        int[] yTicks = grouping?.YTicks is { Count: >= 3 and <= 128 } suppliedTicks ? suppliedTicks.ToArray() : [];
        double plotLeft = grouping?.PlotLeft ?? double.NaN;
        if (image.OriginalToImage != OcrFrameTransform.Identity || !double.IsFinite(plotLeft) ||
            plotLeft < 0 || plotLeft >= image.Width || grouping is null ||
            !double.IsFinite(grouping.PlotBottom) || grouping.PlotBottom < 0 || grouping.PlotBottom >= image.Height ||
            yTicks.Length is < 3 or > 128 || yTicks.Any(tick => tick < 0 || tick > grouping.PlotBottom || tick >= image.Height) ||
            !yTicks.Zip(yTicks.Skip(1)).All(pair => pair.First < pair.Second)) yTicks = [];
        var boundaries = new List<(double Position, ComponentBounds[] Crossing)>();
        for (int index = 1; index < yTicks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double position = (yTicks[index - 1] + (double)yTicks[index]) / 2;
            var crossing = new List<ComponentBounds>();
            foreach (ComponentBounds component in horizontalLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Top < position && component.Bottom + 1 > position) crossing.Add(component);
            }
            boundaries.Add((position, crossing.ToArray()));
        }
        int SupportedLane(ComponentBounds component)
        {
            if (component.Right >= plotLeft) return -1;
            for (int index = 0; index < yTicks.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Top > yTicks[index] || component.Bottom < yTicks[index]) continue;
                double start = index > 0 ? boundaries[index - 1].Position : 0;
                double end = index < boundaries.Count ? boundaries[index].Position : image.Height;
                return component.Top >= start && component.Bottom + 1 <= end ? index : -1;
            }
            return -1;
        }
        bool SeparateMeasuredRows(ComponentBounds first, ComponentBounds second, int gap)
        {
            if (gap <= Math.Max(first.Height, second.Height)) return false;
            int firstLane = SupportedLane(first), secondLane = SupportedLane(second);
            if (firstLane < 0 || secondLane < 0 || firstLane == secondLane) return false;
            for (int index = Math.Min(firstLane, secondLane); index < Math.Max(firstLane, secondLane); index++)
                foreach (ComponentBounds crossing in boundaries[index].Crossing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (crossing.Left <= Math.Min(first.Right, second.Right) &&
                        crossing.Right >= Math.Max(first.Left, second.Left)) return false;
                }
            return true;
        }
        var remaining = horizontalLines
            .OrderBy(static component => component.Left)
            .ThenBy(static component => component.Top)
            .ToList();
        var lines = new List<ComponentBounds>();
        while (remaining.Count > 0)
        {
            var line = remaining[0];
            remaining.RemoveAt(0);
            if (line.IntervalXLabel || line.AmbiguousIntervalXLabel || !IsRotatedGlyphCandidate(line))
            {
                lines.Add(line);
                continue;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var index = remaining.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = remaining[index];
                    if (candidate.IntervalXLabel || candidate.AmbiguousIntervalXLabel || !IsRotatedGlyphCandidate(candidate))
                    {
                        continue;
                    }

                    var horizontalOverlap = Math.Max(
                        0,
                        Math.Min(line.Right, candidate.Right) - Math.Max(line.Left, candidate.Left) + 1);
                    var overlapFraction = horizontalOverlap /
                        (double)Math.Max(1, Math.Min(line.Width, candidate.Width));
                    var verticalGap = candidate.Top > line.Bottom
                        ? candidate.Top - line.Bottom - 1
                        : line.Top > candidate.Bottom
                            ? line.Top - candidate.Bottom - 1
                            : 0;
                    var maximumGap = Math.Max(line.Width, candidate.Width) * _options.MaximumLineGapHeightRatio;
                    if (overlapFraction >= _options.MinimumVerticalOverlapFraction && verticalGap <= maximumGap &&
                        !SeparateMeasuredRows(line, candidate, verticalGap))
                    {
                        line = line.Merge(candidate) with { ObservedVerticalGrouping = true };
                        remaining.RemoveAt(index);
                        changed = true;
                    }
                }
            }

            lines.Add(line);
        }

        return lines;

        static bool IsRotatedGlyphCandidate(ComponentBounds component) =>
            component.ComponentCount == 1 &&
            component.Width >= component.Height * 1.2 &&
            component.MarkerLikeComponentCount == 0;
    }

    private OcrDetectedRegion ToRegion(OcrImage image, ComponentBounds line)
    {
        var topLeft = image.OriginalToImage.MapToOriginal(new OcrPoint(line.Left, line.Top));
        var bottomRight = image.OriginalToImage.MapToOriginal(new OcrPoint(line.Right + 1, line.Bottom + 1));
        var rectangle = new OcrRectangle(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
        var density = line.Area / (double)Math.Max(1, line.Width * line.Height);
        var textLikelihood = line.MarkerLikeComponentCount == line.ComponentCount
            ? 0.12
            : line.ComponentCount > 1
            ? Math.Clamp(0.68 + (line.ComponentCount * 0.06), 0, 0.94)
            : Math.Clamp(0.58 - (line.StructureLikelihood * 0.30), 0.15, 0.65);
        var confidence = Math.Clamp(0.45 + (0.35 * textLikelihood) + (0.10 * Math.Min(1, density * 3)), 0, 0.92);
        var orientation = _options.UseObservedVerticalGroupingOrientation
            ? line.ObservedVerticalGrouping ? -90d : 0d
            : line.Height > line.Width * 1.4 ? -90d : 0d;
        var structureReasons = StructureReasons(line);
        if (line.IntervalXLabel) structureReasons.Add(IntervalXLabelReason);
        if (line.AmbiguousIntervalXLabel) structureReasons.Add(AmbiguousIntervalXLabelReason);
        return new OcrDetectedRegion(
            DeterministicRegionId(rectangle),
            OcrPolygon.FromRectangle(rectangle),
            orientation,
            confidence,
            Evidence: new OcrRegionEvidence(
                line.ComponentCount,
                density,
                textLikelihood,
                line.StructureLikelihood,
                IsLikelyGraphStructure(line),
                OcrCollections.Freeze(structureReasons)));
    }

    private static ComponentBounds AddMorphology(
        OcrImage image,
        ComponentBounds bounds,
        IReadOnlyList<int> componentPixels)
    {
        var rows = new int[bounds.Height];
        var columns = new int[bounds.Width];
        foreach (var pixel in componentPixels)
        {
            var x = pixel % image.Width;
            var y = pixel / image.Width;
            rows[y - bounds.Top]++;
            columns[x - bounds.Left]++;
        }

        var maximumRowFraction = rows.Max() / (double)Math.Max(1, bounds.Width);
        var maximumColumnFraction = columns.Max() / (double)Math.Max(1, bounds.Height);
        var density = bounds.Area / (double)Math.Max(1, bounds.Width * bounds.Height);
        return bounds with
        {
            MaximumRowFillFraction = maximumRowFraction,
            MaximumColumnFillFraction = maximumColumnFraction,
            Density = density,
            MarkerLikeComponentCount = IsCompactMarkerLike(
                bounds.Width,
                bounds.Height,
                density,
                maximumRowFraction,
                maximumColumnFraction) ? 1 : 0,
        };
    }

    private static bool IsLikelyGraphStructure(ComponentBounds component)
    {
        if (component.ComponentCount > 1)
        {
            return component.MarkerLikeComponentCount == component.ComponentCount;
        }

        var aspect = component.Width / (double)Math.Max(1, component.Height);
        var longThin = (aspect >= 2.5 || aspect <= 0.4) && component.Density >= 0.60;
        var compact = aspect is >= 0.65 and <= 1.55;
        var compactStroke = compact &&
            component.MaximumRowFillFraction >= 0.80 &&
            component.MaximumColumnFillFraction >= 0.80 &&
            component.Width <= 20 && component.Height <= 20;
        var lineIntersection = compact && component.Density <= 0.60 &&
            component.MaximumRowFillFraction >= 0.80 &&
            component.MaximumColumnFillFraction >= 0.80;
        return longThin || compactStroke || lineIntersection;
    }

    private static bool IsCompactMarkerLike(
        int width,
        int height,
        double density,
        double maximumRowFillFraction,
        double maximumColumnFillFraction)
    {
        var aspect = width / (double)Math.Max(1, height);
        return aspect is >= 0.65 and <= 1.55 &&
            width is >= 3 and <= 20 && height is >= 3 and <= 20 &&
            density >= 0.25 &&
            maximumRowFillFraction >= 0.60 &&
            maximumColumnFillFraction >= 0.60;
    }

    private static List<string> StructureReasons(ComponentBounds component)
    {
        var reasons = new List<string>();
        var aspect = component.Width / (double)Math.Max(1, component.Height);
        if (component.ComponentCount > 1 &&
            component.MarkerLikeComponentCount == component.ComponentCount)
        {
            reasons.Add("repeated_compact_marker_components");
        }

        if (component.ComponentCount == 1 && (aspect >= 2.5 || aspect <= 0.4))
        {
            reasons.Add("single_component_line_or_tick_like");
        }

        if (component.ComponentCount == 1 && aspect is >= 0.65 and <= 1.55 &&
            component.MaximumRowFillFraction >= 0.80 && component.MaximumColumnFillFraction >= 0.80)
        {
            reasons.Add("single_component_marker_arrow_or_intersection_like");
        }

        if (reasons.Count == 0)
        {
            reasons.Add(component.ComponentCount > 1 ? "multi_glyph_line_evidence" : "single_glyph_candidate");
        }

        return reasons;
    }

    private static byte EstimateThreshold(OcrImage image, CancellationToken cancellationToken)
    {
        long sum = 0;
        for (var y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                sum += image.Pixels.Span[(y * image.Stride) + x];
            }
        }

        var mean = sum / (double)checked(image.Width * image.Height);
        return (byte)Math.Clamp(Math.Round(mean * 0.80), 32, 224);
    }

    private static bool IsForeground(OcrImage image, int x, int y, byte threshold) =>
        image.Pixels.Span[(y * image.Stride) + x] <= threshold;

    private static string DeterministicRegionId(OcrRectangle rectangle)
    {
        var text = FormattableString.Invariant($"{rectangle.X:R},{rectangle.Y:R},{rectangle.Width:R},{rectangle.Height:R}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    private static void ValidateImage(OcrImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length < checked(image.Stride * image.Height) ||
            !image.OriginalToImage.IsInvertible)
        {
            throw new ArgumentException("OCR detector image is invalid.", nameof(image));
        }
    }

    private static void ValidateOptions(ConnectedComponentTextRegionDetectorOptions options)
    {
        if (options.MinimumComponentArea <= 0 ||
            options.MaximumComponentWidthFraction is <= 0 or > 1 ||
            options.MaximumComponentHeightFraction is <= 0 or > 1 ||
            options.MaximumLineGapHeightRatio < 0 ||
            options.MinimumVerticalOverlapFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private readonly record struct ComponentBounds(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int Area,
        int ComponentCount,
        double Density,
        double MaximumRowFillFraction,
        double MaximumColumnFillFraction,
        int MarkerLikeComponentCount)
    {
        public bool ObservedVerticalGrouping { get; init; }
        public bool IntervalXLabel { get; init; }
        public bool AmbiguousIntervalXLabel { get; init; }

        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;

        public double StructureLikelihood => ComponentCount > 1
            ? MarkerLikeComponentCount == ComponentCount ? 0.98 : 0.08
            : IsLikelyGraphStructure(this) ? 0.95 : 0.35;

        public static ComponentBounds Start(int x, int y) =>
            new(x, y, x, y, 0, 1, 0, 0, 0, 0);

        public ComponentBounds Include(int x, int y) =>
            this with
            {
                Left = Math.Min(Left, x),
                Top = Math.Min(Top, y),
                Right = Math.Max(Right, x),
                Bottom = Math.Max(Bottom, y),
                Area = Area + 1,
            };

        public ComponentBounds Merge(ComponentBounds other) =>
            new(
                Math.Min(Left, other.Left),
                Math.Min(Top, other.Top),
                Math.Max(Right, other.Right),
                Math.Max(Bottom, other.Bottom),
                Area + other.Area,
                ComponentCount + other.ComponentCount,
                0,
                Math.Max(MaximumRowFillFraction, other.MaximumRowFillFraction),
                Math.Max(MaximumColumnFillFraction, other.MaximumColumnFillFraction),
                MarkerLikeComponentCount + other.MarkerLikeComponentCount);
    }
}
