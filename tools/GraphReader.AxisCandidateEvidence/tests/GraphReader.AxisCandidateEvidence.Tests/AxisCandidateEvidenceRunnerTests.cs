// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Axis;
using GraphReader.AxisCandidateEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace GraphReader.AxisCandidateEvidence.Tests;

[TestClass]
public sealed class AxisCandidateEvidenceRunnerTests
{
    [TestMethod]
    public async Task SuccessReportPreservesRankedSnapshotShapeAndSourceIdentity()
    {
        using var fixture = new SyntheticFixture();
        AxisCandidateEvidenceReport report = await fixture.CreateRunner(new StaticProvider(FixtureCandidates()))
            .RunAsync();

        Assert.AreEqual("success", report.Status,
            $"Failure {report.Failure?.Code}: {report.Failure?.ExceptionType}: {report.Failure?.Message}");
        Assert.IsNull(report.Failure);
        Assert.AreEqual(fixture.SourceId, report.Source.SourceId);
        Assert.AreEqual(fixture.PanelId, report.Source.PanelId);
        Assert.AreEqual(fixture.ImageSha256, report.Source.SourceSha256);
        Assert.AreEqual(fixture.ImageSha256, report.Source.PanelSha256);
        Assert.AreEqual(800, report.Source.Width);
        Assert.AreEqual(400, report.Source.Height);
        Assert.AreEqual(AxisGeometryCoordinateSpaces.OriginalPixels, report.Source.CoordinateSpace);
        Assert.IsNotNull(report.Runtime);
        Assert.IsTrue(report.CandidateSnapshots.Count >= 2);
        Assert.AreEqual("graphreader.axis-candidate-evidence.v2", report.Schema);
        Assert.IsNotNull(report.NormalizedFeatures);
        Assert.AreEqual(17, report.NormalizedFeatures.FeatureNames.Count);
        Assert.AreEqual(report.CandidateSnapshots.Count, report.NormalizedFeatures.Rows.Count);

        for (int index = 0; index < report.CandidateSnapshots.Count; index++)
        {
            AxisGeometryCandidateSnapshot candidate = report.CandidateSnapshots[index];
            Assert.AreEqual(index, candidate.Rank);
            Assert.IsTrue(double.IsFinite(candidate.Score));
            Assert.IsTrue(index == 0 || report.CandidateSnapshots[index - 1].Score >= candidate.Score);
            Assert.IsNotEmpty(candidate.XSupportingCandidateIds);
            Assert.IsNotEmpty(candidate.YSupportingCandidateIds);
        }

        AxisGeometryCandidateSnapshot first = report.CandidateSnapshots[0];
        JsonElement root = JsonSerializer.SerializeToElement(report);
        JsonElement snapshots = root.GetProperty("candidate_snapshots");
        JsonElement firstJson = snapshots[0];
        foreach (string property in new[]
        {
            "Rank", "Score", "Intersection", "XAxisLine", "YAxisLine", "BottomRight", "TopRight", "TopLeft",
            "XSpan", "YSpan", "XCoverageFraction", "YCoverageFraction", "XRootMeanSquareError",
            "YRootMeanSquareError", "XSupportingCandidateIds", "YSupportingCandidateIds",
        })
        {
            Assert.IsTrue(firstJson.TryGetProperty(property, out _), $"Missing snapshot property {property}.");
        }

        Assert.AreEqual(first.Rank, firstJson.GetProperty("Rank").GetInt32());
        Assert.AreEqual(first.Score, firstJson.GetProperty("Score").GetDouble(), 0d);

        AxisCandidateEvidenceFeatureRow firstFeatures = report.NormalizedFeatures.Rows[0];
        Assert.AreEqual(1, firstFeatures.Rank);
        Assert.AreEqual(first.Score, firstFeatures.Features[0], 0d);
        Assert.AreEqual(first.Intersection.X / report.Source.Width, firstFeatures.Features[1], 0d);
        Assert.AreEqual(first.Intersection.Y / report.Source.Height, firstFeatures.Features[2], 0d);
        Assert.AreEqual(first.XSpan / report.Source.Width, firstFeatures.Features[9], 0d);
        Assert.AreEqual(first.YSpan / report.Source.Height, firstFeatures.Features[10], 0d);
        Assert.AreEqual(first.XSupportingCandidateIds.Count, firstFeatures.Features[15], 0d);
        Assert.AreEqual(first.YSupportingCandidateIds.Count, firstFeatures.Features[16], 0d);
    }

