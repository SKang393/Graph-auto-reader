// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string Root = @"E:\Graph-auto-reader";
    private const string InputPath = "artifacts/manual-axis-ocr-diagnostic-v1/input.json";
    private const string OutputPath = "artifacts/manual-axis-ocr-diagnostic-v1/predictions-v2.json";
    private const string NumericPath = "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx";
    private const string NumericHash = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string OfficialPath = "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx";
    private const string OfficialHash = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    private const string AlphabetPath = "ml/ocr/official_bakeoff/runs/extracted/en_PP-OCRv5_mobile_rec_infer/inference.yml";
    private const string AlphabetHash = "27e91d0582f40168aa218303c76e184bc78fa7a5d105aad0cfbad8458b441067";
    private const string AmbiguityPath = "ml/ocr/ambiguity_source_group_classifier_v3/artifacts/P2-run/graph-ambiguity-source-group-v3-p2.onnx";
    private const string AmbiguityHash = "b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
            string inputRelative = InputPath;
            string outputRelative = OutputPath;
            string expectedInput;
            if (args.Length == 2 && args[0] == "--expected-input-sha256")
            {
                expectedInput = args[1];
            }
            else
            {
                Require(args.Length == 6 && args[0] == "--input" && args[2] == "--expected-input-sha256" && args[4] == "--output",
                    "Expected project-local input, hash, and output are required.");
                inputRelative = args[1];
                expectedInput = args[3];
                outputRelative = args[5];
            }
            const string diagnosticRoot = "artifacts/manual-axis-ocr-diagnostic-v1/";
            Require(inputRelative.StartsWith(diagnosticRoot, StringComparison.Ordinal) &&
                outputRelative.StartsWith(diagnosticRoot, StringComparison.Ordinal), "Diagnostic paths escaped their task directory.");
            string taskDirectory = ProjectPath(diagnosticRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(ProjectPath(inputRelative).StartsWith(taskDirectory, StringComparison.OrdinalIgnoreCase),
                "Input escaped its task directory.");
            string output = ProjectPath(outputRelative);
            Require(output.StartsWith(taskDirectory, StringComparison.OrdinalIgnoreCase), "Output escaped its task directory.");
            Require(!File.Exists(output), "Output already exists.");
            byte[] inputBytes = await ReadPinned(inputRelative, expectedInput, 1_000_000);
            using JsonDocument input = JsonDocument.Parse(inputBytes);
            Require(input.RootElement.GetProperty("schema").GetString() == "graphreader.manual-axis-ocr-input.v1" &&
                input.RootElement.GetProperty("source_split").GetString() == "internal_train_build", "Input scope differs.");
            _ = await ReadPinned(NumericPath, NumericHash, 20_000_000);
            _ = await ReadPinned(OfficialPath, OfficialHash, 20_000_000);
            _ = await ReadPinned(AmbiguityPath, AmbiguityHash, 20_000_000);
            string yaml = Encoding.UTF8.GetString(await ReadPinned(AlphabetPath, AlphabetHash, 100_000));
            string alphabet = ReadAlphabet(yaml);
            Require(Hash(Encoding.UTF8.GetBytes(alphabet)) == OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
                "Official alphabet differs.");

            var frames = new Dictionary<string, OcrImage>(StringComparer.Ordinal);
            var entries = new List<Entry>();
            foreach (JsonElement row in input.RootElement.GetProperty("rows").EnumerateArray())
            {
                string id = row.GetProperty("id").GetString()!;
                string imagePath = row.GetProperty("image_path").GetString()!;
                string imageHash = row.GetProperty("image_sha256").GetString()!;
                Require(imagePath.StartsWith("artifacts/manual-dig-train-anchor-review-v1/images/", StringComparison.Ordinal),
                    "Image is outside the selected train-only review.");
                if (!frames.TryGetValue(imageHash, out OcrImage? image))
                {
                    byte[] bytes = await ReadPinned(imagePath, imageHash, 64_000_000);
                    using Mat decoded = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
                    Require(!decoded.Empty() && decoded.Type() == MatType.CV_8UC1 && decoded.IsContinuous(), "Image decode failed.");
                    byte[] pixels = new byte[checked(decoded.Width * decoded.Height)];
                    Marshal.Copy(decoded.Data, pixels, 0, pixels.Length);
                    image = new OcrImage(decoded.Width, decoded.Height, decoded.Width, pixels, OcrSourceImage.Original,
                        OcrFrameTransform.Identity, CanonicalOriginalWidth: decoded.Width, CanonicalOriginalHeight: decoded.Height);
                    frames.Add(imageHash, image);
                }
                double[] box = row.GetProperty("box_xyxy").EnumerateArray().Select(static item => item.GetDouble()).ToArray();
                Require(box.Length == 4 && box.All(double.IsFinite) && box[0] >= 0 && box[1] >= 0 &&
                    box[2] <= image.Width && box[3] <= image.Height && box[2] > box[0] && box[3] > box[1], "Invalid label box.");
                entries.Add(CreateEntry(id, image, box));
            }
            Require(entries.Count is > 0 and <= 500 &&
                entries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == entries.Count &&
                frames.Count is > 0 and <= 20, "Unexpected training pilot size.");

            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var cache = new ProviderCache();
            var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            await using var runtime = new InferenceRuntime(registry, new BoundedInferenceScheduler(capacity: 4, workerCount: 1),
                cache, allowedProviders: providers);
            ModelIdentity Identity(string id, string version, string hash, string path) => new(id, version, hash, ProjectPath(path));
            var numeric = new LocalOnnxComponentTextRecognizer(runtime, new LocalOnnxComponentTextRecognizerOptions(
                Identity("graph-numeric-component-ensemble-v5", "0.0.21-p1", NumericHash, NumericPath), "0123456789.-%")
                { StageVersion = "manual-axis-ocr-diagnostic-v1", AllowedProviders = providers });
            var officialBase = new LocalOnnxTextRecognizer(runtime, new LocalOnnxTextRecognizerOptions(
                Identity("en_PP-OCRv5_mobile_rec", "0.0.21-converted", OfficialHash, OfficialPath), alphabet)
            {
                InputWidth = 320, InputHeight = 48, InputChannels = 3, InputLayout = OcrTensorLayout.ChannelsFirst,
                InputColorMode = OcrTensorColorMode.GrayscaleReplicated, OutputLayout = OcrOutputLayout.BatchTimeClass,
                OutputActivation = OcrRecognitionOutputActivation.Probabilities, ExpectedTimeSteps = 40, BlankClassIndex = 0,
                MaximumAlternatives = 1, InputName = "x", OutputName = "fetch_name_0", StageVersion = "manual-axis-official-v1",
                ChannelMeans = [0.5f, 0.5f, 0.5f], ChannelScales = [2f, 2f, 2f], AllowedProviders = providers,
            });
            var official = new OcrV8OfficialTextRecognizer(officialBase, new OcrV8SourcePostprocessor(runtime,
                new LocalOnnxAmbiguitySourceGroupOptions(Identity("graph-ambiguity-source-group-v3-p2", "0.0.21-p2", AmbiguityHash, AmbiguityPath))
                { AllowedProviders = providers }));
            Dictionary<string, Outcome> numericOut = await Recognize(numeric, entries, static entry => entry.Numeric);
            Dictionary<string, Outcome> officialOut = await Recognize(official, entries, static entry => entry.Official);
            Require(cache.ProviderCalls.Count == 1 && cache.ProviderCalls.ContainsKey(InferenceProvider.Cpu), "Unexpected provider.");
            var report = new
            {
                schema = "graphreader.manual-axis-ocr-predictions.v1",
                status = "training-pilot-diagnostic-not-acceptance",
                input_sha256 = Hash(inputBytes), inference_provider = "CPUExecutionProvider", optimizer_steps = 0,
                numeric_model_sha256 = NumericHash, official_model_sha256 = OfficialHash, ambiguity_model_sha256 = AmbiguityHash,
                rows = entries.Select(entry => new { id = entry.Id, numeric = numericOut[entry.Id], official = officialOut[entry.Id] }).ToArray(),
            };
            await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions);
            Console.WriteLine($"completed rows={entries.Count}; provider=CPUExecutionProvider; optimizer_steps=0");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"BLOCKED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static Entry CreateEntry(string id, OcrImage image, double[] box)
    {
        var region = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(new OcrRectangle(box[0], box[1], box[2] - box[0], box[3] - box[1])), 0, 1);
        OcrCrop Crop(OcrCropBatcherOptions options)
        {
            IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, [region], options);
            Require(batches.Count == 1 && batches[0].Count == 1 && batches[0][0].OriginalPolygon.Bounds == region.Polygon.Bounds,
                "Cropper changed source geometry.");
            return batches[0][0];
        }
        return new Entry(id,
            Crop(new OcrCropBatcherOptions { TargetWidth = 128, TargetHeight = 32, BatchSize = 16, HorizontalPaddingPixels = 12,
                VerticalPaddingPixels = 1, VerticalContentPaddingRatio = 0.25, ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 1f }),
            Crop(new OcrCropBatcherOptions { TargetWidth = 320, TargetHeight = 48, BatchSize = 16, HorizontalPaddingPixels = 8,
                VerticalPaddingPixels = 2, ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 0.5f }));
    }

    private static int SelfTest()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 100 * 80).ToArray();
        var image = new OcrImage(100, 80, 100, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity,
            CanonicalOriginalWidth: 100, CanonicalOriginalHeight: 80);
        Entry entry = CreateEntry("fixture", image, [20, 30, 40, 45]);
        Require(entry.Numeric.Width == 128 && entry.Numeric.Height == 32 && entry.Official.Width == 320 && entry.Official.Height == 48 &&
            entry.Official.SourceCrop is not null, "Cropper self-test failed.");
        Console.WriteLine("PASS: original-pixel crop geometry and reader input sizes");
        return 0;
    }

    private static async Task<Dictionary<string, Outcome>> Recognize(ITextRecognizer recognizer, List<Entry> entries, Func<Entry, OcrCrop> crop)
    {
        var output = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        foreach (Entry[] batch in entries.Chunk(16))
        {
            IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(batch.Select(crop).ToArray(), CancellationToken.None);
            Require(results.Count == batch.Length, "Recognizer result count differs.");
            for (int i = 0; i < batch.Length; i++)
            {
                OcrRecognitionAlternative? best = results[i].Alternatives.OrderByDescending(static item => item.Confidence).FirstOrDefault();
                output.Add(batch[i].Id, new Outcome(best?.Text, best?.Confidence, results[i].Failure?.Code));
            }
        }
        return output;
    }

    private static string ProjectPath(string relative)
    {
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Path escaped project root.");
        string volume = Path.GetPathRoot(path) ?? throw new InvalidDataException("Path volume is missing.");
        string current = volume;
        foreach (string part in Path.GetRelativePath(volume, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Path contains a reparse point.");
        }
        return path;
    }

    private static async Task<byte[]> ReadPinned(string relative, string expected, int limit)
    {
        string path = ProjectPath(relative);
        Require(new FileInfo(path).Length <= limit, "Pinned file exceeds its byte limit.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(bytes.Length <= limit && Hash(bytes) == expected, "Pinned file length or hash differs.");
        return bytes;
    }

    private static string ReadAlphabet(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = Array.FindIndex(lines, static line => line.Trim() == "character_dict:");
        Require(start >= 0, "Character dictionary missing.");
        var result = new StringBuilder();
        for (int i = start + 1; i < lines.Length && lines[i].StartsWith("  - ", StringComparison.Ordinal); i++)
        {
            string value = lines[i][4..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            result.Append(value);
        }
        if (!result.ToString().Contains(' ', StringComparison.Ordinal)) result.Append(' ');
        return result.ToString();
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed record Entry(string Id, OcrCrop Numeric, OcrCrop Official);
    private sealed record Outcome(string? Text, double? Confidence, string? Failure);
    private sealed class ProviderCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> ProviderCalls { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            Require(value.Length >= 6, "Provider cache value is too short.");
            InferenceProvider provider = (InferenceProvider)value.Span[5];
            ProviderCalls[provider] = ProviderCalls.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
