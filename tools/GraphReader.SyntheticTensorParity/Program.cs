// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
// Diagnostic only. Calls the actual detector preprocessing methods with the
// experimental 512/128 settings; this does not approve production behavior.
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string Root = @"E:\Graph-auto-reader";
    private static readonly (string Id, string Sha, int Width, int Height)[] Images =
    [
        ("fa85e1e8-4ea8-5c81-9434-75e0677bf29d", "4d5b9837c5a8d3581c21261b89bbf2bf209911e0a543299f729787538f26e566", 361, 240),
        ("fcb676f8-cc16-5d93-b6c8-be0d4659200e", "59549447b0447b5e53d2b816723b941c1bfb85bb1b061b34c04794377b1676f5", 863, 395),
        ("0a7d5146-4870-5eb1-a654-eeeb7624097b", "c7c8aaed3c20d0d1b5aec8a2aeb6cbd97c79e85597188506b744cd989101d534", 1338, 492),
        ("6beef600-86a4-5e73-815f-ae96e861bb91", "9f31d5e5c518abb39012c3d2c91091b7bfb47b0a29312c084aaf8954b5dcc4b8", 1338, 492),
    ];

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || !BitConverter.IsLittleEndian)
                throw new InvalidDataException("Expected output directory, run identity path, and pinned identity SHA-256.");
            string output = Path.GetFullPath(args[0]);
            string artifacts = Path.GetFullPath(Path.Combine(Root, "artifacts")) + Path.DirectorySeparatorChar;
            if (!output.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output) || File.Exists(output))
                throw new InvalidDataException("Output must be a new project artifacts directory.");
            string identityPath = Path.GetFullPath(args[1]);
            if (!identityPath.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Run identity must be project-local.");
            byte[] identityBytes = ReadLocked(identityPath);
            if (Hash(identityBytes) != args[2])
                throw new InvalidDataException("Run identity hash differs.");
            using var identity = JsonDocument.Parse(identityBytes);
            string cvVersion = Cv2.GetVersionString() ?? throw new InvalidDataException("OpenCV version missing.");
            var runtime = RuntimeIdentities();
            VerifyRuntimeIdentity(identity.RootElement, runtime);
            Type detector = typeof(LocalOnnxTextRegionDetector);
            MethodInfo dimensions = detector.GetMethod("TensorDimensions", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Production tensor dimensions method missing.");
            MethodInfo create = detector.GetMethod("CreateTensor", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Production tensor creation method missing.");
            var options = new LocalOnnxTextRegionDetectorOptions(new ModelIdentity("diagnostic", "1", new string('0', 64), "diagnostic"))
            {
                MaximumSideLength = 512,
                DimensionMultiple = 128,
                InputColorMode = OcrTensorColorMode.Bgr,
                InputLayout = OcrTensorLayout.ChannelsFirst,
                PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DbPostprocessV1,
            };
            var rows = new List<object>();
            Directory.CreateDirectory(output);
            for (int index = 0; index < Images.Length; index++)
            {
                var item = Images[index];
                string path = Path.Combine(Root, "artifacts", "synthetic-tick-family-v1-20261030", "dataset", "images", item.Id + ".png");
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var buffer = new MemoryStream();
                locked.CopyTo(buffer);
                byte[] encoded = buffer.ToArray();
                if (Hash(encoded) != item.Sha)
                    throw new InvalidDataException("Authenticated training raster changed.");
                var evidence = new WorkflowImageEvidence(path, item.Sha, item.Width, item.Height, WorkflowImageVariant.Original);
                var imported = new WorkflowImportedPanel(Guid.Parse(item.Id), Guid.NewGuid(), "synthetic.png", evidence);
                var prepared = new WorkflowPreparedPanel(imported, evidence, null);
                var request = new ProductionWorkflowDetectionRequest(prepared, evidence, WorkflowImageVariant.Original,
                    Guid.NewGuid(), Guid.NewGuid(), encoded);
                OcrImage image = new ProductionRasterFrameDecoder().Decode(request, CancellationToken.None).CreateOcrImage();
                var shape = ((int Width, int Height))(dimensions.Invoke(null, [image, options])
                    ?? throw new InvalidDataException("Production dimensions missing."));
                float[] tensor = (float[])(create.Invoke(null, [image, shape.Width, shape.Height, options, CancellationToken.None])
                    ?? throw new InvalidDataException("Production tensor missing."));
                if (tensor.Length != 3 * shape.Width * shape.Height || tensor.Any(static value => !float.IsFinite(value)))
                    throw new InvalidDataException("Production tensor shape or values invalid.");
                byte[] tensorBytes = MemoryMarshal.AsBytes(tensor.AsSpan()).ToArray();
                byte[] bgrBytes = image.BgrPixels?.Pixels.ToArray() ?? throw new InvalidDataException("BGR image missing.");
                string tensorName = $"train-{index}.tensor.f32";
                string bgrName = $"train-{index}.bgr.u8";
                WriteNew(Path.Combine(output, tensorName), tensorBytes);
                WriteNew(Path.Combine(output, bgrName), bgrBytes);
                var productionShape = ((int Width, int Height))(dimensions.Invoke(null, [image, options with { MaximumSideLength = 960 }])
                    ?? throw new InvalidDataException("Production dimensions missing."));
                float[] productionTensor = (float[])(create.Invoke(null, [image, productionShape.Width, productionShape.Height,
                    options with { MaximumSideLength = 960 }, CancellationToken.None])
                    ?? throw new InvalidDataException("Production 960 tensor missing."));
                if (productionTensor.Length != 3 * productionShape.Width * productionShape.Height ||
                    productionTensor.Any(static value => !float.IsFinite(value)))
                    throw new InvalidDataException("Production 960 tensor shape or values invalid.");
                byte[] productionBytes = MemoryMarshal.AsBytes(productionTensor.AsSpan()).ToArray();
                string productionName = $"train-{index}.production-960.f32";
                WriteNew(Path.Combine(output, productionName), productionBytes);
                rows.Add(new { source_image_sha256 = item.Sha, tensor_file = tensorName, tensor_sha256 = Hash(tensorBytes),
                    shape = new[] { 1, 3, shape.Height, shape.Width }, bgr_file = bgrName, bgr_sha256 = Hash(bgrBytes),
                    production_960_shape = new[] { 1, 3, productionShape.Height, productionShape.Width },
                    production_960_file = productionName, production_960_sha256 = Hash(productionBytes) });
            }
            VerifyRuntimeIdentity(identity.RootElement, RuntimeIdentities());
            if (Hash(ReadLocked(identityPath)) != args[2])
                throw new InvalidDataException("Run identity changed during capture.");
            var report = new { schema = "graphreader.synthetic-tensor-parity-capture.v1", images = rows,
                run_identity_sha256 = args[2], capture_source_sha256 = runtime["capture_source"],
                capture_project_sha256 = runtime["capture_project"], capture_executable_sha256 = runtime["capture_executable"],
                decoder_assembly_sha256 = runtime["decoder_assembly"], detector_assembly_sha256 = runtime["detector_assembly"],
                opencv_assembly_sha256 = runtime["opencv_assembly"], opencv_native_sha256 = runtime["opencv_native"],
                additional_runtime_sha256 = runtime.Where(item => item.Key.StartsWith("project_", StringComparison.Ordinal) ||
                    item.Key is "runtime_deps" or "runtime_config").ToDictionary(item => item.Key, item => item.Value),
                shared_framework = RuntimeInformation.FrameworkDescription,
                os_description = RuntimeInformation.OSDescription,
                process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                opencv_version = cvVersion, maximum_side_length = 512, dimension_multiple = 128,
                training_input_approved = false, model_inference = false, optimizer_steps = 0 };
            WriteNew(Path.Combine(output, "capture.json"), JsonSerializer.SerializeToUtf8Bytes(report));
            Console.WriteLine("{\"status\":\"captured\",\"training_input_approved\":false}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] ReadLocked(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }
    private static Dictionary<string, string> RuntimeIdentities()
    {
        using Process process = Process.GetCurrentProcess();
        string native = process.Modules.Cast<ProcessModule>()
            .Single(module => string.Equals(module.ModuleName, "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase)).FileName;
        var paths = new Dictionary<string, string>
        {
            ["capture_source"] = Path.Combine(Root, "tools", "GraphReader.SyntheticTensorParity", "Program.cs"),
            ["capture_project"] = Path.Combine(Root, "tools", "GraphReader.SyntheticTensorParity", "GraphReader.SyntheticTensorParity.csproj"),
            ["capture_executable"] = typeof(Program).Assembly.Location,
            ["decoder_assembly"] = typeof(ProductionRasterFrameDecoder).Assembly.Location,
            ["detector_assembly"] = typeof(LocalOnnxTextRegionDetector).Assembly.Location,
            ["opencv_assembly"] = typeof(Mat).Assembly.Location,
            ["opencv_native"] = native,
        };
        string directory = AppContext.BaseDirectory;
        foreach (string name in new[] { "Axis", "Domain", "Export", "Imaging", "Inference", "Legends", "Markers", "Pdf", "Phases", "SuperResolution" })
            paths["project_" + name.ToLowerInvariant()] = Path.Combine(directory, "GraphReader." + name + ".dll");
        paths["runtime_deps"] = Path.Combine(directory, "GraphReader.SyntheticTensorParity.deps.json");
        paths["runtime_config"] = Path.Combine(directory, "GraphReader.SyntheticTensorParity.runtimeconfig.json");
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(item =>
            item.GetName().Name?.StartsWith("GraphReader.", StringComparison.Ordinal) == true))
        {
            string expected = Path.Combine(directory, assembly.GetName().Name + ".dll");
            if (!string.Equals(Path.GetFullPath(assembly.Location), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loaded project assembly escaped app-local runtime directory.");
        }
        return paths.ToDictionary(item => item.Key, item => Hash(ReadLocked(item.Value)), StringComparer.Ordinal);
    }
    private static void VerifyRuntimeIdentity(JsonElement identity, Dictionary<string, string> actual)
    {
        if (identity.GetProperty("schema").GetString() != "graphreader.synthetic-tensor-parity-run-identity.v1")
            throw new InvalidDataException("Run identity schema differs.");
        JsonElement files = identity.GetProperty("files");
        foreach ((string key, string sha) in actual)
            if (files.GetProperty(key).GetProperty("sha256").GetString() != sha)
                throw new InvalidDataException("Run identity runtime hash differs.");
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
