// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Ocr;

/// <summary>
/// An OCR request whose image planes are privately copied before asynchronous
/// work starts. This prevents later caller writes from changing the bytes used
/// for cache identity, detection, or recognition. A caller must not write to a
/// plane concurrently while the initial synchronous copy is in progress.
/// One additional buffer is held per distinct supplied memory segment until
/// recognition completes. The memory preflight is an estimate, not a guarantee
/// that allocations will succeed under concurrent or native memory pressure.
/// </summary>
internal sealed class OcrRequestImageSnapshot
{
    private const int CopyChunkBytes = 4 * 1024 * 1024;

    private OcrRequestImageSnapshot(OcrRequest request) => Request = request;

    internal OcrRequest Request { get; }

    internal OcrRequestImageSnapshot WithDetectedRegions(IReadOnlyList<OcrDetectedRegion> regions, CancellationToken cancellationToken) =>
        new(SnapshotGeometry(Request with { DetectedRegions = regions }, cancellationToken));

    private static IEnumerable<T> Cancellable<T>(IEnumerable<T> values, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (T value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static OcrRequest SnapshotGeometry(OcrRequest request, CancellationToken cancellationToken) => request with
    {
        DetectedRegions = request.DetectedRegions is null ? null : OcrCollections.Freeze(
            Cancellable(request.DetectedRegions, cancellationToken).Select(region =>
                region is null ? null! : region with
                {
                    Evidence = region.Evidence is { Reasons: { } reasons } evidence
                        ? evidence with { Reasons = OcrCollections.Freeze(Cancellable(reasons, cancellationToken)) }
                        : region.Evidence,
                })),
        PhaseDividerXs = request.PhaseDividerXs is null ? null : OcrCollections.Freeze(Cancellable(request.PhaseDividerXs, cancellationToken)),
        XAxisTickStrokes = request.XAxisTickStrokes is null ? null : OcrCollections.Freeze(
            Cancellable(request.XAxisTickStrokes, cancellationToken).Select(stroke =>
                stroke is null ? null! : stroke with
                {
                    SupportingCandidateIds = stroke.SupportingCandidateIds is null
                        ? null!
                        : OcrCollections.Freeze(Cancellable(stroke.SupportingCandidateIds, cancellationToken)),
                })),
    };

    internal static OcrRequestImageSnapshot Create(OcrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OriginalImage is null ||
            (request.DetectorImage is not null && request.DetectorImage.Image is null))
        {
            throw new ArgumentException("OCR original and detector images must be present when supplied.", nameof(request));
        }

        // Inspect all dimensions and compute the complete distinct allocation
        // before copying anything. Gray8 may legally have trailing bytes; keep
        // all of them because cache identity hashes the entire supplied span.
        OcrImage?[] images = [request.OriginalImage, request.EnhancedImage, request.DetectorImage?.Image];
        var distinct = new HashSet<ReadOnlyMemory<byte>>();
        long byteCount = 0;
        foreach (OcrImage? image in images)
        {
            if (image is null)
            {
                continue;
            }

            if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
                (long)image.Stride * image.Height > image.Pixels.Length)
            {
                throw new ArgumentException("OCR image dimensions, stride, or pixel buffer are invalid.", nameof(request));
            }
            Add(image.Pixels);
            if (image.BgrPixels is { } bgr)
            {
                if ((long)bgr.Stride < (long)image.Width * 3 ||
                    (long)bgr.Stride * image.Height != bgr.Pixels.Length)
                {
                    throw new ArgumentException("OCR BGR24 dimensions, stride, or pixel buffer are invalid.", nameof(request));
                }
                Add(bgr.Pixels);
            }
        }

        // The copy is bounded by the distinct supplied planes. Refuse a copy
        // that exceeds the runtime's reported managed-memory budget instead of
        // inventing a smaller image limit. This estimate is not a guarantee:
        // native allocations and concurrent work can still exhaust memory.
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available > 0 && byteCount > Math.Max(0, available - GC.GetTotalMemory(false)))
        {
            throw new InsufficientMemoryException("The OCR input snapshot exceeds available managed memory.");
        }

        var copies = new Dictionary<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>();
        OcrImage Clone(OcrImage image) => image with
        {
            Pixels = ClonePlane(image.Pixels),
            BgrPixels = image.BgrPixels is { } color
                ? color with { Pixels = ClonePlane(color.Pixels) }
                : null,
        };

        ReadOnlyMemory<byte> ClonePlane(ReadOnlyMemory<byte> source)
        {
            if (copies.TryGetValue(source, out ReadOnlyMemory<byte> prior))
            {
                return prior;
            }
            cancellationToken.ThrowIfCancellationRequested();
            byte[] target = new byte[source.Length];
            for (int offset = 0; offset < source.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(CopyChunkBytes, source.Length - offset);
                source.Span.Slice(offset, count).CopyTo(target.AsSpan(offset, count));
                offset += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> result = target;
            copies.Add(source, result);
            return result;
        }

        return new OcrRequestImageSnapshot(SnapshotGeometry(request with
        {
            OriginalImage = Clone(request.OriginalImage),
            EnhancedImage = request.EnhancedImage is { } enhanced ? Clone(enhanced) : null,
            DetectorImage = request.DetectorImage is { } detector
                ? detector with { Image = Clone(detector.Image) }
                : null,
        }, cancellationToken));

        void Add(ReadOnlyMemory<byte> plane)
        {
            if (distinct.Add(plane))
            {
                byteCount = checked(byteCount + plane.Length);
            }
        }
    }
}
