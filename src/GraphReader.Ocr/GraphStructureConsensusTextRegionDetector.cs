// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

public enum GraphStructureConsensusGeometry
{
    ModelPolygon = 0,
    MatchedComponent = 1,
    InitialDbContour = 2,
}

public enum GraphStructureModelInput
{
    AxisMasked = 0,
    Original = 1,
}

public enum GraphStructureConsensusAdmission
{
    Required = 0,
    Advisory = 1,
}

public sealed record GraphStructureConsensusTextRegionDetectorOptions
{
    public double MinimumOverlapCoefficient { get; init; } = 0.50;

    public double MinimumTextLikelihood { get; init; } = 0.45;

    public GraphStructureConsensusGeometry OutputGeometry { get; init; } =
        GraphStructureConsensusGeometry.ModelPolygon;

    public GraphStructureModelInput ModelInput { get; init; } =
        GraphStructureModelInput.AxisMasked;

    public GraphStructureConsensusAdmission Admission { get; init; } =
        GraphStructureConsensusAdmission.Required;
}

/// <summary>
/// Combines model detections with independently derived connected-component
/// evidence. Required admission keeps at most one model detection for each
/// eligible text candidate. Advisory admission retains every accepted atomic
/// DB contour and records any association as evidence only.
/// </summary>
public sealed class GraphStructureConsensusTextRegionDetector : IDualInputTextRegionDetector
{
    public const string CompositionVersion = "graph-structure-consensus-v1";

    public const string MatchedComponentCompositionVersion =
        "graph-structure-consensus-component-geometry-v1";

    public const string OriginalModelInputCompositionVersion =
        "graph-structure-consensus-original-model-input-v1";

    public const string MatchedComponentOriginalModelInputCompositionVersion =
        "graph-structure-consensus-component-geometry-original-model-input-v1";

    public const string InitialDbContourOriginalModelInputCompositionVersion =
        "graph-structure-consensus-initial-db-contour-original-model-input-v1";

    public const string AdvisoryInitialDbContourOriginalModelInputCompositionVersion =
        "graph-structure-consensus-advisory-initial-db-contour-original-model-input-v1";

    private readonly ITextRegionDetector modelDetector;
    private readonly ITextRegionDetector structureCandidateDetector;
    private readonly GraphStructureConsensusTextRegionDetectorOptions options;

    public GraphStructureConsensusTextRegionDetector(
        ITextRegionDetector modelDetector,
        ITextRegionDetector structureCandidateDetector,
        GraphStructureConsensusTextRegionDetectorOptions? options = null)
    {
        this.modelDetector = modelDetector ?? throw new ArgumentNullException(nameof(modelDetector));
        this.structureCandidateDetector = structureCandidateDetector ??
            throw new ArgumentNullException(nameof(structureCandidateDetector));
        this.options = options ?? new GraphStructureConsensusTextRegionDetectorOptions();
        if (!double.IsFinite(this.options.MinimumOverlapCoefficient) ||
            this.options.MinimumOverlapCoefficient is <= 0 or > 1 ||
            !double.IsFinite(this.options.MinimumTextLikelihood) ||
            this.options.MinimumTextLikelihood is < 0 or > 1 ||
            !Enum.IsDefined(this.options.OutputGeometry) ||
            !Enum.IsDefined(this.options.ModelInput) ||
            !Enum.IsDefined(this.options.Admission))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (this.options.OutputGeometry == GraphStructureConsensusGeometry.InitialDbContour &&
            (this.options.ModelInput != GraphStructureModelInput.Original ||
             modelDetector is not IAtomicDbGeometryTextRegionDetector { SupportsAtomicDbGeometry: true }))
        {
            throw new ArgumentException(
                "Initial DB contour geometry requires an atomic DB detector with original model input.",
                nameof(options));
        }

        if (this.options.Admission == GraphStructureConsensusAdmission.Advisory &&
            (this.options.OutputGeometry != GraphStructureConsensusGeometry.InitialDbContour ||
             this.options.ModelInput != GraphStructureModelInput.Original ||
             modelDetector is not IAtomicDbGeometryTextRegionDetector { SupportsAtomicDbGeometry: true }))
        {
            throw new ArgumentException(
                "Advisory structure admission requires atomic initial DB contour geometry with original model input.",
                nameof(options));
        }
    }

