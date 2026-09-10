// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Runs the shipped DB postprocessor over authenticated synthetic shrink targets.
/// The inference session is a model-free oracle which returns supplied probability
/// maps; the reviewed parent model is identified and locked but never executed.
/// </summary>
internal static class OfficialDbTargetOracle
{
    public const string Command = "--evaluate-db-target-oracle";

    private const string RequestSchema = "graphreader.db-target-oracle-request.v1";
    private const string ReportSchema = "graphreader.db-target-oracle-report.v1";
    private const string Scope = "project-owned-synthetic-train-dev-target-diagnostic";
    private const string DetectorManifestSha256 =
        "2ddb1599df210da50bf097383b1f691ee6e1bed27ba9c98b6695a6b06e7e8d51";
    private const string ParentModelSha256 =
        "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb";
    private const string NativeSha256 =
        "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
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
            string[] validated = ValidateCommand(args, repositoryRoot);
            return await RunAsync(
                validated[0],
                validated[1],
                validated[2],
                cancellation.Token).ConfigureAwait(false);
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

    internal static string[] ValidateCommand(string[] args, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        string root = Path.GetFullPath(repositoryRoot);
        if (args.Length != 4 || !string.Equals(args[0], Command, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Usage: {Command} <request.json> <request-sha256> <new-output-directory>");
        }

        string requestPath = InsideRepository(root, args[1], "request");
        string requestSha256 = RequireSha256(args[2], "request");
        string outputPath = InsideArtifacts(root, args[3], "output");
        if (Directory.Exists(outputPath) || File.Exists(outputPath))
        {
            throw new IOException("Use a new target-oracle output directory.");
        }

        return [requestPath, requestSha256, outputPath];
    }

    private static async Task<int> RunAsync(
        string requestPath,
        string requestSha256,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var locks = new List<FileStream>();
        nint nativeHandle = nint.Zero;
        try
        {
            string root = RepositoryRootFor(requestPath);
            byte[] requestBytes = ReadVerifiedLocked(
                requestPath, requestSha256, "request", locks);
            using JsonDocument request = ParseJson(requestBytes, "request");
            JsonElement document = request.RootElement;
            ValidateRequestScope(document);

            BoundFile manifest = ReadDescriptor(
                root, document.GetProperty("detector_manifest"), DetectorManifestSha256,
                "detector manifest", locks);
            BoundFile parentModel = ReadDescriptor(
                root, document.GetProperty("parent_model"), ParentModelSha256,
                "reviewed parent model", locks);
            BoundFile native = ReadDescriptor(
                root, document.GetProperty("native"), NativeSha256,
                "reviewed native runtime", locks);
            ValidateSupplementalEvidence(root, document, locks);

            using JsonDocument manifestDocument = ParseJson(manifest.Bytes, "detector manifest");
            JsonElement manifestRoot = manifestDocument.RootElement;
            string modelId = RequiredText(manifestRoot, "model_id", "detector manifest");
            string modelVersion = RequiredText(manifestRoot, "model_version", "detector manifest");
            if (!string.Equals(modelId, "PP-OCRv5_mobile_det", StringComparison.Ordinal) ||
                !string.Equals(modelVersion, "5.0.0", StringComparison.Ordinal) ||
                !string.Equals(
                    RequiredText(manifestRoot, "sha256", "detector manifest"),
                    ParentModelSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The detector manifest is not the reviewed PP-OCRv5 parent.");
            }

            var model = new ModelIdentity(modelId, modelVersion, ParentModelSha256, parentModel.Path);
            LocalOnnxTextRegionDetectorOptions baseOptions =
                ProductionOcrAdapter.ReadDetectionOptions(model, manifest.Path);
            if (baseOptions.PostprocessAlgorithm != OcrDetectionPostprocessAlgorithm.DbPostprocessV1 ||
                baseOptions.InputColorMode != OcrTensorColorMode.Bgr ||
                baseOptions.OutputActivation is not (
                    OcrDetectionOutputActivation.Probability or
                    OcrDetectionOutputActivation.ProbabilityWithParityTolerance))
            {
                throw new InvalidDataException("The reviewed detector manifest no longer exposes the expected DB probability contract.");
            }

            OraclePanel[] panels = ReadPanels(root, document.GetProperty("panels"), locks);
            AssemblyEvidence[] assemblies = LockExecutingAssemblies(root, locks);

            nativeHandle = NativeLibrary.Load(native.Path);
            NativeLibrary.SetDllImportResolver(
                typeof(OpenCvSharp.Mat).Assembly,
                (name, _, _) => string.Equals(name, "OpenCvSharpExtern", StringComparison.Ordinal)
                    ? nativeHandle
                    : nint.Zero);

            var factory = new OracleSessionFactory(panels);
            await using var registry = new OnnxSessionRegistry(
                new FixedProviderDiscovery(),
                new WindowsExecutionProviderPolicy(),
                factory,
                CpuThreadConfiguration.Create());
            await using var scheduler = new BoundedInferenceScheduler(1, 1);
            await using var runtime = new InferenceRuntime(registry, scheduler, new NoStageCache());

            var timer = Stopwatch.StartNew();
            var outcomes = new List<object>(panels.Length);
            var dispositionTotals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (OraclePanel panel in panels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OcrDbGeometryObservation? geometry = null;
                OcrDbPostprocessObservation? postprocess = null;
                LocalOnnxTextRegionDetectorOptions options = baseOptions with
                {
                    BypassCache = true,
                    DbGeometryObserver = value => geometry = value,
                    DbPostprocessObserver = value => postprocess = value,
                };
                var detector = new LocalOnnxTextRegionDetector(runtime, options);
                var gray = new byte[checked(panel.Width * panel.Height)];
                var bgr = new byte[checked(panel.Width * panel.Height * 3)];
                Array.Fill(gray, byte.MaxValue);
                Array.Fill(bgr, byte.MaxValue);
                var image = new OcrImage(
                    panel.Width,
                    panel.Height,
                    panel.Width,
                    gray,
                    OcrSourceImage.Original,
                    OcrFrameTransform.Identity,
                    OcrContract.CoordinateSpace,
                    panel.Width,
                    panel.Height,
                    new OcrBgrBytePixels(checked(panel.Width * 3), bgr));

                OcrAtomicDbDetection detection = await detector
                    .DetectWithAtomicDbGeometryAsync(image, cancellationToken)
                    .ConfigureAwait(false);
                ValidateObservation(panel, detection, geometry, postprocess);
                foreach (IGrouping<OcrDbContourDisposition, OcrDbContourEvaluation> group in
                         postprocess!.Contours.GroupBy(static item => item.Disposition))
                {
                    string key = JsonNamingPolicy.SnakeCaseLower.ConvertName(group.Key.ToString());
                    dispositionTotals[key] = dispositionTotals.GetValueOrDefault(key) + group.Count();
                }

                outcomes.Add(new
                {
                    panel.PanelId,
                    panel.Split,
                    panel.SourceSha256,
                    panel.Width,
                    panel.Height,
                    panel.TensorWidth,
                    panel.TensorHeight,
                    PanelToSourceMatrix = panel.PanelToSourceMatrix,
                    Target = new
                    {
                        Path = Relative(root, panel.TargetPath),
                        Sha256 = panel.TargetSha256,
                    },
                    InputSha256 = geometry!.InputSha256,
                    ReturnedRegionCount = detection.Regions.Count,
                    AcceptedContourCount = geometry.AcceptedContours.Count,
                    postprocess.AboveThresholdPixelCount,
                    postprocess.TotalContourCount,
                    postprocess.EvaluatedContourCount,
                    Regions = detection.Regions.Select(static region => new
                    {
                        region.RegionId,
                        Polygon = Polygon(region.Polygon),
                        region.OrientationDegrees,
                        region.DetectionConfidence,
                        region.CoordinateSpace,
                    }),
                    AcceptedContours = geometry.AcceptedContours.Select(static contour => new
                    {
                        contour.ReturnedRegionId,
                        InitialPolygon = Polygon(contour.InitialPolygon),
                        ExpandedPolygon = Polygon(contour.ExpandedPolygon),
                        contour.DetectionConfidence,
                        contour.InkDensity,
                    }),
                    ContourDispositions = postprocess.Contours.Select(static contour => new
                    {
                        contour.ContourIndex,
                        contour.PointCount,
                        contour.Disposition,
                        InitialPolygon = contour.InitialPolygon is null ? null : Polygon(contour.InitialPolygon),
                        ExpandedPolygon = contour.ExpandedPolygon is null ? null : Polygon(contour.ExpandedPolygon),
                        contour.InitialShortSide,
                        contour.ExpandedShortSide,
                        contour.BoxConfidence,
                        contour.InkDensity,
                        contour.ReturnedRegionId,
                    }),
                });
            }

            if (factory.RunCount != panels.Length || factory.RemainingCount != 0)
            {
                throw new InvalidDataException("The model-free oracle did not consume every target exactly once.");
            }

            timer.Stop();
            Directory.CreateDirectory(outputRoot);
            string reportPath = Path.Combine(outputRoot, "report.json");
            await using var output = new FileStream(
                reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(output, new
            {
                Schema = ReportSchema,
                Status = "diagnostic_only",
                Scope,
                SyntheticOnly = true,
                PrivateData = false,
                SealedData = false,
                ModelInference = false,
                OptimizerSteps = 0,
                ProductionApproved = false,
                Request = new { Path = Relative(root, requestPath), Sha256 = requestSha256 },
                DetectorManifest = new { Path = Relative(root, manifest.Path), Sha256 = DetectorManifestSha256 },
                ParentModel = new { Path = Relative(root, parentModel.Path), Sha256 = ParentModelSha256, Executed = false },
                Native = new { Path = Relative(root, native.Path), Sha256 = NativeSha256 },
                Assemblies = assemblies,
                PanelCount = panels.Length,
                TrainPanelCount = panels.Count(static panel => panel.Split == "train"),
                ValidationPanelCount = panels.Count(static panel => panel.Split == "validation"),
                OracleSessionRunCount = factory.RunCount,
                DetectorConfigurationFingerprint = new LocalOnnxTextRegionDetector(runtime, baseOptions).ConfigurationFingerprint,
                DispositionTotals = dispositionTotals,
                Panels = outcomes,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                ClaimScope = "Perfect supplied DB shrink-target postprocess geometry only; no model, recognition, or production-accuracy claim.",
            }, JsonOptions, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Status = "diagnostic_only",
                Report = reportPath,
                PanelCount = panels.Length,
                ModelInference = false,
                ProductionApproved = false,
            }, JsonOptions));
            return 0;
        }
        finally
        {
            foreach (FileStream stream in locks)
            {
                stream.Dispose();
            }
        }
    }

    private static void ValidateRequestScope(JsonElement root)
    {
        RequireProperties(root,
        [
            "schema", "scope", "synthetic_only", "private_data", "sealed_data", "model_inference",
            "detector_manifest", "parent_model", "native", "exporter", "target_loader_sha256",
            "binding", "capture_report", "source_geometry_report", "synthetic_truth", "panels",
            "elapsed_ms", "claim_scope",
        ], "request");
        if (!string.Equals(RequiredText(root, "schema", "request"), RequestSchema, StringComparison.Ordinal) ||
            !string.Equals(RequiredText(root, "scope", "request"), Scope, StringComparison.Ordinal) ||
            root.GetProperty("synthetic_only").ValueKind != JsonValueKind.True ||
            root.GetProperty("private_data").ValueKind != JsonValueKind.False ||
            root.GetProperty("sealed_data").ValueKind != JsonValueKind.False ||
            root.GetProperty("model_inference").ValueKind != JsonValueKind.False ||
            !string.Equals(
                RequiredText(root, "claim_scope", "request"),
                "Perfect training target round-trip diagnosis only; not model accuracy or production approval.",
                StringComparison.Ordinal) ||
            !root.GetProperty("elapsed_ms").TryGetDouble(out double elapsed) ||
            !double.IsFinite(elapsed) || elapsed < 0)
        {
            throw new InvalidDataException("Target-oracle request scope is invalid.");
        }
    }

    private static void ValidateSupplementalEvidence(
        string root,
        JsonElement document,
        List<FileStream> locks)
    {
        ReadDescriptor(root, document.GetProperty("exporter"), null, "target exporter", locks);
        ReadDescriptor(root, document.GetProperty("binding"), null, "runtime binding", locks);
        ReadDescriptor(root, document.GetProperty("capture_report"), null, "capture report", locks);
        ReadDescriptor(root, document.GetProperty("source_geometry_report"), null, "source geometry report", locks);
        ReadDescriptor(root, document.GetProperty("synthetic_truth"), null, "synthetic truth", locks);
        RequireSha256(RequiredText(document, "target_loader_sha256", "request"), "target loader");
    }

    private static OraclePanel[] ReadPanels(
        string root,
        JsonElement value,
        List<FileStream> locks)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Request panels must be an array.");
        }

