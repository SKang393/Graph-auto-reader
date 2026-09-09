// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Replays the fixed annotation-free 960/1920 DB inputs and records
/// output-neutral pre-acceptance telemetry from the same detector invocation.
/// </summary>
internal static class DbPostprocessReplay
{
    private const string ManifestSha =
        "277d6962553da1593980e15f2188300573ff5a6fac70f8efc1ba445886e590b6";
    private const string ProtocolSha =
        "63bc5a706c610d55e3f25136c1e5083c6a84b2af70449a56a677a155169f0c1c";
    private const string ExporterSha =
        "08ebe9ee89024f2819bb29a0beb47ef3142328cf015d6cbaa6ccb45f72500f9b";
    private const string VoidReportSha =
        "c08e528d8ca9a7f099693b6b02dce7599a0b67315f3b2c9d2f10a29bbee7d6e3";
    private const string DetectorSha =
        "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb";
    private const string DetectorManifestSha =
        "2ddb1599df210da50bf097383b1f691ee6e1bed27ba9c98b6695a6b06e7e8d51";
    private const string NativeSha =
        "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";
    private const string Candidate960Sha =
        "154d40615bd54f5744e20bb5b4482b86e05f0e3d709c8ac8d794e8d3371a4e38";
    private const string Candidate1920Sha =
        "e0a485408a6ea5205b2e0473548c35e5966fea349ebb1d2fce21c4c55adb3340";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<int> RunCommandAsync(string[] args, string repositoryRoot)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            return await RunAsync(args, repositoryRoot, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                Status = "void",
                Error = exception.Message,
                ProductionApproved = false,
            }, JsonOptions));
            return exception is OperationCanceledException ? 130 : 1;
        }
    }

    private static async Task<int> RunAsync(
        string[] args,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        if (args.Length != 4 || args[0] != "--replay-db-postprocess" ||
            !string.Equals(args[2], ManifestSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only the fixed DB postprocess replay manifest is authorized.");
        }

        string artifactRoot = Path.Combine(
            repositoryRoot,
            "artifacts",
            "goal22-runs",
            "db-postprocess-replay");
        string manifestPath = Inside(artifactRoot, args[1]);
        string outputRoot = Inside(artifactRoot, args[3]);
        byte[] manifestBytes = ReadVerified(manifestPath, ManifestSha, "replay manifest");
        using var manifest = JsonDocument.Parse(manifestBytes);
        JsonElement root = manifest.RootElement;
        ValidateScope(root);
        VerifyRepositoryDescriptor(root.GetProperty("protocol"), repositoryRoot, ProtocolSha, "protocol");
        VerifyRepositoryDescriptor(root.GetProperty("exporter"), repositoryRoot, ExporterSha, "exporter");
        VerifyRepositoryDescriptor(root.GetProperty("void_repair"), repositoryRoot, VoidReportSha, "void repair");

        ValidateCandidates(
            root.GetProperty("configurations"),
            repositoryRoot);
        ValidateReportInventory(root.GetProperty("reports"), repositoryRoot);

        JsonElement detectorRecord = root.GetProperty("detector");
        string detectorPath = Path.GetFullPath(Text(detectorRecord, "model_path"));
        string detectorManifestPath = Path.GetFullPath(Text(detectorRecord, "manifest_path"));
        if (Text(detectorRecord, "model_sha256") != DetectorSha ||
            Text(detectorRecord, "manifest_sha256") != DetectorManifestSha)
        {
            throw new InvalidDataException("Detector identity differs from the fixed replay.");
        }

        byte[] detectorManifestBytes = ReadVerified(
            detectorManifestPath,
            DetectorManifestSha,
            "detector manifest");
        using var detectorManifest = JsonDocument.Parse(detectorManifestBytes);
        JsonElement detectorManifestRoot = detectorManifest.RootElement;
        if (Text(detectorManifestRoot, "model_id") != "PP-OCRv5_mobile_det" ||
            Text(detectorManifestRoot, "model_version") != "5.0.0" ||
            Text(detectorManifestRoot, "sha256") != DetectorSha)
        {
            throw new InvalidDataException("Detector manifest content differs from the fixed replay.");
        }

        JsonElement runtimeRecord = root.GetProperty("runtime");
        string nativePath = Path.GetFullPath(Text(runtimeRecord, "native_path"));
        if (Text(runtimeRecord, "native_sha256") != NativeSha ||
            Text(runtimeRecord, "native_scope") != "reviewed-source-runtime-local-diagnostic")
        {
            throw new InvalidDataException("Native runtime identity differs from the fixed replay.");
        }
        JsonElement[] notices = runtimeRecord.GetProperty("license_inputs").EnumerateArray().ToArray();
        if (notices.Length != 2)
        {
            throw new InvalidDataException("The fixed detector license inventory is incomplete.");
        }
        foreach (JsonElement notice in notices)
        {
            ReadVerified(
                Path.GetFullPath(Text(notice, "path")),
                Text(notice, "sha256"),
                "license input");
        }

        ReplayInput[] inputs = ValidateInputs(
            root.GetProperty("requests"),
            root.GetProperty("reports"),
            repositoryRoot,
            cancellationToken);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException("Use a new output directory; previous replay evidence is retained.");
        }

        using var modelLock = OpenVerifiedLock(detectorPath, DetectorSha, "detector model");
        using var nativeLock = OpenVerifiedLock(nativePath, NativeSha, "native runtime");
        nint nativeHandle = NativeLibrary.Load(nativePath);
        NativeLibrary.SetDllImportResolver(
            typeof(OpenCvSharp.Mat).Assembly,
            (name, _, _) => name == "OpenCvSharpExtern" ? nativeHandle : nint.Zero);
        Directory.CreateDirectory(outputRoot);
        await using var runtime = new ProductionInferenceRuntimeHost(
            new OrtExecutionProviderDiscovery(),
            new WindowsExecutionProviderPolicy(),
            new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance),
            CpuThreadConfiguration.Create(),
            [InferenceProvider.Cpu],
            Path.Combine(outputRoot, "cache"),
            ProductionInferenceRuntimeHost.DefaultQueueCapacity,
            ProductionInferenceRuntimeHost.DefaultWorkerCount);
        var model = new ModelIdentity(
            Text(detectorManifestRoot, "model_id"),
            Text(detectorManifestRoot, "model_version"),
            DetectorSha,
            detectorPath);

        var total = Stopwatch.StartNew();
        var outcomes = new List<object>(inputs.Length);
        var dispositionTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        var fingerprints = new Dictionary<int, string>();
        int failed = 0;
        int detectorInvocationAttempts = 0;
        int detectorInvocationCompletions = 0;
        for (var index = 0; index < inputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayInput input = inputs[index];
            var timer = Stopwatch.StartNew();
            string? error = null;
            OcrDbGeometryObservation? geometry = null;
            OcrDbPostprocessObservation? postprocess = null;
            IReadOnlyList<OcrDetectedRegion>? regions = null;
            object? maskDescriptor = null;
            object? traceDescriptor = null;
            try
            {
                LocalOnnxTextRegionDetectorOptions options =
                    ProductionOcrAdapter.ReadDetectionOptions(model, detectorManifestPath) with
                    {
                        MaximumSideLength = input.MaximumSideLength,
                        BypassCache = true,
                        DbGeometryObserver = value => geometry = value,
                        DbPostprocessObserver = value => postprocess = value,
                    };
                var detector = new LocalOnnxTextRegionDetector(runtime.Runtime, options);
                fingerprints.TryAdd(input.MaximumSideLength, detector.ConfigurationFingerprint);
                if (fingerprints[input.MaximumSideLength] != detector.ConfigurationFingerprint)
                {
                    throw new InvalidDataException("Detector fingerprint changed within one fixed configuration.");
                }

                var image = new OcrImage(
                    input.Width,
                    input.Height,
                    input.Width,
                    input.Gray,
                    OcrSourceImage.Original,
                    OcrFrameTransform.Identity,
                    OcrContract.CoordinateSpace,
                    input.Width,
                    input.Height,
                    new OcrBgrBytePixels(checked(input.Width * 3), input.Bgr));
                detectorInvocationAttempts++;
                regions = await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
                detectorInvocationCompletions++;
                ValidateOutput(input, regions, geometry, postprocess);

                string prefix = FormattableString.Invariant(
                    $"{index:D2}-{input.Split}-{input.MaximumSideLength}-{input.Kind}");
                byte[] mask = postprocess!.CopyThresholdMask();
                maskDescriptor = await WriteBytesAsync(
                    outputRoot,
                    prefix + "-threshold-mask.bin",
                    mask,
                    cancellationToken).ConfigureAwait(false);
                byte[] traceBytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Schema = "graphreader.db-postprocess-observation.v1",
                    Scope = "project-owned-synthetic-train-dev-only",
                    ProductionApproved = false,
                    Input = new
                    {
                        input.Split,
                        input.MaximumSideLength,
                        input.Kind,
                        input.SourceSha256,
                        input.PanelId,
                        input.PanelSha256,
                        input.Width,
                        input.Height,
                        GraySha256 = input.GraySha256,
                        BgrSha256 = input.BgrSha256,
                    },
                    DetectorConfigurationFingerprint = detector.ConfigurationFingerprint,
                    ThresholdMask = maskDescriptor,
                    Observation = postprocess,
                }, JsonOptions);
                traceDescriptor = await WriteBytesAsync(
                    outputRoot,
                    prefix + "-trace.json",
                    traceBytes,
                    cancellationToken).ConfigureAwait(false);
                foreach (IGrouping<OcrDbContourDisposition, OcrDbContourEvaluation> group in
                         postprocess.Contours.GroupBy(static item => item.Disposition))
                {
                    string key = group.Key.ToString();
                    dispositionTotals[key] = dispositionTotals.GetValueOrDefault(key) + group.Count();
                }
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
            {
                failed++;
                error = exception.Message;
            }
            timer.Stop();
            outcomes.Add(new
            {
                Index = index,
                input.Split,
                input.MaximumSideLength,
                input.Kind,
                input.SourceSha256,
                input.PanelId,
                input.PanelSha256,
                Status = error is null ? "parity-pass" : "failed",
                Error = error,
                ReturnedRegions = regions?.Count,
                GeometryAcceptedContours = geometry?.AcceptedContours.Count,
                PostprocessContours = postprocess?.TotalContourCount,
                PostprocessAcceptedContours = postprocess?.Contours.Count(static item =>
                    item.Disposition == OcrDbContourDisposition.Accepted),
                Mask = maskDescriptor,
                Trace = traceDescriptor,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        total.Stop();
        string reportPath = Path.Combine(outputRoot, "report.json");
        await using (var report = new FileStream(
            reportPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(report, new
            {
                Schema = "graphreader.db-postprocess-replay-report.v1",
                Scope = "project-owned-synthetic-train-dev-only",
                SyntheticOnly = true,
                PrivateReads = 0,
                SealedReads = 0,
                TruthReads = 0,
                ProductionApproved = false,
                InputManifestSha256 = ManifestSha,
                ProtocolSha256 = ProtocolSha,
                DetectorSha256 = DetectorSha,
                DetectorManifestSha256 = DetectorManifestSha,
                NativeSha256 = NativeSha,
                Assemblies = new[]
                {
                    typeof(DbPostprocessReplay).Assembly,
                    typeof(LocalOnnxTextRegionDetector).Assembly,
                    typeof(InferenceRuntime).Assembly,
                    typeof(ProductionInferenceRuntimeHost).Assembly,
                }.Distinct().Select(assembly => new
                {
                    Name = assembly.GetName().Name,
                    Sha256 = Hash(File.ReadAllBytes(assembly.Location)),
                }),
                RequestCount = inputs.Length,
                DetectorInvocationAttempts = detectorInvocationAttempts,
                DetectorInvocationCompletions = detectorInvocationCompletions,
                FailedRequestCount = failed,
                OutputParityPassed = failed == 0,
                DetectorConfigurationFingerprints = fingerprints,
                DispositionTotals = dispositionTotals,
                ElapsedMilliseconds = total.Elapsed.TotalMilliseconds,
                Requests = outcomes,
            }, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Report = reportPath,
            Requests = inputs.Length,
            FailedRequests = failed,
        }, JsonOptions));
        return failed == 0 ? 0 : 1;
    }

    private static void ValidateScope(JsonElement root)
    {
        if (Text(root, "schema") != "graphreader.db-postprocess-replay-input.v2" ||
            Text(root, "scope") != "project-owned-synthetic-train-dev-only" ||
            !root.GetProperty("synthetic_only").GetBoolean() ||
            root.GetProperty("private_data").GetBoolean() ||
            root.GetProperty("sealed_data").GetBoolean() ||
            root.GetProperty("truth_included").GetBoolean() ||
            root.GetProperty("production_approved").GetBoolean())
        {
            throw new InvalidDataException("DB postprocess replay scope is invalid.");
        }
    }

    private static void ValidateCandidates(
        JsonElement configurations,
        string repositoryRoot)
    {
        JsonElement[] records = configurations.EnumerateArray().ToArray();
        if (records.Length != 2)
        {
            throw new InvalidDataException("Both fixed detector configurations are required.");
        }
        var candidates = new Dictionary<int, JsonElement>();
        foreach (JsonElement record in records)
        {
            int side = record.GetProperty("maximum_side_length").GetInt32();
            string expected = side switch
            {
                960 => Candidate960Sha,
                1920 => Candidate1920Sha,
                _ => throw new InvalidDataException("Unexpected detector resolution."),
            };
            if (Text(record, "sha256") != expected || !candidates.TryAdd(side, record.Clone()))
            {
                throw new InvalidDataException("Candidate configuration identity is invalid.");
            }
            byte[] bytes = ReadVerified(
                RepositoryPath(repositoryRoot, Text(record, "path")),
                expected,
                "candidate");
            using var candidate = JsonDocument.Parse(bytes);
            JsonElement candidateRoot = candidate.RootElement;
            if (Text(candidateRoot, "schema") != "graphreader.local-synthetic-ocr-candidate.v1" ||
                candidateRoot.GetProperty("production_approved").GetBoolean() ||
                Text(candidateRoot, "native_sha256") != NativeSha ||
                Text(candidateRoot.GetProperty("detector"), "model_sha256") != DetectorSha ||
                (side == 960 && candidateRoot.TryGetProperty(
                    "ocr_detector_maximum_side_length", out _)) ||
                (side == 1920 && candidateRoot.GetProperty(
                    "ocr_detector_maximum_side_length").GetInt32() != 1920))
            {
                throw new InvalidDataException("Candidate configuration content is invalid.");
            }
        }
    }

    private static void ValidateReportInventory(JsonElement reports, string repositoryRoot)
    {
        JsonElement[] records = reports.EnumerateArray().ToArray();
        if (records.Length != 4 ||
            records.Count(record => Text(record, "split") == "train") != 2 ||
            records.Count(record => Text(record, "split") == "dev") != 2 ||
            records.Count(record => record.GetProperty("maximum_side_length").GetInt32() == 960) != 2 ||
            records.Count(record => record.GetProperty("maximum_side_length").GetInt32() == 1920) != 2)
        {
            throw new InvalidDataException("Fixed baseline report inventory is incomplete.");
        }
        foreach (JsonElement record in records)
        {
            ReadVerified(
                RepositoryPath(repositoryRoot, Text(record, "path")),
                Text(record, "sha256"),
                "baseline report");
        }
    }

    private static ReplayInput[] ValidateInputs(
        JsonElement requests,
        JsonElement reports,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var reportCache = reports.EnumerateArray().ToDictionary(
            record => Text(record, "path"),
            record => JsonDocument.Parse(ReadVerified(
                RepositoryPath(repositoryRoot, Text(record, "path")),
                Text(record, "sha256"),
                "baseline report")));
        try
        {
            var inputs = new List<ReplayInput>();
            foreach (JsonElement request in requests.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int width = request.GetProperty("width").GetInt32();
                int height = request.GetProperty("height").GetInt32();
                int side = request.GetProperty("maximum_side_length").GetInt32();
                string split = Text(request, "split");
                string kind = Text(request, "kind");
                if (width <= 0 || height <= 0 || side is not (960 or 1920) ||
                    split is not ("train" or "dev") || kind is not ("axis-masked" or "unmasked"))
                {
                    throw new InvalidDataException("Replay request dimensions or category are invalid.");
                }
                JsonElement grayRecord = request.GetProperty("gray");
                JsonElement bgrRecord = request.GetProperty("bgr");
                byte[] gray = ReadVerified(
                    RepositoryPath(repositoryRoot, Text(grayRecord, "path")),
                    Text(grayRecord, "sha256"),
                    "Gray8 replay input");
                byte[] bgr = ReadVerified(
                    RepositoryPath(repositoryRoot, Text(bgrRecord, "path")),
                    Text(bgrRecord, "sha256"),
                    "BGR24 replay input");
                if (gray.Length != checked(width * height) ||
                    bgr.Length != checked(width * height * 3) ||
                    grayRecord.GetProperty("byte_count").GetInt32() != gray.Length ||
                    bgrRecord.GetProperty("byte_count").GetInt32() != bgr.Length)
                {
                    throw new InvalidDataException("Replay plane dimensions are invalid.");
                }

                JsonElement reportDescriptor = request.GetProperty("baseline_report");
                string reportPath = Text(reportDescriptor, "path");
                if (!reportCache.TryGetValue(reportPath, out JsonDocument? report) ||
                    !reports.EnumerateArray().Any(record =>
                        Text(record, "path") == reportPath &&
                        Text(record, "sha256") == Text(reportDescriptor, "sha256") &&
                        Text(record, "split") == split &&
                        record.GetProperty("maximum_side_length").GetInt32() == side))
                {
                    throw new InvalidDataException("Replay request baseline report is invalid.");
                }
                JsonElement panel = FindPanel(
                    report.RootElement,
                    Text(request, "source_sha256"),
                    Text(request, "panel_id"));
                string regionKey = kind == "axis-masked" ? "model_regions" : "unmasked_model_regions";
                if (panel.GetProperty("width").GetInt32() != width ||
                    panel.GetProperty("height").GetInt32() != height ||
                    Text(panel, "image_sha256") != Text(request, "panel_sha256") ||
                    !JsonElement.DeepEquals(
                        panel.GetProperty("ocr_proposal_diagnostic").GetProperty(regionKey),
                        request.GetProperty("expected_regions")))
                {
                    throw new InvalidDataException("Replay request differs from its baseline report panel.");
                }

                JsonElement sidecarDescriptor = request.GetProperty("baseline_sidecar");
                byte[] sidecarBytes = ReadVerified(
                    RepositoryPath(repositoryRoot, Text(sidecarDescriptor, "path")),
                    Text(sidecarDescriptor, "sha256"),
                    "baseline DB observation");
                using var sidecar = JsonDocument.Parse(sidecarBytes);
                JsonElement[] matchingInvocations = sidecar.RootElement.GetProperty("invocations")
                    .EnumerateArray()
                    .Where(item => Text(item, "kind") == kind)
                    .ToArray();
                if (matchingInvocations.Length != 1 ||
                    Text(matchingInvocations[0], "canonical_gray_sha256") != Text(grayRecord, "sha256") ||
                    Text(matchingInvocations[0], "detector_bgr_sha256") != Text(bgrRecord, "sha256") ||
                    !JsonElement.DeepEquals(
                        matchingInvocations[0].GetProperty("observation"),
                        request.GetProperty("expected_observation")))
                {
                    throw new InvalidDataException("Replay request differs from its baseline DB observation.");
                }

                inputs.Add(new ReplayInput(
                    split,
                    side,
                    kind,
                    Text(request, "source_sha256"),
                    Text(request, "panel_id"),
                    Text(request, "panel_sha256"),
                    width,
                    height,
                    Text(grayRecord, "sha256"),
                    Text(bgrRecord, "sha256"),
                    gray,
                    bgr,
                    request.GetProperty("expected_regions").Clone(),
                    request.GetProperty("expected_observation").Clone()));
            }
            ReplayInput[] result = inputs.ToArray();
            if (result.Length != 52 || result.Count(static item => item.MaximumSideLength == 960) != 26 ||
                result.Count(static item => item.MaximumSideLength == 1920) != 26 ||
                result.Count(static item => item.Split == "train") != 16 ||
                result.Count(static item => item.Split == "dev") != 36 ||
                result.Count(static item => item.Kind == "axis-masked") != 26 ||
                result.Count(static item => item.Kind == "unmasked") != 26)
            {
                throw new InvalidDataException("The complete fixed 52-request replay is required.");
            }
            return result;
        }
        finally
        {
            foreach (JsonDocument report in reportCache.Values)
            {
                report.Dispose();
            }
        }
    }

    private static JsonElement FindPanel(JsonElement report, string sourceSha256, string panelId)
    {
        JsonElement[] panels = report.GetProperty("cases")
            .EnumerateArray()
            .SelectMany(static item => item.GetProperty("panels").EnumerateArray())
            .Where(item => Text(item, "source_image_sha256") == sourceSha256 &&
                           Text(item, "panel_id") == panelId)
            .ToArray();
        return panels.Length == 1
            ? panels[0]
            : throw new InvalidDataException("Replay panel is missing or duplicated in its baseline report.");
    }

    private static void ValidateOutput(
        ReplayInput input,
        IReadOnlyList<OcrDetectedRegion> regions,
        OcrDbGeometryObservation? geometry,
        OcrDbPostprocessObservation? postprocess)
    {
        if (!JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(regions, JsonOptions),
                input.ExpectedRegions) ||
            geometry is null ||
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(geometry, JsonOptions),
                input.ExpectedObservation) ||
            postprocess is null ||
            postprocess.InputSha256 != input.BgrSha256 ||
            postprocess.ImageWidth != input.Width || postprocess.ImageHeight != input.Height ||
            postprocess.TensorWidth != geometry.TensorWidth ||
            postprocess.TensorHeight != geometry.TensorHeight ||
            postprocess.Contours.Count(static item =>
                item.Disposition == OcrDbContourDisposition.Accepted) != regions.Count)
        {
            throw new InvalidDataException("Replayed detector output differs from frozen output evidence.");
        }
        OcrDbContourEvaluation[] accepted = postprocess.Contours
            .Where(static item => item.Disposition == OcrDbContourDisposition.Accepted)
            .ToArray();
        for (var index = 0; index < accepted.Length; index++)
        {
            OcrDbContourEvaluation trace = accepted[index];
            OcrDbAcceptedContourGeometry expected = geometry.AcceptedContours[index];
            if (trace.ReturnedRegionId != expected.ReturnedRegionId ||
                trace.InitialPolygon != expected.InitialPolygon ||
                trace.ExpandedPolygon != expected.ExpandedPolygon ||
                trace.BoxConfidence != expected.DetectionConfidence ||
                trace.InkDensity != expected.InkDensity)
            {
                throw new InvalidDataException("Accepted postprocess trace differs from atomic geometry.");
            }
        }
    }

    private static void VerifyRepositoryDescriptor(
        JsonElement descriptor,
        string repositoryRoot,
        string expectedSha256,
        string label)
    {
        if (Text(descriptor, "sha256") != expectedSha256)
        {
            throw new InvalidDataException($"{label} identity differs from the fixed replay.");
        }
        ReadVerified(
            RepositoryPath(repositoryRoot, Text(descriptor, "path")),
            expectedSha256,
            label);
    }

    private static FileStream OpenVerifiedLock(string path, string expectedSha256, string label)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Hash(stream) != expectedSha256)
        {
            stream.Dispose();
            throw new InvalidDataException($"{label} checksum mismatch.");
        }
        return stream;
    }

    private static async Task<object> WriteBytesAsync(
        string outputRoot,
        string name,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(outputRoot, name);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new { File = name, Sha256 = Hash(bytes), ByteCount = bytes.Length };
    }

    private static byte[] ReadVerified(string path, string expectedSha256, string label)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Hash(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} checksum mismatch: {Path.GetFileName(path)}");
        }
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()
        ?? throw new InvalidDataException("Missing string: " + key);

    private static string RepositoryPath(string repositoryRoot, string path) =>
        Inside(repositoryRoot, Path.Combine(repositoryRoot, path));

    private static string Inside(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Replay paths must remain inside their assigned root.");
        }
        return full;
    }

    private sealed record ReplayInput(
        string Split,
        int MaximumSideLength,
        string Kind,
        string SourceSha256,
        string PanelId,
        string PanelSha256,
        int Width,
        int Height,
        string GraySha256,
        string BgrSha256,
        byte[] Gray,
        byte[] Bgr,
        JsonElement ExpectedRegions,
        JsonElement ExpectedObservation);
}
