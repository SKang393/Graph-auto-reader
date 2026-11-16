// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GraphReader.Inference;

public sealed record StageCacheKey(string Value)
{
    public static StageCacheKey Create(
        string inputSha256,
        string panelCrop,
        string transformChain,
        string stageName,
        string stageVersion,
        string modelSha256,
        IReadOnlyDictionary<string, object?> parameters,
        int contractVersion)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var canonicalParameters = CanonicalJson.Serialize(parameters);
        var material = string.Join(
            "\n",
            inputSha256,
            panelCrop,
            transformChain,
            stageName,
            stageVersion,
            modelSha256,
            canonicalParameters,
            contractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new StageCacheKey(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant());
    }
}

public static class InferenceCacheKeyDeriver
{
    public static StageCacheKey Derive(InferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.CacheMaterial);
        var tensorHash = HashTensorInput(request.Input);
        var providerPolicy = request.AllowedProviders is null
            ? "policy-default"
            : string.Join(',', request.AllowedProviders
                .Distinct()
                .OrderBy(static provider => provider)
                .Select(static provider => provider.ToString()));
        return StageCacheKey.Create(
            request.CacheMaterial.InputSha256 + ":" + tensorHash,
            request.CacheMaterial.PanelCrop,
            request.CacheMaterial.TransformChain,
            request.CacheMaterial.StageName,
            request.CacheMaterial.StageVersion,
            request.Model.Sha256 + ":" + providerPolicy,
            request.CacheMaterial.Parameters,
            request.CacheMaterial.ContractVersion);
    }

    private static string HashTensorInput(InferenceInput input)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, input.InputName);
        AppendString(hash, input.OutputName);
        Span<byte> integer = stackalloc byte[sizeof(long)];
        foreach (var dimension in input.Shape)
        {
            BinaryPrimitives.WriteInt64LittleEndian(integer, dimension);
            hash.AppendData(integer);
        }

        ReadOnlySpan<float> values = input.Values.Span;
        if (BitConverter.IsLittleEndian)
        {
            const int chunkFloats = 16_384;
            for (int offset = 0; offset < values.Length;)
            {
                int count = Math.Min(chunkFloats, values.Length - offset);
                hash.AppendData(MemoryMarshal.AsBytes(values.Slice(offset, count)));
                offset += count;
            }
        }
        else
        {
            Span<byte> number = stackalloc byte[sizeof(float)];
            foreach (var value in values)
            {
                BinaryPrimitives.WriteSingleLittleEndian(number, value);
                hash.AppendData(number);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public interface IStageCache
{
    ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken);

    ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken);
}

public sealed class ContentAddressedStageCache : IStageCache
{
    private readonly string _root;
    private readonly SemaphoreSlim[] _writeGates = Enumerable.Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    public ContentAddressedStageCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        var path = GetPath(key);
        var gate = _writeGates[Convert.ToInt32(key.Value[..2], 16) % _writeGates.Length];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetPath(StageCacheKey key)
    {
        if (key.Value.Length != 64 || !key.Value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Cache key must be a SHA-256 hexadecimal value.", nameof(key));
        }

        return Path.Combine(_root, key.Value[..2], key.Value + ".bin");
    }
}

internal static class CanonicalJson
{
    public static string Serialize(IReadOnlyDictionary<string, object?> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteDictionary(writer, values);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDictionary(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> values)
    {
        writer.WriteStartObject();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            WriteValue(writer, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                WriteDictionary(writer, dictionary);
                break;
            case IEnumerable<object?> items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonElement element:
                WriteElement(writer, element);
                break;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType());
                break;
        }
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteElement(writer, property.Value);
            }

            writer.WriteEndObject();
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteElement(writer, item);
            }

            writer.WriteEndArray();
            return;
        }

        element.WriteTo(writer);
    }
}