        var panels = new List<OraclePanel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int train = 0;
        int validation = 0;
        foreach (JsonElement panel in value.EnumerateArray())
        {
            RequireProperties(panel,
            [
                "panel_id", "split", "width", "height", "tensor_width", "tensor_height",
                "target", "source_sha256", "panel_to_source_matrix", "projections",
            ], "panel");
            string panelId = RequiredText(panel, "panel_id", "panel");
            string split = RequiredText(panel, "split", "panel");
            if (!ids.Add(panelId) || split is not ("train" or "validation"))
            {
                throw new InvalidDataException("Panel identity or split is invalid.");
            }

            int width = PositiveInt32(panel, "width", "panel");
            int height = PositiveInt32(panel, "height", "panel");
            int tensorWidth = PositiveInt32(panel, "tensor_width", "panel");
            int tensorHeight = PositiveInt32(panel, "tensor_height", "panel");
            string sourceSha256 = RequireSha256(
                RequiredText(panel, "source_sha256", "panel"), "panel source");
            double[] matrix = ReadMatrix(panel.GetProperty("panel_to_source_matrix"));
            if (panel.GetProperty("projections").ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Panel projections must be an array.");
            }
            BoundFile target = ReadDescriptor(root, panel.GetProperty("target"), null, "target map", locks);
            float[] probabilities = DecodeTarget(target.Bytes, tensorWidth, tensorHeight);
            panels.Add(new OraclePanel(
                panelId, split, sourceSha256, width, height, tensorWidth, tensorHeight,
                target.Path, target.Sha256, matrix, probabilities));
            train += split == "train" ? 1 : 0;
            validation += split == "validation" ? 1 : 0;
        }

