// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GraphReader.Inference;
using GraphReader.Ocr;
using GraphReader.Markers.Detection;
using GraphReader.App.Integration.Workflow;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal static class Program
{
    private const int SealedTarget = 51;
    private const int ExpectedDev = 120;
    private const string FrozenRealAssignmentSha256 = "decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a";
    private const string OfficialDbDetectorSha256 = "d4aa24d408cd70b8b9f66cc758e20f397fc31a9c69d8477cf8887fc53bd5fceb";
    private const string CorrectedRealRangeSeedManifestSha256 = "123f4f6973588b3294b11237e6e6deac1b8f812a5b1bbab55b1315a546ef5329";
    private const double MorphologyPositiveLabelDistancePx = 5.0;
    private static readonly string[] CiNames = ["CI", "TF_BUILD", "GITHUB_ACTIONS", "GITLAB_CI", "BUILD_BUILDID", "JENKINS_URL", "TEAMCITY_VERSION"];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test-frozen-workflow-csv-binding")
        {
            Console.WriteLine(JsonSerializer.Serialize(FrozenWorkflowCsvScoringSelfTest.Run(), JsonOptions));
            return 0;
        }
        if (args.Length == 7 && args[0] == "--score-frozen-workflow-csv" &&
            args[1] == "--evaluation-input" && args[3] == "--evaluation-input-sha256" && args[5] == "--output")
        {
            object result = FrozenWorkflowCsvScoring.Run(
                Environment.CurrentDirectory, Path.GetFullPath(args[2]), args[4], Path.GetFullPath(args[6]),
                CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        if (args.Length == 1 && args[0] == "--self-test-whole-workflow-csv")
        {
            Console.WriteLine(JsonSerializer.Serialize(WholeWorkflowCsvEvaluatorSelfTest.Run(), JsonOptions));
            return 0;
        }
        if (args.Contains("--self-test", StringComparer.Ordinal))
        {
            SelfTestReport selfTest = SelfTest();
            Console.WriteLine(JsonSerializer.Serialize(selfTest, JsonOptions));
            return string.Equals(selfTest.Status, "pass", StringComparison.Ordinal) ? 0 : 1;
        }
        if (args.Contains("--describe-frozen-candidate-runtime", StringComparer.Ordinal))
        {
            FrozenCandidateRuntimeDescription description =
                FrozenCandidateRuntimeDescription.Create();
            Console.WriteLine(JsonSerializer.Serialize(description, JsonOptions));
            return 0;
        }
        if (args.Contains("--run-frozen-candidate-synthetic", StringComparer.Ordinal))
        {
            string? bindingPath = GetOption(args, "--candidate-binding");
            string? bindingSha256 = GetOption(args, "--candidate-binding-sha256");
            string? inputPath = GetOption(args, "--input-manifest");
            string? inputSha256 = GetOption(args, "--input-manifest-sha256");
            string? outputPath = GetOption(args, "--output");
            if (new[] { bindingPath, bindingSha256, inputPath, inputSha256, outputPath }
                .Any(string.IsNullOrWhiteSpace))
            {
                Console.Error.WriteLine(
                    "FROZEN_CANDIDATE_BINDING_INPUT_AND_OUTPUT_REQUIRED");
                return 2;
            }

            FrozenCandidateSyntheticExecution execution = await FrozenCandidateSyntheticRunner.RunAsync(
                Environment.CurrentDirectory,
                Path.GetFullPath(bindingPath!),
                bindingSha256!,
                Path.GetFullPath(inputPath!),
                inputSha256!,
                Path.GetFullPath(outputPath!),
                CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(execution.Report, JsonOptions));
            return execution.FailedCount == 0 ? 0 : 1;
        }
        if (!args.Contains("--explicit-opt-in", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("PRIVATE_CORPUS_EXPLICIT_OPT_IN_REQUIRED");
            return 2;
        }
        if (CiNames.Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) &&
            !new[] { "0", "false", "no", "off" }.Contains(Environment.GetEnvironmentVariable(name)!.Trim(), StringComparer.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("PRIVATE_CORPUS_DISABLED_IN_CI");
            return 2;
        }
        string? syntheticArg = GetOption(args, "--synthetic-real-range");
        string? officialArg = GetOption(args, "--synthetic-official-db");
        string? tiledArg = GetOption(args, "--synthetic-tiled-proposals");
        if (args.Contains("--run-real-dev-marker-v24-morphology", StringComparer.Ordinal))
        {
            string? markerRoot = GetOption(args, "--root");
            string? markerModel = GetOption(args, "--marker-model");
            if (string.IsNullOrWhiteSpace(markerRoot) || markerRoot.StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(markerModel) || markerModel.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REAL_DEV_MARKER_V24_MORPHOLOGY_ROOT_AND_MODEL_REQUIRED");
                return 2;
            }
            MarkerMorphologyAggregateReport morphologyReport = await RunRealDevMarkerV24MorphologyAsync(
                Path.GetFullPath(markerRoot), Path.GetFullPath(markerModel), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(morphologyReport, JsonOptions));
            return morphologyReport.FailureCount == 0 ? 0 : 1;
        }
        if (args.Contains("--run-real-dev-marker-v24-negative-patches", StringComparer.Ordinal))
        {
            string? markerRoot = GetOption(args, "--root");
            if (string.IsNullOrWhiteSpace(markerRoot) || markerRoot.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REAL_DEV_MARKER_V24_NEGATIVE_PATCHES_ROOT_REQUIRED");
                return 2;
            }
            MarkerNegativePatchAggregateReport negativeReport = await RunRealDevMarkerV24NegativePatchesAsync(
                Path.GetFullPath(markerRoot), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(negativeReport, JsonOptions));
            return negativeReport.FailureCount == 0 ? 0 : 1;
        }
        if (args.Contains("--run-real-dev-marker-v24-diagnostic", StringComparer.Ordinal))
        {
            string? markerRoot = GetOption(args, "--root");
            string? markerModel = GetOption(args, "--marker-model");
            if (string.IsNullOrWhiteSpace(markerRoot) || markerRoot.StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(markerModel) || markerModel.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REAL_DEV_MARKER_V24_DIAGNOSTIC_ROOT_AND_MODEL_REQUIRED");
                return 2;
            }

            MarkerAggregateReport markerReport = await RunRealDevMarkerV24DiagnosticAsync(
                Path.GetFullPath(markerRoot), Path.GetFullPath(markerModel), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(markerReport, JsonOptions));
            return markerReport.FailureCount == 0 && markerReport.Gates.Values.All(value => value) ? 0 : 1;
        }
        if (args.Contains("--run-real-dev-marker-v23", StringComparer.Ordinal))
        {
            string? markerRoot = GetOption(args, "--root");
            string? markerModel = GetOption(args, "--marker-model");
            if (string.IsNullOrWhiteSpace(markerRoot) || markerRoot.StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(markerModel) || markerModel.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REAL_DEV_MARKER_V23_ROOT_AND_MODEL_REQUIRED");
                return 2;
            }

            MarkerAggregateReport markerReport = await RunRealDevMarkerV23Async(
                Path.GetFullPath(markerRoot), Path.GetFullPath(markerModel), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(markerReport, JsonOptions));
            return markerReport.FailureCount == 0 && markerReport.Gates.Values.All(value => value) ? 0 : 1;
        }
        if (args.Contains("--run-real-dev-marker-v23-diagnostic", StringComparer.Ordinal))
        {
            string? markerRoot = GetOption(args, "--root");
            string? markerModel = GetOption(args, "--marker-model");
            if (string.IsNullOrWhiteSpace(markerRoot) || markerRoot.StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(markerModel) || markerModel.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REAL_DEV_MARKER_V23_DIAGNOSTIC_ROOT_AND_MODEL_REQUIRED");
                return 2;
            }

            MarkerAggregateReport markerReport = await RunRealDevMarkerV23DiagnosticAsync(
                Path.GetFullPath(markerRoot), Path.GetFullPath(markerModel), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(markerReport, JsonOptions));
            return markerReport.FailureCount == 0 && markerReport.Gates.Values.All(value => value) ? 0 : 1;
        }
        if (args.Contains("--run-real-dev-marker", StringComparer.Ordinal))
        {
            string? markerRoot = GetOption(args, "--root");
            if (markerRoot is null)
            {
                Console.Error.WriteLine("REAL_DEV_ROOT_AND_RUN_FLAG_REQUIRED");
                return 2;
            }

            MarkerAggregateReport markerReport = await RunRealDevMarkerAsync(Path.GetFullPath(markerRoot), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(markerReport, JsonOptions));
            return markerReport.FailureCount == 0 && markerReport.Gates.Values.All(value => value) ? 0 : 1;
        }
        if (officialArg is not null)
        {
            DetectorAggregateReport detectorReport = await RunOfficialDbAsync(Path.GetFullPath(officialArg), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(detectorReport, JsonOptions));
            return detectorReport.DetectionPrecision >= 0.95 && detectorReport.DetectionRecall >= 0.95 ? 0 : 1;
        }
        if (tiledArg is not null)
        {
            int tileSize = GetIntOption(args, "--tile-size", 960);
            int tileOverlap = GetIntOption(args, "--tile-overlap", Math.Min(192, tileSize / 4));
            TiledProposalAggregateReport tiledReport = await RunTiledProposalsAsync(
                Path.GetFullPath(tiledArg), tileSize, tileOverlap, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(tiledReport, JsonOptions));
            return tiledReport.Gates.Values.All(value => value) ? 0 : 1;
        }
        if (syntheticArg is not null)
        {
            SyntheticAggregateReport synthetic = await RunSyntheticAsync(Path.GetFullPath(syntheticArg), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(synthetic, JsonOptions));
            return synthetic.Gates.Values.All(value => value) ? 0 : 1;
        }
        string? rootArg = GetOption(args, "--root");
        if (rootArg is null || !args.Contains("--run-real-dev", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("REAL_DEV_ROOT_AND_RUN_FLAG_REQUIRED");
            return 2;
        }
        AggregateReport report = await RunAsync(Path.GetFullPath(rootArg), CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return report.Gates.Values.All(value => value) ? 0 : 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int GetIntOption(string[] args, string name, int defaultValue)
    {
        string? value = GetOption(args, name);
        if (value is null) return defaultValue;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidDataException($"INVALID_INTEGER_OPTION:{name}");
    }

    private static async Task<AggregateReport> RunAsync(string rootPath, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(rootPath);
        string[] paths = Directory.EnumerateFiles(root, "*.dig", SearchOption.AllDirectories).OrderBy(Path.GetFullPath, StringComparer.Ordinal).ToArray();
        Dictionary<string, string> assignments = Assign(paths, root);
        RequireFrozenRealAssignment(paths, assignments, root);
        string[] dev = paths.Where(path => assignments[Path.GetFullPath(path)] == "real-dev").ToArray();
        if (dev.Length != ExpectedDev) throw new InvalidDataException($"REAL_DEV_COUNT:{dev.Length}");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int succeeded = 0, failed = 0, anchors = 0, points = 0, calibrated = 0, within = 0, matched = 0;
        int numericYTicks = 0, projectsWithTwoYTicks = 0;
        int recognizedRegions = 0, numericRegions = 0;
        var roleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        double anchorError = 0, maxAnchorError = 0;
        var failureKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var timings = new List<double>();
        await using InferenceRuntime runtime = CreateRuntime();
        OcrV8ProductionCompositionPipeline pipeline = CreatePipeline(runtime);
        foreach (string path in dev)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] before = SHA256.HashData(File.ReadAllBytes(path));
                DigTruth truth = ReadDig(path);
                byte[] png = truth.ImageBytes;
                Raster raster = DecodePng(png);
                OcrImage image = ToOcrImage(raster);
                var started = System.Diagnostics.Stopwatch.StartNew();
                OcrResult result = await pipeline.RecognizeAsync(new OcrRequest(
                    "real-dev-aggregate", "real-dev-panel", Convert.ToHexStringLower(SHA256.HashData(png)), image,
                    PlotBounds(truth.Anchors, raster)), cancellationToken);
                started.Stop(); timings.Add(started.Elapsed.TotalMilliseconds);
                if (!result.Succeeded) throw new InvalidDataException("OCR_RESULT_FAILURE");
                recognizedRegions += result.Regions.Count;
                foreach (OcrRegion region in result.Regions)
                {
                    string role = region.Role.ToString();
                    roleCounts[role] = roleCounts.GetValueOrDefault(role) + 1;
                    if (double.TryParse(region.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) numericRegions++;
                }
                anchors += truth.Anchors.Count;
                points += truth.Points.Count;
                Calibration truthCalibration = Fit(truth.Anchors);
                foreach (OcrRegion region in result.Regions.Where(region => region.Role == OcrTextRole.YTick))
                {
                    if (!double.TryParse(region.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
                    double y = region.Polygon.Bounds.Y + region.Polygon.Bounds.Height / 2;
                    truthCalibration.Ticks.Add((y, value));
                }
                numericYTicks += truthCalibration.Ticks.Count;
                if (truthCalibration.Ticks.Count >= 2) projectsWithTwoYTicks++;
                if (!before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new InvalidDataException("REAL_DEV_SOURCE_MUTATED");
                string? tickFailure = TickFailure(truthCalibration.Ticks);
                if (tickFailure is not null) throw new InvalidDataException(tickFailure);
                Calibration predicted = FitTicks(truthCalibration.Ticks);
                calibrated++;
                foreach (AxisAnchor anchor in truth.Anchors)
                {
                    double predictedScreenY = (anchor.GraphY - predicted.A) / predicted.B;
                    double error = Math.Abs(anchor.ScreenY - predictedScreenY);
                    anchorError += error; maxAnchorError = Math.Max(maxAnchorError, error);
                }
                foreach (CurvePoint point in truth.Points)
                {
                    double expected = truthCalibration.GraphY(point.ScreenY);
                    double actual = predicted.GraphY(point.ScreenY);
                    matched++; if (Math.Abs(actual - expected) <= 5) within++;
                }
                succeeded++;
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or InvalidOperationException)
            {
                failed++; string key = exception.Message.Split(':', 2)[0]; failureKinds[key] = failureKinds.GetValueOrDefault(key) + 1;
            }
        }
        stopwatch.Stop();
        double calibrationCoverage = succeeded / (double)dev.Length;
        double pointYAccuracy = within / (double)Math.Max(1, matched);
        using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath("ml/policy/acceptance-bars.json")));
        double tier1Bar = policy.RootElement.GetProperty("tier1_reviewable_error")
            .GetProperty("text_region_detection_recall_minimum").GetDouble();
        var gates = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["automatic_calibration_coverage"] = calibrationCoverage >= tier1Bar,
            ["export_y_within_five_units"] = pointYAccuracy >= tier1Bar,
            ["zero_silently_accepted_invalid_calibrations"] = failed == dev.Length - calibrated &&
                failureKinds.Keys.All(key => key.StartsWith("CALIBRATION_", StringComparison.Ordinal)),
        };
        return new AggregateReport(
            1, "real_dev_ocr_aggregate_only", dev.Length, SealedTarget, 0, succeeded, failed,
            anchors, points, recognizedRegions, numericRegions,
            new Dictionary<string, int>(roleCounts, StringComparer.Ordinal),
            numericYTicks, projectsWithTwoYTicks, calibrated, matched, within,
            calibrationCoverage,
            anchorError / Math.Max(1, calibrated * 3), maxAnchorError,
            pointYAccuracy, gates, timings.Count == 0 ? 0 : timings.Average(), stopwatch.Elapsed.TotalMilliseconds,
            AssignmentsSha256: AssignmentHash(paths, assignments, root),
            ModelPayloadSha256: OcrPayloadSha256(),
            PolicySha256: AcceptancePolicySha256(),
            new Dictionary<string, int>(failureKinds, StringComparer.Ordinal), false, false, false, false, false);
    }

    private static async Task<MarkerAggregateReport> RunRealDevMarkerAsync(string rootPath, CancellationToken cancellationToken)
        => await RunRealDevMarkerAsync(rootPath, markerModelPath: null, multiradiusGeometry: false, includeStageCounters: false, cancellationToken: cancellationToken);

    private static async Task<MarkerAggregateReport> RunRealDevMarkerV23Async(
        string rootPath, string markerModelPath, CancellationToken cancellationToken)
        => await RunRealDevMarkerAsync(rootPath, markerModelPath, multiradiusGeometry: true, includeStageCounters: false, cancellationToken: cancellationToken);

    private static async Task<MarkerAggregateReport> RunRealDevMarkerV23DiagnosticAsync(
        string rootPath, string markerModelPath, CancellationToken cancellationToken)
        => await RunRealDevMarkerAsync(rootPath, markerModelPath, multiradiusGeometry: true, includeStageCounters: true, cancellationToken: cancellationToken);

    private static async Task<MarkerAggregateReport> RunRealDevMarkerV24DiagnosticAsync(
        string rootPath, string markerModelPath, CancellationToken cancellationToken)
        => await RunRealDevMarkerAsync(rootPath, markerModelPath, multiradiusGeometry: true, includeStageCounters: true, maskPreservingCandidate: true, cancellationToken: cancellationToken);

    private static async Task<MarkerMorphologyAggregateReport> RunRealDevMarkerV24MorphologyAsync(string rootPath, string modelPath, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(rootPath);
        string[] paths = Directory.EnumerateFiles(root, "*.dig", SearchOption.AllDirectories).OrderBy(path => Path.GetFullPath(path), StringComparer.Ordinal).ToArray();
        Dictionary<string, string> assignments = Assign(paths, root);
        RequireFrozenRealAssignment(paths, assignments, root);
        string[] dev = paths.Where(path => assignments[Path.GetFullPath(path)] == "real-dev").ToArray();
        if (dev.Length != ExpectedDev) throw new InvalidDataException($"REAL_DEV_COUNT:{dev.Length}");
        await using InferenceRuntime runtime = CreateRuntime();
        ProductionProposalMarkerCenterAdapter adapter = ProductionProposalMarkerCenterAdapter.CreateMaskPreservingCandidate(
            new ModelIdentity(ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateRevision, ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId, ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256, modelPath), runtime);
        OcrV8ProductionCompositionPipeline pipeline = CreatePipeline(runtime);
        var positive = new MorphologyTotals(); var negativeBelow = new MorphologyTotals(); var negativeAbove = new MorphologyTotals();
        int succeeded = 0, failed = 0, proposalCount = 0; var timings = new List<double>(); var failureKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in dev)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DigTruth truth = ReadDig(path); Raster raster = DecodePng(truth.ImageBytes); OcrRectangle plot = PlotBounds(truth.Anchors, raster);
                OcrResult ocr = await pipeline.RecognizeAsync(new OcrRequest("real-dev-marker-v24-morphology", "real-dev-marker-panel", Convert.ToHexStringLower(SHA256.HashData(truth.ImageBytes)), ToOcrImage(raster), plot), cancellationToken);
                if (!ocr.Succeeded) throw new InvalidDataException("OCR_RESULT_FAILURE");
                float[] luminance = raster.Gray.Select(static value => value / 255f).ToArray();
                MarkerImageFrame frame = new(raster.Width, raster.Height, 1, luminance, MarkerSourceImage.Original, MarkerAffineTransform.Identity,
                    new MarkerMask(raster.Width, raster.Height, RasterizeOcrMask(raster.Width, raster.Height, ocr.Regions.Where(static region => region.ReviewStatus != OcrReviewStatus.Rejected).ToArray())),
                    new MarkerMask(raster.Width, raster.Height, RasterizeAxisMask(raster.Width, raster.Height, truth.Anchors, 2.0)));
                System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                IReadOnlyList<ProposalMarkerMorphologyScoreSummary> scores = await adapter.DetectMaskPreservingMorphologyScoresAsync(frame, MarkerPolygon.FromRectangle(new(0, 0, raster.Width, raster.Height)), cancellationToken);
                stopwatch.Stop(); timings.Add(stopwatch.Elapsed.TotalMilliseconds); proposalCount += scores.Count;
                foreach (ProposalMarkerMorphologyScoreSummary score in scores)
                {
                    bool positiveMatch = truth.Points.Any(point => Math.Sqrt(Math.Pow(score.OriginalBaseCenter.X - point.ScreenX, 2) + Math.Pow(score.OriginalBaseCenter.Y - point.ScreenY, 2)) <= MorphologyPositiveLabelDistancePx);
                    (positiveMatch ? positive : score.Probability < 0.25 ? negativeBelow : negativeAbove).Add(score);
                }
                succeeded++;
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or InvalidOperationException)
            { failed++; string key = exception.Message.Split(':', 2)[0]; failureKinds[key] = failureKinds.GetValueOrDefault(key) + 1; }
        }
        return new MarkerMorphologyAggregateReport(1, "real_dev_marker_v24_scored_morphology_aggregate_only", dev.Length, SealedTarget, 0, succeeded, failed, proposalCount, MorphologyPositiveLabelDistancePx, positive.ToRecord(), negativeBelow.ToRecord(), negativeAbove.ToRecord(), timings.Count == 0 ? 0 : timings.Average(), timings.Sum(), AssignmentHash(paths, assignments, root), ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256, OcrPayloadSha256(), AcceptancePolicySha256(), new Dictionary<string, int>(failureKinds, StringComparer.Ordinal), false, false, false, false, false);
    }

    private static async Task<MarkerNegativePatchAggregateReport> RunRealDevMarkerV24NegativePatchesAsync(
        string rootPath, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(rootPath);
        string[] paths = Directory.EnumerateFiles(root, "*.dig", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetFullPath(path), StringComparer.Ordinal).ToArray();
        Dictionary<string, string> assignments = Assign(paths, root);
        RequireFrozenRealAssignment(paths, assignments, root);
        string[] dev = paths.Where(path => assignments[Path.GetFullPath(path)] == "real-dev").ToArray();
        if (dev.Length != ExpectedDev) throw new InvalidDataException($"REAL_DEV_COUNT:{dev.Length}");
        await using InferenceRuntime runtime = CreateRuntime();
        OcrV8ProductionCompositionPipeline pipeline = CreatePipeline(runtime);
        var positive = new FeatureTotals();
        var negative = new FeatureTotals();
        int grid = 0, ink = 0, ocrRejects = 0, artifactRejects = 0, emitted = 0, succeeded = 0, failed = 0;
        var failureKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var timings = new List<double>();
        foreach (string path in dev)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] before = SHA256.HashData(File.ReadAllBytes(path));
                DigTruth truth = ReadDig(path);
                Raster raster = DecodePng(truth.ImageBytes);
                OcrImage ocrImage = ToOcrImage(raster);
                OcrRectangle plot = PlotBounds(truth.Anchors, raster);
                OcrResult ocr = await pipeline.RecognizeAsync(new OcrRequest(
                    "real-dev-marker-v24-negative-patches", "real-dev-marker-panel",
                    Convert.ToHexStringLower(SHA256.HashData(truth.ImageBytes)), ocrImage, plot), cancellationToken);
                if (!ocr.Succeeded) throw new InvalidDataException("OCR_RESULT_FAILURE");
                float[] luminance = raster.Gray.Select(static value => value / 255f).ToArray();
                float[] ocrMask = RasterizeOcrMask(raster.Width, raster.Height, ocr.Regions.Where(static region => region.ReviewStatus != OcrReviewStatus.Rejected).ToArray());
                float[] artifactMask = RasterizeAxisMask(raster.Width, raster.Height, truth.Anchors, 2.0);
                MarkerImageFrame frame = new(raster.Width, raster.Height, 1, luminance, MarkerSourceImage.Original,
                    MarkerAffineTransform.Identity, new MarkerMask(raster.Width, raster.Height, ocrMask),
                    new MarkerMask(raster.Width, raster.Height, artifactMask));
                System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                ProposalMarkerNegativePatchDiagnosticResult diagnostic = ProductionProposalMarkerCenterAdapter.DiagnoseMaskPreservingProposals(
                    frame, MarkerPolygon.FromRectangle(new(0, 0, raster.Width, raster.Height)), cancellationToken);
                stopwatch.Stop(); timings.Add(stopwatch.Elapsed.TotalMilliseconds);
                ProposalMarkerStageCounters counters = diagnostic.StageCounters;
                grid += counters.ProposalGridPositionsConsidered; ink += counters.ProposalGridPositionsConsidered - counters.LowInkRejects;
                ocrRejects += counters.OcrMaskRejects; artifactRejects += counters.ArtifactMaskRejects; emitted += counters.EmittedProposals;
                foreach (ProposalMarkerPatchFeatureSummary feature in diagnostic.EmittedProposalFeatures)
                {
                    bool isPositive = truth.Points.Any(point => Math.Sqrt(Math.Pow(feature.OriginalBaseCenter.X - point.ScreenX, 2) + Math.Pow(feature.OriginalBaseCenter.Y - point.ScreenY, 2)) <= 3.0);
                    (isPositive ? positive : negative).Add(feature);
                }
                if (!before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new InvalidDataException("REAL_DEV_SOURCE_MUTATED");
                succeeded++;
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or InvalidOperationException)
            {
                failed++; string key = exception.Message.Split(':', 2)[0]; failureKinds[key] = failureKinds.GetValueOrDefault(key) + 1;
            }
        }
        return new MarkerNegativePatchAggregateReport(
            1, "real_dev_marker_v24_negative_patch_features_aggregate_only", dev.Length, SealedTarget, 0,
            succeeded, failed, grid, grid, ink, ocrRejects, artifactRejects, emitted, positive.Count, negative.Count,
            positive.ToRecord(), negative.ToRecord(), timings.Count == 0 ? 0 : timings.Average(), timings.Sum(),
            AssignmentHash(paths, assignments, root), OcrPayloadSha256(), AcceptancePolicySha256(),
            EvidencePolicySha256(), 0, 3.0, 0,
            new Dictionary<string, int>(failureKinds, StringComparer.Ordinal), false, false, false, false, false);
    }

    private static async Task<MarkerAggregateReport> RunRealDevMarkerAsync(
        string rootPath,
        string? markerModelPath,
        bool multiradiusGeometry,
        bool includeStageCounters,
        CancellationToken cancellationToken,
        bool maskPreservingCandidate = false)
    {
        string root = Path.GetFullPath(rootPath);
        string[] paths = Directory.EnumerateFiles(root, "*.dig", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetFullPath(path), StringComparer.Ordinal).ToArray();
        Dictionary<string, string> assignments = Assign(paths, root);
        RequireFrozenRealAssignment(paths, assignments, root);
        string[] dev = paths.Where(path => assignments[Path.GetFullPath(path)] == "real-dev").ToArray();
        if (dev.Length != ExpectedDev) throw new InvalidDataException($"REAL_DEV_COUNT:{dev.Length}");

        await using InferenceRuntime runtime = CreateRuntime();
        OcrV8ProductionCompositionPipeline pipeline = CreatePipeline(runtime);
        string modelPath = multiradiusGeometry
            ? Path.GetFullPath(markerModelPath ?? throw new InvalidDataException(maskPreservingCandidate
                ? "REAL_DEV_MARKER_V24_MODEL_REQUIRED"
                : "REAL_DEV_MARKER_V23_MODEL_REQUIRED"))
            : Path.GetFullPath("ml/markers/center/artifacts/runtime-consistency-v2/P2-run/marker-center-runtime-consistency-p2.onnx");
        ModelIdentity model = maskPreservingCandidate
            ? new ModelIdentity(
                ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateRevision,
                ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId,
                ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256,
                modelPath)
            : multiradiusGeometry
            ? new ModelIdentity(
                ProductionProposalMarkerCenterAdapter.MultiradiusCandidateRevision,
                ProductionProposalMarkerCenterAdapter.MultiradiusCandidateId,
                ProductionProposalMarkerCenterAdapter.ExpectedMultiradiusModelSha256,
                modelPath)
            : new ModelIdentity(
                "marker-center-runtime-consistency-v2",
                "P2",
                "924c555e2f27955c644143125d7abd3b05859ea9928ab9d1e741e0544fa19e8b",
                modelPath);
        ProductionProposalMarkerCenterAdapter adapter = maskPreservingCandidate
            ? ProductionProposalMarkerCenterAdapter.CreateMaskPreservingCandidate(model, runtime)
            : multiradiusGeometry
            ? ProductionProposalMarkerCenterAdapter.CreateMultiradiusCandidate(model, runtime)
            : ProductionProposalMarkerCenterAdapter.CreateCandidate(model, runtime);
        string runMode = maskPreservingCandidate ? "v24-mask-preserving" : multiradiusGeometry ? "v23-multiradius" : "historical-p2";
        string reportScope = maskPreservingCandidate
            ? "real_dev_marker_v24_mask_preserving_aggregate_only"
            : multiradiusGeometry
            ? "real_dev_marker_v23_aggregate_only"
            : "real_dev_marker_aggregate_only";
        string stageId = maskPreservingCandidate ? "real-dev-marker-v24-aggregate" : multiradiusGeometry ? "real-dev-marker-v23-aggregate" : "real-dev-marker-aggregate";
        int succeeded = 0, failed = 0, truePositives = 0, falsePositives = 0, falseNegatives = 0;
        int preNmsTruePositives = 0, preNmsFalsePositives = 0, preNmsFalseNegatives = 0;
        int gridTruePositives = 0, gridFalsePositives = 0, gridFalseNegatives = 0;
        int inkTruePositives = 0, inkFalsePositives = 0, inkFalseNegatives = 0;
        int ocrUnmaskedTruePositives = 0, ocrUnmaskedFalsePositives = 0, ocrUnmaskedFalseNegatives = 0;
        int emittedTruePositives = 0, emittedFalsePositives = 0, emittedFalseNegatives = 0;
        int aboveThresholdTruePositives = 0, aboveThresholdFalsePositives = 0, aboveThresholdFalseNegatives = 0;
        var failureKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var timings = new List<double>();
        var stageCounters = new MarkerStageCounterTotals();
        var truthPatchDistribution = new MarkerTruthPatchDistributionAccumulator();
        foreach (string path in dev)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                byte[] before = SHA256.HashData(File.ReadAllBytes(path));
                DigTruth truth = ReadDig(path);
                Raster raster = DecodePng(truth.ImageBytes);
                OcrImage ocrImage = ToOcrImage(raster);
                OcrRectangle plot = PlotBounds(truth.Anchors, raster);
                OcrRectangle markerSearchBounds = multiradiusGeometry
                    ? new OcrRectangle(0, 0, raster.Width, raster.Height)
                    : plot;
                OcrResult ocr = await pipeline.RecognizeAsync(new OcrRequest(
                    stageId, "real-dev-marker-panel", Convert.ToHexStringLower(SHA256.HashData(truth.ImageBytes)), ocrImage, plot), cancellationToken);
                if (!ocr.Succeeded) throw new InvalidDataException("OCR_RESULT_FAILURE");
                float[] luminance = raster.Gray.Select(static value => value / 255f).ToArray();
                float[] ocrMask = RasterizeOcrMask(raster.Width, raster.Height, ocr.Regions.Where(static region => region.ReviewStatus != OcrReviewStatus.Rejected).ToArray());
                float[] artifactMask = RasterizeAxisMask(raster.Width, raster.Height, truth.Anchors, 2.0);
                MarkerImageFrame frame = new(raster.Width, raster.Height, 1, luminance, MarkerSourceImage.Original, MarkerAffineTransform.Identity, new MarkerMask(raster.Width, raster.Height, ocrMask), new MarkerMask(raster.Width, raster.Height, artifactMask));
                if (includeStageCounters)
                {
                    truthPatchDistribution.Add(frame, truth.Points);
                }
                var started = System.Diagnostics.Stopwatch.StartNew();
                ProposalMarkerCandidateDiagnosticResult? diagnostic = includeStageCounters
                    ? await adapter.DetectCandidateWithDiagnosticsAsync(frame, ToMarkerPolygon(markerSearchBounds), cancellationToken)
                    : null;
                IReadOnlyList<MarkerCenter> predictions = diagnostic?.Candidates ??
                    await adapter.DetectCandidateAsync(frame, ToMarkerPolygon(markerSearchBounds), cancellationToken);
                if (diagnostic is not null)
                {
                    stageCounters.Add(diagnostic.StageCounters);
                }
                started.Stop();
                timings.Add(started.Elapsed.TotalMilliseconds);
                (int tp, int fp, int fn) = MatchCenters(predictions, truth.Points, 5.0);
                truePositives += tp; falsePositives += fp; falseNegatives += fn;
                if (diagnostic is not null)
                {
                    (int preTp, int preFp, int preFn) = MatchCenters(diagnostic.PreNmsCandidates, truth.Points, 5.0);
                    preNmsTruePositives += preTp;
                    preNmsFalsePositives += preFp;
                    preNmsFalseNegatives += preFn;
                    (int gridTp, int gridFp, int gridFn) = MatchPoints(
                        diagnostic.GridProposalCenters,
                        truth.Points,
                        5.0);
                    gridTruePositives += gridTp;
                    gridFalsePositives += gridFp;
                    gridFalseNegatives += gridFn;
                    (int inkTp, int inkFp, int inkFn) = MatchPoints(
                        diagnostic.InkSupportedProposalCenters,
                        truth.Points,
                        5.0);
                    inkTruePositives += inkTp;
                    inkFalsePositives += inkFp;
                    inkFalseNegatives += inkFn;
                    (int ocrTp, int ocrFp, int ocrFn) = MatchPoints(
                        diagnostic.OcrUnmaskedProposalCenters,
                        truth.Points,
                        5.0);
                    ocrUnmaskedTruePositives += ocrTp;
                    ocrUnmaskedFalsePositives += ocrFp;
                    ocrUnmaskedFalseNegatives += ocrFn;
                    (int emittedTp, int emittedFp, int emittedFn) = MatchPoints(
                        diagnostic.EmittedProposalCenters,
                        truth.Points,
                        5.0);
                    emittedTruePositives += emittedTp;
                    emittedFalsePositives += emittedFp;
                    emittedFalseNegatives += emittedFn;
                    (int aboveTp, int aboveFp, int aboveFn) = MatchPoints(
                        diagnostic.AboveThresholdDecodedPoints,
                        truth.Points,
                        5.0);
                    aboveThresholdTruePositives += aboveTp;
                    aboveThresholdFalsePositives += aboveFp;
                    aboveThresholdFalseNegatives += aboveFn;
                }
                if (!before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new InvalidDataException("REAL_DEV_SOURCE_MUTATED");
                succeeded++;
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or InvalidOperationException)
            {
                failed++; string key = exception.Message.Split(':', 2)[0]; failureKinds[key] = failureKinds.GetValueOrDefault(key) + 1;
            }
        }

        double precision = truePositives / (double)Math.Max(1, truePositives + falsePositives);
        double recall = truePositives / (double)Math.Max(1, truePositives + falseNegatives);
        double? preNmsPrecision = includeStageCounters
            ? preNmsTruePositives / (double)Math.Max(1, preNmsTruePositives + preNmsFalsePositives)
            : null;
        double? preNmsRecall = includeStageCounters
            ? preNmsTruePositives / (double)Math.Max(1, preNmsTruePositives + preNmsFalseNegatives)
            : null;
        using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath("ml/policy/acceptance-bars.json")));
        JsonElement bars = policy.RootElement.GetProperty("tier1_reviewable_error");
        var gates = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["marker_center_precision"] = precision >= bars.GetProperty("marker_center_precision_minimum").GetDouble(),
            ["marker_center_recall"] = recall >= bars.GetProperty("marker_center_recall_minimum").GetDouble(),
        };

        return new MarkerAggregateReport(
            1, reportScope, dev.Length, SealedTarget, 0, succeeded, failed,
            truePositives, falsePositives, falseNegatives,
            precision, recall, 5.0, gates,
            timings.Count == 0 ? 0 : timings.Average(),
            timings.Sum(), AssignmentHash(paths, assignments, root), model.Sha256,
            OcrPayloadSha256(), maskPreservingCandidate
                ? "mask-preserving_v24_ink_supported_with_ocr_and_artifact_channels"
                : "v8_accepted_regions_plus_anchor_axes",
            AcceptancePolicySha256(),
            new Dictionary<string, int>(failureKinds, StringComparer.Ordinal), false, false, false, false, false,
            multiradiusGeometry ? runMode : null,
            multiradiusGeometry ? model.ModelId : null,
            multiradiusGeometry ? model.Version : null,
            multiradiusGeometry ? "full_immutable_source_image" : null,
            includeStageCounters ? stageCounters.ToRecord() : null,
            includeStageCounters ? preNmsTruePositives : null,
            includeStageCounters ? preNmsFalsePositives : null,
            includeStageCounters ? preNmsFalseNegatives : null,
            preNmsPrecision,
            preNmsRecall,
            includeStageCounters
                ? StageMatch(gridTruePositives, gridFalsePositives, gridFalseNegatives)
                : null,
            includeStageCounters
                ? StageMatch(inkTruePositives, inkFalsePositives, inkFalseNegatives)
                : null,
            includeStageCounters
                ? StageMatch(ocrUnmaskedTruePositives, ocrUnmaskedFalsePositives, ocrUnmaskedFalseNegatives)
                : null,
            includeStageCounters
                ? StageMatch(emittedTruePositives, emittedFalsePositives, emittedFalseNegatives)
                : null,
            includeStageCounters
                ? StageMatch(aboveThresholdTruePositives, aboveThresholdFalsePositives, aboveThresholdFalseNegatives)
                : null,
            includeStageCounters ? truthPatchDistribution.ToRecord() : null);
    }

    private static MarkerPolygon ToMarkerPolygon(OcrRectangle rectangle) => MarkerPolygon.FromRectangle(new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height));

    private static float[] RasterizeOcrMask(int width, int height, IReadOnlyList<OcrRegion> regions)
    {
        float[] values = new float[checked(width * height)];
        foreach (OcrRegion region in regions)
        {
            IReadOnlyList<OcrPoint> points = region.Polygon.Points;
            int left = Math.Max(0, (int)Math.Floor(points.Min(static point => point.X)));
            int right = Math.Min(width - 1, (int)Math.Ceiling(points.Max(static point => point.X)));
            int top = Math.Max(0, (int)Math.Floor(points.Min(static point => point.Y)));
            int bottom = Math.Min(height - 1, (int)Math.Ceiling(points.Max(static point => point.Y)));
            for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++) if (PointInPolygon(x + 0.5, y + 0.5, points)) values[y * width + x] = 1;
        }
        return values;
    }

    private static float[] RasterizeAxisMask(int width, int height, IReadOnlyList<AxisAnchor> anchors, double radius)
    {
        float[] values = new float[checked(width * height)];
        RasterizeSegment(values, width, height, anchors[0].ScreenX, anchors[0].ScreenY, anchors[1].ScreenX, anchors[1].ScreenY, radius);
        RasterizeSegment(values, width, height, anchors[0].ScreenX, anchors[0].ScreenY, anchors[2].ScreenX, anchors[2].ScreenY, radius);
        return values;
    }

    private static void RasterizeSegment(float[] values, int width, int height, double x1, double y1, double x2, double y2, double radius)
    {
        int left = Math.Max(0, (int)Math.Floor(Math.Min(x1, x2) - radius));
        int right = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(x1, x2) + radius));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(y1, y2) - radius));
        int bottom = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(y1, y2) + radius));
        double dx = x2 - x1, dy = y2 - y1, denominator = dx * dx + dy * dy;
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
        {
            double parameter = denominator <= double.Epsilon ? 0 : Math.Clamp(((x + 0.5 - x1) * dx + (y + 0.5 - y1) * dy) / denominator, 0, 1);
            double px = x1 + parameter * dx, py = y1 + parameter * dy;
            if (Math.Sqrt(Math.Pow(x + 0.5 - px, 2) + Math.Pow(y + 0.5 - py, 2)) <= radius) values[y * width + x] = 1;
        }
    }

    private static bool PointInPolygon(double x, double y, IReadOnlyList<OcrPoint> points)
    {
        bool inside = false;
        for (int current = 0; current < points.Count; current++)
        {
            OcrPoint a = points[current], b = points[current == 0 ? points.Count - 1 : current - 1];
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X) inside = !inside;
        }
        return inside;
    }

    private static (int TruePositives, int FalsePositives, int FalseNegatives) MatchCenters(IReadOnlyList<MarkerCenter> predictions, IReadOnlyList<CurvePoint> truth, double tolerance)
        => MatchPoints(predictions.Select(static prediction => prediction.Center).ToArray(), truth, tolerance);

    private static (int TruePositives, int FalsePositives, int FalseNegatives) MatchPoints(IReadOnlyList<MarkerPoint> predictions, IReadOnlyList<CurvePoint> truth, double tolerance)
    {
        int[][] edges = predictions.Select(prediction => truth
            .Select((point, truthIndex) => (truthIndex, distance: Math.Sqrt(
                Math.Pow(prediction.X - point.ScreenX, 2) +
                Math.Pow(prediction.Y - point.ScreenY, 2))))
            .Where(edge => edge.distance <= tolerance)
            .OrderBy(edge => edge.distance)
            .Select(edge => edge.truthIndex)
            .ToArray()).ToArray();
        int truePositives = MaximumMatching(edges, truth.Count).Count(match => match >= 0);
        return (truePositives, predictions.Count - truePositives, truth.Count - truePositives);
    }

    private static MarkerStageMatch StageMatch(int truePositives, int falsePositives, int falseNegatives) =>
        new(
            truePositives,
            falsePositives,
            falseNegatives,
            truePositives / (double)Math.Max(1, truePositives + falsePositives),
            truePositives / (double)Math.Max(1, truePositives + falseNegatives));

    private static int[] MaximumMatching(int[][] edges, int truthCount)
    {
        int[] predictionForTruth = Enumerable.Repeat(-1, truthCount).ToArray();

        bool Augment(int predictionIndex, bool[] visited)
        {
            foreach (int truthIndex in edges[predictionIndex])
            {
                if ((uint)truthIndex >= (uint)truthCount)
                    throw new InvalidDataException("MATCH_TRUTH_INDEX_OUT_OF_RANGE");
                if (visited[truthIndex]) continue;
                visited[truthIndex] = true;
                int prior = predictionForTruth[truthIndex];
                if (prior < 0 || Augment(prior, visited))
                {
                    predictionForTruth[truthIndex] = predictionIndex;
                    return true;
                }
            }
            return false;
        }

        for (int predictionIndex = 0; predictionIndex < edges.Length; predictionIndex++)
            Augment(predictionIndex, new bool[truthCount]);

        int[] truthForPrediction = Enumerable.Repeat(-1, edges.Length).ToArray();
        for (int truthIndex = 0; truthIndex < predictionForTruth.Length; truthIndex++)
        {
            int predictionIndex = predictionForTruth[truthIndex];
            if (predictionIndex >= 0) truthForPrediction[predictionIndex] = truthIndex;
        }
        return truthForPrediction;
    }

    private static async Task<SyntheticAggregateReport> RunSyntheticAsync(string datasetPath, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(datasetPath);
        ValidateCorrectedSyntheticDataset(root);
        string[] images = Directory.EnumerateFiles(Path.Combine(root, "images"), "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (images.Length == 0) throw new InvalidDataException("SYNTHETIC_DATASET_EMPTY");
        await using InferenceRuntime runtime = CreateRuntime();
        OcrV8ProductionCompositionPipeline pipeline = CreatePipeline(runtime);
        LocalOnnxProposalTextRegionDetector proposalDetector = CreateProposalDetector(runtime);
        int truthRegions = 0, truePositives = 0, falsePositives = 0, falseNegatives = 0, roleCorrect = 0, exact = 0, characters = 0, editErrors = 0, prohibited = 0;
        var dimensionCounts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var times = new List<double>();
        var proposalObservations = new List<ProposalObservation>();
        foreach (string imagePath in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string annotationPath = Path.Combine(root, "annotations", Path.GetFileNameWithoutExtension(imagePath) + ".json");
            SyntheticCase truth = ReadSyntheticCase(annotationPath);
            Raster raster = DecodePng(File.ReadAllBytes(imagePath));
            OcrImage image = ToOcrImage(raster);
            OcrDetectorImage detectorImage = ToMaskedDetectorImage(raster, truth.MaskLines);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            OcrResult result = await pipeline.RecognizeAsync(new OcrRequest(
                "synthetic-real-range", Path.GetFileNameWithoutExtension(imagePath), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(imagePath))), image,
                truth.PlotBounds, DetectorImage: detectorImage), cancellationToken);
            watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds);
            if (!result.Succeeded) throw new InvalidDataException("SYNTHETIC_OCR_RESULT_FAILURE");
            IReadOnlyList<OcrDetectedRegion> rawProposals = await proposalDetector.DetectProposalsAsync(detectorImage.Image, cancellationToken);
            proposalObservations.Add(new ProposalObservation(
                raster.Width,
                raster.Height,
                truth.Texts.Select(item => new ProposalTruth(item.Role, item.Box)).ToArray(),
                rawProposals.Select(item => new ProposalCandidate((float)item.DetectionConfidence, item.Polygon.Bounds)).ToArray()));
            string dimension = $"{raster.Width}x{raster.Height}";
            int[] dimensionValues = dimensionCounts.GetValueOrDefault(dimension) ?? new int[8];
            dimensionCounts[dimension] = dimensionValues;
            truthRegions += truth.Texts.Count;
            dimensionValues[0] += truth.Texts.Count;
            OcrRegion[] regions = result.Regions.ToArray();
            int[][] edges = regions.Select(region => truth.Texts
                .Select((item, index) => (index, overlap: IoU(region.Polygon.Bounds, item.Box)))
                .Where(item => item.overlap >= 0.5)
                .OrderByDescending(item => item.overlap)
                .Select(item => item.index)
                .ToArray()).ToArray();
            int[] matches = MaximumMatching(edges, truth.Texts.Count);
            int matchedScene = 0;
            for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
            {
                OcrRegion region = regions[regionIndex];
                int index = matches[regionIndex];
                if (index < 0)
                {
                    falsePositives++;
                    dimensionValues[2]++;
                    if (truth.Prohibited.Any(box => Contains(box, region.Polygon.Bounds.Center))) prohibited++;
                    continue;
                }
                matchedScene++; truePositives++;
                dimensionValues[1]++;
                SyntheticText expected = truth.Texts[index];
                bool textMatch = string.Equals(region.Text, expected.Text, StringComparison.Ordinal);
                exact += textMatch ? 1 : 0;
                dimensionValues[4] += textMatch ? 1 : 0;
                bool roleMatch = RoleName(region.Role) == expected.Role;
                roleCorrect += roleMatch ? 1 : 0;
                dimensionValues[5] += roleMatch ? 1 : 0;
                int characterCount = expected.Text.EnumerateRunes().Count();
                int errors = Levenshtein(expected.Text, region.Text);
                characters += characterCount; editErrors += errors;
                dimensionValues[6] += characterCount;
                dimensionValues[7] += errors;
            }
            int sceneFalseNegatives = truth.Texts.Count - matchedScene;
            falseNegatives += sceneFalseNegatives;
            dimensionValues[3] += sceneFalseNegatives;
        }
        using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath("ml/policy/acceptance-bars.json")));
        JsonElement bars = policy.RootElement.GetProperty("tier1_reviewable_error");
        double precision = truePositives / (double)Math.Max(1, truePositives + falsePositives);
        double recall = truePositives / (double)Math.Max(1, truePositives + falseNegatives);
        double cer = editErrors / (double)Math.Max(1, characters);
        double role = roleCorrect / (double)Math.Max(1, truePositives);
        double prohibitedRate = prohibited / (double)Math.Max(1, truePositives + falsePositives);
        var gates = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["detection_precision"] = precision >= bars.GetProperty("text_region_detection_precision_minimum").GetDouble(),
            ["detection_recall"] = recall >= bars.GetProperty("text_region_detection_recall_minimum").GetDouble(),
            ["recognition_exact"] = exact / (double)Math.Max(1, truthRegions) >= bars.GetProperty("recognition_exact_match_minimum").GetDouble(),
            ["character_error_rate"] = cer <= bars.GetProperty("character_error_rate_maximum").GetDouble(),
            ["role_accuracy"] = role >= bars.GetProperty("role_accuracy_minimum").GetDouble(),
            ["prohibited_structure_hit_rate"] = prohibitedRate <= bars.GetProperty("prohibited_structure_hit_rate_maximum").GetDouble(),
        };
        Dictionary<string, SyntheticDimensionReport> byDimension = dimensionCounts.ToDictionary(
            item => item.Key,
            item => new SyntheticDimensionReport(
                item.Value[0], item.Value[1], item.Value[2], item.Value[3],
                item.Value[1] / (double)Math.Max(1, item.Value[1] + item.Value[2]),
                item.Value[1] / (double)Math.Max(1, item.Value[1] + item.Value[3]),
                item.Value[4] / (double)Math.Max(1, item.Value[0]),
                item.Value[7] / (double)Math.Max(1, item.Value[6]),
                item.Value[5] / (double)Math.Max(1, item.Value[1])),
            StringComparer.Ordinal);
        return new SyntheticAggregateReport(1, "synthetic_real_range_ocr_aggregate_only", images.Length, truthRegions, truePositives, falsePositives, falseNegatives, precision, recall, exact / (double)Math.Max(1, truthRegions), cer, role, prohibited, prohibitedRate, times.Average(), byDimension, ProposalThresholdSummary(proposalObservations), ProposalRoleRecall(proposalObservations), gates, OcrPayloadSha256(), AcceptancePolicySha256(), false, false, false);
    }

    private static async Task<DetectorAggregateReport> RunOfficialDbAsync(string datasetPath, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(datasetPath);
        string[] images = Directory.EnumerateFiles(Path.Combine(root, "images"), "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (images.Length == 0) throw new InvalidDataException("SYNTHETIC_DATASET_EMPTY");
        await using InferenceRuntime runtime = CreateRuntime();
        LocalOnnxTextRegionDetector detector = CreateOfficialDbDetector(runtime);
        int truthRegions = 0, truePositives = 0, falsePositives = 0, falseNegatives = 0;
        var byDimension = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var times = new List<double>();
        foreach (string imagePath in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntheticCase truth = ReadSyntheticCase(Path.Combine(root, "annotations", Path.GetFileNameWithoutExtension(imagePath) + ".json"));
            Raster raster = DecodePng(File.ReadAllBytes(imagePath));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectAsync(ToMaskedDetectorImage(raster, truth.MaskLines).Image, cancellationToken);
            watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds);
            int[] values = byDimension.GetValueOrDefault($"{raster.Width}x{raster.Height}") ?? new int[4];
            byDimension[$"{raster.Width}x{raster.Height}"] = values;
            truthRegions += truth.Texts.Count; values[0] += truth.Texts.Count;
            int[][] edges = regions.Select(region => truth.Texts
                .Select((item, index) => (index, overlap: IoU(region.Polygon.Bounds, item.Box)))
                .Where(item => item.overlap >= 0.5)
                .OrderByDescending(item => item.overlap)
                .Select(item => item.index)
                .ToArray()).ToArray();
            int matched = MaximumMatching(edges, truth.Texts.Count).Count(match => match >= 0);
            int sceneFalsePositives = regions.Count - matched;
            int sceneFalseNegatives = truth.Texts.Count - matched;
            truePositives += matched; values[3] += matched;
            falsePositives += sceneFalsePositives; values[1] += sceneFalsePositives;
            falseNegatives += sceneFalseNegatives; values[2] += sceneFalseNegatives;
        }
        double precision = truePositives / (double)Math.Max(1, truePositives + falsePositives);
        double recall = truePositives / (double)Math.Max(1, truePositives + falseNegatives);
        var dimensions = byDimension.ToDictionary(item => item.Key, item => new DetectorDimensionReport(item.Value[0], item.Value[3], item.Value[1], item.Value[2], item.Value[3] / (double)Math.Max(1, item.Value[3] + item.Value[1]), item.Value[3] / (double)Math.Max(1, item.Value[0])), StringComparer.Ordinal);
        return new DetectorAggregateReport(1, "synthetic_real_range_official_db_aggregate_only", images.Length, truthRegions, truePositives, falsePositives, falseNegatives, precision, recall, dimensions, times.Average(), OfficialDbDetectorSha256, false, false, false, 0.30, 0.60, 1.5, 3, 1000);
    }

    private static async Task<TiledProposalAggregateReport> RunTiledProposalsAsync(
        string datasetPath,
        int tileSize,
        int tileOverlap,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(datasetPath);
        ValidateCorrectedSyntheticDataset(root);
        string[] images = Directory.EnumerateFiles(Path.Combine(root, "images"), "*.png")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (images.Length == 0) throw new InvalidDataException("SYNTHETIC_DATASET_EMPTY");

        await using InferenceRuntime runtime = CreateRuntime();
        var detector = new SourceTiledProposalDetector(CreateProposalDetector(runtime), tileSize, tileOverlap);
        int truthRegions = 0, truePositives = 0, falsePositives = 0, falseNegatives = 0;
        var byDimension = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var timings = new List<double>();
        foreach (string imagePath in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntheticCase truth = ReadSyntheticCase(Path.Combine(root, "annotations", Path.GetFileNameWithoutExtension(imagePath) + ".json"));
            Raster raster = DecodePng(File.ReadAllBytes(imagePath));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<OcrDetectedRegion> regions = await detector.DetectProposalsAsync(
                ToMaskedDetectorImage(raster, truth.MaskLines).Image, cancellationToken);
            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);

            int[][] edges = regions.Select(region => truth.Texts
                .Select((item, index) => (index, overlap: IoU(region.Polygon.Bounds, item.Box)))
                .Where(item => item.overlap >= 0.5)
                .OrderByDescending(item => item.overlap)
                .Select(item => item.index)
                .ToArray()).ToArray();
            int matched = MaximumMatching(edges, truth.Texts.Count).Count(match => match >= 0);
            int sceneFalsePositives = regions.Count - matched;
            int sceneFalseNegatives = truth.Texts.Count - matched;
            truthRegions += truth.Texts.Count;
            truePositives += matched;
            falsePositives += sceneFalsePositives;
            falseNegatives += sceneFalseNegatives;
            string dimension = $"{raster.Width}x{raster.Height}";
            int[] values = byDimension.GetValueOrDefault(dimension) ?? new int[4];
            values[0] += truth.Texts.Count;
            values[1] += matched;
            values[2] += sceneFalsePositives;
            values[3] += sceneFalseNegatives;
            byDimension[dimension] = values;
        }

        double precision = truePositives / (double)Math.Max(1, truePositives + falsePositives);
        double recall = truePositives / (double)Math.Max(1, truePositives + falseNegatives);
        using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath("ml/policy/acceptance-bars.json")));
        JsonElement bars = policy.RootElement.GetProperty("tier1_reviewable_error");
        var gates = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["detection_precision"] = precision >= bars.GetProperty("text_region_detection_precision_minimum").GetDouble(),
            ["detection_recall"] = recall >= bars.GetProperty("text_region_detection_recall_minimum").GetDouble(),
        };
        Dictionary<string, DetectorDimensionReport> dimensions = byDimension.ToDictionary(
            item => item.Key,
            item => new DetectorDimensionReport(
                item.Value[0], item.Value[1], item.Value[2], item.Value[3],
                item.Value[1] / (double)Math.Max(1, item.Value[1] + item.Value[2]),
                item.Value[1] / (double)Math.Max(1, item.Value[0])),
            StringComparer.Ordinal);
        return new TiledProposalAggregateReport(
            1, "synthetic_real_range_source_tiled_v10_proposals_aggregate_only", images.Length,
            truthRegions, truePositives, falsePositives, falseNegatives, precision, recall,
            tileSize, tileOverlap, dimensions, timings.Average(),
            detector.TotalSuppressedDuplicateCount,
            OcrV8ProductionCompositionFactory.DetectorSha256,
            CorrectedRealRangeSeedManifestSha256,
            AcceptancePolicySha256(), EvidencePolicySha256(), gates,
            false, false, false, 0, 0, false);
    }

    private static void ValidateCorrectedSyntheticDataset(string root)
    {
        string manifestPath = Path.Combine(root, "seed-manifest.json");
        if (!File.Exists(manifestPath) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath))), CorrectedRealRangeSeedManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("CORRECTED_SYNTHETIC_MANIFEST_MISMATCH");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!string.Equals(manifest.RootElement.GetProperty("preset").GetString(), "real_range", StringComparison.Ordinal))
            throw new InvalidDataException("CORRECTED_SYNTHETIC_PRESET_MISMATCH");
        JsonElement artifacts = manifest.RootElement.GetProperty("artifact_sha256");
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "images"), "*.png")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "annotations"), "*.json")))
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!artifacts.TryGetProperty(relative, out JsonElement expected) ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), expected.GetString(), StringComparison.Ordinal))
                throw new InvalidDataException("CORRECTED_SYNTHETIC_ARTIFACT_MISMATCH");
        }
    }

    private sealed class SourceTiledProposalDetector(
        ITextRegionProposalDetector inner,
        int tileSize,
        int tileOverlap) : ITextRegionProposalDetector
    {
        private const double InternalEdgeMargin = 4;

        public int TotalSuppressedDuplicateCount { get; private set; }

        public string ConfigurationFingerprint => $"source-tiled-v1:{tileSize}:{tileOverlap}:{inner.ConfigurationFingerprint}";

        public ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectAsync(OcrImage image, CancellationToken cancellationToken) =>
            DetectProposalsAsync(image, cancellationToken);

        public async ValueTask<IReadOnlyList<OcrDetectedRegion>> DetectProposalsAsync(
            OcrImage image,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (tileSize <= 0 || tileOverlap < 0 || tileOverlap >= tileSize)
                throw new InvalidOperationException("SOURCE_TILE_CONFIGURATION_INVALID");
            var output = new List<OcrDetectedRegion>();
            foreach (int top in TileStarts(image.Height, tileSize, tileOverlap))
            {
                foreach (int left in TileStarts(image.Width, tileSize, tileOverlap))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int width = Math.Min(tileSize, image.Width - left);
                    int height = Math.Min(tileSize, image.Height - top);
                    OcrImage tile = CropOcrImage(image, left, top, width, height);
                    IReadOnlyList<OcrDetectedRegion> proposals = await inner.DetectProposalsAsync(tile, cancellationToken);
                    foreach (OcrDetectedRegion proposal in proposals)
                    {
                        OcrRectangle box = proposal.Polygon.Bounds;
                        OcrPoint tileTopLeft = image.OriginalToImage.MapToOriginal(new OcrPoint(left, top));
                        OcrPoint tileBottomRight = image.OriginalToImage.MapToOriginal(new OcrPoint(left + width, top + height));
                        bool touchesInternalEdge =
                            (left > 0 && box.Left <= tileTopLeft.X + InternalEdgeMargin) ||
                            (top > 0 && box.Top <= tileTopLeft.Y + InternalEdgeMargin) ||
                            (left + width < image.Width && box.Right >= tileBottomRight.X - InternalEdgeMargin) ||
                            (top + height < image.Height && box.Bottom >= tileBottomRight.Y - InternalEdgeMargin);
                        if (!touchesInternalEdge) output.Add(proposal);
                    }
                }
            }
            OcrDetectedRegion[] exactUnique = output
                .GroupBy(item => item.RegionId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.DetectionConfidence).First())
                .OrderByDescending(item => item.DetectionConfidence)
                .ThenBy(item => item.Polygon.Bounds.Top)
                .ThenBy(item => item.Polygon.Bounds.Left)
                .ToArray();
            var kept = new List<OcrDetectedRegion>();
            foreach (OcrDetectedRegion candidate in exactUnique)
            {
                if (kept.Any(current => IoU(candidate.Polygon.Bounds, current.Polygon.Bounds) >= 0.8))
                {
                    TotalSuppressedDuplicateCount++;
                    continue;
                }
                kept.Add(candidate);
            }
            return Array.AsReadOnly(kept
                .OrderBy(item => item.Polygon.Bounds.Top)
                .ThenBy(item => item.Polygon.Bounds.Left)
                .ToArray());
        }

        public static int[] TileStarts(int length, int size, int overlap)
        {
            if (length <= size) return [0];
            int step = size - overlap;
            var starts = new List<int>();
            for (int value = 0; value <= length - size; value += step) starts.Add(value);
            if (starts[^1] != length - size) starts.Add(length - size);
            return starts.ToArray();
        }

        private static OcrImage CropOcrImage(OcrImage image, int left, int top, int width, int height)
        {
            byte[] gray = new byte[checked(width * height)];
            ReadOnlySpan<byte> sourceGray = image.Pixels.Span;
            for (int y = 0; y < height; y++)
                sourceGray.Slice(checked((top + y) * image.Stride + left), width).CopyTo(gray.AsSpan(y * width, width));

            OcrBgrBytePixels? bgr = null;
            if (image.BgrPixels is not null)
            {
                byte[] values = new byte[checked(width * height * 3)];
                ReadOnlySpan<byte> sourceBgr = image.BgrPixels.Pixels.Span;
                for (int y = 0; y < height; y++)
                    sourceBgr.Slice(checked((top + y) * image.BgrPixels.Stride + left * 3), width * 3)
                        .CopyTo(values.AsSpan(y * width * 3, width * 3));
                bgr = new OcrBgrBytePixels(width * 3, values);
            }
            OcrFrameTransform transform = image.OriginalToImage;
            return new OcrImage(
                width, height, width, gray, image.SourceImage,
                new OcrFrameTransform(transform.ScaleX, transform.ScaleY, transform.OffsetX - left, transform.OffsetY - top),
                image.CoordinateSpace, image.CanonicalOriginalWidth, image.CanonicalOriginalHeight, bgr);
        }
    }

    private static LocalOnnxTextRegionDetector CreateOfficialDbDetector(InferenceRuntime runtime)
    {
        string path = Path.GetFullPath("ml/ocr/official_bakeoff/runs/conversion/PP-OCRv5_mobile_det.onnx");
        return new LocalOnnxTextRegionDetector(runtime, new LocalOnnxTextRegionDetectorOptions(new ModelIdentity(
            "PP-OCRv5_mobile_det", "0.0.21-converted", OfficialDbDetectorSha256, path))
        {
            InputName = "x",
            OutputName = "fetch_name_0",
            InputColorMode = OcrTensorColorMode.Bgr,
            PostprocessAlgorithm = OcrDetectionPostprocessAlgorithm.DbPostprocessV1,
            ProbabilityThreshold = 0.30f,
            BoxConfidenceThreshold = 0.60f,
            UnclipRatio = 1.5,
            MinimumSideLength = 3,
            MaximumRegions = 1000,
            AllowedProviders = [InferenceProvider.Cpu],
            BypassCache = true,
        });
    }

    private static LocalOnnxProposalTextRegionDetector CreateProposalDetector(InferenceRuntime runtime)
    {
        string path = Path.GetFullPath("ml/ocr/component_spaced_recall_detector_v10/artifacts/P2-run/graph-text-spaced-component-recall-v10-p2.onnx");
        return new LocalOnnxProposalTextRegionDetector(runtime, new LocalOnnxProposalTextRegionDetectorOptions(new ModelIdentity(
            "graph-text-spaced-component-recall-v10-p2", "0.0.21-p2", OcrV8ProductionCompositionFactory.DetectorSha256, path))
        {
            AllowedProviders = [InferenceProvider.Cpu],
            BypassCache = true,
        });
    }

    private sealed record ProposalCandidate(float Confidence, OcrRectangle Box);
    private sealed record ProposalTruth(string Role, OcrRectangle Box);
    private sealed record ProposalObservation(int Width, int Height, IReadOnlyList<ProposalTruth> Truth, IReadOnlyList<ProposalCandidate> Proposals);
    private sealed record ProposalAggregate(int TruePositives, int FalsePositives, int FalseNegatives, double Precision, double Recall);

    private static Dictionary<string, IReadOnlyDictionary<string, ProposalAggregate>> ProposalThresholdSummary(IReadOnlyList<ProposalObservation> observations)
    {
        double[] thresholds = [0.0, 0.82, 0.85, 0.90, 0.95];
        var output = new Dictionary<string, IReadOnlyDictionary<string, ProposalAggregate>>(StringComparer.Ordinal);
        foreach (double threshold in thresholds)
        {
            var perGroup = new Dictionary<string, ProposalAggregate>(StringComparer.Ordinal);
            foreach (string groupName in new[] { "overall", "wide", "tall", "other" })
            {
                IEnumerable<ProposalObservation> group = groupName == "overall"
                    ? observations
                    : observations.Where(item => groupName == "wide" ? item.Width > item.Height * 2 : groupName == "tall" ? item.Height > item.Width * 2 : item.Width <= item.Height * 2 && item.Height <= item.Width * 2);
                int tp = 0, fp = 0, fn = 0;
                foreach (ProposalObservation observation in group)
                {
                    ProposalCandidate[] proposals = observation.Proposals.Where(item => item.Confidence >= threshold).ToArray();
                    int[][] edges = proposals.Select(proposal => observation.Truth
                        .Select((truth, index) => (index, overlap: IoU(proposal.Box, truth.Box)))
                        .Where(item => item.overlap >= 0.5)
                        .OrderByDescending(item => item.overlap)
                        .Select(item => item.index)
                        .ToArray()).ToArray();
                    int matched = MaximumMatching(edges, observation.Truth.Count).Count(match => match >= 0);
                    tp += matched;
                    fp += proposals.Length - matched;
                    fn += observation.Truth.Count - matched;
                }
                perGroup[groupName] = new ProposalAggregate(tp, fp, fn, tp / (double)Math.Max(1, tp + fp), tp / (double)Math.Max(1, tp + fn));
            }
            output[threshold.ToString("0.00", CultureInfo.InvariantCulture)] = perGroup;
        }
        return output;
    }

    private static Dictionary<string, ProposalAggregate> ProposalRoleRecall(IReadOnlyList<ProposalObservation> observations)
    {
        var totals = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (ProposalObservation observation in observations)
        {
            int[][] edges = observation.Proposals.Select(proposal => observation.Truth
                .Select((truth, index) => (index, overlap: IoU(proposal.Box, truth.Box)))
                .Where(item => item.overlap >= 0.5)
                .OrderByDescending(item => item.overlap)
                .Select(item => item.index)
                .ToArray()).ToArray();
            HashSet<int> used = MaximumMatching(edges, observation.Truth.Count)
                .Where(match => match >= 0).ToHashSet();
            foreach (IGrouping<string, ProposalTruth> group in observation.Truth.GroupBy(item => item.Role, StringComparer.Ordinal))
            {
                int[] values = totals.GetValueOrDefault(group.Key) ?? new int[2];
                values[0] += group.Count();
                values[1] += observation.Truth.Select((truth, index) => (truth, index)).Count(item => item.truth.Role == group.Key && used.Contains(item.index));
                totals[group.Key] = values;
            }
        }
        return totals.ToDictionary(item => item.Key, item => new ProposalAggregate(item.Value[1], 0, item.Value[0] - item.Value[1], 0, item.Value[1] / (double)Math.Max(1, item.Value[0])), StringComparer.Ordinal);
    }

    private sealed record SyntheticText(string Text, string Role, OcrRectangle Box);
    private sealed record SyntheticCase(List<SyntheticText> Texts, List<OcrRectangle> Prohibited, OcrRectangle PlotBounds, List<MaskLine> MaskLines);
    private sealed record MaskLine(double X1, double Y1, double X2, double Y2);

    private static SyntheticCase ReadSyntheticCase(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        var texts = new List<SyntheticText>();
        IEnumerable<JsonElement> textArrays = root.TryGetProperty("texts", out JsonElement rootTexts)
            ? new[] { rootTexts }.Concat(root.TryGetProperty("panels", out JsonElement panels) ? panels.EnumerateArray().Where(item => item.TryGetProperty("texts", out _)).Select(item => item.GetProperty("texts")) : [])
            : (root.TryGetProperty("panels", out JsonElement onlyPanels) ? onlyPanels.EnumerateArray().Where(item => item.TryGetProperty("texts", out _)).Select(item => item.GetProperty("texts")) : []);
        foreach (JsonElement textArray in textArrays) foreach (JsonElement item in textArray.EnumerateArray())
        {
            if (!item.TryGetProperty("rendered_pixel_box", out JsonElement box) || box.GetArrayLength() != 4) continue;
            texts.Add(new SyntheticText(item.GetProperty("text").GetString() ?? string.Empty, NormalizeRole(item.GetProperty("role").GetString()), Rectangle(box)));
        }
        var prohibited = new List<OcrRectangle>();
        if (root.TryGetProperty("hard_negatives", out JsonElement negativeArray)) foreach (JsonElement item in negativeArray.EnumerateArray())
        {
            if (item.TryGetProperty("rendered_pixel_box", out JsonElement box) && box.GetArrayLength() == 4) prohibited.Add(Rectangle(box));
        }
        var lines = new List<MaskLine>();
        foreach (JsonElement container in (new[] { root }).Concat(root.TryGetProperty("panels", out JsonElement maskPanels) ? maskPanels.EnumerateArray() : []))
        {
            foreach (string key in new[] { "axes", "ticks", "dividers" })
            {
                if (!container.TryGetProperty(key, out JsonElement lineArray)) continue;
                foreach (JsonElement item in lineArray.EnumerateArray())
                {
                    if (!item.TryGetProperty("line", out JsonElement line) || line.GetArrayLength() != 2) continue;
                    lines.Add(new MaskLine(line[0][0].GetDouble(), line[0][1].GetDouble(), line[1][0].GetDouble(), line[1][1].GetDouble()));
                }
            }
        }
        JsonElement firstPanel = root.GetProperty("panels")[0];
        OcrRectangle plotBounds = Rectangle(firstPanel.GetProperty("plot_box"));
        return new SyntheticCase(texts, prohibited, plotBounds, lines);
    }

    private static OcrRectangle Rectangle(JsonElement box) => new(box[0].GetDouble(), box[1].GetDouble(), box[2].GetDouble(), box[3].GetDouble());
    private static bool Contains(OcrRectangle box, OcrPoint point) => point.X >= box.Left && point.X <= box.Right && point.Y >= box.Top && point.Y <= box.Bottom;
    private static double IoU(OcrRectangle a, OcrRectangle b) { double left = Math.Max(a.Left, b.Left), top = Math.Max(a.Top, b.Top), right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom); double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top); return intersection / Math.Max(double.Epsilon, a.Width * a.Height + b.Width * b.Height - intersection); }
    private static string RoleName(OcrTextRole role) => role switch
    {
        OcrTextRole.YTick => "y_tick",
        OcrTextRole.XTick => "x_tick",
        OcrTextRole.AxisTitle => "axis_title",
        OcrTextRole.PhaseHeading => "phase_heading",
        OcrTextRole.LegendText => "legend_text",
        OcrTextRole.Participant => "participant",
        OcrTextRole.Annotation => "annotation",
        _ => "other",
    };

    private static string NormalizeRole(string? role) => role switch
    {
        "condition_label" => "other",
        "y_tick" or "x_tick" or "axis_title" or "phase_heading" or "legend_text" or "participant" or "annotation" or "other" => role,
        _ => "other",
    };
    private static int Levenshtein(string left, string right) { System.Text.Rune[] a = left.EnumerateRunes().ToArray(), b = right.EnumerateRunes().ToArray(); int[] previous = Enumerable.Range(0, b.Length + 1).ToArray(); for (int row = 1; row <= a.Length; row++) { int[] current = new int[b.Length + 1]; current[0] = row; for (int col = 1; col <= b.Length; col++) current[col] = Math.Min(Math.Min(current[col - 1] + 1, previous[col] + 1), previous[col - 1] + (a[row - 1] == b[col - 1] ? 0 : 1)); previous = current; } return previous[^1]; }

    private static InferenceRuntime CreateRuntime()
    {
        var factory = new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance);
        var registry = new OnnxSessionRegistry(new OrtExecutionProviderDiscovery(), new WindowsExecutionProviderPolicy(), factory, CpuThreadConfiguration.Create(1));
        return new InferenceRuntime(registry, new BoundedInferenceScheduler(2, 1), new ContentAddressedStageCache(Path.Combine(Path.GetTempPath(), "GraphReader.RealAcceptance.Ocr", Guid.NewGuid().ToString("N"))));
    }

    private static OcrV8ProductionCompositionPipeline CreatePipeline(InferenceRuntime runtime)
    {
        static ModelIdentity Model(string id, string version, string hash, string path) => new(id, version, hash, Path.GetFullPath(path));
        string yaml = Path.GetFullPath("ml/ocr/official_bakeoff/runs/extracted/en_PP-OCRv5_mobile_rec_infer/inference.yml");
        string alphabet = ReadAlphabet(yaml);
        return OcrV8ProductionCompositionFactory.Create(runtime, new OcrV8ProductionPayloadSet(
            Model("graph-text-spaced-component-recall-v10-p2", "0.0.21-p2", OcrV8ProductionCompositionFactory.DetectorSha256, "ml/ocr/component_spaced_recall_detector_v10/artifacts/P2-run/graph-text-spaced-component-recall-v10-p2.onnx"),
            Model("en_PP-OCRv5_mobile_rec", "0.0.21-converted", OcrV8ProductionCompositionFactory.OfficialRecognizerSha256, "ml/ocr/official_bakeoff/runs/conversion/en_PP-OCRv5_mobile_rec.onnx"),
            Model("graph-numeric-component-ensemble-v5", "0.0.21-p1", OcrV8ProductionCompositionFactory.NumericRecognizerSha256, "ml/ocr/component_ensemble_v5/artifacts/P1-run/graph-numeric-component-ensemble-v5-p1.onnx"),
            Model("graph-ambiguity-source-group-v3-p2", "0.0.21-p2", OcrV8ProductionCompositionFactory.AmbiguityRecognizerSha256, "ml/ocr/ambiguity_source_group_classifier_v3/artifacts/P2-run/graph-ambiguity-source-group-v3-p2.onnx"), alphabet), [InferenceProvider.Cpu], bypassCache: true);
    }

    private static string ReadAlphabet(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8); int start = Array.FindIndex(lines, line => line.Trim() == "character_dict:");
        if (start < 0) throw new InvalidDataException("ALPHABET_MISSING");
        var result = new StringBuilder();
        for (int i = start + 1; i < lines.Length && lines[i].StartsWith("  - ", StringComparison.Ordinal); i++)
        {
            string value = lines[i][4..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }
            result.Append(value);
        }
        if (!result.ToString().Contains(' ', StringComparison.Ordinal))
        {
            result.Append(' ');
        }
        return result.ToString();
    }

    private sealed record DigTruth(byte[] ImageBytes, List<AxisAnchor> Anchors, List<CurvePoint> Points);
    private sealed record AxisAnchor(double ScreenX, double ScreenY, double GraphX, double GraphY);
    private sealed record CurvePoint(double ScreenX, double ScreenY);
    private sealed record Raster(int Width, int Height, byte[] Gray, byte[] Bgr);
    private enum SyntheticExportVariant
    {
        Valid,
        Empty,
        Subset,
        Duplicate,
        WrongScale,
        MixedSeries,
        InvalidCalibration,
    }

    private sealed record SyntheticExportCase(
        string CaseId,
        Guid ProjectId,
        Guid PanelId,
        IReadOnlyList<ExportPhase> Phases,
        IReadOnlyList<ExportSeries> Series,
        IReadOnlyList<ExportPoint> WorkflowPoints,
        IReadOnlyList<ExportSeriesRelation> Relations,
        IReadOnlyList<ExportEvaluationRow> ExpectedRows,
        Guid SubsetPointId,
        Guid DuplicatePointId,
        Guid MixedSeriesPointId,
        Guid OtherSeriesId);

    private sealed record ExportEvaluationRow(Guid TargetSeriesId, double XValue, double YValue, string Phase);

    private sealed record ExportEvaluation(
        double OutputPrecision,
        double TruthCoverage,
        double MatchedRowYAccuracy,
        int UnmatchedRows,
        int DuplicateRows,
        int MissingRows,
        int WrongScaleRows,
        int FailedCases,
        bool InvalidCalibrationBlocked);

    private sealed record SelfTestReport(
        string Status,
        bool SelfTest,
        bool PrivateCorpusAccess,
        int ModelInferenceRuns,
        int SyntheticCases,
        double OutputPrecision,
        double TruthCoverage,
        double MatchedRowYAccuracy,
        int UnmatchedRows,
        int DuplicateRows,
        int MissingRows,
        int WrongScaleRows,
        int FailedCases,
        bool InvalidCalibrationBlocked,
        bool WorkflowDetectionRan,
        bool WorkflowStepsComplete,
        bool WorkflowSyntheticBoundaryAccepted,
        bool WorkflowExportSucceeded,
        bool WorkflowSourceUnchanged,
        int WorkflowExportRows,
        double WorkflowOutputPrecision,
        double WorkflowTruthCoverage,
        double WorkflowMatchedRowYAccuracy);
    private sealed class Calibration
    {
        public List<(double ScreenY, double Value)> Ticks { get; } = [];
        public double A { get; init; }
        public double B { get; init; }
        public double GraphY(double screenY) => A + B * screenY;
    }

    private static DigTruth ReadDig(string path)
    {
        byte[] raw = File.ReadAllBytes(path); string xml = Encoding.UTF8.GetString(raw);
        if (raw.Length > 64 * 1024 * 1024 || xml.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase) || xml.Contains(" SYSTEM ", StringComparison.OrdinalIgnoreCase) || xml.Contains(" PUBLIC ", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DIG_XML_INVALID");
        XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); XElement root = document.Root ?? throw new InvalidDataException("DIG_ROOT_MISSING");
        byte[]? image = null; foreach (XElement item in root.Descendants()) if (item.Name.LocalName.Contains("image", StringComparison.OrdinalIgnoreCase) || item.Name.LocalName.Contains("raster", StringComparison.OrdinalIgnoreCase)) { string text = string.Concat(item.Nodes().OfType<XText>().Select(node => node.Value)).Replace("\r", "").Replace("\n", "").Replace(" ", ""); if (text.Length > 0) { image = Convert.FromBase64String(text); if (image.Length > 4 && !image.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && image.AsSpan(4).StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) image = image[4..]; break; } }
        if (image is null) throw new InvalidDataException("DIG_IMAGE_MISSING");
        var anchors = new List<AxisAnchor>(); var points = new List<CurvePoint>();
        foreach (XElement item in root.Descendants().Where(item => new[] { "Point", "DataPoint", "CurvePoint", "Coordinate" }.Contains(item.Name.LocalName, StringComparer.OrdinalIgnoreCase))) { XElement? screen = item.Descendants().FirstOrDefault(child => child.Name.LocalName.Equals("PositionScreen", StringComparison.OrdinalIgnoreCase)); XElement? graph = item.Descendants().FirstOrDefault(child => child.Name.LocalName.Equals("PositionGraph", StringComparison.OrdinalIgnoreCase)); if (screen is not null && graph is not null && Pair(screen) is { } s && Pair(graph) is { } g) anchors.Add(new AxisAnchor(s.X, s.Y, g.X, g.Y)); else if (screen is not null && Pair(screen) is { } p) points.Add(new CurvePoint(p.X, p.Y)); }
        if (anchors.Count != 3) throw new InvalidDataException($"DIG_AXIS_ANCHOR_COUNT:{anchors.Count}"); return new DigTruth(image, anchors, points);
    }

    private static (double X, double Y)? Pair(XElement element) => double.TryParse((string?)element.Attribute("x") ?? (string?)element.Attribute("X"), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.TryParse((string?)element.Attribute("y") ?? (string?)element.Attribute("Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ? (x, y) : null;

    private static Raster DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false); BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); BitmapSource source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0); int stride = source.PixelWidth * 4; byte[] bgra = new byte[stride * source.PixelHeight]; source.CopyPixels(bgra, stride, 0); byte[] gray = new byte[source.PixelWidth * source.PixelHeight]; byte[] bgr = new byte[gray.Length * 3]; for (int i = 0, p = 0; i < gray.Length; i++, p += 4) { byte value = (byte)Math.Clamp((0.114 * bgra[p]) + (0.587 * bgra[p + 1]) + (0.299 * bgra[p + 2]), 0, 255); gray[i] = value; bgr[i * 3] = bgra[p]; bgr[i * 3 + 1] = bgra[p + 1]; bgr[i * 3 + 2] = bgra[p + 2]; } return new Raster(source.PixelWidth, source.PixelHeight, gray, bgr);
    }

    private static OcrImage ToOcrImage(Raster raster) => new(raster.Width, raster.Height, raster.Width, raster.Gray, OcrSourceImage.Original, OcrFrameTransform.Identity, OcrContract.CoordinateSpace, raster.Width, raster.Height, new OcrBgrBytePixels(raster.Width * 3, raster.Bgr));

    private static OcrDetectorImage ToMaskedDetectorImage(Raster raster, IReadOnlyList<MaskLine> lines)
    {
        byte[] gray = (byte[])raster.Gray.Clone();
        byte[] bgr = (byte[])raster.Bgr.Clone();
        foreach (MaskLine line in lines)
        {
            int left = Math.Max(0, (int)Math.Floor(Math.Min(line.X1, line.X2) - 2));
            int right = Math.Min(raster.Width - 1, (int)Math.Ceiling(Math.Max(line.X1, line.X2) + 2));
            int top = Math.Max(0, (int)Math.Floor(Math.Min(line.Y1, line.Y2) - 2));
            int bottom = Math.Min(raster.Height - 1, (int)Math.Ceiling(Math.Max(line.Y1, line.Y2) + 2));
            for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++) if (DistanceToSegment(x + 0.5, y + 0.5, line) <= 2)
            {
                int index = y * raster.Width + x;
                gray[index] = 255;
                bgr[index * 3] = bgr[index * 3 + 1] = bgr[index * 3 + 2] = 255;
            }
        }
        OcrImage image = new(raster.Width, raster.Height, raster.Width, gray, OcrSourceImage.Original, OcrFrameTransform.Identity, OcrContract.CoordinateSpace, raster.Width, raster.Height, new OcrBgrBytePixels(raster.Width * 3, bgr));
        return new OcrDetectorImage(image, Convert.ToHexStringLower(SHA256.HashData(gray)), Convert.ToHexStringLower(SHA256.HashData(bgr)));
    }

    private static double DistanceToSegment(double x, double y, MaskLine line)
    {
        double dx = line.X2 - line.X1, dy = line.Y2 - line.Y1;
        double parameter = dx * dx + dy * dy <= double.Epsilon ? 0 : Math.Clamp(((x - line.X1) * dx + (y - line.Y1) * dy) / (dx * dx + dy * dy), 0, 1);
        double px = line.X1 + parameter * dx, py = line.Y1 + parameter * dy;
        return Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
    }

    private static OcrRectangle PlotBounds(IReadOnlyList<AxisAnchor> anchors, Raster raster)
    {
        double left = Math.Clamp(anchors.Min(item => item.ScreenX), 0, raster.Width - 1);
        double top = Math.Clamp(anchors.Min(item => item.ScreenY), 0, raster.Height - 1);
        double right = Math.Clamp(anchors.Max(item => item.ScreenX), left + 1, raster.Width);
        double bottom = Math.Clamp(anchors.Max(item => item.ScreenY), top + 1, raster.Height);
        return new OcrRectangle(left, top, right - left, bottom - top);
    }

    private static Calibration Fit(IReadOnlyList<AxisAnchor> anchors)
    {
        double meanY = anchors.Average(item => item.ScreenY), meanG = anchors.Average(item => item.GraphY); double denominator = anchors.Sum(item => (item.ScreenY - meanY) * (item.ScreenY - meanY)); if (denominator <= double.Epsilon) throw new InvalidDataException("CALIBRATION_DEGENERATE"); double b = anchors.Sum(item => (item.ScreenY - meanY) * (item.GraphY - meanG)) / denominator; return new Calibration { A = meanG - b * meanY, B = b };
    }

    private static Calibration FitTicks(IReadOnlyList<(double ScreenY, double Value)> ticks)
    {
        double meanY = ticks.Average(item => item.ScreenY), meanV = ticks.Average(item => item.Value); double denominator = ticks.Sum(item => (item.ScreenY - meanY) * (item.ScreenY - meanY)); if (denominator <= double.Epsilon) throw new InvalidDataException("CALIBRATION_TICKS_DEGENERATE"); double b = ticks.Sum(item => (item.ScreenY - meanY) * (item.Value - meanV)) / denominator; return new Calibration { A = meanV - b * meanY, B = b };
    }

    private static string? TickFailure(List<(double ScreenY, double Value)> ticks)
    {
        if (ticks.Count < 2) return "CALIBRATION_INSUFFICIENT_YTICKS";
        var ordered = ticks.OrderBy(item => item.ScreenY).ToArray();
        if (ordered.Zip(ordered.Skip(1), static (left, right) => Math.Abs(right.ScreenY - left.ScreenY)).Any(static value => value < 1)) return "CALIBRATION_DUPLICATE_YTICK_POSITION";
        double step = ordered[1].Value - ordered[0].Value;
        if (Math.Abs(step) < 1e-9) return "CALIBRATION_NONMONOTONIC_YTICKS";
        for (int i = 2; i < ordered.Length; i++)
        {
            double actual = ordered[i].Value - ordered[i - 1].Value;
            if (Math.Sign(actual) != Math.Sign(step)) return "CALIBRATION_NONMONOTONIC_YTICKS";
            if (Math.Abs(actual - step) > Math.Max(1, Math.Abs(step)) * 0.25) return "CALIBRATION_UNEVEN_YTICKS";
        }
        return null;
    }

    private static Dictionary<string, string> Assign(IEnumerable<string> source, string root, int target = SealedTarget)
    {
        string[] paths = source.Select(Path.GetFullPath).OrderBy(path => path, StringComparer.Ordinal).ToArray(); var groups = paths.GroupBy(path => Sha256Hex(Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray(); var sealedPaths = new HashSet<string>(StringComparer.Ordinal); int count = 0; foreach (IGrouping<string, string> group in groups) if (count < target && (count + group.Count() <= target || sealedPaths.Count == 0)) { foreach (string path in group) sealedPaths.Add(path); count += group.Count(); } return paths.ToDictionary(path => path, path => sealedPaths.Contains(path) ? "real-sealed" : "real-dev", StringComparer.Ordinal);
    }

    private static void RequireFrozenRealAssignment(string[] paths, Dictionary<string, string> assignments, string root)
    {
        if (paths.Length != ExpectedDev + SealedTarget ||
            assignments.Values.Count(static split => split == "real-dev") != ExpectedDev ||
            assignments.Values.Count(static split => split == "real-sealed") != SealedTarget ||
            !string.Equals(AssignmentHash(paths, assignments, root), FrozenRealAssignmentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("FROZEN_REAL_CORPUS_ASSIGNMENT_MISMATCH");
        }
    }

    private static string AssignmentHash(IEnumerable<string> paths, Dictionary<string, string> assignments, string root) => Sha256Hex(string.Join("\n", paths.OrderBy(path => path, StringComparer.Ordinal).Select(path => $"{Sha256Hex(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))}={assignments[Path.GetFullPath(path)]}")));
    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string AcceptancePolicySha256() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath("ml/policy/acceptance-bars.json"))));
    private static string EvidencePolicySha256() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath("ml/policy/evidence-policy.json"))));
    private static Dictionary<string, string> OcrPayloadSha256() => new(StringComparer.Ordinal)
    {
        ["detector"] = OcrV8ProductionCompositionFactory.DetectorSha256,
        ["official_recognizer"] = OcrV8ProductionCompositionFactory.OfficialRecognizerSha256,
        ["numeric_recognizer"] = OcrV8ProductionCompositionFactory.NumericRecognizerSha256,
        ["ambiguity_recognizer"] = OcrV8ProductionCompositionFactory.AmbiguityRecognizerSha256,
        ["official_alphabet"] = OcrV8ProductionCompositionFactory.OfficialAlphabetSha256,
    };

    private static void FrozenRealAssignmentSelfTest()
    {
        // Names only. No private directory is enumerated and no project is read.
        string root = Path.Combine(Path.GetTempPath(), "GraphReaderFrozenAssignmentSyntheticFixture");
        string[] paths = Enumerable.Range(0, ExpectedDev + SealedTarget)
            .Select(index => Path.Combine(root, "fixture-study", $"case-{index:D3}.dig"))
            .ToArray();
        var assignments = paths.Select((path, index) => (path, split: index < SealedTarget ? "real-sealed" : "real-dev"))
            .ToDictionary(static item => item.path, static item => item.split, StringComparer.Ordinal);
        string originalHash = AssignmentHash(paths, assignments, root);
        ExpectRejected();
        (assignments[paths[0]], assignments[paths[^1]]) = (assignments[paths[^1]], assignments[paths[0]]);
        if (AssignmentHash(paths, assignments, root) == originalHash)
        {
            throw new InvalidDataException("FROZEN_ASSIGNMENT_SELF_TEST_DID_NOT_BIND_SPLIT_MEMBERSHIP");
        }
        ExpectRejected();

        void ExpectRejected()
        {
            try
            {
                RequireFrozenRealAssignment(paths, assignments, root);
            }
            catch (InvalidDataException exception) when (exception.Message == "FROZEN_REAL_CORPUS_ASSIGNMENT_MISMATCH")
            {
                return;
            }
            throw new InvalidDataException("FROZEN_ASSIGNMENT_SELF_TEST_ACCEPTED_FOREIGN_SAME_COUNT_CORPUS");
        }
    }

    private static SelfTestReport SelfTest()
    {
        FrozenRealAssignmentSelfTest();
        FrozenCandidateSelfTest.Run(Environment.CurrentDirectory);
        FrozenCandidateBindingSelfTest.Run(Environment.CurrentDirectory);
        SelfTestReport exportSelfTest = ExportWorkflowEvaluatorSelfTest();
        AxisAnchor[] anchors = [new(0, 0, 0, 100), new(0, 10, 0, 80), new(20, 0, 1, 100)];
        Calibration calibration = Fit(anchors);
        if (Math.Abs(calibration.GraphY(5) - 90) > 1e-9 ||
            TickFailure([(0, 100), (10, 80)]) is not null ||
            TickFailure([(0, 100), (10, 80), (20, 85)]) != "CALIBRATION_NONMONOTONIC_YTICKS" ||
            Math.Abs(IoU(new OcrRectangle(0, 0, 10, 10), new OcrRectangle(0, 0, 10, 10)) - 1) > 1e-9 ||
            Levenshtein("20", "70") != 1 || RoleName(OcrTextRole.YTick) != "y_tick" ||
            MatchCenters([new MarkerCenter("m", new MarkerPoint(5, 5), 3, 0, 1, MarkerSourceImage.Original)], [new CurvePoint(5, 5)], 5) != (1, 0, 0) ||
            MatchPoints([new MarkerPoint(5, 5)], [new CurvePoint(5, 5)], 5) != (1, 0, 0) ||
            MaximumMatching(new int[][] { [0, 1], [0] }, 2).Count(match => match >= 0) != 2 ||
            !SourceTiledProposalDetector.TileStarts(1000, 640, 128).SequenceEqual([0, 360]) ||
            RasterizeAxisMask(32, 32, [new AxisAnchor(4, 4, 0, 0), new AxisAnchor(4, 28, 0, 1), new AxisAnchor(28, 4, 1, 0)], 1).All(static value => value == 0) ||
            !MarkerTruthPatchSelfTest() ||
            !NegativePatchFeatureSelfTest() ||
            !exportSelfTest.SelfTest ||
            ProductionProposalMarkerCenterAdapter.MultiradiusCandidateRevision != "marker-center-multiradius-geometry-v23" ||
            ProductionProposalMarkerCenterAdapter.MultiradiusCandidateId != "P1" ||
            ProductionProposalMarkerCenterAdapter.ExpectedMultiradiusModelSha256 != "0b413db48f8e6707ee5ec99afff4cd8ec3d25c6b8a8d9f165bd416deb4578a38" ||
            ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateRevision != "marker-center-mask-preserving-v24" ||
            ProductionProposalMarkerCenterAdapter.MaskPreservingCandidateId != "P1" ||
            ProductionProposalMarkerCenterAdapter.ExpectedMaskPreservingModelSha256 != "4dece2eeb87229d5d57e0d2d714c1915ebecf8e9475b0d466a03dd970993fdb4")
        {
            throw new InvalidOperationException("SELF_TEST_CALIBRATION_FAILED");
        }

        return exportSelfTest;
    }

    /// <summary>
    /// Runs the local acceptance boundary against artifacts produced by the real
    /// ExportService. Truth is supplied only to the evaluator, never to the
    /// workflow/export request.
    /// </summary>
    private static SelfTestReport ExportWorkflowEvaluatorSelfTest()
    {
        WorkflowSyntheticExportResult workflow = WorkflowSyntheticAcceptance.RunAsync()
            .GetAwaiter()
            .GetResult();
        WorkflowSelfTestMetrics workflowMetrics = EvaluateWorkflowExport(
            workflow,
            [
                new WorkflowSyntheticExportRow(1, 100d / 6d, "a"),
                new WorkflowSyntheticExportRow(2, 80, "b"),
            ]);
        SyntheticExportCase truth = CreateSyntheticExportCase();
        ExportEvaluation valid = EvaluateSyntheticExport(truth, SyntheticExportVariant.Valid);
        ExportEvaluation empty = EvaluateSyntheticExport(truth, SyntheticExportVariant.Empty);
        ExportEvaluation subset = EvaluateSyntheticExport(truth, SyntheticExportVariant.Subset);
        ExportEvaluation duplicate = EvaluateSyntheticExport(truth, SyntheticExportVariant.Duplicate);
        ExportEvaluation wrongScale = EvaluateSyntheticExport(truth, SyntheticExportVariant.WrongScale);
        ExportEvaluation mixedSeries = EvaluateSyntheticExport(truth, SyntheticExportVariant.MixedSeries);
        ExportEvaluation invalidCalibration = EvaluateSyntheticExport(truth, SyntheticExportVariant.InvalidCalibration);

        bool passed = valid.OutputPrecision == 1 && valid.TruthCoverage == 1 && valid.MatchedRowYAccuracy == 1 &&
            valid.UnmatchedRows == 0 && valid.DuplicateRows == 0 && valid.MissingRows == 0 && valid.WrongScaleRows == 0 && valid.FailedCases == 0 &&
            empty.FailedCases == 1 && empty.MissingRows == truth.ExpectedRows.Count &&
            subset.TruthCoverage < 1 && subset.MissingRows > 0 &&
            duplicate.DuplicateRows > 0 && duplicate.OutputPrecision < 1 &&
            wrongScale.WrongScaleRows > 0 && wrongScale.MatchedRowYAccuracy < 1 &&
            mixedSeries.FailedCases == 1 && mixedSeries.TruthCoverage < 1 &&
            invalidCalibration.FailedCases == 1 && invalidCalibration.InvalidCalibrationBlocked &&
            workflow.DetectionRan && workflow.SyntheticBoundaryAccepted && workflow.ExportSucceeded && workflow.SourceUnchanged &&
            workflow.Steps.SequenceEqual([WorkflowStep.Import, WorkflowStep.Prepare, WorkflowStep.Detect, WorkflowStep.Review]) &&
            workflowMetrics.OutputPrecision >= 0.95 && workflowMetrics.TruthCoverage >= 0.95 &&
            workflowMetrics.MatchedRowYAccuracy >= 0.95;
        ExportEvaluation[] evaluations = [valid, empty, subset, duplicate, wrongScale, mixedSeries, invalidCalibration];
        return new SelfTestReport(
            passed ? "pass" : "fail", passed, false, 0, evaluations.Length,
            valid.OutputPrecision, valid.TruthCoverage, valid.MatchedRowYAccuracy,
            evaluations.Sum(item => item.UnmatchedRows), evaluations.Sum(item => item.DuplicateRows),
            evaluations.Sum(item => item.MissingRows), evaluations.Sum(item => item.WrongScaleRows),
            evaluations.Sum(item => item.FailedCases), invalidCalibration.InvalidCalibrationBlocked,
            workflow.DetectionRan,
            workflow.Steps.SequenceEqual([WorkflowStep.Import, WorkflowStep.Prepare, WorkflowStep.Detect, WorkflowStep.Review]),
            workflow.SyntheticBoundaryAccepted, workflow.ExportSucceeded, workflow.SourceUnchanged,
            workflow.Rows.Count, workflowMetrics.OutputPrecision, workflowMetrics.TruthCoverage,
            workflowMetrics.MatchedRowYAccuracy);
    }

    private static WorkflowSelfTestMetrics EvaluateWorkflowExport(
        WorkflowSyntheticExportResult workflow,
        IReadOnlyList<WorkflowSyntheticExportRow> truthRows)
    {
        List<WorkflowSyntheticExportRow> expected = truthRows.ToList();
        HashSet<int> matchedExpected = [];
        int matched = 0;
        int within = 0;
        foreach (WorkflowSyntheticExportRow row in workflow.Rows)
        {
            int index = -1;
            for (int candidateIndex = 0; candidateIndex < expected.Count; candidateIndex++)
            {
                WorkflowSyntheticExportRow candidate = expected[candidateIndex];
                if (!matchedExpected.Contains(candidateIndex) &&
                    string.Equals(candidate.Phase, row.Phase, StringComparison.Ordinal) &&
                    Math.Abs(candidate.X - row.X) <= 1e-9)
                {
                    index = candidateIndex;
                    break;
                }
            }

            if (index < 0)
            {
                continue;
            }

            matchedExpected.Add(index);
            matched++;
            if (Math.Abs(expected[index].Y - row.Y) <= MorphologyPositiveLabelDistancePx)
            {
                within++;
            }
        }

        int duplicates = workflow.Rows
            .GroupBy(row => (row.X, row.Phase))
            .Sum(group => Math.Max(0, group.Count() - 1));
        return new WorkflowSelfTestMetrics(
            workflow.Rows.Count == 0 ? 1 : (matched - duplicates) / (double)workflow.Rows.Count,
            expected.Count == 0 ? 1 : matched / (double)expected.Count,
            matched == 0 ? 0 : within / (double)matched);
    }

    private sealed record WorkflowSelfTestMetrics(
        double OutputPrecision,
        double TruthCoverage,
        double MatchedRowYAccuracy);

    private static ExportEvaluation EvaluateSyntheticExport(
        SyntheticExportCase truth,
        SyntheticExportVariant variant)
    {
        IReadOnlyList<ExportPoint> predicted = variant == SyntheticExportVariant.Empty
            ? Array.Empty<ExportPoint>()
            : truth.WorkflowPoints.Select(point => variant switch
            {
                SyntheticExportVariant.Subset when point.PointId == truth.SubsetPointId => null,
                SyntheticExportVariant.Subset => point,
                SyntheticExportVariant.Duplicate when point.PointId == truth.DuplicatePointId => point with { GraphX = 3, PrintedXValue = 3 },
                SyntheticExportVariant.WrongScale => point with { GraphY = point.GraphY * 10 },
                SyntheticExportVariant.MixedSeries when point.PointId == truth.MixedSeriesPointId => point with { SeriesId = truth.OtherSeriesId },
                _ => point,
            }).Where(static point => point is not null).Cast<ExportPoint>().ToArray();

        ExportCalibration calibration = variant == SyntheticExportVariant.InvalidCalibration
            ? new(ExportCalibrationStatus.NeedsReview, hasYCalibration: false, hasPrintedSessionCalibration: false,
                hasAbsoluteSessionOrigin: false, firstObservedSession: null, confidence: 0,
                reasons: ["synthetic-invalid-calibration"])
            : new(ExportCalibrationStatus.Valid, hasYCalibration: true, hasPrintedSessionCalibration: true,
                hasAbsoluteSessionOrigin: true, firstObservedSession: 1, confidence: 0.99);

        HashSet<Guid> predictedIds = predicted.Select(point => point.PointId).ToHashSet();
        ExportSeries[] series = truth.Series
            .Select(item => new ExportSeries(item.SeriesId, item.Symbol, item.DisplayName, item.SemanticRole,
                item.PointIds.Where(predictedIds.Contains), item.Confidence, item.LegendText))
            .ToArray();

        string outputDirectory = Path.Combine(Path.GetTempPath(), "graph-reader-real-acceptance-self-test", Guid.NewGuid().ToString("N"));
        ExportRequest request = new(
            Guid.NewGuid(), truth.ProjectId, truth.PanelId, outputDirectory, truth.CaseId,
            ExportMode.PrintedSession, ExportAuditMode.None, ExportOperation.WriteFiles,
            calibration, ExportSessionOriginPolicy.Default, truth.Phases, series,
            predicted, truth.Relations);
        ExportResult result;
        List<ExportEvaluationRow> actual = [];
        try
        {
            result = new ExportService().ExportAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            foreach (MinimalCsvArtifact artifact in result.MinimalArtifacts)
            {
                if (artifact.WrittenPath is null || !File.Exists(artifact.WrittenPath))
                {
                    throw new InvalidOperationException("ExportService reported a minimal artifact without a written CSV");
                }
                actual.AddRange(ReadMinimalCsv(artifact.WrittenPath, artifact.InterventionSeriesId));
            }
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
        List<ExportEvaluationRow> expected = truth.ExpectedRows.ToList();
        HashSet<int> matchedExpected = [];
        int matched = 0, within = 0, wrongScale = 0;
        foreach (ExportEvaluationRow row in actual)
        {
            int index = -1;
            for (int candidateIndex = 0; candidateIndex < expected.Count; candidateIndex++)
            {
                ExportEvaluationRow candidate = expected[candidateIndex];
                if (!matchedExpected.Contains(candidateIndex) && candidate.TargetSeriesId == row.TargetSeriesId &&
                    Math.Abs(candidate.XValue - row.XValue) < 1e-9 &&
                    string.Equals(candidate.Phase, row.Phase, StringComparison.Ordinal))
                {
                    index = candidateIndex;
                    break;
                }
            }
            if (index < 0) continue;
            matchedExpected.Add(index); matched++;
            double error = Math.Abs(expected[index].YValue - row.YValue);
            if (error <= 5) within++; else wrongScale++;
        }

        int duplicates = actual.GroupBy(row => (row.TargetSeriesId, row.XValue, row.Phase))
            .Sum(group => Math.Max(0, group.Count() - 1));
        int unmatched = actual.Count - matched;
        int missing = expected.Count - matched;
        return new ExportEvaluation(
            actual.Count == 0 ? 1 : (matched - duplicates) / (double)actual.Count,
            expected.Count == 0 ? 1 : matched / (double)expected.Count,
            matched == 0 ? 0 : within / (double)matched,
            unmatched, duplicates, missing, wrongScale,
            result.Succeeded && actual.Count == 0 ? 1 : (result.Succeeded ? 0 : 1),
            !result.Succeeded && result.Failures.Any(failure => failure.Code.Contains("CALIBRATION", StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<ExportEvaluationRow> ReadMinimalCsv(string path, Guid targetSeriesId)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0 || !string.Equals(lines[0], ExportContract.MinimalCsvHeader, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ExportService wrote an invalid minimal CSV header");
        }
        foreach (string line in lines.Skip(1).Where(static line => line.Length > 0))
        {
            string[] fields = line.Split(',');
            if (fields.Length != 3 ||
                !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                throw new InvalidOperationException("ExportService wrote an invalid minimal CSV row");
            }
            yield return new ExportEvaluationRow(targetSeriesId, x, y, fields[2]);
        }
    }

    private static SyntheticExportCase CreateSyntheticExportCase()
    {
        Guid projectId = Guid.NewGuid();
        Guid panelId = Guid.NewGuid();
        Guid phaseA = Guid.NewGuid();
        Guid phaseB = Guid.NewGuid();
        Guid baseline = Guid.NewGuid();
        Guid intervention = Guid.NewGuid();
        Guid otherSeries = Guid.NewGuid();
        Guid baselineOne = Guid.NewGuid();
        Guid baselineTwo = Guid.NewGuid();
        Guid interventionOne = Guid.NewGuid();
        Guid interventionTwo = Guid.NewGuid();
        ExportPhase[] phases =
        [
            new(phaseA, 1, "a", ExportPhaseType.Baseline, "a", 1, 2, 0.99),
            new(phaseB, 2, "b", ExportPhaseType.Intervention, "b", 3, 4, 0.99),
        ];
        ExportPoint[] points =
        [
            SyntheticPoint(baselineOne, baseline, phaseA, 1, 10),
            SyntheticPoint(baselineTwo, baseline, phaseA, 2, 12),
            SyntheticPoint(interventionOne, intervention, phaseB, 3, 30),
            SyntheticPoint(interventionTwo, intervention, phaseB, 4, 32),
        ];
        ExportSeries[] series =
        [
            new(baseline, "■", "Baseline", ExportSeriesRole.Baseline, [baselineOne, baselineTwo], 0.99),
            new(intervention, "●", "Intervention", ExportSeriesRole.Intervention, [interventionOne, interventionTwo], 0.99),
        ];
        return new SyntheticExportCase(
            "synthetic-case-01", projectId, panelId, phases, series, points,
            [new ExportSeriesRelation(intervention, baseline)],
            points.Select(point => new ExportEvaluationRow(intervention, point.GraphX!.Value, point.GraphY!.Value, point.PhaseId == phaseA ? "a" : "b"))
                .ToArray(),
            baselineOne, interventionTwo, interventionOne, otherSeries);
    }

    private static ExportPoint SyntheticPoint(Guid pointId, Guid seriesId, Guid phaseId, double x, double y) =>
        new(pointId, pointId, seriesId, phaseId, new ExportPixelPoint(x * 10, 100 - y), x, y, (int)x, x, null,
            ExportXValueSource.Printed, 0.99, 0.99, 0.99, ExportReviewStatus.Accepted, "synthetic-detector", "self-test");

    private static bool MarkerTruthPatchSelfTest()
    {
        const int width = 5, height = 5;
        float[] luminance = Enumerable.Repeat(0.5f, width * height).ToArray();
        float[] ocr = new float[width * height];
        float[] artifact = new float[width * height];
        ocr[(2 * width) + 2] = 1;
        var frame = new MarkerImageFrame(
            width,
            height,
            1,
            luminance,
            MarkerSourceImage.Original,
            MarkerAffineTransform.Identity,
            new MarkerMask(width, height, ocr),
            new MarkerMask(width, height, artifact));
        var accumulator = new MarkerTruthPatchDistributionAccumulator();
        accumulator.Add(frame, [new CurvePoint(2, 2)]);
        MarkerTruthPatchDistribution report = accumulator.ToRecord();
        return report.PatchCount == 1 &&
            report.PatchSizePx == ProductionProposalMarkerCenterAdapter.PatchSize &&
            Math.Abs(report.InkMaximum.Maximum - 0.5) <= 1e-12 &&
            report.OcrCenter5x5HardRejectCount == 1 &&
            report.ArtifactCenter5x5HardRejectCount == 0;
    }

    private static bool NegativePatchFeatureSelfTest()
    {
        var totals = new FeatureTotals();
        totals.Add(new ProposalMarkerPatchFeatureSummary(new MarkerPoint(1, 1), 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7));
        totals.Add(new ProposalMarkerPatchFeatureSummary(new MarkerPoint(2, 2), 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9));
        MarkerNegativePatchFeatureQuantiles report = totals.ToRecord();
        return totals.Count == 2 &&
            Math.Abs(report.InkMean.Minimum - 0.1) <= 1e-12 &&
            Math.Abs(report.InkMean.Median - 0.2) <= 1e-12 &&
            Math.Abs(report.ArtifactMaskMaximum.Maximum - 0.9) <= 1e-12;
    }

    private sealed record AggregateReport(int SchemaVersion, string ReportScope, int RealDevProjects, int RealSealedProjects, int RealSealedReads, int SuccessfulProjects, int FailureCount, int AxisAnchorCount, int CurvePointCount, int RecognizedRegionCount, int NumericRegionCount, IReadOnlyDictionary<string, int> RoleCounts, int NumericYTickCount, int ProjectsWithAtLeastTwoYTicks, int CalibratedProjects, int MatchedPoints, int PointsWithinFiveUnits, double CalibrationProjectSuccessRate, double MeanAnchorErrorPx, double MaximumAnchorErrorPx, double PointYAccuracy, IReadOnlyDictionary<string, bool> Gates, double MeanProjectInferenceMs, double TotalRuntimeMs, string AssignmentsSha256, IReadOnlyDictionary<string, string> ModelPayloadSha256, string PolicySha256, IReadOnlyDictionary<string, int> FailureKinds, bool CaseLevelOutput, bool TruthRowsOutput, bool PixelOutput, bool TrainingUse, bool CandidateSelection);
    private sealed class MarkerStageCounterTotals
    {
        private int proposalGridPositionsConsidered;
        private int lowInkRejects;
        private int ocrMaskRejects;
        private int artifactMaskRejects;
        private int emittedProposals;
        private int inferenceOutputs;
        private int outputsAbove025;
        private int decodedPointsMasked;
        private int geometryConsensusRejectsAfterRefinementAttempts;
        private int decodedPointsOutsidePlot;
        private int candidatesBeforeNms;
        private int nmsSuppressions;
        private int finalCandidates;

        public void Add(ProposalMarkerStageCounters counters)
        {
            proposalGridPositionsConsidered += counters.ProposalGridPositionsConsidered;
            lowInkRejects += counters.LowInkRejects;
            ocrMaskRejects += counters.OcrMaskRejects;
            artifactMaskRejects += counters.ArtifactMaskRejects;
            emittedProposals += counters.EmittedProposals;
            inferenceOutputs += counters.InferenceOutputs;
            outputsAbove025 += counters.OutputsAbove025;
            decodedPointsMasked += counters.DecodedPointsMasked;
            geometryConsensusRejectsAfterRefinementAttempts += counters.GeometryConsensusRejectsAfterRefinementAttempts;
            decodedPointsOutsidePlot += counters.DecodedPointsOutsidePlot;
            candidatesBeforeNms += counters.CandidatesBeforeNms;
            nmsSuppressions += counters.NmsSuppressions;
            finalCandidates += counters.FinalCandidates;
        }

        public ProposalMarkerStageCounters ToRecord() => new(
            proposalGridPositionsConsidered,
            lowInkRejects,
            ocrMaskRejects,
            artifactMaskRejects,
            emittedProposals,
            inferenceOutputs,
            outputsAbove025,
            decodedPointsMasked,
            geometryConsensusRejectsAfterRefinementAttempts,
            decodedPointsOutsidePlot,
            candidatesBeforeNms,
            nmsSuppressions,
            finalCandidates);
    }

    private sealed class MarkerTruthPatchDistributionAccumulator
    {
        private const int PatchSize = ProductionProposalMarkerCenterAdapter.PatchSize;
        private const int PatchRadius = PatchSize / 2;
        private const float HardMaskThreshold = 0.35f;
        private readonly List<double> inkMean = [];
        private readonly List<double> inkCenter5Mean = [];
        private readonly List<double> inkMaximum = [];
        private readonly List<double> ocrMaskMean = [];
        private readonly List<double> ocrMaskMaximum = [];
        private readonly List<double> artifactMaskMean = [];
        private readonly List<double> artifactMaskMaximum = [];
        private int ocrCenterHardRejectCount;
        private int artifactCenterHardRejectCount;

        public void Add(MarkerImageFrame frame, IReadOnlyList<CurvePoint> points)
        {
            ReadOnlySpan<float> luminance = frame.ChannelsFirstPixels.Span;
            ReadOnlySpan<float> ocr = frame.OcrMask.Values.Span;
            ReadOnlySpan<float> artifact = frame.ArtifactMask.Values.Span;
            foreach (CurvePoint point in points)
            {
                int centerX = (int)Math.Round(point.ScreenX);
                int centerY = (int)Math.Round(point.ScreenY);
                double inkSum = 0, ocrSum = 0, artifactSum = 0;
                double inkMax = 0, ocrMax = 0, artifactMax = 0;
                double centerInkSum = 0, centerOcrMax = 0, centerArtifactMax = 0;
                int centerCount = 0;
                for (int dy = -PatchRadius; dy <= PatchRadius; dy++)
                {
                    for (int dx = -PatchRadius; dx <= PatchRadius; dx++)
                    {
                        double inkValue = 0, ocrValue = 0, artifactValue = 0;
                        int x = centerX + dx;
                        int y = centerY + dy;
                        if ((uint)x < (uint)frame.Width && (uint)y < (uint)frame.Height)
                        {
                            int index = (y * frame.Width) + x;
                            inkValue = 1 - luminance[index];
                            ocrValue = ocr[index];
                            artifactValue = artifact[index];
                        }
                        inkSum += inkValue;
                        ocrSum += ocrValue;
                        artifactSum += artifactValue;
                        inkMax = Math.Max(inkMax, inkValue);
                        ocrMax = Math.Max(ocrMax, ocrValue);
                        artifactMax = Math.Max(artifactMax, artifactValue);
                        if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2)
                        {
                            centerInkSum += inkValue;
                            centerOcrMax = Math.Max(centerOcrMax, ocrValue);
                            centerArtifactMax = Math.Max(centerArtifactMax, artifactValue);
                            centerCount++;
                        }
                    }
                }
                int pixelCount = PatchSize * PatchSize;
                inkMean.Add(inkSum / pixelCount);
                inkCenter5Mean.Add(centerInkSum / centerCount);
                inkMaximum.Add(inkMax);
                ocrMaskMean.Add(ocrSum / pixelCount);
                ocrMaskMaximum.Add(ocrMax);
                artifactMaskMean.Add(artifactSum / pixelCount);
                artifactMaskMaximum.Add(artifactMax);
                ocrCenterHardRejectCount += centerOcrMax >= HardMaskThreshold ? 1 : 0;
                artifactCenterHardRejectCount += centerArtifactMax >= HardMaskThreshold ? 1 : 0;
            }
        }

        public MarkerTruthPatchDistribution ToRecord() => new(
            inkMean.Count,
            PatchSize,
            Summarize(inkMean),
            Summarize(inkCenter5Mean),
            Summarize(inkMaximum),
            Summarize(ocrMaskMean),
            Summarize(ocrMaskMaximum),
            Summarize(artifactMaskMean),
            Summarize(artifactMaskMaximum),
            ocrCenterHardRejectCount,
            artifactCenterHardRejectCount,
            HardMaskThreshold);

        private static DistributionSummary Summarize(List<double> values)
        {
            if (values.Count == 0)
            {
                return new DistributionSummary(0, 0, 0, 0, 0, 0, 0);
            }
            double[] ordered = values.Order().ToArray();
            double Percentile(double fraction)
            {
                double position = (ordered.Length - 1) * fraction;
                int lower = (int)Math.Floor(position);
                int upper = (int)Math.Ceiling(position);
                if (lower == upper) return ordered[lower];
                double weight = position - lower;
                return ordered[lower] * (1 - weight) + ordered[upper] * weight;
            }
            return new DistributionSummary(
                ordered[0],
                Percentile(0.05),
                Percentile(0.10),
                Percentile(0.50),
                Percentile(0.90),
                Percentile(0.95),
                ordered[^1]);
        }
    }

    private sealed class FeatureTotals
    {
        private readonly List<double>[] values = Enumerable.Range(0, 7).Select(static _ => new List<double>()).ToArray();
        public int Count => values[0].Count;
        public void Add(ProposalMarkerPatchFeatureSummary feature)
        {
            values[0].Add(feature.InkMean); values[1].Add(feature.InkCenter5x5Mean); values[2].Add(feature.InkMaximum);
            values[3].Add(feature.OcrMaskMean); values[4].Add(feature.OcrMaskMaximum);
            values[5].Add(feature.ArtifactMaskMean); values[6].Add(feature.ArtifactMaskMaximum);
        }
        public MarkerNegativePatchFeatureQuantiles ToRecord() => new(
            Quantiles(values[0]), Quantiles(values[1]), Quantiles(values[2]), Quantiles(values[3]),
            Quantiles(values[4]), Quantiles(values[5]), Quantiles(values[6]));
        private static MarkerFeatureQuantiles Quantiles(List<double> source)
        {
            if (source.Count == 0) return new(0, 0, 0, 0, 0, 0, 0);
            double[] ordered = source.Order().ToArray();
            double At(double fraction)
            {
                double position = (ordered.Length - 1) * fraction;
                int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
                return lower == upper ? ordered[lower] : ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
            }
            return new(ordered[0], At(0.05), At(0.25), At(0.50), At(0.75), At(0.95), ordered[^1]);
        }
    }

    private sealed class MorphologyTotals
    {
        private readonly List<double>[] values = Enumerable.Range(0, 10).Select(static _ => new List<double>()).ToArray();
        public void Add(ProposalMarkerMorphologyScoreSummary s) { values[0].Add(s.Probability); values[1].Add(s.DarkFractionAt012); values[2].Add(s.DarkFractionAt05); values[3].Add(s.InkCenter5x5Mean); values[4].Add(s.MaximumRowDarkFraction); values[5].Add(s.MaximumColumnDarkFraction); values[6].Add(s.ForegroundExtentBalance); values[7].Add(s.CovarianceEigenvalueRatio); values[8].Add(s.BorderDarkFraction); values[9].Add(s.MaximumRingSupportCount); }
        public MarkerMorphologyQuantiles ToRecord() => new(values[0].Count, Quantiles(values[0]), Quantiles(values[1]), Quantiles(values[2]), Quantiles(values[3]), Quantiles(values[4]), Quantiles(values[5]), Quantiles(values[6]), Quantiles(values[7]), Quantiles(values[8]), Quantiles(values[9]));
        private static MarkerQuantiles Quantiles(List<double> source) { if (source.Count == 0) return new(0, 0, 0, 0, 0, 0, 0); double[] v = source.Order().ToArray(); double At(double f) { double p = (v.Length - 1) * f; int l = (int)Math.Floor(p), u = (int)Math.Ceiling(p); return l == u ? v[l] : v[l] + (v[u] - v[l]) * (p - l); } return new(v[0], At(.05), At(.25), At(.5), At(.75), At(.95), v[^1]); }
    }
    private sealed record MarkerQuantiles(double Minimum, double P05, double P25, double Median, double P75, double P95, double Maximum);
    private sealed record MarkerMorphologyQuantiles(int Count, MarkerQuantiles Probability, MarkerQuantiles DarkFractionAt012, MarkerQuantiles DarkFractionAt05, MarkerQuantiles InkCenter5x5Mean, MarkerQuantiles MaximumRowDarkFraction, MarkerQuantiles MaximumColumnDarkFraction, MarkerQuantiles ForegroundExtentBalance, MarkerQuantiles CovarianceEigenvalueRatio, MarkerQuantiles BorderDarkFraction, MarkerQuantiles MaximumRingSupportCount);
    private sealed record MarkerMorphologyAggregateReport(int SchemaVersion, string ReportScope, int RealDevProjects, int RealSealedProjects, int RealSealedReads, int SuccessfulProjects, int FailureCount, int ProposalCount, double PositiveLabelDistancePx, MarkerMorphologyQuantiles Positive, MarkerMorphologyQuantiles NegativeBelow025, MarkerMorphologyQuantiles NegativeAbove025, double MeanProjectRuntimeMs, double TotalRuntimeMs, string AssignmentsSha256, string ModelSha256, IReadOnlyDictionary<string, string> UpstreamOcrPayloadSha256, string PolicySha256, IReadOnlyDictionary<string, int> FailureKinds, bool CaseLevelOutput, bool TruthRowsOutput, bool PixelOutput, bool TrainingUse, bool CandidateSelection);

    private sealed record MarkerFeatureQuantiles(double Minimum, double P05, double P25, double Median, double P75, double P95, double Maximum);
    private sealed record MarkerNegativePatchFeatureQuantiles(MarkerFeatureQuantiles InkMean, MarkerFeatureQuantiles InkCenter5x5Mean, MarkerFeatureQuantiles InkMaximum, MarkerFeatureQuantiles OcrMaskMean, MarkerFeatureQuantiles OcrMaskMaximum, MarkerFeatureQuantiles ArtifactMaskMean, MarkerFeatureQuantiles ArtifactMaskMaximum);
    private sealed record MarkerNegativePatchAggregateReport(int SchemaVersion, string ReportScope, int RealDevProjects, int RealSealedProjects, int RealSealedReads, int SuccessfulProjects, int FailureCount, int ProposalGridPositionsConsidered, int RawProposalCount, int InkSupportedProposals, int OcrMaskRejects, int ArtifactMaskRejects, int EmittedProposals, int PositiveProposalCount, int NegativeProposalCount, MarkerNegativePatchFeatureQuantiles PositiveFeatures, MarkerNegativePatchFeatureQuantiles NegativeFeatures, double MeanProjectRuntimeMs, double TotalRuntimeMs, string AssignmentsSha256, IReadOnlyDictionary<string, string> UpstreamOcrPayloadSha256, string PolicySha256, string EvidencePolicySha256, int MarkerModelInferenceRuns, double PositiveLabelDistancePx, int SourceMutationCount, IReadOnlyDictionary<string, int> FailureKinds, bool CaseLevelOutput, bool TruthRowsOutput, bool PixelOutput, bool TrainingUse, bool CandidateSelection);
    private sealed record MarkerStageMatch(int TruePositives, int FalsePositives, int FalseNegatives, double Precision, double Recall);
    private sealed record DistributionSummary(double Minimum, double P05, double P10, double Median, double P90, double P95, double Maximum);
    private sealed record MarkerTruthPatchDistribution(int PatchCount, int PatchSizePx, DistributionSummary InkMean, DistributionSummary InkCenter5x5Mean, DistributionSummary InkMaximum, DistributionSummary OcrMaskMean, DistributionSummary OcrMaskMaximum, DistributionSummary ArtifactMaskMean, DistributionSummary ArtifactMaskMaximum, int OcrCenter5x5HardRejectCount, int ArtifactCenter5x5HardRejectCount, double HardMaskThreshold);
    private sealed record MarkerAggregateReport(int SchemaVersion, string ReportScope, int RealDevProjects, int RealSealedProjects, int RealSealedReads, int SuccessfulProjects, int FailureCount, int TruePositives, int FalsePositives, int FalseNegatives, double Precision, double Recall, double TolerancePx, IReadOnlyDictionary<string, bool> Gates, double MeanProjectInferenceMs, double TotalRuntimeMs, string AssignmentsSha256, string ModelSha256, IReadOnlyDictionary<string, string> UpstreamOcrPayloadSha256, string MaskingMode, string PolicySha256, IReadOnlyDictionary<string, int> FailureKinds, bool CaseLevelOutput, bool TruthRowsOutput, bool PixelOutput, bool TrainingUse, bool CandidateSelection, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CandidateRevision, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CandidateId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MarkerSearchBounds, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProposalMarkerStageCounters? StageCounters, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PreNmsTruePositives, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PreNmsFalsePositives, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PreNmsFalseNegatives, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreNmsPrecision, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreNmsRecall, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MarkerStageMatch? GridProposalMatch, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MarkerStageMatch? InkSupportedProposalMatch, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MarkerStageMatch? OcrUnmaskedProposalMatch, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MarkerStageMatch? EmittedProposalMatch, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MarkerStageMatch? AboveThresholdDecodedMatch, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MarkerTruthPatchDistribution? TruthPatchDistribution);
    private sealed record SyntheticDimensionReport(int TruthRegionCount, int TruePositives, int FalsePositives, int FalseNegatives, double DetectionPrecision, double DetectionRecall, double RecognitionExact, double CharacterErrorRate, double RoleAccuracy);
    private sealed record DetectorDimensionReport(int TruthRegionCount, int TruePositives, int FalsePositives, int FalseNegatives, double DetectionPrecision, double DetectionRecall);
    private sealed record TiledProposalAggregateReport(int SchemaVersion, string ReportScope, int SceneCount, int TruthRegionCount, int TruePositives, int FalsePositives, int FalseNegatives, double DetectionPrecision, double DetectionRecall, int TileSize, int TileOverlap, IReadOnlyDictionary<string, DetectorDimensionReport> ByDimension, double MeanSceneInferenceMs, int SuppressedCrossTileDuplicates, string ModelSha256, string DatasetManifestSha256, string AcceptancePolicySha256, string EvidencePolicySha256, IReadOnlyDictionary<string, bool> Gates, bool CaseLevelOutput, bool TruthRowsOutput, bool PixelOutput, int PublicGateEvaluations, int RealSealedReads, bool TrainingUse);
    private sealed record DetectorAggregateReport(int SchemaVersion, string ReportScope, int SceneCount, int TruthRegionCount, int TruePositives, int FalsePositives, int FalseNegatives, double DetectionPrecision, double DetectionRecall, IReadOnlyDictionary<string, DetectorDimensionReport> ByDimension, double MeanSceneInferenceMs, string ModelSha256, bool CaseLevelOutput, bool TruthRowsOutput, bool PixelOutput, double ProbabilityThreshold, double BoxConfidenceThreshold, double UnclipRatio, int MinimumSideLength, int MaximumRegions);
    private sealed record SyntheticAggregateReport(int SchemaVersion, string ReportScope, int SceneCount, int TruthRegionCount, int TruePositives, int FalsePositives, int FalseNegatives, double DetectionPrecision, double DetectionRecall, double RecognitionExact, double CharacterErrorRate, double RoleAccuracy, int ProhibitedStructureHits, double ProhibitedStructureHitRate, double MeanSceneInferenceMs, IReadOnlyDictionary<string, SyntheticDimensionReport> ByDimension, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ProposalAggregate>> ProposalThresholdSummary, IReadOnlyDictionary<string, ProposalAggregate> ProposalRoleRecall, IReadOnlyDictionary<string, bool> Gates, IReadOnlyDictionary<string, string> ModelPayloadSha256, string PolicySha256, bool CaseLevelOutput, bool TruthRowsOutput, bool PixelOutput);
}