    [TestMethod]
    public async Task EmptyDetectorOutputProducesBoundFailureRecord()
    {
        using var fixture = new SyntheticFixture();
        AxisCandidateEvidenceReport report = await fixture.CreateRunner(new StaticProvider([])).RunAsync();

        Assert.AreEqual("failed", report.Status);
        Assert.IsNotNull(report.Failure);
        Assert.AreEqual("axis_geometry_failure", report.Failure.Kind);
        Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", report.Failure.Code);
        Assert.IsEmpty(report.CandidateSnapshots);
        Assert.IsNull(report.NormalizedFeatures);
        Assert.AreEqual(fixture.SourceId, report.Source.SourceId);
        Assert.AreEqual(fixture.PanelId, report.Source.PanelId);
    }

    [TestMethod]
    public async Task PreCanceledRequestProducesCancellationRecordWithoutCandidates()
    {
        using var fixture = new SyntheticFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AxisCandidateEvidenceReport report = await fixture.CreateRunner(new StaticProvider(FixtureCandidates()))
            .RunAsync(cancellation.Token);

        Assert.AreEqual("cancelled", report.Status);
        Assert.IsNotNull(report.Failure);
        Assert.AreEqual("REQUEST_CANCELLED", report.Failure.Code);
        Assert.IsEmpty(report.CandidateSnapshots);
    }

    [TestMethod]
    public async Task ImageHashMismatchFailsBeforeDetectorAndKeepsBoundIdentity()
    {
        using var fixture = new SyntheticFixture();
        AxisCandidateEvidenceInputBinding binding = fixture.Binding with
        {
            SourceImageSha256 = new string('0', 64),
        };
        var provider = new StaticProvider(FixtureCandidates());

        AxisCandidateEvidenceReport report = await fixture.CreateRunner(provider, binding).RunAsync();

        Assert.AreEqual("failed", report.Status);
        Assert.AreEqual("SOURCE_IMAGE_HASH_MISMATCH", report.Failure?.Code);
        Assert.AreEqual(fixture.SourceId, report.Source.SourceId);
        Assert.AreEqual(fixture.PanelId, report.Source.PanelId);
        Assert.AreEqual(0, provider.CallCount);
        Assert.IsEmpty(report.CandidateSnapshots);
    }

    [TestMethod]
    public async Task ReportWriterIsProjectBoundAndNeverOverwrites()
    {
        using var fixture = new SyntheticFixture();
        AxisCandidateEvidenceReport report = await fixture.CreateRunner(new StaticProvider(FixtureCandidates()))
            .RunAsync();
        string relativeOutput = $"artifacts/goal22-runs/axis-candidate-evidence-tests/{fixture.Id}/report.json";

        string digest = AxisCandidateEvidenceReportWriter.WriteNewReport(fixture.ProjectRoot, relativeOutput, report);
        string output = Path.Combine(fixture.ProjectRoot, relativeOutput.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual(Hash(File.ReadAllBytes(output)), digest);
        Assert.ThrowsExactly<IOException>(() =>
            AxisCandidateEvidenceReportWriter.WriteNewReport(fixture.ProjectRoot, relativeOutput, report));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            AxisCandidateEvidenceReportWriter.WriteNewReport(fixture.ProjectRoot, "outside/report.json", report));
    }

