// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App;
using GraphReader.App.Controls;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Export;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

/// <summary>
/// Opt-in local validation of the manual portable workflow with a real graph.
/// The source image is supplied only through an environment variable and is never
/// copied into the repository or a tracked test fixture. This test does not measure
/// or claim automatic detection accuracy.
/// </summary>
[TestClass]
public sealed class RealGraphManualPortableValidationTests
{
    private const string SourcePathEnvironmentVariable = "GRAPHREADER_REAL_GRAPH_PATH";
    private const string EvidenceRootEnvironmentVariable = "GRAPHREADER_REAL_GRAPH_EVIDENCE_ROOT";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    [TestCategory("PrivateRealGraph")]
    public void OptInRealGraphCompletesManualPortableWorkflowAndRendersWpfEvidence()
    {
        string? configuredSourcePath = Environment.GetEnvironmentVariable(SourcePathEnvironmentVariable);
        string? configuredEvidenceRoot = Environment.GetEnvironmentVariable(EvidenceRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredSourcePath) || string.IsNullOrWhiteSpace(configuredEvidenceRoot))
        {
            Assert.Inconclusive(
                $"Set both {SourcePathEnvironmentVariable} and {EvidenceRootEnvironmentVariable} to run the private real-graph validation.");
            return;
        }

        string sourcePath = Path.GetFullPath(configuredSourcePath);
        string evidenceRoot = Path.GetFullPath(configuredEvidenceRoot);
        Assert.IsTrue(File.Exists(sourcePath), $"The configured real graph does not exist: {sourcePath}");

        string runRoot = Path.Combine(
            evidenceRoot,
            $"run-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));
                Task scenario = VerifyRealGraphAsync(sourcePath, runRoot);
                _ = scenario.ContinueWith(
                    _ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                Dispatcher.Run();
                scenario.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "RealGraphManualPortableValidationTests.STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(
            thread.Join(TimeSpan.FromSeconds(60)),
            "The opt-in real-graph WPF validation timed out after 60 seconds.");
        if (failure is not null)
        {
            if (failure is AssertInconclusiveException inconclusive)
            {
                Assert.Inconclusive(inconclusive.Message);
                return;
            }

            throw new AssertFailedException(
                $"The opt-in real-graph WPF validation failed. Evidence root: {runRoot}. Failure: {failure}",
                failure);
        }
    }

