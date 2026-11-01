// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Ocr;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record TraceCropObservation(
    string RegionId,
    OcrSourceImage SourceImage,
    int Width,
    int Height,
    string CropSha256,
    OcrPolygon OriginalPolygon,
    IReadOnlyList<OcrRecognitionAlternative>? Alternatives,
    OcrFailure? Failure);

/// <summary>Records crop identities and raw output while forwarding the exact recognizer result.</summary>
internal sealed class TraceTextRecognizer(ITextRecognizer inner, List<TraceCropObservation> observations) : ITextRecognizer
{
    public string ModelId => inner.ModelId;
    public string ModelVersion => inner.ModelVersion;
    public string ModelSha256 => inner.ModelSha256;
    public string ConfigurationFingerprint => inner.ConfigurationFingerprint;

    public async ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(
        IReadOnlyList<OcrCrop> crops, CancellationToken cancellationToken)
    {
        IReadOnlyList<OcrRecognition> result = await inner.RecognizeBatchAsync(crops, cancellationToken).ConfigureAwait(false);
        foreach (OcrCrop crop in crops)
        {
            OcrRecognition? reading = result.FirstOrDefault(item => item.RegionId == crop.RegionId && item.SourceImage == crop.SourceImage);
            observations.Add(new TraceCropObservation(crop.RegionId, crop.SourceImage, crop.Width, crop.Height,
                crop.CropSha256, crop.OriginalPolygon, reading?.Alternatives, reading?.Failure));
        }
        return result;
    }
}

internal static partial class Program
{
    private static async Task<int> TraceRecognizerSelfTest()
    {
        var expected = new[] { new OcrRecognition("sample", OcrSourceImage.Original,
            [new OcrRecognitionAlternative("1", 0.9, OcrSourceImage.Original)], 0) };
        var source = new TraceSelfTestRecognizer(expected);
        var observations = new List<TraceCropObservation>();
        var traced = new TraceTextRecognizer(source, observations);
        var crop = new OcrCrop("sample", OcrSourceImage.Original, 1, 1, new float[1],
            new string('a', 64), OcrPolygon.FromRectangle(new OcrRectangle(0, 0, 1, 1)));
        IReadOnlyList<OcrRecognition> actual = await traced.RecognizeBatchAsync([crop], CancellationToken.None);
        Require(ReferenceEquals(expected, actual) && traced.ModelId == source.ModelId &&
            traced.ModelVersion == source.ModelVersion && traced.ModelSha256 == source.ModelSha256 &&
            traced.ConfigurationFingerprint == source.ConfigurationFingerprint && observations.Count == 1 &&
            observations[0].CropSha256 == crop.CropSha256 && ReferenceEquals(observations[0].Alternatives, expected[0].Alternatives),
            "Trace recognizer did not transparently forward its input and result.");
        Console.WriteLine("Trace recognizer self-test passed.");
        return 0;
    }

    private sealed class TraceSelfTestRecognizer(IReadOnlyList<OcrRecognition> result) : ITextRecognizer
    {
        public string ModelId => "trace-test";
        public string ModelVersion => "1";
        public string ModelSha256 => new('b', 64);
        public string ConfigurationFingerprint => "trace-test-config";

        public ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(
            IReadOnlyList<OcrCrop> crops, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
