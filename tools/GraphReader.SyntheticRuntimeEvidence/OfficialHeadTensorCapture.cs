// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Captures the exact production PP-OCRv5 detector input tensor through a
/// model-free inference session. Synthetic truth is deliberately absent.
/// </summary>
internal static class OfficialHeadTensorCapture
{
    private const string RequestSchema = "graphreader.official-head-tensor-capture-request.v1";
    private const string ReportSchema = "graphreader.official-head-tensor-capture-report.v1";
    private const string Scope = "project-owned-synthetic-train-dev-model-free";
    private const string DetectorSha =
        "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb";
    private const int MaximumSideLength = 960;
    private const int DimensionMultiple = 128;
    private const string DetectorConfigurationFingerprint =
        "7a8eb59f3b6980096e80247a6b195e25b3e0b887240e700aaf77cf5a1d64bc81";

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
                ModelInference = false,
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
        if (args.Length != 4 || args[0] != "--capture-official-head-tensors")
        {
            throw new InvalidDataException(
                "Usage: --capture-official-head-tensors <request.json> <request-sha256> <new-output-directory>");
        }

        string root = Path.GetFullPath(repositoryRoot);
        string requestPath = Inside(root, args[1]);
        string requestSha = RequireSha(args[2], "request");
        string outputRoot = Inside(root, args[3]);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException("Use a new output directory; previous tensor evidence is retained.");
        }

        byte[] requestBytes = ReadVerified(requestPath, requestSha, null, "capture request");
        using var requestDocument = JsonDocument.Parse(requestBytes);
        JsonElement request = requestDocument.RootElement;
        ValidateRequestScope(request);
        string captureSourceSha = VerifyRepositoryDescriptor(
            request.GetProperty("capture_source"), root, "tensor capture source");
        JsonElement[] executionAssemblies = VerifyExecutionAssemblies(request, root);
        VerifyRepositoryDescriptor(request.GetProperty("binding"), root, "V3 binding");
        (JsonElement candidate, string candidateSha) = ReadCandidate(request, root);
        (ModelIdentity model, string detectorManifestPath, string detectorManifestSha) =
            ReadDetector(request, candidate, root);
        (string nativePath, string nativeSha) = ReadNative(request, candidate);
        VerifyLicenses(request, candidate);
        Dictionary<string, ReportBinding> reports = ReadReports(request, root, candidateSha);
        CaptureInput[] panels = ReadPanels(request, reports, root);

        cancellationToken.ThrowIfCancellationRequested();
        string nativeActual = Hash(File.ReadAllBytes(nativePath));
        if (!string.Equals(nativeActual, nativeSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native runtime checksum changed.");
        }
        nint nativeHandle = NativeLibrary.Load(nativePath);
        NativeLibrary.SetDllImportResolver(
            typeof(OpenCvSharp.Mat).Assembly,
            (name, _, _) => name == "OpenCvSharpExtern" ? nativeHandle : nint.Zero);

        Directory.CreateDirectory(outputRoot);
        var factory = new CaptureSessionFactory();
        await using var registry = new OnnxSessionRegistry(
            new CpuDiscovery(),
            new WindowsExecutionProviderPolicy(),
            factory,
            CpuThreadConfiguration.Create(1));
        await using var scheduler = new BoundedInferenceScheduler(capacity: 2, workerCount: 1);
        await using var runtime = new InferenceRuntime(
            registry,
            scheduler,
            new ContentAddressedStageCache(Path.Combine(outputRoot, "cache")));
        LocalOnnxTextRegionDetectorOptions options =
            ProductionOcrAdapter.ReadDetectionOptions(model, detectorManifestPath) with
            {
                BypassCache = true,
                AllowedProviders = [InferenceProvider.Cpu],
            };
        if (options.MaximumSideLength != MaximumSideLength ||
            options.DimensionMultiple != DimensionMultiple ||
            options.InputColorMode != OcrTensorColorMode.Bgr ||
            options.InputLayout != OcrTensorLayout.ChannelsFirst ||
            options.InputChannels != 3 ||
            options.PostprocessAlgorithm != OcrDetectionPostprocessAlgorithm.DbPostprocessV1)
        {
            throw new InvalidDataException("Detector manifest is not the fixed production DB tensor contract.");
        }
        var detector = new LocalOnnxTextRegionDetector(runtime, options);
        if (Text(request, "detector_configuration_fingerprint") !=
                DetectorConfigurationFingerprint ||
            detector.ConfigurationFingerprint != DetectorConfigurationFingerprint)
        {
            throw new InvalidDataException(
                "Detector configuration fingerprint differs from the fixed production input contract.");
        }

        var outputs = new List<object>(panels.Length);
        var total = Stopwatch.StartNew();
        foreach (CaptureInput panel in panels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            byte[] encoded = ReadVerified(
                panel.PanelPngPath,
                panel.PanelPngSha256,
                panel.PanelPngByteCount,
                "panel PNG");
            ProductionDecodedRaster decoded = new ProductionRasterFrameDecoder().Decode(
                CreateDetectionRequest(panel, encoded), cancellationToken);
            OcrImage image = decoded.CreateOcrImage();
            string graySha = Hash(image.Pixels.ToArray());
            string bgrSha = Hash(image.BgrPixels?.Pixels.ToArray() ?? []);
            if (graySha != panel.RecordedUnmaskedGraySha256 ||
                bgrSha != panel.ReconstructedBgrSha256)
            {
                throw new InvalidDataException(
                    "Production-decoded original pixels differ from authenticated request evidence.");
            }

            int before = factory.CaptureCount;
            IReadOnlyList<OcrDetectedRegion> regions =
                await detector.DetectAsync(image, cancellationToken).ConfigureAwait(false);
            if (regions.Count != 0 || factory.CaptureCount != before + 1)
            {
                throw new InvalidDataException("Capture-only detector execution was not output-neutral.");
            }
            InferenceInput captured = factory.LastInput ?? throw new InvalidDataException(
                "Capture-only inference session returned no tensor.");
            long[] shape = captured.Shape.ToArray();
            if (shape.Length != 4 || shape[0] != 1 || shape[1] != 3 ||
                shape[2] <= 0 || shape[3] <= 0 ||
                shape[2] % DimensionMultiple != 0 || shape[3] % DimensionMultiple != 0 ||
                shape[2] * shape[3] != captured.Values.Length / 3)
            {
                throw new InvalidDataException("Captured tensor shape differs from production NCHW DB input.");
            }
            float[] values = captured.Values.ToArray();
            if (values.Any(static value => !float.IsFinite(value)))
            {
                throw new InvalidDataException("Captured tensor contains non-finite values.");
            }
            byte[] tensorBytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
            if (!BitConverter.IsLittleEndian)
            {
                throw new PlatformNotSupportedException("Tensor capture requires little-endian float32 bytes.");
            }
            string splitDirectory = Path.Combine(outputRoot, panel.Split);
            Directory.CreateDirectory(splitDirectory);
            string tensorFile = Path.Combine(panel.Split, panel.PanelId + ".f32");
            string tensorPath = Path.Combine(outputRoot, tensorFile);
            await WriteNewAsync(tensorPath, tensorBytes, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            outputs.Add(new
            {
                panel.Split,
                panel.SourceSha256,
                panel.PanelId,
                panel.PanelSha256,
                panel.Width,
                panel.Height,
                Crop = panel.Crop,
                RecordedUnmaskedGraySha256 = graySha,
                ReconstructedBgrSha256 = bgrSha,
                BgrIdentityKind = "reconstructed_from_authenticated_panel_png",
                DetectorConfigurationFingerprint = detector.ConfigurationFingerprint,
                Tensor = new
                {
                    File = tensorFile.Replace(Path.DirectorySeparatorChar, '/'),
                    Sha256 = Hash(tensorBytes),
                    ByteCount = tensorBytes.Length,
                    Shape = shape,
                    Dtype = "float32-le",
                    captured.InputName,
                    captured.OutputName,
                },
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        total.Stop();
        string reportPath = Path.Combine(outputRoot, "report.json");
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = ReportSchema,
            Scope,
            SyntheticOnly = true,
            PrivateData = false,
            SealedData = false,
            TruthUsedByCapture = false,
            ModelInference = false,
            TrainingInputReady = false,
            ProductionApproved = false,
            Request = new
            {
                Path = Path.GetRelativePath(root, requestPath).Replace(Path.DirectorySeparatorChar, '/'),
                Sha256 = requestSha,
            },
            BindingSha256 = Text(request.GetProperty("binding"), "sha256"),
            CandidateSha256 = candidateSha,
            DetectorModelSha256 = DetectorSha,
            DetectorManifestSha256 = detectorManifestSha,
            NativeSha256 = nativeSha,
            CaptureSourceSha256 = captureSourceSha,
            MaximumSideLength,
            DimensionMultiple,
            PanelCount = panels.Length,
            FailedPanelCount = 0,
            DetectorConfigurationFingerprint = detector.ConfigurationFingerprint,
            Assemblies = executionAssemblies,
            ElapsedMilliseconds = total.Elapsed.TotalMilliseconds,
            Panels = outputs,
        }, JsonOptions);
        await WriteNewAsync(reportPath, reportBytes, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Report = reportPath,
            PanelCount = panels.Length,
            ModelInference = false,
        }, JsonOptions));
        return 0;
    }

    private static void ValidateRequestScope(JsonElement root)
    {
        RequireProperties(root,
            "schema", "scope", "synthetic_only", "private_data", "sealed_data",
            "truth_included", "model_inference", "training_input_ready", "production_approved",
            "capture_source", "assemblies",
            "binding", "candidate", "detector", "native", "license_inputs",
            "maximum_side_length", "dimension_multiple", "detector_configuration_fingerprint",
            "reports", "panels");
        if (Text(root, "schema") != RequestSchema || Text(root, "scope") != Scope ||
            !root.GetProperty("synthetic_only").GetBoolean() ||
            root.GetProperty("private_data").GetBoolean() ||
            root.GetProperty("sealed_data").GetBoolean() ||
            root.GetProperty("truth_included").GetBoolean() ||
            root.GetProperty("model_inference").GetBoolean() ||
            root.GetProperty("training_input_ready").GetBoolean() ||
            root.GetProperty("production_approved").GetBoolean() ||
            root.GetProperty("maximum_side_length").GetInt32() != MaximumSideLength ||
            root.GetProperty("dimension_multiple").GetInt32() != DimensionMultiple)
        {
            throw new InvalidDataException("Official-head capture request scope is invalid.");
        }
    }

    private static (JsonElement Candidate, string Sha256) ReadCandidate(
        JsonElement request,
        string root)
    {
        JsonElement descriptor = request.GetProperty("candidate");
        string sha = RequireSha(Text(descriptor, "sha256"), "candidate");
        byte[] bytes = ReadVerified(RepositoryPath(root, Text(descriptor, "path")), sha, null, "candidate");
        using var document = JsonDocument.Parse(bytes);
        JsonElement candidate = document.RootElement.Clone();
        if (Text(candidate, "schema") != "graphreader.local-synthetic-ocr-candidate.v1" ||
            candidate.GetProperty("production_approved").GetBoolean())
        {
            throw new InvalidDataException("Capture requires the fixed unapproved synthetic candidate.");
        }
        return (candidate, sha);
    }

    private static (ModelIdentity Model, string ManifestPath, string ManifestSha) ReadDetector(
        JsonElement request,
        JsonElement candidate,
        string root)
    {
        JsonElement detector = request.GetProperty("detector");
        JsonElement expected = candidate.GetProperty("detector");
        foreach (string key in new[]
        {
            "model_path", "model_id", "model_version", "model_sha256", "manifest_sha256",
        })
        {
            if (Text(detector, key) != Text(expected, key))
            {
                throw new InvalidDataException("Detector request differs from candidate identity.");
            }
        }
        string manifestPath = RepositoryPath(root, Text(detector, "manifest_path"));
        if (!string.Equals(
                manifestPath,
                Path.GetFullPath(Text(expected, "manifest_path")),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Detector manifest path differs from candidate identity.");
        }
        string modelSha = RequireSha(Text(detector, "model_sha256"), "detector model");
        if (modelSha != DetectorSha || Text(detector, "model_id") != "PP-OCRv5_mobile_det" ||
            Text(detector, "model_version") != "5.0.0")
        {
            throw new InvalidDataException("Capture detector is not the pinned PP-OCRv5 detector.");
        }
        string manifestSha = RequireSha(Text(detector, "manifest_sha256"), "detector manifest");
        ReadVerified(manifestPath, manifestSha, null, "detector manifest");
        return (new ModelIdentity(
            Text(detector, "model_id"),
            Text(detector, "model_version"),
            modelSha,
            Text(detector, "model_path")), manifestPath, manifestSha);
    }

    private static (string Path, string Sha256) ReadNative(JsonElement request, JsonElement candidate)
    {
        JsonElement native = request.GetProperty("native");
        if (Text(native, "path") != Text(candidate, "native_path") ||
            Text(native, "sha256") != Text(candidate, "native_sha256") ||
            Text(native, "scope") != Text(candidate, "native_scope") ||
            Text(native, "scope") != "reviewed-source-runtime-local-diagnostic")
        {
            throw new InvalidDataException("Native capture identity differs from candidate identity.");
        }
        return (Path.GetFullPath(Text(native, "path")), RequireSha(Text(native, "sha256"), "native"));
    }

    private static void VerifyLicenses(JsonElement request, JsonElement candidate)
    {
        JsonElement requested = request.GetProperty("license_inputs");
        JsonElement expected = candidate.GetProperty("license_inputs");
        if (!JsonElement.DeepEquals(requested, expected) || requested.GetArrayLength() != 2)
        {
            throw new InvalidDataException("Detector license evidence is incomplete.");
        }
        foreach (JsonElement item in requested.EnumerateArray())
        {
            ReadVerified(
                Path.GetFullPath(Text(item, "path")),
                RequireSha(Text(item, "sha256"), "license input"),
                null,
                "license input");
        }
    }

    private static Dictionary<string, ReportBinding> ReadReports(
        JsonElement request,
        string root,
        string candidateSha)
    {
        JsonElement[] records = request.GetProperty("reports").EnumerateArray().ToArray();
        if (records.Length != 6 || records.Count(static item => Text(item, "split") == "train") != 5 ||
            records.Count(static item => Text(item, "split") == "validation") != 1)
        {
            throw new InvalidDataException("Capture requires all six frozen V3 runtime exchanges.");
        }
        var output = new Dictionary<string, ReportBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement record in records)
        {
            string manifestPath = RepositoryPath(root, Text(record, "manifest_path"));
            string manifestSha = RequireSha(Text(record, "manifest_sha256"), "source manifest");
            ReadVerified(manifestPath, manifestSha, null, "source manifest");
            string reportPath = RepositoryPath(root, Text(record, "report_path"));
            string reportSha = RequireSha(Text(record, "report_sha256"), "runtime report");
            byte[] reportBytes = ReadVerified(reportPath, reportSha, null, "runtime report");
            using var reportDocument = JsonDocument.Parse(reportBytes);
            JsonElement report = reportDocument.RootElement.Clone();
            if (Text(report, "schema") != "graphreader.synthetic-runtime-seed-evidence.v2" ||
                Text(report, "scope") != "local-synthetic-seed-diagnostic" ||
                report.GetProperty("production_approved").GetBoolean() ||
                Text(report, "input_manifest_sha256") != manifestSha ||
                Text(report, "candidate_sha256") != candidateSha ||
                report.GetProperty("failed_panels").GetInt32() != 0 ||
                report.GetProperty("completed_panels").GetInt32() != report.GetProperty("panel_count").GetInt32())
            {
                throw new InvalidDataException("Runtime report is not complete fixed V3 evidence.");
            }
            if (!output.TryAdd(reportPath, new ReportBinding(
                    Text(record, "split"), reportPath, reportSha, manifestSha, report)))
            {
                throw new InvalidDataException("Capture request repeats a runtime report.");
            }
        }
        return output;
    }

    private static CaptureInput[] ReadPanels(
        JsonElement request,
        IReadOnlyDictionary<string, ReportBinding> reports,
        string root)
    {
        JsonElement[] records = request.GetProperty("panels").EnumerateArray().ToArray();
        if (records.Length != 37 || records.Count(static item => Text(item, "split") == "train") != 28 ||
            records.Count(static item => Text(item, "split") == "validation") != 9)
        {
            throw new InvalidDataException("Capture requires the complete 28/9 V3 panel inventory.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<CaptureInput>(records.Length);
        foreach (JsonElement record in records)
        {
            string split = Text(record, "split");
            string sourceSha = RequireSha(Text(record, "source_sha256"), "source");
            string panelId = Text(record, "panel_id");
            if (!Guid.TryParseExact(panelId, "D", out Guid guid) || guid == Guid.Empty || !ids.Add(panelId))
            {
                throw new InvalidDataException("Capture panel GUID is invalid or duplicated.");
            }
            string panelSha = RequireSha(Text(record, "panel_sha256"), "panel");
            int width = record.GetProperty("width").GetInt32();
            int height = record.GetProperty("height").GetInt32();
            int[] crop = record.GetProperty("crop").EnumerateArray().Select(static value => value.GetInt32()).ToArray();
            if (width <= 0 || height <= 0 || crop.Length != 4 || crop[2] != width || crop[3] != height)
            {
                throw new InvalidDataException("Capture panel dimensions or crop are invalid.");
            }
            string reportPath = RepositoryPath(root, Text(record, "report_path"));
            if (!reports.TryGetValue(reportPath, out ReportBinding? report) ||
                report.Split != split || report.Sha256 != Text(record, "report_sha256"))
            {
                throw new InvalidDataException("Capture panel report binding changed.");
            }
            JsonElement reportPanel = FindPanel(report.Document, sourceSha, panelId);
            JsonElement reportCrop = reportPanel.GetProperty("crop");
            if (Text(reportPanel, "status") != "seed-completed" ||
                Text(reportPanel, "image_sha256") != panelSha ||
                reportPanel.GetProperty("width").GetInt32() != width ||
                reportPanel.GetProperty("height").GetInt32() != height ||
                reportCrop.GetProperty("x").GetInt32() != crop[0] ||
                reportCrop.GetProperty("y").GetInt32() != crop[1] ||
                reportCrop.GetProperty("width").GetInt32() != crop[2] ||
                reportCrop.GetProperty("height").GetInt32() != crop[3])
            {
                throw new InvalidDataException("Capture panel differs from its runtime report.");
            }
            string recordedGray = RequireSha(
                Text(reportPanel.GetProperty("ocr_proposal_diagnostic"), "unmasked_input_sha256"),
                "recorded unmasked Gray8");
            if (recordedGray != Text(record, "recorded_unmasked_gray_sha256"))
            {
                throw new InvalidDataException("Capture Gray8 identity differs from runtime OCR evidence.");
            }
            if (Text(record, "bgr_identity_kind") != "reconstructed_from_authenticated_panel_png")
            {
                throw new InvalidDataException("Capture BGR identity provenance is invalid.");
            }
            JsonElement png = record.GetProperty("panel_png");
            JsonElement reportPng = reportPanel.GetProperty("panel_png");
            string canonicalPng = Path.Combine(
                Path.GetDirectoryName(reportPath)!, sourceSha, panelId, Text(reportPng, "file"));
            string pngPath = RepositoryPath(root, Text(png, "path"));
            int pngBytes = png.GetProperty("byte_count").GetInt32();
            string pngSha = RequireSha(Text(png, "sha256"), "panel PNG");
            if (!string.Equals(pngPath, canonicalPng, StringComparison.OrdinalIgnoreCase) ||
                pngSha != Text(reportPng, "sha256") || pngBytes != reportPng.GetProperty("byte_count").GetInt32())
            {
                throw new InvalidDataException("Capture panel PNG differs from runtime report evidence.");
            }
            output.Add(new CaptureInput(
                split, sourceSha, panelId, panelSha, width, height, crop,
                pngPath, pngSha, pngBytes, recordedGray,
                RequireSha(Text(record, "reconstructed_bgr_sha256"), "reconstructed BGR24")));
        }
        return output.OrderBy(static item => item.Split, StringComparer.Ordinal)
            .ThenBy(static item => item.PanelId, StringComparer.Ordinal).ToArray();
    }

    private static JsonElement FindPanel(JsonElement report, string sourceSha, string panelId)
    {
        JsonElement[] matches = report.GetProperty("cases").EnumerateArray()
            .Where(item => Text(item, "image_sha256") == sourceSha)
            .SelectMany(static item => item.GetProperty("panels").EnumerateArray())
            .Where(item => Text(item, "panel_id") == panelId)
            .ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(
            "Capture panel is missing or duplicated in its runtime report.");
    }

    private static ProductionWorkflowDetectionRequest CreateDetectionRequest(
        CaptureInput panel,
        byte[] encoded)
    {
        var image = new WorkflowImageEvidence(
            panel.PanelPngPath,
            panel.PanelSha256,
            panel.Width,
            panel.Height,
            WorkflowImageVariant.Original);
        Guid panelId = Guid.ParseExact(panel.PanelId, "D");
        Guid sourceId = new(Convert.FromHexString(panel.SourceSha256).AsSpan(0, 16));
        var imported = new WorkflowImportedPanel(panelId, sourceId, "synthetic-panel.png", image);
        var prepared = new WorkflowPreparedPanel(imported, image, null);
        return new ProductionWorkflowDetectionRequest(
            prepared,
            image,
            WorkflowImageVariant.Original,
            DeterministicGuid(panel.SourceSha256 + panel.PanelId + "run"),
            DeterministicGuid(panel.SourceSha256 + panel.PanelId + "project"),
            encoded);
    }

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static async Task WriteNewAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string VerifyRepositoryDescriptor(JsonElement descriptor, string root, string label)
    {
        string sha = RequireSha(Text(descriptor, "sha256"), label);
        ReadVerified(
            RepositoryPath(root, Text(descriptor, "path")),
            sha,
            null,
            label);
        return sha;
    }

    private static JsonElement[] VerifyExecutionAssemblies(JsonElement request, string root)
    {
        JsonElement[] records = request.GetProperty("assemblies").EnumerateArray()
            .Select(static item => item.Clone()).ToArray();
        var executing = new[]
        {
            typeof(OfficialHeadTensorCapture).Assembly,
            typeof(ProductionRasterFrameDecoder).Assembly,
            typeof(LocalOnnxTextRegionDetector).Assembly,
            typeof(InferenceRuntime).Assembly,
        }.ToDictionary(
            static assembly => assembly.GetName().Name
                ?? throw new InvalidDataException("Executing assembly has no name."),
            StringComparer.Ordinal);
        if (records.Length != executing.Count)
        {
            throw new InvalidDataException("Capture execution assembly inventory is incomplete.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement record in records)
        {
            RequireProperties(record, "name", "path", "sha256");
            string name = Text(record, "name");
            if (!seen.Add(name) || !executing.TryGetValue(name, out System.Reflection.Assembly? assembly))
            {
                throw new InvalidDataException("Capture execution assembly identity is invalid.");
            }
            string path = RepositoryPath(root, Text(record, "path"));
            if (!string.Equals(path, Path.GetFullPath(assembly.Location), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Capture is not executing the bound assembly path.");
            }
            ReadVerified(path, RequireSha(Text(record, "sha256"), "execution assembly"), null,
                "execution assembly");
        }
        return records;
    }

    private static byte[] ReadVerified(
        string path,
        string expectedSha,
        int? expectedBytes,
        string label)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if ((expectedBytes is not null && bytes.Length != expectedBytes.Value) ||
            !string.Equals(Hash(bytes), expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} bytes differ from authenticated evidence.");
        }
        return bytes;
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        string[] actual = value.EnumerateObject().Select(static item => item.Name)
            .OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        string[] expected = names.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Capture request contains missing or unknown fields.");
        }
    }

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()
        ?? throw new InvalidDataException("Missing string: " + key);

    private static string RequireSha(string value, string label) =>
        value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw new InvalidDataException($"{label} SHA-256 is invalid.");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string RepositoryPath(string root, string path) =>
        Inside(root, Path.Combine(root, path));

    private static string Inside(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Capture artifact paths must remain inside the repository root.");
        }
        return full;
    }

    private sealed record ReportBinding(
        string Split,
        string Path,
        string Sha256,
        string ManifestSha256,
        JsonElement Document);

    private sealed record CaptureInput(
        string Split,
        string SourceSha256,
        string PanelId,
        string PanelSha256,
        int Width,
        int Height,
        int[] Crop,
        string PanelPngPath,
        string PanelPngSha256,
        int PanelPngByteCount,
        string RecordedUnmaskedGraySha256,
        string ReconstructedBgrSha256);

    private sealed class CpuDiscovery : IExecutionProviderDiscovery
    {
        public IReadOnlyList<string> GetAvailableProviders() => ["CPUExecutionProvider"];
    }

    private sealed class CaptureSessionFactory : IInferenceSessionFactory
    {
        public InferenceInput? LastInput { get; private set; }

        public int CaptureCount { get; private set; }

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider != InferenceProvider.Cpu)
            {
                throw new InvalidDataException("Tensor capture permits CPU session identity only.");
            }
            return ValueTask.FromResult<IInferenceSession>(new Session(this));
        }

        private sealed class Session(CaptureSessionFactory owner) : IInferenceSession
        {
            public InferenceProvider Provider => InferenceProvider.Cpu;

            public ValueTask<InferenceExecution> RunAsync(
                InferenceInput input,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (input.Shape.Count != 4)
                {
                    throw new InvalidDataException("Capture session requires NCHW input.");
                }
                owner.LastInput = new InferenceInput(
                    input.Values.ToArray(),
                    input.Shape.ToArray(),
                    input.InputName,
                    input.OutputName);
                owner.CaptureCount++;
                int outputCount = checked((int)(input.Shape[2] * input.Shape[3]));
                return ValueTask.FromResult(new InferenceExecution(
                    Array.AsReadOnly(new float[outputCount]),
                    InferenceProvider.Cpu,
                    new StageTiming(0, 0, 0, 0, 0, owner.CaptureCount == 1, false),
                    new MemoryDiagnostics(0, 0, 0, 0, outputCount)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
