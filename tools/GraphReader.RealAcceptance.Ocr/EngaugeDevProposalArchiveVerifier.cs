// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

// Validates the whole archive while retaining the caller's locked handle. No
// record data is returned; a consumer may rewind this same handle only after
// success. Callers must acquire FileAccess.Read / FileShare.Read and retain it
// throughout training to prevent a replace/write between verification and use.
internal static class EngaugeDevProposalArchiveVerifier
{
    private const int PatchValues = NormalizedMarkerProposalContract.ChannelCount * NormalizedMarkerProposalContract.PatchSize * NormalizedMarkerProposalContract.PatchSize;
    internal static void Verify(FileStream file, byte[] key, EngaugeDevProposalArchiveReceipt expected, string expectedBindingSha256, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expected);
        if (!file.CanRead || !file.CanSeek || key.Length != 32) throw new InvalidDataException("ENGAUGE_ARCHIVE_VERIFY_ARGUMENT");
        token.ThrowIfCancellationRequested();
        file.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        var reader = new Reader(file, sha, hmac, token);
        if (!reader.Read(8).SequenceEqual("GAPOS001"u8.ToArray()) ||
            !CryptographicOperations.FixedTimeEquals(reader.Read(16), Decode(expected.RunId, 16)) ||
            reader.U32() != NormalizedMarkerProposalContract.ChannelCount || reader.U32() != NormalizedMarkerProposalContract.PatchSize ||
            reader.U32() != NormalizedMarkerProposalContract.PatchSize)
            throw new InvalidDataException("ENGAUGE_ARCHIVE_HEADER_INVALID");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong batches = 0, positives = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            byte kind = reader.Read(1)[0];
            if (kind == 0) break;
            if (kind != 1) throw new InvalidDataException("ENGAUGE_ARCHIVE_RECORD_INVALID");
            string image = Convert.ToHexString(reader.Read(32));
            if (!seen.Add(image)) throw new InvalidDataException("ENGAUGE_ARCHIVE_DUPLICATE_IMAGE");
            uint count = reader.U32();
            long bytesPerRecord = 16L + 4L * PatchValues;
            if ((long)count > (file.Length - file.Position) / bytesPerRecord) throw new InvalidDataException("ENGAUGE_ARCHIVE_TRUNCATED");
            for (uint i = 0; i < count; i++)
            {
                byte[] record = reader.Read(16 + 4 * PatchValues);
                double x = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(0, 8)));
                double y = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8, 8)));
                if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0) throw new InvalidDataException("ENGAUGE_ARCHIVE_COORDINATE_INVALID");
                for (int j = 0; j < PatchValues; j++)
                {
                    float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(16 + 4 * j, 4)));
                    if (!float.IsFinite(value) || value < 0 || value > 1) throw new InvalidDataException("ENGAUGE_ARCHIVE_TENSOR_INVALID");
                }
            }
            checked { batches++; positives += count; }
        }
        if (!CryptographicOperations.FixedTimeEquals(reader.Read(32), Decode(expectedBindingSha256, 32)))
            throw new InvalidDataException("ENGAUGE_ARCHIVE_BINDING_MISMATCH");
        for (int i = 0; i < 7; i++) _ = reader.Read(32);
        if (reader.U64() != batches || reader.U64() != positives || batches != (ulong)expected.BatchCount || positives != (ulong)expected.PositiveCount || batches == 0 || positives == 0)
            throw new InvalidDataException("ENGAUGE_ARCHIVE_COUNTS_INVALID");
        byte[] computedTag = hmac.GetHashAndReset();
        byte[] storedTag = reader.ReadTag();
        if (file.Position != file.Length || !CryptographicOperations.FixedTimeEquals(computedTag, storedTag) ||
            !CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), Decode(expected.Sha256, 32)))
            throw new InvalidDataException("ENGAUGE_ARCHIVE_AUTHENTICATION_FAILED");
        file.Position = 0;
    }

    private static byte[] Decode(string value, int bytes)
    {
        if (value is null || value.Length != bytes * 2 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new InvalidDataException("ENGAUGE_ARCHIVE_EXPECTATION_INVALID");
        return Convert.FromHexString(value);
    }
    private sealed class Reader(FileStream file, IncrementalHash sha, IncrementalHash hmac, CancellationToken token)
    {
        internal byte[] Read(int length)
        {
            token.ThrowIfCancellationRequested();
            byte[] data = new byte[length];
            try { file.ReadExactly(data); } catch (EndOfStreamException) { throw new InvalidDataException("ENGAUGE_ARCHIVE_TRUNCATED"); }
            sha.AppendData(data); hmac.AppendData(data); return data;
        }
        internal byte[] ReadTag()
        {
            byte[] data = new byte[32];
            try { file.ReadExactly(data); } catch (EndOfStreamException) { throw new InvalidDataException("ENGAUGE_ARCHIVE_TRUNCATED"); }
            sha.AppendData(data); return data;
        }
        internal uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));
        internal ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Read(8));
    }
}
