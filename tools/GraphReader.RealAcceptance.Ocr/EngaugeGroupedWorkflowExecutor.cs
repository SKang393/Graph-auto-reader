// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed class EngaugeGroupedWorkflowImageInput
{
    private readonly byte[] imageBytes;

    internal EngaugeGroupedWorkflowImageInput(
        string caseKey,
        string sourceSha256,
        int width,
        int height,
        byte[] imageBytes)
    {
        CaseKey = caseKey;
        SourceSha256 = sourceSha256;
        Width = width;
        Height = height;
        this.imageBytes = (byte[])imageBytes.Clone();
    }

    internal string CaseKey { get; }
    internal string SourceSha256 { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal byte[] CopyImageBytes() => (byte[])imageBytes.Clone();
}

internal sealed record EngaugeGroupedWorkflowAggregateReport(
    string Schema,
    int ImageGroups,
    int DecodedImageGroups,
    int ProjectFiles,
    int WorkflowInvocations,
    int WorkflowSucceededImageGroups,
    int WorkflowFailedImageGroups,
    int WorkflowSucceededProjects,
    int WorkflowFailedProjects,
    int TruthSeries,
    int TruthPoints,
    int CoincidentCrossProjectPointPairs,
    IReadOnlyDictionary<string, int> ExecutionFailureKinds,
    WholeWorkflowEvaluationResult Evaluation,
    bool AggregateOnly,
    bool CaseLevelOutput,
    bool TruthRowsOutput,
    bool PredictionOutput,
    bool PathsOutput,
    bool NamesOutput);

internal static class EngaugeGroupedWorkflowExecutor
{
    // Matches the bound embedded-image limit enforced by
    // EngaugeDigWholeWorkflowTruthAdapter before grouping.
    private const long MaximumSourcePixels = 40_000_000;
    private const long MaximumDecodedImageBytes = 512L * 1024 * 1024;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal const string ReportSchema = "graphreader.engauge-grouped-workflow-aggregate.v1";
    internal const string ExecutionFailed = "WORKFLOW_EXECUTION_FAILED";
    internal const string ExportFailed = "WORKFLOW_EXPORT_FAILED";
    internal const string OutputMissing = "WORKFLOW_OUTPUT_MISSING";
    internal const string OutputIdentityMismatch = "WORKFLOW_OUTPUT_IDENTITY_MISMATCH";
    internal const string OutputInvalid = "WORKFLOW_OUTPUT_INVALID";

    private static readonly HashSet<string> SafeWorkflowFailureCodes = new(StringComparer.Ordinal)
    {
        ProductionWorkflowFailureCodes.ImageImportFailed,
        ProductionWorkflowFailureCodes.PdfImportUnavailable,
        ProductionWorkflowFailureCodes.PdfImportFailed,
        ProductionWorkflowFailureCodes.PdfPanelBytesUnavailable,
        ProductionWorkflowFailureCodes.DetectionModelsUnavailable,
        ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
        ProductionWorkflowFailureCodes.ReviewProjectionRejected,
        ProductionWorkflowFailureCodes.RecalibrationRequired,
        ExportFailed,
    };

    internal static async Task<EngaugeGroupedWorkflowAggregateReport> ExecuteAsync(
        IReadOnlyList<EngaugeWorkflowImageGroup> groups,
        WholeWorkflowEvaluationOptions evaluationOptions,
        Func<EngaugeGroupedWorkflowImageInput, CancellationToken, Task<WholeWorkflowCaseOutput?>> executeImageAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(evaluationOptions);
        ArgumentNullException.ThrowIfNull(executeImageAsync);
        cancellationToken.ThrowIfCancellationRequested();
        EngaugeWorkflowImageGroup[] ordered = ValidateInventory(groups);

        var outputs = new List<WholeWorkflowCaseOutput>(ordered.Length);
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        int decodedGroups = 0;
        int invocations = 0;
        int succeededGroups = 0;
        int failedGroups = 0;
        int succeededProjects = 0;
        int failedProjects = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EngaugeWorkflowImageGroup group = ordered[index];
            WholeWorkflowTruthCase truth = group.TruthCase;
            byte[] imageBytes = group.CopyImageBytes();
            if (!CanFullyDecodeImage(
                    imageBytes,
                    truth.SourceWidth,
                    truth.SourceHeight,
                    cancellationToken))
            {
                WholeWorkflowCaseOutput decodeFailure = Failure(
                    truth,
                    ProductionWorkflowFailureCodes.ImageImportFailed);
                outputs.Add(decodeFailure);
                failedGroups++;
                failedProjects += group.Projects.Count;
                Increment(failures, decodeFailure.FailureCode!);
                continue;
            }
            decodedGroups++;

            var input = new EngaugeGroupedWorkflowImageInput(
                truth.CaseKey,
                truth.SourceSha256,
                truth.SourceWidth,
                truth.SourceHeight,
                imageBytes);
            WholeWorkflowCaseOutput output;
            invocations++;
            try
            {
                WholeWorkflowCaseOutput? candidate = await executeImageAsync(input, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                output = NormalizeOutput(candidate, truth);
            }
            catch (ProductionWorkflowStageException exception)
            {
                output = Failure(truth, SanitizeFailureCode(exception.Failure.Code));
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                output = Failure(truth, ExecutionFailed);
            }

            outputs.Add(output);
            if (output.WorkflowSucceeded)
            {
                succeededGroups++;
                succeededProjects += group.Projects.Count;
            }
            else
            {
                failedGroups++;
                failedProjects += group.Projects.Count;
                Increment(failures, output.FailureCode ?? OutputInvalid);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        WholeWorkflowEvaluationResult evaluation = WholeWorkflowCsvEvaluator.Evaluate(
            ordered.Select(static group => group.TruthCase).ToArray(),
            outputs,
            evaluationOptions,
            cancellationToken);
        return new EngaugeGroupedWorkflowAggregateReport(
            ReportSchema,
            ordered.Length,
            decodedGroups,
            ordered.Sum(static group => group.Projects.Count),
            invocations,
            succeededGroups,
            failedGroups,
            succeededProjects,
            failedProjects,
            ordered.Sum(static group => group.TruthCase.Series.Count),
            ordered.Sum(static group => group.TruthCase.Points.Count),
            ordered.Sum(static group => group.CoincidentCrossProjectPointPairs),
            new ReadOnlyDictionary<string, int>(failures),
            evaluation,
            AggregateOnly: true,
            CaseLevelOutput: false,
            TruthRowsOutput: false,
            PredictionOutput: false,
            PathsOutput: false,
            NamesOutput: false);
    }

    private static EngaugeWorkflowImageGroup[] ValidateInventory(
        IReadOnlyList<EngaugeWorkflowImageGroup> groups)
    {
        if (groups.Count == 0 || groups.Any(static group => group is null))
        {
            throw new ArgumentException("A complete nonempty image-group inventory is required.", nameof(groups));
        }
        var caseKeys = new HashSet<string>(StringComparer.Ordinal);
        var sourceHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (EngaugeWorkflowImageGroup group in groups)
        {
            WholeWorkflowTruthCase truth = group.TruthCase;
            byte[] bytes = group.CopyImageBytes();
            if (!IsSha256(truth.SourceSha256) ||
                !string.Equals(truth.CaseKey, "image-" + truth.SourceSha256, StringComparison.Ordinal) ||
                truth.SourceWidth <= 0 || truth.SourceHeight <= 0 ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    truth.SourceSha256, StringComparison.Ordinal) ||
                group.Projects.Count == 0 ||
                group.Projects.Any(static project => project.Anchors.Count != 3) ||
                group.Projects.Sum(static project => project.SeriesCount) != truth.Series.Count ||
                group.Projects.Sum(static project => project.PointCount) != truth.Points.Count)
            {
                throw new InvalidDataException("DIG_GROUP_EXECUTION_INVENTORY_INVALID");
            }
            if (!caseKeys.Add(truth.CaseKey) || !sourceHashes.Add(truth.SourceSha256))
            {
                throw new InvalidDataException("DIG_GROUP_EXECUTION_INVENTORY_DUPLICATE");
            }
        }
        return groups.OrderBy(static group => group.TruthCase.CaseKey, StringComparer.Ordinal).ToArray();
    }

    private static WholeWorkflowCaseOutput NormalizeOutput(
        WholeWorkflowCaseOutput? candidate,
        WholeWorkflowTruthCase truth)
    {
        if (candidate is null)
        {
            return Failure(truth, OutputMissing);
        }
        if (!string.Equals(candidate.CaseKey, truth.CaseKey, StringComparison.Ordinal) ||
            !string.Equals(candidate.SourceSha256, truth.SourceSha256, StringComparison.Ordinal))
        {
            return Failure(truth, OutputIdentityMismatch);
        }
        if (candidate.WorkflowSucceeded)
        {
            return candidate.FailureCode is null && candidate.Artifacts.Count > 0
                ? candidate
                : Failure(truth, candidate.Artifacts.Count == 0 ? OutputMissing : OutputInvalid);
        }
        return candidate with { FailureCode = SanitizeFailureCode(candidate.FailureCode) };
    }

    private static bool CanFullyDecodeImage(
        byte[] bytes,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        if (expectedWidth <= 0 || expectedHeight <= 0 ||
            (long)expectedWidth * expectedHeight > MaximumSourcePixels)
        {
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasValidCompressedPngData(bytes, cancellationToken))
            {
                return false;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth != expectedWidth || frame.PixelHeight != expectedHeight)
            {
                return false;
            }

            // WIC can expose valid metadata while deferring a corrupt IDAT failure.
            // Materialize every pixel before the image-only inference delegate runs.
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0d);
            converted.Freeze();
            int stride = checked(expectedWidth * 4);
            var pixels = new byte[checked(stride * expectedHeight)];
            cancellationToken.ThrowIfCancellationRequested();
            converted.CopyPixels(pixels, stride, 0);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception exception) when (exception is
            NotSupportedException or
            FileFormatException or
            ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            COMException or
            OverflowException)
        {
            return false;
        }
    }

    private static bool HasValidCompressedPngData(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (bytes.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumEmbeddedImageBytes ||
            !bytes.AsSpan().StartsWith(PngSignature))
        {
            return false;
        }

        using var compressed = new MemoryStream();
        int offset = PngSignature.Length;
        int chunkIndex = 0;
        bool sawImageData = false;
        bool sawEnd = false;
        int bitDepth = 0;
        int colorType = -1;
        int interlaceMethod = -1;
        while (offset <= bytes.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunkIndex++;
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length < 0 || length > bytes.Length - offset - 12)
            {
                return false;
            }

            ReadOnlySpan<byte> type = bytes.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (chunkIndex != 1 || length != 13)
                {
                    return false;
                }
                ReadOnlySpan<byte> header = bytes.AsSpan(offset + 8, length);
                if (BinaryPrimitives.ReadInt32BigEndian(header[..4]) <= 0 ||
                    BinaryPrimitives.ReadInt32BigEndian(header.Slice(4, 4)) <= 0 ||
                    header[10] != 0 || header[11] != 0 || header[12] is not (0 or 1))
                {
                    return false;
                }
                bitDepth = header[8];
                colorType = header[9];
                interlaceMethod = header[12];
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (bitDepth == 0)
                {
                    return false;
                }
                compressed.Write(bytes, offset + 8, length);
                sawImageData |= length > 0;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0)
                {
                    return false;
                }
                sawEnd = true;
            }

            offset = checked(offset + 12 + length);
            if (sawEnd)
            {
                break;
            }
        }

        if (!sawImageData || !sawEnd || offset != bytes.Length)
        {
            return false;
        }

        if (!TryGetPngChannels(bitDepth, colorType, out int channels))
        {
            return false;
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        (long RowBytes, int Rows)[] passes = GetPngPasses(
            width, height, bitDepth, channels, interlaceMethod);
        long expectedDecodedBytes = passes.Aggregate(
            0L,
            static (total, pass) => checked(total + pass.RowBytes * pass.Rows));
        if (expectedDecodedBytes is <= 0 or > MaximumDecodedImageBytes || compressed.Length < 6)
        {
            return false;
        }

        byte[] compressedBytes = compressed.ToArray();
        int headerValue = compressedBytes[0] << 8 | compressedBytes[1];
        if ((compressedBytes[0] & 0x0f) != 8 || (compressedBytes[0] >> 4) > 7 ||
            headerValue % 31 != 0 || (compressedBytes[1] & 0x20) != 0)
        {
            return false;
        }
        uint expectedAdler32 = BinaryPrimitives.ReadUInt32BigEndian(
            compressedBytes.AsSpan(compressedBytes.Length - 4, 4));

        using var encoded = new MemoryStream(compressedBytes, writable: false);
        using var decompressor = new ZLibStream(
            encoded,
            CompressionMode.Decompress,
            leaveOpen: true);
        var buffer = new byte[64 * 1024];
        long decodedBytes = 0;
        ulong adlerS1 = 1;
        ulong adlerS2 = 0;
        int passIndex = 0;
        int rowIndex = 0;
        long positionInRow = 0;
        int read;
        while ((read = decompressor.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (decodedBytes + read > expectedDecodedBytes)
            {
                return false;
            }
            for (int index = 0; index < read; index++)
            {
                while (passIndex < passes.Length && rowIndex == passes[passIndex].Rows)
                {
                    passIndex++;
                    rowIndex = 0;
                }
                if (passIndex >= passes.Length ||
                    (positionInRow == 0 && buffer[index] > 4))
                {
                    return false;
                }
                adlerS1 += buffer[index];
                adlerS2 += adlerS1;
                positionInRow++;
                if (positionInRow == passes[passIndex].RowBytes)
                {
                    positionInRow = 0;
                    rowIndex++;
                }
            }
            adlerS1 %= 65_521;
            adlerS2 %= 65_521;
            decodedBytes += read;
        }
        uint actualAdler32 = (uint)(adlerS2 << 16 | adlerS1);
        return decodedBytes == expectedDecodedBytes &&
            positionInRow == 0 &&
            actualAdler32 == expectedAdler32 &&
            encoded.Position == encoded.Length;
    }

    private static bool TryGetPngChannels(int bitDepth, int colorType, out int channels)
    {
        channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        return colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => false,
        };
    }

    private static (long RowBytes, int Rows)[] GetPngPasses(
        int width,
        int height,
        int bitDepth,
        int channels,
        int interlaceMethod)
    {
        if (interlaceMethod == 0)
        {
            return [(checked(((long)width * channels * bitDepth + 7) / 8) + 1, height)];
        }

        int[] startX = [0, 4, 0, 2, 0, 1, 0];
        int[] startY = [0, 0, 4, 0, 2, 0, 1];
        int[] stepX = [8, 8, 4, 4, 2, 2, 1];
        int[] stepY = [8, 8, 8, 4, 4, 2, 2];
        var passes = new List<(long RowBytes, int Rows)>(7);
        for (int index = 0; index < 7; index++)
        {
            int passWidth = width <= startX[index]
                ? 0
                : checked((width - startX[index] + stepX[index] - 1) / stepX[index]);
            int passHeight = height <= startY[index]
                ? 0
                : checked((height - startY[index] + stepY[index] - 1) / stepY[index]);
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }
            long rowBytes = checked(((long)passWidth * channels * bitDepth + 7) / 8) + 1;
            passes.Add((rowBytes, passHeight));
        }
        return passes.ToArray();
    }

    private static WholeWorkflowCaseOutput Failure(WholeWorkflowTruthCase truth, string code) =>
        new(truth.CaseKey, truth.SourceSha256, false, code, []);

    private static string SanitizeFailureCode(string? code) =>
        code is not null && SafeWorkflowFailureCodes.Contains(code) ? code : ExecutionFailed;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}
