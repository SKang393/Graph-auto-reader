// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Case payload held only inside the aggregate sealed evaluation process.</summary>
internal sealed record OriginalDbOcrSealedSourcePayload(
    [property: JsonIgnore] int Ordinal,
    [property: JsonIgnore] string SourceId,
    [property: JsonIgnore] long Seed,
    [property: JsonIgnore] string ImageSha256,
    [property: JsonIgnore] string AnnotationSha256,
    [property: JsonIgnore] byte[] ImageBytes,
    [property: JsonIgnore] byte[] AnnotationBytes,
    [property: JsonIgnore] int Width,
    [property: JsonIgnore] int Height);

/// <summary>
/// Authenticates and reads an OCR synthetic reserve entirely in memory.
/// Admission occurs before the first byte is read from the supplied stream.
/// </summary>
internal static class OriginalDbOcrSealedArchive
{
    internal const string ArchiveSchema = "graphreader.synthetic-sealed-reserve-archive.v2";
    internal const string SourceSnapshotSchema = "graphreader.synthetic-generator-source-snapshot.v1";
    internal const string AcceptanceScope = "goal22.full-ocr.five-axis-family.real-range.v1";
    internal const string CoverageProtocolSha256 = "26ab0e017ccc6dc17d26effe11b1fb40f440ed4471e896c1afac3c8cc96999c4";

    private const string FailurePrefix = "OCR_SEALED_ARCHIVE_INVALID:";
    private const int MaximumSourceCount = 128;
    private const int MaximumEntryCount = 512;
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const long MaximumImagePixels = 16L * 1024 * 1024;

    private static readonly HashSet<string> ManifestFields =
    [
        "schema", "generation_config_sha256", "generator_source_bundle_sha256",
        "source_snapshot_manifest_sha256", "split", "purpose", "acceptance_scope",
        "coverage_protocol_sha256", "payload_bundle_sha256", "case_identity_sha256",
        "case_count", "cases", "synthetic_only", "private_data",
    ];

    private static readonly HashSet<string> SourceManifestFields =
        ["schema", "source_bundle_sha256", "sources"];
    private static readonly HashSet<string> SourceRowFields =
        ["path", "sha256", "archive_path"];
    private static readonly HashSet<string> CaseFields = ["ordinal", "files"];
    private static readonly string[] CasePayloadNames =
        ["annotation.json", "image.png", "marker-mask.png", "scene.json"];

