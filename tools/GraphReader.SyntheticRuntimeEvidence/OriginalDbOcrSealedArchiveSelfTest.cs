// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OriginalDbOcrSealedArchiveSelfTest
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public static object Run()
    {
        int checks = 0;
        Fixture valid = BuildFixture();
        var stream = new CountingStream(valid.ArchiveBytes);
        bool admitted = false;
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> sources =
            OriginalDbOcrSealedArchive.Read(
                stream,
                valid.ArchiveSha256,
                valid.ManifestSha256,
                1,
                OriginalDbOcrSealedArchive.CoverageProtocolSha256,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    Require(stream.ReadCalls == 0, "admission precedes every stream read");
                    admitted = true;
                },
                CancellationToken.None);
        Require(admitted && stream.ReadCalls > 0, "successful admission permits reads");
        checks++;
        Require(sources.Count == 1 && sources[0].Ordinal == 0 &&
            sources[0].SourceId == "source-1" && sources[0].Seed == 7 &&
            sources[0].Width == 1 && sources[0].Height == 1 &&
            sources[0].ImageSha256 == Hash(Png) && sources[0].ImageBytes.SequenceEqual(Png),
            "ordered source payload is authenticated in memory");
        checks++;
        Require(JsonSerializer.Serialize(sources[0]) == "{}",
            "case bytes and identities cannot enter JSON evidence");
        checks++;

        var deniedStream = new CountingStream(valid.ArchiveBytes);
        try
        {
            _ = OriginalDbOcrSealedArchive.Read(
                deniedStream,
                valid.ArchiveSha256,
                valid.ManifestSha256,
                1,
                OriginalDbOcrSealedArchive.CoverageProtocolSha256,
                _ => throw new AdmissionDeniedException(),
                CancellationToken.None);
            throw new InvalidOperationException("Expected admission denial was not raised.");
        }
        catch (AdmissionDeniedException)
        {
            Require(deniedStream.ReadCalls == 0, "admission denial prevents all reads");
        }
        checks++;

        var cancelledStream = new CountingStream(valid.ArchiveBytes);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                _ = OriginalDbOcrSealedArchive.Read(
                    cancelledStream,
                    valid.ArchiveSha256,
                    valid.ManifestSha256,
                    1,
                    OriginalDbOcrSealedArchive.CoverageProtocolSha256,
                    _ => throw new InvalidOperationException("callback must not run"),
                    cancellation.Token);
                throw new InvalidOperationException("Expected cancellation was not raised.");
            }
            catch (OperationCanceledException)
            {
                Require(cancelledStream.ReadCalls == 0, "pre-cancellation prevents reads");
            }
        }
        checks++;

        ExpectFailure(BuildFixture("malformed"), 1);
        checks++;
        ExpectFailure(BuildFixture("duplicate"), 1);
        checks++;
        ExpectFailure(BuildFixture("path"), 1);
        checks++;
        ExpectFailure(BuildFixture("hash"), 1);
        checks++;
        ExpectFailure(BuildFixture("count"), 2);
        checks++;
        ExpectFailure(BuildFixture("protocol"), 1);
        checks++;

        var unsupportedStream = new CountingStream(valid.ArchiveBytes);
        ExpectFailure(
            unsupportedStream,
            valid,
            1,
            "0" + OriginalDbOcrSealedArchive.CoverageProtocolSha256[1..]);
        Require(unsupportedStream.ReadCalls == 0,
            "unsupported protocol identity is rejected before admission or reads");
        checks++;

        // Python _archive_bytes produced these two handwritten, unsealed 3x2 cases.
        // Frozen bytes check producer/reader compatibility, including Unicode identities.
        byte[] pythonArchive = Convert.FromBase64String("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");
        using var pythonStream = new MemoryStream(pythonArchive, writable: false);
        IReadOnlyList<OriginalDbOcrSealedSourcePayload> pythonSources = OriginalDbOcrSealedArchive.Read(
            pythonStream, "c939a0cd0d267f29d09bdf33b5ba67e756d2a00e7691496d9adb02361ea27622", "ea165f7e1da58f2cd71debaa1b9396473f4eba7aeed9452f695a2c659f98048d", 2,
            OriginalDbOcrSealedArchive.CoverageProtocolSha256, _ => { }, CancellationToken.None);
        Require(pythonSources.Count == 2 && pythonSources[0].SourceId == "fixture-café" &&
            pythonSources[1].SourceId == "fixture-second" && pythonSources[0].Seed == 1 &&
            pythonSources[1].Seed == 2 && pythonSources.All(source => source.Width == 3 && source.Height == 2),
            "Python archive canonical hashes and source order match");
        checks++;

        return new
        {
            Status = "passed",
            CheckCount = checks,
            ModelInference = false,
            FileIo = false,
            ArchiveExtraction = false,
            CaseDataReturned = false,
            PrivateData = false,
            SealedData = false,
            ProductionApproved = false,
        };
    }

    private static void ExpectFailure(Fixture fixture, int expectedSourceCount)
    {
        var stream = new CountingStream(fixture.ArchiveBytes);
        ExpectFailure(
            stream,
            fixture,
            expectedSourceCount,
            OriginalDbOcrSealedArchive.CoverageProtocolSha256);
        Require(stream.ReadCalls > 0, "post-admission validation failure reads only in memory");
    }

    private static void ExpectFailure(
        CountingStream stream,
        Fixture fixture,
        int expectedSourceCount,
        string protocolSha256)
    {
        try
        {
            _ = OriginalDbOcrSealedArchive.Read(
                stream,
                fixture.ArchiveSha256,
                fixture.ManifestSha256,
                expectedSourceCount,
                protocolSha256,
                _ => Require(stream.ReadCalls == 0, "failure admission precedes reads"),
                CancellationToken.None);
            throw new InvalidOperationException("Expected archive validation failure was not raised.");
        }
        catch (InvalidDataException error)
        {
            Require(
                error.Message.StartsWith("OCR_SEALED_ARCHIVE_INVALID:", StringComparison.Ordinal) &&
                !error.Message.Contains("source-1", StringComparison.Ordinal) &&
                !error.Message.Contains("not-json", StringComparison.Ordinal),
                "archive failure is fixed and sanitized");
        }
    }

    private static Fixture BuildFixture(string? mutation = null)
    {
        byte[] sourcePayload = Encoding.UTF8.GetBytes("project-owned-source");
        string sourceSha = Hash(sourcePayload);
        string sourcePath = mutation == "path" ? "../source.py" : "ml/synthetic/source.py";
        byte[] sourceRows = CanonicalRows([(sourcePath, sourceSha)]);
        string sourceBundleSha = Hash(sourceRows);
        byte[] sourceManifest = CanonicalJson(new SortedDictionary<string, object?>
        {
            ["schema"] = OriginalDbOcrSealedArchive.SourceSnapshotSchema,
            ["source_bundle_sha256"] = sourceBundleSha,
            ["sources"] = new object[]
            {
                new SortedDictionary<string, object?>
                {
                    ["archive_path"] = "sources/0000.bin",
                    ["path"] = sourcePath,
                    ["sha256"] = sourceSha,
                },
            },
        });

        byte[] scene = CanonicalJson(new SortedDictionary<string, object?>
        {
            ["scene_id"] = "source-1",
            ["seed"] = 7,
        });
        byte[] annotation = CanonicalJson(new SortedDictionary<string, object?>
        {
            ["canvas"] = new SortedDictionary<string, object?>
            {
                ["height"] = 1,
                ["width"] = 1,
            },
            ["coordinate_space"] = "original_pixels",
            ["scene_id"] = "source-1",
            ["seed"] = 7,
        });
        var payloads = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["annotation.json"] = annotation,
            ["image.png"] = Png,
            ["marker-mask.png"] = Png,
            ["scene.json"] = scene,
        };
        var fileHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var payloadRows = new List<(string Path, string Sha256)>();
        foreach ((string name, byte[] payload) in payloads)
        {
            string hash = Hash(payload);
            fileHashes.Add(name, mutation == "hash" && name == "annotation.json" ? new string('0', 64) : hash);
            payloadRows.Add(($"cases/0000/{name}", hash));
        }

        int caseCount = mutation == "count" ? 2 : 1;
        string protocolSha = mutation == "protocol"
            ? new string('0', 64)
            : OriginalDbOcrSealedArchive.CoverageProtocolSha256;
        byte[] manifest = mutation == "malformed"
            ? Encoding.UTF8.GetBytes("not-json")
            : CanonicalJson(new SortedDictionary<string, object?>
            {
                ["acceptance_scope"] = OriginalDbOcrSealedArchive.AcceptanceScope,
                ["case_count"] = caseCount,
                ["case_identity_sha256"] = new[]
                {
                    Hash(CanonicalJson(new SortedDictionary<string, object?>
                    {
                        ["scene_id"] = "source-1",
                        ["seed"] = 7,
                    })),
                },
                ["cases"] = new object[]
                {
                    new SortedDictionary<string, object?>
                    {
                        ["files"] = fileHashes,
                        ["ordinal"] = 0,
                    },
                },
                ["coverage_protocol_sha256"] = protocolSha,
                ["generation_config_sha256"] = new string('1', 64),
                ["generator_source_bundle_sha256"] = sourceBundleSha,
                ["payload_bundle_sha256"] = Hash(CanonicalRows(payloadRows)),
                ["private_data"] = false,
                ["purpose"] = "sealed_acceptance",
                ["schema"] = OriginalDbOcrSealedArchive.ArchiveSchema,
                ["source_snapshot_manifest_sha256"] = Hash(sourceManifest),
                ["split"] = "test",
                ["synthetic_only"] = true,
            });

        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "sources/0000.bin", sourcePayload);
            WriteEntry(archive, "source-snapshot.json", sourceManifest);
            foreach ((string name, byte[] payload) in payloads)
            {
                WriteEntry(archive, $"cases/0000/{name}", payload);
            }
            WriteEntry(archive, "manifest.json", manifest);
            if (mutation == "duplicate")
            {
                WriteEntry(archive, "manifest.json", manifest);
            }
        }
        byte[] archiveBytes = archiveStream.ToArray();
        return new Fixture(archiveBytes, Hash(archiveBytes), Hash(manifest));
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] payload)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        using Stream stream = entry.Open();
        stream.Write(payload);
    }

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static byte[] CanonicalJson(object value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, CanonicalJsonOptions);
        return [.. payload, (byte)'\n'];
    }

    private static byte[] CanonicalRows(IEnumerable<(string Path, string Sha256)> rows)
    {
        object[] documents = rows.Select(row => new SortedDictionary<string, object?>
        {
            ["path"] = row.Path,
            ["sha256"] = row.Sha256,
        }).ToArray();
        return CanonicalJson(documents);
    }

    private static string Hash(ReadOnlySpan<byte> payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + label);
        }
    }

    private sealed record Fixture(
        byte[] ArchiveBytes,
        string ArchiveSha256,
        string ManifestSha256);

    private sealed class AdmissionDeniedException : Exception
    {
    }

    private sealed class CountingStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            return _inner.Read(buffer);
        }

        public override int ReadByte()
        {
            ReadCalls++;
            return _inner.ReadByte();
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
