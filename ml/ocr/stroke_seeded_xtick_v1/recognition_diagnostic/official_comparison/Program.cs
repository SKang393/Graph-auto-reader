// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

// This compares two existing application recognizers on a consumed synthetic
// development probe. It is not a model approval or a release gate.
internal static class Program
{
    private const string Corpus = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3";
    private const string ManifestHash = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba";
    private const string PredictionsHash = "235d39476cf3ef498d0c026f05e4c9eb626fe43c853d0af49ab6d0bef2d54097";
    private const string OfficialPath = "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx";
    private const string OfficialHash = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    private const string YamlPath = "ml/ocr/official_bakeoff/runs/extracted/en_PP-OCRv5_mobile_rec_infer/inference.yml";
    private const string YamlHash = "27e91d0582f40168aa218303c76e184bc78fa7a5d105aad0cfbad8458b441067";
    private const string NumericPath = "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx";
    private const string NumericHash = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string AmbiguityPath = "ml/ocr/ambiguity_source_group_classifier_v3/artifacts/P2-run/graph-ambiguity-source-group-v3-p2.onnx";
    private const string AmbiguityHash = "b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: OfficialComparison <project-root> <report-path>");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[0]);
            byte[] manifestBytes = await ReadPinned(root, $"{Corpus}/manifest.json", ManifestHash, 5_000_000);
            byte[] predictionBytes = await ReadPinned(root,
                "artifacts/stroke-seeded-xtick-v1-p2-v5-recognition-probe-v1/predictions.json", PredictionsHash, 5_000_000);
            byte[] yaml = await ReadPinned(root, YamlPath, YamlHash, 100_000);
            await ReadPinned(root, OfficialPath, OfficialHash, 20_000_000);
            await ReadPinned(root, NumericPath, NumericHash, 20_000_000);
            await ReadPinned(root, AmbiguityPath, AmbiguityHash, 20_000_000);
            string alphabet = ReadAlphabet(Encoding.UTF8.GetString(yaml));
            Require(Hash(Encoding.UTF8.GetBytes(alphabet)) == OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
                "Official alphabet differs from the application composition.");

            using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
            using JsonDocument predictions = JsonDocument.Parse(predictionBytes);
            Require(predictions.RootElement.GetProperty("probe_manifest_sha256").GetString() == ManifestHash,
                "Predictions refer to a different probe manifest.");
            var manifestRows = manifest.RootElement.GetProperty("records").EnumerateArray()
                .ToDictionary(row => row.GetProperty("case_id").GetString()!, StringComparer.Ordinal);

            var capture = new ProviderCaptureCache();
            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            await using var runtime = new InferenceRuntime(registry,
                new BoundedInferenceScheduler(capacity: 4, workerCount: 1), capture, allowedProviders: providers);
            ModelIdentity Identity(string id, string version, string hash, string path) =>
                new(id, version, hash, Path.Combine(root, path));
            var numeric = new LocalOnnxComponentTextRecognizer(runtime,
                new LocalOnnxComponentTextRecognizerOptions(
                    Identity("graph-numeric-component-ensemble-v5", "0.0.21-p1", NumericHash, NumericPath), "0123456789.-%")
                { StageVersion = "official-comparison-numeric-v1", AllowedProviders = providers });
            var officialBase = new LocalOnnxTextRecognizer(runtime,
                new LocalOnnxTextRecognizerOptions(
                    Identity("en_PP-OCRv5_mobile_rec", "0.0.21-converted", OfficialHash, OfficialPath), alphabet)
                {
                    InputWidth = 320, InputHeight = 48, InputChannels = 3,
                    InputLayout = OcrTensorLayout.ChannelsFirst, InputColorMode = OcrTensorColorMode.GrayscaleReplicated,
                    OutputLayout = OcrOutputLayout.BatchTimeClass, OutputActivation = OcrRecognitionOutputActivation.Probabilities,
                    ExpectedTimeSteps = 40, BlankClassIndex = 0, MaximumAlternatives = 1,
                    InputName = "x", OutputName = "fetch_name_0", StageVersion = "official-comparison-general-v1",
                    ChannelMeans = [0.5f, 0.5f, 0.5f], ChannelScales = [2f, 2f, 2f], AllowedProviders = providers,
                });
            var official = new OcrV8OfficialTextRecognizer(officialBase,
                new OcrV8SourcePostprocessor(runtime,
                    new LocalOnnxAmbiguitySourceGroupOptions(
                        Identity("graph-ambiguity-source-group-v3-p2", "0.0.21-p2", AmbiguityHash, AmbiguityPath))
                    { AllowedProviders = providers }));

            var rows = new List<Sample>();
            var entries = new List<Entry>();
            foreach (JsonElement prediction in predictions.RootElement.GetProperty("records").EnumerateArray())
            {
                string id = prediction.GetProperty("case_id").GetString()!;
                Require(manifestRows.TryGetValue(id, out JsonElement source), "Prediction is absent from manifest.");
                string imagePath = source.GetProperty("image_path").GetString()!;
                string annotationPath = source.GetProperty("annotation_path").GetString()!;
                string imageHash = source.GetProperty("image_sha256").GetString()!;
                string annotationHash = source.GetProperty("annotation_sha256").GetString()!;
                Require(prediction.GetProperty("image_sha256").GetString() == imageHash &&
                    prediction.GetProperty("annotation_sha256").GetString() == annotationHash, "Prediction input hash mismatch.");
                byte[] png = await ReadPinned(root, $"{Corpus}/{imagePath}", imageHash, 1_000_000);
                byte[] annotationBytes = await ReadPinned(root, $"{Corpus}/{annotationPath}", annotationHash, 100_000);
                using JsonDocument annotation = JsonDocument.Parse(annotationBytes);
                using Mat decoded = Cv2.ImDecode(png, ImreadModes.Grayscale);
                Require(!decoded.Empty() && decoded.Width == 256 && decoded.Height == 96 && decoded.Type() == MatType.CV_8UC1,
                    "Unexpected probe raster.");
                byte[] pixels = new byte[decoded.Width * decoded.Height];
                System.Runtime.InteropServices.Marshal.Copy(decoded.Data, pixels, 0, pixels.Length);
                var image = new OcrImage(decoded.Width, decoded.Height, decoded.Width, pixels,
                    OcrSourceImage.Original, OcrFrameTransform.Identity,
                    CanonicalOriginalWidth: decoded.Width, CanonicalOriginalHeight: decoded.Height);
                JsonElement box = prediction.GetProperty("top_one_xyxy_normalized");
                double x0 = box[0].GetDouble() * 256, y0 = box[1].GetDouble() * 96;
                double x1 = box[2].GetDouble() * 256, y1 = box[3].GetDouble() * 96;
                var predicted = new OcrRectangle(x0, y0, x1 - x0, y1 - y0);
                Require(predicted.IsValid && x0 >= 0 && y0 >= 0 && x1 <= 256 && y1 <= 96, "Invalid predicted box.");
                string kind = prediction.GetProperty("kind").GetString()!;
                double score = prediction.GetProperty("top_one_score").GetDouble();
                OcrRectangle? target = null;
                string? expected = null;
                if (kind == "positive")
                {
                    expected = annotation.RootElement.GetProperty("expected_tick_label").GetString();
                    Require(expected is not null && prediction.GetProperty("expected_text").GetString() == expected,
                        "Expected numeric label mismatch.");
                    JsonElement targets = annotation.RootElement.GetProperty("targets");
                    Require(targets.GetArrayLength() == 1, "Positive annotation must have one target.");
                    JsonElement gt = targets[0].GetProperty("target_crop_xyxy");
                    target = new OcrRectangle(gt[0].GetDouble(), gt[1].GetDouble(),
                        gt[2].GetDouble() - gt[0].GetDouble(), gt[3].GetDouble() - gt[1].GetDouble());
                    Require(target.Value.IsValid, "Invalid target box.");
                }
                else Require(kind == "background_only", "Unexpected probe record kind.");

                var sample = new Sample(id, kind, expected, score, target is not null ? IoU(predicted, target.Value) : 0);
                rows.Add(sample);
                entries.Add(CreateEntry(image, sample, "pred", predicted));
                if (target is not null) entries.Add(CreateEntry(image, sample, "gt", target.Value));
            }
            Require(rows.Count == 323 && rows.Count(row => row.Kind == "positive") == 165 && entries.Count == 488,
                "Unexpected fixed probe inventory.");

            Dictionary<string, Outcome> numericOut = await Recognize(numeric, entries, e => e.NumericCrop);
            Dictionary<string, Outcome> officialOut = await Recognize(official, entries, e => e.OfficialCrop);
            Require(capture.Providers.Count == 1 && capture.Providers.ContainsKey(InferenceProvider.Cpu),
                "Unexpected inference provider.");

            var summaries = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach ((string key, IEnumerable<Entry> subset) in new[]
            {
                ("ground_truth_positive", entries.Where(e => e.Variant == "gt")),
                ("predicted_positive", entries.Where(e => e.Variant == "pred" && e.Sample.Kind == "positive")),
                ("predicted_positive_iou_ge_0_5", entries.Where(e => e.Variant == "pred" && e.Sample.Kind == "positive" && e.Sample.IoU >= 0.5)),
                ("forced_background", entries.Where(e => e.Variant == "pred" && e.Sample.Kind == "background_only")),
            })
                summaries[key] = Summarize(subset.ToArray(), numericOut, officialOut);
            string sourcePath = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/official_comparison/Program.cs";
            string projectPath = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/official_comparison/OfficialComparison.csproj";
            var report = new
            {
                schema = "graphreader.stroke-seeded-xtick-official-comparison.v3",
                acceptance_status = "consumed synthetic development diagnostic only",
                probe_manifest_sha256 = ManifestHash, predictions_sha256 = PredictionsHash,
                official_model_sha256 = OfficialHash, official_yaml_sha256 = YamlHash,
                official_alphabet_sha256 = OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
                ambiguity_model_sha256 = AmbiguityHash, numeric_model_sha256 = NumericHash,
                runner_source_sha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(root, sourcePath))),
                runner_project_sha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(root, projectPath))),
                runner_assembly_sha256 = Hash(await File.ReadAllBytesAsync(typeof(Program).Assembly.Location)),
                provider = "CPUExecutionProvider", provider_cache_writes = capture.Providers[InferenceProvider.Cpu],
                crop_contract = "Application OcrCropBatcher and V8 numeric/general settings; source-image identity transform.",
                recognition_contract = "Numeric V5 versus official PP-OCRv5 with V8 source-spacing and ambiguity postprocessor.",
                selection_simulation = "Assumes each positive crop has a tick role and is accepted; applies only V8 numeric text/confidence preference, not the V8 detector or role classifier.",
                background_contract = "Forced top-one boxes on backgrounds, regardless of detector score; these are not selected detections.",
                sample_count = rows.Count, compared_crop_count = entries.Count, summaries,
                training_performed = false, production_behavior_changed = false, production_approval = false,
                limitations = new[] { "Previously consumed procedural images, not untouched acceptance or real graph data.",
                    "Comparison does not establish a safe model-selection or fusion rule.",
                    "Runner source and assembly hashes are recorded; transitive build inputs are not exhaustively pinned." },
            };
            string output = Path.GetFullPath(args[1]);
            Require(output.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Report must be inside project artifacts.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(summaries, JsonOptions));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static Entry CreateEntry(OcrImage image, Sample sample, string variant, OcrRectangle box)
    {
        string id = sample.Id + ":" + variant;
        var region = new OcrDetectedRegion(id, OcrPolygon.FromRectangle(box), 0, 1.0);
        OcrCrop Crop(OcrCropBatcherOptions options)
        {
            var batches = OcrCropBatcher.CreateBatches(image, [region], options);
            Require(batches.Count == 1 && batches[0].Count == 1 && batches[0][0].OriginalPolygon.Bounds == box,
                "Crop does not preserve the requested original box.");
            return batches[0][0];
        }
        return new Entry(sample, variant, id,
            Crop(new OcrCropBatcherOptions { TargetWidth = 128, TargetHeight = 32, BatchSize = 16,
                HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1, VerticalContentPaddingRatio = 0.25,
                ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 1f }),
            Crop(new OcrCropBatcherOptions { TargetWidth = 320, TargetHeight = 48, BatchSize = 16,
                HorizontalPaddingPixels = 8, VerticalPaddingPixels = 2,
                ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 0.5f }));
    }

    private static async Task<Dictionary<string, Outcome>> Recognize(ITextRecognizer recognizer,
        List<Entry> entries, Func<Entry, OcrCrop> selector)
    {
        var output = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        foreach (Entry[] batch in entries.Chunk(16))
        {
            var results = await recognizer.RecognizeBatchAsync(batch.Select(selector).ToArray(), CancellationToken.None);
            Require(results.Count == batch.Length, "Recognizer returned the wrong batch size.");
            for (int i = 0; i < batch.Length; i++)
            {
                var best = results[i].Alternatives.OrderByDescending(a => a.Confidence).FirstOrDefault();
                output.Add(batch[i].Id, new Outcome(best?.Text, best?.Confidence, results[i].Failure?.Code));
            }
        }
        return output;
    }

    private static object Summarize(Entry[] entries, Dictionary<string, Outcome> numeric, Dictionary<string, Outcome> official)
    {
        object Counts(Dictionary<string, Outcome> outcomes) => new
        {
            exact = entries.Count(e => e.Sample.Expected is not null && outcomes[e.Id].Failure is null && outcomes[e.Id].Text == e.Sample.Expected),
            wrong = entries.Count(e => e.Sample.Expected is not null && outcomes[e.Id].Failure is null && outcomes[e.Id].Text is not null && outcomes[e.Id].Text != e.Sample.Expected),
            empty = entries.Count(e => outcomes[e.Id].Failure is null && outcomes[e.Id].Text is null),
            failure = entries.Count(e => outcomes[e.Id].Failure is not null),
            background_with_text = entries.Count(e => e.Sample.Expected is null && outcomes[e.Id].Failure is null && outcomes[e.Id].Text is not null),
        };
        bool NumericEligible(Entry entry)
        {
            Outcome outcome = numeric[entry.Id];
            return outcome.Failure is null && outcome.Confidence is >= 0.65 && outcome.Text is not null &&
                Regex.IsMatch(outcome.Text.Trim(), @"^-?\d+(?:\.\d+)?%?$", RegexOptions.CultureInvariant);
        }
        Outcome Selected(Entry entry) => NumericEligible(entry) ? numeric[entry.Id] : official[entry.Id];
        bool NumericValueConflict(Entry entry)
        {
            Outcome left = numeric[entry.Id], right = official[entry.Id];
            if (left.Failure is not null || right.Failure is not null ||
                left.Confidence is not (> 0 and <= 1) || right.Confidence is not (> 0 and <= 1)) return false;
            NumericParseResult leftNumber = GraphNumericParser.Parse(left.Text);
            NumericParseResult rightNumber = GraphNumericParser.Parse(right.Text);
            return leftNumber.IsSuccess && rightNumber.IsSuccess && leftNumber.Value is { } leftValue &&
                rightNumber.Value is { } rightValue && leftValue != rightValue;
        }
        return new
        {
            count = entries.Length, numeric = Counts(numeric), official = Counts(official),
            both_exact = entries.Count(e => e.Sample.Expected is not null && numeric[e.Id].Failure is null && official[e.Id].Failure is null && numeric[e.Id].Text == e.Sample.Expected && official[e.Id].Text == e.Sample.Expected),
            only_numeric_exact = entries.Count(e => e.Sample.Expected is not null && numeric[e.Id].Failure is null && numeric[e.Id].Text == e.Sample.Expected && (official[e.Id].Failure is not null || official[e.Id].Text != e.Sample.Expected)),
            only_official_exact = entries.Count(e => e.Sample.Expected is not null && official[e.Id].Failure is null && official[e.Id].Text == e.Sample.Expected && (numeric[e.Id].Failure is not null || numeric[e.Id].Text != e.Sample.Expected)),
            disagreement = entries.Count(e => numeric[e.Id].Text != official[e.Id].Text),
            assumed_tick_selection = new
            {
                numeric_selected = entries.Count(NumericEligible),
                exact = entries.Count(e => e.Sample.Expected is not null && Selected(e).Failure is null && Selected(e).Text == e.Sample.Expected),
                wrong = entries.Count(e => e.Sample.Expected is not null && Selected(e).Failure is null && Selected(e).Text is not null && Selected(e).Text != e.Sample.Expected),
                empty = entries.Count(e => Selected(e).Failure is null && Selected(e).Text is null),
                failure = entries.Count(e => Selected(e).Failure is not null),
                numeric_selected_wrong_official_exact = entries.Count(e => e.Sample.Expected is not null && NumericEligible(e) && numeric[e.Id].Text != e.Sample.Expected && official[e.Id].Failure is null && official[e.Id].Text == e.Sample.Expected),
                numeric_selected_exact_official_wrong = entries.Count(e => e.Sample.Expected is not null && NumericEligible(e) && numeric[e.Id].Text == e.Sample.Expected && (official[e.Id].Failure is not null || official[e.Id].Text != e.Sample.Expected)),
                numeric_value_conflicts = entries.Count(NumericValueConflict),
                conflicts_numeric_selected_wrong_official_exact = entries.Count(e => e.Sample.Expected is not null && NumericValueConflict(e) && NumericEligible(e) && numeric[e.Id].Text != e.Sample.Expected && official[e.Id].Text == e.Sample.Expected),
                conflicts_numeric_selected_exact_official_wrong = entries.Count(e => e.Sample.Expected is not null && NumericValueConflict(e) && NumericEligible(e) && numeric[e.Id].Text == e.Sample.Expected && official[e.Id].Text != e.Sample.Expected),
            },
        };
    }

    private static string ReadAlphabet(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim() == "character_dict:");
        Require(start >= 0, "Official YAML has no character dictionary.");
        var result = new StringBuilder();
        for (int i = start + 1; i < lines.Length && lines[i].StartsWith("  - ", StringComparison.Ordinal); i++)
        {
            string value = lines[i][4..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            result.Append(value);
        }
        if (!result.ToString().Contains(' ', StringComparison.Ordinal)) result.Append(' ');
        return result.ToString();
    }

    private static async Task<byte[]> ReadPinned(string root, string relative, string expected, int maximum)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Input escaped project root.");
        var info = new FileInfo(path);
        Require(info.Exists && info.Length > 0 && info.Length <= maximum, "Input missing or exceeds size bound.");
        for (FileSystemInfo? current = info; current is not null && !string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase);
             current = current is FileInfo file ? file.Directory : (current as DirectoryInfo)?.Parent)
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, "Reparse point in input path.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expected, "Pinned input hash mismatch: " + relative);
        return bytes;
    }

    private static double IoU(OcrRectangle a, OcrRectangle b)
    {
        double intersection = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
            Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed record Sample(string Id, string Kind, string? Expected, double Score, double IoU);
    private sealed record Entry(Sample Sample, string Variant, string Id, OcrCrop NumericCrop, OcrCrop OfficialCrop);
    private sealed record Outcome(string? Text, double? Confidence, string? Failure);
    private sealed class ProviderCaptureCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            Require(value.Length >= 6, "Inference cache entry lacks provider field.");
            var provider = (InferenceProvider)value.Span[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