        if (panels.Count != 37 || train != 28 || validation != 9)
        {
            throw new InvalidDataException("Target-oracle request must contain exactly 28 train and 9 validation panels.");
        }

        return panels.ToArray();
    }

    internal static float[] DecodeTarget(byte[] payload, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (width <= 0 || height <= 0 || payload.Length != checked(width * height * sizeof(float)))
        {
            throw new InvalidDataException("Target map byte count differs from its declared tensor shape.");
        }

        var values = new float[checked(width * height)];
        for (var index = 0; index < values.Length; index++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(index * sizeof(float), sizeof(float)));
            if (!float.IsFinite(value) || value is not (0f or 1f))
            {
                throw new InvalidDataException("Target maps must contain only finite binary float32 values.");
            }

            values[index] = value;
        }

        return values;
    }

    private static void ValidateObservation(
        OraclePanel panel,
        OcrAtomicDbDetection detection,
        OcrDbGeometryObservation? geometry,
        OcrDbPostprocessObservation? postprocess)
    {
        if (geometry is null || postprocess is null ||
            geometry.ImageWidth != panel.Width || geometry.ImageHeight != panel.Height ||
            geometry.TensorWidth != panel.TensorWidth || geometry.TensorHeight != panel.TensorHeight ||
            postprocess.ImageWidth != panel.Width || postprocess.ImageHeight != panel.Height ||
            postprocess.TensorWidth != panel.TensorWidth || postprocess.TensorHeight != panel.TensorHeight ||
            detection.Regions.Count != geometry.AcceptedContours.Count ||
            detection.Regions.Count != postprocess.Contours.Count(static item =>
                item.Disposition == OcrDbContourDisposition.Accepted) ||
            !detection.Regions.Select(static item => item.RegionId).SequenceEqual(
                geometry.AcceptedContours.Select(static item => item.ReturnedRegionId)))
        {
            throw new InvalidDataException("DB target-oracle output is inconsistent with its atomic observations.");
        }
    }

    private static AssemblyEvidence[] LockExecutingAssemblies(string root, List<FileStream> locks)
    {
        Assembly[] required =
        [
            typeof(OfficialDbTargetOracle).Assembly,
            typeof(ProductionOcrAdapter).Assembly,
            typeof(LocalOnnxTextRegionDetector).Assembly,
            typeof(InferenceRuntime).Assembly,
        ];
        AssemblyEvidence[] evidence = required
            .Distinct()
            .Select(assembly =>
            {
                string name = assembly.GetName().Name ??
                    throw new InvalidDataException("An executing assembly name is missing.");
                string path = Path.GetFullPath(assembly.Location);
                string digest = Hash(File.ReadAllBytes(path));
                locks.Add(OpenVerifiedLock(path, digest, "executing assembly"));
                return new AssemblyEvidence(name, Relative(root, path), digest);
            })
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ToArray();
        string[] expected =
        [
            "GraphReader.App", "GraphReader.Inference", "GraphReader.Ocr",
            "GraphReader.SyntheticRuntimeEvidence",
        ];
        if (!evidence.Select(static item => item.Name).SequenceEqual(expected))
        {
            throw new InvalidDataException("Target-oracle executing assembly inventory is incomplete.");
        }

        return evidence;
    }

    private static object Polygon(OcrPolygon polygon) => new
    {
        Points = polygon.Points.Select(static point => new { point.X, point.Y, IsFinite = point.IsFinite }),
    };

    private static BoundFile ReadDescriptor(
        string root,
        JsonElement descriptor,
        string? expectedSha256,
        string label,
        List<FileStream> locks)
    {
        RequireProperties(descriptor, ["path", "sha256"], label);
        string path = InsideRepository(root, RequiredText(descriptor, "path", label), label);
        string sha256 = RequireSha256(RequiredText(descriptor, "sha256", label), label);
        if (expectedSha256 is not null && !string.Equals(sha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} is not the pinned identity.");
        }

        byte[] bytes = ReadVerifiedLocked(path, sha256, label, locks);
        return new BoundFile(path, sha256, bytes);
    }

    private static byte[] ReadVerifiedLocked(
        string path,
        string expectedSha256,
        string label,
        List<FileStream> locks)
    {
        FileStream stream = OpenVerifiedLock(path, expectedSha256, label);
        locks.Add(stream);
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static FileStream OpenVerifiedLock(string path, string expectedSha256, string label)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{label} bytes differ from the authenticated SHA-256.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static JsonDocument ParseJson(byte[] bytes, string label)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is not valid canonical JSON.", exception);
        }
    }

    private static void RequireProperties(JsonElement value, string[] expected, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be an object.");
        }

        string[] actual = value.EnumerateObject().Select(static item => item.Name).ToArray();
        if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count() ||
            !actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException($"{label} has duplicate, unknown, or missing fields.");
        }
    }

    private static string RequiredText(JsonElement value, string propertyName, string label)
    {
        JsonElement property = value.GetProperty(propertyName);
        string? result = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return string.IsNullOrWhiteSpace(result)
            ? throw new InvalidDataException($"{label} {propertyName} is missing.")
            : result;
    }

    private static int PositiveInt32(JsonElement value, string propertyName, string label)
    {
        JsonElement property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int result) && result > 0
            ? result
            : throw new InvalidDataException($"{label} {propertyName} must be a positive integer.");
    }

    private static double[] ReadMatrix(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 9)
        {
            throw new InvalidDataException("Panel-to-source matrix must contain nine values.");
        }

        double[] matrix = value.EnumerateArray().Select(item =>
        {
            if (!item.TryGetDouble(out double result) || !double.IsFinite(result))
            {
                throw new InvalidDataException("Panel-to-source matrix must be finite.");
            }

            return result;
        }).ToArray();
        if (Math.Abs(matrix[6]) > 1e-12 || Math.Abs(matrix[7]) > 1e-12 ||
            Math.Abs(matrix[8] - 1) > 1e-12)
        {
            throw new InvalidDataException("Panel-to-source matrix is not an affine source mapping.");
        }

        return matrix;
    }

    private static string RequireSha256(string value, string label)
    {
        if (value.Length != 64 ||
            value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{label} SHA-256 must be 64 lowercase hexadecimal characters.");
        }

        return value;
    }

    private static string InsideRepository(string root, string pathValue, string label)
    {
        if (Path.IsPathRooted(pathValue))
        {
            throw new InvalidDataException($"{label} path must be repository-relative.");
        }

        string path = Path.GetFullPath(Path.Combine(root, pathValue));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} path escaped the repository.");
        }

        return path;
    }

    private static string InsideArtifacts(string root, string pathValue, string label)
    {
        string path = Path.GetFullPath(Path.IsPathRooted(pathValue)
            ? pathValue
            : Path.Combine(root, pathValue));
        string artifacts = Path.Combine(root, "artifacts");
        string prefix = artifacts + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} must be inside repository artifacts.");
        }

        return path;
    }

    private static string RepositoryRootFor(string requestPath)
    {
        string? current = Path.GetDirectoryName(requestPath);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current, "src")) &&
                Directory.Exists(Path.Combine(current, "tools")) &&
                Directory.Exists(Path.Combine(current, "artifacts")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        throw new InvalidDataException("Could not resolve the repository root from the request path.");
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class FixedProviderDiscovery : IExecutionProviderDiscovery
    {
        public IReadOnlyList<string> GetAvailableProviders() => Array.Empty<string>();
    }

    private sealed class NoStageCache : IStageCache
    {
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<byte[]?>(null);
        }

        public ValueTask PutAsync(
            StageCacheKey key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OracleSessionFactory(IReadOnlyList<OraclePanel> panels) : IInferenceSessionFactory
    {
        private readonly Queue<OraclePanel> remaining = new(panels);
        private int runCount;

        public int RunCount => Volatile.Read(ref runCount);

        public int RemainingCount
        {
            get
            {
                lock (remaining)
                {
                    return remaining.Count;
                }
            }
        }

        public ValueTask<IInferenceSession> CreateAsync(
            ModelIdentity model,
            InferenceProvider provider,
            CpuThreadConfiguration cpuConfiguration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider != InferenceProvider.Cpu || model.Sha256 != ParentModelSha256)
            {
                throw new InvalidOperationException("The target oracle supports only the bound parent identity on fake CPU execution.");
            }

            return ValueTask.FromResult<IInferenceSession>(new OracleSession(this));
        }

        private OraclePanel Take(InferenceInput input)
        {
            lock (remaining)
            {
                if (remaining.Count == 0)
                {
                    throw new InvalidOperationException("The target oracle received an unexpected inference request.");
                }

                OraclePanel panel = remaining.Dequeue();
                long[] expected = [1, 3, panel.TensorHeight, panel.TensorWidth];
                if (!input.Shape.SequenceEqual(expected) ||
                    input.Values.Length != checked(3 * panel.TensorHeight * panel.TensorWidth))
                {
                    throw new InvalidDataException("Production preprocessing produced a tensor shape different from the authenticated target.");
                }

                Interlocked.Increment(ref runCount);
                return panel;
            }
        }

        private sealed class OracleSession(OracleSessionFactory owner) : IInferenceSession
        {
            public InferenceProvider Provider => InferenceProvider.Cpu;

            public ValueTask<InferenceExecution> RunAsync(
                InferenceInput input,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OraclePanel panel = owner.Take(input);
                float[] output = (float[])panel.Probabilities.Clone();
                return ValueTask.FromResult(new InferenceExecution(
                    Array.AsReadOnly(output),
                    InferenceProvider.Cpu,
                    new StageTiming(0, 0, 0, 0, 0, false, false),
                    new MemoryDiagnostics(0, 0, 0, 0, output.Length)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed record BoundFile(string Path, string Sha256, byte[] Bytes);

    private sealed record OraclePanel(
        string PanelId,
        string Split,
        string SourceSha256,
        int Width,
        int Height,
        int TensorWidth,
        int TensorHeight,
        string TargetPath,
        string TargetSha256,
        double[] PanelToSourceMatrix,
        float[] Probabilities);

    private sealed record AssemblyEvidence(string Name, string Path, string Sha256);
}