    public string ConfigurationFingerprint => string.Create(
        CultureInfo.InvariantCulture,
        $"{GetCompositionVersion(options.OutputGeometry, options.ModelInput, options.Admission)}:{options.MinimumOverlapCoefficient:R}:{options.MinimumTextLikelihood:R}:model={modelDetector.ConfigurationFingerprint}:candidate={structureCandidateDetector.ConfigurationFingerprint}");

    public static string GetCompositionVersion(GraphStructureConsensusGeometry geometry) => geometry switch
    {
        GraphStructureConsensusGeometry.ModelPolygon => CompositionVersion,
        GraphStructureConsensusGeometry.MatchedComponent => MatchedComponentCompositionVersion,
        GraphStructureConsensusGeometry.InitialDbContour =>
            throw new ArgumentOutOfRangeException(
                nameof(geometry),
                "Initial DB contour geometry requires explicit original model input."),
        _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
    };

    public static string GetCompositionVersion(
        GraphStructureConsensusGeometry geometry,
        GraphStructureModelInput modelInput) => (geometry, modelInput) switch
    {
        (GraphStructureConsensusGeometry.ModelPolygon, GraphStructureModelInput.AxisMasked) =>
            CompositionVersion,
        (GraphStructureConsensusGeometry.MatchedComponent, GraphStructureModelInput.AxisMasked) =>
            MatchedComponentCompositionVersion,
        (GraphStructureConsensusGeometry.ModelPolygon, GraphStructureModelInput.Original) =>
            OriginalModelInputCompositionVersion,
        (GraphStructureConsensusGeometry.MatchedComponent, GraphStructureModelInput.Original) =>
            MatchedComponentOriginalModelInputCompositionVersion,
        (GraphStructureConsensusGeometry.InitialDbContour, GraphStructureModelInput.Original) =>
            InitialDbContourOriginalModelInputCompositionVersion,
        (_, var invalidInput) when !Enum.IsDefined(invalidInput) =>
            throw new ArgumentOutOfRangeException(nameof(modelInput)),
        _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
    };

    public static string GetCompositionVersion(
        GraphStructureConsensusGeometry geometry,
        GraphStructureModelInput modelInput,
        GraphStructureConsensusAdmission admission)
    {
        if (!Enum.IsDefined(admission))
        {
            throw new ArgumentOutOfRangeException(nameof(admission));
        }

        if (admission == GraphStructureConsensusAdmission.Required)
        {
            return GetCompositionVersion(geometry, modelInput);
        }

        return (geometry, modelInput) switch
        {
            (GraphStructureConsensusGeometry.InitialDbContour, GraphStructureModelInput.Original) =>
                AdvisoryInitialDbContourOriginalModelInputCompositionVersion,
            (_, var invalidInput) when !Enum.IsDefined(invalidInput) =>
                throw new ArgumentOutOfRangeException(nameof(modelInput)),
            (var invalidGeometry, _) when !Enum.IsDefined(invalidGeometry) =>
                throw new ArgumentOutOfRangeException(nameof(geometry)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(admission),
                "Advisory admission supports only initial DB contour geometry with original model input."),
        };
    }

