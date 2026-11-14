// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Inference;

namespace GraphReader.App.Services;

/// <summary>Invalidates a local timing profile when its execution inputs change.</summary>
internal static class CalibrationOcrDeviceIdentity
{
    private const string PolicyIdentity = "ocr-owned-fixture-four-reads-v1-15percent-halfcore-paced";
    private static readonly string[] RuntimeFiles =
    [
        "GraphReader.App.dll", "GraphReader.Ocr.dll", "GraphReader.Inference.dll",
        "Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll",
    ];
    private static readonly string[] AssetFiles = ["en_PP-OCRv5_mobile_rec.onnx", "recognizer.json"];

    internal static Task<string> ReadAsync(string applicationRoot, string assetRoot, CancellationToken token) =>
        Task.Run(() => ReadCoreAsync(applicationRoot, assetRoot, new WindowsDxgiAdapterInventory(),
            string.Join('|', RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture,
                RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount,
                Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")), token), token);

    internal static async Task<string> ReadCoreAsync(string applicationRoot, string assetRoot,
        IDxgiAdapterInventory inventory, string processorAndOsIdentity, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        ArgumentNullException.ThrowIfNull(inventory);
        if (!Path.IsPathFullyQualified(applicationRoot) || !Path.IsPathFullyQualified(assetRoot))
            throw new ArgumentException("Device profile roots must be absolute.");

        var files = new List<FileIdentity>();
        foreach (string name in RuntimeFiles)
            files.Add(new(name, await HashFileAsync(Path.Combine(applicationRoot, name), token).ConfigureAwait(false)));
        foreach (string name in AssetFiles)
            files.Add(new(name, await HashFileAsync(Path.Combine(assetRoot, name), token).ConfigureAwait(false)));
        token.ThrowIfCancellationRequested();
        DxgiAdapter[] hardware = inventory.Enumerate().Where(adapter => !adapter.IsSoftware)
            .OrderBy(adapter => adapter.Index).ToArray();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            policy = PolicyIdentity, host = processorAndOsIdentity, files, hardware,
        });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 256 * 1024 * 1024)
            throw new InvalidDataException("Device profile input exceeds its file bound.");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private sealed record FileIdentity(string Name, string Sha256);
}
