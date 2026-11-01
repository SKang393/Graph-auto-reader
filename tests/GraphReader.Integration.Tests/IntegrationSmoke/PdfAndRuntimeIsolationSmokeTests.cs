// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Runtime;
using GraphReader.Domain;
using GraphReader.Pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class PdfAndRuntimeIsolationSmokeTests
{
    private static readonly Guid PdfSourceId = Guid.Parse("91000000-0000-0000-0000-000000000001");
    private static readonly Guid FigureId = Guid.Parse("92000000-0000-0000-0000-000000000001");
    private static readonly Guid FirstPanelId = Guid.Parse("93000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondPanelId = Guid.Parse("93000000-0000-0000-0000-000000000002");
    private static readonly Guid[] ExpectedPanelIds = [FirstPanelId, SecondPanelId];
    private static readonly string[] ExpectedParticipants = ["Participant A", "Participant B"];

    [TestMethod]
    public async Task RecordedPdfImportCreatesStableOrderedPanelTabsWithoutRenderingOrNetwork()
    {
        var inspector = new RecordedPdfInspector();
        var panelizer = new RecordedPanelizer();
        var service = new PdfImportService(inspector, panelizer);
        var request = new PdfImportRequest(
            Guid.Parse("94000000-0000-0000-0000-000000000001"),
            IntegrationSmokeIds.Project.Value,
            new ImmutableByteBuffer("%PDF-1.7 deterministic-public-fixture"u8),
            "synthetic-public.pdf",
            null,
            new PdfPanelizationOptions());

        PdfImportResult result = await service.ImportAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, Format(result.Failures));
        Assert.AreEqual(1, inspector.CallCount);
        Assert.AreEqual(1, panelizer.CallCount);
        Assert.AreEqual(0, inspector.NetworkAccessAttemptCount);
        Assert.AreEqual(0, panelizer.NetworkAccessAttemptCount);
        CollectionAssert.AreEqual(
            ExpectedPanelIds,
            result.Panels.Select(static panel => panel.PanelId).ToArray());
        CollectionAssert.AreEqual(
            ExpectedParticipants,
            result.Panels.Select(static panel => panel.ParticipantLabel).ToArray());
        Assert.IsTrue(result.Panels.All(static panel => panel.PageNumber == 1));
        Assert.IsTrue(result.Panels.All(static panel => panel.CropInSourcePixels.IsValid));
    }

    [TestMethod]
    public void InstalledModeInitializesOnlyLocalApplicationDataRoots()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("installed app");
        string localRoot = environment.PathFor("LocalAppData");
        Directory.CreateDirectory(executableRoot);
        var access = new RecordingDirectoryAccess();
        var bootstrapper = new RuntimePathBootstrapper(
            new RecordedRuntimeEnvironment(executableRoot, localRoot),
            access);

        DomainResult<IApplicationPaths> initialized = bootstrapper.Initialize();

        Assert.IsTrue(initialized.IsSuccess, Format(initialized.Errors));
        Assert.AreEqual(DistributionMode.Installed, initialized.Value!.Mode);
        Assert.AreEqual(5, access.Paths.Count);
        Assert.IsTrue(access.Paths.All(path => IsUnder(path, Path.Combine(localRoot, "GraphAutoReader"))));
        Assert.IsFalse(access.Paths.Any(path => IsUnder(path, executableRoot)));
    }

    [TestMethod]
    public void PortableModeInitializesOnlyPortableDataRoots()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("portable path 한글");
        string localRoot = environment.PathFor("LocalAppData");
        Directory.CreateDirectory(executableRoot);
        File.WriteAllText(Path.Combine(executableRoot, "portable.mode"), string.Empty);
        var access = new RecordingDirectoryAccess();
        var bootstrapper = new RuntimePathBootstrapper(
            new RecordedRuntimeEnvironment(executableRoot, localRoot),
            access);

        DomainResult<IApplicationPaths> initialized = bootstrapper.Initialize();

        Assert.IsTrue(initialized.IsSuccess, Format(initialized.Errors));
        Assert.AreEqual(DistributionMode.Portable, initialized.Value!.Mode);
        Assert.AreEqual(5, access.Paths.Count);
        Assert.IsTrue(access.Paths.All(path => IsUnder(path, Path.Combine(executableRoot, "Data"))));
        Assert.IsFalse(access.Paths.Any(path => IsUnder(path, localRoot)));
    }

    [TestMethod]
    public void PortableModeInitializesWhenLocalApplicationDataIsUnavailable()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("portable without LocalAppData");
        Directory.CreateDirectory(executableRoot);
        File.WriteAllText(Path.Combine(executableRoot, "portable.mode"), string.Empty);
        var access = new RecordingDirectoryAccess();
        var bootstrapper = new RuntimePathBootstrapper(
            new RecordedRuntimeEnvironment(executableRoot, string.Empty),
            access);

        DomainResult<IApplicationPaths> initialized = bootstrapper.Initialize();

        Assert.IsTrue(initialized.IsSuccess, Format(initialized.Errors));
        Assert.AreEqual(DistributionMode.Portable, initialized.Value!.Mode);
        Assert.AreEqual(5, access.Paths.Count);
        Assert.IsTrue(access.Paths.All(path => IsUnder(path, Path.Combine(executableRoot, "Data"))));
    }

    [TestMethod]
    public void InstalledModeStillRejectsUnavailableLocalApplicationData()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("installed without LocalAppData");
        Directory.CreateDirectory(executableRoot);
        var access = new RecordingDirectoryAccess();
        var bootstrapper = new RuntimePathBootstrapper(
            new RecordedRuntimeEnvironment(executableRoot, string.Empty),
            access);

        DomainResult<IApplicationPaths> initialized = bootstrapper.Initialize();

        Assert.IsFalse(initialized.IsSuccess);
        Assert.AreEqual("APPLICATION_PATHS_UNAVAILABLE", initialized.Errors.Single().Code);
        Assert.AreEqual(0, access.Paths.Count);
    }

    [TestMethod]
    public void DevelopmentDataOverrideIsAcceptedOnlyInPortableMode()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("portable path 한글");
        string localRoot = environment.PathFor("LocalAppData");
        string sharedDataRoot = environment.PathFor("shared preview Data");
        Directory.CreateDirectory(executableRoot);
        File.WriteAllText(Path.Combine(executableRoot, "portable.mode"), string.Empty);
        var access = new RecordingDirectoryAccess();
        var bootstrapper = new RuntimePathBootstrapper(
            new RecordedRuntimeEnvironment(executableRoot, localRoot, sharedDataRoot),
            access);

        DomainResult<IApplicationPaths> initialized = bootstrapper.Initialize();

        Assert.IsTrue(initialized.IsSuccess, Format(initialized.Errors));
        Assert.AreEqual(DistributionMode.Portable, initialized.Value!.Mode);
        Assert.IsTrue(access.Paths.All(path => IsUnder(path, sharedDataRoot)));
        Assert.IsFalse(access.Paths.Any(path => IsUnder(path, executableRoot)));
        Assert.IsFalse(access.Paths.Any(path => IsUnder(path, localRoot)));
    }

    [TestMethod]
    public void DevelopmentDataOverrideIsIgnoredWithoutPortableSentinel()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("installed app");
        string localRoot = environment.PathFor("LocalAppData");
        string rejectedOverride = environment.PathFor("rejected preview Data");
        Directory.CreateDirectory(executableRoot);
        var access = new RecordingDirectoryAccess();
        var bootstrapper = new RuntimePathBootstrapper(
            new RecordedRuntimeEnvironment(executableRoot, localRoot, rejectedOverride),
            access);

        DomainResult<IApplicationPaths> initialized = bootstrapper.Initialize();

        Assert.IsTrue(initialized.IsSuccess, Format(initialized.Errors));
        Assert.AreEqual(DistributionMode.Installed, initialized.Value!.Mode);
        Assert.IsTrue(access.Paths.All(path => IsUnder(path, Path.Combine(localRoot, "GraphAutoReader"))));
        Assert.IsFalse(access.Paths.Any(path => IsUnder(path, rejectedOverride)));
    }

    [TestMethod]
    public void PortableReadOnlyFailureIsStructuredAndActionable()
    {
        using var environment = new IntegrationSmokeTestEnvironment();
        string executableRoot = environment.PathFor("read only portable");
        Directory.CreateDirectory(executableRoot);
        File.WriteAllText(Path.Combine(executableRoot, "portable.mode"), string.Empty);
        var bootstrapper = new RuntimePathBootstrapper(
            new RecordedRuntimeEnvironment(executableRoot, environment.PathFor("LocalAppData")),
            new RecordingDirectoryAccess(throwOnWrite: true));

        DomainResult<IApplicationPaths> initialized = bootstrapper.Initialize();

        Assert.IsFalse(initialized.IsSuccess);
        DomainError error = initialized.Errors.Single();
        Assert.AreEqual("PORTABLE_DATA_NOT_WRITABLE", error.Code);
        Assert.AreEqual("move_portable_installation", error.SuggestedAction);
        Assert.IsTrue(error.Recoverable);
    }

    private static bool IsUnder(string candidate, string root)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string Format(IEnumerable<PdfFailure> failures) =>
        string.Join(" | ", failures.Select(static failure => $"{failure.Code}: {failure.TechnicalMessage}"));

    private static string Format(IEnumerable<DomainError> failures) =>
        string.Join(" | ", failures.Select(static failure => $"{failure.Code}: {failure.TechnicalMessage}"));

    private sealed class RecordedPdfInspector : IPdfDocumentInspector
    {
        public int CallCount { get; private set; }

        public List<string> RequestedRemoteResources { get; } = [];

        public int NetworkAccessAttemptCount => RequestedRemoteResources.Count;

        public Task<PdfInspectionResult> InspectAsync(
            PdfInspectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var page = new PdfPageSnapshot(
                1,
                600,
                800,
                [
                    new PdfTextBlock(Guid.Parse("95000000-0000-0000-0000-000000000001"), "Participant A", new PdfRectD(20, 700, 100, 20), PdfTextRole.ParticipantLabel, 1),
                    new PdfTextBlock(Guid.Parse("95000000-0000-0000-0000-000000000002"), "Participant B", new PdfRectD(20, 300, 100, 20), PdfTextRole.ParticipantLabel, 1),
                ],
                [],
                []);
            var document = new PdfDocumentSnapshot(
                new string('a', 64),
                new PdfDocumentMetadata("Synthetic", null, null, null, null, null),
                [page]);
            return Task.FromResult(new PdfInspectionResult(
                document,
                [],
                new PdfInspectionTiming(1, 2, 3)));
        }
    }

    private sealed class RecordedPanelizer : IPdfPanelizationEngine
    {
        public int CallCount { get; private set; }

        public List<string> RequestedRemoteResources { get; } = [];

        public int NetworkAccessAttemptCount => RequestedRemoteResources.Count;

        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var bounds = new PdfRectD(10, 10, 580, 780);
            var figure = new PdfFigureCandidate(
                FigureId,
                1,
                PdfFigureSourceKind.VectorPageRegion,
                null,
                bounds,
                bounds,
                580,
                780,
                null,
                null,
                "Synthetic panels",
                [new PdfPanelEvidence(PdfPanelEvidenceKind.RepeatedAxes, 1, "recorded")],
                1);
            PdfPanelRecord[] panels =
            [
                Panel(FirstPanelId, "Participant A", 20),
                Panel(SecondPanelId, "Participant B", 410),
            ];
            return Task.FromResult(new PdfPanelizationResult([figure], panels, elapsedMilliseconds: 4));
        }

        public PdfPanelizationResult ApplySplit(PdfPanelizationResult current, PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(PdfPanelizationResult current, PdfManualMergeCommand command) =>
            throw new NotSupportedException();

        private static PdfPanelRecord Panel(Guid id, string participant, double y) =>
            new(
                id,
                FigureId,
                1,
                id == FirstPanelId ? 0 : 1,
                new PdfRectD(0, y, 580, 350),
                new PdfRectD(10, y, 580, 350),
                new PdfRectD(10, y, 580, 350),
                participant,
                "Synthetic panels",
                [],
                [new PdfPanelEvidence(PdfPanelEvidenceKind.ParticipantLabel, 1, participant)],
                1);
    }

    private sealed record RecordedRuntimeEnvironment(
        string ExecutableDirectory,
        string LocalApplicationDataRoot,
        string? DevelopmentPortableDataRoot = null) : IRuntimePathEnvironment;

    private sealed class RecordingDirectoryAccess : IRuntimeDirectoryAccess
    {
        private readonly bool _throwOnWrite;

        public RecordingDirectoryAccess(bool throwOnWrite = false)
        {
            _throwOnWrite = throwOnWrite;
        }

        public List<string> Paths { get; } = [];

        public void EnsureWritable(string directoryPath)
        {
            Paths.Add(Path.GetFullPath(directoryPath));
            if (_throwOnWrite)
            {
                throw new UnauthorizedAccessException("recorded read-only directory");
            }
        }
    }
}
