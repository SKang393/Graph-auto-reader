// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Pdf;
using OpenCvSharp;

internal static class Program
{
    private const string Root = @"E:\Graph-auto-reader";
    private const string OutputParent = "artifacts/manual-axis-ocr-diagnostic-v1/";
    private const string OpenCvHash = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Require(args.Length is 2 or 3 && args[1].StartsWith("panel-diagnostic-", StringComparison.Ordinal) &&
                args[1].All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'), "Supply a training split and new panel-diagnostic output name.");
            string profile = args.Length == 3 ? args[2] : "baseline";
            Require(profile is "baseline" or "wide", "Unknown line-provider profile.");
            Dataset dataset = args[0] switch
            {
                "build" => new("internal_train_build", "artifacts/manual-axis-ocr-diagnostic-v1/axis-input.json",
                    "ed420156153e1e5496a97b8edce657aa9e63730837c06ba07c46943d713887ae",
                    "artifacts/manual-dig-train-anchor-review-v1/images/", "train-sample-", 8),
                "holdout" => new("internal_train_holdout", "artifacts/manual-axis-ocr-diagnostic-v1/holdout-axis-input.json",
                    "c49f7693ce9401c8e90b7b196fc280d528c2a21f012c1e4a41d410a1ae4199fc",
                    "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/", "holdout-", 5),
                "fresh" => new("internal_train_fresh", "artifacts/manual-axis-ocr-diagnostic-v1/axis-fresh-input.json",
                    "bd158b9bebc87e7eaf831a0e224d78af3e3f1e9350c9f5a30587728055eb457d",
                    "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/", "fresh-", 7),
                "dev" => new("internal_dev", "artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1/manifest.json",
                    "d0adfe55915cc323ff408acf342110972eca2a92ae188ac43b8090d91054afd4",
                    "artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1/images/", "dev-image-", 16),
                _ => throw new InvalidDataException("Only pinned development or training samples are allowed."),
            };
            string native = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "OpenCvSharpExtern.dll");
            Require(File.Exists(native) && Hash(await File.ReadAllBytesAsync(native)) == OpenCvHash, "Reviewed OpenCV runtime differs.");
            NativeLibrary.SetDllImportResolver(typeof(Cv2).Assembly, (name, _, _) =>
                name == "OpenCvSharpExtern" ? NativeLibrary.Load(native) : IntPtr.Zero);
            byte[] inputBytes = await ReadPinned(dataset.Input, dataset.InputHash, 100_000);
            using JsonDocument input = JsonDocument.Parse(inputBytes);
            string expectedSchema = dataset.Split == "internal_dev" ? "graphreader.manual-dev-axis-input.v1" : "graphreader.manual-axis-geometry-input.v1";
            Require(input.RootElement.GetProperty("schema").GetString() == expectedSchema &&
                input.RootElement.GetProperty("source_split").GetString() == dataset.Split, "Input scope differs.");
            string output = ProjectPath(OutputParent + args[1]);
            Require(!Directory.Exists(output) && !File.Exists(output), "Output already exists.");
            Directory.CreateDirectory(output);
            var panelizer = new ProductionRasterPanelizer();
            var detector = new AxisGeometryDetector();
            var provider = new OpenCvLineCandidateProvider(profile == "wide"
                ? new OpenCvLineCandidateOptions { HoughMinimumLineLengthPixels = 40d }
                : null);
            var rows = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in input.RootElement.GetProperty("rows").EnumerateArray())
            {
                string id = item.GetProperty("id").GetString()!;
                Require(seen.Add(id) && id.StartsWith(dataset.IdPrefix, StringComparison.Ordinal), "Invalid image identity.");
                string path = item.GetProperty("image_path").GetString()!;
                Require(path.StartsWith(dataset.SourcePrefix, StringComparison.Ordinal), "Source is outside the pinned training capture.");
                byte[] bytes = await ReadPinned(path, item.GetProperty("image_sha256").GetString()!, 64_000_000);
                int width = item.GetProperty("width").GetInt32();
                int height = item.GetProperty("height").GetInt32();
                var panels = new List<object>();
                string? failure = null;
                try
                {
                    ProductionRasterPanelizationResult result = await panelizer.PanelizeAsync(
                        new ImmutableByteBuffer(bytes), Hash(bytes), width, height, CancellationToken.None);
                    foreach (ProductionRasterPanel panel in result.Panels)
                    {
                        byte[] crop = panel.CopyEncodedBytes();
                        panels.Add(new { panel.Order, crop = panel.EncodedCropInSourcePixels, panel.Width, panel.Height,
                            crop_sha256 = Hash(crop), fallback = false, axis = await AnalyzeAxis(crop, panel.Width, panel.Height) });
                    }
                    rows.Add(new { id, failure, panels, warnings = result.Warnings });
                }
                catch (ProductionRasterPanelizationException error)
                {
                    failure = error.Code;
                    if (error.Code == ProductionRasterPanelizationFailureCodes.NoPanelDetected)
                    {
                        panels.Add(new { Order = 1, crop = new PdfRectD(0, 0, width, height), Width = width, Height = height,
                            crop_sha256 = Hash(bytes), fallback = true, axis = await AnalyzeAxis(bytes, width, height) });
                    }
                    rows.Add(new { id, failure, panels, warnings = error.Warnings });
                }
            }
            Require(rows.Count == dataset.Count, "Unexpected training image count.");
            var report = new { schema = "graphreader.manual-panel-diagnostic.v1", scope = "development-diagnostic-not-acceptance",
                source_split = dataset.Split, profile, input_sha256 = dataset.InputHash, opencv_sha256 = OpenCvHash, optimizer_steps = 0, rows };
            string reportPath = Path.Combine(output, "report.json");
            await using (var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions);
            Console.WriteLine($"completed images={rows.Count}; optimizer_steps=0; report_sha256={Hash(await File.ReadAllBytesAsync(reportPath))}");
            return 0;

            async Task<object> AnalyzeAxis(byte[] crop, int width, int height)
            {
                byte[] pixels = DecodeGrayscale(crop, width, height);
                try
                {
                    var frame = new GrayscaleLineCandidateFrame(width, height, width, pixels);
                    IReadOnlyList<GeometryLineCandidate> lines = await provider.DetectLinesAsync(frame, CancellationToken.None);
                    var observer = new CandidateObserver();
                    var request = new AxisGeometryRequest(width, height, lines) { CandidateObserver = observer };
                    AxisGeometryResult geometry = await detector.DetectAsync(request);
                    Require(observer.Candidates is not null, "Ranked axis candidates were not captured.");
                    return new { success = true, y_axis = geometry.YAxis.Line, x_axis = geometry.XAxis.Line,
                        confidence = geometry.Confidence, needs_review = geometry.Uncertainty.NeedsReview,
                        uncertainty_reasons = geometry.Uncertainty.Reasons, warnings = geometry.Diagnostics.Warnings,
                        line_candidates = lines, ranked_candidates = observer.Candidates };
                }
                catch (AxisGeometryDetectionException error)
                {
                    return new { success = false, failure = error.Code };
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static byte[] DecodeGrayscale(byte[] bytes, int width, int height)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Require(decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth == width && decoder.Frames[0].PixelHeight == height,
            "Panel image dimensions differ.");
        var bgra = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        byte[] color = new byte[checked(width * height * 4)];
        bgra.CopyPixels(color, checked(width * 4), 0);
        byte[] pixels = new byte[checked(width * height)];
        for (int i = 0; i < pixels.Length; i++)
        {
            int offset = i * 4, alpha = color[offset + 3];
            int blue = ((color[offset] * alpha) + (255 * (255 - alpha)) + 127) / 255;
            int green = ((color[offset + 1] * alpha) + (255 * (255 - alpha)) + 127) / 255;
            int red = ((color[offset + 2] * alpha) + (255 * (255 - alpha)) + 127) / 255;
            pixels[i] = (byte)(((299 * red) + (587 * green) + (114 * blue) + 500) / 1000);
        }
        return pixels;
    }

    private static string ProjectPath(string relative)
    {
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Path escaped project root.");
        string current = Path.GetPathRoot(path)!;
        foreach (string part in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Path contains a reparse point.");
        }
        return path;
    }

    private static async Task<byte[]> ReadPinned(string relative, string expected, int limit)
    {
        string path = ProjectPath(relative);
        Require(new FileInfo(path).Length <= limit, "Pinned file exceeds its byte limit.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(bytes.Length <= limit && Hash(bytes) == expected, "Pinned file length or hash differs.");
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record Dataset(string Split, string Input, string InputHash, string SourcePrefix, string IdPrefix, int Count);

    private sealed class CandidateObserver : IAxisGeometryCandidateObserver
    {
        public IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }

        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates;
    }
}
