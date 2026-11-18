// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Ocr;

/// <summary>
/// Applies the frozen V8 source-byte spacing and ambiguity rules to an exact
/// official PP-OCR recognizer result. The wrapped recognizer owns the original
/// logits and confidence; this decorator may alter only the returned string.
/// </summary>
public sealed class OcrV8OfficialTextRecognizer : ITextRecognizer
{
    private readonly ITextRecognizer officialRecognizer;
    private readonly OcrV8SourcePostprocessor sourcePostprocessor;

    public OcrV8OfficialTextRecognizer(
        ITextRecognizer officialRecognizer,
        OcrV8SourcePostprocessor sourcePostprocessor)
    {
        this.officialRecognizer = officialRecognizer ??
            throw new ArgumentNullException(nameof(officialRecognizer));
        this.sourcePostprocessor = sourcePostprocessor ??
            throw new ArgumentNullException(nameof(sourcePostprocessor));
    }

    public string ModelId => officialRecognizer.ModelId;

    public string ModelVersion => officialRecognizer.ModelVersion;

    public string ModelSha256 => officialRecognizer.ModelSha256;

    public string ConfigurationFingerprint => HashStrings(
    [
        OcrV8SourcePostprocessor.CompositionRevision,
        officialRecognizer.ModelId,
        officialRecognizer.ModelVersion,
        officialRecognizer.ModelSha256,
        officialRecognizer.ConfigurationFingerprint,
        sourcePostprocessor.ModelId,
        sourcePostprocessor.ModelVersion,
        sourcePostprocessor.ModelSha256,
        sourcePostprocessor.ConfigurationFingerprint,
    ]);

    public async ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(
        IReadOnlyList<OcrCrop> crops,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crops);
        cancellationToken.ThrowIfCancellationRequested();
        if (crops.Any(static crop => crop.SourceImage == OcrSourceImage.Original && crop.SourceCrop is null))
        {
            throw new ArgumentException(
                "V8 official recognition requires checksum-bound raw source crops for every original-image crop.",
                nameof(crops));
        }

        IReadOnlyList<OcrRecognition> raw = await officialRecognizer
            .RecognizeBatchAsync(crops, cancellationToken)
            .ConfigureAwait(false);
        if (raw.Count != crops.Count)
        {
            throw new InvalidDataException(
                $"Official recognizer returned {raw.Count} results for {crops.Count} V8 crops.");
        }

        var processed = new OcrRecognition[raw.Count];
        for (var index = 0; index < raw.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRecognition recognition = raw[index];
            OcrCrop crop = crops[index];
            if (recognition.Failure is not null || crop.SourceCrop is null || recognition.Alternatives.Count == 0)
            {
                processed[index] = recognition;
                continue;
            }

            var alternatives = new List<OcrRecognitionAlternative>(recognition.Alternatives.Count);
            double ambiguityMilliseconds = 0;
            foreach (OcrRecognitionAlternative alternative in recognition.Alternatives)
            {
                string conservative = OcrV8SourcePostprocessor.RestoreConservativeSourceSpaces(
                    crop.SourceCrop,
                    alternative.Text);
                if (OcrV8SourcePostprocessor.GraphNumber().IsMatch(alternative.Text.Trim()) &&
                    !OcrV8SourcePostprocessor.GraphNumber().IsMatch(conservative.Trim()))
                {
                    conservative = alternative.Text;
                }

                OcrV8AmbiguityResult ambiguity = await sourcePostprocessor
                    .ResolveAmbiguityAsync(crop.SourceCrop, conservative, cancellationToken)
                    .ConfigureAwait(false);
                ambiguityMilliseconds += ambiguity.InferenceMilliseconds;
                alternatives.Add(alternative with { Text = ambiguity.Text });
            }

            processed[index] = recognition with
            {
                Alternatives = OcrCollections.Freeze(alternatives),
                SingleCtcRead = null,
                InferenceMilliseconds = recognition.InferenceMilliseconds + ambiguityMilliseconds,
            };
        }

        return Array.AsReadOnly(processed);
    }

    private static string HashStrings(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
