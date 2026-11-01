// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GraphReader.Inference;
using GraphReader.Ocr;
using OpenCvSharp;

internal static class Program
{
    private const string NumericPath = "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx";
    private const string NumericHash = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84";
    private const string NumericEvidencePath = "ml/ocr/component_ensemble_v5/artifacts/P1-run/candidate-report.json";
    private const string NumericEvidenceHash = "e5095b63221b7a745e48ead61011a7794e5da54e0fc24fd641d785bc09257260";
    private const string OfficialPath = "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx";
    private const string OfficialHash = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743";
    private const string OfficialYamlPath = "ml/ocr/official_bakeoff/runs/extracted/en_PP-OCRv5_mobile_rec_infer/inference.yml";
    private const string OfficialYamlHash = "27e91d0582f40168aa218303c76e184bc78fa7a5d105aad0cfbad8458b441067";
    private const string AmbiguityPath = "ml/ocr/ambiguity_source_group_classifier_v3/artifacts/P2-run/graph-ambiguity-source-group-v3-p2.onnx";
    private const string AmbiguityHash = "b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096";
    private const double ConfirmThreshold = 0.65;
    private static readonly Regex NumericGrammar = new(@"^-?\d+(?:\.\d+)?%?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: Confirmation <project-root> <score-rows.json> <recognition.json>");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string scorePath = Path.GetFullPath(args[1]);
            string outputPath = Path.GetFullPath(args[2]);
            Require(scorePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Score input escaped project root.");
            Require(outputPath.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Output must be under project artifacts.");
            Require(!File.Exists(outputPath), "Output already exists; refusing overwrite.");

            byte[] scoreBytes = await File.ReadAllBytesAsync(scorePath);
            using JsonDocument scoreDoc = JsonDocument.Parse(scoreBytes);
            JsonElement score = scoreDoc.RootElement;
            string scoreHash = Hash(scoreBytes);
            Require(score.GetProperty("status").GetString() == "truth-blind-classifier-score-pass-complete" &&
                score.GetProperty("truth_read").GetBoolean() == false &&
                score.GetProperty("private_or_sealed_inputs_read").GetBoolean() == false,
                "Score pass is not truth-blind or not complete.");
            Require(score.GetProperty("numeric_confidence_threshold").GetDouble() == ConfirmThreshold,
                "Numeric decision threshold differs from frozen rule.");
            Require(score.GetProperty("numeric_model_sha256").GetString() == NumericHash,
                "Numeric model identity differs from frozen contract.");
            Require(score.GetProperty("official_model_sha256").GetString() == OfficialHash,
                "Official comparison model identity differs from frozen contract.");

            byte[] evidenceBytes = await ReadPinned(root, NumericEvidencePath, NumericEvidenceHash, 2_000_000);
            using JsonDocument evidence = JsonDocument.Parse(evidenceBytes);
            Require(evidence.RootElement.GetProperty("selected_threshold").GetDouble() == ConfirmThreshold &&
                evidence.RootElement.GetProperty("synthetic_only").GetBoolean() &&
                !evidence.RootElement.GetProperty("sealed_public_archive_opened").GetBoolean(),
                "Numeric threshold evidence is not the synthetic-only selection report.");
            _ = await ReadPinned(root, NumericPath, NumericHash, 20_000_000);
            _ = await ReadPinned(root, OfficialPath, OfficialHash, 20_000_000);
            byte[] yamlBytes = await ReadPinned(root, OfficialYamlPath, OfficialYamlHash, 100_000);
            _ = await ReadPinned(root, AmbiguityPath, AmbiguityHash, 20_000_000);
            string alphabet = ReadAlphabet(System.Text.Encoding.UTF8.GetString(yamlBytes));
            Require(Hash(System.Text.Encoding.UTF8.GetBytes(alphabet)) == OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
                "Official OCR alphabet differs from the application contract.");

            var candidates = new List<Candidate>();
            foreach (JsonElement row in score.GetProperty("rows").EnumerateArray())
            {
                if (!row.GetProperty("p3_selected").GetBoolean() && !row.GetProperty("p5_selected").GetBoolean()) continue;
                string cropPath = row.GetProperty("crop_path").GetString()!;
                string cropHash = row.GetProperty("crop_sha256").GetString()!;
                byte[] cropBytes = await ReadPinned(root, cropPath, cropHash, 1_000_000);
                candidates.Add(new Candidate(row.GetProperty("proposal_id").GetString()!,
                    row.GetProperty("source_id").GetString()!, row.GetProperty("panel_id").GetString()!,
                    row.GetProperty("p3_score").GetDouble(), row.GetProperty("p5_score").GetDouble(),
                    row.GetProperty("p3_selected").GetBoolean(), row.GetProperty("p5_selected").GetBoolean(),
                    cropHash, cropBytes));
            }
            Require(candidates.Count == score.GetProperty("selected_union_rows").GetInt32(),
                "Selected union candidate count differs from score receipt.");

            Candidate[] unique = candidates.GroupBy(row => row.CropHash, StringComparer.Ordinal)
                .Select(group =>
                {
                    Require(group.All(row => row.CropBytes.AsSpan().SequenceEqual(group.First().CropBytes)),
                        "Same crop hash mapped to inconsistent bytes.");
                    return group.First();
                }).ToArray();
            var entries = unique.Select(CreateEntry).ToArray();
            var capture = new ProviderCaptureCache();
            IReadOnlyList<InferenceProvider> providers = [InferenceProvider.Cpu];
            var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance), CpuThreadConfiguration.Create(1));
            await using var runtime = new InferenceRuntime(registry,
                new BoundedInferenceScheduler(capacity: 4, workerCount: 1), capture, allowedProviders: providers);
            ModelIdentity Identity(string id, string version, string sha, string rel) =>
                new(id, version, sha, Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var numeric = new LocalOnnxComponentTextRecognizer(runtime,
                new LocalOnnxComponentTextRecognizerOptions(
                    Identity("graph-numeric-component-ensemble-v5", "0.0.21-p1", NumericHash, NumericPath), "0123456789.-%")
                { StageVersion = "relaxed-width-numeric-confirmation-v1", AllowedProviders = providers });
            var officialBase = new LocalOnnxTextRecognizer(runtime,
                new LocalOnnxTextRecognizerOptions(
                    Identity("en_PP-OCRv5_mobile_rec", "0.0.21-converted", OfficialHash, OfficialPath), alphabet)
                {
                    InputWidth = 320, InputHeight = 48, InputChannels = 3,
                    InputLayout = OcrTensorLayout.ChannelsFirst, InputColorMode = OcrTensorColorMode.GrayscaleReplicated,
                    OutputLayout = OcrOutputLayout.BatchTimeClass, OutputActivation = OcrRecognitionOutputActivation.Probabilities,
                    ExpectedTimeSteps = 40, BlankClassIndex = 0, MaximumAlternatives = 1,
                    InputName = "x", OutputName = "fetch_name_0", StageVersion = "relaxed-width-official-confirmation-v1",
                    ChannelMeans = [0.5f, 0.5f, 0.5f], ChannelScales = [2f, 2f, 2f], AllowedProviders = providers,
                });
            var official = new OcrV8OfficialTextRecognizer(officialBase,
                new OcrV8SourcePostprocessor(runtime,
                    new LocalOnnxAmbiguitySourceGroupOptions(
                        Identity("graph-ambiguity-source-group-v3-p2", "0.0.21-p2", AmbiguityHash, AmbiguityPath))
                    { AllowedProviders = providers }));

            Dictionary<string, Outcome> numericOutputs = await Recognize(numeric, entries, entry => entry.NumericCrop);
            Dictionary<string, Outcome> officialOutputs = await Recognize(official, entries, entry => entry.OfficialCrop);
            Require(capture.Providers.Count == 1 && capture.Providers.ContainsKey(InferenceProvider.Cpu),
                "Unexpected recognizer provider.");
            var outputRows = candidates.Select(candidate =>
            {
                Outcome n = numericOutputs[candidate.CropHash];
                Outcome o = officialOutputs[candidate.CropHash];
                bool NumericConfirmed = Confirm(n);
                bool OfficialConfirmed = o.Failure is null && o.Text is not null && NumericGrammar.IsMatch(o.Text.Trim());
                return new RecognitionRow(candidate.ProposalId, candidate.SourceId, candidate.PanelId,
                    candidate.P3Score, candidate.P5Score, candidate.P3Selected, candidate.P5Selected, candidate.CropHash,
                    n.Text, n.Confidence, n.Failure, NumericConfirmed,
                    o.Text, o.Confidence, o.Failure, OfficialConfirmed);
            }).ToArray();

            var report = new
            {
                schema = "graphreader.relaxed-width-numeric-recognition-pass.v1",
                completed = true,
                acceptance_status = "exploratory consumed synthetic development diagnostic only",
                score_rows_sha256 = scoreHash,
                numeric_model_sha256 = NumericHash,
                numeric_training_evidence_sha256 = NumericEvidenceHash,
                official_model_sha256 = OfficialHash,
                official_yaml_sha256 = OfficialYamlHash,
                official_alphabet_sha256 = OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
                ambiguity_model_sha256 = AmbiguityHash,
                inference_provider = "CPUExecutionProvider",
                provider_cache_writes = capture.Providers[InferenceProvider.Cpu],
                numeric_cropper = "OcrCropBatcher 128x32; horizontal padding 12, vertical padding 1, content ratio 0.25; preserve aspect and pad",
                official_cropper = "OcrCropBatcher 320x48; horizontal padding 8, vertical padding 2; preserve aspect and pad",
                crop_geometry = "whole proposal-centered 256x96 seed crop, not a detected text box",
                numeric_confirmation_threshold = ConfirmThreshold,
                official_minimum_confidence = 0.0,
                numeric_grammar = NumericGrammar.ToString(),
                unique_crop_count = unique.Length,
                selected_candidate_rows = candidates.Count,
                model_assembly_sha256 = Hash(await File.ReadAllBytesAsync(typeof(LocalOnnxComponentTextRecognizer).Assembly.Location)),
                inference_assembly_sha256 = Hash(await File.ReadAllBytesAsync(typeof(InferenceRuntime).Assembly.Location)),
                runner_assembly_sha256 = Hash(await File.ReadAllBytesAsync(typeof(Program).Assembly.Location)),
                runner_source_sha256 = await HashSource(root, "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/relaxed_width_numeric_confirmation_v1/Program.cs"),
                project_source_sha256 = await HashSource(root, "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/relaxed_width_numeric_confirmation_v1/Confirmation.csproj"),
                numeric_source_sha256 = await HashSource(root, "src/GraphReader.Ocr/LocalOnnxComponentTextRecognizer.cs"),
                official_source_sha256 = await HashSource(root, "src/GraphReader.Ocr/OcrV8OfficialTextRecognizer.cs"),
                cropper_source_sha256 = await HashSource(root, "src/GraphReader.Ocr/OcrCropBatcher.cs"),
                rows = outputRows,
                truth_read = false,
                training_performed = false,
                production_activation = false,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, JsonOptions) + "\n");
            Console.WriteLine(JsonSerializer.Serialize(new { selected_rows = candidates.Count, unique_crops = unique.Length,
                provider = "CPUExecutionProvider", output = outputPath }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int SelfTest()
    {
        try
        {
            byte[] pixels = Enumerable.Repeat((byte)255, 256 * 96).ToArray();
            var image = new OcrImage(256, 96, 256, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity,
                CanonicalOriginalWidth: 256, CanonicalOriginalHeight: 96);
            var region = new OcrDetectedRegion("fixture", OcrPolygon.FromRectangle(new OcrRectangle(0, 0, 256, 96)), 0, 1);
            OcrCrop numeric = OcrCropBatcher.CreateBatches(image, [region], new OcrCropBatcherOptions
            {
                TargetWidth = 128, TargetHeight = 32, HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1,
                VerticalContentPaddingRatio = 0.25, ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 1,
            })[0][0];
            OcrCrop official = OcrCropBatcher.CreateBatches(image, [region], new OcrCropBatcherOptions
            {
                TargetWidth = 320, TargetHeight = 48, HorizontalPaddingPixels = 8, VerticalPaddingPixels = 2,
                ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 0.5f,
            })[0][0];
            Require(numeric.Width == 128 && numeric.Height == 32 && numeric.OriginalPolygon.Bounds == new OcrRectangle(0, 0, 256, 96),
                "Numeric cropper fixture contract mismatch.");
            Require(official.Width == 320 && official.Height == 48 && official.SourceCrop is not null,
                "Official cropper fixture/source crop contract mismatch.");
            Require(numeric.Pixels.Span.ToArray().All(float.IsFinite) && official.Pixels.Span.ToArray().All(float.IsFinite),
                "Cropper emitted non-finite pixels.");
            Console.WriteLine("cropper self-test passed: numeric=128x32 official=320x48 source metadata retained");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"self-test failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static Entry CreateEntry(Candidate candidate)
    {
        using Mat decoded = Cv2.ImDecode(candidate.CropBytes, ImreadModes.Grayscale);
        Require(!decoded.Empty() && decoded.Width == 256 && decoded.Height == 96 && decoded.Type() == MatType.CV_8UC1,
            "Candidate crop is not the frozen 256x96 grayscale image.");
        byte[] pixels = new byte[decoded.Width * decoded.Height];
        System.Runtime.InteropServices.Marshal.Copy(decoded.Data, pixels, 0, pixels.Length);
        var image = new OcrImage(decoded.Width, decoded.Height, decoded.Width, pixels, OcrSourceImage.Original,
            OcrFrameTransform.Identity, CanonicalOriginalWidth: decoded.Width, CanonicalOriginalHeight: decoded.Height);
        var region = new OcrDetectedRegion(candidate.ProposalId,
            OcrPolygon.FromRectangle(new OcrRectangle(0, 0, decoded.Width, decoded.Height)), 0, 1);
        OcrCrop Make(OcrCropBatcherOptions options)
        {
            IReadOnlyList<IReadOnlyList<OcrCrop>> batches = OcrCropBatcher.CreateBatches(image, [region], options);
            Require(batches.Count == 1 && batches[0].Count == 1 &&
                batches[0][0].OriginalPolygon.Bounds == new OcrRectangle(0, 0, decoded.Width, decoded.Height),
                "Production cropper did not preserve the candidate seed crop frame.");
            return batches[0][0];
        }
        return new Entry(candidate,
            Make(new OcrCropBatcherOptions { TargetWidth = 128, TargetHeight = 32, BatchSize = 16,
                HorizontalPaddingPixels = 12, VerticalPaddingPixels = 1, VerticalContentPaddingRatio = 0.25,
                ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 1f }),
            Make(new OcrCropBatcherOptions { TargetWidth = 320, TargetHeight = 48, BatchSize = 16,
                HorizontalPaddingPixels = 8, VerticalPaddingPixels = 2,
                ResizeMode = OcrCropResizeMode.PreserveAspectRatioPad, PaddingValue = 0.5f }));
    }

    private static async Task<Dictionary<string, Outcome>> Recognize(ITextRecognizer recognizer,
        Entry[] entries, Func<Entry, OcrCrop> select)
    {
        var output = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        foreach (Entry[] batch in entries.Chunk(16))
        {
            IReadOnlyList<OcrRecognition> results = await recognizer.RecognizeBatchAsync(
                batch.Select(select).ToArray(), CancellationToken.None);
            Require(results.Count == batch.Length, "Recognizer returned wrong batch size.");
            for (int index = 0; index < batch.Length; index++)
            {
                OcrRecognition result = results[index];
                OcrRecognitionAlternative? best = result.Alternatives.OrderByDescending(a => a.Confidence).FirstOrDefault();
                output.Add(batch[index].Candidate.CropHash, new Outcome(best?.Text, best?.Confidence, result.Failure?.Code));
            }
        }
        return output;
    }

    private static bool Confirm(Outcome output) => output.Failure is null && output.Confidence is >= ConfirmThreshold &&
        output.Text is not null && NumericGrammar.IsMatch(output.Text.Trim());

    private static string ReadAlphabet(string yaml)
    {
        string[] lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim() == "character_dict:");
        Require(start >= 0, "Official YAML has no character dictionary.");
        var result = new System.Text.StringBuilder();
        for (int index = start + 1; index < lines.Length && lines[index].StartsWith("  - ", StringComparison.Ordinal); index++)
        {
            string value = lines[index][4..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            result.Append(value);
        }
        if (!result.ToString().Contains(' ', StringComparison.Ordinal)) result.Append(' ');
        return result.ToString();
    }

    private static async Task<byte[]> ReadPinned(string root, string relative, string expectedHash, int maximum)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Input escaped project root.");
        var file = new FileInfo(path);
        Require(file.Exists && file.Length > 0 && file.Length <= maximum, "Missing or oversized input.");
        for (FileSystemInfo? current = file; current is not null && !string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
             current = current is FileInfo f ? f.Directory : (current as DirectoryInfo)?.Parent)
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, "Reparse point in input path.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Require(Hash(bytes) == expectedHash, "Pinned input hash mismatch: " + relative);
        return bytes;
    }

    private static async Task<string> HashSource(string root, string relative)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return Hash(await File.ReadAllBytesAsync(path));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed record Candidate(string ProposalId, string SourceId, string PanelId, double P3Score, double P5Score,
        bool P3Selected, bool P5Selected, string CropHash, byte[] CropBytes);
    private sealed record Entry(Candidate Candidate, OcrCrop NumericCrop, OcrCrop OfficialCrop);
    private sealed record Outcome(string? Text, double? Confidence, string? Failure);
    private sealed record RecognitionRow(string ProposalId, string SourceId, string PanelId, double P3Score, double P5Score,
        bool P3Selected, bool P5Selected, string CropHash, string? NumericText, double? NumericConfidence,
        string? NumericFailure, bool NumericConfirmed, string? OfficialText, double? OfficialConfidence,
        string? OfficialFailure, bool OfficialConfirmed);
    private sealed class ProviderCaptureCache : IStageCache
    {
        public Dictionary<InferenceProvider, int> Providers { get; } = [];
        public ValueTask<byte[]?> TryGetAsync(StageCacheKey key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask PutAsync(StageCacheKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            Require(value.Length >= 6, "Inference cache entry lacks provider field.");
            InferenceProvider provider = (InferenceProvider)value.Span[5];
            Providers[provider] = Providers.GetValueOrDefault(provider) + 1;
            return ValueTask.CompletedTask;
        }
    }
}
