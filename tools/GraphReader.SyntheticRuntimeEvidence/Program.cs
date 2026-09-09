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

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: <input-manifest.json> <candidate.json> <candidate-sha256> <new-output-directory>");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                Status = "failed", Error = exception.Message, ProductionApproved = false,
                TrainingInputReady = false,
            }, JsonOptions));
            return exception is OperationCanceledException ? 130 : 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string inputPath = Path.GetFullPath(args[0]);
        string candidatePath = Path.GetFullPath(args[1]);
        string outputRoot = Path.GetFullPath(args[3]);
        RequireArtifactOutput(outputRoot);
        byte[] candidateBytes = File.ReadAllBytes(candidatePath);
        byte[] inputBytes = File.ReadAllBytes(inputPath);
        if (!string.Equals(Hash(candidateBytes), args[2], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Candidate descriptor checksum mismatch.");
        }
        using JsonDocument input = JsonDocument.Parse(inputBytes);
        using JsonDocument candidate = JsonDocument.Parse(candidateBytes);
        JsonElement inputs = input.RootElement;
        JsonElement config = candidate.RootElement;
        RequireKeys(inputs, "schema", "source", "preset", "seed", "split", "contains_truth", "contains_precomputed_masks", "images");
        if (Text(inputs, "schema") != "graphreader.synthetic-runtime-raster-inputs.v1" ||
            Text(inputs, "source") != "project-owned-synthetic-five-axis-family-v1" ||
            Text(inputs, "split") is not ("train" or "validation") ||
            inputs.GetProperty("contains_truth").GetBoolean() ||
            inputs.GetProperty("contains_precomputed_masks").GetBoolean())
        {
            throw new InvalidDataException("Only annotation-free project-owned synthetic train/dev raster inputs are accepted.");
        }
        if (Text(config, "schema") != "graphreader.local-synthetic-ocr-candidate.v1" ||
            config.GetProperty("production_approved").GetBoolean())
        {
            throw new InvalidDataException("An explicitly unapproved local candidate descriptor is required.");
        }
        foreach (JsonElement notice in config.GetProperty("license_inputs").EnumerateArray())
        {
            VerifyFile(Text(notice, "path"), Text(notice, "sha256"));
        }
        var images = new List<(JsonElement Record, string Path)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement record in inputs.GetProperty("images").EnumerateArray())
        {
            RequireKeys(record, "image", "image_sha256", "width", "height", "split", "family", "seed");
            string name = Text(record, "image");
            if (name != Path.GetFileName(name) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                !names.Add(name) || !hashes.Add(Text(record, "image_sha256")) ||
                Text(record, "split") != Text(inputs, "split"))
            {
                throw new InvalidDataException("Synthetic raster names must be unique local PNG basenames in the declared split.");
            }
            string path = Path.Combine(Path.GetDirectoryName(inputPath)!, name);
            VerifyFile(path, Text(record, "image_sha256"));
            images.Add((record, path));
        }
        if (images.Count == 0)
        {
            throw new InvalidDataException("The synthetic input exchange is empty.");
        }
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException("Use a new output directory; prior evidence is never replaced.");
        }

        string nativePath = Path.GetFullPath(Text(config, "native_path"));
        string nativeSha = Text(config, "native_sha256");
        string nativeScope = nativeSha.ToLowerInvariant() switch
        {
            "1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd" =>
                "existing-development-runtime-unapproved-for-release",
            "87c12460daba638b36e916ea2bb832d0759fbf094b8639919a7ce11b0cca5791" =>
                "reviewed-source-runtime-local-diagnostic",
            _ => throw new InvalidDataException("Native runtime has no recorded local diagnostic scope."),
        };
        if (Text(config, "native_scope") != nativeScope)
        {
            throw new InvalidDataException("Native scope does not match the pinned runtime bytes.");
        }
        // Keep the exact native file read-locked for this process. This is a local
        // diagnostic identity, not a production runtime approval.
        using var nativeLock = new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(nativeLock)), nativeSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native runtime checksum mismatch.");
        }
        nint nativeHandle = NativeLibrary.Load(nativePath);
        NativeLibrary.SetDllImportResolver(typeof(OpenCvSharp.Mat).Assembly,
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
        var total = Stopwatch.StartNew();
        ProductionOcrAdapter ocr = await ProductionOcrAdapter.CreateForLocalSyntheticCandidateEvaluationAsync(
            Descriptor(config.GetProperty("detector")), Descriptor(config.GetProperty("recognizer")),
            runtime, nativeSha, cancellationToken).ConfigureAwait(false);
        var axis = new ProductionAxisGeometryAdapter(nativeSha, isApproved: false);
        if (ocr.IsApproved || axis.IsApproved)
        {
            throw new InvalidOperationException("Synthetic evaluation must never approve a production adapter.");
        }
        var results = new List<object>();
        int completed = 0;
        foreach ((JsonElement record, string path) in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            string imageSha = Text(record, "image_sha256");
            string stage = "decode";
            int? regionCount = null;
            int? cropCount = null;
            object? emptyOcrDiagnostic = null;
            try
            {
                var image = new WorkflowImageEvidence(path, imageSha,
                    record.GetProperty("width").GetInt32(), record.GetProperty("height").GetInt32(),
                    WorkflowImageVariant.Original);
                var imported = new WorkflowImportedPanel(Guid.NewGuid(), Guid.NewGuid(), Path.GetFileName(path), image);
                var request = new ProductionWorkflowDetectionRequest(
                    new WorkflowPreparedPanel(imported, image, enhanced: null), image,
                    WorkflowImageVariant.Original, Guid.NewGuid(), Guid.NewGuid(),
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                ProductionDecodedRaster raster = new ProductionRasterFrameDecoder().Decode(request, cancellationToken);
                stage = "axis";
                ProductionAxisGeometryEvidence geometry = await axis
                    .DetectForLocalSyntheticCandidateEvaluationAsync(request, cancellationToken).ConfigureAwait(false);
                double left = geometry.Geometry.PlotPolygon.Points.Min(static point => point.X);
                double top = geometry.Geometry.PlotPolygon.Points.Min(static point => point.Y);
                double right = geometry.Geometry.PlotPolygon.Points.Max(static point => point.X);
                double bottom = geometry.Geometry.PlotPolygon.Points.Max(static point => point.Y);
                OcrDetectorImage detectorImage = raster.CreateOcrDetectorImage(geometry.Geometry, cancellationToken);
                stage = "ocr";
                ProductionOcrEvidence text = await ocr.RecognizeForLocalSyntheticCandidateEvaluationAsync(
                    request, raster, new OcrRectangle(left, top, right - left, bottom - top),
                    detectorImage, cancellationToken).ConfigureAwait(false);
                regionCount = text.Result.Regions.Count;
                cropCount = text.Result.Cache.CropCount;
                if (regionCount == 0)
                {
                    // Diagnose a failed stage without changing its result or
                    // substituting these proposals into the seed composer.
                    LocalSyntheticOcrModelDescriptor descriptor = Descriptor(config.GetProperty("detector"));
                    var rawDetector = new LocalOnnxTextRegionDetector(runtime.Runtime,
                        ProductionOcrAdapter.ReadDetectionOptions(descriptor.Identity, descriptor.ManifestPath));
                    IReadOnlyList<OcrDetectedRegion> modelRegions = await rawDetector
                        .DetectAsync(detectorImage.Image, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<OcrDetectedRegion> componentRegions = await new ConnectedComponentTextRegionDetector()
                        .DetectAsync(detectorImage.Image, cancellationToken).ConfigureAwait(false);
                    emptyOcrDiagnostic = new { ModelRegions = modelRegions, ComponentRegions = componentRegions };
                }
                stage = "seed-composition";
                ProductionDetectionMaskSeed seed = ProductionDetectionMaskComposer.BuildSeedForLocalSyntheticCandidateEvaluation(
                    request, raster, geometry, text.ModelEvidence, text.Result, cancellationToken);
                string caseRoot = Path.Combine(outputRoot, imageSha);
                Directory.CreateDirectory(caseRoot);
                object ocrMask = WritePlane(caseRoot, "ocr-seed.f32", seed.CopyOcrMask().Values.ToArray());
                object geometryMask = WritePlane(caseRoot, "geometry-seed.f32", seed.CopyArtifactMask().Values.ToArray());
                object sourceGray = WriteBytes(caseRoot, "source-gray8.bin", raster.CreateOcrImage().Pixels.ToArray());
                results.Add(new
                {
                    ImageSha256 = imageSha, Width = raster.Width, Height = raster.Height,
                    Status = "seed-completed", ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                    OcrMask = ocrMask, GeometryMask = geometryMask, SourceGray = sourceGray,
                    DetectorInputSha256 = detectorImage.PixelSha256,
                    DetectorBgrSha256 = detectorImage.BgrPixelSha256,
                    Axis = geometry, Ocr = text.Result, OcrModels = text.ModelEvidence,
                });
                completed++;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
            {
                results.Add(new { ImageSha256 = imageSha, Status = "failed", Stage = stage,
                    DetectedRegionCount = regionCount, RecognitionCropCount = cropCount, Error = exception.Message,
                    EmptyOcrDiagnostic = emptyOcrDiagnostic,
                    ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
            }
        }
        object report = new
        {
            Schema = "graphreader.synthetic-runtime-seed-evidence.v1",
            Scope = "local-synthetic-seed-diagnostic", ProductionApproved = false, TrainingInputReady = false,
            CompleteArtifactMask = false, MissingStage = "residual-arrow-bracket-legend-intersection-artifact-mask",
            InputManifestSha256 = Hash(inputBytes), CandidateSha256 = Hash(candidateBytes),
            NativeSha256 = nativeSha, NativeScope = nativeScope,
            RuntimeAssemblies = new[]
            {
                typeof(Program).Assembly, typeof(ProductionOcrAdapter).Assembly,
                typeof(GraphReader.Axis.AxisGeometryDetector).Assembly, typeof(OcrPipeline).Assembly,
                typeof(InferenceRuntime).Assembly,
            }.Select(static assembly => new
            {
                Name = assembly.GetName().Name,
                Sha256 = Hash(File.ReadAllBytes(assembly.Location)),
            }).ToArray(),
            Count = images.Count, Completed = completed, Failed = images.Count - completed,
            ElapsedMilliseconds = total.Elapsed.TotalMilliseconds, Cases = results,
        };
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "report.json"),
            JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { Completed = completed, Failed = images.Count - completed,
            ProductionApproved = false, TrainingInputReady = false, Output = outputRoot }, JsonOptions));
        return completed == images.Count ? 0 : 1;
    }

    private static LocalSyntheticOcrModelDescriptor Descriptor(JsonElement record) => new(
        new ModelIdentity(Text(record, "model_id"), Text(record, "model_version"),
            Text(record, "model_sha256"), Text(record, "model_path")),
        Text(record, "manifest_path"), Text(record, "manifest_sha256"));

    private static void RequireArtifactOutput(string outputRoot)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
               !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        if (directory is null || !outputRoot.StartsWith(
                Path.Combine(directory.FullName, "artifacts") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Synthetic evidence output must stay under this repository's ignored artifacts directory.");
        }
    }

    private static void RequireKeys(JsonElement record, params string[] keys)
    {
        string[] actual = record.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != keys.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(keys))
        {
            throw new InvalidDataException("Unexpected or duplicate synthetic input fields; truth and masks must stay outside the runtime input exchange.");
        }
    }

    private static string Text(JsonElement record, string key) =>
        record.GetProperty(key).GetString() ?? throw new InvalidDataException($"Missing {key}.");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void VerifyFile(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Input checksum mismatch: {Path.GetFileName(path)}.");
        }
    }

    private static object WritePlane(string directory, string name, float[] values)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Float evidence uses little-endian float32.");
        }
        var bytes = new byte[checked(values.Length * sizeof(float))];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return WriteBytes(directory, name, bytes);
    }

    private static object WriteBytes(string directory, string name, byte[] bytes)
    {
        using var output = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
        output.Write(bytes);
        return new { File = name, Sha256 = Hash(bytes), ByteCount = bytes.Length };
    }
}
