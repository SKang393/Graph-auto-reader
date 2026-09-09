// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Runs an explicitly unapproved frozen DB-head candidate on the exact
/// annotation-free V3 panel inventory. Synthetic truth remains outside C#.
/// </summary>
internal static class OfficialHeadCandidateEvaluation
{
    internal const string Command = "--evaluate-official-head-candidate";
    internal const string CandidateSchema = "graphreader.frozen-db-head-ocr-candidate.v1";
    internal const string CandidateScope = "project-owned-synthetic-train-dev-unapproved-frozen-candidate";
    private const string CaptureRequestSchema = "graphreader.official-head-tensor-capture-request.v1";
    private const string CaptureScope = "project-owned-synthetic-train-dev-model-free";
    private const string RuntimeReportSchema = "graphreader.synthetic-runtime-seed-evidence.v2";
    private const string RuntimeReportScope = "local-synthetic-seed-diagnostic";
    private const string OutputSchema = "graphreader.official-head-candidate-evaluation.v1";
    private const int ExpectedPanelCount = 37;
    private const int ExpectedTrainPanelCount = 28;
    private const int ExpectedDevPanelCount = 9;

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
                SyntheticOnly = true,
                PrivateData = false,
                SealedData = false,
                ProductionApproved = false,
            }, JsonOptions));
            return exception is OperationCanceledException ? 130 : 1;
        }
    }

    internal static string[] ValidateCommand(string[] args, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 6 || !string.Equals(args[0], Command, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Usage: --evaluate-official-head-candidate <capture-request.json> <request-sha256> " +
                "<candidate.json> <candidate-sha256> <new-output-directory>");
        }
        string root = Path.GetFullPath(repositoryRoot);
        string[] resolved =
        [
            Inside(root, args[1]),
            RequireSha(args[2], "capture request"),
            Inside(root, args[3]),
            RequireSha(args[4], "candidate"),
            Inside(root, args[5]),
        ];
        if (Directory.Exists(resolved[4]) || File.Exists(resolved[4]))
        {
            throw new IOException("Use a new output directory; prior candidate evidence is retained.");
        }
        return resolved;
    }

    private static async Task<int> RunAsync(
        string[] args,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string[] command = ValidateCommand(args, root);
        string requestPath = command[0];
        string requestSha = command[1];
        string candidatePath = command[2];
        string candidateSha = command[3];
        string outputRoot = command[4];

        byte[] requestBytes = ReadVerified(requestPath, requestSha, null, "capture request");
        using var requestDocument = JsonDocument.Parse(requestBytes);
        JsonElement request = requestDocument.RootElement;
        ValidateCaptureScope(request);
        VerifyDescriptor(request.GetProperty("binding"), root, "V3 binding");
        VerifyDescriptor(request.GetProperty("capture_source"), root, "capture source");
        Dictionary<string, ReportBinding> reports = ReadReports(request, root);
        EvaluationPanel[] panels = ReadPanels(request, reports, root);

        byte[] candidateBytes = ReadVerified(candidatePath, candidateSha, null, "head candidate");
        using var candidateDocument = JsonDocument.Parse(candidateBytes);
        CandidateBinding candidate = ReadCandidate(candidateDocument.RootElement, root);
        var locks = new List<FileStream>();
        nint nativeHandle = nint.Zero;
        try
        {
            LockAndVerify(candidate.Detector.ModelPath, candidate.Detector.ModelSha256,
                "detector model", locks);
            LockAndVerify(candidate.Detector.ManifestPath, candidate.Detector.ManifestSha256,
                "detector manifest", locks);
            LockAndVerify(candidate.Recognizer.ModelPath, candidate.Recognizer.ModelSha256,
                "recognizer model", locks);
            LockAndVerify(candidate.Recognizer.ManifestPath, candidate.Recognizer.ManifestSha256,
                "recognizer manifest", locks);
            LockAndVerify(candidate.NativePath, candidate.NativeSha256, "native runtime", locks);
            foreach (BoundFile license in candidate.Licenses)
            {
                LockAndVerify(license.Path, license.Sha256, "reviewed license input", locks);
            }

            cancellationToken.ThrowIfCancellationRequested();
            nativeHandle = NativeLibrary.Load(candidate.NativePath);
            NativeLibrary.SetDllImportResolver(
                typeof(OpenCvSharp.Mat).Assembly,
                (name, _, _) => string.Equals(name, "OpenCvSharpExtern", StringComparison.Ordinal)
                    ? nativeHandle
                    : nint.Zero);
            Directory.CreateDirectory(outputRoot);
            await using var runtime = new ProductionInferenceRuntimeHost(
                new OrtExecutionProviderDiscovery(),
                new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance),
                CpuThreadConfiguration.Create(),
                [InferenceProvider.Cpu],
                Path.Combine(outputRoot, "inference-cache"),
                ProductionInferenceRuntimeHost.DefaultQueueCapacity,
                ProductionInferenceRuntimeHost.DefaultWorkerCount);

            ProductionOcrAdapter adapter = await ProductionOcrAdapter
                .CreateForFrozenDbHeadCandidateEvaluationAsync(
                    candidate.Detector.Descriptor,
                    candidate.Recognizer.Descriptor,
                    runtime,
                    candidate.NativeSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (adapter.IsApproved || !string.Equals(
                    adapter.ConfigurationScope,
                    "unapproved_frozen_candidate",
                    StringComparison.Ordinal) ||
                !adapter.AdapterId.Contains(
                    ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("DB-head evaluation adapter escaped its unapproved frozen scope.");
            }
            LocalOnnxTextRegionDetectorOptions detectorOptions = ProductionOcrAdapter
                .ReadDetectionOptions(candidate.Detector.Descriptor.Identity,
                    candidate.Detector.ManifestPath) with
                {
                    AllowedProviders = [InferenceProvider.Cpu],
                };
            var rawDetector = new LocalOnnxTextRegionDetector(runtime.Runtime, detectorOptions);

            var outputPanels = new List<object>(panels.Length);
            var total = Stopwatch.StartNew();
            int completed = 0;
            int failed = 0;
            foreach (EvaluationPanel panel in panels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                object[] rawOutput = [];
                string stage = "decode";
                try
                {
                    byte[] encoded = ReadVerified(panel.PanelPngPath, panel.PanelPngSha256,
                        panel.PanelPngByteCount, "panel PNG");
                    ProductionWorkflowDetectionRequest detectionRequest = CreateDetectionRequest(panel, encoded);
                    ProductionDecodedRaster raster = new ProductionRasterFrameDecoder()
                        .Decode(detectionRequest, cancellationToken);
                    OcrImage original = raster.CreateOcrImage();
                    string graySha = Hash(original.Pixels.Span);
                    string bgrSha = original.BgrPixels is { } bgrPixels
                        ? Hash(bgrPixels.Pixels.Span)
                        : Hash(ReadOnlySpan<byte>.Empty);
                    if (graySha != panel.RecordedGraySha256 || bgrSha != panel.RecordedBgrSha256)
                    {
                        throw new InvalidDataException(
                            "Production-decoded panel pixels differ from the authenticated capture request.");
                    }
                    var detectorImage = new OcrDetectorImage(original, graySha, bgrSha);
                    stage = "raw_detection";
                    IReadOnlyList<OcrDetectedRegion> raw = await rawDetector
                        .DetectAsync(original, cancellationToken).ConfigureAwait(false);
                    rawOutput = raw.Select(region => RawRegion(panel, region)).ToArray();
                    stage = "ocr";
                    ProductionOcrEvidence recognized = await adapter
                        .RecognizeForCandidateEvaluationAsync(
                            detectionRequest,
                            raster,
                            panel.PlotBounds,
                            detectorImage,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ValidateOcrCoverage(raw, recognized.Result);
                    timer.Stop();
                    completed++;
                    outputPanels.Add(CompletedPanel(panel, rawOutput, recognized, graySha, bgrSha,
                        timer.Elapsed.TotalMilliseconds));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    timer.Stop();
                    failed++;
                    outputPanels.Add(FailedPanel(
                        panel, rawOutput, stage, exception, timer.Elapsed.TotalMilliseconds));
                }
            }
            total.Stop();
            string reportPath = Path.Combine(outputRoot, "report.json");
            byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = OutputSchema,
                Status = failed == 0 ? "panels_completed" : "failed",
                Scope = CandidateScope,
                SyntheticOnly = true,
                PrivateData = false,
                SealedData = false,
                TruthUsedByRuntime = false,
                OptimizerSteps = 0,
                ProductionApproved = false,
                TrainingInputReady = false,
                Request = new
                {
                    Path = Relative(root, requestPath),
                    Sha256 = requestSha,
                },
                Candidate = new
                {
                    Path = Relative(root, candidatePath),
                    Sha256 = candidateSha,
                    CompositionVersion = ProductionOcrAdapter.OriginalDbCandidateCompositionVersion,
                    AdapterId = adapter.AdapterId,
                    ConfigurationScope = adapter.ConfigurationScope,
                    Detector = candidate.Detector.OutputIdentity,
                    Recognizer = candidate.Recognizer.OutputIdentity,
                    NativeSha256 = candidate.NativeSha256,
                },
                InputMode = "production_decoded_original_bgr_db",
                DetectorPostprocess = "manifest_bound_unchanged_db_postprocess",
                MaximumLogicalDetectorRequestsPerPanel = 2,
                DetectorRequestsShareExactRuntimeInputAndStageCacheKey = true,
                GraphStructureConsensusApplied = false,
                AxisMaskAppliedToDetector = false,
                AxisBoundsUsedForRoleClassification = true,
                PanelCount = panels.Length,
                CompletedPanelCount = completed,
                FailedPanelCount = failed,
                ModelInference = true,
                ElapsedMilliseconds = total.Elapsed.TotalMilliseconds,
                Panels = outputPanels,
            }, JsonOptions);
            await WriteNewAsync(reportPath, reportBytes, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Report = reportPath,
                PanelCount = panels.Length,
                CompletedPanelCount = completed,
                FailedPanelCount = failed,
                ProductionApproved = false,
            }, JsonOptions));
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            foreach (FileStream stream in locks)
            {
                stream.Dispose();
            }
            if (nativeHandle != nint.Zero)
            {
                NativeLibrary.Free(nativeHandle);
            }
        }
    }

    private static object CompletedPanel(
        EvaluationPanel panel,
        object[] rawOutput,
        ProductionOcrEvidence evidence,
        string graySha,
        string bgrSha,
        double elapsedMilliseconds) => new
        {
            panel.Split,
            panel.SourceSha256,
            panel.SourceWidth,
            panel.SourceHeight,
            panel.PanelId,
            panel.PanelSha256,
            panel.Width,
            panel.Height,
            Crop = Box(panel.Crop),
            RequestedCrop = Box(panel.RequestedCrop),
            panel.SourceToPanelMatrix,
            panel.PanelToSourceMatrix,
            Status = "completed",
            OriginalGraySha256 = graySha,
            OriginalBgrSha256 = bgrSha,
            RawDetectorRegions = rawOutput,
            RecognizedRegions = evidence.Result.Regions.Select(region => RecognizedRegion(panel, region)).ToArray(),
            RegionFailures = evidence.Result.RegionFailures ?? [],
            Ocr = new
            {
                evidence.Result.ContractVersion,
                evidence.Result.RunId,
                evidence.Result.ProjectId,
                evidence.Result.PanelId,
                evidence.Result.Stage,
                evidence.Result.StageVersion,
                evidence.Result.InputSha256,
                CoordinateSpace = "panel_original_pixels",
                evidence.Result.Succeeded,
                evidence.Result.Confidence,
                evidence.Result.Warnings,
                evidence.Result.Timing,
                evidence.Result.Cache,
                evidence.Result.Failure,
                Masks = evidence.Result.Masks.Select(mask => new
                {
                    mask.RegionId,
                    PanelPolygon = mask.Polygon,
                    SourcePolygon = MapSourcePolygon(panel, mask.Polygon),
                    mask.Confidence,
                }).ToArray(),
            },
            ConfiguredModels = evidence.ConfiguredModels,
            ExecutedModels = evidence.ModelEvidence,
            ElapsedMilliseconds = elapsedMilliseconds,
        };

    private static object FailedPanel(
        EvaluationPanel panel,
        object[] rawOutput,
        string stage,
        Exception exception,
        double elapsedMilliseconds) => new
    {
        panel.Split,
        panel.SourceSha256,
        panel.SourceWidth,
        panel.SourceHeight,
        panel.PanelId,
        panel.PanelSha256,
        panel.Width,
        panel.Height,
        Crop = Box(panel.Crop),
        RequestedCrop = Box(panel.RequestedCrop),
        panel.SourceToPanelMatrix,
        panel.PanelToSourceMatrix,
        Status = "failed",
        Stage = stage,
        Error = exception.Message,
        RawDetectorRegions = rawOutput,
        RecognizedRegions = Array.Empty<object>(),
        ElapsedMilliseconds = elapsedMilliseconds,
    };

    private static object RawRegion(EvaluationPanel panel, OcrDetectedRegion region) => new
    {
        region.RegionId,
        PanelPolygon = region.Polygon,
        SourcePolygon = MapSourcePolygon(panel, region.Polygon),
        region.OrientationDegrees,
        region.DetectionConfidence,
        region.Context,
        CoordinateSpace = "source_original_pixels",
        region.Evidence,
    };

    private static object RecognizedRegion(EvaluationPanel panel, OcrRegion region) => new
    {
        region.RegionId,
        PanelPolygon = region.Polygon,
        SourcePolygon = MapSourcePolygon(panel, region.Polygon),
        region.Text,
        region.Alternatives,
        Role = region.Role.ToString().ToLowerInvariant(),
        region.Confidence,
        SourceImage = region.SourceImage.ToString().ToLowerInvariant(),
        ReviewStatus = region.ReviewStatus.ToString().ToLowerInvariant(),
        CoordinateSpace = "source_original_pixels",
    };

    internal static OcrPolygon MapPolygon(OcrPolygon polygon, IReadOnlyList<double> matrix)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Count != 9 || matrix.Any(static value => !double.IsFinite(value)))
        {
            throw new InvalidDataException("Panel-to-source matrix must contain nine finite values.");
        }
        return new OcrPolygon(polygon.Points.Select(point =>
        {
            double denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
            if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
            {
                throw new InvalidDataException("Panel-to-source transform is singular.");
            }
            double x = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator;
            double y = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator;
            return new OcrPoint(x, y);
        }).ToArray());
    }

    private static OcrPolygon MapSourcePolygon(EvaluationPanel panel, OcrPolygon polygon)
    {
        OcrPolygon mapped = MapPolygon(polygon, panel.PanelToSourceMatrix);
        if (!mapped.Bounds.IsValid || mapped.Bounds.Left < 0 || mapped.Bounds.Top < 0 ||
            mapped.Bounds.Right > panel.SourceWidth || mapped.Bounds.Bottom > panel.SourceHeight)
        {
            throw new InvalidDataException("Panel OCR polygon maps outside its authenticated source image.");
        }
        return mapped;
    }

    internal static void ValidateOcrCoverage(
        IReadOnlyList<OcrDetectedRegion> raw,
        OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || result.Failure is not null)
        {
            throw new InvalidDataException("Candidate OCR result is structurally unsuccessful.");
        }
        string[] rawIds = raw.Select(static item => item.RegionId).Order(StringComparer.Ordinal).ToArray();
        if (rawIds.Length != rawIds.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Raw detector region identities are duplicated.");
        }
        string[] outputIds = result.Regions.Select(static item => item.RegionId)
            .Concat((result.RegionFailures ?? []).Select(static item => item.RegionId))
            .Order(StringComparer.Ordinal).ToArray();
        if (!rawIds.SequenceEqual(outputIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Recognized regions plus explicit failures do not preserve the complete raw detector inventory.");
        }
        Dictionary<string, OcrDetectedRegion> rawById = raw.ToDictionary(
            static item => item.RegionId, StringComparer.Ordinal);
        foreach (OcrRegion recognized in result.Regions)
        {
            if (!rawById.TryGetValue(recognized.RegionId, out OcrDetectedRegion? detected) ||
                !detected.Polygon.Points.SequenceEqual(recognized.Polygon.Points))
            {
                throw new InvalidDataException("Recognized OCR geometry differs from its raw detector region.");
            }
        }
    }

    private static CandidateBinding ReadCandidate(JsonElement root, string repositoryRoot)
    {
        RequireProperties(root,
            "schema", "scope", "production_approved", "training_input_ready",
            "composition_version", "native_path", "native_sha256", "native_scope",
            "license_inputs", "detector", "recognizer");
        if (Text(root, "schema") != CandidateSchema || Text(root, "scope") != CandidateScope ||
            root.GetProperty("production_approved").GetBoolean() ||
            root.GetProperty("training_input_ready").GetBoolean() ||
            Text(root, "composition_version") != ProductionOcrAdapter.OriginalDbCandidateCompositionVersion ||
            Text(root, "native_scope") != "reviewed-source-runtime-local-diagnostic")
        {
            throw new InvalidDataException("Frozen DB-head candidate scope or composition is invalid.");
        }
        CandidateModel detector = ReadModel(root.GetProperty("detector"), repositoryRoot, "ocr_detection");
        CandidateModel recognizer = ReadModel(root.GetProperty("recognizer"), repositoryRoot, "ocr_recognition");
        if (detector.ModelSha256 == recognizer.ModelSha256 ||
            string.Equals(detector.ModelPath, recognizer.ModelPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Candidate detector and recognizer payloads must be distinct.");
        }
        JsonElement[] licenseRecords = root.GetProperty("license_inputs").EnumerateArray().ToArray();
        if (licenseRecords.Length == 0)
        {
            throw new InvalidDataException("Frozen DB-head candidate requires reviewed license evidence.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var licenses = new List<BoundFile>(licenseRecords.Length);
        foreach (JsonElement record in licenseRecords)
        {
            RequireProperties(record, "path", "sha256");
            string path = RepositoryPath(repositoryRoot, Text(record, "path"));
            string hash = RequireSha(Text(record, "sha256"), "license input");
            if (!seen.Add(path + "\n" + hash))
            {
                throw new InvalidDataException("Frozen DB-head candidate repeats a license input.");
            }
            licenses.Add(new BoundFile(path, hash));
        }
        return new CandidateBinding(
            detector,
            recognizer,
            RepositoryPath(repositoryRoot, Text(root, "native_path")),
            RequireSha(Text(root, "native_sha256"), "native runtime"),
            licenses.AsReadOnly());
    }

    private static CandidateModel ReadModel(JsonElement record, string root, string expectedTask)
    {
        RequireProperties(record,
            "model_path", "model_id", "model_version", "model_sha256",
            "manifest_path", "manifest_sha256");
        string modelPath = RepositoryPath(root, Text(record, "model_path"));
        string manifestPath = RepositoryPath(root, Text(record, "manifest_path"));
        string modelSha = RequireSha(Text(record, "model_sha256"), expectedTask + " model");
        string manifestSha = RequireSha(Text(record, "manifest_sha256"), expectedTask + " manifest");
        byte[] manifestBytes = ReadVerified(manifestPath, manifestSha, null, expectedTask + " manifest");
        using var manifestDocument = JsonDocument.Parse(manifestBytes);
        JsonElement manifest = manifestDocument.RootElement;
        if (Text(manifest, "model_id") != Text(record, "model_id") ||
            Text(manifest, "model_version") != Text(record, "model_version") ||
            Text(manifest, "task") != expectedTask ||
            RequireSha(Text(manifest, "sha256"), expectedTask + " manifest payload") != modelSha ||
            !manifest.GetProperty("providers").EnumerateArray()
                .Any(static item => string.Equals(item.GetString(), "cpu", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"{expectedTask} manifest identity or CPU provider differs from candidate.");
        }
        var identity = new ModelIdentity(
            Text(record, "model_id"), Text(record, "model_version"), modelSha, modelPath);
        return new CandidateModel(
            modelPath,
            modelSha,
            manifestPath,
            manifestSha,
            new FrozenCandidateOcrModelDescriptor(identity, manifestPath, manifestSha),
            new
            {
                Task = expectedTask,
                identity.ModelId,
                identity.Version,
                Sha256 = identity.Sha256,
                ManifestPath = Relative(root, manifestPath),
                ManifestSha256 = manifestSha,
            });
    }

    private static void ValidateCaptureScope(JsonElement root)
    {
        RequireProperties(root,
            "schema", "scope", "synthetic_only", "private_data", "sealed_data",
            "truth_included", "model_inference", "training_input_ready", "production_approved",
            "capture_source", "assemblies", "binding", "candidate", "detector", "native",
            "license_inputs", "maximum_side_length", "dimension_multiple",
            "detector_configuration_fingerprint", "reports", "panels");
        if (Text(root, "schema") != CaptureRequestSchema || Text(root, "scope") != CaptureScope ||
            !root.GetProperty("synthetic_only").GetBoolean() ||
            root.GetProperty("private_data").GetBoolean() || root.GetProperty("sealed_data").GetBoolean() ||
            root.GetProperty("truth_included").GetBoolean() ||
            root.GetProperty("model_inference").GetBoolean() ||
            root.GetProperty("training_input_ready").GetBoolean() ||
            root.GetProperty("production_approved").GetBoolean())
        {
            throw new InvalidDataException("Official-head capture request scope is invalid.");
        }
    }

    private static Dictionary<string, ReportBinding> ReadReports(JsonElement request, string root)
    {
        JsonElement[] records = request.GetProperty("reports").EnumerateArray().ToArray();
        if (records.Length != 6 || records.Count(static item => Text(item, "split") == "train") != 5 ||
            records.Count(static item => Text(item, "split") == "validation") != 1)
        {
            throw new InvalidDataException("Candidate evaluation requires all six V3 runtime exchanges.");
        }
        var output = new Dictionary<string, ReportBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement record in records)
        {
            string split = Text(record, "split");
            string manifestPath = RepositoryPath(root, Text(record, "manifest_path"));
            string manifestSha = RequireSha(Text(record, "manifest_sha256"), "source manifest");
            ReadVerified(manifestPath, manifestSha, null, "source manifest");
            string reportPath = RepositoryPath(root, Text(record, "report_path"));
            string reportSha = RequireSha(Text(record, "report_sha256"), "runtime report");
            byte[] bytes = ReadVerified(reportPath, reportSha, null, "runtime report");
            using var document = JsonDocument.Parse(bytes);
            JsonElement report = document.RootElement.Clone();
            if (Text(report, "schema") != RuntimeReportSchema ||
                Text(report, "scope") != RuntimeReportScope ||
                report.GetProperty("production_approved").GetBoolean() ||
                Text(report, "input_manifest_sha256") != manifestSha ||
                report.GetProperty("failed_panels").GetInt32() != 0 ||
                report.GetProperty("completed_panels").GetInt32() != report.GetProperty("panel_count").GetInt32() ||
                !output.TryAdd(reportPath, new ReportBinding(split, reportPath, reportSha, report)))
            {
                throw new InvalidDataException("Runtime report is not complete fixed V3 evidence.");
            }
        }
        return output;
    }

    private static EvaluationPanel[] ReadPanels(
        JsonElement request,
        IReadOnlyDictionary<string, ReportBinding> reports,
        string root)
    {
        JsonElement[] records = request.GetProperty("panels").EnumerateArray().ToArray();
        if (records.Length != ExpectedPanelCount ||
            records.Count(static item => Text(item, "split") == "train") != ExpectedTrainPanelCount ||
            records.Count(static item => Text(item, "split") == "validation") != ExpectedDevPanelCount)
        {
            throw new InvalidDataException("Candidate evaluation requires the complete 28/9 V3 panel inventory.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<EvaluationPanel>(records.Length);
        foreach (JsonElement record in records)
        {
            string split = Text(record, "split");
            string sourceSha = RequireSha(Text(record, "source_sha256"), "source");
            string panelId = Text(record, "panel_id");
            if (!Guid.TryParseExact(panelId, "D", out Guid parsedPanelId) || parsedPanelId == Guid.Empty ||
                !ids.Add(panelId))
            {
                throw new InvalidDataException("Candidate panel GUID is invalid or duplicated.");
            }
            string panelSha = RequireSha(Text(record, "panel_sha256"), "panel");
            int width = record.GetProperty("width").GetInt32();
            int height = record.GetProperty("height").GetInt32();
            int[] crop = ReadIntBox(record.GetProperty("crop"), "capture crop");
            if (width <= 0 || height <= 0 || crop[2] != width || crop[3] != height)
            {
                throw new InvalidDataException("Capture panel dimensions differ from crop dimensions.");
            }
            string reportPath = RepositoryPath(root, Text(record, "report_path"));
            if (!reports.TryGetValue(reportPath, out ReportBinding? report) || report.Split != split ||
                report.Sha256 != RequireSha(Text(record, "report_sha256"), "panel report"))
            {
                throw new InvalidDataException("Capture panel report binding changed.");
            }
            JsonElement reportPanel = FindPanel(report.Document, sourceSha, panelId);
            JsonElement source = FindSource(report.Document, sourceSha);
            int sourceWidth = source.GetProperty("width").GetInt32();
            int sourceHeight = source.GetProperty("height").GetInt32();
            int[] reportCrop = ReadBox(reportPanel.GetProperty("crop"), "report crop");
            int[] requestedCrop = ReadBox(reportPanel.GetProperty("requested_crop"), "requested crop");
            if (Text(reportPanel, "status") != "seed-completed" ||
                Text(reportPanel, "image_sha256") != panelSha ||
                reportPanel.GetProperty("width").GetInt32() != width ||
                reportPanel.GetProperty("height").GetInt32() != height ||
                !crop.SequenceEqual(reportCrop))
            {
                throw new InvalidDataException("Capture panel differs from its runtime report.");
            }
            double[] sourceToPanel = ReadMatrix(reportPanel.GetProperty("source_to_panel_matrix"));
            double[] panelToSource = ReadMatrix(reportPanel.GetProperty("panel_to_source_matrix"));
            ValidateInverse(sourceToPanel, panelToSource);
            OcrRectangle plotBounds = ReadPlotBounds(reportPanel.GetProperty("axis"), width, height);
            string recordedGray = RequireSha(
                Text(reportPanel.GetProperty("ocr_proposal_diagnostic"), "unmasked_input_sha256"),
                "recorded unmasked Gray8");
            if (recordedGray != RequireSha(Text(record, "recorded_unmasked_gray_sha256"),
                    "capture unmasked Gray8") ||
                Text(record, "bgr_identity_kind") != "reconstructed_from_authenticated_panel_png")
            {
                throw new InvalidDataException("Capture original-pixel identity differs from runtime evidence.");
            }
            JsonElement png = record.GetProperty("panel_png");
            JsonElement reportPng = reportPanel.GetProperty("panel_png");
            string canonicalPng = Path.Combine(Path.GetDirectoryName(reportPath)!, sourceSha, panelId,
                Text(reportPng, "file"));
            string pngPath = RepositoryPath(root, Text(png, "path"));
            string pngSha = RequireSha(Text(png, "sha256"), "panel PNG");
            int pngBytes = png.GetProperty("byte_count").GetInt32();
            if (!string.Equals(pngPath, canonicalPng, StringComparison.OrdinalIgnoreCase) ||
                pngSha != Text(reportPng, "sha256") ||
                pngBytes != reportPng.GetProperty("byte_count").GetInt32())
            {
                throw new InvalidDataException("Capture PNG differs from runtime report evidence.");
            }
            output.Add(new EvaluationPanel(
                split, sourceSha, sourceWidth, sourceHeight, panelId, panelSha, width, height,
                crop, requestedCrop, sourceToPanel, panelToSource, plotBounds, pngPath, pngSha,
                pngBytes, recordedGray,
                RequireSha(Text(record, "reconstructed_bgr_sha256"), "reconstructed BGR24")));
        }
        return output.OrderBy(static item => item.Split, StringComparer.Ordinal)
            .ThenBy(static item => item.PanelId, StringComparer.Ordinal).ToArray();
    }

    private static OcrRectangle ReadPlotBounds(JsonElement axis, int width, int height)
    {
        JsonElement geometry = axis.GetProperty("geometry");
        if (Text(geometry, "coordinate_space") != "original_pixels")
        {
            throw new InvalidDataException("Axis geometry is not in panel original pixels.");
        }
        JsonElement[] points = geometry.GetProperty("plot_polygon").GetProperty("points")
            .EnumerateArray().ToArray();
        if (points.Length != 4)
        {
            throw new InvalidDataException("Axis plot polygon must contain four points.");
        }
        double[] x = points.Select(point => point.GetProperty("x").GetDouble()).ToArray();
        double[] y = points.Select(point => point.GetProperty("y").GetDouble()).ToArray();
        if (points.Any(static point => !point.GetProperty("is_finite").GetBoolean()) ||
            x.Any(static value => !double.IsFinite(value)) ||
            y.Any(static value => !double.IsFinite(value)))
        {
            throw new InvalidDataException("Axis plot polygon contains nonfinite coordinates.");
        }
        var bounds = new OcrRectangle(x.Min(), y.Min(), x.Max() - x.Min(), y.Max() - y.Min());
        if (!bounds.IsValid || bounds.Left < 0 || bounds.Top < 0 ||
            bounds.Right > width || bounds.Bottom > height)
        {
            throw new InvalidDataException("Axis plot bounds lie outside the panel.");
        }
        return bounds;
    }

    private static ProductionWorkflowDetectionRequest CreateDetectionRequest(
        EvaluationPanel panel,
        byte[] encoded)
    {
        var evidence = new WorkflowImageEvidence(
            panel.PanelPngPath, panel.PanelSha256, panel.Width, panel.Height,
            WorkflowImageVariant.Original);
        Guid panelId = Guid.ParseExact(panel.PanelId, "D");
        Guid sourceId = DeterministicGuid(panel.SourceSha256 + "source");
        Guid projectId = DeterministicGuid(panel.SourceSha256 + panel.PanelId + "project");
        var imported = new WorkflowImportedPanel(panelId, sourceId, "synthetic-panel.png", evidence);
        var prepared = new WorkflowPreparedPanel(imported, evidence, null);
        return new ProductionWorkflowDetectionRequest(
            prepared, evidence, WorkflowImageVariant.Original,
            DeterministicGuid(panel.SourceSha256 + panel.PanelId + "run"), projectId, encoded);
    }

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static JsonElement FindSource(JsonElement report, string sourceSha)
    {
        JsonElement[] matches = report.GetProperty("cases").EnumerateArray()
            .Where(item => Text(item, "image_sha256") == sourceSha).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(
            "Capture source is missing or duplicated in its runtime report.");
    }

    private static JsonElement FindPanel(JsonElement report, string sourceSha, string panelId)
    {
        JsonElement source = FindSource(report, sourceSha);
        JsonElement[] matches = source.GetProperty("panels").EnumerateArray()
            .Where(item => Text(item, "panel_id") == panelId).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException(
            "Capture panel is missing or duplicated in its runtime report.");
    }

    private static int[] ReadIntBox(JsonElement value, string label)
    {
        int[] result = value.EnumerateArray().Select(static item => item.GetInt32()).ToArray();
        if (result.Length != 4 || result[0] < 0 || result[1] < 0 || result[2] <= 0 || result[3] <= 0)
        {
            throw new InvalidDataException($"{label} is invalid.");
        }
        return result;
    }

    private static int[] ReadBox(JsonElement value, string label)
    {
        RequireProperties(value, "x", "y", "width", "height");
        return ReadIntBox(JsonSerializer.SerializeToElement(new[]
        {
            value.GetProperty("x").GetInt32(), value.GetProperty("y").GetInt32(),
            value.GetProperty("width").GetInt32(), value.GetProperty("height").GetInt32(),
        }), label);
    }

    private static double[] ReadMatrix(JsonElement value)
    {
        double[] matrix = value.EnumerateArray().Select(static item => item.GetDouble()).ToArray();
        if (matrix.Length != 9 || matrix.Any(static item => !double.IsFinite(item)))
        {
            throw new InvalidDataException("Panel transform must contain nine finite values.");
        }
        return matrix;
    }

    private static void ValidateInverse(double[] forward, double[] inverse)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double value = 0;
                for (int index = 0; index < 3; index++)
                {
                    value += forward[row * 3 + index] * inverse[index * 3 + column];
                }
                double expected = row == column ? 1 : 0;
                if (Math.Abs(value - expected) > 1e-9)
                {
                    throw new InvalidDataException("Panel transforms are not mutual inverses.");
                }
            }
        }
    }

    private static object Box(int[] box) => new
    {
        X = box[0], Y = box[1], Width = box[2], Height = box[3],
    };

    private static void VerifyDescriptor(JsonElement descriptor, string root, string label) =>
        _ = ReadVerified(
            RepositoryPath(root, Text(descriptor, "path")),
            RequireSha(Text(descriptor, "sha256"), label), null, label);

    private static void LockAndVerify(
        string path,
        string expectedSha,
        string label,
        List<FileStream> locks)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (Hash(stream) != expectedSha)
            {
                throw new InvalidDataException($"{label} bytes differ from candidate evidence.");
            }
            stream.Position = 0;
            locks.Add(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static byte[] ReadVerified(string path, string expectedSha, int? expectedBytes, string label)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if ((expectedBytes is not null && bytes.Length != expectedBytes.Value) || Hash(bytes) != expectedSha)
        {
            throw new InvalidDataException($"{label} bytes differ from authenticated evidence.");
        }
        return bytes;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        string[] actual = value.EnumerateObject().Select(static item => item.Name)
            .Order(StringComparer.Ordinal).ToArray();
        string[] expected = names.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Evidence JSON contains missing or unknown fields.");
        }
    }

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()
        ?? throw new InvalidDataException("Missing string: " + key);

    private static string RequireSha(string value, string label) =>
        value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw new InvalidDataException($"{label} SHA-256 is invalid.");

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Hash(Stream stream) =>
        Convert.ToHexStringLower(SHA256.HashData(stream));

    private static string RepositoryPath(string root, string relativePath) =>
        Inside(root, Path.Combine(root, relativePath));

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Inside(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Candidate evaluation paths must remain inside the repository root.");
        }
        return full;
    }

    private sealed record BoundFile(string Path, string Sha256);

    private sealed record CandidateModel(
        string ModelPath,
        string ModelSha256,
        string ManifestPath,
        string ManifestSha256,
        FrozenCandidateOcrModelDescriptor Descriptor,
        object OutputIdentity);

    private sealed record CandidateBinding(
        CandidateModel Detector,
        CandidateModel Recognizer,
        string NativePath,
        string NativeSha256,
        IReadOnlyList<BoundFile> Licenses);

    private sealed record ReportBinding(
        string Split,
        string Path,
        string Sha256,
        JsonElement Document);

    private sealed record EvaluationPanel(
        string Split,
        string SourceSha256,
        int SourceWidth,
        int SourceHeight,
        string PanelId,
        string PanelSha256,
        int Width,
        int Height,
        int[] Crop,
        int[] RequestedCrop,
        double[] SourceToPanelMatrix,
        double[] PanelToSourceMatrix,
        OcrRectangle PlotBounds,
        string PanelPngPath,
        string PanelPngSha256,
        int PanelPngByteCount,
        string RecordedGraySha256,
        string RecordedBgrSha256);
}
