using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.ManualTrainOnlyCapture;

internal static class TrainOnlySourceCaptureSelfTest
{
    public static object Run()
    {
        Require(TrainOnlySourceCapture.ExpectedAssignmentSha256 == FrozenRealCorpusInventory.FrozenAssignmentSha256 &&
                TrainOnlySourceCapture.ExpectedAssignmentSha256.Length == 64,
            "assignment_digest_uses_frozen_inventory_constant");
        Program.ValidateExecutionBindingForTest(@"E:\Graph-auto-reader", typeof(Program).Assembly.Location);
        ExpectFailure(() => Program.ValidateExecutionBindingForTest(@"C:\Graph-auto-reader", typeof(Program).Assembly.Location), "authorized E");
        ExpectFailure(() => Program.ValidateExecutionBindingForTest(@"E:\Graph-auto-reader", @"C:\other\tool.dll"), "escapes");
        string trainStudy = FindStudy(EngaugeDevStudyPartition.Train);
        string devStudy = FindStudy(EngaugeDevStudyPartition.Development);
        var projects = new (string CaseId, string StudyId, string Split)[]
        {
            (new string('a', 64), trainStudy, FrozenRealCorpusInventory.RealDev),
            (new string('b', 64), devStudy, FrozenRealCorpusInventory.RealDev),
            (new string('c', 64), trainStudy, FrozenRealCorpusInventory.RealDev),
        };
        var opened = new List<string>();
        TrainOnlySourceRow ReadTrain(string caseId)
        {
            opened.Add(caseId);
            return new TrainOnlySourceRow(caseId, trainStudy, new string('1', 64),
                new string('2', 64), 32, 24, "synthetic curve-point center; marker-glyph-correspondence-unverified; no-negative-labels", 1, [new TrainOnlyPoint(6, 10)]);
        }

        TrainOnlyCaptureResult result = TrainOnlySourceCapture.CaptureForTest(projects, ReadTrain);
        Require(result.selectedRealDevProjectCount == 3 && result.trainProjectCount == 2 &&
                result.developmentProjectCount == 1 && result.trainProjectReadCount == 2 &&
                result.developmentProjectReadCount == 0 && result.rows.Count == 2 &&
                result.rows[0].caseId == projects[0].CaseId && result.rows[1].caseId == projects[2].CaseId &&
                result.duplicateEmbeddedImageGroups == 1 && result.duplicateEmbeddedImageRows == 2 &&
                opened.SequenceEqual([projects[0].CaseId, projects[2].CaseId]),
            "train_only_reader_gate");
        Require(result.rows[0].labelingScope.Contains("unverified", StringComparison.Ordinal) &&
                result.rows[0].labelingScope.Contains("no-negative-labels", StringComparison.Ordinal),
            "limited_positive_label_claim");

        ExpectFailure(() => TrainOnlySourceCapture.CaptureForTest(
            [projects[0], projects[0]], _ => throw new InvalidOperationException("reader must not run")),
            "duplicate");
        var sealedProject = (new string('c', 64), new string('d', 64), "real-sealed");
        ExpectFailure(() => TrainOnlySourceCapture.CaptureForTest(
            [projects[0], sealedProject], _ => throw new InvalidOperationException("reader must not run")),
            "split");
        ExpectFailure(() => TrainOnlySourceCapture.CaptureForTest(projects, _ => throw new InvalidOperationException("reader must not run"), new string('0', 64)),
            "assignment");
        VerifyOversizedStreamRejectedBeforeRead();
        VerifyCancelledHashStopsBeforeRead();
        VerifyLockedSyntheticTrainProject(trainStudy, devStudy);
        _ = Program.VerifyAttemptJournalSynthetic(@"E:\Graph-auto-reader");

        return new
        {
            status = "pass",
            scope = "synthetic-metadata-only",
            development_projects_opened = 0,
            sealed_projects_opened = 0,
            model_runs = 0,
            optimizer_steps = 0,
            negative_labels = 0,
            training_authorized = false,
        };
    }

    private static string FindStudy(EngaugeDevStudyPartition partition)
    {
        for (int value = 1; value < 10000; value++)
        {
            string id = value.ToString("x64", CultureInfo.InvariantCulture);
            if (EngaugeDevSourceMaterializer.PartitionForStudy(id) == partition)
                return id;
        }
        throw new InvalidOperationException("SYNTHETIC_STUDY_NOT_FOUND");
    }

    private static void VerifyOversizedStreamRejectedBeforeRead()
    {
        var stream = new NonReadingStream(EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes + 1);
        ExpectFailure(() => TrainOnlySourceCapture.ValidateTrainProjectLength(stream), "length");
        Require(stream.ReadCount == 0, "oversized_rejected_before_hash_read");
    }

