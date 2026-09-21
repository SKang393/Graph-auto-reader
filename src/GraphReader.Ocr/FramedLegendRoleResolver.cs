// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

/// <summary>Supplies post-OCR legend context from an original-pixel frame and separate symbol.</summary>
public static class FramedLegendRoleResolver
{
    public const string CompositionVersion = "original-pixel-framed-legend-context-v2";
    public const string RecoveryCompositionVersion = "original-pixel-framed-legend-text-recovery-and-assembly-v2";

    /// <summary>Joins detected words only when original pixels establish one framed legend row.</summary>
    public static IReadOnlyList<OcrDetectedRegion> AssembleDetectedRows(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> regions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImage(image);
        foreach (OcrDetectedRegion region in regions)
        {
            OcrRectangle box = region.Polygon.Bounds;
            if (!box.IsValid || box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height ||
                region.CoordinateSpace != OcrContract.CoordinateSpace)
                throw new ArgumentException("Legend words must retain original pixel geometry.", nameof(regions));
        }
        IReadOnlyList<OcrDetectedRegion> completed = CompleteSingleRowTextBounds(
            image, regions, cancellationToken, preserveOtherDetections: false);
        var replaced = new HashSet<int>();
        var merged = new Dictionary<int, OcrDetectedRegion>();
        foreach (int index in Enumerable.Range(0, regions.Count).OrderBy(i => regions[i].Polygon.Bounds.Left)
                     .ThenBy(i => regions[i].Polygon.Bounds.Top).ThenBy(i => regions[i].RegionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (replaced.Contains(index) || completed[index].Polygon == regions[index].Polygon) continue;
            OcrRectangle row = completed[index].Polygon.Bounds;
            int[] members = Enumerable.Range(0, regions.Count).Where(i => Overlaps(row, regions[i].Polygon.Bounds))
                .OrderBy(i => regions[i].Polygon.Bounds.Left).ToArray();
            if (members.Length < 2 || members.Any(i => replaced.Contains(i) || HasProtectedContext(regions[i]) ||
                    !Contains(row, regions[i].Polygon.Bounds))) continue;
            bool aligned = true;
            for (int i = 1; i < members.Length; i++)
            {
                OcrRectangle left = regions[members[i - 1]].Polygon.Bounds, right = regions[members[i]].Polygon.Bounds;
                double overlap = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top);
                double gap = right.Left - left.Right;
                if (gap <= 0 || gap > Math.Min(left.Height, right.Height) ||
                    overlap < 0.35 * Math.Min(left.Height, right.Height) ||
                    Math.Max(left.Height, right.Height) > 2 * Math.Min(left.Height, right.Height)) aligned = false;
            }
            if (!aligned) continue;
            string material = RecoveryCompositionVersion + "\n" + string.Join('\n',
                members.Select(i => regions[i].RegionId).Order(StringComparer.Ordinal));
            merged[index] = completed[index] with
            {
                RegionId = "framed-legend-row:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
                DetectionConfidence = members.Min(i => regions[i].DetectionConfidence),
                Context = members.All(i => Equals(regions[i].Context, regions[index].Context)) ? regions[index].Context : null,
                Evidence = null,
            };
            foreach (int member in members) replaced.Add(member);
        }
        return OcrCollections.Freeze(Enumerable.Range(0, regions.Count)
            .Where(i => !replaced.Contains(i) || merged.ContainsKey(i))
            .Select(i => merged.TryGetValue(i, out OcrDetectedRegion? region) ? region : regions[i]));

        static bool Overlaps(OcrRectangle a, OcrRectangle b) => a.Left < b.Right && a.Right > b.Left &&
            a.Top < b.Bottom && a.Bottom > b.Top;
        static bool Contains(OcrRectangle outer, OcrRectangle inner) => inner.Left >= outer.Left && inner.Top >= outer.Top &&
            inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    }

    private static bool HasProtectedContext(OcrDetectedRegion region) =>
        GraphTextRoleClassifier.GetOrientation(region.OrientationDegrees) != OcrOrientation.Horizontal ||
        region.Context is { ExplicitRoleHint: not null } or { NearAnnotationArrow: true } or
            { NearPhaseDivider: true } or { NumericExpected: true } or { AxisTitleExpected: true } or { InParticipantBand: true };

