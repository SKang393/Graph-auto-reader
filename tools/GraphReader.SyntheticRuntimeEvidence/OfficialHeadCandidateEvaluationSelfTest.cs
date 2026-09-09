// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OfficialHeadCandidateEvaluationSelfTest
{
    public static object Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "graphreader-head-evaluator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int checks = 0;
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

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + label);
        }
    }
}
