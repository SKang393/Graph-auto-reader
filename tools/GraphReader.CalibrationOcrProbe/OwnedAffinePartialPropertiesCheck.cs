// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal sealed record OwnedRetainedSpanPreflight(bool Passed, string FrameSha256, string DerivedSha256,
        int Width, int Height, int Left, int Top, int Right, int Bottom, byte OriginGray, byte XEndGray,
        bool XSpanObserved, string XSpanKind, int XSpanWork, int YFirstRawGapLength, int MaximumGapLength);

    internal static OwnedRetainedSpanPreflight CheckOwnedRetainedSpan(byte[] image, RustAffineAxisGeometry axes, CancellationToken token)
    {
        var prepared = CalibrationWorkingFrame.Create(image, 640, 480, axes, "owned-indexed-fixture-preflight", token);
        CalibrationWorkingFrame frame = prepared.Frame;
        byte[] gray = DecodePropertyGray(prepared.Image, frame.Width, frame.Height, token);
        RustAxisGeometry box = frame.Geometry;
        SpanReplay replay = ReplayHorizontalSpan(gray, frame.Width, frame.Height, box, token);
        byte origin = gray[box.Bottom * frame.Width + box.Left], end = gray[box.Bottom * frame.Width + box.Right];
        int index = box.Top;
        while (index <= box.Bottom && gray[index * frame.Width + box.Left] < 200) { token.ThrowIfCancellationRequested(); index++; }
        int start = index;
        while (index <= box.Bottom && gray[index * frame.Width + box.Left] >= 200) { token.ThrowIfCancellationRequested(); index++; }
        int maximumGap = replay.MaxSpan + 4;
        bool passed = origin < 200 && end < 200 && replay.Observed && start > box.Top && index <= box.Bottom && index - start > maximumGap;
        return new(passed, Program.Hash(JsonSerializer.SerializeToUtf8Bytes(axes, Program.Json)), Program.Hash(prepared.Image),
            frame.Width, frame.Height, box.Left, box.Top, box.Right, box.Bottom, origin, end,
            replay.Observed, replay.Kind, replay.Work, index - start, maximumGap);
    }

    private const string PartialPropertySourceHash = "afdd99f8af2d548cc46b2ee8cf01603ead200f222f73d09f3f52d6f162984d56";
    private const string PartialPropertyResultHash = "f1a7c77f8c899d084fd1b52594fdc49d27416c00baeb2c5d4ff740c622eed391";
    private const string PartialPropertyDerivedHash = "7bf7a8007ec73ef6b0cbfa3eaac9e42c30f209fed2b7e25686e17787284162f0";
    private const string PartialPropertyRuntimeHash = "795c2d3bee675747a0a4d3d61fbf446a0f18ad62d80b0baea30689d59319ea2a";
    private static readonly string[] PartialPropertyStages = ["runtime", "inputs", "rectification", "properties", "preservation"];

    internal static Task<(object Report, bool Completed)> RunOwnedAffinePartialPropertiesAsync(
        string imagePath, string resultPath, CpuBudgetJob budget, CancellationToken token) => Task.Run(() =>
    {
        var timer = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        var stages = PartialPropertyStages.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        string stage = "runtime";
        string? failure = null, runtimeHash = null, derivedHash = null;
        object? properties = null;
        bool? preserved = null;
        void Finish() { stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds; step.Restart(); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!budget.SilentBreakawayVerified || budget.MaximumLogicalCores > .5) throw new InvalidDataException("CPU_BUDGET");
            runtimeHash = PartialPropertyRuntimeFingerprint();
            if (runtimeHash != PartialPropertyRuntimeHash) throw new InvalidDataException("RUNTIME_MISMATCH");
            Finish(); stage = "inputs";
            byte[] original = Program.ReadBounded(imagePath, 131_072), previous = Program.ReadBounded(resultPath, 1_000_000);
            Program.RequireHash(original, PartialPropertySourceHash); Program.RequireHash(previous, PartialPropertyResultHash);
            using JsonDocument cached = JsonDocument.Parse(previous);
            JsonElement receipt = cached.RootElement.GetProperty("ocr_request");
            if (receipt.GetProperty("source_file_sha256").GetString() != PartialPropertySourceHash ||
                receipt.GetProperty("panel_image_sha256").GetString() != PartialPropertyDerivedHash)
                throw new InvalidDataException("CACHED_BINDING");
            Matrix matrix = Matrix.Identity; matrix.RotateAt(2, 320, 240);
            RustAxisPoint Map(double x, double y) { Point p = matrix.Transform(new Point(x, y)); return new(p.X, p.Y); }
            var axes = new RustAffineAxisGeometry(Map(100, 380), Map(580, 380), Map(100, 80), 0);
            Finish(); stage = "rectification";
            var prepared = CalibrationWorkingFrame.Create(original, 640, 480, axes, "owned-property-only", token);
            CalibrationWorkingFrame frame = prepared.Frame;
            derivedHash = Program.Hash(prepared.Image);
            JsonElement plot = receipt.GetProperty("plot");
            if (derivedHash != PartialPropertyDerivedHash || frame.Width != receipt.GetProperty("width").GetInt32() ||
                frame.Height != receipt.GetProperty("height").GetInt32() || frame.Geometry.Left != plot.GetProperty("left").GetInt32() ||
                frame.Geometry.Right != plot.GetProperty("right").GetInt32() || frame.Geometry.Top != plot.GetProperty("top").GetInt32() ||
                frame.Geometry.Bottom != plot.GetProperty("bottom").GetInt32()) throw new InvalidDataException("DERIVED_REPLAY_MISMATCH");
            Finish(); stage = "properties";
            byte[] sourceGray = DecodePropertyGray(original, 640, 480, token);
            byte[] gray = DecodePropertyGray(prepared.Image, frame.Width, frame.Height, token);
            RustAxisGeometry box = frame.Geometry;
            SpanReplay x = ReplayHorizontalSpan(gray, frame.Width, frame.Height, box, token);
            int y = box.Top;
            while (y <= box.Bottom && gray[y * frame.Width + box.Left] < 200) { token.ThrowIfCancellationRequested(); y++; }
            int gapStart = y;
            while (y <= box.Bottom && gray[y * frame.Width + box.Left] >= 200) { token.ThrowIfCancellationRequested(); y++; }
            int maxSpan = Math.Max(24, (int)Math.Ceiling(Math.Min(Math.Min(frame.Width, frame.Height) * .08, 80.0)));
            properties = new
            {
                original_sample_convention = "Nearest indexed pixel center: Math.Round(ToEven); positive-weight bilinear lattice samples also listed, not re-rendered",
                original_origin = OriginalPropertySample(sourceGray, axes.Origin),
                original_x_end = OriginalPropertySample(sourceGray, axes.XEnd),
                original_y_end = OriginalPropertySample(sourceGray, axes.YEnd),
                derived_sample_convention = "Exact indexed integer pixel centers (Left,Bottom), (Right,Bottom), (Left,Top)",
                derived_origin_gray = gray[box.Bottom * frame.Width + box.Left],
                derived_x_end_gray = gray[box.Bottom * frame.Width + box.Right],
                derived_y_end_gray = gray[box.Top * frame.Width + box.Left],
                origin_observed = gray[box.Bottom * frame.Width + box.Left] < 200,
                x_span_replay = x,
                y_first_raw_gap = new { found = gapStart <= box.Bottom, length = y - gapStart,
                    terminal = gapStart <= box.Bottom && (gapStart == box.Top || y > box.Bottom),
                    exceeds_existing_gap_limit = y - gapStart > maxSpan + 4, maximum_span = maxSpan,
                    complete_vertical_bridge_replay_performed = false },
                unchanged_clip = new { box.Left, box.Top, box.Right, box.Bottom },
                ink_predicate = "Gray8 < 200; unchanged clipped X replay; no counterfactual window",
            };
            Finish(); stage = "preservation";
            preserved = Program.Hash(Program.ReadBounded(imagePath, 131_072)) == PartialPropertySourceHash &&
                Program.Hash(Program.ReadBounded(resultPath, 1_000_000)) == PartialPropertyResultHash &&
                PartialPropertyRuntimeFingerprint() == PartialPropertyRuntimeHash;
            if (preserved != true) throw new InvalidDataException("PRESERVATION_MISMATCH");
            token.ThrowIfCancellationRequested(); Finish();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is OperationCanceledException ? "CANCELLED" : error is InvalidDataException ? error.Message : error.GetType().Name;
            stages[stage] = "failed"; timings[stage] = step.Elapsed.TotalMilliseconds;
        }
        object report = new
        {
            schema = "graphreader.owned-affine-partial-properties.v1", status = failure is null ? "completed" : "failed", failure,
            declared_cases = 1, completed_cases = failure is null ? 1 : 0, failed_cases = failure is null ? 0 : 1,
            stages, stage_counts = new { completed = stages.Values.Count(value => value == "completed"),
                failed = stages.Values.Count(value => value == "failed"), unattempted = stages.Values.Count(value => value == "unattempted") },
            timings, source_sha256 = PartialPropertySourceHash, cached_result_sha256 = PartialPropertyResultHash,
            derived_sha256 = derivedHash, runtime_fingerprint = runtimeHash, properties, inputs_and_runtime_preserved = preserved,
            rectification_calls = stages["rectification"] == "unattempted" ? 0 : 1,
            worker_calls = 0, ocr_calls = 0, detector_calls = 0, image_writes = false, private_reads = 0,
            budget.MaximumLogicalCores, deadline_seconds = 30, elapsed_milliseconds = timer.Elapsed.TotalMilliseconds,
        };
        return (report, failure is null);
    }, CancellationToken.None);

    private static string PartialPropertyRuntimeFingerprint()
    {
        string root = AppContext.BaseDirectory;
        string[] names = OwnedAffineRuntimeNames.Order(StringComparer.Ordinal).ToArray();
        if (names.Length != 20) throw new InvalidDataException("RUNTIME_COUNT");
        var hashes = names.ToDictionary(name => name, name => Program.Hash(Program.ReadBounded(Path.Combine(root, name), 128 * 1024 * 1024)), StringComparer.Ordinal);
        foreach (var assembly in new[] { typeof(CalibrationWorkingFrame).Assembly, typeof(GraphReader.Imaging.AffineRasterRectifier).Assembly })
            Program.RequireHash(Program.ReadBounded(assembly.Location, 16_777_216), hashes[assembly.GetName().Name + ".dll"]);
        return Program.Hash(Encoding.UTF8.GetBytes(string.Concat(names.Select(name => name + "\t" + hashes[name] + "\n"))));
    }

    private static byte[] DecodePropertyGray(byte[] png, int width, int height, CancellationToken token)
    {
        using var stream = new MemoryStream(png, writable: false);
        BitmapFrame decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (decoded.PixelWidth != width || decoded.PixelHeight != height) throw new InvalidDataException("DECODE_DIMENSIONS");
        var gray = new FormatConvertedBitmap(WhiteBackgroundRaster.Normalize(decoded, token), PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[checked(width * height)]; gray.CopyPixels(pixels, width, 0); return pixels;
    }

    private static object OriginalPropertySample(byte[] source, RustAxisPoint point)
    {
        int nearestX = (int)Math.Round(point.X), nearestY = (int)Math.Round(point.Y);
        int left = (int)Math.Floor(point.X), top = (int)Math.Floor(point.Y);
        double dx = point.X - left, dy = point.Y - top;
        var lattice = new List<object>();
        for (int row = 0; row <= 1; row++) for (int column = 0; column <= 1; column++)
        {
            double weight = (column == 0 ? 1 - dx : dx) * (row == 0 ? 1 - dy : dy);
            if (weight > 0) lattice.Add(new { x = left + column, y = top + row, weight, gray = source[(top + row) * 640 + left + column] });
        }
        return new { point.X, point.Y, nearest_x = nearestX, nearest_y = nearestY, nearest_gray = source[nearestY * 640 + nearestX], positive_weight_lattice = lattice };
    }
}