    /// <summary>Proposes missing text crops from pixels, without supplying a word or role.</summary>
    public static async ValueTask<IReadOnlyList<OcrDetectedRegion>> RecoverMissingTextAsync(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> detected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(detected);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImage(image);
        foreach (OcrDetectedRegion region in detected)
        {
            OcrRectangle box = region.Polygon.Bounds;
            if (!box.IsValid || box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height ||
                region.CoordinateSpace != OcrContract.CoordinateSpace)
                throw new ArgumentException("Existing detections must retain original pixel geometry.", nameof(detected));
        }
        var detector = new ConnectedComponentTextRegionDetector(
            new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false });
        IReadOnlyList<OcrDetectedRegion> components = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
        bool[] ink = CreateInkMask(image, cancellationToken);
        List<HorizontalRun> runs = FindRuns(ink, image.Width, image.Height, cancellationToken);
        var frames = new HashSet<OcrRectangle>();
        var recovered = new List<OcrDetectedRegion>();
        foreach (OcrDetectedRegion component in components.OrderBy(static r => r.Polygon.Bounds.Left)
                     .ThenBy(static r => r.Polygon.Bounds.Top))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrDetectedRegion seed = component with
            {
                RegionId = "framed-legend:" + component.RegionId,
                OrientationDegrees = 0,
                DetectionConfidence = Math.Min(component.DetectionConfidence, 0.70),
                Evidence = null,
            };
            FramedLegendRoleEvidence? frame = FindContext(ink, image.Width, runs,
                seed.RegionId, seed.Polygon.Bounds, cancellationToken, incompleteRow: true);
            if (frame is null || frames.Contains(frame.FrameBounds) ||
                detected.Any(r => Overlaps(r.Polygon.Bounds, frame.FrameBounds))) continue;
            // A normal first letter must not become a supposed legend symbol.
            if (seed.Polygon.Bounds.Left - frame.GlyphBounds.Right < 0.5 * seed.Polygon.Bounds.Height) continue;

            OcrDetectedRegion completed = CompleteSingleRowTextBounds(
                image, [seed], cancellationToken, allowSingleGlyphSeed: true).Single();
            OcrRectangle text = completed.Polygon.Bounds;
            if (text.Width < 3 * text.Height || text.Height > 1.5 * component.Polygon.Bounds.Height) continue;
            FramedLegendRoleEvidence? confirmed = FindContext(ink, image.Width, runs,
                completed.RegionId, text, cancellationToken);
            if (confirmed is null || confirmed.FrameBounds != frame.FrameBounds || confirmed.GlyphBounds != frame.GlyphBounds) continue;

