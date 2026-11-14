// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Windows;
using GraphReader.App.Appearance;
using GraphReader.App.Integration;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Integration.Runtime;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.Domain;

namespace GraphReader.App;

public partial class App : Application, IDisposable
{
    private ThemeService? _themeService;
    private ProductionInferenceRuntimeHost? _inferenceRuntimeHost;

    public IThemeService ThemeService =>
        _themeService ?? throw new InvalidOperationException("The application theme service is not initialized.");

    public IThemeService? AvailableThemeService => _themeService;

    public ILocalizationService LocalizationService { get; private set; } = null!;

    public IApplicationPaths? ApplicationPaths { get; private set; }

    public DomainError? StartupError { get; private set; }

    public DomainError? WorkflowStartupError { get; private set; }

    public string? StartupErrorMessageKey { get; private set; }

    public IWorkspaceService WorkspaceService { get; private set; } = new UnavailableWorkspaceService();

    public CalibrationOcrDeviceSettings? CalibrationOcrDevices { get; private set; }
    public CalibrationOcrDeviceOptimizer? CalibrationDeviceOptimizer { get; private set; }

    public WorkflowRuntimeEnvironment RuntimeEnvironment { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(argument => string.Equals(argument, "--native-axis-worker", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                _ = StartupArguments.Parse(e.Args);
                int exitCode = await Task.Run(() => NativeAxisWorker.RunAsync(RuntimeModeSelector.SelectBuildDefault(),
                    Console.OpenStandardInput(), Console.OpenStandardOutput()));
                Shutdown(exitCode);
            }
            catch (Exception) { Shutdown(2); }
            return;
        }
        if (e.Args.Any(argument => string.Equals(argument, "--marker-exemplar-worker", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length != 1) { Shutdown(2); return; }
                int exitCode = await Task.Run(() => MarkerExemplarWorker.RunAsync(
                    RuntimeModeSelector.SelectBuildDefault(), Console.OpenStandardInput(), Console.OpenStandardOutput()));
                Shutdown(exitCode);
            }
            catch (Exception) { Shutdown(2); }
            return;
        }
        if (e.Args.Any(argument => string.Equals(argument, "--calibration-ocr-worker", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                StartupArguments workerArguments = StartupArguments.Parse(e.Args);
                string assetRoot = workerArguments.CalibrationOcrAssetsRoot!;
                int exitCode = await Task.Run(() => CalibrationOcrWorker.RunAsync(
                    assetRoot, RuntimeModeSelector.SelectBuildDefault(),
                    Console.OpenStandardInput(), Console.OpenStandardOutput(),
                    deviceSelection: workerArguments.CalibrationOcrDevice));
                Shutdown(exitCode);
            }
            catch (Exception)
            {
                // Headless child startup never opens an error dialog in the user's session.
                Shutdown(2);
            }
            return;
        }

        _themeService = new ThemeService(Resources);
        LocalizationService = new LocalizationService(Resources);
        StartupArguments startupArguments = StartupArguments.Parse(e.Args);
        RuntimeEnvironment = RuntimeModeSelector.Select();
        if (startupArguments.ProductionRuntimeSmoke)
        {
            bool productionRuntime =
                RuntimeModeSelector.SelectBuildDefault() == WorkflowRuntimeEnvironment.Production;
            bool packagedPdfiumReady = true;
            if (productionRuntime && startupArguments.RequirePackagedPdfium)
            {
                (_, bool configured, DomainError? error) =
                    ApplicationComposition.CreateReviewedPdfImporter(AppContext.BaseDirectory);
                packagedPdfiumReady = configured && error is null;
            }

            Shutdown(productionRuntime && packagedPdfiumReady ? 0 : 2);
            return;
        }

        DomainResult<IApplicationPaths> runtimePaths =
            RuntimePathBootstrapper.CreateDefault().Initialize();
        ApplicationPaths = runtimePaths.Value;
        StartupError = runtimePaths.Errors.Count > 0 ? runtimePaths.Errors[0] : null;
        ApplicationCompositionResult composition = await ApplicationComposition.CreateAsync(
            RuntimeEnvironment,
            ApplicationPaths,
            cancellationToken: CancellationToken.None);
        WorkspaceService = composition.WorkspaceService;
        CalibrationOcrDevices = composition.CalibrationOcrDevices;
        CalibrationDeviceOptimizer = composition.CalibrationDeviceOptimizer;
        _inferenceRuntimeHost = composition.InferenceRuntimeHost;
        WorkflowStartupError = composition.StartupError;
        StartupErrorMessageKey = StartupError?.UserMessageKey ?? WorkflowStartupError?.UserMessageKey;

        bool portableSmoke = startupArguments.PortableSmoke;
        if (portableSmoke)
        {
            int exitCode = RunPortableSmoke();
            Shutdown(exitCode);
            return;
        }

        if (startupArguments.OpenImagePath is not null &&
            WorkspaceService is IManualWorkspaceService manualWorkspace)
        {
            await manualWorkspace
                .ImportImagesAsync([startupArguments.OpenImagePath], CancellationToken.None);
        }

        var mainWindow = new MainWindow(_themeService);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private int RunPortableSmoke()
    {
        if (StartupError is not null || WorkflowStartupError is not null ||
            ApplicationPaths is null ||
            RuntimeEnvironment != WorkflowRuntimeEnvironment.ManualPreview ||
            WorkspaceService is not IRuntimeWorkspaceService runtimeWorkspace ||
            runtimeWorkspace.UsesFakeGraphData ||
            runtimeWorkspace.CreateWorkspace().Count != 0)
        {
            return 2;
        }

        var window = new MainWindow(_themeService)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Close();
        return 0;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        CalibrationDeviceOptimizer?.Dispose();
        CalibrationDeviceOptimizer = null;
        if (WorkspaceService is IDisposable disposableWorkspace)
        {
            disposableWorkspace.Dispose();
        }

        ProductionInferenceRuntimeHost? inferenceRuntimeHost = _inferenceRuntimeHost;
        _inferenceRuntimeHost = null;
        if (inferenceRuntimeHost is not null)
        {
            inferenceRuntimeHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _themeService?.Dispose();
        _themeService = null;
        GC.SuppressFinalize(this);
    }
}
