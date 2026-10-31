// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.App.Models;
using GraphReader.App.ViewModels;
using GraphReader.Axis;
using GraphReader.Domain;
using GraphReader.Export;
using GraphReader.Imaging;

namespace GraphReader.App.Services;

public interface IWorkspaceService
{
    IReadOnlyList<WorkspaceTabViewModel> CreateWorkspace();

    Task RunStageAsync(WorkflowStage stage, CancellationToken cancellationToken);
}

public enum AutomaticStageState
{
    Available,
    Unavailable,
    Experimental,
    Approved,
}

public sealed record AutomaticStageStatus(
    string Stage,
    AutomaticStageState State,
    string Explanation);

public interface IRuntimeWorkspaceService : IWorkspaceService
{
    WorkflowRuntimeEnvironment RuntimeEnvironment { get; }

    IReadOnlyList<AutomaticStageStatus> AutomaticStages { get; }

    bool UsesFakeGraphData { get; }
}

public sealed record ManualCalibrationRequest(
    GraphReader.Axis.PixelPoint Session1Y0,
    GraphReader.Axis.PixelPoint Session1YMaximum,
    GraphReader.Axis.PixelPoint SessionMaximumY0,
    double YMaximum,
    double XMaximum,
    double FirstVisibleSession = 1);

public sealed record ManualSeriesDefinition(
    string DisplayName,
    string Symbol,
    MarkerShape Shape,
    MarkerFill Fill,
    SemanticRole SemanticRole);

public enum EnhancementPreviewMode
{
    Original,
    Enhanced,
    Comparison,
}

public sealed record WorkspaceEnhancementResult(
    bool Succeeded,
    string Message,
    string? OutputPath = null,
    double RuntimeMilliseconds = 0,
    string? UserMessageKey = null,
    IReadOnlyList<object>? MessageArguments = null);

public interface IManualWorkspaceService : IRuntimeWorkspaceService
{
    ProjectDocument CurrentProject { get; }

    string? CurrentProjectPath { get; }

    IReadOnlyList<ImageImportError> LastImportErrors { get; }

    Task<IReadOnlyList<WorkspaceTabViewModel>> ImportImagesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceTabViewModel>> OpenProjectAsync(
        string path,
        CancellationToken cancellationToken);

    Task<WorkspaceEnhancementResult> EnhanceAsync(
        string tabId,
        CancellationToken cancellationToken);

    void SetEnhancementPreviewMode(string tabId, EnhancementPreviewMode mode);

    bool CloseTab(string tabId);

    Task<DomainResult<ProjectSaveReceipt>> SaveProjectAsync(
        string? path,
        CancellationToken cancellationToken);

    ManualCalibrationState Calibrate(string tabId, ManualCalibrationRequest request);

    SeriesCardViewModel AddSeries(string tabId, ManualSeriesDefinition definition);

    void UpdateSeries(string tabId, string seriesId, ManualSeriesDefinition definition);

    void SetSeriesRelations(
        string tabId,
        string interventionSeriesId,
        string? sharedBaselineSeriesId,
        IEnumerable<string> applicableProbeSeriesIds);

    GraphReader.App.Models.GraphPoint AddPoint(string tabId, string seriesId, double pixelX, double pixelY);

    void MovePoint(string tabId, string pointId, double pixelX, double pixelY);

    void DeletePoint(string tabId, string pointId);

    void SetPointReviewStatus(string tabId, string pointId, ReviewStatus status);

    void ReassignPoint(string tabId, string pointId, string targetSeriesId);

    EditablePhaseDivider AddPhaseDivider(string tabId, double originalX, string code, string label);

    void MovePhaseDivider(string tabId, string dividerId, double originalX);

    void DeletePhaseDivider(string tabId, string dividerId);

    void LabelPhaseDivider(string tabId, string dividerId, string code, string label);

    Task<DomainResult<ProjectSnapshotReceipt>> AutosaveAsync(
        SnapshotTrigger trigger,
        string? tabId,
        string? entityId,
        CancellationToken cancellationToken);

    Task<DomainResult<ProjectSnapshotReceipt>> TimerAutosaveAsync(
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken);

    Task<DomainResult<RecoveryDiscoveryReport>> DiscoverRecoveryAsync(
        CancellationToken cancellationToken);

    Task<DomainResult<ProjectSaveReceipt>> RecoverLatestToNewFileAsync(
        string destinationPath,
        CancellationToken cancellationToken);

    Task<ExportResult> ExportAsync(
        string tabId,
        string outputDirectory,
        CancellationToken cancellationToken);
}

public interface IWorkspaceEditStateSink
{
    void SynchronizeRestoredTab(string tabId, IReadOnlyDictionary<string, ReviewStatus>? reviewStatuses = null);
}

/// <summary>
/// Real-data workspace contract for an evidence-backed automatic workflow.
/// Implementations must never substitute recorded or synthetic detections.
/// </summary>
public interface IAutomaticWorkspaceService : IManualWorkspaceService
{
    WorkflowRunResult? LastAutomaticRun { get; }

    Task<WorkflowRunResult> RunAutomaticDetectionAsync(CancellationToken cancellationToken);
}
