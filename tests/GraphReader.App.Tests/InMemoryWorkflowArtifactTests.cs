// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.Export;
using GraphReader.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class InMemoryWorkflowArtifactTests
{
    [TestMethod]
    public async Task ImmutableImageSourceUsesProductionImportWithoutCreatingSourceFile()
    {
        using var directory = new TemporaryDirectory();
        byte[] expected = CreatePng(80, 60);
        byte[] callerBytes = (byte[])expected.Clone();
        string hash = Hash(expected);
        string sourceReference = Path.Combine(directory.Path, "never-written.png");
        var source = new WorkflowSourceRequest(Guid.NewGuid(), WorkflowSourceKind.Image, sourceReference)
        {
            InMemoryImageSource = new WorkflowInMemoryImageSource(hash, callerBytes),
        };
        callerBytes[0] ^= 0xff;
        var store = new ProductionWorkflowPanelStore();

        WorkflowImportSnapshot result = await new ProductionWorkflowImportStage(store, new ImageImportService())
            .ImportAsync(new WorkflowImportRequest(Guid.NewGuid(), [source]), CancellationToken.None);

        Assert.IsNotEmpty(result.Panels);
        Assert.IsFalse(File.Exists(sourceReference));
        RasterSourceImageEvidence[] retainedSources = result.Panels
            .Select(panel => store.Get(panel.PanelId).RasterPanelSource?.Source)
            .OfType<RasterSourceImageEvidence>()
            .ToArray();
        Assert.HasCount(result.Panels.Count, retainedSources);
        Assert.IsTrue(retainedSources.All(item => ReferenceEquals(retainedSources[0], item)));
        Assert.AreEqual(sourceReference, retainedSources[0].Image.Reference);
        Assert.AreEqual(hash, retainedSources[0].Image.Sha256);
        CollectionAssert.AreEqual(expected, retainedSources[0].CopyBytes());
        byte[] returnedCopy = retainedSources[0].CopyBytes();
        returnedCopy[1] ^= 0xff;
        CollectionAssert.AreEqual(expected, retainedSources[0].CopyBytes());
    }

    [TestMethod]
    public async Task ImmutableImageSourceRejectsInconsistentBindingAndUnsupportedImporter()
    {
        byte[] png = CreatePng(20, 20);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkflowInMemoryImageSource(new string('0', 64), png));

        var bound = new WorkflowInMemoryImageSource(Hash(png), png);
        Assert.ThrowsExactly<ArgumentException>(() => new WorkflowImportRequest(
            Guid.NewGuid(),
            [new WorkflowSourceRequest(Guid.NewGuid(), WorkflowSourceKind.Pdf, "memory.png")
            {
                InMemoryImageSource = bound,
            }]));

        var importer = new PathOnlyImageImporter();
        var source = new WorkflowSourceRequest(Guid.NewGuid(), WorkflowSourceKind.Image, "memory.png")
        {
            InMemoryImageSource = bound,
        };
        ProductionWorkflowStageException exception = await Assert.ThrowsExactlyAsync<ProductionWorkflowStageException>(
            () => new ProductionWorkflowImportStage(new ProductionWorkflowPanelStore(), importer)
                .ImportAsync(new WorkflowImportRequest(Guid.NewGuid(), [source]), CancellationToken.None));

        Assert.AreEqual(ProductionWorkflowFailureCodes.ImageImportFailed, exception.Failure.Code);
        Assert.AreEqual(0, importer.PathImportCalls);
    }

    [TestMethod]
    public async Task PreviewExportReturnsImmutableSerializedBytesWithoutCreatingDirectory()
    {
        using var directory = new TemporaryDirectory();
        ExportFixture fixture = CreateExportFixture();
        var stage = new ProductionWorkflowExportStage(fixture.Store, new ExportService());
        string previewDirectory = Path.Combine(directory.Path, "preview-must-not-exist");

        WorkflowExportResult preview = await stage.ExportAsync(
            fixture.Review,
            new WorkflowExportRequest(Guid.NewGuid(), previewDirectory)
            {
                Operation = ExportOperation.Preview,
            },
            CancellationToken.None);

        Assert.IsTrue(preview.Succeeded, string.Join(" | ", preview.Warnings));
        Assert.IsFalse(Directory.Exists(previewDirectory));
        Assert.IsNotEmpty(preview.Artifacts);
        Assert.IsTrue(preview.Artifacts.All(static artifact => artifact.WrittenPath is null));
        Assert.IsTrue(preview.Artifacts.All(static artifact => artifact.HasInMemoryContent));
        WorkflowExportArtifact minimal = preview.Artifacts.Single(static artifact =>
            artifact.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !artifact.FileName.Contains("audit", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("participant_Intervention.csv", minimal.FileName);
        CollectionAssert.AreEqual(
            "x_value,y_value,phase\n1,42,b\n"u8.ToArray(),
            minimal.CopyContentBytes());
        Assert.AreEqual(Hash(minimal.CopyContentBytes()), minimal.Sha256);
        byte[] changedCopy = minimal.CopyContentBytes();
        changedCopy[0] ^= 0xff;
        CollectionAssert.AreEqual(
            "x_value,y_value,phase\n1,42,b\n"u8.ToArray(),
            minimal.CopyContentBytes());
    }

    [TestMethod]
    public async Task PreviewBytesMatchDefaultFileExportAndDefaultArtifactsRemainFileBacked()
    {
        using var directory = new TemporaryDirectory();
        ExportFixture fixture = CreateExportFixture();
        var stage = new ProductionWorkflowExportStage(fixture.Store, new ExportService());
        WorkflowExportResult preview = await stage.ExportAsync(
            fixture.Review,
            new WorkflowExportRequest(Guid.NewGuid(), "unused") { Operation = ExportOperation.Preview },
            CancellationToken.None);
        string outputDirectory = Path.Combine(directory.Path, "written");

        WorkflowExportResult written = await stage.ExportAsync(
            fixture.Review,
            new WorkflowExportRequest(Guid.NewGuid(), outputDirectory),
            CancellationToken.None);

        Assert.IsTrue(preview.Succeeded);
        Assert.IsTrue(written.Succeeded, string.Join(" | ", written.Warnings));
        Assert.AreEqual(preview.Artifacts.Count, written.Artifacts.Count);
        foreach (WorkflowExportArtifact previewArtifact in preview.Artifacts)
        {
            WorkflowExportArtifact writtenArtifact = written.Artifacts.Single(
                artifact => string.Equals(artifact.FileName, previewArtifact.FileName, StringComparison.Ordinal));
            Assert.IsNotNull(writtenArtifact.WrittenPath);
            Assert.IsTrue(File.Exists(writtenArtifact.WrittenPath));
            Assert.IsFalse(writtenArtifact.HasInMemoryContent);
            CollectionAssert.AreEqual(
                previewArtifact.CopyContentBytes(),
                await File.ReadAllBytesAsync(writtenArtifact.WrittenPath));
            Assert.AreEqual(previewArtifact.Sha256, writtenArtifact.Sha256);
            Assert.AreEqual(previewArtifact.RowCount, writtenArtifact.RowCount);
        }
    }

    [TestMethod]
    public async Task MultiPanelExportKeepsRepeatedNamesDistinctAndPreviewMatchesWrittenBytes()
    {
        using var directory = new TemporaryDirectory();
        ExportFixture fixture = CreateMultiPanelExportFixture();
        var stage = new ProductionWorkflowExportStage(fixture.Store, new ExportService());
        var previewRequest = new WorkflowExportRequest(Guid.NewGuid(), "unused") { Operation = ExportOperation.Preview };
        WorkflowExportResult preview = await stage.ExportAsync(fixture.Review, previewRequest, CancellationToken.None);
        var reversed = new WorkflowReviewState(fixture.Review.ProjectId, fixture.Review.Panels.Reverse());
        WorkflowExportResult reordered = await stage.ExportAsync(reversed, previewRequest, CancellationToken.None);
        WorkflowExportResult written = await stage.ExportAsync(
            fixture.Review,
            new WorkflowExportRequest(previewRequest.RunId, Path.Combine(directory.Path, "written")),
            CancellationToken.None);

        Assert.IsTrue(preview.Succeeded, string.Join(" | ", preview.Warnings));
        Assert.IsTrue(reordered.Succeeded, string.Join(" | ", reordered.Warnings));
        Assert.IsTrue(written.Succeeded, string.Join(" | ", written.Warnings));
        Assert.HasCount(6, preview.Artifacts);
        Assert.AreEqual(preview.Artifacts.Count, preview.Artifacts.Select(static a => a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        CollectionAssert.AreEqual(
            "x_value,y_value,phase\n1,42,b\n"u8.ToArray(),
            preview.Artifacts.Single(static a => a.FileName == "panel-001_participant_Intervention.csv").CopyContentBytes());
        CollectionAssert.AreEqual(
            "x_value,y_value,phase\n1,43,b\n"u8.ToArray(),
            preview.Artifacts.Single(static a => a.FileName == "panel-002_participant_Intervention.csv").CopyContentBytes());
        foreach (WorkflowExportArtifact artifact in preview.Artifacts)
        {
            WorkflowExportArtifact reorderedArtifact = reordered.Artifacts.Single(a => a.FileName == artifact.FileName);
            WorkflowExportArtifact writtenArtifact = written.Artifacts.Single(a => a.FileName == artifact.FileName);
            Assert.IsNotNull(writtenArtifact.WrittenPath);
            CollectionAssert.AreEqual(artifact.CopyContentBytes(), reorderedArtifact.CopyContentBytes());
            CollectionAssert.AreEqual(artifact.CopyContentBytes(), await File.ReadAllBytesAsync(writtenArtifact.WrittenPath));
            Assert.AreEqual(artifact.Sha256, writtenArtifact.Sha256);
        }
    }

    [TestMethod]
    public async Task MultiPanelExportDoesNotOverwriteAnExistingDestination()
    {
        using var directory = new TemporaryDirectory();
        ExportFixture fixture = CreateMultiPanelExportFixture();
        string protectedPath = Path.Combine(directory.Path, "panel-001_participant_Intervention.csv");
        byte[] sentinel = "Existing user data"u8.ToArray();
        await File.WriteAllBytesAsync(protectedPath, sentinel);

        WorkflowExportResult result = await new ProductionWorkflowExportStage(fixture.Store, new ExportService())
            .ExportAsync(fixture.Review, new WorkflowExportRequest(Guid.NewGuid(), directory.Path), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("EXPORT_FILE_EXISTS", result.FailureCode);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(protectedPath));
        Assert.HasCount(1, Directory.GetFiles(directory.Path));
    }

    private static ExportFixture CreateMultiPanelExportFixture()
    {
        Guid projectId = Guid.NewGuid();
        ExportFixture first = CreateExportFixture(projectId, "Panel A", 42, ExportAuditMode.ExtendedCsvAndJson);
        ExportFixture second = CreateExportFixture(projectId, "Panel B", 43, ExportAuditMode.ExtendedCsvAndJson);
        first.Store.Register(second.Store.Get(second.Review.Panels.Single().PanelId));
        return new ExportFixture(first.Store, new WorkflowReviewState(projectId, first.Review.Panels.Concat(second.Review.Panels)));
    }

    private static ExportFixture CreateExportFixture(
        Guid? sharedProjectId = null,
        string displayName = "memory.png",
        double graphY = 42,
        ExportAuditMode auditMode = ExportAuditMode.ExtendedCsv)
    {
        Guid projectId = sharedProjectId ?? Guid.NewGuid();
        Guid panelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid pointId = Guid.NewGuid();
        Guid seriesId = Guid.NewGuid();
        Guid phaseId = Guid.NewGuid();
        byte[] sourceBytes = [4, 5, 6];
        string sourceHash = Hash(sourceBytes);
        var image = new WorkflowImageEvidence(
            "memory.png",
            sourceHash,
            50,
            80,
            WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(panelId, sourceId, displayName, image);
        var provenance = new WorkflowVisionEnvelope(
            1,
            Guid.NewGuid(),
            projectId,
            panelId,
            "markers",
            "fixture-v1",
            sourceHash,
            null,
            new WorkflowVisionTiming(0, 0, 0, 0),
            1);
        var evidence = new ProductionPanelExportEvidence(
            new ExportCalibration(
                ExportCalibrationStatus.Valid,
                hasYCalibration: true,
                hasPrintedSessionCalibration: true,
                hasAbsoluteSessionOrigin: true,
                firstObservedSession: 1,
                confidence: 1),
            [new ExportPhase(phaseId, 1, "b", ExportPhaseType.Intervention, null, 0, 50, 1)],
            [new ExportSeries(seriesId, "●", "Intervention", ExportSeriesRole.Intervention, [pointId], 1)],
            [new ExportSeriesRelation(seriesId, sharedBaselineSeriesId: null)],
            [new ProductionPointExportEvidence(
                pointId,
                MarkerId: null,
                ObservationIndex: 1,
                PrintedXValue: 1,
                EstimatedXValue: null,
                XSource: ExportXValueSource.Printed,
                XConfidence: 1,
                YConfidence: 1)],
            [provenance],
            auditMode: auditMode);
        var store = new ProductionWorkflowPanelStore();
        store.Register(new ProductionPanelEvidence(
            panel,
            WorkflowSourceKind.Image,
            sourceBytes,
            exportEvidence: evidence));
        var point = new WorkflowPoint(
            pointId.ToString("D"),
            "point-1",
            20,
            30,
            1,
            WorkflowImageVariant.Original,
            WorkflowReviewStatus.Accepted,
            "●",
            "circle",
            "filled",
            seriesId.ToString("D"),
            phaseId.ToString("D"),
            1,
            graphY,
            "markers",
            null,
            false);
        var review = new WorkflowReviewState(
            projectId,
            [new WorkflowReviewPanel(new WorkflowPreparedPanel(panel, image, null), [point], [provenance])]);
        return new ExportFixture(store, review);
    }

    private static byte[] CreatePng(int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, checked(width * height * 4)).ToArray();
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            checked(width * 4));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record ExportFixture(ProductionWorkflowPanelStore Store, WorkflowReviewState Review);

    private sealed class PathOnlyImageImporter : IImageImportService
    {
        public int PathImportCalls { get; private set; }

        public Task<ImageImportResult> ImportAsync(string path, CancellationToken cancellationToken)
        {
            PathImportCalls++;
            throw new AssertFailedException("The source path must not be read for an in-memory import.");
        }

        public Task<BatchImportResult> ImportBatchAsync(
            IEnumerable<string> paths,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "GraphReader.App.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
