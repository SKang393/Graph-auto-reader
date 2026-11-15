// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static class CornerTickWorkerCheck
{
    internal static async Task<object> RunAsync(string appExe, string assetRoot, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(appExe) || !Path.IsPathFullyQualified(assetRoot) ||
            AssemblyName.GetAssemblyName(Path.ChangeExtension(appExe, ".dll")).Version != new Version(0, 4, 51, 0) ||
            GraphTextRoleClassifier.Version != "graph-text-role-classifier-v12-bottom-corner-tick-lanes")
            throw new InvalidDataException("This owned control requires the actual 0.4.51 runtime.");
        string root = Path.GetDirectoryName(appExe)!;
        foreach (Assembly assembly in new[] { typeof(CalibrationTickProposal).Assembly, typeof(GraphTextRoleClassifier).Assembly })
            if (FileHash(assembly.Location) != FileHash(Path.Combine(root, assembly.GetName().Name + ".dll")))
                throw new InvalidDataException("Probe and packaged calibration assemblies differ.");
        Program.RequireHash(Program.ReadBounded(Path.Combine(assetRoot, "en_PP-OCRv5_mobile_rec.onnx"), 64 * 1024 * 1024), Program.ModelHash);
        Program.RequireHash(Program.ReadBounded(Path.Combine(assetRoot, "recognizer.json"), 131_072), Program.ManifestHash);

        // The same owned glyph pixels are placed at the same coordinates in both panels.
        // Only the right-hand axis extent and white canvas width change.
        Glyph[] glyphs = [RenderGlyph("1"), RenderGlyph("2"), RenderGlyph("3"),
            RenderGlyph("10"), RenderGlyph("5"), RenderGlyph("0")];
        var controls = new List<object>();
        bool allComplete = true;
        var total = Stopwatch.StartNew();
        await using var reader = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        foreach ((string name, int width, int right) in new[] { ("narrow", 640, 580), ("wide", 1160, 1100) })
        {
            token.ThrowIfCancellationRequested();
            byte[] encoded = Panel(width, right, glyphs);
            string hash = Program.Hash(encoded);
            var geometry = new RustAxisGeometry(100, 380, right, 80, 0)
            { XTickPositions = [140, 340, 540], YTickPositions = [80, 230, 380] };
            var request = new CalibrationOcrRequest("owned-corner-" + name, hash, hash, hash,
                width, 450, new(100, 80, right, 380)) { MeasuredXTicks = [140, 340, 540] };
            CalibrationOcrResponse response = await reader.SuggestAsync(request, encoded, token).ConfigureAwait(false);
            if (response.FailureCode is not null)
                throw new InvalidDataException("Owned corner worker: " + response.FailureCode);
            if (response.Runtime is not { } runtime ||
                runtime.ModelSha256 != Program.ModelHash || runtime.ManifestSha256 != Program.ManifestHash || runtime.Provider != "Cpu")
                throw new InvalidDataException("Owned corner worker did not return the reviewed CPU reader.");
            foreach (CalibrationOcrRuntimeFile file in runtime.Files)
            {
                string filename = file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file.Name : file.Name + ".dll";
                if (file.Sha256 != FileHash(Path.Combine(root, filename)))
                    throw new InvalidDataException("Worker runtime differs from the selected package.");
            }
            CalibrationOcrProposal[] zeroRows = response.Proposals.Where(row => row.NumericLiteral && row.NumericValue == 0).ToArray();
            CalibrationOcrProposal? zero = zeroRows.Length == 1 ? zeroRows[0] : null;
            bool reproducedCorner = zero is not null && zero.Polygon.Max(p => p.X) < 100 &&
                zero.Polygon.Min(p => p.Y) < 380 && zero.Polygon.Max(p => p.Y) > 380 && zero.Polygon.Average(p => p.Y) > 380 &&
                zero.Polygon.Average(p => p.X) < 100 - 480 * .05 && zero.Polygon.Average(p => p.X) >= 100 - 1000 * .05;
            CalibrationTickProposalResult proposal = CalibrationTickProposal.Create(geometry, response);
            bool complete = reproducedCorner && zero!.SuggestedAxis == "Y" && zero.Role == "YTick" &&
                response.Proposals.Count == 6 && response.Proposals.All(row => row.NumericLiteral && row.FailureCode is null) &&
                proposal.IsComplete && proposal.PairedTicks.Count == 6 && !proposal.UsesEstimatedPositions &&
                proposal.FirstSession == 1 && proposal.FinalSession == 3 && proposal.YMaximum == 10;
            allComplete &= complete;
            controls.Add(new { control = name, width, height = 450, plot = request.Plot, image_sha256 = hash,
                encoded_bytes = encoded.Length, corner_reproduced = reproducedCorner, complete,
                // SuggestedAxis is produced by the worker's pre-recognition NumericExpected classifier.
                corner_row = zero, rows = response.Proposals, proposal, response.Timings, response.Runtime });
        }
        return new { schema = "graphreader.owned-corner-tick-worker-check.v1", status = allComplete ? "completed" : "owned_control_incomplete",
            candidate_version = "0.4.51", app_exe_sha256 = FileHash(appExe), app_dll_sha256 = FileHash(Path.ChangeExtension(appExe, ".dll")),
            classifier_version = GraphTextRoleClassifier.Version, scope = "owned-synthetic-review-only", controls,
            geometry_source = "explicit-owned-fixture-not-axis-detection", production_approved = false, applied = false,
            accuracy_claim = false, image_writes = false, private_payload_reads = 0, total_milliseconds = total.Elapsed.TotalMilliseconds };
    }

    private sealed record Glyph(int Width, int Height, byte[] Pixels);

    private static Glyph RenderGlyph(string text)
    {
        const int side = 64;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, side, side));
            drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 16, Brushes.Black, 1), new Point(8, 8));
        }
        var rendered = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var gray = new FormatConvertedBitmap(rendered, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[side * side]; gray.CopyPixels(pixels, side, 0);
        int left = side, top = side, right = -1, bottom = -1;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                if (pixels[y * side + x] < 255)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left || bottom < top) throw new InvalidDataException("Owned glyph did not render.");
        int width = right - left + 1, height = bottom - top + 1;
        byte[] trimmed = new byte[width * height];
        for (int y = 0; y < height; y++) pixels.AsSpan((top + y) * side + left, width).CopyTo(trimmed.AsSpan(y * width));
        return new(width, height, trimmed);
    }

    private static byte[] Panel(int width, int right, Glyph[] glyphs)
    {
        const int height = 450;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 100; x <= right; x++) pixels[380 * width + x] = 0;
        for (int y = 80; y <= 380; y++) pixels[y * width + 100] = 0;
        int[] xs = [140, 340, 540], ys = [80, 230, 380];
        for (int i = 0; i < 3; i++)
        {
            for (int y = 381; y <= 384; y++) pixels[y * width + xs[i]] = 0;
            for (int x = 96; x < 100; x++) pixels[ys[i] * width + x] = 0;
            Place(glyphs[i], xs[i], 406);
            Place(glyphs[i + 3], 74, ys[i] + (i == 2 ? 1 : 0));
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();

        void Place(Glyph glyph, int centerX, int centerY)
        {
            int left = centerX - glyph.Width / 2, top = centerY - glyph.Height / 2;
            for (int y = 0; y < glyph.Height; y++)
                glyph.Pixels.AsSpan(y * glyph.Width, glyph.Width).CopyTo(pixels.AsSpan((top + y) * width + left, glyph.Width));
        }
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