    public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.ModelInput == GraphStructureModelInput.Original)
        {
            throw new InvalidOperationException(
                "Original model input requires both immutable original and detector images.");
        }

        return await DetectCoreAsync(image, image, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(
        OcrImage originalImage,
        OcrImage detectorImage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalImage);
        ArgumentNullException.ThrowIfNull(detectorImage);
        cancellationToken.ThrowIfCancellationRequested();

        OcrImage modelImage = options.ModelInput switch
        {
            GraphStructureModelInput.AxisMasked => detectorImage,
            GraphStructureModelInput.Original => ValidateAndSelectOriginal(
                originalImage,
                detectorImage),
            _ => throw new InvalidOperationException("Unsupported consensus model input."),
        };
        return await DetectCoreAsync(modelImage, detectorImage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectCoreAsync(
        OcrImage modelImage,
        OcrImage structureImage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OcrAtomicDbDetection? atomicDbDetection = null;
        IReadOnlyList<OcrDetectedRegion> modelRegions;
        if (options.OutputGeometry == GraphStructureConsensusGeometry.InitialDbContour)
        {
            var atomicDetector = modelDetector as IAtomicDbGeometryTextRegionDetector ??
                throw new InvalidOperationException(
                    "Initial DB contour geometry requires an atomic DB detector.");
            if (!atomicDetector.SupportsAtomicDbGeometry)
            {
                throw new InvalidOperationException(
                    "The model detector does not support atomic DB contour geometry.");
            }

            atomicDbDetection = await atomicDetector
                .DetectWithAtomicDbGeometryAsync(modelImage, cancellationToken)
                .ConfigureAwait(false);
            modelRegions = atomicDbDetection.Regions;
            ValidateAtomicDbDetection(atomicDbDetection, modelImage);
        }
        else
        {
            modelRegions = await modelDetector
                .DetectAsync(modelImage, cancellationToken)
                .ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (modelRegions.Count == 0)
        {
            return Array.Empty<OcrDetectedRegion>();
        }

        IReadOnlyList<OcrDetectedRegion> candidateRegions = await structureCandidateDetector
            .DetectAsync(structureImage, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRegions(modelRegions, requireEvidence: false, "model");
        ValidateRegions(candidateRegions, requireEvidence: true, "structure candidate");

        IndexedRegion[] eligibleCandidates = candidateRegions
            .Select(static (region, index) => new IndexedRegion(index, region))
            .Where(item =>
                item.Region.Evidence is { LikelyGraphStructure: false } evidence &&
                evidence.TextLikelihood >= options.MinimumTextLikelihood)
            .ToArray();
        Match[] matches = modelRegions
            .SelectMany((modelRegion, modelIndex) => eligibleCandidates.Select(candidate =>
                new Match(
                    modelIndex,
                    candidate.Index,
                    OverlapCoefficient(modelRegion.Polygon.Bounds, candidate.Region.Polygon.Bounds))))
            .Where(match => match.OverlapCoefficient >= options.MinimumOverlapCoefficient)
            .OrderByDescending(match => modelRegions[match.ModelIndex].DetectionConfidence)
            .ThenByDescending(static match => match.OverlapCoefficient)
            .ThenByDescending(match =>
                candidateRegions[match.CandidateIndex].Evidence!.TextLikelihood)
            .ThenBy(match => modelRegions[match.ModelIndex].RegionId, StringComparer.Ordinal)
            .ThenBy(match => candidateRegions[match.CandidateIndex].RegionId, StringComparer.Ordinal)
            .ToArray();

        if (options.Admission == GraphStructureConsensusAdmission.Advisory)
        {
            return BuildAdvisoryOutput(
                modelRegions,
                candidateRegions,
                matches,
                atomicDbDetection,
                cancellationToken);
        }

        var usedModels = new HashSet<int>();
        var usedCandidates = new HashSet<int>();
        var output = new List<OcrDetectedRegion>();
        foreach (Match match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (usedModels.Contains(match.ModelIndex) || usedCandidates.Contains(match.CandidateIndex))
            {
                continue;
            }

            usedModels.Add(match.ModelIndex);
            usedCandidates.Add(match.CandidateIndex);
            OcrDetectedRegion model = modelRegions[match.ModelIndex];
            OcrDetectedRegion candidate = candidateRegions[match.CandidateIndex];
            output.Add(options.OutputGeometry switch
            {
                GraphStructureConsensusGeometry.ModelPolygon => model with
                {
                    OrientationDegrees = Math.Abs(model.OrientationDegrees) <= double.Epsilon
                        ? candidate.OrientationDegrees
                        : model.OrientationDegrees,
                    Context = model.Context ?? candidate.Context,
                    Evidence = candidate.Evidence,
                },
                GraphStructureConsensusGeometry.MatchedComponent => model with
                {
                    RegionId = MatchedComponentRegionId(model, candidate),
                    Polygon = candidate.Polygon,
                    OrientationDegrees = candidate.OrientationDegrees,
                    Context = model.Context ?? candidate.Context,
                    Evidence = MatchedComponentEvidence(model, candidate),
                },
                GraphStructureConsensusGeometry.InitialDbContour => model with
                {
                    RegionId = InitialDbContourRegionId(
                        model,
                        candidate,
                        InitialPolygon(atomicDbDetection, model.RegionId)),
                    Polygon = InitialPolygon(atomicDbDetection, model.RegionId),
                    OrientationDegrees = Math.Abs(model.OrientationDegrees) <= double.Epsilon
                        ? candidate.OrientationDegrees
                        : model.OrientationDegrees,
                    Context = model.Context ?? candidate.Context,
                    Evidence = InitialDbContourEvidence(model, candidate),
                },
                _ => throw new InvalidOperationException("Unsupported consensus output geometry."),
            });
        }

        return Array.AsReadOnly(output
            .OrderBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal)
            .ToArray());
    }

    private static ReadOnlyCollection<OcrDetectedRegion> BuildAdvisoryOutput(
        IReadOnlyList<OcrDetectedRegion> modelRegions,
        IReadOnlyList<OcrDetectedRegion> candidateRegions,
        IReadOnlyList<Match> matches,
        OcrAtomicDbDetection? atomicDbDetection,
        CancellationToken cancellationToken)
    {
        var candidateByModel = new Dictionary<int, int>();
        var usedCandidates = new HashSet<int>();
        foreach (Match match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidateByModel.ContainsKey(match.ModelIndex) ||
                !usedCandidates.Add(match.CandidateIndex))
            {
                continue;
            }

            candidateByModel.Add(match.ModelIndex, match.CandidateIndex);
        }

        var output = new OcrDetectedRegion[modelRegions.Count];
        for (var modelIndex = 0; modelIndex < modelRegions.Count; modelIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrDetectedRegion model = modelRegions[modelIndex];
            OcrPolygon initialPolygon = InitialPolygon(atomicDbDetection, model.RegionId);
            OcrDetectedRegion? candidate = candidateByModel.TryGetValue(modelIndex, out int candidateIndex)
                ? candidateRegions[candidateIndex]
                : null;
            output[modelIndex] = model with
            {
                RegionId = AdvisoryInitialDbContourRegionId(model, initialPolygon),
                Polygon = initialPolygon,
                Evidence = AdvisoryInitialDbContourEvidence(model, candidate),
            };
        }

        return Array.AsReadOnly(output
            .OrderBy(static region => region.Polygon.Bounds.Top)
            .ThenBy(static region => region.Polygon.Bounds.Left)
            .ThenBy(static region => region.RegionId, StringComparer.Ordinal)
            .ToArray());
    }

    private static OcrImage ValidateAndSelectOriginal(
        OcrImage originalImage,
        OcrImage detectorImage)
    {
        ValidateAlignedInputs(originalImage, detectorImage);
        return originalImage;
    }

    private static void ValidateAlignedInputs(OcrImage originalImage, OcrImage detectorImage)
    {
        if (!HasValidLayout(originalImage) ||
            !HasValidLayout(detectorImage) ||
            originalImage.SourceImage != OcrSourceImage.Original ||
            detectorImage.SourceImage != OcrSourceImage.Original ||
            !originalImage.OriginalToImage.IsInvertible ||
            !detectorImage.OriginalToImage.IsInvertible ||
            !string.Equals(
                originalImage.CoordinateSpace,
                OcrContract.CoordinateSpace,
                StringComparison.Ordinal) ||
            !string.Equals(
                detectorImage.CoordinateSpace,
                OcrContract.CoordinateSpace,
                StringComparison.Ordinal) ||
            !HasValidCanonicalDimensions(originalImage) ||
            !HasValidCanonicalDimensions(detectorImage) ||
            originalImage.Width != detectorImage.Width ||
            originalImage.Height != detectorImage.Height ||
            originalImage.Stride != detectorImage.Stride ||
            originalImage.OriginalToImage != detectorImage.OriginalToImage ||
            !string.Equals(
                originalImage.CoordinateSpace,
                detectorImage.CoordinateSpace,
                StringComparison.Ordinal) ||
            originalImage.CanonicalOriginalWidth != detectorImage.CanonicalOriginalWidth ||
            originalImage.CanonicalOriginalHeight != detectorImage.CanonicalOriginalHeight ||
            (originalImage.BgrPixels is null) != (detectorImage.BgrPixels is null))
        {
            throw new InvalidDataException(
                "Original and detector OCR images must have aligned dimensions, transforms, and provenance.");
        }
    }

    private static bool HasValidCanonicalDimensions(OcrImage image) =>
        (image.CanonicalOriginalWidth is null && image.CanonicalOriginalHeight is null) ||
        (image.CanonicalOriginalWidth > 0 && image.CanonicalOriginalHeight > 0);

    private static bool HasValidLayout(OcrImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width ||
            image.Pixels.Length != checked(image.Stride * image.Height))
        {
            return false;
        }

        if (image.BgrPixels is not { } bgr)
        {
            return true;
        }

        return bgr.Stride >= checked(image.Width * 3) &&
            bgr.Pixels.Length == checked(bgr.Stride * image.Height);
    }

    private static void ValidateRegions(
        IReadOnlyList<OcrDetectedRegion> regions,
        bool requireEvidence,
        string label)
    {
        ArgumentNullException.ThrowIfNull(regions);
        foreach (OcrDetectedRegion region in regions)
        {
            OcrRegionEvidence? evidence = region.Evidence;
            bool invalidEvidence = evidence is not null &&
                (evidence.ComponentCount < 0 ||
                 !double.IsFinite(evidence.InkDensity) || evidence.InkDensity is < 0 or > 1 ||
                 !double.IsFinite(evidence.TextLikelihood) || evidence.TextLikelihood is < 0 or > 1 ||
                 !double.IsFinite(evidence.StructureLikelihood) || evidence.StructureLikelihood is < 0 or > 1);
            if (string.IsNullOrWhiteSpace(region.RegionId) ||
                !region.Polygon.Bounds.IsValid ||
                !double.IsFinite(region.OrientationDegrees) ||
                !double.IsFinite(region.DetectionConfidence) ||
                region.DetectionConfidence is < 0 or > 1 ||
                !string.Equals(region.CoordinateSpace, OcrContract.CoordinateSpace, StringComparison.Ordinal) ||
                (requireEvidence && evidence is null) ||
                invalidEvidence)
            {
                throw new InvalidDataException($"The {label} detector returned invalid evidence.");
            }
        }
    }

    private static double OverlapCoefficient(OcrRectangle left, OcrRectangle right)
    {
        double intersectionWidth = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        double intersectionHeight = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        double intersection = intersectionWidth * intersectionHeight;
        double denominator = Math.Min(left.Width * left.Height, right.Width * right.Height);
        return denominator <= 0 ? 0 : intersection / denominator;
    }

    private static void ValidateAtomicDbDetection(
        OcrAtomicDbDetection detection,
        OcrImage modelImage)
    {
        ArgumentNullException.ThrowIfNull(detection);
        OcrDbGeometryObservation geometry = detection.Geometry;
        string grayInputSha256 = Convert.ToHexStringLower(
            SHA256.HashData(modelImage.Pixels.Span));
        string? bgrInputSha256 = modelImage.BgrPixels is { } bgr
            ? Convert.ToHexStringLower(SHA256.HashData(bgr.Pixels.Span))
            : null;
        if (geometry.ImageWidth != modelImage.Width ||
            geometry.ImageHeight != modelImage.Height ||
            geometry.TensorWidth <= 0 ||
            geometry.TensorHeight <= 0 ||
            geometry.InputSha256.Length != 64 ||
            geometry.InputSha256.Any(static character => !Uri.IsHexDigit(character)) ||
            (!string.Equals(geometry.InputSha256, grayInputSha256, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(geometry.InputSha256, bgrInputSha256, StringComparison.OrdinalIgnoreCase)) ||
            geometry.AcceptedContours.Count != detection.Regions.Count)
        {
            throw new InvalidDataException("Atomic DB contour geometry does not match its model input.");
        }

        ValidateRegions(detection.Regions, requireEvidence: true, "atomic DB model");
        var modelById = new Dictionary<string, OcrDetectedRegion>(StringComparer.Ordinal);
        foreach (OcrDetectedRegion region in detection.Regions)
        {
            if (!modelById.TryAdd(region.RegionId, region))
            {
                throw new InvalidDataException("Atomic DB model region identities must be unique.");
            }
        }

        var contourById = new Dictionary<string, OcrDbAcceptedContourGeometry>(StringComparer.Ordinal);
        foreach (OcrDbAcceptedContourGeometry contour in geometry.AcceptedContours)
        {
            if (string.IsNullOrWhiteSpace(contour.ReturnedRegionId) ||
                !contourById.TryAdd(contour.ReturnedRegionId, contour) ||
                !modelById.TryGetValue(contour.ReturnedRegionId, out OcrDetectedRegion? region) ||
                !IsValidDbPolygon(contour.InitialPolygon) ||
                !IsValidDbPolygon(contour.ExpandedPolygon) ||
                !PolygonsMatchExactly(contour.ExpandedPolygon, region.Polygon) ||
                !double.IsFinite(contour.DetectionConfidence) ||
                contour.DetectionConfidence is < 0 or > 1 ||
                contour.DetectionConfidence != region.DetectionConfidence ||
                !double.IsFinite(contour.InkDensity) ||
                contour.InkDensity is < 0 or > 1 ||
                region.Evidence is not { } evidence ||
                contour.InkDensity != evidence.InkDensity)
            {
                throw new InvalidDataException("Atomic DB contour mapping is invalid or inconsistent.");
            }
        }

        if (contourById.Count != modelById.Count)
        {
            throw new InvalidDataException("Atomic DB contour mapping is incomplete.");
        }
    }

    private static bool IsValidDbPolygon(OcrPolygon polygon)
    {
        if (polygon is null || polygon.Points.Count != 4 || !polygon.Bounds.IsValid)
        {
            return false;
        }

        double twiceArea = 0;
        for (var index = 0; index < polygon.Points.Count; index++)
        {
            OcrPoint current = polygon.Points[index];
            OcrPoint next = polygon.Points[(index + 1) % polygon.Points.Count];
            twiceArea += (current.X * next.Y) - (next.X * current.Y);
        }

        return double.IsFinite(twiceArea) && Math.Abs(twiceArea) > double.Epsilon;
    }

    private static bool PolygonsMatchExactly(OcrPolygon left, OcrPolygon right)
    {
        if (left.Points.Count != right.Points.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Points.Count; index++)
        {
            if (BitConverter.DoubleToInt64Bits(left.Points[index].X) !=
                    BitConverter.DoubleToInt64Bits(right.Points[index].X) ||
                BitConverter.DoubleToInt64Bits(left.Points[index].Y) !=
                    BitConverter.DoubleToInt64Bits(right.Points[index].Y))
            {
                return false;
            }
        }

        return true;
    }

    private static OcrPolygon InitialPolygon(OcrAtomicDbDetection? detection, string regionId)
    {
        OcrDbAcceptedContourGeometry contour = detection?.Geometry.AcceptedContours
            .SingleOrDefault(item => string.Equals(item.ReturnedRegionId, regionId, StringComparison.Ordinal)) ??
            throw new InvalidDataException("The selected model region has no initial DB contour mapping.");
        return contour.InitialPolygon;
    }

    private static string MatchedComponentRegionId(
        OcrDetectedRegion model,
        OcrDetectedRegion candidate)
    {
        using var material = new MemoryStream();
        using (var writer = new BinaryWriter(material, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(MatchedComponentCompositionVersion);
            writer.Write(model.RegionId);
            writer.Write(candidate.RegionId);
            writer.Write(candidate.Polygon.Points.Count);
            foreach (OcrPoint point in candidate.Polygon.Points)
            {
                writer.Write(BitConverter.DoubleToInt64Bits(point.X));
                writer.Write(BitConverter.DoubleToInt64Bits(point.Y));
            }
        }

        byte[] hash = SHA256.HashData(material.ToArray());
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string InitialDbContourRegionId(
        OcrDetectedRegion model,
        OcrDetectedRegion candidate,
        OcrPolygon initialPolygon)
    {
        using var material = new MemoryStream();
        using (var writer = new BinaryWriter(material, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(InitialDbContourOriginalModelInputCompositionVersion);
            writer.Write(model.RegionId);
            writer.Write(candidate.RegionId);
            writer.Write(initialPolygon.Points.Count);
            foreach (OcrPoint point in initialPolygon.Points)
            {
                writer.Write(BitConverter.DoubleToInt64Bits(point.X));
                writer.Write(BitConverter.DoubleToInt64Bits(point.Y));
            }
        }

        byte[] hash = SHA256.HashData(material.ToArray());
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string AdvisoryInitialDbContourRegionId(
        OcrDetectedRegion model,
        OcrPolygon initialPolygon)
    {
        using var material = new MemoryStream();
        using (var writer = new BinaryWriter(material, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(AdvisoryInitialDbContourOriginalModelInputCompositionVersion);
            writer.Write(model.RegionId);
            writer.Write(initialPolygon.Points.Count);
            foreach (OcrPoint point in initialPolygon.Points)
            {
                writer.Write(BitConverter.DoubleToInt64Bits(point.X));
                writer.Write(BitConverter.DoubleToInt64Bits(point.Y));
            }
        }

        byte[] hash = SHA256.HashData(material.ToArray());
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static OcrRegionEvidence MatchedComponentEvidence(
        OcrDetectedRegion model,
        OcrDetectedRegion candidate)
    {
        OcrRegionEvidence evidence = candidate.Evidence ??
            throw new InvalidOperationException("Matched component evidence is missing.");
        return evidence with
        {
            Reasons = Array.AsReadOnly(evidence.Reasons
                .Concat([
                    $"consensus_model_region_id:{model.RegionId}",
                    $"consensus_component_region_id:{candidate.RegionId}",
                ])
                .ToArray()),
        };
    }

    private static OcrRegionEvidence InitialDbContourEvidence(
        OcrDetectedRegion model,
        OcrDetectedRegion candidate)
    {
        OcrRegionEvidence evidence = candidate.Evidence ??
            throw new InvalidOperationException("Initial DB contour evidence is missing.");
        return evidence with
        {
            Reasons = Array.AsReadOnly(evidence.Reasons
                .Concat([
                    "consensus_geometry:initial_db_contour",
                    $"consensus_model_region_id:{model.RegionId}",
                    $"consensus_component_region_id:{candidate.RegionId}",
                ])
                .ToArray()),
        };
    }

    private static OcrRegionEvidence AdvisoryInitialDbContourEvidence(
        OcrDetectedRegion model,
        OcrDetectedRegion? candidate)
    {
        OcrRegionEvidence evidence = model.Evidence ??
            throw new InvalidOperationException("Atomic DB model evidence is missing.");
        string[] association = candidate is null
            ? []
            : [$"consensus_component_region_id:{candidate.RegionId}"];
        return evidence with
        {
            Reasons = Array.AsReadOnly(evidence.Reasons
                .Concat([
                    "consensus_admission:advisory",
                    "consensus_geometry:initial_db_contour",
                    $"consensus_model_region_id:{model.RegionId}",
                ])
                .Concat(association)
                .ToArray()),
        };
    }

    private readonly record struct IndexedRegion(int Index, OcrDetectedRegion Region);

    private readonly record struct Match(
        int ModelIndex,
        int CandidateIndex,
        double OverlapCoefficient);
}
