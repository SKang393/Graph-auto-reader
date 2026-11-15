// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static class EnteredSessionWorkerCheck
{
    private static readonly double[] ExpectedValues = [0, 5, 10];
    internal static async Task<object> RunAsync(Program.ProbeRequest source, string binding,
        string appExe, string assetRoot, string rustExe, CancellationToken token)
    {
        if (source.Scope != "owned-synthetic-review-only" || source.SourceCropXywh is not [0, 0, 640, 480] ||
            !Path.IsPathFullyQualified(source.ImagePath) || !Path.IsPathFullyQualified(appExe) ||
            !Path.IsPathFullyQualified(rustExe) || !Path.IsPathFullyQualified(assetRoot) ||
            !File.Exists(appExe) || !File.Exists(rustExe) || !Directory.Exists(assetRoot))
            throw new InvalidDataException("Expected an explicit owned 640 by 480 input and absolute worker paths.");
        var total = Stopwatch.StartNew();
        byte[] image = Program.ReadBounded(source.ImagePath, 10_240);
        Program.RequireHash(image, source.ImageSha256);
        var (decoded, sourceWidth, sourceHeight) = Program.Decode(image, source.SourceCropXywh, token);
        if (sourceWidth != 640 || sourceHeight != 480 || decoded.Width != 640 || decoded.Height != 480)
            throw new InvalidDataException("Owned fixture dimensions differ.");
        var stage = Stopwatch.StartNew();
        RustAxisProposalResult axis = await new RustAxisProposalService(rustExe).SuggestAsync(image, token).ConfigureAwait(false);
        double geometryMs = stage.Elapsed.TotalMilliseconds;
        if (axis.Geometry is not { } geometry) throw new InvalidDataException("Owned axis geometry unavailable: " + axis.ErrorCode);
        var request = new CalibrationOcrRequest("entered-session-owned", binding, source.ImageSha256,
            Program.Hash(image), decoded.Width, decoded.Height, new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
        {
            MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null,
            MeasuredYTicks = geometry.YTickPositions.Count > 0 ? geometry.YTickPositions.ToArray() : null,
        };
        await using var reader = new CalibrationOcrWorkerClient(appExe, assetRoot, () => CalibrationOcrDeviceSelection.Cpu);
        stage.Restart();
        CalibrationOcrResponse response = await reader.SuggestAsync(request, image, token).ConfigureAwait(false);
        double ocrWallMs = stage.Elapsed.TotalMilliseconds;
        if (response.FailureCode is not null) throw new InvalidDataException("Owned OCR failed: " + response.FailureCode);
        var evidence = response.Proposals.Select(row => new CalibrationTickFitEvidence(row, null)).ToArray();
        stage.Restart();
        CalibrationTickProposalResult automatic = CalibrationTickProposal.CreateReviewDraft(geometry, response);
        CalibrationTickProposalResult entered = CalibrationTickProposal.CreateReviewDraftForFirstSession(geometry, response, evidence, 1);
        double fitMs = stage.Elapsed.TotalMilliseconds;
        // The values below are owned-control assertions after inference, not model inputs.
        if (automatic.IsComplete || automatic.FirstSession is not null || !automatic.Reasons.Contains(CalibrationTickProposalReasons.InvalidRange) ||
            !entered.IsComplete || entered.FirstSession != 1 || entered.FinalSession != 10 || entered.YMaximum != 10 ||
            !entered.UsesUserEnteredSessionPosition || entered.PairedTicks.Count != 6 ||
            entered.PairedTicks.Count(pair => pair.Axis == "X") != 3 || entered.PairedTicks.Count(pair => pair.Axis == "Y") != 3 ||
            entered.Anchors is not { } anchors || Math.Abs(anchors.FirstSessionY0.X - 180) > 1e-8 ||
            anchors.FirstSessionY0.Y != 380 || anchors.FinalSessionY0.X != 540 ||
            !entered.PairedTicks.Where(pair => pair.Axis == "X").Select(pair => pair.Value).Order().SequenceEqual(ExpectedValues) ||
            !entered.PairedTicks.Where(pair => pair.Axis == "Y").Select(pair => pair.Value).Order().SequenceEqual(ExpectedValues))
            throw new InvalidDataException("Owned entered-session assertion failed.");
        return new { schema = "graphreader.entered-session-worker-check.v1", scope = source.Scope,
            source_sha256 = source.ImageSha256, panel_sha256 = request.PanelImageSha256,
            inference_plot_from = "rust_detected_original_panel_pixels", geometry, measured_x_ticks = request.MeasuredXTicks,
            measured_y_ticks = request.MeasuredYTicks, automatic, entered, raw_proposals = response.Proposals,
            geometry_milliseconds = geometryMs, ocr_wall_milliseconds = ocrWallMs, ocr_timings = response.Timings,
            runtime = response.Runtime, fit_milliseconds = fitMs, total_milliseconds = total.Elapsed.TotalMilliseconds,
            user_selected_first_session = 1, review_only = true, applied = false, accuracy_claim = false,
            private_inputs = false, production_approved = false };
    }
}