    private static async Task VerifyRealGraphAsync(string sourcePath, string runRoot)
    {
        string sourceHashBefore = Sha256(sourcePath);
        string runtimeRoot = Path.Combine(runRoot, "portable-runtime");
        string localApplicationDataDecoy = Path.Combine(runRoot, "local-app-data-must-not-be-used");
        string portableDataRoot = Path.Combine(runtimeRoot, "Data");
        string projectPath = Path.Combine(runRoot, "chandler-manual.garproj");
        string recoveredProjectPath = Path.Combine(runRoot, "chandler-manual-recovered.garproj");
        string exportRoot = Path.Combine(runRoot, "export");
        string screenshotPath = Path.Combine(runRoot, "manual-workflow.png");
        string reportPath = Path.Combine(runRoot, "real-graph-manual-validation.json");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(exportRoot);
        await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "portable.mode"), string.Empty);

        ApplicationPaths paths = ApplicationPaths.Create(runtimeRoot, localApplicationDataDecoy);
        Assert.AreEqual(DistributionMode.Portable, paths.Mode);
        StringAssert.StartsWith(paths.AutosaveRoot, portableDataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(Directory.Exists(localApplicationDataDecoy));

        ApplicationCompositionResult composition = await ApplicationComposition.CreateAsync(
            WorkflowRuntimeEnvironment.ManualPreview,
            paths);
        Assert.AreEqual(WorkflowRuntimeEnvironment.ManualPreview, composition.Environment);
        Assert.IsNull(composition.StartupError);
        var workspace = Assert.IsInstanceOfType<ManualPreviewWorkspaceService>(composition.WorkspaceService);
        Assert.AreEqual(WorkflowRuntimeEnvironment.ManualPreview, workspace.RuntimeEnvironment);
        Assert.IsFalse(workspace.UsesFakeGraphData);

        var dialogs = new RealGraphDialogService
        {
            Images = [sourcePath],
            ProjectToOpen = projectPath,
            ProjectSaveDestinations = [projectPath, recoveredProjectPath],
            ExportDirectory = exportRoot,
        };
        using var viewModel = new MainWindowViewModel(
            workspace,
            localizationService: null,
            dialogService: dialogs);

        Execute(viewModel.ImportCommand);
        await WaitForAsync(
            () => viewModel.Tabs.Count == 1 && !viewModel.IsBusy,
            "real-image import");
        Assert.AreEqual(1, dialogs.ImageSelectionCount);
        Assert.IsNotNull(viewModel.SelectedTab);
        WorkspaceTabViewModel tab = viewModel.SelectedTab!;
        Assert.AreEqual(Path.GetFullPath(sourcePath), Path.GetFullPath(tab.SourcePath!));
        Assert.AreEqual(sourceHashBefore, tab.SourceSha256);
        Assert.IsNotNull(tab.ImageSource);
        Assert.IsGreaterThan(0, tab.PixelWidth);
        Assert.IsGreaterThan(0, tab.PixelHeight);
        Assert.HasCount(0, tab.Points);
        Assert.HasCount(0, tab.SeriesCards);
        Assert.HasCount(0, tab.PhaseDividers);

        double initialZoom = tab.ZoomLevel;
        Execute(viewModel.ZoomInCommand);
        Assert.IsGreaterThan(initialZoom, tab.ZoomLevel);
        Execute(viewModel.ZoomOutCommand);
        Assert.AreEqual(initialZoom, tab.ZoomLevel, 0.000001);
        Assert.IsNotNull(viewModel.Magnifier);

        Point session1Y0 = ScalePoint(tab, 64, 296);
        Point session1Y100 = ScalePoint(tab, 64, 76);
        Point session24Y0 = ScalePoint(tab, 763, 296);
        viewModel.ManualYMaximum = 100;
        viewModel.ManualXMaximum = 24;
        Execute(viewModel.StartCalibrationCommand);
        await viewModel.HandleCanvasPointAsync(session1Y0);
        await viewModel.HandleCanvasPointAsync(session1Y100);
        await viewModel.HandleCanvasPointAsync(session24Y0);
        Assert.IsNotNull(tab.Calibration);
        ManualCalibrationState calibration = tab.Calibration!;
        Assert.IsNotNull(calibration.XTransform);
        Assert.IsNotNull(calibration.YTransform);
        Assert.AreEqual(1, calibration.XTransform.PixelToGraph(session1Y0.X), 0.000001);
        Assert.AreEqual(24, calibration.XTransform.PixelToGraph(session24Y0.X), 0.000001);
        Assert.AreEqual(0, calibration.YTransform.PixelToGraph(session1Y0.Y), 0.000001);
        Assert.AreEqual(100, calibration.YTransform.PixelToGraph(session1Y100.Y), 0.000001);
        AssertCalibrationContract(workspace.CurrentProject);

        SeriesCardViewModel filledSeries = CreateSeries(
            viewModel,
            "Filled observations",
            MarkerShape.Circle,
            MarkerFill.Filled,
            SemanticRole.Intervention);
        SeriesCardViewModel openSeries = CreateSeries(
            viewModel,
            "Generalization probes",
            MarkerShape.Circle,
            MarkerFill.Open,
            SemanticRole.Generalization);
        Assert.AreEqual("●", filledSeries.Symbol);
        Assert.AreEqual("○", openSeries.Symbol);

        viewModel.SelectedSeriesId = filledSeries.SeriesId;
        SeriesRelationChoiceViewModel probeChoice = viewModel.ProbeRelationChoices.Single(
            choice => choice.SeriesId == openSeries.SeriesId);
        probeChoice.IsSelected = true;
        Execute(viewModel.ApplySeriesRelationsCommand);

        viewModel.SelectedSeriesId = filledSeries.SeriesId;
        Execute(viewModel.BeginAddFilledPointCommand);
        await viewModel.HandleCanvasPointAsync(ScalePoint(tab, 78, 224));
        string filledOne = viewModel.SelectedPointId!;
        string filledToMove = await AddPointAsync(viewModel, filledSeries.SeriesId, ScalePoint(tab, 341, 209));
        string filledThree = await AddPointAsync(viewModel, filledSeries.SeriesId, ScalePoint(tab, 367, 149));
        string filledToDelete = await AddPointAsync(viewModel, filledSeries.SeriesId, ScalePoint(tab, 394, 114));
        viewModel.SelectedPointId = filledToMove;
        Execute(viewModel.BeginMovePointCommand);
        Point movedPixel = ScalePoint(tab, 344, 206);
        await viewModel.HandleCanvasPointAsync(movedPixel);
        Assert.AreEqual(movedPixel.X, tab.Points.Single(point => point.PointId == filledToMove).PixelX, 0.000001);
        Assert.IsTrue(viewModel.DeletePointCommand.CanExecute(filledToDelete));
        viewModel.DeletePointCommand.Execute(filledToDelete);
        Assert.IsFalse(tab.Points.Any(point => point.PointId == filledToDelete));
        string readdedFilled = await AddPointAsync(viewModel, filledSeries.SeriesId, ScalePoint(tab, 394, 114));
        Assert.AreNotEqual(filledToDelete, readdedFilled);

        viewModel.SelectedSeriesId = openSeries.SeriesId;
        Execute(viewModel.BeginAddOpenPointCommand);
        await viewModel.HandleCanvasPointAsync(ScalePoint(tab, 656, 76));
        string openOne = viewModel.SelectedPointId!;
        string openTwo = await AddPointAsync(viewModel, openSeries.SeriesId, ScalePoint(tab, 682, 76));
        Assert.HasCount(6, tab.Points);
        Assert.AreEqual(4, filledSeries.Count);
        Assert.AreEqual(2, openSeries.Count);

        viewModel.SelectedSeriesId = filledSeries.SeriesId;
        viewModel.NewSeriesName = "Filled observations edited";
        Execute(viewModel.EditSeriesCommand);
        Assert.AreEqual("Filled observations edited", filledSeries.Label);
        viewModel.NewSeriesName = "Filled observations";
        Execute(viewModel.EditSeriesCommand);
        Assert.AreEqual("Filled observations", filledSeries.Label);

        double pitch = (session24Y0.X - session1Y0.X) / 23d;
        double betweenSession9And10 = session1Y0.X + (8.5d * pitch);
        double betweenSession14And15 = session1Y0.X + (13.5d * pitch);
        EditablePhaseDivider interventionDivider = await AddDividerAsync(
            viewModel,
            betweenSession9And10,
            "b",
            "Intervention");
        EditablePhaseDivider unknownLaterDivider = await AddDividerAsync(
            viewModel,
            betweenSession14And15,
            "phase3",
            "Phase 3");
        Assert.AreEqual(betweenSession9And10, interventionDivider.OriginalX, 0.000001);
        Assert.AreEqual(betweenSession14And15, unknownLaterDivider.OriginalX, 0.000001);
        Assert.AreEqual("b", interventionDivider.Code);
        Assert.AreEqual("Intervention", interventionDivider.Label);
        Assert.AreEqual("phase3", unknownLaterDivider.Code);
        Assert.AreEqual("Phase 3", unknownLaterDivider.Label);
        Assert.HasCount(2, tab.PhaseDividers);

        Execute(viewModel.SaveProjectAsCommand);
        await WaitForAsync(
            () => File.Exists(projectPath) &&
                string.Equals(workspace.CurrentProjectPath, projectPath, StringComparison.OrdinalIgnoreCase) &&
                !viewModel.IsBusy,
            "Save As");
        Assert.AreEqual(1, dialogs.ProjectSaveSelectionCount);
        Assert.IsFalse(tab.IsDirty);
        Assert.AreEqual(sourceHashBefore, Sha256(sourcePath));
        Assert.AreEqual(sourceHashBefore, workspace.CurrentProject.Sources.Single().Sha256);
        Assert.IsFalse(
            (await File.ReadAllTextAsync(projectPath)).Contains("recorded_fake", StringComparison.OrdinalIgnoreCase));

        string savedBytes = await File.ReadAllTextAsync(projectPath);
        viewModel.SelectedPointId = filledThree;
        Execute(viewModel.BeginMovePointCommand);
        Point secondMove = ScalePoint(tab, 370, 146);
        await viewModel.HandleCanvasPointAsync(secondMove);
        Assert.IsTrue(tab.IsDirty);
        Execute(viewModel.SaveProjectCommand);
        await WaitForAsync(
            () => !viewModel.IsBusy &&
                !string.Equals(File.ReadAllText(projectPath), savedBytes, StringComparison.Ordinal),
            "existing-path Save");
        Assert.IsFalse(tab.IsDirty);
        Assert.AreEqual(1, dialogs.ProjectSaveSelectionCount);

        Execute(viewModel.CloseTabCommand);
        Assert.HasCount(0, viewModel.Tabs);
        Assert.IsNull(viewModel.SelectedTab);
        Execute(viewModel.OpenProjectCommand);
        await WaitForAsync(
            () => viewModel.Tabs.Count == 1 && viewModel.SelectedTab is not null && !viewModel.IsBusy,
            "close and reopen persistence");
        WorkspaceTabViewModel reopenedTab = viewModel.SelectedTab!;
        Assert.HasCount(6, reopenedTab.Points);
        Assert.HasCount(2, reopenedTab.SeriesCards);
        Assert.HasCount(2, reopenedTab.PhaseDividers);
        Assert.IsNotNull(reopenedTab.Calibration);
        Assert.AreEqual(24, reopenedTab.Calibration.XMaximum);
        Assert.AreEqual(100, reopenedTab.Calibration.YMaximum);
        Assert.IsTrue(reopenedTab.SeriesCards.Any(series =>
            series.Shape == MarkerShape.Circle && series.Fill == MarkerFill.Filled));
        Assert.IsTrue(reopenedTab.SeriesCards.Any(series =>
            series.Shape == MarkerShape.Circle && series.Fill == MarkerFill.Open));
        Assert.IsTrue(reopenedTab.PhaseDividers.Any(divider =>
            divider.Code == "b" && divider.Label == "Intervention"));
        Assert.IsTrue(reopenedTab.PhaseDividers.Any(divider =>
            divider.Code == "phase3" && divider.Label == "Phase 3"));

        Execute(viewModel.ExportCommand);
        await WaitForAsync(
            () => !viewModel.IsBusy &&
                Directory.GetFiles(exportRoot, "*.csv", SearchOption.AllDirectories).Length > 0,
            "GraphReader.Export CSV export");
        string[] csvFiles = Directory.GetFiles(exportRoot, "*.csv", SearchOption.AllDirectories);
        string[] minimalCsvFiles = csvFiles.Where(path =>
            string.Equals(File.ReadLines(path).First(), "x_value,y_value,phase", StringComparison.Ordinal)).ToArray();
        Assert.IsGreaterThan(0, minimalCsvFiles.Length);
        Assert.IsTrue(minimalCsvFiles.All(path => File.ReadLines(path).Skip(1).Any()));
        Assert.IsGreaterThan(0, Directory.GetFiles(exportRoot, "*.json", SearchOption.AllDirectories).Length);

        string recoveryPointId = reopenedTab.Points.Single(point => point.PointId == filledToMove).PointId;
        viewModel.SelectedPointId = recoveryPointId;
        Execute(viewModel.BeginMovePointCommand);
        Point recoveryMove = ScalePoint(reopenedTab, 347, 203);
        await viewModel.HandleCanvasPointAsync(recoveryMove);
        string autosavePath = new ProjectSnapshotService(paths.AutosaveRoot)
            .GetSnapshotPath(workspace.CurrentProject.ProjectId);
        Assert.IsTrue(File.Exists(autosavePath), "The real edit must write autosave evidence under portable Data.");
        StringAssert.StartsWith(autosavePath, portableDataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(Directory.Exists(localApplicationDataDecoy));

        Execute(viewModel.RecoverProjectCommand);
        await WaitForAsync(
            () => File.Exists(recoveredProjectPath) &&
                string.Equals(workspace.CurrentProjectPath, recoveredProjectPath, StringComparison.OrdinalIgnoreCase) &&
                viewModel.SelectedTab is not null &&
                !viewModel.IsBusy,
            "autosave recovery to a new file");
        WorkspaceTabViewModel recoveredTab = viewModel.SelectedTab!;
        Assert.AreEqual(
            recoveryMove.X,
            recoveredTab.Points.Single(point => point.PointId == recoveryPointId).PixelX,
            0.000001);
        Assert.HasCount(6, recoveredTab.Points);
        Assert.HasCount(2, recoveredTab.SeriesCards);
        Assert.HasCount(2, recoveredTab.PhaseDividers);
        Assert.IsNotNull(recoveredTab.Calibration);
        Assert.AreEqual(sourceHashBefore, recoveredTab.SourceSha256);

        AutomaticStageStatus enhancementStage = workspace.AutomaticStages.Single(status =>
            string.Equals(status.Stage, "enhancement", StringComparison.Ordinal));
        bool enhancementConfigurationPresent =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                ApplicationComposition.RealEsrganManifestEnvironmentVariable)) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                ApplicationComposition.RealEsrganRuntimeRootEnvironmentVariable));
        bool localEnhancementAvailable = enhancementStage.State == AutomaticStageState.Experimental;
        string? enhancedOutputPath = null;
        double enhancementRuntimeMilliseconds = 0;
        if (localEnhancementAvailable)
        {
            Assert.IsTrue(viewModel.EnhanceCommand.CanExecute(null));
            Execute(viewModel.EnhanceCommand);
            await WaitForAsync(
                () => !viewModel.IsBusy && recoveredTab.HasEnhancedPreview,
                "official Real-ESRGAN x2 enhancement");
            Assert.IsNotNull(recoveredTab.EnhancedImageSource);
            BitmapSource enhancedBitmap = Assert.IsInstanceOfType<BitmapSource>(recoveredTab.EnhancedImageSource);
            Assert.AreEqual(recoveredTab.PixelWidth * 2, enhancedBitmap.PixelWidth);
            Assert.AreEqual(recoveredTab.PixelHeight * 2, enhancedBitmap.PixelHeight);
            Execute(viewModel.ShowComparisonPreviewCommand);
            Assert.AreEqual(EnhancementPreviewMode.Comparison, recoveredTab.EnhancementPreviewMode);
            Assert.IsTrue(recoveredTab.IsComparisonPreview);
            Assert.IsTrue(recoveredTab.IsDirty);
            Assert.AreEqual(sourceHashBefore, Sha256(sourcePath));
            JsonElement enhancement = workspace.CurrentProject.Panels.Single().Enhancement!.Value;
            Assert.AreEqual("comparison", enhancement.GetProperty("selected_preview").GetString());
            Assert.IsTrue(enhancement.GetProperty("original_immutable").GetBoolean());
            JsonElement envelope = enhancement.GetProperty("enhancement");
            Assert.AreEqual("realesr-animevideov3", envelope.GetProperty("model").GetProperty("model_id").GetString());
            Assert.AreEqual(sourceHashBefore, envelope.GetProperty("input_sha256").GetString());
            enhancementRuntimeMilliseconds = envelope.GetProperty("timing_ms").GetProperty("total").GetDouble();
            string derivativeRoot = Path.Combine(paths.CacheRoot, "Enhancement", "Derivatives");
            enhancedOutputPath = Directory.GetFiles(derivativeRoot, "*.png", SearchOption.TopDirectoryOnly).Single();
            Execute(viewModel.SaveProjectCommand);
            await WaitForAsync(() => !viewModel.IsBusy, "enhancement provenance save");
            Assert.IsFalse(recoveredTab.IsDirty);
            StringAssert.Contains(await File.ReadAllTextAsync(recoveredProjectPath), "realesr-animevideov3");
        }

        ScreenshotEvidence screenshot = RenderMainWindow(viewModel, screenshotPath, 1400, 900);
        Assert.IsTrue(File.Exists(screenshotPath));
        Assert.IsGreaterThan(0, new FileInfo(screenshotPath).Length);
        Assert.AreEqual(1400, screenshot.Width);
        Assert.AreEqual(900, screenshot.Height);

        string sourceHashAfter = Sha256(sourcePath);
        Assert.AreEqual(sourceHashBefore, sourceHashAfter);
        Assert.AreEqual(sourceHashBefore, workspace.CurrentProject.Sources.Single().Sha256);
        Assert.IsTrue(workspace.CurrentProject.Panels.Single().Points.All(static point =>
            point.SourceStage == "manual" && point.MarkerId is null && point.ModelVersion is null));
        Assert.IsTrue(workspace.CurrentProject.Panels.Single().Markers.Count == 0);
        Assert.IsTrue(workspace.CurrentProject.Panels.Single().OcrRegions.Count == 0);

        var report = new
        {
            schema = "graphreader.private-real-graph-manual-validation.v1",
            completedUtc = DateTimeOffset.UtcNow,
            runtimeEnvironment = composition.Environment.ToString(),
            distributionMode = paths.Mode.ToString(),
            usesFakeGraphData = workspace.UsesFakeGraphData,
            automaticDetectionAccuracyClaimed = false,
            enhancement = new
            {
                configured = enhancementConfigurationPresent,
                available = localEnhancementAvailable,
                availabilityEvidence = enhancementStage.Explanation,
                model = localEnhancementAvailable ? "realesr-animevideov3" : null,
                scale = localEnhancementAvailable ? 2 : 0,
                originalImmutable = string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.Ordinal),
                outputPath = enhancedOutputPath,
                outputSha256 = enhancedOutputPath is null ? null : Sha256(enhancedOutputPath),
                runtimeMilliseconds = enhancementRuntimeMilliseconds,
                previewMode = recoveredTab.EnhancementPreviewMode.ToString(),
                releaseEligible = false,
            },
            source = new
            {
                path = sourcePath,
                sha256Before = sourceHashBefore,
                sha256After = sourceHashAfter,
                unchanged = string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.Ordinal),
                pixelWidth = recoveredTab.PixelWidth,
                pixelHeight = recoveredTab.PixelHeight,
            },
            calibration = new
            {
                anchors = new[] { "(1,0)", "(1,100)", "(24,0)" },
                yMaximum = recoveredTab.Calibration.YMaximum,
                xMaximum = recoveredTab.Calibration.XMaximum,
            },
            manualEdits = new
            {
                filledSeriesId = filledSeries.SeriesId,
                openSeriesId = openSeries.SeriesId,
                filledPointIds = new[] { filledOne, filledToMove, filledThree, readdedFilled },
                openPointIds = new[] { openOne, openTwo },
                pointCount = recoveredTab.Points.Count,
                dividerCount = recoveredTab.PhaseDividers.Count,
                phaseBoundaries = new[]
                {
                    new { betweenSessions = "9/10", code = "b", label = "Intervention", originalX = betweenSession9And10 },
                    new { betweenSessions = "14/15", code = "phase3", label = "Phase 3", originalX = betweenSession14And15 },
                },
                moveVerified = true,
                deleteAndReaddVerified = true,
            },
            persistence = new
            {
                projectPath,
                recoveredProjectPath,
                autosavePath,
                portableDataRoot,
                saveAsVerified = true,
                saveExistingPathVerified = true,
                closeAndReopenVerified = true,
                recoveryVerified = true,
            },
            export = new
            {
                outputRoot = exportRoot,
                csvHeader = ExportContract.MinimalCsvHeader,
                minimalCsvFiles = minimalCsvFiles.Select(path => new { path, sha256 = Sha256(path) }).ToArray(),
                csvFiles = csvFiles.Select(path => new { path, sha256 = Sha256(path) }).ToArray(),
            },
            wpfUi = new
            {
                realMainWindowRendered = true,
                graphTabDisplayed = true,
                fitVerified = true,
                zoomInOutVerified = true,
                horizontalPanVerified = true,
                fixedRightMagnifierVerified = true,
            },
            screenshot = new
            {
                path = screenshotPath,
                screenshot.Width,
                screenshot.Height,
                screenshot.Sha256,
            },
        };
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, EvidenceJsonOptions));
        Assert.IsTrue(File.Exists(reportPath));
    }

    private static void AssertCalibrationContract(ProjectDocument project)
    {
        Assert.IsNotNull(project.Panels.Single().Calibration);
        CalibrationRecord calibration = project.Panels.Single().Calibration!;
        Assert.IsTrue(calibration.UserConfirmed);
        Assert.HasCount(3, calibration.Anchors);
        CalibrationAnchor session1Y0 = calibration.Anchors.Single(
            static anchor => anchor.Kind == CalibrationAnchorKind.Session1Y0);
        CalibrationAnchor session1Y100 = calibration.Anchors.Single(
            static anchor => anchor.Kind == CalibrationAnchorKind.Session1Ymax);
        CalibrationAnchor session24Y0 = calibration.Anchors.Single(
            static anchor => anchor.Kind == CalibrationAnchorKind.SessionmaxY0);
        Assert.AreEqual(1, session1Y0.Graph.X);
        Assert.AreEqual(0, session1Y0.Graph.Y);
        Assert.AreEqual(1, session1Y100.Graph.X);
        Assert.AreEqual(100, session1Y100.Graph.Y);
        Assert.AreEqual(24, session24Y0.Graph.X);
        Assert.AreEqual(0, session24Y0.Graph.Y);
    }

    private static SeriesCardViewModel CreateSeries(
        MainWindowViewModel viewModel,
        string label,
        MarkerShape shape,
        MarkerFill fill,
        SemanticRole role)
    {
        viewModel.NewSeriesName = label;
        viewModel.NewSeriesShape = shape;
        viewModel.NewSeriesFill = fill;
        viewModel.NewSeriesRole = role;
        Execute(viewModel.CreateSeriesCommand);
        return viewModel.SeriesCards.Single(series => series.Label == label);
    }

    private static async Task<string> AddPointAsync(
        MainWindowViewModel viewModel,
        string seriesId,
        Point pixel)
    {
        viewModel.SelectedSeriesId = seriesId;
        Execute(viewModel.BeginAddPointCommand);
        await viewModel.HandleCanvasPointAsync(pixel);
        Assert.IsNotNull(viewModel.SelectedPointId);
        return viewModel.SelectedPointId!;
    }

    private static async Task<EditablePhaseDivider> AddDividerAsync(
        MainWindowViewModel viewModel,
        double originalX,
        string code,
        string label)
    {
        viewModel.PhaseCode = code;
        viewModel.PhaseLabel = label;
        Execute(viewModel.BeginAddPhaseDividerCommand);
        await viewModel.HandleCanvasPointAsync(new Point(originalX, 0));
        Assert.IsNotNull(viewModel.SelectedDividerId);
        string selectedDividerId = viewModel.SelectedDividerId!;
        return viewModel.SelectedTab!.PhaseDividers.Single(divider => divider.DividerId == selectedDividerId);
    }

    private static Point ScalePoint(WorkspaceTabViewModel tab, double referenceX, double referenceY) =>
        new(referenceX * tab.PixelWidth / 863d, referenceY * tab.PixelHeight / 395d);

    private static ScreenshotEvidence RenderMainWindow(
        MainWindowViewModel viewModel,
        string path,
        int width,
        int height)
    {
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = width,
            Height = height,
            Left = SystemParameters.VirtualScreenLeft - width - 100,
            Top = SystemParameters.VirtualScreenTop - height - 100,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Focusable = false,
            IsHitTestVisible = false,
        };
        AddWpfResources(window);
        try
        {
            bool contentRendered = false;
            var renderFrame = new DispatcherFrame();
            var timeout = new DispatcherTimer(
                TimeSpan.FromSeconds(5),
                DispatcherPriority.Send,
                (_, _) => renderFrame.Continue = false,
                window.Dispatcher);
            window.ContentRendered += (_, _) =>
            {
                contentRendered = true;
                renderFrame.Continue = false;
            };
            window.Show();
            timeout.Start();
            if (!contentRendered)
            {
                Dispatcher.PushFrame(renderFrame);
            }

            timeout.Stop();
            Assert.IsTrue(contentRendered, "The off-screen real-graph window did not render content.");
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            FrameworkElement content = Assert.IsInstanceOfType<FrameworkElement>(window.Content);
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();

            GraphCanvasControl graphCanvas = Assert.IsInstanceOfType<GraphCanvasControl>(window.FindName("GraphCanvasHost"));
            FrameworkElement magnifier = Assert.IsInstanceOfType<FrameworkElement>(window.FindName("MagnifierInspector"));
            Point graphOrigin = graphCanvas.TranslatePoint(new Point(0, 0), content);
            Point magnifierOrigin = magnifier.TranslatePoint(new Point(0, 0), content);
            Assert.IsGreaterThan(
                graphOrigin.X + graphCanvas.ActualWidth,
                magnifierOrigin.X,
                "The fixed magnifier must remain to the right of the editable graph canvas.");

            double prePanZoom = viewModel.SelectedTab!.ZoomLevel;
            Execute(viewModel.ZoomInCommand);
            Execute(viewModel.ZoomInCommand);
            Assert.IsGreaterThan(prePanZoom, viewModel.SelectedTab.ZoomLevel);
            content.UpdateLayout();
            ScrollViewer? discoveredPanHost = FindVisualDescendant<ScrollViewer>(graphCanvas);
            Assert.IsNotNull(discoveredPanHost);
            ScrollViewer panHost = discoveredPanHost!;
            Assert.AreEqual(PanningMode.Both, panHost.PanningMode);
            Assert.IsGreaterThan(0, panHost.ScrollableWidth);
            panHost.ScrollToHorizontalOffset(Math.Min(24, panHost.ScrollableWidth));
            panHost.UpdateLayout();
            Assert.IsGreaterThan(0, panHost.HorizontalOffset, "The real graph canvas must support horizontal panning.");

            Button fitButton = Assert.IsInstanceOfType<Button>(window.FindName("FitGraphButton"));
            fitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            content.UpdateLayout();
            Assert.AreEqual(1, viewModel.SelectedTab.ZoomLevel, 0.000001);
            Assert.AreEqual(0, panHost.HorizontalOffset, 0.000001);
            Assert.AreEqual(0, panHost.VerticalOffset, 0.000001);
            Assert.IsGreaterThan(0, graphCanvas.FitScale);
            Assert.AreEqual(graphCanvas.FitScale, graphCanvas.EffectiveScale, 0.000001);

            RenderTargetBitmap bitmap = CaptureWindow(window, width, height);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }

        return new ScreenshotEvidence(width, height, Sha256(path));
    }

    private static RenderTargetBitmap CaptureWindow(Window window, int width, int height)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Assert.AreNotEqual(
            IntPtr.Zero,
            handle,
            "The off-screen real-graph window has no native handle.");
        HwndSource? source = HwndSource.FromHwnd(handle);
        Assert.IsNotNull(source);
        Assert.IsNotNull(source.CompositionTarget);
        source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        window.InvalidateVisual();
        window.UpdateLayout();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        FrameworkElement content = Assert.IsInstanceOfType<FrameworkElement>(window.Content);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new VisualBrush(content)
                {
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    AutoLayoutContent = true,
                    Stretch = Stretch.None,
                },
                null,
                new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        uint? firstPixel = null;
        bool hasRenderedPixel = false;
        bool hasColorVariation = false;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];
            if (alpha == 0)
            {
                continue;
            }

            hasRenderedPixel = true;
            uint pixel = (uint)(
                pixels[index] |
                (pixels[index + 1] << 8) |
                (pixels[index + 2] << 16) |
                (alpha << 24));
            firstPixel ??= pixel;
            if (pixel != firstPixel.Value)
            {
                hasColorVariation = true;
                break;
            }
        }

        if (!hasRenderedPixel)
        {
            Assert.Inconclusive(
                "The WPF pixel surface is unavailable on this noninteractive Windows test desktop; no private screenshot evidence was emitted.");
        }

        Assert.IsTrue(hasColorVariation, "The off-screen real-graph window produced a uniform pixel surface.");
        bitmap.Freeze();
        return bitmap;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void AddWpfResources(FrameworkElement element)
    {
        string[] resourceSources =
        [
            "/GraphReader.App;component/Themes/DesignTokens.xaml",
            "/GraphReader.App;component/Themes/LightTheme.xaml",
            "/GraphReader.App;component/Themes/Controls.xaml",
            "/GraphReader.App;component/Localization/Resources.en-US.xaml",
        ];
        foreach (string source in resourceSources)
        {
            element.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(source, UriKind.RelativeOrAbsolute),
            });
        }
    }

    private static void Execute(ICommand command)
    {
        Assert.IsTrue(command.CanExecute(null), $"Command '{command.GetType().Name}' was unexpectedly disabled.");
        command.Execute(null);
    }

    private static async Task WaitForAsync(Func<bool> completed, string operation)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!completed())
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(10))
            {
                Assert.Fail($"Timed out after 10 seconds waiting for {operation}.");
            }

            await Task.Delay(10);
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed record ScreenshotEvidence(int Width, int Height, string Sha256);

    private sealed class RealGraphDialogService : IWorkspaceDialogService
    {
        public IReadOnlyList<string> Images { get; init; } = [];

        public string? ProjectToOpen { get; init; }

        public IReadOnlyList<string?> ProjectSaveDestinations { get; init; } = [];

        public string? ExportDirectory { get; init; }

        public int ImageSelectionCount { get; private set; }

        public int ProjectSaveSelectionCount { get; private set; }

        public IReadOnlyList<string> SelectImages()
        {
            ImageSelectionCount++;
            return Images;
        }

        public string? SelectProjectToOpen() => ProjectToOpen;

        public string? SelectProjectToSave(string? currentPath)
        {
            _ = currentPath;
            int selectionIndex = ProjectSaveSelectionCount++;
            return selectionIndex < ProjectSaveDestinations.Count
                ? ProjectSaveDestinations[selectionIndex]
                : null;
        }

        public string? SelectExportDirectory() => ExportDirectory;
    }
}
