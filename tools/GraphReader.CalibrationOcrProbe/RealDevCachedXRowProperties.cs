// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Ocr;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string CachedXRowReportHash = "c052507d9185b7f1132c36d551592aa5b24103c5277ea3188ba795dfbfc30e87";
    private sealed record XRowTarget(int ProjectIndex, int[] Crop, int Width, int Height, int SourceWidth, int SourceHeight,
        int PlotBottom, int[] Ticks, int Left, int Top, int Right, int Bottom);

    private static XRowTarget ParseCachedXRow(byte[] bytes)
    {
        Program.RequireHash(bytes, CachedXRowReportHash);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        if (root.GetProperty("schema").GetString() != NativeRasterEndpointTraceSchema ||
            root.GetProperty("selection").GetString() != "first_12_sorted_anonymized_case_id" ||
            root.GetProperty("sample_denominator").GetInt32() != TraceSampleCount || root.GetProperty("frozen_dev_denominator").GetInt32() != Denominator ||
            root.GetProperty("attempted").GetInt32() != TraceSampleCount || root.GetProperty("unattempted").GetInt32() != 0 ||
            root.GetProperty("sealed_payload_reads").GetInt32() != 0 || root.GetProperty("truth_scoring").GetBoolean() || root.GetProperty("image_writes").GetBoolean() ||
            root.GetProperty("assignment_sha256").GetString() != CachedAxisAssignmentHash ||
            root.GetProperty("selected_inventory_sha256").GetString() != CachedAxisInventoryHash ||
            root.GetProperty("source_digest").GetString() != CachedAxisSourceDigest) throw new InvalidDataException();
        JsonElement candidate = root.GetProperty("candidate");
        if (candidate.GetProperty("app_exe_sha256").GetString() != NativeRasterEndpointAppExeHash ||
            candidate.GetProperty("app_dll_sha256").GetString() != NativeRasterEndpointAppDllHash ||
            candidate.GetProperty("rust_sha256").GetString() != NativeRasterEndpointRustHash) throw new InvalidDataException();
        JsonElement projects = root.GetProperty("projects");
        if (projects.GetArrayLength() != TraceSampleCount) throw new InvalidDataException();
        var targets = new List<XRowTarget>(); int index = 0;
        foreach (JsonElement project in projects.EnumerateArray())
        {
            if (project.GetProperty("index").GetInt32() != index || project.GetProperty("status").GetString() != "completed") throw new InvalidDataException();
            foreach (JsonElement panel in project.GetProperty("panels").EnumerateArray())
            {
                if (!panel.TryGetProperty("exact_box_tick_measurement", out JsonElement measure) || !measure.GetProperty("attempted").GetBoolean()) continue;
                if (!measure.GetProperty("bound").GetBoolean() || panel.GetProperty("stages").GetProperty("provider").GetString() != "opencv-review") throw new InvalidDataException();
                JsonElement[] rows = panel.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("role").GetString() == "XTick").ToArray();
                if (rows.Length != 1) throw new InvalidDataException();
                JsonElement rect = panel.GetProperty("crop"), geometry = panel.GetProperty("geometry"), bounds = rows[0].GetProperty("bounds");
                int[] ticks = geometry.GetProperty("x_ticks").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                if (ticks.Length is < 2 or > 128 || !ticks.Zip(ticks.Skip(1)).All(pair => pair.First < pair.Second)) throw new InvalidDataException();
                targets.Add(new(index, [ExactInt(rect.GetProperty("x")), ExactInt(rect.GetProperty("y")), ExactInt(rect.GetProperty("width")), ExactInt(rect.GetProperty("height"))],
                    panel.GetProperty("width").GetInt32(), panel.GetProperty("height").GetInt32(), panel.GetProperty("source_width").GetInt32(), panel.GetProperty("source_height").GetInt32(),
                    geometry.GetProperty("bottom").GetInt32(), ticks, ExactInt(bounds.GetProperty("left")), ExactInt(bounds.GetProperty("top")), ExactInt(bounds.GetProperty("right")), ExactInt(bounds.GetProperty("bottom"))));
            }
            index++;
        }
        return targets.Count == 1 ? targets[0] : throw new InvalidDataException();
    }

    private static async Task<object> MeasureXRowAsync(OcrImage image, XRowTarget target, CancellationToken token)
    {
        if (target.Left < 0 || target.Top <= target.PlotBottom || target.Right > image.Width || target.Bottom > image.Height || target.Left >= target.Right || target.Top >= target.Bottom ||
            target.Ticks.Any(tick => tick < 0 || tick >= image.Width)) throw new InvalidDataException();
        var detector = new ConnectedComponentTextRegionDetector(new ConnectedComponentTextRegionDetectorOptions { GroupComponentsIntoLines = false, UseObservedVerticalGroupingOrientation = true });
        IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(image, token).ConfigureAwait(false);
        var overlapping = regions.Select(region => region.Polygon.Bounds).Where(box =>
            box.Top < target.Bottom && box.Bottom > target.Top && box.Left < target.Right && box.Right > target.Left).ToArray();
        var boxes = overlapping.Where(box => box.Top > target.PlotBottom).ToArray();
        var cuts = target.Ticks.Zip(target.Ticks.Skip(1), (left, right) => new { left, right, position = (left + (double)right) / 2 }).Select(cut => new {
            cut.left, cut.right, cut.position,
            crossing_count = boxes.Count(box => box.Left < cut.position && box.Right > cut.position),
            left_tick_covered_count = boxes.Count(box => box.Left <= cut.left && box.Right > cut.left),
            right_tick_covered_count = boxes.Count(box => box.Left <= cut.right && box.Right > cut.right),
            left_side_count = boxes.Count(box => box.Right <= cut.position), right_side_count = boxes.Count(box => box.Left >= cut.position)
        }).ToArray();
        return new { predicate_scope = "diagnostic_row_overlap_approximation_not_actual_merge_veto", total_detected_components = regions.Count,
            row_overlapping_components_at_or_above_baseline = overlapping.Length - boxes.Length,
            components = boxes.Select(box => new { box.Left, box.Top, box.Right, box.Bottom }).ToArray(), cuts };
    }

    private static object? PublishXRowProperties(object? properties, bool digestVerified) => digestVerified ? properties : null;

    internal static async Task<object> RunCachedXRowPropertiesAsync(string cachedReport, string corpusRoot, string app, string assets, string rust,
        CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(cachedReport) || !Path.IsPathFullyQualified(corpusRoot) ||
            !Path.IsPathFullyQualified(app) || !Path.IsPathFullyQualified(assets) || !Path.IsPathFullyQualified(rust)) throw new InvalidDataException();
        XRowTarget target = ParseCachedXRow(Program.ReadBounded(cachedReport, 1_000_000));
        using Candidate candidate = Candidate.VerifyNativeRasterEndpoint(app, assets, rust, token);
        var reader = ResolveEmbeddedImageReader();
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.AssignmentSha256 != CachedAxisAssignmentHash || metadata.SelectedInventorySha256 != CachedAxisInventoryHash) throw new InvalidDataException();
        FrozenRealCorpusProject[] all = selector.EnumerateSelectedProjects(token).OrderBy(p => p.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (all.Length != Denominator || all.Any(p => p.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        int attempted = 0, completed = 0, failed = 0, cropAttempted = 0, cropCompleted = 0, cropFailed = 0, componentAttempted = 0, componentCompleted = 0, componentFailed = 0;
        string? blockedStage = null, blockedCategory = null; object? properties = null;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < TraceSampleCount; i++)
        {
            if (token.IsCancellationRequested) { blockedStage = "overall_deadline"; blockedCategory = "CANCELLED"; break; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            attempted++; string stage = "source_read";
            try
            {
                ImageOnlyPayload payload = ReadLockedImageOnly(selector, all[i], digest, reader, deadline.Token);
                if (i == target.ProjectIndex)
                {
                    stage = "crop_decode"; cropAttempted++;
                    if (target.SourceWidth != payload.Width || target.SourceHeight != payload.Height || target.Crop[2] != target.Width || target.Crop[3] != target.Height) throw new InvalidDataException();
                    var (image, sourceWidth, sourceHeight) = Program.Decode(payload.Image, target.Crop, deadline.Token);
                    if (sourceWidth != payload.Width || sourceHeight != payload.Height || image.Width != target.Width || image.Height != target.Height) throw new InvalidDataException();
                    cropCompleted++; stage = "component_detection"; componentAttempted++;
                    properties = await MeasureXRowAsync(image, target, deadline.Token).ConfigureAwait(false); componentCompleted++;
                }
                completed++;
            }
            catch (OperationCanceledException) { failed++; if (stage == "crop_decode") cropFailed++; else if (stage == "component_detection") componentFailed++; blockedStage = token.IsCancellationRequested ? "overall_deadline" : stage; blockedCategory = token.IsCancellationRequested ? "CANCELLED" : "SOURCE_TIMEOUT"; break; }
            catch (Exception error) when (error is not OutOfMemoryException) { failed++; if (stage == "crop_decode") cropFailed++; else if (stage == "component_detection") componentFailed++; blockedStage = stage; blockedCategory = TraceExceptionCategory(error); break; }
        }
        string actual = Convert.ToHexStringLower(digest.GetHashAndReset());
        bool verified = completed == TraceSampleCount && actual == CachedAxisSourceDigest && componentCompleted == 1;
        if (!verified && blockedStage is null) { blockedStage = "source_digest"; blockedCategory = "DIGEST_MISMATCH"; }
        return new { schema = "graphreader.cached-x-row-properties.v1", scope = "development_only_sensitive_geometry", status = verified ? "completed" : "blocked", blocked_stage = blockedStage, blocked_category = blockedCategory,
            sample_denominator = TraceSampleCount, frozen_dev_denominator = Denominator, source_attempted = attempted, source_completed = completed, source_failed = failed, source_unattempted = TraceSampleCount - attempted,
            cached_target_panels = 1, crop_attempted = cropAttempted, crop_completed = cropCompleted, crop_failed = cropFailed, crop_unattempted = 1 - cropAttempted,
            component_attempted = componentAttempted, component_completed = componentCompleted, component_failed = componentFailed, component_unattempted = 1 - componentAttempted,
            source_digest_verified = verified, properties = PublishXRowProperties(properties, verified), cached_report_sha256 = CachedXRowReportHash,
            assignment_sha256 = metadata.AssignmentSha256, selected_inventory_sha256 = metadata.SelectedInventorySha256,
            source_digest = verified ? actual : null,
            axis_calls = 0, ocr_recognition_calls = 0, truth_scoring = false, image_writes = false, sealed_payload_reads = 0 };
    }

    internal static async Task<object> CachedXRowPropertiesSelfTestAsync()
    {
        try { _ = ParseCachedXRow("tampered"u8.ToArray()); throw new InvalidOperationException(); } catch (InvalidDataException) { }
        byte[] pixels = Enumerable.Repeat((byte)255, 64 * 64).ToArray();
        for (int y = 30; y <= 35; y++) for (int x = 17; x <= 19; x++) pixels[y * 64 + x] = 0;
        for (int y = 30; y <= 35; y++) for (int x = 37; x <= 39; x++) pixels[y * 64 + x] = 0;
        for (int y = 15; y <= 20; y++) for (int x = 27; x <= 29; x++) pixels[y * 64 + x] = 0;
        for (int x = 2; x <= 61; x++) pixels[25 * 64 + x] = 0;
        foreach (int x in new[] { 18, 38 }) for (int y = 25; y <= 28; y++) pixels[y * 64 + x] = 0;
        var image = new OcrImage(64, 64, 64, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        var target = new XRowTarget(0, [0, 0, 64, 64], 64, 64, 64, 64, 25, [18, 38], 10, 28, 45, 38);
        JsonElement result = JsonSerializer.SerializeToElement(await MeasureXRowAsync(image, target, CancellationToken.None).ConfigureAwait(false), Program.Json);
        if (result.GetProperty("components").GetArrayLength() != 2 || result.GetProperty("cuts")[0].GetProperty("crossing_count").GetInt32() != 0 ||
            result.GetProperty("cuts")[0].GetProperty("left_tick_covered_count").GetInt32() != 1 || result.GetProperty("cuts")[0].GetProperty("right_tick_covered_count").GetInt32() != 1 ||
            result.GetProperty("total_detected_components").GetInt32() != 3 ||
            result.GetProperty("row_overlapping_components_at_or_above_baseline").GetInt32() != 0 ||
            PublishXRowProperties(result, false) is not null || PublishXRowProperties(result, true) is null) throw new InvalidDataException();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await MeasureXRowAsync(image, target, cancelled.Token).ConfigureAwait(false); throw new InvalidOperationException(); } catch (OperationCanceledException) { }
        return new { schema = "graphreader.cached-x-row-properties-self-test.v1", tamper_refused_before_selector = true,
            partial_properties_withheld = true, component_cut_geometry_checked = true, above_baseline_filtered = true,
            connected_full_baseline_filtered = true, cancellation_checked = true, private_reads = 0 };
    }
}