    private static void VerifyCancelledHashStopsBeforeRead()
    {
        var stream = new NonReadingStream(4);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = TrainOnlySourceCapture.HashStreamForTest(stream, cancellation.Token);
            throw new InvalidOperationException("TRAIN_ONLY_SELF_TEST_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        Require(stream.ReadCount == 0, "cancelled_hash_does_not_read");
    }

    private static void VerifyLockedSyntheticTrainProject(string trainStudy, string developmentStudy)
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAYCAAAAAC+OKDoAAAAG0lEQVR4nGP8z4AfMBGQZxhVAAGj4QABwyMcAGFuAS8iYygSAAAAAElFTkSuQmCC";
        const string axes = "<Axes>" +
            "<Point><PositionScreen X=\"2\" Y=\"2\"/><PositionGraph X=\"1\" Y=\"100\"/></Point>" +
            "<Point><PositionScreen X=\"2\" Y=\"20\"/><PositionGraph X=\"1\" Y=\"0\"/></Point>" +
            "<Point><PositionScreen X=\"25\" Y=\"20\"/><PositionGraph X=\"8\" Y=\"0\"/></Point></Axes>";
        string projectRoot = Path.GetFullPath(Environment.CurrentDirectory);
        if (!File.Exists(Path.Combine(projectRoot, "CODEX_START_HERE.md")) || !Directory.Exists(Path.Combine(projectRoot, "artifacts")))
            throw new InvalidOperationException("TRAIN_ONLY_SELF_TEST_PROJECT_ROOT_REQUIRED");
        string testRoot = Path.Combine(projectRoot, "artifacts", "test-temp");
        Directory.CreateDirectory(testRoot);
        string directory = Path.Combine(testRoot, "manual-train-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string trainPath = Path.Combine(directory, "train.dig");
        string developmentPath = Path.Combine(directory, "development-must-not-be-opened.dig");
        string caseId = new('a', 64);
        FrozenRealCorpusProject[] selected =
        [
            new(trainPath, caseId, trainStudy, FrozenRealCorpusInventory.RealDev),
            new(developmentPath, new string('b', 64), developmentStudy, FrozenRealCorpusInventory.RealDev),
        ];
        var selection = new FrozenRealCorpusSelection(FrozenRealCorpusInventory.RealDev, 2, 2, 0,
            FrozenRealCorpusInventory.FrozenAssignmentSha256, new string('2', 64), selected);
        FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.CreateForTest(_ => selection, CancellationToken.None);
        try
        {
            string document = "<Document><SourceImage Format=\"PNG\">" + png + "</SourceImage>" + axes +
                "<Curve Name=\"synthetic\"><Point><PositionScreen X=\"6\" Y=\"10\"/></Point></Curve></Document>";
            File.WriteAllText(trainPath, document, new UTF8Encoding(false));
            FrozenRealCorpusProject trainProject = selector.RequireProject(trainPath, caseId, trainStudy, CancellationToken.None);
            ExpectFailure(() => TrainOnlySourceCapture.ReadTrainProjectForTest(selector, trainProject, Path.Combine(directory, "wrong-root"), CancellationToken.None), "escaped");
            TrainOnlySourceRow row = TrainOnlySourceCapture.ReadTrainProjectForTest(selector, trainProject, projectRoot, CancellationToken.None);
            byte[] projectBytes = File.ReadAllBytes(trainPath);
            Require(row.caseId == caseId && row.curvePointCount == 1 &&
                    row.curvePoints.SequenceEqual([new TrainOnlyPoint(6, 10)]) &&
                    row.projectSha256 == Convert.ToHexStringLower(SHA256.HashData(projectBytes)) &&
                    row.embeddedImageSha256 == Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(png))) &&
                    row.imageWidth == 32 && row.imageHeight == 24 && !File.Exists(developmentPath),
                "same_handle_synthetic_train_parse_and_development_not_opened");
            using var exclusive = new FileStream(trainPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            if (File.Exists(trainPath)) File.Delete(trainPath);
            if (File.Exists(developmentPath)) File.Delete(developmentPath);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static void ExpectFailure(Action action, string expected)
    {
        try { action(); }
        catch (InvalidDataException exception) when (exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"TRAIN_ONLY_SELF_TEST_EXPECTED_FAILURE:{expected}");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"TRAIN_ONLY_SELF_TEST_FAILED:{name}");
    }

    private sealed class NonReadingStream(long length) : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Synthetic guard stream must not be read.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
