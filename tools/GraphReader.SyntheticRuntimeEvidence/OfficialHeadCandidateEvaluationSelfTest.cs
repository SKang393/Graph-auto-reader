// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OfficialHeadCandidateEvaluationSelfTest
{
    private static readonly double[] ExpectedDividerPositions = [30d, 70d];
    private static readonly string[] ClearanceVersions = ["synthetic-arrow-label-clearance-v1", "synthetic-legend-clearance-v1"];
    private static readonly string[] PeripheralClearanceVersions = [.. ClearanceVersions, "synthetic-peripheral-text-clearance-v1"];
    private static int ValidateCaptureProfiles()
    {
        JsonElement legacy = CaptureProfile(false, 28, 9);
        JsonElement supplemental = CaptureProfile(true, 6, 0);
        OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(legacy, false);
        OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(supplemental, true);
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(
            legacy, true), "scope");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(
            supplemental, false), "scope");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(
            CaptureProfile(true, 5, 1), true), "inventory");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(
            CaptureProfile(false, 29, 8), false), "inventory");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(
            CaptureProfile(true, 6, 0, 5, 1), true), "exchanges");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCaptureProfileForSelfTest(
            CaptureProfile(false, 28, 9, 1, 0), false), "exchanges");
        return 8;
    }

    private static JsonElement CaptureProfile(
        bool supplemental, int trainPanels, int devPanels,
        int? trainReports = null, int? devReports = null)
    {
        static object[] Splits(int train, int dev) => Enumerable.Repeat("train", train)
            .Concat(Enumerable.Repeat("validation", dev))
            .Select(static split => (object)new { split }).ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            schema = supplemental ? "graphreader.supplemental-official-head-tensor-capture-request.v1"
                : "graphreader.official-head-tensor-capture-request.v1",
            scope = supplemental ? "project-owned-synthetic-train-only-supplemental-model-free"
                : "project-owned-synthetic-train-dev-model-free",
            synthetic_only = true,
            private_data = false,
            sealed_data = false,
            truth_included = false,
            model_inference = false,
            training_input_ready = false,
            production_approved = false,
            capture_source = new { },
            assemblies = Array.Empty<object>(),
            binding = new { },
            candidate = new { },
            detector = new { },
            native = new { },
            license_inputs = Array.Empty<object>(),
            maximum_side_length = 960,
            dimension_multiple = 128,
            detector_configuration_fingerprint = "profile-contract-only",
            reports = Splits(trainReports ?? (supplemental ? 1 : 5), devReports ?? (supplemental ? 0 : 1)),
            panels = Splits(trainPanels, devPanels),
        });
    }

    private static int ValidatePhaseDividerInputs()
    {
        static JsonElement Axis(double[] positions, string space = "original_pixels") =>
            JsonSerializer.SerializeToElement(new
            {
                geometry = new
                {
                    coordinate_space = space,
                    phase_dividers = positions.Select(x => new
                    {
                        line = new { midpoint = new { x, y = 50d, is_finite = true } },
                    }).ToArray(),
                },
            });
        var plot = new OcrRectangle(10, 10, 100, 100);
        Require(OfficialHeadCandidateEvaluation.ReadPhaseDividerXs(Axis([70, 30, 70]), plot)
            .SequenceEqual(ExpectedDividerPositions), "detected divider positions sorted and deduplicated");
        Require(OfficialHeadCandidateEvaluation.ReadPhaseDividerXs(Axis([]), plot).Count == 0,
            "explicit measured absence of dividers");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ReadPhaseDividerXs(Axis([120]), plot),
            "outside");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ReadPhaseDividerXs(Axis([30], "source"), plot),
            "original pixels");
        return 4;
    }

    public static object Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "graphreader-head-evaluator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int checks = ValidateCaptureProfiles() + ValidatePhaseDividerInputs() + ValidateLayoutClearanceInputs();
            string request = Path.Combine(root, "request.json");
            string candidate = Path.Combine(root, "candidate.json");
            string output = Path.Combine(root, "new-output");
            File.WriteAllText(request, "{}");
            File.WriteAllText(candidate, "{}");
            string[] parsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.Command,
                request,
                new string('a', 64),
                candidate,
                new string('b', 64),
                output,
            ], root);
            Require(parsed.Length == 5 && parsed[1] == new string('a', 64) &&
                parsed[3] == new string('b', 64), "exact six-token command");
            checks++;

            string[] participantParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.ParticipantLaneCommand,
                request,
                new string('a', 64),
                candidate,
                new string('b', 64),
                output,
            ], root);
            Require(participantParsed.SequenceEqual(parsed), "separate participant-lane command");
            checks++;
            string[] insidePlotParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.InsidePlotCommand,
                request, new string('a', 64), candidate, new string('b', 64), output,
            ], root);
            Require(insidePlotParsed.SequenceEqual(parsed), "separate inside-plot command");
            checks++;
            string[] pixelBoundsParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.PixelBoundsCommand,
                request, new string('a', 64), candidate, new string('b', 64), output,
            ], root);
            Require(pixelBoundsParsed.SequenceEqual(parsed), "separate pixel-bounds command");
            checks++;
            string[] combinedParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.CombinedAssemblyCommand,
                request, new string('a', 64), candidate, new string('b', 64), output,
            ], root);
            Require(combinedParsed.SequenceEqual(parsed), "separate combined-assembly command");
            checks++;
            string[] headerParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.HeaderContextCommand,
                request, new string('a', 64), candidate, new string('b', 64), output,
            ], root);
            Require(headerParsed.SequenceEqual(parsed), "separate header-context command");
            checks++;
            string[] legendParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.LegendContextCommand,
                request, new string('a', 64), candidate, new string('b', 64), output,
            ], root);
            Require(legendParsed.SequenceEqual(parsed), "separate legend-context command");
            checks++;
            string[] layoutParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.LayoutClearanceCommand,
                request, new string('a', 64), candidate, new string('b', 64), output,
            ], root);
            Require(layoutParsed.SequenceEqual(parsed), "separate layout-clearance command");
            checks++;
            foreach (string tickCommand in new[] { OfficialHeadCandidateEvaluation.TickLaneCommand,
                         OfficialHeadCandidateEvaluation.LayoutTickLaneCommand })
            {
                Require(OfficialHeadCandidateEvaluation.ValidateCommand(
                    [tickCommand, request, new string('a', 64), candidate, new string('b', 64), output], root)
                    .SequenceEqual(parsed), "separate tick-lane commands");
                checks++;
            }
            string[] supplementalParsed = OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.SupplementalCommand,
                request, new string('a', 64), candidate, new string('b', 64), output,
            ], root);
            Require(supplementalParsed.SequenceEqual(parsed), "separate supplemental command");
            checks++;
            ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                "--unknown-candidate", request, new string('a', 64), candidate,
                new string('b', 64), output,
            ], root), "Usage:");
            checks++;

            JsonElement participantScope = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["schema"] = OfficialHeadCandidateEvaluation.CandidateSchema,
                ["scope"] = OfficialHeadCandidateEvaluation.CandidateScope,
                ["production_approved"] = false,
                ["training_input_ready"] = false,
                ["composition_version"] = ProductionOcrAdapter.ParticipantLaneCandidateCompositionVersion,
                ["native_path"] = "bound-native.dll",
                ["native_sha256"] = new string('a', 64),
                ["native_scope"] = "reviewed-source-runtime-local-diagnostic",
                ["license_inputs"] = Array.Empty<object>(),
                ["detector"] = new { },
                ["recognizer"] = new { },
                ["execution_assemblies"] = Array.Empty<object>(),
            });
            OfficialHeadCandidateEvaluation.ValidateCandidateScope(
                participantScope, ProductionOcrAdapter.ParticipantLaneCandidateCompositionVersion);
            checks++;
            ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCandidateScope(
                participantScope, ProductionOcrAdapter.OriginalDbCandidateCompositionVersion), "composition");
            checks++;

            ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCommand(
                [OfficialHeadCandidateEvaluation.Command], root), "Usage:");
            checks++;
            ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.Command, request, "short", candidate,
                new string('b', 64), output,
            ], root), "SHA-256");
            checks++;
            ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateCommand(
            [
                OfficialHeadCandidateEvaluation.Command, request, new string('a', 64),
                candidate, new string('b', 64), Path.Combine(root, "..", "foreign"),
            ], root), "inside");
            checks++;

            OcrPolygon mapped = OfficialHeadCandidateEvaluation.MapPolygon(
                OcrPolygon.FromRectangle(new OcrRectangle(2, 3, 5, 7)),
                [1, 0, 30, 0, 1, 11, 0, 0, 1]);
            Require(mapped.Bounds == new OcrRectangle(32, 14, 5, 7), "single source mapping");
            checks++;

            OcrDetectedRegion detected = Region("r1", 2, 3, 5, 7);
            OcrResult successful = Result(
                [new OcrRegion("r1", detected.Polygon, "10", [], OcrTextRole.XTick,
                0.9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed)]);
            OfficialHeadCandidateEvaluation.ValidateOcrCoverage([detected], successful);
            checks++;
            OfficialHeadCandidateEvaluation.ValidateBaselineReadingsPreserved(successful, successful);
            ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateBaselineReadingsPreserved(
                successful, successful with { Regions = [successful.Regions[0] with { Text = "70" }] }),
                "baseline reading");
            ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateOcrCoverage(
                [detected], successful with { Regions = [.. successful.Regions, successful.Regions[0] with { RegionId = "unbound" }] }),
                "complete raw detector inventory");
            checks += 3;

            OcrResult missing = Result([]);
            ExpectFailure(
                () => OfficialHeadCandidateEvaluation.ValidateOcrCoverage([detected], missing),
                "complete raw detector inventory");
            checks++;

            OcrResult failed = successful with
            {
                Failure = new OcrFailure("failed", "error", "Errors.Test", "failure", true, "retry"),
            };
            ExpectFailure(
                () => OfficialHeadCandidateEvaluation.ValidateOcrCoverage([detected], failed),
                "structurally unsuccessful");
            checks++;

            var assemblyRecords = new[]
            {
                typeof(OfficialHeadCandidateEvaluation).Assembly,
                typeof(GraphReader.App.Integration.Workflow.ProductionRasterFrameDecoder).Assembly,
                typeof(LocalOnnxTextRegionDetector).Assembly,
                typeof(GraphReader.Inference.InferenceRuntime).Assembly,
            }.Select(assembly => new Dictionary<string, string>
            {
                ["name"] = assembly.GetName().Name!,
                ["path"] = Path.GetFileName(assembly.Location),
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            }).ToArray();
            Require(OfficialHeadCandidateEvaluation.ReadExecutionAssemblies(
                JsonSerializer.SerializeToElement(assemblyRecords), AppContext.BaseDirectory).Length == 4,
                "bound executing assemblies");
            checks++;
            assemblyRecords[0]["sha256"] = new string('0', 64);
            ExpectFailure(() => OfficialHeadCandidateEvaluation.ReadExecutionAssemblies(
                JsonSerializer.SerializeToElement(assemblyRecords), AppContext.BaseDirectory), "bytes differ");
            checks++;

            return new
            {
                Status = "passed",
                CheckCount = checks,
                ModelInference = false,
                TruthRead = false,
                PrivateData = false,
                SealedData = false,
                ProductionApproved = false,
            };
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OcrDetectedRegion Region(string id, double x, double y, double width, double height) =>
        new(id, OcrPolygon.FromRectangle(new OcrRectangle(x, y, width, height)), 0, 0.9);

    private static OcrResult Result(IReadOnlyList<OcrRegion> regions) => new(
        1,
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        OcrContract.Stage,
        "test",
        Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])),
        OcrContract.CoordinateSpace,
        regions,
        [],
        new OcrTiming(0, 0, 0, 0),
        0.9,
        [],
        new OcrCacheDiagnostics(false, "cache", regions.Count, regions.Count == 0 ? 0 : 1),
        null,
        []);

    private static void ExpectFailure(Action action, string expected)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected validation failure was not raised.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            if (!exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected failure containing '{expected}', got '{exception.Message}'.", exception);
            }
        }
    }

    private static int ValidateLayoutClearanceInputs()
    {
        static JsonElement Request(bool privateData = false, bool truth = false, int sources = 23,
            bool peripheralSchema = false, bool peripheralVersions = false) =>
            JsonSerializer.SerializeToElement(new
            {
                schema = peripheralSchema ? "graphreader.layout-clearance-ocr-inputs.v2"
                    : "graphreader.layout-clearance-ocr-inputs.v1", synthetic_only = true,
                private_data = privateData, sealed_data = false, truth_included = truth,
                production_approved = false, training_input_ready = false,
                historical_request = new { },
                generator_versions = peripheralVersions ? PeripheralClearanceVersions : ClearanceVersions,
                generator_sources = Array.Empty<object>(),
                sources = Enumerable.Repeat(new { }, sources).ToArray(),
                panels = Enumerable.Repeat(new { }, 37).ToArray(),
            });
        OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(Request());
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(Request(privateData: true)), "truth-free");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(Request(truth: true)), "truth-free");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(Request(sources: 22)), "inventory");
        OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(Request(peripheralSchema: true, peripheralVersions: true));
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(Request(peripheralSchema: true)), "inventory");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(Request(peripheralVersions: true)), "inventory");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(
            Request(peripheralSchema: true, peripheralVersions: true, privateData: true)), "truth-free");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateLayoutClearanceScope(
            Request(peripheralSchema: true, peripheralVersions: true, truth: true)), "truth-free");
        byte[] gray = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        byte[] color = Enumerable.Range(0, 36).Select(i => (byte)(i + 30)).ToArray();
        var source = new OcrImage(4, 3, 4, gray, OcrSourceImage.Original,
            OcrFrameTransform.Identity, BgrPixels: new OcrBgrBytePixels(12, color));
        var crop = new OcrImage(2, 1, 2, gray.AsMemory(5, 2), OcrSourceImage.Original,
            OcrFrameTransform.Identity, BgrPixels: new OcrBgrBytePixels(6, color.AsMemory(15, 6)));
        OfficialHeadCandidateEvaluation.ValidateDerivedCrop(source, crop, [1, 1, 2, 1]);
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateDerivedCrop(source, crop, [0, 1, 2, 1]), "declared crop");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateDerivedCrop(source, crop, [3, 1, 2, 1]), "bounds");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateDerivedCrop(source,
            crop with { BgrPixels = new OcrBgrBytePixels(6, new byte[6]) }, [1, 1, 2, 1]), "declared crop");
        ExpectFailure(() => OfficialHeadCandidateEvaluation.ValidateDerivedCrop(source,
            crop with { Pixels = new byte[2] }, [1, 1, 2, 1]), "declared crop");
        return 14;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + label);
        }
    }
}
