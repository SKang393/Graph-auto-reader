// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static class TickProposalCheck
{
    internal static async Task<object> RunAsync(Program.ProbeRequest source, string binding,
        string appExe, string assetRoot, string rustExe, CancellationToken token)
    {
        if (source.Scope != "owned-synthetic-review-only" || source.SourceCropXywh is not { Length: 4 } crop ||
            !Path.IsPathFullyQualified(source.ImagePath) || !Path.IsPathFullyQualified(appExe) ||
            !Path.IsPathFullyQualified(rustExe) || !Path.IsPathFullyQualified(assetRoot) ||
            !File.Exists(appExe) || !File.Exists(rustExe) || !Directory.Exists(assetRoot))
            throw new InvalidDataException("An explicit owned image and absolute worker paths are required.");
        var total = Stopwatch.StartNew();
        byte[] original = Program.ReadBounded(source.ImagePath, 64 * 1024 * 1024);
        Program.RequireHash(original, source.ImageSha256);
        var (image, _, _) = Program.Decode(original, crop, token);
        byte[] panel;
        using (var input = new MemoryStream(original, writable: false))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new InvalidDataException("Expected one owned image frame.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(decoder.Frames[0], new Int32Rect(crop[0], crop[1], crop[2], crop[3]))));
            using var output = new MemoryStream();
            encoder.Save(output);
            panel = output.ToArray();
        }
        var stage = Stopwatch.StartNew();
        RustAxisProposalResult axis = await new RustAxisProposalService(rustExe).SuggestAsync(panel, token);
        double geometryMilliseconds = stage.Elapsed.TotalMilliseconds;
        if (axis.Geometry is not { } geometry)
            return new { review_only = true, axis.ErrorCode, geometry_milliseconds = geometryMilliseconds };
        var request = new CalibrationOcrRequest("tick-proposal-check", binding, source.ImageSha256,
            Program.Hash(panel), image.Width, image.Height,
            new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
        { MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null };
        // Use detected geometry, never the known plot in an older diagnostic request.
        await using var reader = new CalibrationOcrWorkerClient(appExe, assetRoot);
        CalibrationOcrResponse response = await reader.SuggestAsync(request, panel, token);
        if (response.FailureCode is not null) throw new InvalidDataException(response.FailureCode);
        stage.Restart();
        CalibrationTickProposalResult proposal = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        double pairingMilliseconds = stage.Elapsed.TotalMilliseconds;
        return new
        {
            review_only = true, production_approved = false, applied = false,
            position_evidence = proposal.UsesEstimatedPositions ? "label-position-estimate" : "measured-ticks",
            source_sha256 = source.ImageSha256, source_crop_xywh = crop,
            coordinate_space = "original_panel_pixels", panel_sha256 = request.PanelImageSha256,
            geometry, geometry_milliseconds = geometryMilliseconds,
            candidate_count = response.Proposals.Count,
            numeric_candidate_count = response.Proposals.Count(row => row.NumericLiteral),
            proposal, pairing_milliseconds = pairingMilliseconds,
            ocr_timings = response.Timings, runtime = response.Runtime,
            total_milliseconds = total.Elapsed.TotalMilliseconds,
        };
    }
}
