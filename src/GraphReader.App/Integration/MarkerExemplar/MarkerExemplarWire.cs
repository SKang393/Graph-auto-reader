// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.App.Services;

namespace GraphReader.App.Integration.MarkerExemplar;

internal static class MarkerExemplarWire
{
    internal const int MaximumImageBytes = 32 * 1024 * 1024;
    private const int MaximumMetadataBytes = 16_384;
    private const int MaximumResponseBytes = 65_536;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    internal static async Task WriteRequestAsync(Stream stream, MarkerExemplarRequest request,
        ReadOnlyMemory<byte> image, CancellationToken token)
    {
        await WritePartAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, Options), MaximumMetadataBytes, token).ConfigureAwait(false);
        await WritePartAsync(stream, image, MaximumImageBytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<(MarkerExemplarRequest Request, byte[] Image)> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        byte[] metadata = await ReadPartAsync(stream, MaximumMetadataBytes, token).ConfigureAwait(false);
        MarkerExemplarRequest request = JsonSerializer.Deserialize<MarkerExemplarRequest>(metadata, Options)
            ?? throw new InvalidDataException();
        if (!MarkerExemplarValidation.ValidMetadata(request)) throw new InvalidDataException();
        return (request, await ReadPartAsync(stream, MaximumImageBytes, token).ConfigureAwait(false));
    }

    internal static async Task WriteResponseAsync(Stream stream, MarkerExemplarResponse response, CancellationToken token)
    {
        await WritePartAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, Options), MaximumResponseBytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<MarkerExemplarResponse> ReadResponseAsync(Stream stream, CancellationToken token) =>
        JsonSerializer.Deserialize<MarkerExemplarResponse>(await ReadPartAsync(stream, MaximumResponseBytes, token).ConfigureAwait(false), Options)
        ?? throw new InvalidDataException();

    private static async Task WritePartAsync(Stream stream, ReadOnlyMemory<byte> data, int maximum, CancellationToken token)
    {
        if (data.Length is <= 0 || data.Length > maximum) throw new InvalidDataException();
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPartAsync(Stream stream, int maximum, CancellationToken token)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 || count > maximum) throw new InvalidDataException();
        byte[] bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
}
