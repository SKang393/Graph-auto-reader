// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphReader.App.Services;

public interface ICalibrationOcrSuggestionService : IAsyncDisposable
{
    Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request,
        ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken);
}

public sealed record CalibrationOcrPlot(double Left, double Top, double Right, double Bottom);
public sealed record CalibrationOcrPoint(double X, double Y);
public sealed record CalibrationOcrRequest(string RequestId, string Binding, string SourceFileSha256,
    string PanelImageSha256, int Width, int Height, CalibrationOcrPlot Plot)
{
    public int ProtocolVersion { get; init; } = 1;
}

public sealed record CalibrationOcrProposal(string RegionId, IReadOnlyList<CalibrationOcrPoint> Polygon,
    string Text, double Confidence, double? NumericValue, string? NormalizedText, bool NumericLiteral,
    string Role, string SuggestedAxis, string? FailureCode)
{
    public bool RequiresReview { get; init; } = true;
}

public sealed record CalibrationOcrSequenceCheck(bool NeedsReview, int CandidateCount, int ResolvedCount,
    int RejectedCount, IReadOnlyList<string> Reasons);
public sealed record CalibrationOcrTimings(double DecodeMilliseconds, double ComponentMilliseconds,
    double CropMilliseconds, double InitializationMilliseconds, double RecognitionMilliseconds,
    double TotalMilliseconds, bool ReusedSession);
public sealed record CalibrationOcrCpuBudget(uint LogicalProcessors, uint Rate, double MaximumLogicalCores, bool Verified)
{
    public string PriorityClass { get; init; } = "BelowNormal";
}
public sealed record CalibrationOcrProcessMemory(long PrivateBytesBefore, long PrivateBytesAfter,
    long WorkingSetBefore, long WorkingSetAfter, long ManagedBytesBefore, long ManagedBytesAfter);
public sealed record CalibrationOcrRuntimeFile(string Name, string Sha256);
public sealed record CalibrationOcrRuntime(string ModelSha256, string ManifestSha256, string Provider,
    int IntraOperationThreads, int InterOperationThreads, int WorkerCount, int SessionCreationCount,
    bool PersistentCache, CalibrationOcrCpuBudget CpuBudget, CalibrationOcrProcessMemory Memory,
    IReadOnlyList<CalibrationOcrRuntimeFile> Files);
public sealed record CalibrationOcrResponse(CalibrationOcrRequest? Request,
    IReadOnlyList<CalibrationOcrProposal> Proposals, CalibrationOcrSequenceCheck? XSequence,
    CalibrationOcrSequenceCheck? YSequence, CalibrationOcrTimings? Timings,
    CalibrationOcrRuntime? Runtime, string? FailureCode)
{
    public static CalibrationOcrResponse Failure(CalibrationOcrRequest? request, string reason) =>
        new(request, [], null, null, null, null, reason);
}

public static class CalibrationOcrReasonCodes
{
    public const string Unavailable = "CALIBRATION_OCR_UNAVAILABLE";
    public const string InvalidInput = "CALIBRATION_OCR_INVALID_INPUT";
    public const string AssetMismatch = "CALIBRATION_OCR_ASSET_MISMATCH";
    public const string ResourceUnavailable = "CALIBRATION_OCR_RESOURCE_UNAVAILABLE";
    public const string Timeout = "CALIBRATION_OCR_TIMEOUT";
    public const string InvalidFrame = "CALIBRATION_OCR_INVALID_FRAME";
    public const string Cancelled = "CALIBRATION_OCR_CANCELLED";
    public const string RecognitionFailed = "CALIBRATION_OCR_RECOGNITION_FAILED";
    public const string TooManyCandidates = "CALIBRATION_OCR_TOO_MANY_CANDIDATES";
    public const string SequenceInsufficient = "CALIBRATION_OCR_SEQUENCE_INSUFFICIENT";
    public const string SequenceConflict = "CALIBRATION_OCR_SEQUENCE_CONFLICT";
    public const string SequenceSpacing = "CALIBRATION_OCR_SEQUENCE_SPACING";
    public const string NonnumericReading = "CALIBRATION_OCR_NONNUMERIC_READING";
}

internal sealed record CalibrationOcrWorkItem(CalibrationOcrRequest Request, byte[] EncodedImage);

// Private App-to-App pipe protocol. It is not a persisted project or vision contract.
internal static class CalibrationOcrWire
{
    internal const int MaximumMetadataBytes = 16_384;
    internal const int MaximumImageBytes = 64 * 1024 * 1024;
    internal const int MaximumResponseBytes = 1_000_000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    internal static async Task WriteRequestAsync(Stream stream, CalibrationOcrRequest request,
        ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken)
    {
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(request, Options);
        CheckLength(metadata.Length, MaximumMetadataBytes);
        CheckLength(encodedOriginalPanel.Length, MaximumImageBytes);
        await WritePartAsync(stream, metadata, cancellationToken).ConfigureAwait(false);
        await WritePartAsync(stream, encodedOriginalPanel, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CalibrationOcrWorkItem?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken,
        Action? requestStarted = null)
    {
        byte[]? metadata = await ReadPartAsync(stream, MaximumMetadataBytes, allowEof: true, cancellationToken, requestStarted).ConfigureAwait(false);
        if (metadata is null) return null;
        CalibrationOcrRequest request = JsonSerializer.Deserialize<CalibrationOcrRequest>(metadata, Options)
            ?? throw new InvalidDataException(CalibrationOcrReasonCodes.InvalidFrame);
        byte[] image = await ReadPartAsync(stream, MaximumImageBytes, allowEof: false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(CalibrationOcrReasonCodes.InvalidFrame);
        return new(request, image);
    }

    internal static async Task WriteResponseAsync(Stream stream, CalibrationOcrResponse response, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, Options);
        CheckLength(bytes.Length, MaximumResponseBytes);
        await WritePartAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CalibrationOcrResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<CalibrationOcrResponse>(
            await ReadPartAsync(stream, MaximumResponseBytes, allowEof: false, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(CalibrationOcrReasonCodes.InvalidFrame), Options)
            ?? throw new InvalidDataException(CalibrationOcrReasonCodes.InvalidFrame);

    private static async Task WritePartAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadPartAsync(Stream stream, int maximum, bool allowEof, CancellationToken cancellationToken,
        Action? firstByteReceived = null)
    {
        byte[] length = new byte[4];
        int first = await stream.ReadAsync(length.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0 && allowEof) return null;
        if (first == 0) throw new EndOfStreamException();
        firstByteReceived?.Invoke();
        await stream.ReadExactlyAsync(length.AsMemory(1), cancellationToken).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        CheckLength(count, maximum);
        byte[] bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static void CheckLength(int length, int maximum)
    {
        if (length is <= 0 || length > maximum) throw new InvalidDataException(CalibrationOcrReasonCodes.InvalidFrame);
    }
}
