// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDigWholeWorkflowTruthAdapterSelfTest
{
    internal static object Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GraphReaderEngaugeTruthAdapterSelfTest",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        try
        {
            byte[] png = CreatePng(128, 96);
            string valid = Document(
                png,
                Anchors(
                    (10, 20, 1, 0),
                    (10, 80, 1, 100),
                    (110, 20, 11, 0)),
                Curve("Baseline", (30, 50), (40, 35)) +
                Curve("Intervention", (70, 65)));
            string path = Write(directory, "valid.dig", valid);
            EngaugeDigWholeWorkflowTruth parsed = EngaugeDigWholeWorkflowTruthAdapter.Read(
                path, "anonymous-case-001", CancellationToken.None);
            Require(parsed.SourceWidth == 128 && parsed.SourceHeight == 96, "source dimensions");
            Require(parsed.SourceImageSha256 == Convert.ToHexStringLower(SHA256.HashData(png)), "source hash");
            Require(parsed.Anchors.Count == 3 && parsed.Curves.Count == 2, "anchor and curve counts");
            Require(parsed.Curves[0].CurveKey == "Baseline" &&
                parsed.Curves[1].CurveKey == "Intervention", "exact curve identities");
            Require(parsed.TruthCase.Series.Count == 2 && parsed.TruthCase.Points.Count == 3,
                "complete point inventory");
            Require(parsed.TruthCase.Relations is null &&
                parsed.TruthCase.Points.All(static point => point.AuthoritativePhaseCode is null),
                "relations and phases unavailable");
            WholeWorkflowTruthPoint mapped = parsed.TruthCase.Points[0];
            Require(Close(mapped.GraphX, 3) && Close(mapped.GraphY, 50) &&
                mapped.ExpectedExportX == 3 && mapped.ExpectedExportMode == ExportMode.PrintedSession,
                "three-anchor mapping");
            byte[] copy = parsed.CopyImageBytes();
            copy[0] = 0;
            Require(parsed.CopyImageBytes()[0] == 137, "immutable image bytes");
            WholeWorkflowEvaluationResult failedRuntime = WholeWorkflowCsvEvaluator.Evaluate(
                [parsed.TruthCase],
                [new WholeWorkflowCaseOutput(
                    parsed.TruthCase.CaseKey,
                    parsed.SourceImageSha256,
                    WorkflowSucceeded: false,
                    FailureCode: "FIXTURE_RUNTIME_FAILURE",
                    Artifacts: [])],
                new WholeWorkflowEvaluationOptions(5, 0.01, 5),
                CancellationToken.None);
            Require(failedRuntime.TruthSeries == 2 && failedRuntime.TruthPoints == 3 &&
                failedRuntime.FailedCases == 1 && failedRuntime.MatchedPoints == 0,
                "failed runtime retains full truth denominator");
            checks.Add("valid_exact_curve_and_point_inventory");
            checks.Add("three_anchor_affine_mapping");
            checks.Add("source_image_identity_and_immutability");
            checks.Add("relations_and_phases_explicitly_unavailable");
            checks.Add("failed_runtime_retains_full_truth_denominator");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try
                {
                    _ = EngaugeDigWholeWorkflowTruthAdapter.Read(
                        path, "canceled-case", canceled.Token);
                    throw new InvalidOperationException("Expected cancellation.");
                }
                catch (OperationCanceledException)
                {
                }
            }
            checks.Add("pre_canceled_read_rejected");

            EngaugeDigWholeWorkflowTruth translated = EngaugeDigWholeWorkflowTruthAdapter.Read(
                Write(directory, "translated-calibration.dig", Document(
                    png,
                    Anchors(
                        (10, 20, 1_000_000_001, -2_000_000_000),
                        (10, 80, 1_000_000_001, -1_999_999_900),
                        (110, 20, 1_000_000_011, -2_000_000_000)),
                    Curve("Translated", (30, 50)))),
                "anonymous-case-translated",
                CancellationToken.None);
            Require(Close(translated.TruthCase.Points[0].GraphX, 1_000_000_003) &&
                Close(translated.TruthCase.Points[0].GraphY, -1_999_999_950),
                "translation-invariant calibration");
            checks.Add("calibration_degeneracy_is_translation_invariant");

            EngaugeDigWholeWorkflowTruth exactNames = EngaugeDigWholeWorkflowTruthAdapter.Read(
                Write(directory, "exact-names.dig", Document(
                    png,
                    Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("No points removed", (20, 40)) + Curve("A &amp; B", (30, 50)))),
                "anonymous-case-002",
                CancellationToken.None);
            Require(exactNames.Curves.Count == 2 &&
                exactNames.TruthCase.Series.Select(static series => series.SeriesKey)
                    .SequenceEqual(["No points removed", "A & B"], StringComparer.Ordinal),
                "exact XML-decoded identities");
            checks.Add("every_nonempty_curve_identity_preserved_exactly");
            ExpectFailure(directory, "empty-curve.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("Empty")),
                "DIG_CURVE_POINTS_MISSING");
            checks.Add("empty_curve_rejected_without_silent_drop");

            ExpectFailure(directory, "dtd.dig",
                "<!DOCTYPE Document [<!ENTITY x 'unsafe'>]>" + valid,
                "DIG_XML_INVALID");
            checks.Add("dtd_and_entities_rejected");
            ExpectFailure(directory, "two-anchors.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100)), Curve("A", (30, 50))),
                "DIG_AXIS_ANCHOR_COUNT:2");
            checks.Add("exact_three_anchor_count");
            ExpectFailure(directory, "duplicate-curve.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("Same", (30, 50)) + Curve("Same", (60, 50))),
                "DIG_CURVE_IDENTITY_DUPLICATE");
            checks.Add("duplicate_curve_identity_rejected");
            ExpectFailure(directory, "missing-curve-name.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    "<Curve><Point><PositionScreen X=\"30\" Y=\"50\"/></Point></Curve>"),
                "DIG_CURVE_IDENTITY_INVALID");
            checks.Add("missing_curve_identity_rejected");
            ExpectFailure(directory, "malformed-point.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    "<Curve Name=\"A\"><Point/></Curve>"),
                "DIG_CURVE_SCREEN_POSITION");
            checks.Add("malformed_curve_point_rejected");
            ExpectFailure(directory, "ambiguous-point.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    "<Curve Name=\"A\"><Point><PositionScreen X=\"30\" Y=\"50\"/>" +
                    "<PositionScreen X=\"31\" Y=\"51\"/></Point></Curve>"),
                "DIG_CURVE_SCREEN_POSITION");
            checks.Add("ambiguous_curve_point_rejected");
            ExpectFailure(directory, "degenerate.dig",
                Document(png, Anchors((10, 20, 1, 0), (20, 30, 2, 1), (30, 40, 3, 2)),
                    Curve("A", (30, 50))),
                "DIG_AXIS_ANCHORS_DEGENERATE");
            checks.Add("degenerate_calibration_rejected");
            ExpectFailure(directory, "nearly-degenerate.dig",
                Document(png, Anchors((10, 10, 0, 0), (50, 50, 1, 1), (90, 90.0000000001, 2, 2.1)),
                    Curve("A", (30, 50))),
                "DIG_AXIS_ANCHORS_DEGENERATE");
            checks.Add("nearly_collinear_calibration_rejected");
            ExpectFailure(directory, "nonlinear.dig",
                Document(png, "<CoordinateSettings XScale=\"Logarithmic\"/>" +
                    Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("A", (30, 50))),
                "DIG_NONLINEAR_SCALE_UNSUPPORTED");
            checks.Add("detected_nonlinear_scale_rejected");
            ExpectFailure(directory, "outside.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("A", (129, 50))),
                "DIG_CURVE_POINT_OUTSIDE_IMAGE");
            checks.Add("out_of_source_point_rejected");
            ExpectFailure(directory, "dimension-mismatch.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("A", (30, 50))).Replace(
                        "<SourceImage Format=\"PNG\">",
                        "<SourceImage Format=\"PNG\" Width=\"127\" Height=\"96\">",
                        StringComparison.Ordinal),
                "DIG_IMAGE_DIMENSION_MISMATCH");
            checks.Add("declared_image_dimension_mismatch_rejected");
            ExpectFailure(directory, "format-mismatch.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("A", (30, 50))).Replace(
                        "Format=\"PNG\"",
                        "Format=\"JPEG\"",
                        StringComparison.Ordinal),
                "DIG_IMAGE_FORMAT_MISMATCH");
            checks.Add("declared_image_format_mismatch_rejected");
            ExpectFailure(directory, "curve-graph-coordinate.dig",
                Document(png, Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    "<Curve Name=\"A\"><Point><PositionScreen X=\"30\" Y=\"50\"/>" +
                    "<PositionGraph X=\"3\" Y=\"50\"/></Point></Curve>"),
                "DIG_CURVE_GRAPH_POSITION_AMBIGUOUS");
            checks.Add("curve_graph_coordinate_ambiguity_rejected");
            byte[] truncatedPng = png[..24];
            ExpectFailure(directory, "truncated-png.dig",
                Document(truncatedPng,
                    Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)),
                    Curve("A", (30, 50))),
                "DIG_IMAGE_PNG_INVALID");
            checks.Add("complete_png_container_required");

            string oversized = Path.Combine(directory, "oversized.dig");
            using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes + 1);
            }
            ExpectFailure(oversized, "DIG_PROJECT_SIZE_UNSUPPORTED");
            checks.Add("project_byte_limit_enforced_before_parse");

            return new
            {
                status = "pass",
                check_count = checks.Count,
                checks,
                private_reads = 0,
                sealed_reads = 0,
                model_runs = 0,
                truth_relations_available = false,
                phase_truth_available = false,
            };
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ExpectFailure(string directory, string name, string xml, string code)
    {
        string path = Write(directory, name, xml);
        ExpectFailure(path, code);
    }

    private static void ExpectFailure(string path, string code)
    {
        try
        {
            _ = EngaugeDigWholeWorkflowTruthAdapter.Read(path, "fixture-case", CancellationToken.None);
            throw new InvalidOperationException($"Expected adapter failure '{code}'.");
        }
        catch (EngaugeDigTruthException exception) when (
            exception.Message.StartsWith(code, StringComparison.Ordinal) &&
            code.StartsWith(exception.Code, StringComparison.Ordinal))
        {
        }
    }

    private static string Write(string directory, string name, string xml)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string Document(byte[] png, string anchors, string curves) =>
        "<Document><SourceImage Format=\"PNG\">" + Convert.ToBase64String(png) +
        "</SourceImage><Axes>" + anchors + "</Axes>" + curves + "</Document>";

    private static string Anchors(params (double ScreenX, double ScreenY, double GraphX, double GraphY)[] values) =>
        string.Concat(values.Select(value => FormattableString.Invariant(
            $"<Point><PositionScreen X=\"{value.ScreenX}\" Y=\"{value.ScreenY}\"/><PositionGraph X=\"{value.GraphX}\" Y=\"{value.GraphY}\"/></Point>")));

    private static string Curve(string name, params (double X, double Y)[] points) =>
        "<Curve Name=\"" + name + "\">" + string.Concat(points.Select(point =>
            FormattableString.Invariant(
                $"<Point><PositionScreen X=\"{point.X}\" Y=\"{point.Y}\"/></Point>"))) + "</Curve>";

    private static byte[] CreatePng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> dimensions = stackalloc byte[13];
        dimensions.Clear();
        WriteBigEndian(dimensions[..4], width);
        WriteBigEndian(dimensions.Slice(4, 4), height);
        dimensions[8] = 8;
        dimensions[9] = 0;
        WriteChunk(output, "IHDR"u8, dimensions);
        byte[] pixels = new byte[checked((width + 1) * height)];
        Array.Fill(pixels, byte.MaxValue);
        for (var row = 0; row < height; row++)
        {
            pixels[row * (width + 1)] = 0;
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(pixels);
        }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, payload.Length);
        output.Write(length);
        output.Write(type);
        output.Write(payload);
        byte[] checksumInput = new byte[type.Length + payload.Length];
        type.CopyTo(checksumInput);
        payload.CopyTo(checksumInput.AsSpan(type.Length));
        Span<byte> checksum = stackalloc byte[4];
        WriteBigEndian(checksum, unchecked((int)Crc32(checksumInput)));
        output.Write(checksum);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & unchecked((uint)-(int)(crc & 1)));
            }
        }
        return ~crc;
    }

    private static void WriteBigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) <= 1e-9;

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Engauge truth adapter self-test failed: " + label);
        }
    }
}
