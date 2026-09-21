// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace GraphReader.Evidence;

/// <summary>Path-independent identity of the native OCR stage shared by the two evidence tools.</summary>
internal static class ComposedOcrExecutionIdentity
{
    internal static readonly string[] RequiredFiles =
    [
        "GraphReader.App.dll", "GraphReader.Axis.dll", "GraphReader.Domain.dll",
        "GraphReader.Export.dll", "GraphReader.Imaging.dll", "GraphReader.Inference.dll",
        "GraphReader.Legends.dll", "GraphReader.Markers.dll", "GraphReader.Ocr.dll",
        "GraphReader.Pdf.dll", "GraphReader.Phases.dll", "GraphReader.SuperResolution.dll",
        "Microsoft.ML.OnnxRuntime.dll", "OpenCvSharp.dll", "OpenCvSharpExtern.dll",
        "onnxruntime.dll", "onnxruntime_providers_shared.dll", "Imazen.WebP.dll",
        "libwebp.dll", "libwebpdemux.dll", "libwebpmux.dll", "libsharpyuv.dll",
        "UglyToad.PdfPig.dll", "UglyToad.PdfPig.Core.dll", "UglyToad.PdfPig.DocumentLayoutAnalysis.dll",
        "UglyToad.PdfPig.Fonts.dll", "UglyToad.PdfPig.Package.dll", "UglyToad.PdfPig.Tokenization.dll",
        "UglyToad.PdfPig.Tokens.dll", "System.Numerics.Tensors.dll",
    ];

    internal static string Create(
        string composition, string axisStage, string detector, string detectorManifest,
        string recognizer, string recognizerManifest, string provider, string optimization,
        int intraThreads, int interThreads, int workers, int queue,
        IReadOnlyDictionary<string, string> files)
    {
        if (string.IsNullOrWhiteSpace(composition) || string.IsNullOrWhiteSpace(axisStage) ||
            provider != "cpu" || optimization != "disabled" ||
            intraThreads < 1 || interThreads != 1 || workers != 1 || queue != 1)
            throw new InvalidDataException("COMPOSED_OCR_EXECUTION_CONFIGURATION_INVALID");
        foreach (string hash in new[] { detector, detectorManifest, recognizer, recognizerManifest }) ValidateSha(hash);
        var sharedFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in RequiredFiles)
        {
            if (!files.TryGetValue(name, out string? hash))
                throw new InvalidDataException("COMPOSED_OCR_EXECUTION_FILE_MISSING");
            ValidateSha(hash);
            sharedFiles.Add(name, hash);
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "graphreader.composed-ocr-execution.v1", composition, axisStage,
            detector, detectorManifest, recognizer, recognizerManifest, provider, optimization,
            intraThreads, interThreads, workers, queue, files = sharedFiles,
            framework = Environment.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static IReadOnlyDictionary<string, string> ReadRuntimeFiles(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in RequiredFiles)
        {
            using var stream = File.OpenRead(Path.Combine(directory, name));
            files.Add(name, Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
        return files;
    }

    private static void ValidateSha(string value)
    {
        if (value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("COMPOSED_OCR_EXECUTION_HASH_INVALID");
    }
}
