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

            int validReadCallbacks = 0;
            bool streamWasOpenDuringCallback = false;
            EngaugeDigWholeWorkflowTruth callbackParsed = EngaugeDigWholeWorkflowTruthAdapter.Read(
                path,
                "anonymous-case-callback",
                () =>
                {
                    validReadCallbacks++;
                    try
                    {
                        using var exclusive = new FileStream(
                            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException)
                    {
                        streamWasOpenDuringCallback = true;
                    }
                },
                CancellationToken.None);
            Require(validReadCallbacks == 1 && streamWasOpenDuringCallback &&
                callbackParsed.ProjectSha256 == parsed.ProjectSha256 &&
                callbackParsed.TruthCase.Points.SequenceEqual(parsed.TruthCase.Points),
                "first payload callback runs once after open without changing output");
            checks.Add("first_payload_callback_runs_once_after_open_without_changing_output");

            using (var callerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                callerStream.Position = callerStream.Length / 2;
                int callerCallbacks = 0;
                EngaugeDigWholeWorkflowTruth streamParsed = EngaugeDigWholeWorkflowTruthAdapter.Read(
                    callerStream,
                    "anonymous-case-001",
                    () =>
                    {
                        callerCallbacks++;
                        callerStream.Position = callerStream.Length - 1;
                    },
                    CancellationToken.None);
                Require(callerCallbacks == 1 && callerStream.CanRead && callerStream.CanSeek &&
                    streamParsed.ProjectSha256 == parsed.ProjectSha256 &&
                    streamParsed.SourceImageSha256 == parsed.SourceImageSha256 &&
                    streamParsed.CopyImageBytes().SequenceEqual(parsed.CopyImageBytes()) &&
                    streamParsed.TruthCase.Points.SequenceEqual(parsed.TruthCase.Points) &&
                    streamParsed.Anchors.SequenceEqual(parsed.Anchors),
                    "caller-owned stream matches path even when callback repositions it");
                callerStream.Position = 0;
                Require(callerStream.ReadByte() >= 0, "caller handle remains open on success");
            }
            checks.Add("caller_owned_stream_matches_path_and_remains_open");

            string mutablePath = Write(directory, "caller-mutable.dig", valid);
            string replacement = valid.Replace(
                "<PositionScreen X=\"30\" Y=\"50\"/>",
                "<PositionScreen X=\"31\" Y=\"50\"/>", StringComparison.Ordinal);
            Require(replacement != valid, "mutation fixture changes a synthetic point");
            using (var mutableStream = new MutatesAfterReadStream(mutablePath, replacement))
            {
                int callbacks = 0;
                EngaugeDigWholeWorkflowTruth stable = EngaugeDigWholeWorkflowTruthAdapter.Read(
                    mutableStream, "anonymous-mutable-stream", () => callbacks++, CancellationToken.None);
                Require(mutableStream.Mutated && callbacks == 1 && mutableStream.CanRead &&
                    File.ReadAllText(mutablePath) == replacement &&
                    stable.ProjectSha256 == parsed.ProjectSha256 &&
                    stable.TruthCase.Points.SequenceEqual(parsed.TruthCase.Points),
                    "parsed result and hash use captured bytes despite later file mutation");
            }
            checks.Add("caller_stream_snapshot_survives_concurrent_mutation");

            using (var growingStream = new GrowingReadStream(path))
            {
                int callbacks = 0;
                try
                {
                    _ = EngaugeDigWholeWorkflowTruthAdapter.Read(
                        growingStream, "anonymous-growing-stream", () => callbacks++, CancellationToken.None);
                    throw new InvalidOperationException("Expected growing stream size rejection.");
                }
                catch (EngaugeDigTruthException exception) when (exception.Code == "DIG_PROJECT_SIZE_UNSUPPORTED") { }
                Require(callbacks == 1 && growingStream.CanRead,
                    "growing stream rejected at byte limit without closing caller handle");
            }
            checks.Add("caller_stream_growth_is_bounded_after_initial_length_check");

            using (var callerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int callbacks = 0;
                var failure = new InvalidOperationException("fixture-stream-callback-failure");
                try
                {
                    _ = EngaugeDigWholeWorkflowTruthAdapter.Read(callerStream, "anonymous-stream-callback-failure",
                        () => { callbacks++; throw failure; }, CancellationToken.None);
                    throw new InvalidOperationException("Expected caller callback failure.");
                }
                catch (InvalidOperationException exception) when (ReferenceEquals(exception, failure)) { }
                Require(callbacks == 1 && callerStream.CanRead && callerStream.Position == 0,
                    "callback failure leaves caller handle open without reading");

                using var canceledStream = new CancellationTokenSource();
                canceledStream.Cancel();
                try
                {
                    _ = EngaugeDigWholeWorkflowTruthAdapter.Read(callerStream, "anonymous-stream-canceled",
                        () => callbacks++, canceledStream.Token);
                    throw new InvalidOperationException("Expected caller stream cancellation.");
                }
                catch (OperationCanceledException) { }
                Require(callbacks == 1 && callerStream.CanRead,
                    "canceled caller stream does not run callback or close handle");
            }
            checks.Add("caller_stream_callback_and_cancellation_preserve_handle");

            string malformedStreamPath = Write(directory, "caller-malformed.dig", "<Document>");
            using (var callerStream = new FileStream(malformedStreamPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int callbacks = 0;
                try
                {
                    _ = EngaugeDigWholeWorkflowTruthAdapter.Read(callerStream, "anonymous-malformed-stream",
                        () => callbacks++, CancellationToken.None);
                    throw new InvalidOperationException("Expected caller stream parse failure.");
                }
                catch (EngaugeDigTruthException exception) when (exception.Code == "DIG_XML_INVALID") { }
                Require(callbacks == 1 && callerStream.CanRead && callerStream.CanSeek,
                    "parse failure leaves caller handle open after one callback");
            }
            checks.Add("caller_stream_parse_failure_preserves_handle");

            int unsupportedStreamCallbacks = 0;
            using (var writeOnly = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                try
                {
                    _ = EngaugeDigWholeWorkflowTruthAdapter.Read(writeOnly, "anonymous-write-only",
                        () => unsupportedStreamCallbacks++, CancellationToken.None);
                    throw new InvalidOperationException("Expected unsupported caller stream.");
                }
                catch (EngaugeDigTruthException exception) when (exception.Code == "DIG_STREAM_UNSUPPORTED") { }
                Require(writeOnly.CanWrite && unsupportedStreamCallbacks == 0,
                    "write-only caller handle rejected before callback and remains open");
            }
            var closedStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            closedStream.Dispose();
            try
            {
                _ = EngaugeDigWholeWorkflowTruthAdapter.Read(closedStream, "anonymous-closed",
                    () => unsupportedStreamCallbacks++, CancellationToken.None);
                throw new InvalidOperationException("Expected closed caller stream rejection.");
            }
            catch (EngaugeDigTruthException exception) when (exception.Code == "DIG_STREAM_UNSUPPORTED") { }
            Require(unsupportedStreamCallbacks == 0, "closed caller handle rejected before callback");
            checks.Add("caller_stream_unsupported_handles_rejected_before_callback");

            int throwingCallbacks = 0;
            var callbackFailure = new InvalidOperationException("fixture-ledger-failure");
            try
            {
                _ = EngaugeDigWholeWorkflowTruthAdapter.Read(
                    path,
                    "anonymous-case-callback-failure",
                    () =>
                    {
                        throwingCallbacks++;
                        throw callbackFailure;
                    },
                    CancellationToken.None);
                throw new InvalidOperationException("Expected first payload callback failure.");
            }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, callbackFailure))
            {
            }
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Require(exclusive.Length > 0, "callback failure closes unopened payload stream");
            }
            Require(throwingCallbacks == 1, "throwing callback runs exactly once");
            checks.Add("callback_failure_prevents_payload_read_and_closes_stream");

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                int canceledCallbacks = 0;
                try
                {
                    _ = EngaugeDigWholeWorkflowTruthAdapter.Read(
                        path, "canceled-case", () => canceledCallbacks++, canceled.Token);
                    throw new InvalidOperationException("Expected cancellation.");
                }
                catch (OperationCanceledException)
                {
                }
                Require(canceledCallbacks == 0, "pre-canceled read does not invoke callback");
            }
            checks.Add("pre_canceled_read_rejected_without_callback");

            int preReadFailureCallbacks = 0;
            string missingPath = Path.Combine(directory, "missing.dig");
            try
            {
                _ = EngaugeDigWholeWorkflowTruthAdapter.Read(
                    missingPath, "missing-case", () => preReadFailureCallbacks++, CancellationToken.None);
                throw new InvalidOperationException("Expected missing project failure.");
            }
            catch (FileNotFoundException)
            {
            }
            string emptyPath = Path.Combine(directory, "empty.dig");
            File.WriteAllBytes(emptyPath, []);
            ExpectFailureWithoutCallback(emptyPath, "DIG_PROJECT_SIZE_UNSUPPORTED", ref preReadFailureCallbacks);
            Require(preReadFailureCallbacks == 0, "missing and empty inputs do not invoke callback");
            checks.Add("missing_and_empty_inputs_fail_before_callback");

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

            byte[] engaugeImage = [0, 0, 0, 1, .. png];
            string axisCurve = "<Curve CurveName=\"Axes\"><CurvePoints>" +
                Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0))
                    .Replace("<Point>", "<Point IsAxisPoint=\"True\">", StringComparison.Ordinal) +
                "</CurvePoints></Curve>";
            const string dataCurve = "<Curve CurveName=\"Curve1\"><CurvePoints>" +
                "<Point IsAxisPoint=\"False\"><PositionScreen X=\"30\" Y=\"50\"/></Point>" +
                "</CurvePoints></Curve>";
            string engaugeXml = "<Document><Image Width=\"128\" Height=\"96\"><![CDATA[" +
                Convert.ToBase64String(engaugeImage) + "]]></Image>" + axisCurve + dataCurve + "</Document>";
            EngaugeDigWholeWorkflowTruth engauge = EngaugeDigWholeWorkflowTruthAdapter.Read(
                Write(directory, "engauge-axis-curve.dig", engaugeXml),
                "anonymous-engauge-fixture", CancellationToken.None);
            Require(engauge.Anchors.Count == 3 && engauge.Curves.Count == 1 &&
                engauge.Curves[0].CurveKey == "Curve1" && engauge.TruthCase.Series.Count == 1 &&
                engauge.TruthCase.Points.Count == 1 &&
                Close(engauge.TruthCase.Points[0].GraphX, 3) &&
                Close(engauge.TruthCase.Points[0].GraphY, 50) &&
                engauge.CopyImageBytes().SequenceEqual(png),
                "Engauge flagged axis curve and prefixed embedded PNG");
            checks.Add("engauge_axis_curve_flagged_separately_from_data");

            ExpectFailure(directory, "engauge-ambiguous-curve-identity.dig",
                engaugeXml.Replace("CurveName=\"Curve1\"", "CurveName=\"Curve1\" Name=\"Other\"", StringComparison.Ordinal),
                "DIG_CURVE_IDENTITY_INVALID");
            ExpectFailure(directory, "engauge-invalid-axis-flag.dig",
                engaugeXml.Replace("IsAxisPoint=\"False\"", "IsAxisPoint=\"unknown\"", StringComparison.Ordinal),
                "DIG_AXIS_POINT_FLAG_INVALID");
            ExpectFailure(directory, "engauge-duplicate-axis-flags.dig",
                engaugeXml.Replace("IsAxisPoint=\"False\"",
                    "IsAxisPoint=\"False\" xmlns:extra=\"urn:synthetic\" extra:IsAxisPoint=\"True\"",
                    StringComparison.Ordinal),
                "DIG_AXIS_POINT_FLAG_INVALID");
            ExpectFailure(directory, "engauge-mixed-points.dig",
                engaugeXml.Replace("IsAxisPoint=\"False\"", "IsAxisPoint=\"True\"", StringComparison.Ordinal)
                    .Replace("<Curve CurveName=\"Curve1\"><CurvePoints>", "<Curve CurveName=\"Curve1\"><CurvePoints>" +
                        "<Point IsAxisPoint=\"False\"><PositionScreen X=\"40\" Y=\"50\"/></Point>", StringComparison.Ordinal),
                "DIG_MIXED_AXIS_AND_CURVE_POINTS");
            ExpectFailure(directory, "engauge-unowned-data-point.dig",
                Document(png,
                    Anchors((10, 20, 1, 0), (10, 80, 1, 100), (110, 20, 11, 0)) +
                    "<Point IsAxisPoint=\"False\"><PositionScreen X=\"30\" Y=\"50\"/></Point>",
                    Curve("A", (30, 50))),
                "DIG_UNOWNED_CURVE_POINT");
            checks.Add("engauge_ambiguous_identity_and_axis_flags_rejected");

            string doctypeDocument = "<?xml version=\"1.0\"?>\n<!DOCTYPE engauge>\n" + engaugeXml;
            EngaugeDigWholeWorkflowTruth doctypeParsed = EngaugeDigWholeWorkflowTruthAdapter.Read(
                Write(directory, "engauge-empty-doctype.dig", doctypeDocument),
                "anonymous-doctype-fixture", CancellationToken.None);
            Require(doctypeParsed.Anchors.Count == 3 && doctypeParsed.TruthCase.Points.Count == 1 &&
                doctypeParsed.CopyImageBytes().SequenceEqual(png), "empty Engauge doctype accepted");
            EngaugeDigWholeWorkflowTruth whitespaceDoctype = EngaugeDigWholeWorkflowTruthAdapter.Read(
                Write(directory, "engauge-whitespace-doctype.dig",
                    "<!-- synthetic prolog -->\n<!DOCTYPE\nengauge \n>" + engaugeXml),
                "anonymous-whitespace-doctype", CancellationToken.None);
            Require(whitespaceDoctype.TruthCase.Points.Count == 1, "empty Engauge doctype allows XML whitespace");
            string utf16Path = Path.Combine(directory, "engauge-utf16-doctype.dig");
            File.WriteAllText(utf16Path,
                "<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<!DOCTYPE engauge>\n" + engaugeXml,
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            int acceptedDoctypeCallbacks = 0;
            EngaugeDigWholeWorkflowTruth utf16Doctype = EngaugeDigWholeWorkflowTruthAdapter.Read(
                utf16Path, "anonymous-utf16-doctype", () => acceptedDoctypeCallbacks++, CancellationToken.None);
            Require(utf16Doctype.TruthCase.Points.Count == 1 && acceptedDoctypeCallbacks == 1,
                "UTF-16 LE BOM doctype and first-read callback");
            string utf16BigEndianPath = Path.Combine(directory, "engauge-utf16be-doctype.dig");
            File.WriteAllText(utf16BigEndianPath,
                "<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<!DOCTYPE engauge>\n" + engaugeXml,
                new UnicodeEncoding(bigEndian: true, byteOrderMark: true));
            EngaugeDigWholeWorkflowTruth utf16BigEndian = EngaugeDigWholeWorkflowTruthAdapter.Read(
                utf16BigEndianPath, "anonymous-utf16be-doctype", CancellationToken.None);
            Require(utf16BigEndian.TruthCase.Points.Count == 1 && utf16BigEndian.CopyImageBytes().SequenceEqual(png),
                "UTF-16 BE BOM doctype accepted");
            string utf16NoBomPath = Path.Combine(directory, "engauge-utf16-no-bom.dig");
            File.WriteAllText(utf16NoBomPath,
                "<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<!DOCTYPE engauge>\n" + engaugeXml,
                new UnicodeEncoding(bigEndian: false, byteOrderMark: false));
            ExpectFailure(utf16NoBomPath, "DIG_XML_ENCODING_UNSUPPORTED");
            ExpectFailure(directory, "engauge-unsupported-encoding.dig",
                "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><!DOCTYPE engauge>" + engaugeXml,
                "DIG_XML_ENCODING_UNSUPPORTED");
            int rejectedDoctypeCallbacks = 0;
            string rejectedDoctype = Write(directory, "engauge-rejected-callback.dig",
                "<!DOCTYPE engauge []>" + engaugeXml);
            try
            {
                _ = EngaugeDigWholeWorkflowTruthAdapter.Read(rejectedDoctype,
                    "anonymous-rejected-doctype", () => rejectedDoctypeCallbacks++, CancellationToken.None);
                throw new InvalidOperationException("Expected rejected doctype.");
            }
            catch (EngaugeDigTruthException exception) when (exception.Code == "DIG_XML_INVALID") { }
            Require(rejectedDoctypeCallbacks == 1, "rejected doctype first-read callback");
            ExpectFailure(directory, "engauge-oversized-prolog.dig",
                "<!--" + new string('a', (1024 * 1024) + 1) + "--><!DOCTYPE engauge>" + engaugeXml,
                "DIG_XML_PROLOG_LIMIT");
            ExpectFailure(directory, "engauge-oversized-declaration.dig",
                "<!DOCTYPE engauge " + new string(' ', (1024 * 1024) + 1) + ">" + engaugeXml,
                "DIG_XML_PROLOG_LIMIT");
            ExpectFailure(directory, "engauge-external-doctype.dig",
                "<!DOCTYPE engauge SYSTEM \"file:///not-accessible.dtd\">" + engaugeXml, "DIG_XML_INVALID");
            ExpectFailure(directory, "engauge-other-doctype.dig",
                "<!DOCTYPE other>" + engaugeXml, "DIG_XML_INVALID");
            ExpectFailure(directory, "engauge-empty-subset.dig",
                "<!DOCTYPE engauge []>" + engaugeXml, "DIG_XML_INVALID");
            ExpectFailure(directory, "engauge-comment-lookalike.dig",
                "<!-- <!DOCTYPE engauge> --><!DOCTYPE engauge []>" + engaugeXml, "DIG_XML_INVALID");
            ExpectFailure(directory, "engauge-entity-subset.dig",
                "<!DOCTYPE engauge [<!ENTITY x \"unsafe\">]>" + engaugeXml, "DIG_XML_INVALID");
            checks.Add("only_empty_engauge_doctype_accepted");

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
            int oversizedCallbacks = 0;
            ExpectFailureWithoutCallback(
                oversized, "DIG_PROJECT_SIZE_UNSUPPORTED", ref oversizedCallbacks);
            Require(oversizedCallbacks == 0, "oversized input does not invoke callback");
            checks.Add("project_byte_limit_enforced_before_callback_and_parse");

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

    private static void ExpectFailureWithoutCallback(string path, string code, ref int callbackCount)
    {
        int observedCallbacks = callbackCount;
        try
        {
            _ = EngaugeDigWholeWorkflowTruthAdapter.Read(
                path,
                "fixture-case",
                () => observedCallbacks++,
                CancellationToken.None);
            throw new InvalidOperationException($"Expected adapter failure '{code}'.");
        }
        catch (EngaugeDigTruthException exception) when (
            exception.Message.StartsWith(code, StringComparison.Ordinal) &&
            code.StartsWith(exception.Code, StringComparison.Ordinal))
        {
            callbackCount = observedCallbacks;
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

    private sealed class MutatesAfterReadStream : FileStream
    {
        private readonly string targetPath;
        private readonly string replacement;

        internal MutatesAfterReadStream(string path, string replacement)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        {
            targetPath = path;
            this.replacement = replacement;
        }

        internal bool Mutated { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            if (read == 0 && !Mutated)
            {
                File.WriteAllText(targetPath, replacement, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Mutated = true;
            }
            return read;
        }
    }

    private sealed class GrowingReadStream(string path)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        private long emitted;

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes + 1 - emitted;
            int read = (int)Math.Min(count, remaining);
            emitted += read;
            return read;
        }
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