            OcrRectangle[] inside = components.Select(static r => r.Polygon.Bounds)
                .Where(b => StrictlyContains(frame.FrameBounds, b)).ToArray();
            if (inside.Count(b => Contains(text, b)) < 3 ||
                inside.Any(b => !Contains(text, b) && !Contains(frame.GlyphBounds, b))) continue;
            if (recovered.Any(r => Overlaps(r.Polygon.Bounds, text))) continue;
            frames.Add(frame.FrameBounds);
            recovered.Add(completed);
        }
        return OcrCollections.Freeze(recovered);

        static bool Contains(OcrRectangle outer, OcrRectangle inner) => inner.Left >= outer.Left &&
            inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
        static bool StrictlyContains(OcrRectangle outer, OcrRectangle inner) => inner.Left > outer.Left &&
            inner.Top > outer.Top && inner.Right < outer.Right && inner.Bottom < outer.Bottom;
        static bool Overlaps(OcrRectangle a, OcrRectangle b) => a.Left < b.Right && a.Right > b.Left &&
            a.Top < b.Bottom && a.Bottom > b.Top;
    }

    public static FramedLegendRoleResolution Resolve(
        OcrImage image,
        IReadOnlyList<OcrRegion> regions,
        IReadOnlyList<OcrDetectedRegion> detectedRegions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(detectedRegions);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImage(image);
        var detections = detectedRegions.ToDictionary(static region => region.RegionId, StringComparer.Ordinal);
        var eligible = new List<OcrRegion>();
        foreach (OcrRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle box = region.Polygon.Bounds;
            if (!box.IsValid || box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height ||
                region.CoordinateSpace != OcrContract.CoordinateSpace || !detections.TryGetValue(region.RegionId, out OcrDetectedRegion? detected))
            {
                throw new ArgumentException("Recognized regions must retain valid original detection geometry.", nameof(regions));
            }
            OcrRegionContext? context = detected.Context;
            if (region.Role is not (OcrTextRole.Annotation or OcrTextRole.Other) ||
                region.ReviewStatus != OcrReviewStatus.Unreviewed || string.IsNullOrWhiteSpace(region.Text) ||
                GraphNumericParser.IsLiteralGraphNumber(region.Text) ||
                GraphTextRoleClassifier.GetOrientation(detected.OrientationDegrees) != OcrOrientation.Horizontal ||
                context?.ExplicitRoleHint is not null || context?.NearAnnotationArrow is true ||
                context?.NearPhaseDivider is true || context?.NumericExpected is true ||
                context?.AxisTitleExpected is true || context?.InParticipantBand is true)
            {
                continue;
            }
            eligible.Add(region);
        }
        if (eligible.Count == 0)
        {
            return new(OcrCollections.Freeze(regions), Array.Empty<FramedLegendRoleEvidence>());
        }

        bool[] ink = CreateInkMask(image, cancellationToken);
        List<HorizontalRun> runs = FindRuns(ink, image.Width, image.Height, cancellationToken);
        List<FramedLegendRoleEvidence> evidence = FindContexts(ink, image.Width, runs, eligible, cancellationToken);
        HashSet<string> changed = evidence.Select(static item => item.RegionId).ToHashSet(StringComparer.Ordinal);
        return new(
            OcrCollections.Freeze(regions.Select(region => changed.Contains(region.RegionId)
                ? region with { Role = OcrTextRole.LegendText, Confidence = Math.Min(region.Confidence, 0.70) }
                : region)),
            OcrCollections.Freeze(evidence));
    }

    /// <summary>
    /// Locates symbols beside already recognized legend labels. These are legend
    /// evidence only and must never be supplied to graph calibration as observations.
    /// </summary>
    public static IReadOnlyList<FramedLegendRoleEvidence> LocateSymbols(
        OcrImage image, IReadOnlyList<OcrRegion> regions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateImage(image);
        OcrRegion[] labels = regions.Where(static region => region.Role == OcrTextRole.LegendText &&
            region.ReviewStatus != OcrReviewStatus.Rejected && !string.IsNullOrWhiteSpace(region.Text)).ToArray();
        foreach (OcrRegion label in labels)
        {
            OcrRectangle box = label.Polygon.Bounds;
            if (!box.IsValid || box.Left < 0 || box.Top < 0 || box.Right > image.Width || box.Bottom > image.Height ||
                label.CoordinateSpace != OcrContract.CoordinateSpace)
                throw new ArgumentException("Legend labels must retain original pixel geometry.", nameof(regions));
        }
        if (labels.Length == 0) return Array.Empty<FramedLegendRoleEvidence>();
        bool[] ink = CreateInkMask(image, cancellationToken);
        List<HorizontalRun> runs = FindRuns(ink, image.Width, image.Height, cancellationToken);
        return OcrCollections.Freeze(FindContexts(ink, image.Width, runs, labels, cancellationToken));
    }

    private static List<FramedLegendRoleEvidence> FindContexts(
        bool[] ink, int width, List<HorizontalRun> runs, IReadOnlyList<OcrRegion> labels,
        CancellationToken cancellationToken)
    {
        var evidence = new List<FramedLegendRoleEvidence>();
        foreach (OcrRegion label in labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FramedLegendRoleEvidence? item = FindContext(ink, width, runs, label.RegionId, label.Polygon.Bounds, cancellationToken);
            if (item is not null) evidence.Add(item);
        }
        // A shorter row can share a frame established by a longer label, but must
        // have its own detached symbol. This does not widen the frame horizontally.
        FramedLegendRoleEvidence[] anchors = evidence.ToArray();
        OcrRectangle[] frames = anchors.Select(static item => item.FrameBounds).Distinct().ToArray();
        HashSet<string> anchored = evidence.Select(static item => item.RegionId).ToHashSet(StringComparer.Ordinal);
        foreach (OcrRegion label in labels.Where(label => !anchored.Contains(label.RegionId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle text = label.Polygon.Bounds;
            var matches = new List<FramedLegendRoleEvidence>();
            foreach (OcrRectangle frame in frames)
            {
                if (text.Left <= frame.Left || text.Top <= frame.Top || text.Right >= frame.Right || text.Bottom >= frame.Bottom)
                    continue;
                OcrRectangle? glyph = FindSymbol(ink, width, frame, text, cancellationToken);
                if (glyph.HasValue && anchors.Any(anchor => anchor.FrameBounds == frame &&
                        glyph.Value.Left < anchor.GlyphBounds.Right && glyph.Value.Right > anchor.GlyphBounds.Left))
                    matches.Add(new(label.RegionId, frame, glyph.Value));
            }
            if (matches.Count == 1) evidence.Add(matches[0]);
        }
        return evidence;
    }

    /// <summary>
    /// Completes a truncated row only inside a small closed frame with a
    /// detached symbol. The frame supplies a search boundary, never text.
    /// </summary>
    internal static IReadOnlyList<OcrDetectedRegion> CompleteSingleRowTextBounds(
        OcrImage image, IReadOnlyList<OcrDetectedRegion> regions, CancellationToken cancellationToken,
        bool allowSingleGlyphSeed = false, bool preserveOtherDetections = true)
    {
        bool[] ink = CreateInkMask(image, cancellationToken);
        List<HorizontalRun> runs = FindRuns(ink, image.Width, image.Height, cancellationToken);
        var result = new List<OcrDetectedRegion>(regions.Count);
        foreach (OcrDetectedRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRectangle box = region.Polygon.Bounds;
            OcrRegionContext? context = region.Context;
            if (GraphTextRoleClassifier.GetOrientation(region.OrientationDegrees) != OcrOrientation.Horizontal ||
                (!allowSingleGlyphSeed && box.Width < 2 * box.Height) || context?.ExplicitRoleHint is not null ||
                context?.NearAnnotationArrow is true || context?.NearPhaseDivider is true ||
                context?.NumericExpected is true || context?.AxisTitleExpected is true ||
                context?.InParticipantBand is true)
            {
                result.Add(region);
                continue;
            }
            FramedLegendRoleEvidence? frame = FindContext(
                ink, image.Width, runs, region.RegionId, box, cancellationToken, incompleteRow: true);
            // A row already reaching the normal two-height frame margin is
            // complete enough for existing legend context. Do not chase noise
            // or an overlapping arrow beyond that established text extent.
            if (frame is null || frame.FrameBounds.Right <= box.Right + 2 * box.Height)
            {
                result.Add(region);
                continue;
            }
            int top = Math.Max((int)frame.FrameBounds.Top + 2, (int)Math.Floor(box.Top - box.Height / 2));
            int bottom = Math.Min((int)frame.FrameBounds.Bottom - 2, (int)Math.Ceiling(box.Bottom + box.Height / 2));
            int end = (int)frame.FrameBounds.Right - 2;
            bool[] frameConnected = FrameConnectedInk(ink, image.Width, frame.FrameBounds, cancellationToken);
            int frameLeft = (int)frame.FrameBounds.Left, frameTop = (int)frame.FrameBounds.Top;
            int frameWidth = (int)frame.FrameBounds.Width;
            int lastInkRight = (int)Math.Ceiling(box.Right);
            double left = box.Left, right = box.Right, y0 = box.Top, y1 = box.Bottom;
            for (int x = (int)Math.Ceiling(box.Left); x < end; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A word-sized gap may connect the row; a detached object may not.
                if (x >= box.Right && x - lastInkRight > box.Height) break;
                for (int y = top; y < bottom; y++)
                {
                    if (!ink[y * image.Width + x] || frameConnected[(y - frameTop) * frameWidth + x - frameLeft]) continue;
                    lastInkRight = x + 1;
                    right = Math.Max(right, x + 1);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y + 1);
                }
            }
            var completed = new OcrRectangle(left, y0, right - left, y1 - y0);
            // Keep separate detector evidence separate. Existing word assembly
            // handles rows for which both fragments were already detected.
            bool overlapsOther = regions.Any(other => other.RegionId != region.RegionId &&
                other.Polygon.Bounds.Left < completed.Right && other.Polygon.Bounds.Right > completed.Left &&
                other.Polygon.Bounds.Top < completed.Bottom && other.Polygon.Bounds.Bottom > completed.Top);
            result.Add(right > box.Right + 1 && (!preserveOtherDetections || !overlapsOther)
                ? region with { Polygon = OcrPolygon.FromRectangle(completed), Evidence = null }
                : region);
        }
        return OcrCollections.Freeze(result);
    }

    private static bool[] FrameConnectedInk(
        bool[] ink, int imageWidth, OcrRectangle frame, CancellationToken cancellationToken)
    {
        int left = (int)frame.Left, top = (int)frame.Top;
        int width = (int)frame.Width, height = (int)frame.Height;
        var connected = new bool[checked(width * height)];
        var queue = new Queue<int>();
        void Add(int x, int y)
        {
            int index = y * width + x;
            if (connected[index] || !ink[(top + y) * imageWidth + left + x]) return;
            connected[index] = true;
            queue.Enqueue(index);
        }
        for (int x = 0; x < width; x++) { Add(x, 0); Add(x, height - 1); }
        for (int y = 0; y < height; y++) { Add(0, y); Add(width - 1, y); }
        while (queue.TryDequeue(out int index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int x = index % width, y = index / width;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && yy >= 0 && xx < width && yy < height) Add(xx, yy);
            }
        }
        // Borders and crossing arrows/lines are structure, not trailing words.
        // This is a read-only search mask; original recognition pixels stay intact.
        return connected;
    }

    private static void ValidateImage(OcrImage image)
    {
        if (image.SourceImage != OcrSourceImage.Original || image.OriginalToImage != OcrFrameTransform.Identity ||
            image.CoordinateSpace != OcrContract.CoordinateSpace || image.Width <= 0 || image.Height <= 0 ||
            image.Stride < image.Width || image.Pixels.Length < (long)image.Stride * image.Height)
            throw new ArgumentException("Legend context requires aligned original Gray8 pixels.", nameof(image));
    }

    private static bool[] CreateInkMask(OcrImage image, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) sum += pixels[y * image.Stride + x];
        }
        // Same full-image foreground rule as the existing component detector.
        int threshold = Math.Clamp((int)Math.Round(sum / ((double)image.Width * image.Height) * 0.80), 32, 224);
        var ink = new bool[checked(image.Width * image.Height)];
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) ink[y * image.Width + x] = pixels[y * image.Stride + x] <= threshold;
        }
        return ink;
    }

    private static List<HorizontalRun> FindRuns(bool[] ink, int width, int height, CancellationToken cancellationToken)
    {
        var runs = new List<HorizontalRun>();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = -1, last = -1, count = 0;
            for (int x = 0; x <= width; x++)
            {
                bool foreground = x < width && ink[y * width + x];
                if (start >= 0 && (x == width || (foreground && x - last > 3)))
                {
                    if (count >= 8) runs.Add(new(start, y, last + 1, count / (double)(last - start + 1)));
                    start = -1;
                    count = 0;
                }
                if (!foreground) continue;
                if (start < 0) start = x;
                last = x;
                count++;
            }
        }
        return runs;
    }

    private static FramedLegendRoleEvidence? FindContext(
        bool[] ink, int width, List<HorizontalRun> runs, string regionId, OcrRectangle box, CancellationToken cancellationToken,
        bool incompleteRow = false)
    {
        double height = box.Height;
        HorizontalRun[] candidates = runs.Where(run =>
            run.Left >= box.Left - 5 * height && run.Left < box.Left &&
            run.Right >= box.Right && run.Right <= (incompleteRow ? box.Left + 40 * height : box.Right + 2 * height) &&
            run.Right - run.Left >= box.Width + height && run.Density >= 0.9).ToArray();
        // Frame height depends on the number of legend rows, not this row's font
        // size. Require both vertical edges instead of a four-text-height cutoff.
        HorizontalRun[] above = candidates.Where(run => run.Y < box.Top)
            .OrderByDescending(static run => run.Y).ThenBy(static run => run.Left).ToArray();
        HorizontalRun[] below = candidates.Where(run => run.Y >= box.Bottom)
            .OrderBy(static run => run.Y).ThenBy(static run => run.Left).ToArray();
        foreach (HorizontalRun upper in above)
        foreach (HorizontalRun lower in below)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (incompleteRow && lower.Y - upper.Y + 1 > 4 * height) continue;
            if (Math.Abs(upper.Left - lower.Left) > 2 || Math.Abs(upper.Right - lower.Right) > 2) continue;
            int left = Math.Min(upper.Left, lower.Left), right = Math.Max(upper.Right, lower.Right) - 1;
            if (!VerticalEdge(left, upper.Y, lower.Y) || !VerticalEdge(right, upper.Y, lower.Y)) continue;
            var frame = new OcrRectangle(left, upper.Y, right - left + 1, lower.Y - upper.Y + 1);
            OcrRectangle? glyph = FindSymbol(ink, width, frame, box, cancellationToken);
            if (glyph.HasValue) return new(regionId, frame, glyph.Value);
        }
        return null;

        bool VerticalEdge(int x, int top, int bottom)
        {
            int supported = 0;
            for (int y = top; y <= bottom; y++)
            {
                bool found = false;
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++) found |= ink[y * width + xx];
                if (found) supported++;
            }
            return supported / (double)(bottom - top + 1) >= 0.9;
        }
    }

    private static OcrRectangle? FindSymbol(
        bool[] ink, int width, OcrRectangle frame, OcrRectangle text, CancellationToken cancellationToken)
    {
        int left = Math.Max((int)frame.Left + 2, (int)Math.Floor(text.Left - 4 * text.Height));
        int right = (int)Math.Floor(text.Left - Math.Max(2, 0.15 * text.Height));
        int top = Math.Max((int)frame.Top + 2, (int)Math.Floor(text.Top - text.Height));
        int bottom = Math.Min((int)frame.Bottom - 1, (int)Math.Ceiling(text.Bottom + text.Height));
        if (right <= left || bottom <= top) return null;
        int localWidth = right - left, localHeight = bottom - top;
        var visited = new bool[checked(localWidth * localHeight)];
        var queue = new Queue<int>();
        OcrRectangle? found = null;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = (y - top) * localWidth + x - left;
            if (visited[index] || !ink[y * width + x]) continue;
            visited[index] = true;
            queue.Enqueue(index);
            int x0 = x, x1 = x + 1, y0 = y, y1 = y + 1;
            while (queue.TryDequeue(out int current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int cx = current % localWidth, cy = current / localWidth;
                x0 = Math.Min(x0, cx + left); x1 = Math.Max(x1, cx + left + 1);
                y0 = Math.Min(y0, cy + top); y1 = Math.Max(y1, cy + top + 1);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= localWidth || ny >= localHeight) continue;
                    int next = ny * localWidth + nx;
                    if (visited[next] || !ink[(ny + top) * width + nx + left]) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
            if (x0 <= left || x1 >= right || y0 <= top || y1 >= bottom) continue;
            double w = x1 - x0, h = y1 - y0;
            if (w < 0.35 * text.Height || w > 2 * text.Height || h < 0.35 * text.Height || h > 2 * text.Height ||
                w / h < 0.5 || w / h > 2 || Math.Abs((y0 + y1 - text.Top - text.Bottom) / 2) > 0.75 * text.Height ||
                text.Left - x1 < 0.15 * text.Height || text.Left - x1 > 2 * text.Height) continue;
            if (found.HasValue) return null;
            found = new(x0, y0, w, h);
        }
        return found;
    }

    private readonly record struct HorizontalRun(int Left, int Y, int Right, double Density);
}

public sealed record FramedLegendRoleEvidence(string RegionId, OcrRectangle FrameBounds, OcrRectangle GlyphBounds);

public sealed record FramedLegendRoleResolution(
    IReadOnlyList<OcrRegion> Regions,
    IReadOnlyList<FramedLegendRoleEvidence> Evidence);
