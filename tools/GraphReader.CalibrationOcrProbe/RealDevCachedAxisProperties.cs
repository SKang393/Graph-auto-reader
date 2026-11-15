// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using GraphReader.Ocr;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string CachedAxisReportHash = "cba00fe43ba990ff538dee9a6c7eac832dcef2eef6987ad102e03265bf8f51ff";
    private const string CachedAxisAssignmentHash = "decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a";
    private const string CachedAxisInventoryHash = "03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d";
    private const string CachedAxisSourceDigest = "4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1";
    private sealed record CachedAxisTarget(int ProjectIndex, int[] Crop, int Width, int Height,
        int SourceWidth, int SourceHeight, int Left, int Bottom, int Right, int Top);
    private sealed record ImageOnlyPayload(byte[] Image, int Width, int Height);

    private static MethodInfo ResolveEmbeddedImageReader()
    {
        MethodInfo? method = typeof(EngaugeDigWholeWorkflowTruthAdapter).GetMethod("ReadEmbeddedPng",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, [typeof(XElement), typeof(CancellationToken)], null);
        if (method is null || method.ReturnType != typeof((byte[], int, int))) throw new InvalidDataException();
        return method;
    }

    private static ImageOnlyPayload ExtractImageOnly(Stream stream, MethodInfo readerMethod, CancellationToken token)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
            MaxCharactersInDocument = EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes, MaxCharactersFromEntities = 1024,
            CloseInput = false };
        using XmlReader reader = XmlReader.Create(stream, settings);
        var active = new List<(int Depth, XElement Element)>();
        XElement? selected = null;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && (reader.LocalName.Contains("Image", StringComparison.OrdinalIgnoreCase) ||
                reader.LocalName.Contains("Raster", StringComparison.OrdinalIgnoreCase)))
            {
                var element = new XElement(reader.LocalName);
                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                        if (reader.LocalName.Equals("Width", StringComparison.OrdinalIgnoreCase) ||
                            reader.LocalName.Equals("Height", StringComparison.OrdinalIgnoreCase) ||
                            reader.LocalName.Equals("Format", StringComparison.OrdinalIgnoreCase))
                            element.Add(new XAttribute(XName.Get(reader.LocalName, reader.NamespaceURI), reader.Value));
                    reader.MoveToElement();
                }
                if (!reader.IsEmptyElement) active.Add((reader.Depth, element));
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                foreach (var item in active)
                    if (reader.Depth == item.Depth + 1) item.Element.Add(new XText(reader.Value));
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    if (active[i].Depth != reader.Depth) continue;
                    XElement element = active[i].Element;
                    active.RemoveAt(i);
                    if (!element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value))) continue;
                    if (selected is not null) throw new InvalidDataException();
                    selected = element;
                }
            }
        }
        if (selected is null) throw new InvalidDataException();
        var (bytes, width, height) = ((byte[], int, int))(readerMethod.Invoke(null, [selected, token]) ?? throw new InvalidDataException());
        return new(bytes, width, height);
    }

    private static ImageOnlyPayload ReadLockedImageOnly(FrozenDevTrainingSelector selector, FrozenRealCorpusProject project,
        IncrementalHash digest, MethodInfo readerMethod, CancellationToken token)
    {
        _ = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
        using var stream = new FileStream(project.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes) throw new InvalidDataException();
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
        string before = HashStream(stream, token);
        digest.AppendData("project"u8); digest.AppendData(Convert.FromHexString(before));
        _ = selector.RequireProject(project.GetProjectPath(), project.AnonymizedCaseId, project.AnonymizedStudyId, token);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
        stream.Position = 0;
        ImageOnlyPayload image = ExtractImageOnly(stream, readerMethod, token);
        stream.Position = 0;
        string after = HashStream(stream, token);
        EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(stream, project.GetProjectPath());
        if (before != after) throw new InvalidDataException();
        digest.AppendData("image"u8); digest.AppendData(SHA256.HashData(image.Image));
        return image;
    }

    private static List<CachedAxisTarget> ParseCachedAxisTargets(byte[] bytes, string expectedHash)
    {
        Program.RequireHash(bytes, expectedHash);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schema").GetString() != ExactBoxTickTraceSchema ||
            root.GetProperty("selection").GetString() != "first_12_sorted_anonymized_case_id" ||
            root.GetProperty("frozen_dev_denominator").GetInt32() != Denominator ||
            root.GetProperty("sample_denominator").GetInt32() != TraceSampleCount ||
            root.GetProperty("attempted").GetInt32() != TraceSampleCount || root.GetProperty("unattempted").GetInt32() != 0 ||
            root.GetProperty("sealed_payload_reads").GetInt32() != 0 || root.GetProperty("truth_scoring").GetBoolean() ||
            root.GetProperty("image_writes").GetBoolean() ||
            root.GetProperty("assignment_sha256").GetString() != CachedAxisAssignmentHash ||
            root.GetProperty("selected_inventory_sha256").GetString() != CachedAxisInventoryHash ||
            root.GetProperty("source_digest").GetString() != CachedAxisSourceDigest) throw new InvalidDataException();
        JsonElement candidate = root.GetProperty("candidate");
        if (candidate.GetProperty("app_exe_sha256").GetString() != ExactBoxTickAppExeHash ||
            candidate.GetProperty("app_dll_sha256").GetString() != ExactBoxTickAppDllHash ||
            candidate.GetProperty("rust_sha256").GetString() != ExactBoxTickRustHash) throw new InvalidDataException();
        JsonElement projects = root.GetProperty("projects");
        if (projects.GetArrayLength() != TraceSampleCount) throw new InvalidDataException();
        var targets = new List<CachedAxisTarget>(); int index = 0;
        foreach (JsonElement project in projects.EnumerateArray())
        {
            if (project.GetProperty("index").GetInt32() != index || project.GetProperty("status").GetString() != "completed") throw new InvalidDataException();
            foreach (JsonElement panel in project.GetProperty("panels").EnumerateArray())
            {
                if (!panel.TryGetProperty("exact_box_tick_measurement", out JsonElement tick) ||
                    !tick.TryGetProperty("error_code", out JsonElement error) || error.GetString() != "AXIS_BOX_NOT_OBSERVED") continue;
                if (panel.GetProperty("stages").GetProperty("provider").GetString() != "opencv-review" ||
                    !tick.GetProperty("attempted").GetBoolean()) throw new InvalidDataException();
                JsonElement crop = panel.GetProperty("crop"), geometry = panel.GetProperty("geometry");
                int[] rect = [ExactInt(crop.GetProperty("x")), ExactInt(crop.GetProperty("y")),
                    ExactInt(crop.GetProperty("width")), ExactInt(crop.GetProperty("height"))];
                targets.Add(new(index, rect, panel.GetProperty("width").GetInt32(), panel.GetProperty("height").GetInt32(),
                    panel.GetProperty("source_width").GetInt32(), panel.GetProperty("source_height").GetInt32(),
                    geometry.GetProperty("left").GetInt32(), geometry.GetProperty("bottom").GetInt32(),
                    geometry.GetProperty("right").GetInt32(), geometry.GetProperty("top").GetInt32()));
            }
            index++;
        }
        return targets;
    }

    private static int ExactInt(JsonElement value)
    {
        double number = value.GetDouble();
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < 0 || number > int.MaxValue) throw new InvalidDataException();
        return (int)number;
    }

    internal static object RunCachedAxisProperties(string cachedReport, string corpusRoot, string app, string assets,
        string rust, CpuBudgetJob budget, CancellationToken token)
    {
        if (!budget.SilentBreakawayVerified || !Path.IsPathFullyQualified(cachedReport) || !Path.IsPathFullyQualified(corpusRoot) ||
            !Path.IsPathFullyQualified(app) || !Path.IsPathFullyQualified(assets) || !Path.IsPathFullyQualified(rust)) throw new InvalidDataException();
        IReadOnlyList<CachedAxisTarget> targets = ParseCachedAxisTargets(Program.ReadBounded(cachedReport, 1_000_000), CachedAxisReportHash);
        using Candidate candidate = Candidate.VerifyExactBoxTick(app, assets, rust, token);
        MethodInfo imageReader = ResolveEmbeddedImageReader();
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, token);
        FrozenDevTrainingMetadata metadata = selector.Describe();
        if (metadata.AssignmentSha256 != CachedAxisAssignmentHash || metadata.SelectedInventorySha256 != CachedAxisInventoryHash) throw new InvalidDataException();
        FrozenRealCorpusProject[] all = selector.EnumerateSelectedProjects(token).OrderBy(p => p.AnonymizedCaseId, StringComparer.Ordinal).ToArray();
        if (all.Length != Denominator || all.Any(p => p.Split != FrozenRealCorpusInventory.RealDev)) throw new InvalidDataException();
        var properties = new List<object>();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int attempted = 0, completed = 0, failed = 0, cropAttempts = 0, cropCompleted = 0, cropFailed = 0;
        int propertyAttempts = 0, propertyCompleted = 0, propertyFailed = 0;
        string? blockedStage = null, blockedCategory = null;
        for (int i = 0; i < TraceSampleCount; i++)
        {
            if (token.IsCancellationRequested) { blockedStage = "overall_deadline"; blockedCategory = "CANCELLED"; break; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            attempted++;
            string stage = "source_read";
            try
            {
                ImageOnlyPayload payload = ReadLockedImageOnly(selector, all[i], digest, imageReader, deadline.Token);
                foreach (CachedAxisTarget target in targets.Where(item => item.ProjectIndex == i))
                {
                    stage = "crop_decode"; cropAttempts++;
                    if (target.SourceWidth != payload.Width || target.SourceHeight != payload.Height ||
                        target.Crop[2] != target.Width || target.Crop[3] != target.Height) throw new InvalidDataException();
                    var (image, sourceWidth, sourceHeight) = Program.Decode(payload.Image, target.Crop, deadline.Token);
                    if (sourceWidth != payload.Width || sourceHeight != payload.Height || image.Width != target.Width || image.Height != target.Height) throw new InvalidDataException();
                    cropCompleted++; stage = "property_measurement"; propertyAttempts++;
                    properties.Add(MeasureCachedAxisProperties(image, target.Left, target.Bottom, target.Right, target.Top));
                    propertyCompleted++;
                }
                completed++;
            }
            catch (OperationCanceledException)
            {
                failed++; if (stage == "crop_decode") cropFailed++; else if (stage == "property_measurement") propertyFailed++;
                blockedStage = token.IsCancellationRequested ? "overall_deadline" : stage;
                blockedCategory = token.IsCancellationRequested ? "CANCELLED" : "SOURCE_TIMEOUT";
                break;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failed++; if (stage == "crop_decode") cropFailed++; else if (stage == "property_measurement") propertyFailed++;
                blockedStage = stage; blockedCategory = TraceExceptionCategory(error); break;
            }
        }
        string actualDigest = Convert.ToHexStringLower(digest.GetHashAndReset());
        bool digestVerified = completed == TraceSampleCount && actualDigest == CachedAxisSourceDigest && cropCompleted == targets.Count;
        if (!digestVerified && blockedStage is null) { blockedStage = "source_digest"; blockedCategory = "DIGEST_MISMATCH"; }
        IReadOnlyList<object> published = PublishCachedAxisProperties(properties, digestVerified);
        return new { schema = "graphreader.cached-axis-pixel-properties.v2", scope = "development_only_image_properties",
            status = digestVerified ? "completed" : "blocked", blocked_stage = blockedStage, blocked_category = blockedCategory,
            selection = "first_12_sorted_anonymized_case_id", frozen_dev_denominator = Denominator, sample_denominator = TraceSampleCount,
            source_attempted = attempted, source_completed = completed, source_failed = failed, source_unattempted = TraceSampleCount - attempted,
            cached_failed_sources = targets.Select(item => item.ProjectIndex).Distinct().Count(), cached_failed_panels = targets.Count,
            crop_attempted = cropAttempts, crop_completed = cropCompleted, crop_failed = cropFailed, crop_unattempted = targets.Count - cropAttempts,
            property_attempted = propertyAttempts, property_completed = propertyCompleted, property_failed = propertyFailed,
            property_results = published.Count, properties = published, source_digest_verified = digestVerified,
            assignment_sha256 = metadata.AssignmentSha256, selected_inventory_sha256 = metadata.SelectedInventorySha256,
            source_digest = digestVerified ? actualDigest : null, cached_report_sha256 = CachedAxisReportHash,
            axis_calls = 0, ocr_calls = 0, truth_scoring = false, image_writes = false, sealed_payload_reads = 0 };
    }

    private static IReadOnlyList<object> PublishCachedAxisProperties(IReadOnlyList<object> properties, bool digestVerified) =>
        digestVerified ? properties : Array.Empty<object>();

    private static object MeasureCachedAxisProperties(OcrImage image, int left, int bottom, int right, int top)
    {
        if (left < 0 || top < 0 || right >= image.Width || bottom >= image.Height || left >= right || top >= bottom ||
            image.Stride < image.Width || image.Pixels.Length < image.Stride * image.Height) throw new InvalidDataException();
        byte[] pixels = image.Pixels.ToArray();
        bool Dark(int x, int y) => pixels[y * image.Stride + x] < 128;
        byte Gray8(int x, int y) => pixels[y * image.Stride + x];
        object Line(bool horizontal, int offset)
        {
            int fixedPosition = (horizontal ? bottom : left) + offset;
            int limit = horizontal ? image.Height : image.Width;
            if (fixedPosition < 0 || fixedPosition >= limit)
                return new { offset, available = false, dark_count = (int?)null, total = (int?)null,
                    longest_white_run = (int?)null, gray8_histogram_nonzero_bins = (object[]?)null };
            int dark = 0, run = 0, longest = 0, start = horizontal ? left : top, end = horizontal ? right : bottom;
            var histogram = new int[256];
            for (int p = start; p <= end; p++)
            {
                byte intensity = horizontal ? Gray8(p, fixedPosition) : Gray8(fixedPosition, p);
                histogram[intensity]++;
                if (intensity < 128) { dark++; run = 0; }
                else { run++; longest = Math.Max(longest, run); }
            }
            object[] nonzeroBins = histogram.Select((count, intensity) => new { intensity, count })
                .Where(bin => bin.count > 0).Cast<object>().ToArray();
            return new { offset, available = true, dark_count = (int?)dark, total = (int?)(end - start + 1),
                longest_white_run = (int?)longest, gray8_histogram_nonzero_bins = (object[]?)nonzeroBins };
        }
        // Legacy v1 flags remain unchanged: *_dark means intensity < 128, while longest_white_run counts intensity >= 128.
        return new { origin_dark = Dark(left, bottom), x_end_dark = Dark(right, bottom), y_end_dark = Dark(left, top),
            origin_gray8 = Gray8(left, bottom), x_end_gray8 = Gray8(right, bottom), y_end_gray8 = Gray8(left, top),
            horizontal = Enumerable.Range(-2, 5).Select(offset => Line(true, offset)).ToArray(),
            vertical = Enumerable.Range(-2, 5).Select(offset => Line(false, offset)).ToArray() };
    }

    internal static object CachedAxisPropertiesSelfTest()
    {
        byte[] bogus = "owned-tampered-report"u8.ToArray();
        try { _ = ParseCachedAxisTargets(bogus, new string('0', 64)); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        object[] partial = [new { owned = true }];
        if (PublishCachedAxisProperties(partial, false).Count != 0 || PublishCachedAxisProperties(partial, true).Count != 1) throw new InvalidDataException();
        // Draw a two-pixel horizontal band one pixel above the supplied baseline.
        // Crop once from the larger original, then address only panel coordinates.
        byte[] pixels = Enumerable.Repeat((byte)255, 40 * 40).ToArray();
        for (int x = 6; x <= 25; x++) { pixels[(5 + 18) * 40 + 4 + x] = 0; pixels[(5 + 19) * 40 + 4 + x] = 0; }
        for (int y = 3; y <= 19; y++) pixels[(5 + y) * 40 + 4 + 6] = 0;
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
            40, 40, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, pixels, 40)));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] png = stream.ToArray();
        string xml = "<!DOCTYPE Engauge><Engauge><Image Format=\"PNG\" Width=\"40\" Height=\"40\">" +
            Convert.ToBase64String(png) + "</Image><Point><PositionGraph>ignored</PositionGraph></Point></Engauge>";
        using var xmlStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml), writable: false);
        ImageOnlyPayload extracted = ExtractImageOnly(xmlStream, ResolveEmbeddedImageReader(), CancellationToken.None);
        if (extracted.Width != 40 || extracted.Height != 40 || !extracted.Image.SequenceEqual(png)) throw new InvalidDataException();
        var (image, width, height) = Program.Decode(extracted.Image, [4, 5, 32, 32]);
        if (width != 40 || height != 40 || image.Width != 32 || image.Height != 32) throw new InvalidDataException();
        JsonElement result = JsonSerializer.SerializeToElement(MeasureCachedAxisProperties(image, 6, 20, 25, 3), Program.Json);
        JsonElement rows = result.GetProperty("horizontal");
        if (result.GetProperty("origin_dark").GetBoolean() || result.GetProperty("x_end_dark").GetBoolean() ||
            !result.GetProperty("y_end_dark").GetBoolean() || rows[2].GetProperty("dark_count").GetInt32() != 0 ||
            rows[1].GetProperty("dark_count").GetInt32() != 20 || rows[0].GetProperty("dark_count").GetInt32() != 20 ||
            rows[2].GetProperty("longest_white_run").GetInt32() != 20) throw new InvalidDataException();

        byte[] grayPixels = Enumerable.Repeat((byte)255, 32 * 32).ToArray();
        grayPixels[20 * 32 + 5] = 128;
        grayPixels[20 * 32 + 15] = 192;
        grayPixels[5 * 32 + 5] = 192;
        var grayImage = new OcrImage(32, 32, 32, grayPixels, OcrSourceImage.Original,
            OcrFrameTransform.Identity);
        JsonElement grayResult = JsonSerializer.SerializeToElement(
            MeasureCachedAxisProperties(grayImage, 5, 20, 15, 5), Program.Json);
        JsonElement baseline = grayResult.GetProperty("horizontal")[2];
        JsonElement histogram = baseline.GetProperty("gray8_histogram_nonzero_bins");
        var observedBins = histogram.EnumerateArray().ToDictionary(
            bin => bin.GetProperty("intensity").GetInt32(), bin => bin.GetProperty("count").GetInt32());
        if (grayResult.GetProperty("origin_gray8").GetByte() != 128 ||
            grayResult.GetProperty("x_end_gray8").GetByte() != 192 ||
            grayResult.GetProperty("y_end_gray8").GetByte() != 192 ||
            grayResult.GetProperty("origin_dark").GetBoolean() ||
            grayResult.GetProperty("x_end_dark").GetBoolean() ||
            histogram.GetArrayLength() != 3 || baseline.GetProperty("total").GetInt32() != 11 ||
            observedBins.GetValueOrDefault(128) != 1 || observedBins.GetValueOrDefault(192) != 1 ||
            observedBins.GetValueOrDefault(255) != 9 || observedBins.Values.Sum() != 11)
            throw new InvalidDataException();
        return new { schema = "graphreader.cached-axis-pixel-properties-self-test.v2", tamper_refused_before_selector = true,
            image_only_xml_checked = true, white_offset_and_thick_band_checked = true, crop_offset_once_checked = true,
            partial_properties_withheld = true, gray8_endpoint_intensities_distinguished = true,
            complete_histogram_bins_and_totals_checked = true, private_reads = 0 };
    }
}
