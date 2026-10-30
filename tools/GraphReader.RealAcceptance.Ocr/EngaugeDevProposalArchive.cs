// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

// Private interchange v1, little-endian, no padding: magic ASCII GAPOS001, 16-byte
// random run ID, u32 channels, u32 height, u32 width. Each record: byte 1,
// 32-byte upstream-verified image SHA256, u32 positive count, then for each
// positive f64 X, f64 Y, followed by channels*height*width f32 values in CHW
// order. Footer: byte 0, eight 32-byte digests (binding, protocol, assignment,
// inventory, OCR detection, OCR recognition, marker center, classifier), u64
// batch count, u64 positive count, then HMAC-SHA256 of all preceding bytes.
// The returned SHA256 covers the complete file. A reader MUST verify the expected
// external SHA256, run ID, HMAC and binding, length, counts, float ranges and EOF
// BEFORE exposing any data to training. HMAC key and expected values must arrive
// separately from the archive; SHA256 alone does not authenticate a file. The
// image digest binds the upstream-verified source, but the archive has no image
// bytes and cannot independently rehash it. No case names or negative labels.
internal sealed record EngaugeDevProposalArchiveReceipt(string Sha256, string RunId, long BatchCount, long PositiveCount);

internal sealed class EngaugeDevProposalArchive : IDisposable
{
    private static readonly byte[] Magic = "GAPOS001"u8.ToArray();
    private const int PatchValues = NormalizedMarkerProposalContract.ChannelCount * NormalizedMarkerProposalContract.PatchSize * NormalizedMarkerProposalContract.PatchSize;
    private readonly string path;
    private readonly string temporaryPath;
    private readonly FileStream stream;
    private readonly IncrementalHash hmac;
    private readonly IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly byte[] runId;
    private long batches;
    private long positives;
    private bool closed;
    private bool failed;

    internal EngaugeDevProposalArchive(string repositoryRoot, string outputPath, ReadOnlySpan<byte> authenticationKey)
    {
        if (authenticationKey.Length != 32) throw new ArgumentException("ENGAUGE_ARCHIVE_KEY_INVALID");
        string root = Path.GetFullPath(repositoryRoot);
        path = Path.GetFullPath(outputPath);
        string artifactRoot = Path.Combine(root, "artifacts");
        string relative = Path.GetRelativePath(artifactRoot, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("ENGAUGE_ARCHIVE_PATH_OUTSIDE_PROJECT");
        string parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("ENGAUGE_ARCHIVE_PATH_INVALID");
        // Existing directory ancestry only. Do not follow a junction out of the checkout.
        for (string? current = parent; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ENGAUGE_ARCHIVE_REPARSE_PATH");
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        if (File.Exists(path)) throw new IOException("ENGAUGE_ARCHIVE_ALREADY_EXISTS");
        temporaryPath = path + ".partial-" + Guid.NewGuid().ToString("N");
        stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, authenticationKey);
        runId = RandomNumberGenerator.GetBytes(16);
        Write(Magic); Write(runId);
        WriteU32(NormalizedMarkerProposalContract.ChannelCount); WriteU32(NormalizedMarkerProposalContract.PatchSize); WriteU32(NormalizedMarkerProposalContract.PatchSize);
    }

    internal ValueTask ConsumeAsync(EngaugeDevPositiveProposalBatch batch, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        ArgumentNullException.ThrowIfNull(batch);
        try
        {
        token.ThrowIfCancellationRequested();
        byte[] imageHash = DecodeDigest(batch.ImageSha256);
        if (!seen.Add(batch.ImageSha256)) throw new InvalidDataException("ENGAUGE_ARCHIVE_DUPLICATE_IMAGE");
        int count = batch.Coordinates.Count;
        if (batch.Tensor.Length != checked(count * PatchValues)) throw new InvalidDataException("ENGAUGE_ARCHIVE_TENSOR_LENGTH");
        foreach (MarkerPoint point in batch.Coordinates)
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0)
                throw new InvalidDataException("ENGAUGE_ARCHIVE_COORDINATE_INVALID");
        foreach (float value in batch.Tensor.Span)
            if (!float.IsFinite(value) || value < 0 || value > 1) throw new InvalidDataException("ENGAUGE_ARCHIVE_TENSOR_INVALID");
        Write([1]); Write(imageHash); WriteU32(checked((uint)count));
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            byte[] record = new byte[16 + 4 * PatchValues];
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(0, 8), BitConverter.DoubleToInt64Bits(batch.Coordinates[i].X));
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8, 8), BitConverter.DoubleToInt64Bits(batch.Coordinates[i].Y));
            ReadOnlySpan<float> values = batch.Tensor.Span.Slice(i * PatchValues, PatchValues);
            for (int j = 0; j < values.Length; j++)
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16 + 4 * j, 4), BitConverter.SingleToInt32Bits(values[j]));
            Write(record);
        }
        checked { batches++; positives += count; }
        return ValueTask.CompletedTask;
        }
        catch { failed = true; throw; }
    }

    internal EngaugeDevProposalArchiveReceipt Complete(EngaugeDevProposalPreparationResult result, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
        token.ThrowIfCancellationRequested();
        if (failed || result.ProcessedTrainGroupCount != batches || result.TrainGroupCount != batches || result.PositiveProposalCount != positives ||
            batches == 0 || positives == 0 || !result.AggregateOnly || result.ProductionApproved || result.OptimizerSteps != 0 || result.SealedReads != 0)
            throw new InvalidDataException("ENGAUGE_ARCHIVE_RESULT_MISMATCH");
        Write([0]);
        foreach (string value in new[] { result.BindingSha256, result.ProtocolSha256, result.AssignmentSha256,
                     result.SelectedInventorySha256, result.OcrDetectionSha256, result.OcrRecognitionSha256,
                     result.MarkerCenterSha256, result.MarkerClassifierSha256 }) Write(DecodeDigest(value));
        WriteU64(checked((ulong)batches)); WriteU64(checked((ulong)positives));
        byte[] tag = hmac.GetHashAndReset();
        stream.Write(tag); digest.AppendData(tag);
        stream.Flush(flushToDisk: true);
        string sha = Convert.ToHexStringLower(digest.GetHashAndReset());
        stream.Dispose(); hmac.Dispose(); digest.Dispose(); closed = true;
        try { File.Move(temporaryPath, path, overwrite: false); }
        catch { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); throw; }
        return new(sha, Convert.ToHexStringLower(runId), batches, positives);
        }
        catch { failed = true; throw; }
    }

    private void Write(ReadOnlySpan<byte> value) { stream.Write(value); hmac.AppendData(value); digest.AppendData(value); }
    private void WriteU32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); Write(b); }
    private void WriteU64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); Write(b); }
    private static byte[] DecodeDigest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new InvalidDataException("ENGAUGE_ARCHIVE_DIGEST_INVALID");
        return Convert.FromHexString(value);
    }
    public void Dispose()
    {
        if (closed) return;
        closed = true; stream.Dispose(); hmac.Dispose(); digest.Dispose();
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}