    public static IReadOnlyList<OriginalDbOcrSealedSourcePayload> Read(
        Stream seekableArchive,
        string expectedArchiveSha256,
        string expectedManifestSha256,
        int expectedSourceCount,
        string expectedCoverageProtocolSha256,
        Action<CancellationToken> beforeFirstPayloadRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seekableArchive);
        ArgumentNullException.ThrowIfNull(beforeFirstPayloadRead);
        string archiveSha = Sha256(expectedArchiveSha256, "EXPECTED_ARCHIVE_SHA_INVALID");
        string manifestSha = Sha256(expectedManifestSha256, "EXPECTED_MANIFEST_SHA_INVALID");
        string protocolSha = Sha256(
            expectedCoverageProtocolSha256,
            "EXPECTED_PROTOCOL_SHA_INVALID");
        if (protocolSha != CoverageProtocolSha256)
        {
            throw Failure("PROTOCOL_UNSUPPORTED");
        }
        if (expectedSourceCount <= 0 || expectedSourceCount > MaximumSourceCount)
        {
            throw Failure("SOURCE_COUNT_INVALID");
        }
        long archiveLength;
        try
        {
            if (!seekableArchive.CanRead || !seekableArchive.CanSeek)
            {
                throw Failure("STREAM_INVALID");
            }
            archiveLength = seekableArchive.Length;
        }
        catch (InvalidDataException error) when (
            error.Message.StartsWith(FailurePrefix, StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Failure("STREAM_INVALID");
        }
        if (archiveLength <= 0 || archiveLength > MaximumArchiveBytes)
        {
            throw Failure("ARCHIVE_SIZE_INVALID");
        }

        cancellationToken.ThrowIfCancellationRequested();
        beforeFirstPayloadRead(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            seekableArchive.Position = 0;
            byte[] archiveBytes = ReadStream(
                seekableArchive,
                checked((int)archiveLength),
                cancellationToken);
            if (Hash(archiveBytes) != archiveSha)
            {
                throw Failure("ARCHIVE_SHA_MISMATCH");
            }
            using var memory = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
            return ReadArchive(
                archive,
                manifestSha,
                expectedSourceCount,
                protocolSha,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException error) when (
            error.Message.StartsWith(FailurePrefix, StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Failure("FORMAT_INVALID");
        }
    }

    private static ReadOnlyCollection<OriginalDbOcrSealedSourcePayload> ReadArchive(
        ZipArchive archive,
        string expectedManifestSha256,
        int expectedSourceCount,
        string expectedCoverageProtocolSha256,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntryCount)
        {
            throw Failure("ENTRY_COUNT_INVALID");
        }
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = CanonicalPath(entry.FullName, "ENTRY_PATH_INVALID");
            if (string.IsNullOrEmpty(entry.Name) || !entries.TryAdd(path, entry))
            {
                throw Failure("ENTRY_DUPLICATE_OR_DIRECTORY");
            }
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes)
            {
                throw Failure("ENTRY_SIZE_INVALID");
            }
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw Failure("EXPANDED_SIZE_INVALID");
            }
        }

        byte[] manifestPayload = ReadEntry(entries, "manifest.json", cancellationToken);
        if (Hash(manifestPayload) != expectedManifestSha256)
        {
            throw Failure("MANIFEST_SHA_MISMATCH");
        }
        using JsonDocument manifestDocument = ParseJson(manifestPayload, "MANIFEST_JSON_INVALID");
        JsonElement manifest = manifestDocument.RootElement;
        RequireObject(manifest, ManifestFields, "MANIFEST_SHAPE_INVALID");
        if (RequiredString(manifest, "schema", "MANIFEST_INVALID") != ArchiveSchema ||
            RequiredString(manifest, "split", "MANIFEST_INVALID") != "test" ||
            RequiredString(manifest, "purpose", "MANIFEST_INVALID") != "sealed_acceptance" ||
            RequiredString(manifest, "acceptance_scope", "MANIFEST_INVALID") != AcceptanceScope ||
            Sha256(RequiredString(manifest, "coverage_protocol_sha256", "MANIFEST_INVALID"),
                "MANIFEST_INVALID") != expectedCoverageProtocolSha256 ||
            !RequiredBoolean(manifest, "synthetic_only", "MANIFEST_INVALID") ||
            RequiredBoolean(manifest, "private_data", "MANIFEST_INVALID"))
        {
            throw Failure("MANIFEST_SCOPE_INVALID");
        }
        _ = Sha256(RequiredString(manifest, "generation_config_sha256", "MANIFEST_INVALID"),
            "MANIFEST_INVALID");
        string sourceBundleSha = Sha256(
            RequiredString(manifest, "generator_source_bundle_sha256", "MANIFEST_INVALID"),
            "MANIFEST_INVALID");
        string sourceManifestSha = Sha256(
            RequiredString(manifest, "source_snapshot_manifest_sha256", "MANIFEST_INVALID"),
            "MANIFEST_INVALID");
        string payloadBundleSha = Sha256(
            RequiredString(manifest, "payload_bundle_sha256", "MANIFEST_INVALID"),
            "MANIFEST_INVALID");
        int caseCount = RequiredPositiveInt(manifest, "case_count", "CASE_COUNT_INVALID");
        if (caseCount != expectedSourceCount)
        {
            throw Failure("CASE_COUNT_MISMATCH");
        }

        byte[] sourceManifestPayload = ReadEntry(
            entries,
            "source-snapshot.json",
            cancellationToken);
        if (Hash(sourceManifestPayload) != sourceManifestSha)
        {
            throw Failure("SOURCE_MANIFEST_SHA_MISMATCH");
        }
        ValidateSourceSnapshot(
            sourceManifestPayload,
            sourceBundleSha,
            entries,
            cancellationToken,
            out HashSet<string> expectedEntries);
        expectedEntries.Add("manifest.json");
        expectedEntries.Add("source-snapshot.json");

        JsonElement cases = RequiredArray(manifest, "cases", "CASES_INVALID");
        JsonElement caseIdentities = RequiredArray(
            manifest,
            "case_identity_sha256",
            "CASE_IDENTITIES_INVALID");
        if (cases.GetArrayLength() != caseCount || caseIdentities.GetArrayLength() != caseCount)
        {
            throw Failure("CASE_COUNT_MISMATCH");
        }

        var payloadRows = new List<(string Path, string Sha256)>();
        var sources = new List<OriginalDbOcrSealedSourcePayload>(caseCount);
        for (int ordinal = 0; ordinal < caseCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement rawCase = cases[ordinal];
            RequireObject(rawCase, CaseFields, "CASE_SHAPE_INVALID");
            if (RequiredInt(rawCase, "ordinal", "CASE_ORDINAL_INVALID") != ordinal)
            {
                throw Failure("CASE_ORDINAL_INVALID");
            }
            JsonElement files = RequiredObject(rawCase, "files", "CASE_FILES_INVALID");
            RequireObject(files, new HashSet<string>(CasePayloadNames, StringComparer.Ordinal),
                "CASE_FILES_INVALID");

            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in CasePayloadNames)
            {
                string expectedHash = Sha256(
                    RequiredString(files, name, "CASE_HASH_INVALID"),
                    "CASE_HASH_INVALID");
                string path = $"cases/{ordinal:0000}/{name}";
                byte[] payload = ReadEntry(entries, path, cancellationToken);
                if (Hash(payload) != expectedHash)
                {
                    throw Failure("CASE_PAYLOAD_SHA_MISMATCH");
                }
                payloads.Add(name, payload);
                hashes.Add(name, expectedHash);
                payloadRows.Add((path, expectedHash));
                expectedEntries.Add(path);
            }

            SourceIdentity identity = ValidateCase(payloads, cancellationToken);
            string expectedIdentity = Sha256(caseIdentities[ordinal], "CASE_IDENTITY_INVALID");
            if (Hash(CanonicalCaseIdentity(identity.SourceId, identity.Seed)) != expectedIdentity)
            {
                throw Failure("CASE_IDENTITY_MISMATCH");
            }
            sources.Add(new OriginalDbOcrSealedSourcePayload(
                ordinal,
                identity.SourceId,
                identity.Seed,
                hashes["image.png"],
                hashes["annotation.json"],
                payloads["image.png"],
                payloads["annotation.json"],
                identity.Width,
                identity.Height));
        }

        if (Hash(CanonicalRows(payloadRows)) != payloadBundleSha)
        {
            throw Failure("PAYLOAD_BUNDLE_MISMATCH");
        }
        if (entries.Count != expectedEntries.Count || entries.Keys.Any(path => !expectedEntries.Contains(path)))
        {
            throw Failure("UNMANIFESTED_ENTRY");
        }
        return new ReadOnlyCollection<OriginalDbOcrSealedSourcePayload>(sources);
    }

    private static void ValidateSourceSnapshot(
        byte[] payload,
        string expectedBundleSha256,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        CancellationToken cancellationToken,
        out HashSet<string> expectedEntries)
    {
        using JsonDocument document = ParseJson(payload, "SOURCE_MANIFEST_JSON_INVALID");
        JsonElement root = document.RootElement;
        RequireObject(root, SourceManifestFields, "SOURCE_MANIFEST_SHAPE_INVALID");
        if (RequiredString(root, "schema", "SOURCE_MANIFEST_INVALID") != SourceSnapshotSchema ||
            Sha256(RequiredString(root, "source_bundle_sha256", "SOURCE_MANIFEST_INVALID"),
                "SOURCE_MANIFEST_INVALID") != expectedBundleSha256)
        {
            throw Failure("SOURCE_MANIFEST_INVALID");
        }
        JsonElement rows = RequiredArray(root, "sources", "SOURCE_ROWS_INVALID");
        if (rows.GetArrayLength() <= 0 || rows.GetArrayLength() > MaximumSourceCount)
        {
            throw Failure("SOURCE_ROWS_INVALID");
        }

        var identities = new List<(string Path, string Sha256)>();
        expectedEntries = new HashSet<string>(StringComparer.Ordinal);
        string? previousPath = null;
        for (int index = 0; index < rows.GetArrayLength(); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement row = rows[index];
            RequireObject(row, SourceRowFields, "SOURCE_ROW_INVALID");
            string sourcePath = CanonicalPath(
                RequiredString(row, "path", "SOURCE_PATH_INVALID"),
                "SOURCE_PATH_INVALID");
            if (previousPath is not null && string.CompareOrdinal(previousPath, sourcePath) >= 0)
            {
                throw Failure("SOURCE_ORDER_INVALID");
            }
            previousPath = sourcePath;
            string sourceSha = Sha256(
                RequiredString(row, "sha256", "SOURCE_HASH_INVALID"),
                "SOURCE_HASH_INVALID");
            string archivePath = $"sources/{index:0000}.bin";
            if (RequiredString(row, "archive_path", "SOURCE_ARCHIVE_PATH_INVALID") != archivePath)
            {
                throw Failure("SOURCE_ARCHIVE_PATH_INVALID");
            }
            byte[] sourcePayload = ReadEntry(entries, archivePath, cancellationToken);
            if (Hash(sourcePayload) != sourceSha)
            {
                throw Failure("SOURCE_PAYLOAD_SHA_MISMATCH");
            }
            identities.Add((sourcePath, sourceSha));
            expectedEntries.Add(archivePath);
        }
        if (Hash(CanonicalRows(identities)) != expectedBundleSha256)
        {
            throw Failure("SOURCE_BUNDLE_MISMATCH");
        }
    }

    private static SourceIdentity ValidateCase(
        Dictionary<string, byte[]> payloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument sceneDocument = ParseJson(payloads["scene.json"], "SCENE_JSON_INVALID");
        using JsonDocument annotationDocument = ParseJson(
            payloads["annotation.json"],
            "ANNOTATION_JSON_INVALID");
        JsonElement scene = sceneDocument.RootElement;
        JsonElement annotation = annotationDocument.RootElement;
        RequireKind(scene, JsonValueKind.Object, "SCENE_INVALID");
        RequireKind(annotation, JsonValueKind.Object, "ANNOTATION_INVALID");
        string sourceId = RequiredString(scene, "scene_id", "SOURCE_ID_INVALID");
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw Failure("SOURCE_ID_INVALID");
        }
        long seed = RequiredLong(scene, "seed", "SOURCE_SEED_INVALID");
        if (RequiredString(annotation, "scene_id", "ANNOTATION_ID_INVALID") != sourceId ||
            RequiredLong(annotation, "seed", "ANNOTATION_ID_INVALID") != seed ||
            RequiredString(annotation, "coordinate_space", "ANNOTATION_ID_INVALID") !=
                "original_pixels")
        {
            throw Failure("ANNOTATION_ID_INVALID");
        }
        JsonElement canvas = RequiredObject(annotation, "canvas", "CANVAS_INVALID");
        int width = RequiredPositiveInt(canvas, "width", "CANVAS_INVALID");
        int height = RequiredPositiveInt(canvas, "height", "CANVAS_INVALID");
        (int imageWidth, int imageHeight) = PngDimensions(
            payloads["image.png"],
            "IMAGE_INVALID",
            cancellationToken);
        (int maskWidth, int maskHeight) = PngDimensions(
            payloads["marker-mask.png"],
            "MARKER_MASK_INVALID",
            cancellationToken);
        if (imageWidth != width || imageHeight != height ||
            maskWidth != width || maskHeight != height)
        {
            throw Failure("IMAGE_ANNOTATION_IDENTITY_MISMATCH");
        }
        return new SourceIdentity(sourceId, seed, width, height);
    }

    private static (int Width, int Height) PngDimensions(
        byte[] payload,
        string code,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (payload.Length < 33 || !payload.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(8, 4)) != 13 ||
            !payload.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw Failure(code);
        }
        uint rawWidth = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(16, 4));
        uint rawHeight = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(20, 4));
        if (rawWidth == 0 || rawHeight == 0 || rawWidth > int.MaxValue || rawHeight > int.MaxValue ||
            checked((long)rawWidth * rawHeight) > MaximumImagePixels)
        {
            throw Failure(code);
        }
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            var decoder = new PngBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != (int)rawWidth ||
                decoder.Frames[0].PixelHeight != (int)rawHeight)
            {
                throw Failure(code);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException error) when (
            error.Message.StartsWith(FailurePrefix, StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Failure(code);
        }
        return ((int)rawWidth, (int)rawHeight);
    }

    private static byte[] ReadEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path,
        CancellationToken cancellationToken)
    {
        if (!entries.TryGetValue(path, out ZipArchiveEntry? entry))
        {
            throw Failure("ENTRY_MISSING");
        }
        using Stream stream = entry.Open();
        return ReadStream(stream, checked((int)entry.Length), cancellationToken);
    }

    private static byte[] ReadStream(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        int offset = 0;
        while (offset < result.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(result.AsSpan(offset, Math.Min(64 * 1024, result.Length - offset)));
            if (count <= 0)
            {
                throw Failure("STREAM_TRUNCATED");
            }
            offset += count;
        }
        if (stream.ReadByte() != -1)
        {
            throw Failure("ENTRY_LENGTH_MISMATCH");
        }
        return result;
    }

    private static JsonDocument ParseJson(byte[] payload, string code)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            RejectDuplicatePropertyNames(document.RootElement);
            return document;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            document?.Dispose();
            throw Failure(code);
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    private static void RejectDuplicatePropertyNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Failure("JSON_DUPLICATE_KEY");
                }
                RejectDuplicatePropertyNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicatePropertyNames(item);
            }
        }
    }

    private static void RequireObject(JsonElement value, HashSet<string> fields, string code)
    {
        RequireKind(value, JsonValueKind.Object, code);
        var observed = value.EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!observed.SetEquals(fields) || observed.Count != value.EnumerateObject().Count())
        {
            throw Failure(code);
        }
    }

    private static JsonElement RequiredObject(JsonElement value, string property, string code)
    {
        if (!value.TryGetProperty(property, out JsonElement result))
        {
            throw Failure(code);
        }
        RequireKind(result, JsonValueKind.Object, code);
        return result;
    }

    private static JsonElement RequiredArray(JsonElement value, string property, string code)
    {
        if (!value.TryGetProperty(property, out JsonElement result))
        {
            throw Failure(code);
        }
        RequireKind(result, JsonValueKind.Array, code);
        return result;
    }

    private static string RequiredString(JsonElement value, string property, string code)
    {
        if (!value.TryGetProperty(property, out JsonElement result) ||
            result.ValueKind != JsonValueKind.String)
        {
            throw Failure(code);
        }
        return result.GetString() ?? throw Failure(code);
    }

    private static bool RequiredBoolean(JsonElement value, string property, string code)
    {
        if (!value.TryGetProperty(property, out JsonElement result) ||
            result.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Failure(code);
        }
        return result.GetBoolean();
    }

    private static int RequiredPositiveInt(JsonElement value, string property, string code)
    {
        int result = RequiredInt(value, property, code);
        return result > 0 ? result : throw Failure(code);
    }

    private static int RequiredInt(JsonElement value, string property, string code)
    {
        if (!value.TryGetProperty(property, out JsonElement result) ||
            result.ValueKind != JsonValueKind.Number || !result.TryGetInt32(out int number))
        {
            throw Failure(code);
        }
        return number;
    }

    private static long RequiredLong(JsonElement value, string property, string code)
    {
        if (!value.TryGetProperty(property, out JsonElement result) ||
            result.ValueKind != JsonValueKind.Number || !result.TryGetInt64(out long number))
        {
            throw Failure(code);
        }
        return number;
    }

    private static void RequireKind(JsonElement value, JsonValueKind expected, string code)
    {
        if (value.ValueKind != expected)
        {
            throw Failure(code);
        }
    }

    private static string CanonicalPath(string value, string code)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '/' || value.Contains('\\') ||
            value.Contains(':') || value.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw Failure(code);
        }
        return value;
    }

    private static string Sha256(JsonElement value, string code) =>
        value.ValueKind == JsonValueKind.String
            ? Sha256(value.GetString() ?? string.Empty, code)
            : throw Failure(code);

    private static string Sha256(string value, string code)
    {
        if (value is null)
        {
            throw Failure(code);
        }
        string normalized = value.ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw Failure(code);
        }
        return normalized;
    }

    private static string Hash(ReadOnlySpan<byte> payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    private static byte[] CanonicalRows(IEnumerable<(string Path, string Sha256)> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = CanonicalWriter(stream))
        {
            writer.WriteStartArray();
            foreach ((string path, string sha256) in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("sha256", sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static byte[] CanonicalCaseIdentity(string sourceId, long seed)
    {
        using var stream = new MemoryStream();
        using (var writer = CanonicalWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("scene_id", sourceId);
            writer.WriteNumber("seed", seed);
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static Utf8JsonWriter CanonicalWriter(Stream stream) => new(
        stream,
        new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private static InvalidDataException Failure(string code) => new(FailurePrefix + code);

    private sealed record SourceIdentity(string SourceId, long Seed, int Width, int Height);
}