    private static IReadOnlyList<GeometryLineCandidate> FixtureCandidates() =>
    [
        new("x-main", new GeometryLineSegment(new PixelPoint(100, 300), new PixelPoint(700, 300)), LineCandidateSource.RecordedFixture),
        new("y-main", new GeometryLineSegment(new PixelPoint(100, 300), new PixelPoint(100, 50)), LineCandidateSource.RecordedFixture),
        new("x-alt", new GeometryLineSegment(new PixelPoint(180, 310), new PixelPoint(680, 310)), LineCandidateSource.RecordedFixture),
        new("y-alt", new GeometryLineSegment(new PixelPoint(180, 310), new PixelPoint(180, 70)), LineCandidateSource.RecordedFixture),
    ];

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class StaticProvider(IReadOnlyList<GeometryLineCandidate> candidates) : ILineCandidateProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<GeometryLineCandidate>> DetectLinesAsync(
            GrayscaleLineCandidateFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(candidates);
        }
    }

    private sealed class SyntheticFixture : IDisposable
    {
        private static readonly string[] SourceFiles =
        [
            "src/GraphReader.Axis/AxisGeometryContracts.cs",
            "src/GraphReader.Axis/AxisGeometryDetector.cs",
            "src/GraphReader.Axis/OpenCvLineCandidateProvider.cs",
            "src/GraphReader.Axis/GraphReader.Axis.csproj",
            "Directory.Packages.props",
        ];

        private readonly string _directory;

        public SyntheticFixture()
        {
            ProjectRoot = FindProjectRoot(Environment.CurrentDirectory);
            Id = Guid.NewGuid().ToString("N");
            _directory = Path.Combine(ProjectRoot, "artifacts", "goal22-runs", "axis-candidate-evidence-tests", Id);
            Directory.CreateDirectory(_directory);
            SourceId = "a6d214e3-29db-4472-a40c-a06f30acb59d";
            PanelId = "02754013-ef39-46a6-8cc5-a5fe3c03a50f";

            string manifestPath = Path.Combine(_directory, "manifest.json");
            byte[] manifest = "synthetic test manifest without annotations"u8.ToArray();
            File.WriteAllBytes(manifestPath, manifest);
            SourceManifestSha256 = Hash(manifest);

            using var image = new Mat(400, 800, MatType.CV_8UC3, Scalar.All(255));
            Cv2.Line(image, new Point(100, 300), new Point(700, 300), Scalar.Black, 2);
            Cv2.Line(image, new Point(100, 300), new Point(100, 50), Scalar.Black, 2);
            Cv2.Line(image, new Point(180, 310), new Point(680, 310), Scalar.Black, 2);
            Cv2.Line(image, new Point(180, 310), new Point(180, 70), Scalar.Black, 2);
            Cv2.ImEncode(".png", image, out byte[] png);
            string imagePath = Path.Combine(_directory, "source.png");
            File.WriteAllBytes(imagePath, png);
            ImageSha256 = Hash(png);

            Binding = new AxisCandidateEvidenceInputBinding(
                "graphreader.axis-candidate-evidence.input.v1",
                "synthetic_test_fixture",
                Relative(manifestPath),
                SourceManifestSha256,
                "test-fixture",
                SourceId,
                PanelId,
                Relative(imagePath),
                ImageSha256,
                png.LongLength,
                ImageSha256,
                AxisGeometryCoordinateSpaces.OriginalPixels,
                new OpenCvLineCandidateOptions(),
                new AxisGeometryOptions(),
                SourceFiles.ToDictionary(
                    path => path,
                    path => Hash(File.ReadAllBytes(Path.Combine(ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar)))),
                    StringComparer.Ordinal),
                ["Generated test pixels only; no labels or ground truth are consumed.", "Test-only data.", "Not an accuracy result."]);
        }

        public string ProjectRoot { get; }

        public string Id { get; }

        public string SourceId { get; }

        public string PanelId { get; }

        public string SourceManifestSha256 { get; }

        public string ImageSha256 { get; }

        public AxisCandidateEvidenceInputBinding Binding { get; }

        public AxisCandidateEvidenceRunner CreateRunner(
            ILineCandidateProvider provider,
            AxisCandidateEvidenceInputBinding? binding = null) =>
            new(ProjectRoot, binding ?? Binding, provider);

        public void Dispose()
        {
            string expectedParent = Path.Combine(ProjectRoot, "artifacts", "goal22-runs", "axis-candidate-evidence-tests");
            if (!_directory.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Test cleanup path escaped its dedicated project output directory.");
            }

            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private string Relative(string path) => Path.GetRelativePath(ProjectRoot, path).Replace('\\', '/');

        private static string FindProjectRoot(string start)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                    File.Exists(Path.Combine(directory.FullName, "src", "GraphReader.Axis", "GraphReader.Axis.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate Graph Auto Reader root for the test fixture.");
        }
    }
}
