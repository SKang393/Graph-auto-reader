// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.Axis;
using OpenCvSharp;

internal static class Program
{
    private const string Root = @"E:\Graph-auto-reader";
    private const string InputPath = "artifacts/manual-axis-ocr-diagnostic-v1/axis-input.json";
    private const string OutputPath = "artifacts/manual-axis-ocr-diagnostic-v1/automatic-axis.json";
    private const string OpenCvHash = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            string native = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "OpenCvSharpExtern.dll");
            Require(File.Exists(native) && Hash(await File.ReadAllBytesAsync(native)) == OpenCvHash, "Reviewed OpenCV runtime differs.");
            NativeLibrary.SetDllImportResolver(typeof(Cv2).Assembly, (name, _, _) =>
                name == "OpenCvSharpExtern" ? NativeLibrary.Load(native) : IntPtr.Zero);
            if (args.Length == 1 && args[0] == "--self-test") return await SelfTest();
            string inputRelative = InputPath;
            string outputRelative = OutputPath;
            string expectedInput;
            if (args.Length == 2 && args[0] == "--expected-input-sha256")
            {
                expectedInput = args[1];
            }
            else
            {
                Require(args.Length == 6 && args[0] == "--input" && args[2] == "--expected-input-sha256" && args[4] == "--output",
                    "Expected project-local input, hash, and output are required.");
                inputRelative = args[1];
                expectedInput = args[3];
                outputRelative = args[5];
            }
            const string diagnosticRoot = "artifacts/manual-axis-ocr-diagnostic-v1/";
            Require(inputRelative.StartsWith(diagnosticRoot, StringComparison.Ordinal) &&
                outputRelative.StartsWith(diagnosticRoot, StringComparison.Ordinal), "Diagnostic paths escaped their task directory.");
            string taskDirectory = ProjectPath(diagnosticRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(ProjectPath(inputRelative).StartsWith(taskDirectory, StringComparison.OrdinalIgnoreCase), "Input escaped its task directory.");
            string output = ProjectPath(outputRelative);
            Require(output.StartsWith(taskDirectory, StringComparison.OrdinalIgnoreCase), "Output escaped its task directory.");
            Require(!File.Exists(output), "Output already exists.");
            byte[] inputBytes = await ReadPinned(inputRelative, expectedInput, 100_000);
            using JsonDocument input = JsonDocument.Parse(inputBytes);
            string? sourceSplit = input.RootElement.GetProperty("source_split").GetString();
            Require(input.RootElement.GetProperty("schema").GetString() == "graphreader.manual-axis-geometry-input.v1" &&
                (sourceSplit is "internal_train_build" or "internal_train_holdout"), "Input scope differs.");
            var results = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var detector = new AxisGeometryDetector();
            var provider = new OpenCvLineCandidateProvider();
            foreach (JsonElement row in input.RootElement.GetProperty("rows").EnumerateArray())
            {
                string id = row.GetProperty("id").GetString()!;
                Require(seen.Add(id), "Duplicate image identity.");
                string imagePath = row.GetProperty("image_path").GetString()!;
                string imageHash = row.GetProperty("image_sha256").GetString()!;
                Require(imagePath.StartsWith("artifacts/manual-dig-train-anchor-review-v1/images/", StringComparison.Ordinal) ||
                    imagePath.StartsWith("artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/", StringComparison.Ordinal),
                    "Image is outside the selected training review.");
                byte[] bytes = await ReadPinned(imagePath, imageHash, 64_000_000);
                int width = row.GetProperty("width").GetInt32();
                int height = row.GetProperty("height").GetInt32();
                byte[] pixels = DecodeGrayscale(bytes, width, height);
                try
                {
                    AxisGeometryResult result = await detector.DetectAsync(
                        new GrayscaleLineCandidateFrame(width, height, width, pixels), provider);
                    results.Add(new { id, success = true, y_axis = result.YAxis.Line, x_axis = result.XAxis.Line,
                        plot = result.PlotPolygon, result.Confidence, result.Uncertainty.NeedsReview,
                        warnings = result.Diagnostics.Warnings });
                }
                catch (AxisGeometryDetectionException error)
                {
                    results.Add(new { id, success = false, failure = error.Code });
                }
            }
            Require(results.Count is > 0 and <= 20, "Unexpected training sample count.");
            var report = new { schema = "graphreader.manual-axis-geometry-result.v1",
                scope = "train-only-no-calibration-input-not-acceptance", input_sha256 = Hash(inputBytes),
                source_split = sourceSplit, opencv_sha256 = OpenCvHash, optimizer_steps = 0, rows = results };
            await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions);
            Console.WriteLine($"completed images={results.Count}; optimizer_steps=0");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> SelfTest()
    {
        const int width = 120;
        const int height = 100;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 10; x <= 105; x++) pixels[(85 * width) + x] = 0;
        for (int y = 10; y <= 85; y++) pixels[(y * width) + 10] = 0;
        AxisGeometryResult result = await new AxisGeometryDetector().DetectAsync(
            new GrayscaleLineCandidateFrame(width, height, width, pixels), new OpenCvLineCandidateProvider());
        Require(Math.Abs(result.PlotPolygon.BottomLeft.X - 10) < 5 && Math.Abs(result.PlotPolygon.BottomLeft.Y - 85) < 5,
            "Synthetic axis origin differs.");
        Console.WriteLine("PASS: reviewed OpenCV runtime and synthetic axis detection");
        return 0;
    }

    private static byte[] DecodeGrayscale(byte[] bytes, int width, int height)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Require(decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth == width && decoder.Frames[0].PixelHeight == height,
            "Image decode differs from source identity.");
        var bgra = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        byte[] color = new byte[checked(width * height * 4)];
        bgra.CopyPixels(color, checked(width * 4), 0);
        byte[] pixels = new byte[checked(width * height)];
        for (int i = 0; i < pixels.Length; i++)
        {
            int offset = i * 4;
            int alpha = color[offset + 3];
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
        string volume = Path.GetPathRoot(path) ?? throw new InvalidDataException("Path volume missing.");
        string current = volume;
        foreach (string part in Path.GetRelativePath(volume, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
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
}
