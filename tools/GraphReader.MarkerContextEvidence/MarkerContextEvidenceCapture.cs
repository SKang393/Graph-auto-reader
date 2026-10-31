// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;

namespace GraphReader.MarkerContextEvidence;

/// <summary>
/// Captures the exact production classifier patch and its source-coordinate mapping.
/// This utility has no inference, label, optimizer, or dataset access.
/// </summary>
public static class MarkerContextEvidenceCapture
{
    public const string Schema = "graphreader.marker-context-crop-evidence.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static MarkerContextEvidence Capture(
        string caseId,
        MarkerImageFrame frame,
        MarkerCenter marker,
        MarkerPatchExtractionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(options);
        if (caseId.Length > 80 || caseId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Synthetic case IDs must be short ASCII identifiers.", nameof(caseId));
        }

        // Copy every source plane and mask before sampling so evidence binds one immutable snapshot.
        float[] sourcePlanes = frame.ChannelsFirstPixels.ToArray();
        float[] ocrMask = frame.OcrMask.Values.ToArray();
        float[] artifactMask = frame.ArtifactMask.Values.ToArray();
        var frozenFrame = frame with
        {
            ChannelsFirstPixels = sourcePlanes,
            OcrMask = frame.OcrMask with { Values = ocrMask },
            ArtifactMask = frame.ArtifactMask with { Values = artifactMask },
        };

        MarkerPatch patch = new MarkerPatchExtractor()
            .Extract(frozenFrame, [marker], options, CancellationToken.None)
            .Single();

        var transform = frame.OriginalToFrame;
        MarkerPoint frameCenter = transform.MapFromOriginal(marker.Center);
        double scaleX = Math.Sqrt((transform.M11 * transform.M11) + (transform.M21 * transform.M21));
        double scaleY = Math.Sqrt((transform.M12 * transform.M12) + (transform.M22 * transform.M22));
        double frameRadius = marker.Radius * Math.Sqrt(scaleX * scaleY);
        double halfExtent = Math.Max(options.MinimumHalfExtentFramePixels, frameRadius * options.RadiusScale);
        if (!frameCenter.IsFinite || !double.IsFinite(halfExtent))
        {
            throw new ArgumentException("Synthetic marker mapping is not finite.", nameof(marker));
        }

        double patchScaleX = 2 * halfExtent / options.Width;
        double patchScaleY = 2 * halfExtent / options.Height;
        double patchOffsetX = frameCenter.X - halfExtent + (halfExtent / options.Width);
        double patchOffsetY = frameCenter.Y - halfExtent + (halfExtent / options.Height);
        double[] patchToFrame = [patchScaleX, 0, patchOffsetX, 0, patchScaleY, patchOffsetY, 0, 0, 1];
        double[] frameToOriginal = InvertAffine(transform);
        double[] patchToOriginal = Compose(frameToOriginal, patchToFrame);

        var input = new MarkerContextEvidenceInput(
            Schema,
            "synthetic_fixture",
            caseId,
            frame.SourceImage.ToString(),
            frame.Width,
            frame.Height,
            frame.ChannelCount,
            sourcePlanes,
            ocrMask,
            artifactMask,
            new MarkerContextAffine(transform.M11, transform.M12, transform.M13, transform.M21, transform.M22, transform.M23),
            new MarkerContextPoint(marker.Center.X, marker.Center.Y),
            marker.Radius,
            marker.MarkerId,
            new MarkerContextOptions(options.Width, options.Height, options.ChannelCount,
                options.RadiusScale, options.MinimumHalfExtentFramePixels, options.PaddingValue,
                options.OriginalPixelContentBounds is { } bounds && bounds.TryGetValue(marker.MarkerId, out MarkerRectangle contentBounds)
                    ? new MarkerContextRectangle(contentBounds.X, contentBounds.Y, contentBounds.Width, contentBounds.Height)
                    : null));

        return new MarkerContextEvidence(
            input,
            [1, patch.ChannelCount, patch.Height, patch.Width],
            patch.ChannelsFirstPixels.ToArray(),
            [.. patchToFrame],
            [.. patchToOriginal]);
    }

    public static byte[] Serialize(MarkerContextEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions);
    }

    private static double[] InvertAffine(MarkerAffineTransform transform)
    {
        if (!transform.IsInvertible)
        {
            throw new ArgumentException("A finite invertible original-to-frame transform is required.", nameof(transform));
        }

        double determinant = transform.Determinant;
        return [
            transform.M22 / determinant,
            -transform.M12 / determinant,
            ((transform.M12 * transform.M23) - (transform.M22 * transform.M13)) / determinant,
            -transform.M21 / determinant,
            transform.M11 / determinant,
            ((transform.M21 * transform.M13) - (transform.M11 * transform.M23)) / determinant,
            0, 0, 1,
        ];
    }

    private static double[] Compose(double[] left, double[] right)
    {
        var result = new double[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                for (int inner = 0; inner < 3; inner++)
                {
                    result[(row * 3) + column] += left[(row * 3) + inner] * right[(inner * 3) + column];
                }
            }
        }
        return result;
    }
}

public sealed record MarkerContextEvidenceInput(
    string Schema,
    string Scope,
    string CaseId,
    string SourceImage,
    int Width,
    int Height,
    int ChannelCount,
    float[] ChannelsFirstPixels,
    float[] OcrMask,
    float[] ArtifactMask,
    MarkerContextAffine OriginalToFrame,
    MarkerContextPoint MarkerCenterOriginalPixels,
    double MarkerRadiusOriginalPixels,
    string MarkerId,
    MarkerContextOptions Options);

public sealed record MarkerContextEvidence(
    MarkerContextEvidenceInput Input,
    int[] OutputShapeNchw,
    float[] ProductionTensor,
    double[] PatchToFrameMatrix3x3,
    double[] PatchToOriginalMatrix3x3);

public sealed record MarkerContextAffine(double M11, double M12, double M13, double M21, double M22, double M23);
public sealed record MarkerContextPoint(double X, double Y);
public sealed record MarkerContextRectangle(double X, double Y, double Width, double Height);
public sealed record MarkerContextOptions(
    int Width,
    int Height,
    int OutputChannels,
    double RadiusScale,
    double MinimumHalfExtentFramePixels,
    float PaddingValue,
    MarkerContextRectangle? OriginalPixelContentBounds);
