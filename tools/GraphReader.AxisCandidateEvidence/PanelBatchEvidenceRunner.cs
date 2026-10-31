// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using OpenCvSharp;

namespace GraphReader.AxisCandidateEvidence;

public sealed class AxisCandidatePanelBatchEvidenceRunner
{
    public const string ReportSchema = "graphreader.axis-candidate-panel-batch-evidence.v1";
    private const string InputSchema = "graphreader.axis-candidate-panel-batch-input.v1";
    private const string DevCorpusManifestPath = "artifacts/axis-roles-dev-corpus-v3-20261031/manifest.json";
    private const string DevCorpusManifestSha256 = "c3c735a38dac5aa893331958ba09319e7fcfac534f8122738d8d07db37f507b1";
    private const string DevAdmissionPath = "artifacts/goal22-runs/axis-roles-dev-measure-admission-v2/binding.json";
    private const string DevAdmissionSha256 = "a32b43b05c74ea2279f2a7cd90e53024c5981f356dbf8735adbc8d1653168203";
    private const string DevInputsPath = "artifacts/goal22-runs/axis-roles-dev-measure-admission-v2/inputs.json";
    private const string DevInputsSha256 = "c0a4b9f292f0ea71e61365323909bfbbad46e84ad67baa27d93ec2ba5916c6cd";
    private const string DevReceiptPath = "artifacts/goal22-runs/axis-roles-dev-measure-run-v2/truth-free-receipt.json";
    private const string DevReceiptSha256 = "4bd1189d391b6a2e0cd9dfd4c73c1c3206f774327ebf7fa5f36fe6a6dfc8678a";
    private const string DevPanelizationRunRoot = "artifacts/goal22-runs/axis-roles-dev-measure-run-v2";
    private const string DevPanelizerRuntimePrefix = "artifacts/goal22-runs/axis-roles-panelization-build-v2/";
    private const int DevPanelizerRuntimeFileCount = 73;
    private const string ProductionAxisAdapterSourcePath = "src/GraphReader.App/Integration/Workflow/ProductionAxisGeometryAdapter.cs";
    private static readonly OpenCvLineCandidateOptions ProductionOpenCvOptions = new();
    private static readonly AxisGeometryOptions ProductionGeometryOptions = new();

    private static readonly string[] RequiredProductionSourcePaths =
    [
        "artifacts/goal22-tools/axis-roles-production-panelization-v2/Panelization.csproj",
        "artifacts/goal22-tools/axis-roles-production-panelization-v2/Program.cs",
        "src/GraphReader.App/GraphReader.App.csproj",
        "src/GraphReader.App/Integration/Workflow/ProductionRasterFrameDecoder.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionWorkflowDetectionStage.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionWorkflowImportStage.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionWorkflowPanelStore.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionRasterPanelizer.cs",
        "src/GraphReader.App/Integration/Workflow/RasterPanelSourceProvenance.cs",
        "src/GraphReader.App/Integration/Workflow/WorkflowContracts.cs",
        "src/GraphReader.Axis/AxisGeometryContracts.cs",
        "src/GraphReader.Axis/AxisGeometryDetector.cs",
        "src/GraphReader.Axis/GraphReader.Axis.csproj",
        "src/GraphReader.Axis/OpenCvLineCandidateProvider.cs",
        "src/GraphReader.Imaging/GraphReader.Imaging.csproj",
        "src/GraphReader.Imaging/ImageImportService.cs",
        "Directory.Packages.props",
    ];

    private static readonly string[] RequiredPanelizerRuntimePaths =
    [
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.deps.json",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.dll",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.runtimeconfig.json",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.App.dll",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Axis.dll",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Domain.dll",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Imaging.dll",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Pdf.dll",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/OpenCvSharp.dll",
        "artifacts/goal22-runs/axis-roles-panelization-build-v2/runtimes/win-x64/native/OpenCvSharpExtern.dll",
    ];

    private static readonly string[] AdditionalPanelizerSourcePaths =
    [
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py",
        "artifacts/goal22-tools/axis-roles-production-panelization-v2/Panelization.csproj",
        "artifacts/goal22-tools/axis-roles-production-panelization-v2/Program.cs",
        "ml/axis/candidate_ranker_foundation_v1/panelize_train_v2.py",
    ];

    private static readonly string[] DevOnlyPanelizerRuntimeSourcePaths =
    [
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/bind.py",
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/rank.py",
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/run.py",
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/test_rank.py",
        "artifacts/goal22-tools/axis-unmasked-ocr-v1/rank_numeric_regions.py",
    ];

    private static readonly string[] DevAdmissionSourcePaths =
    [
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/bind.py",
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py",
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/rank.py",
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/run.py",
        "artifacts/goal22-tools/axis-roles-dev-measure-v2/test_rank.py",
        "artifacts/goal22-tools/axis-roles-production-panelization-v2/Panelization.csproj",
        "artifacts/goal22-tools/axis-roles-production-panelization-v2/Program.cs",
        "artifacts/goal22-tools/axis-unmasked-ocr-v1/rank_numeric_regions.py",
        "src/GraphReader.App/Integration/Workflow/ProductionRasterPanelizer.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionWorkflowImportStage.cs",
        "src/GraphReader.App/Integration/Workflow/RasterPanelSourceProvenance.cs",
        "src/GraphReader.Imaging/ImageImportService.cs",
    ];

    private static readonly string[] ToolSourcePaths =
    [
        "tools/GraphReader.AxisCandidateEvidence/GraphReader.AxisCandidateEvidence.csproj",
        "tools/GraphReader.AxisCandidateEvidence/EvidenceContracts.cs",
        "tools/GraphReader.AxisCandidateEvidence/EvidenceRunner.cs",
        "tools/GraphReader.AxisCandidateEvidence/FeatureNormalization.cs",
        "tools/GraphReader.AxisCandidateEvidence/GlobalUsings.cs",
        "tools/GraphReader.AxisCandidateEvidence/PanelBatchEvidenceContracts.cs",
        "tools/GraphReader.AxisCandidateEvidence/PanelBatchEvidenceRunner.cs",
        "tools/GraphReader.AxisCandidateEvidence/Program.cs",
        "tools/GraphReader.AxisCandidateEvidence/input-binding.json",
    ];

    private static readonly JsonSerializerOptions InputJsonOptions = CreateInputJsonOptions();
    private readonly string _projectRoot;
    private readonly string _manifestPath;
    private readonly string _manifestRelativePath;
    private readonly string _manifestSha256;
    private readonly AxisCandidatePanelBatchBinding _binding;
    private readonly ILineCandidateProvider _candidateProvider;
    private readonly IAxisGeometryDetector _detector;

    private AxisCandidatePanelBatchEvidenceRunner(
        string projectRoot,
        string manifestPath,
        string manifestRelativePath,
        string manifestSha256,
        AxisCandidatePanelBatchBinding binding,
        ILineCandidateProvider? candidateProvider,
        IAxisGeometryDetector? detector)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _manifestPath = manifestPath;
        _manifestRelativePath = manifestRelativePath;
        _manifestSha256 = manifestSha256;
        _binding = binding;
        _candidateProvider = candidateProvider ?? new OpenCvLineCandidateProvider(binding.OpenCvOptions);
        _detector = detector ?? new AxisGeometryDetector();
    }

    public static AxisCandidatePanelBatchEvidenceRunner Load(
        string projectRoot,
        string relativeManifestPath,
        string expectedManifestSha256,
        ILineCandidateProvider? candidateProvider = null,
        IAxisGeometryDetector? detector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestSha256);
        string fullRoot = Path.GetFullPath(projectRoot);
        string manifestPath = ResolveProjectPath(relativeManifestPath, fullRoot);
        Program.EnsureNoReparsePoints(manifestPath, fullRoot, allowMissing: false);
        byte[] bytes = Program.ReadLocked(manifestPath);
        string actualSha = Hash(bytes);
        if (!string.Equals(actualSha, expectedManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Panel input manifest SHA-256 does not match the externally supplied digest.");
        }

        AxisCandidatePanelBatchBinding binding = JsonSerializer.Deserialize<AxisCandidatePanelBatchBinding>(bytes, InputJsonOptions)
            ?? throw new InvalidDataException("Panel input manifest is empty.");
        return new AxisCandidatePanelBatchEvidenceRunner(
            fullRoot,
            manifestPath,
            relativeManifestPath.Replace('\\', '/'),
            actualSha,
            binding,
            candidateProvider,
            detector);
    }

    public async Task<AxisCandidatePanelBatchReport> RunAsync(CancellationToken cancellationToken = default)
    {
        PanelBatchContext? context = null;
        try
        {
            ValidateBinding();
            cancellationToken.ThrowIfCancellationRequested();
            context = LoadAndVerifyPanelBatchInputs();
            IReadOnlyDictionary<string, string> productionBefore = VerifyProductionSourceBindings();
            IReadOnlyDictionary<string, string> toolBefore = VerifyToolSourceBindings();
            IReadOnlyDictionary<string, string> panelizerRuntimeBefore = VerifyPanelizerRuntimeBindings(context);
            IReadOnlyDictionary<string, string> toolRuntimeBefore = VerifyToolRuntimeBindings();
            VerifyPanelImageInventory(context);

            var panelResults = new List<AxisCandidatePanelResult>(context.SelectedPanels.Count);
            bool cancelled = false;
            foreach (PanelWorkItem item in context.SelectedPanels)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    panelResults.Add(CreateUnprocessedResult(item, "REQUEST_CANCELLED", typeof(OperationCanceledException)));
                    cancelled = true;
                    continue;
                }

                panelResults.Add(await ProcessPanelAsync(item, cancellationToken).ConfigureAwait(false));
                if (panelResults[^1].Status == "cancelled")
                {
                    cancelled = true;
                }
            }

            EnsureSameHash(context.AdmissionBindingPath, _binding.PanelizationAdmissionBindingSha256, "panelization admission binding");
            EnsureSameHash(context.ReceiptPath, _binding.PanelizationReceiptSha256, "panelization receipt");
            EnsureSameHash(_manifestPath, _manifestSha256, "panel input manifest");
            foreach (BoundPath supplemental in context.SupplementalBoundFiles ?? [])
            {
                EnsureSameHash(supplemental.Path, supplemental.Sha256, supplemental.Name);
            }
            VerifyPanelImageInventory(context);
            foreach (SourceWorkItem source in context.Sources.Where(static source => source.PanelizationReportPath is not null))
            {
                EnsureSameHash(source.PanelizationReportPath!, source.PanelizationReportSha256!, "source panelization report");
            }

            IReadOnlyDictionary<string, string> productionAfter = VerifyProductionSourceBindings();
            IReadOnlyDictionary<string, string> toolAfter = VerifyToolSourceBindings();
            IReadOnlyDictionary<string, string> panelizerRuntimeAfter = VerifyPanelizerRuntimeBindings(context);
            IReadOnlyDictionary<string, string> toolRuntimeAfter = VerifyToolRuntimeBindings();
            EnsureEqual(productionBefore, productionAfter, "production source bundle");
            EnsureEqual(toolBefore, toolAfter, "exporter source bundle");
            EnsureEqual(panelizerRuntimeBefore, panelizerRuntimeAfter, "panelizer runtime bundle");
            EnsureEqual(toolRuntimeBefore, toolRuntimeAfter, "exporter runtime bundle");

            AxisCandidatePanelEvidenceRuntime runtime = CreateRuntime(
                productionAfter,
                panelizerRuntimeAfter,
                toolAfter,
                toolRuntimeAfter);
            string status = cancelled
                ? "cancelled"
                : panelResults.Any(static result => result.Status == "failed")
                    ? "completed_with_failures"
                    : "success";
            AxisCandidatePanelBatchFailure? failure = cancelled
                ? new AxisCandidatePanelBatchFailure(
                    "cancelled",
                    "REQUEST_CANCELLED",
                    typeof(OperationCanceledException).FullName ?? nameof(OperationCanceledException),
                    null)
                : null;
            return CreateReport(context, panelResults, runtime, status, failure);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(context, "cancelled", "REQUEST_CANCELLED", exception.GetType(), null);
        }
        catch (PanelEvidenceException exception)
        {
            return Failure(context, "input_or_provenance_failure", exception.Code, exception.ExceptionType, exception.Message);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Failure(context, "processing_failure", null, exception.GetType(), exception.ToString());
        }
    }

    private void ValidateBinding()
    {
        bool validScope =
            (_binding.Scope == "public_synthetic_train" && _binding.Split == "train") ||
            (_binding.Scope == "public_synthetic_development" && _binding.Split == "dev") ||
            (_binding.Scope == "synthetic_test_fixture" && _binding.Split == "test-fixture");
        if (_binding.Schema != InputSchema || !validScope ||
            !IsHash(_binding.PanelizationAdmissionBindingSha256) ||
            !IsHash(_binding.PanelizationReceiptSha256) ||
            _binding.ExpectedSourceCount <= 0 || _binding.ExpectedPanelCount < 0 ||
            _binding.ExpectedDetectedSourceCount < 0 || _binding.ExpectedFallbackSourceCount < 0 ||
            _binding.ExpectedFailedSourceCount < 0 ||
            _binding.ExpectedDetectedSourceCount + _binding.ExpectedFallbackSourceCount + _binding.ExpectedFailedSourceCount != _binding.ExpectedSourceCount ||
            _binding.SelectedPanels is null ||
            _binding.ProductionSourceBindings is null || _binding.PanelizerRuntimeBindings is null ||
            _binding.ToolSourceBindings is null || _binding.ToolRuntimeBindings is null ||
            _binding.OpenCvOptions is null || _binding.GeometryOptions is null ||
            !ExactKeys(
                _binding.ProductionSourceBindings.Keys,
                _binding.Scope == "public_synthetic_development"
                    ? RequiredProductionSourcePaths.Append(ProductionAxisAdapterSourcePath)
                    : RequiredProductionSourcePaths) ||
            !ExactKeys(_binding.ToolSourceBindings.Keys, ToolSourcePaths) ||
            _binding.PanelizerRuntimeBindings.Count < RequiredPanelizerRuntimePaths.Length ||
            _binding.PanelizerRuntimeBindings.Count > 200 ||
            _binding.SelectedPanels.Select(static item => (item.SourceId, item.PanelId)).Distinct().Count() != _binding.SelectedPanels.Count)
        {
            throw new PanelEvidenceException("PANEL_INPUT_MANIFEST_SCHEMA_INVALID", typeof(InvalidDataException));
        }

        if (_binding.OpenCvOptions != ProductionOpenCvOptions || _binding.GeometryOptions != ProductionGeometryOptions)
        {
            throw new PanelEvidenceException("NON_PRODUCTION_AXIS_OPTIONS_REJECTED", typeof(InvalidDataException));
        }

        if (_binding.Scope == "public_synthetic_development")
        {
            if (_binding.DevCorpusManifestPath != DevCorpusManifestPath ||
                _binding.DevCorpusManifestSha256 != DevCorpusManifestSha256 ||
                _binding.PanelizationAdmissionBindingPath != DevAdmissionPath ||
                _binding.PanelizationAdmissionBindingSha256 != DevAdmissionSha256 ||
                _binding.PanelizationReceiptPath != DevReceiptPath ||
                _binding.PanelizationReceiptSha256 != DevReceiptSha256 ||
                _binding.PanelizationRunRoot != DevPanelizationRunRoot ||
                _binding.ExpectedSourceCount != 32 || _binding.ExpectedDetectedSourceCount != 30 ||
                _binding.ExpectedFallbackSourceCount != 2 || _binding.ExpectedFailedSourceCount != 0 ||
                _binding.ExpectedPanelCount != 48 || _binding.SelectedPanels.Count != 0)
            {
                throw new PanelEvidenceException("DEV_PANEL_INPUT_IDENTITY_REJECTED", typeof(InvalidDataException));
            }
        }
        else if (_binding.DevCorpusManifestPath is not null || _binding.DevCorpusManifestSha256 is not null)
        {
            throw new PanelEvidenceException("DEV_MANIFEST_ON_NON_DEV_INPUT_REJECTED", typeof(InvalidDataException));
        }

        _ = ResolveProjectPath(_binding.PanelizationAdmissionBindingPath, _projectRoot);
        _ = ResolveProjectPath(_binding.PanelizationReceiptPath, _projectRoot);
        bool fixtureScope = _binding.Scope == "synthetic_test_fixture";
        string panelizationRoot = ResolveProjectPath(_binding.PanelizationRunRoot, _projectRoot, fixtureScope);
        string relativeRoot = Path.GetRelativePath(_projectRoot, panelizationRoot).Replace('\\', '/');
        if (!relativeRoot.StartsWith("artifacts/goal22-runs/", StringComparison.Ordinal) ||
            _binding.ExpectedSourceCount > 1024 || _binding.ExpectedPanelCount > 4096 ||
            _binding.SelectedPanels.Count > _binding.ExpectedPanelCount)
        {
            throw new PanelEvidenceException("PANEL_INPUT_SCOPE_REJECTED", typeof(InvalidDataException));
        }

        foreach ((string path, string hash) in _binding.ProductionSourceBindings
            .Concat(_binding.PanelizerRuntimeBindings)
            .Concat(_binding.ToolSourceBindings)
            .Concat(_binding.ToolRuntimeBindings))
        {
            if (!IsHash(hash))
            {
                throw new PanelEvidenceException("PANEL_INPUT_HASH_INVALID", typeof(InvalidDataException), path);
            }
            _ = ResolveProjectPath(path, _projectRoot, fixtureScope);
        }

    }

    private PanelBatchContext LoadAndVerifyPanelBatchInputs()
    {
        if (_binding.Scope == "public_synthetic_development")
        {
            return LoadAndVerifyDevPanelBatchInputs();
        }

        IReadOnlyList<AxisCandidatePanelSelection> selectionItems = _binding.SelectedPanels ??
            throw new PanelEvidenceException("SELECTED_PANEL_LIST_MISSING", typeof(InvalidDataException));
        string admissionBindingPath = ResolveProjectPath(_binding.PanelizationAdmissionBindingPath);
        byte[] admissionBytes = Program.ReadLocked(admissionBindingPath);
        EnsureHash(admissionBytes, _binding.PanelizationAdmissionBindingSha256, "panelization admission binding");
        using JsonDocument admission = JsonDocument.Parse(admissionBytes);
        JsonElement admissionRoot = admission.RootElement;
        if (RequiredString(admissionRoot, "schema") != "graphreader.axis-ranker-panelization-binding.v2" ||
            admissionRoot.GetProperty("annotations_read").GetBoolean() ||
            admissionRoot.GetProperty("dev_pixels_read").GetBoolean() ||
            admissionRoot.GetProperty("reserved_read").GetBoolean() ||
            admissionRoot.GetProperty("training_admitted").GetBoolean() ||
            admissionRoot.GetProperty("optimizer_steps").GetInt32() != 0 ||
            admissionRoot.GetProperty("sources").GetArrayLength() != _binding.ExpectedSourceCount)
        {
            throw new PanelEvidenceException("PANELIZATION_ADMISSION_BINDING_REJECTED", typeof(InvalidDataException));
        }

        Dictionary<string, SourceInventoryItem> inventory = ReadSourceInventory(admissionRoot);
        SortedDictionary<string, string> panelizerRuntime = ReadPanelizerRuntimeBindings(admissionRoot);

        string receiptPath = ResolveProjectPath(_binding.PanelizationReceiptPath);
        byte[] receiptBytes = Program.ReadLocked(receiptPath);
        EnsureHash(receiptBytes, _binding.PanelizationReceiptSha256, "panelization receipt");
        using JsonDocument receipt = JsonDocument.Parse(receiptBytes);
        JsonElement receiptRoot = receipt.RootElement;
        if (RequiredString(receiptRoot, "schema") != "graphreader.axis-ranker-training-panelization-receipt.v2" ||
            RequiredHash(receiptRoot, "binding_sha256") != _binding.PanelizationAdmissionBindingSha256 ||
            receiptRoot.GetProperty("truth_read").GetBoolean() ||
            receiptRoot.GetProperty("dev_read").GetBoolean() ||
            receiptRoot.GetProperty("reserved_read").GetBoolean() ||
            receiptRoot.GetProperty("training_admitted").GetBoolean() ||
            receiptRoot.GetProperty("optimizer_steps").GetInt32() != 0 ||
            receiptRoot.GetProperty("sources").GetArrayLength() != _binding.ExpectedSourceCount)
        {
            throw new PanelEvidenceException("PANELIZATION_RECEIPT_REJECTED", typeof(InvalidDataException));
        }

        var sources = new List<SourceWorkItem>(_binding.ExpectedSourceCount);
        var allPanels = new List<PanelWorkItem>(_binding.ExpectedPanelCount);
        int detectedCount = 0;
        int fallbackCount = 0;
        int failedCount = 0;
        int panelCount = 0;
        var seenSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement receiptSource in receiptRoot.GetProperty("sources").EnumerateArray())
        {
            string sourceId = RequiredGuid(receiptSource, "id");
            string sourceHash = RequiredHash(receiptSource, "source_sha256");
            if (!seenSourceIds.Add(sourceId) || !inventory.TryGetValue(sourceId, out SourceInventoryItem? source) ||
                !string.Equals(source.Sha256, sourceHash, StringComparison.Ordinal))
            {
                throw new PanelEvidenceException("SOURCE_INVENTORY_RECEIPT_MISMATCH", typeof(InvalidDataException), sourceId);
            }

            string status = RequiredString(receiptSource, "source_status");
            string processStatus = RequiredString(receiptSource, "process_status");
            int exitCode = receiptSource.GetProperty("exit_code").GetInt32();
            JsonElement receiptPanelRows = receiptSource.GetProperty("panels");
            int declaredPanelCount = receiptPanelRows.GetArrayLength();
            string? panelizationSha = OptionalString(receiptSource, "panelization_sha256");
            IReadOnlyList<string> sourceWarnings = ReadStringArray(receiptSource.GetProperty("warnings"));
            if (declaredPanelCount < 0)
            {
                throw new PanelEvidenceException("PANEL_COUNT_INVALID", typeof(InvalidDataException), sourceId);
            }

            if (status == "failed")
            {
                failedCount++;
                if (processStatus != "failed" || exitCode == 0 || declaredPanelCount != 0 ||
                    declaredPanelCount != 0)
                {
                    throw new PanelEvidenceException("FAILED_SOURCE_ACCOUNTING_INVALID", typeof(InvalidDataException), sourceId);
                }

                sources.Add(new SourceWorkItem(source, status, sourceWarnings, null, null, [], OptionalString(receiptSource, "exception_type")));
                continue;
            }

            if (status is not ("detected" or "no_panel_fallback") || processStatus != "completed" || exitCode != 0 ||
                string.IsNullOrWhiteSpace(panelizationSha) || !IsHash(panelizationSha))
            {
                throw new PanelEvidenceException("SOURCE_STATUS_INVALID", typeof(InvalidDataException), sourceId);
            }

            if (status == "detected")
            {
                detectedCount++;
            }
            else
            {
                fallbackCount++;
            }

            string reportRelative = Path.Combine(_binding.PanelizationRunRoot, sourceId, "panelization", "panelization.json").Replace('\\', '/');
            string panelReportPath = ResolveProjectPath(reportRelative);
            byte[] reportBytes = Program.ReadLocked(panelReportPath);
            EnsureHash(reportBytes, panelizationSha, "per-source panelization report");
            List<PanelWorkItem> sourcePanels = ReadAndCrossCheckPanelizationReport(
                reportBytes,
                Path.GetDirectoryName(panelReportPath)!,
                source,
                sourceId,
                sourceHash,
                status,
                declaredPanelCount,
                receiptSource.GetProperty("panels"));
            panelCount += sourcePanels.Count;
            sources.Add(new SourceWorkItem(source, status, sourceWarnings, panelReportPath, panelizationSha, sourcePanels, null));
            allPanels.AddRange(sourcePanels);
        }

        if (seenSourceIds.Count != inventory.Count || inventory.Count != _binding.ExpectedSourceCount ||
            detectedCount != _binding.ExpectedDetectedSourceCount ||
            fallbackCount != _binding.ExpectedFallbackSourceCount ||
            failedCount != _binding.ExpectedFailedSourceCount ||
            panelCount != _binding.ExpectedPanelCount ||
            seenSourceIds.Count != receiptRoot.GetProperty("sources").GetArrayLength())
        {
            throw new PanelEvidenceException("PANELIZATION_DENOMINATOR_MISMATCH", typeof(InvalidDataException));
        }

        var allByKey = allPanels.ToDictionary(static item => (item.Source.SourceId, item.Panel.PanelId));
        List<PanelWorkItem> selected;
        if (selectionItems.Count == 0)
        {
            selected = allPanels;
        }
        else
        {
            selected = new List<PanelWorkItem>(selectionItems.Count);
            foreach (AxisCandidatePanelSelection selection in selectionItems)
            {
                if (!allByKey.TryGetValue((selection.SourceId, selection.PanelId), out PanelWorkItem? panel))
                {
                    throw new PanelEvidenceException("SELECTED_PANEL_NOT_IN_RECEIPT", typeof(InvalidDataException), selection.PanelId);
                }
                selected.Add(panel);
            }
        }

        return new PanelBatchContext(
            admissionBindingPath,
            receiptPath,
            sources,
            selected,
            panelizerRuntime,
            inventory.Count,
            detectedCount,
            fallbackCount,
            failedCount,
            panelCount);
    }

    private PanelBatchContext LoadAndVerifyDevPanelBatchInputs()
    {
        string admissionBindingPath = ResolveProjectPath(DevAdmissionPath);
        byte[] admissionBytes = Program.ReadLocked(admissionBindingPath);
        EnsureHash(admissionBytes, DevAdmissionSha256, "fixed public dev admission binding");
        using JsonDocument admission = JsonDocument.Parse(admissionBytes);
        JsonElement admissionRoot = admission.RootElement;
        if (RequiredString(admissionRoot, "schema") != "graphreader.axis-roles-dev-measure-binding.v2" ||
            RequiredHash(admissionRoot, "corpus_manifest_sha256") != DevCorpusManifestSha256 ||
            admissionRoot.GetProperty("source_count").GetInt32() != 32 ||
            admissionRoot.GetProperty("reserved_read").GetBoolean() ||
            admissionRoot.GetProperty("truth_in_prediction").GetBoolean() ||
            admissionRoot.GetProperty("production_approved").GetBoolean() ||
            admissionRoot.GetProperty("optimizer_steps").GetInt32() != 0)
        {
            throw new PanelEvidenceException("DEV_ADMISSION_BINDING_REJECTED", typeof(InvalidDataException));
        }

        string manifestPath = ResolveProjectPath(DevCorpusManifestPath);
        byte[] manifestBytes = Program.ReadLocked(manifestPath);
        EnsureHash(manifestBytes, DevCorpusManifestSha256, "fixed public dev corpus manifest");
        using JsonDocument manifest = JsonDocument.Parse(manifestBytes);
        JsonElement manifestRoot = manifest.RootElement;
        if (manifestRoot.GetProperty("dev_cases").GetArrayLength() != 32 ||
            manifestRoot.GetProperty("private_reads").GetInt32() != 0 ||
            manifestRoot.GetProperty("sealed_reads").GetInt32() != 0 ||
            manifestRoot.GetProperty("reserved_pixels_accessed").GetBoolean() ||
            manifestRoot.GetProperty("training_approved").GetBoolean())
        {
            throw new PanelEvidenceException("DEV_CORPUS_MANIFEST_REJECTED", typeof(InvalidDataException));
        }

        string inputsPath = ResolveProjectPath(DevInputsPath);
        byte[] inputsBytes = Program.ReadLocked(inputsPath);
        EnsureHash(inputsBytes, DevInputsSha256, "fixed public dev source inventory");
        EnsureHash(inputsBytes, RequiredHash(admissionRoot, "inputs_sha256"), "dev source inventory binding");
        using JsonDocument inputs = JsonDocument.Parse(inputsBytes);
        JsonElement inputsRoot = inputs.RootElement;
        if (RequiredString(inputsRoot, "schema") != "graphreader.axis-roles-dev-source-input.v1" ||
            inputsRoot.GetProperty("sources").GetArrayLength() != 32)
        {
            throw new PanelEvidenceException("DEV_SOURCE_INVENTORY_REJECTED", typeof(InvalidDataException));
        }

        var manifestCases = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in manifestRoot.GetProperty("dev_cases").EnumerateArray())
        {
            string id = RequiredGuid(item, "scene_id");
            if (!manifestCases.TryAdd(id, item))
            {
                throw new PanelEvidenceException("DEV_SCENE_ID_DUPLICATE", typeof(InvalidDataException), id);
            }
        }

        var inventory = new Dictionary<string, SourceInventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in inputsRoot.GetProperty("sources").EnumerateArray())
        {
            string id = RequiredGuid(item, "id");
            string path = RequiredRelativePath(item, "path");
            string sha = RequiredHash(item, "sha256");
            int width = item.GetProperty("width").GetInt32();
            int height = item.GetProperty("height").GetInt32();
            if (!manifestCases.TryGetValue(id, out JsonElement manifestCase))
            {
                throw new PanelEvidenceException("DEV_SOURCE_NOT_IN_MANIFEST", typeof(InvalidDataException), id);
            }
            int caseIndex = manifestCase.GetProperty("index").GetInt32();
            string expectedSourcePath = $"artifacts/axis-roles-dev-corpus-v3-20261031/images/{caseIndex}.png";
            if (path != expectedSourcePath || sha != RequiredHash(manifestCase, "image_sha256") ||
                width <= 0 || height <= 0 || !inventory.TryAdd(id, new SourceInventoryItem(id, path, sha, width, height)))
            {
                throw new PanelEvidenceException("DEV_SOURCE_ROW_MISMATCH", typeof(InvalidDataException), id);
            }
            string sourcePath = ResolveProjectPath(path);
            if (HashPath(sourcePath) != sha)
            {
                throw new PanelEvidenceException("DEV_SOURCE_IMAGE_HASH_MISMATCH", typeof(InvalidDataException), id);
            }
        }
        if (inventory.Count != 32 || manifestCases.Keys.Except(inventory.Keys, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new PanelEvidenceException("DEV_SOURCE_DENOMINATOR_MISMATCH", typeof(InvalidDataException));
        }

        SortedDictionary<string, string> panelizerRuntime = ReadPanelizerRuntimeBindings(admissionRoot);
        string receiptPath = ResolveProjectPath(DevReceiptPath);
        byte[] receiptBytes = Program.ReadLocked(receiptPath);
        EnsureHash(receiptBytes, DevReceiptSha256, "fixed truth-free dev panelization receipt");
        using JsonDocument receipt = JsonDocument.Parse(receiptBytes);
        JsonElement receiptRoot = receipt.RootElement;
        if (RequiredString(receiptRoot, "schema") != "graphreader.axis-roles-dev-truth-free-receipt.v2" ||
            RequiredHash(receiptRoot, "binding_sha256") != DevAdmissionSha256 ||
            receiptRoot.GetProperty("truth_read").GetBoolean() ||
            receiptRoot.GetProperty("reserved_read").GetBoolean() ||
            receiptRoot.GetProperty("sources").GetArrayLength() != 32)
        {
            throw new PanelEvidenceException("DEV_PANELIZATION_RECEIPT_REJECTED", typeof(InvalidDataException));
        }

        var sources = new List<SourceWorkItem>(32);
        var allPanels = new List<PanelWorkItem>(48);
        var seenSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int detectedCount = 0;
        int fallbackCount = 0;
        int failedCount = 0;
        int panelCount = 0;
        foreach (JsonElement receiptSource in receiptRoot.GetProperty("sources").EnumerateArray())
        {
            string sourceId = RequiredGuid(receiptSource, "id");
            if (!seenSourceIds.Add(sourceId) || !inventory.TryGetValue(sourceId, out SourceInventoryItem? source))
            {
                throw new PanelEvidenceException("DEV_RECEIPT_SOURCE_MISMATCH", typeof(InvalidDataException), sourceId);
            }
            JsonElement panelization = receiptSource.GetProperty("panelization");
            string status = RequiredString(panelization, "source_status");
            string processStatus = RequiredString(panelization, "process_status");
            int exitCode = panelization.GetProperty("exit_code").GetInt32();
            int declaredPanelCount = panelization.GetProperty("panel_count").GetInt32();
            JsonElement receiptPanels = receiptSource.GetProperty("panels");
            if (declaredPanelCount != receiptPanels.GetArrayLength())
            {
                throw new PanelEvidenceException("DEV_RECEIPT_PANEL_COUNT_MISMATCH", typeof(InvalidDataException), sourceId);
            }
            IReadOnlyList<string> sourceWarnings = ReadStringArray(panelization.GetProperty("warnings"));
            if (status == "failed")
            {
                failedCount++;
                if (processStatus != "failed" || exitCode == 0 || declaredPanelCount != 0)
                {
                    throw new PanelEvidenceException("DEV_FAILED_SOURCE_ACCOUNTING_INVALID", typeof(InvalidDataException), sourceId);
                }
                sources.Add(new SourceWorkItem(source, status, sourceWarnings, null, null, [], null));
                continue;
            }
            if (status is not ("detected" or "no_panel_fallback") || processStatus != "completed" || exitCode != 0 ||
                declaredPanelCount < 1 || !panelization.GetProperty("success").GetBoolean())
            {
                throw new PanelEvidenceException("DEV_SOURCE_STATUS_INVALID", typeof(InvalidDataException), sourceId);
            }
            if (status == "detected") detectedCount++; else fallbackCount++;
            string panelizationSha = RequiredHash(panelization, "panelization_sha256");
            string reportRelative = $"{DevPanelizationRunRoot}/{sourceId}/panelization/panelization.json";
            string reportPath = ResolveProjectPath(reportRelative);
            byte[] reportBytes = Program.ReadLocked(reportPath);
            EnsureHash(reportBytes, panelizationSha, "dev per-source panelization report");
            using JsonDocument panelReport = JsonDocument.Parse(reportBytes);
            JsonElement reportSource = panelReport.RootElement.GetProperty("sources")[0];
            JsonElement reportPanels = reportSource.GetProperty("panels");
            if (reportPanels.GetArrayLength() != receiptPanels.GetArrayLength())
            {
                throw new PanelEvidenceException("DEV_PANEL_SUMMARY_COUNT_MISMATCH", typeof(InvalidDataException), sourceId);
            }
            for (int index = 0; index < reportPanels.GetArrayLength(); index++)
            {
                JsonElement reported = reportPanels[index];
                JsonElement summarized = receiptPanels[index];
                if (RequiredGuid(reported, "panel_id") != RequiredGuid(summarized, "panel_id") ||
                    RequiredHash(reported, "sha256") != RequiredHash(summarized, "panel_sha256"))
                {
                    throw new PanelEvidenceException("DEV_PANEL_SUMMARY_IDENTITY_MISMATCH", typeof(InvalidDataException), sourceId);
                }
            }
            List<PanelWorkItem> sourcePanels = ReadAndCrossCheckPanelizationReport(
                reportBytes,
                Path.GetDirectoryName(reportPath)!,
                source,
                sourceId,
                source.Sha256,
                status,
                declaredPanelCount,
                reportPanels);
            panelCount += sourcePanels.Count;
            sources.Add(new SourceWorkItem(source, status, sourceWarnings, reportPath, panelizationSha, sourcePanels, null));
            allPanels.AddRange(sourcePanels);
        }

        if (seenSourceIds.Count != 32 || inventory.Count != 32 || detectedCount != 30 || fallbackCount != 2 ||
            failedCount != 0 || panelCount != 48 || allPanels.Count != 48)
        {
            throw new PanelEvidenceException("DEV_PANELIZATION_DENOMINATOR_MISMATCH", typeof(InvalidDataException));
        }
        if (_binding.SelectedPanels is null || _binding.SelectedPanels.Count != 0)
        {
            throw new PanelEvidenceException("DEV_PANEL_SELECTION_MUST_BE_ALL", typeof(InvalidDataException));
        }

        return new PanelBatchContext(
            admissionBindingPath,
            receiptPath,
            sources,
            allPanels,
            panelizerRuntime,
            inventory.Count,
            detectedCount,
            fallbackCount,
            failedCount,
            panelCount,
            [new BoundPath(manifestPath, DevCorpusManifestSha256, "public dev corpus manifest"),
             new BoundPath(inputsPath, DevInputsSha256, "public dev source inventory")]);
    }

    private Dictionary<string, SourceInventoryItem> ReadSourceInventory(JsonElement admissionRoot)
    {
        var result = new Dictionary<string, SourceInventoryItem>(StringComparer.OrdinalIgnoreCase);
        JsonElement sources = admissionRoot.GetProperty("sources");
        if (sources.GetArrayLength() != _binding.ExpectedSourceCount)
        {
            throw new PanelEvidenceException("SOURCE_INVENTORY_COUNT_INVALID", typeof(InvalidDataException));
        }

        foreach (JsonElement item in sources.EnumerateArray())
        {
            string id = RequiredGuid(item, "id");
            string path = RequiredRelativePath(item, "path");
            string sha = RequiredHash(item, "sha256");
            int width = item.GetProperty("width").GetInt32();
            int height = item.GetProperty("height").GetInt32();
            bool allowedSourceRoot = _binding.Scope == "public_synthetic_train"
                ? path.StartsWith("artifacts/axis-roles-training-corpus-v1-20261031/images/", StringComparison.Ordinal)
                : path.StartsWith("artifacts/goal22-runs/axis-candidate-evidence-test-fixtures/", StringComparison.Ordinal);
            if (width <= 0 || height <= 0 ||
                !allowedSourceRoot ||
                !result.TryAdd(id, new SourceInventoryItem(id, path, sha, width, height)))
            {
                throw new PanelEvidenceException("SOURCE_INVENTORY_ROW_INVALID", typeof(InvalidDataException), id);
            }

            string sourcePath = ResolveProjectPath(path);
            if (HashPath(sourcePath) != sha)
            {
                throw new PanelEvidenceException("SOURCE_INVENTORY_IMAGE_HASH_MISMATCH", typeof(InvalidDataException), id);
            }
        }
        return result;
    }

    private SortedDictionary<string, string> ReadPanelizerRuntimeBindings(JsonElement admissionRoot)
    {
        bool isPublicDev = _binding.Scope == "public_synthetic_development";
        if (isPublicDev)
        {
            VerifyDevAdmissionSourceFiles(admissionRoot);
        }

        JsonElement files = admissionRoot.GetProperty(isPublicDev ? "runtime_files" : "files");
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (files.ValueKind != JsonValueKind.Object || files.EnumerateObject().Count() > 200)
        {
            throw new PanelEvidenceException("PANELIZER_RUNTIME_FILE_MAP_INVALID", typeof(InvalidDataException));
        }

        JsonProperty[] panelizerFiles = files.EnumerateObject()
            .Where(file => !isPublicDev || file.Name.StartsWith(DevPanelizerRuntimePrefix, StringComparison.Ordinal))
            .ToArray();
        if (isPublicDev && panelizerFiles.Length != DevPanelizerRuntimeFileCount)
        {
            throw new PanelEvidenceException("DEV_PANELIZER_RUNTIME_FILE_COUNT_INVALID", typeof(InvalidDataException));
        }

        foreach (JsonProperty file in panelizerFiles)
        {
            string path = file.Name;
            if (!IsPanelizerRuntimePathAllowed(path, _binding.Scope))
            {
                throw new PanelEvidenceException("PANELIZER_RUNTIME_PATH_SCOPE_REJECTED", typeof(InvalidDataException), path);
            }

            string expected = file.Value.GetString() ?? string.Empty;
            if (!IsHash(expected))
            {
                throw new PanelEvidenceException("PANELIZER_RUNTIME_HASH_INVALID", typeof(InvalidDataException), path);
            }

            string fullPath = ResolveProjectPath(path);
            string observed = HashPath(fullPath);
            if (!string.Equals(observed, expected, StringComparison.Ordinal))
            {
                throw new PanelEvidenceException("PANELIZER_RUNTIME_HASH_MISMATCH", typeof(InvalidDataException), path);
            }
            actual.Add(path, observed);
        }

        if (RequiredPanelizerRuntimePaths.Any(path => !actual.ContainsKey(path)) ||
            !ExactKeys(_binding.PanelizerRuntimeBindings.Keys, actual.Keys) ||
            !DictionaryEquals(actual, _binding.PanelizerRuntimeBindings))
        {
            throw new PanelEvidenceException("PANELIZER_RUNTIME_INPUT_PIN_MISMATCH", typeof(InvalidDataException));
        }
        return actual;
    }

    private void VerifyDevAdmissionSourceFiles(JsonElement admissionRoot)
    {
        JsonElement files = admissionRoot.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Object || files.EnumerateObject().Count() != DevAdmissionSourcePaths.Length)
        {
            throw new PanelEvidenceException("DEV_ADMISSION_SOURCE_MAP_INVALID", typeof(InvalidDataException));
        }

        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty file in files.EnumerateObject())
        {
            string path = file.Name;
            if (!DevAdmissionSourcePaths.Contains(path, StringComparer.Ordinal) ||
                !(DevOnlyPanelizerRuntimeSourcePaths.Contains(path, StringComparer.Ordinal) ||
                  AdditionalPanelizerSourcePaths.Contains(path, StringComparer.Ordinal) ||
                  RequiredProductionSourcePaths.Contains(path, StringComparer.Ordinal)))
            {
                throw new PanelEvidenceException("DEV_ADMISSION_SOURCE_PATH_SCOPE_REJECTED", typeof(InvalidDataException), path);
            }

            string expected = file.Value.GetString() ?? string.Empty;
            if (!IsHash(expected))
            {
                throw new PanelEvidenceException("DEV_ADMISSION_SOURCE_HASH_INVALID", typeof(InvalidDataException), path);
            }

            string fullPath = ResolveProjectPath(path);
            Program.EnsureNoReparsePoints(fullPath, _projectRoot, allowMissing: false);
            string observed = HashPath(fullPath);
            if (!string.Equals(observed, expected, StringComparison.Ordinal))
            {
                throw new PanelEvidenceException("DEV_ADMISSION_SOURCE_HASH_MISMATCH", typeof(InvalidDataException), path);
            }
            actual.Add(path, observed);
        }

        if (!ExactKeys(DevAdmissionSourcePaths, actual.Keys))
        {
            throw new PanelEvidenceException("DEV_ADMISSION_SOURCE_MAP_INCOMPLETE", typeof(InvalidDataException));
        }
    }

    private static bool IsPanelizerRuntimePathAllowed(string path, string scope) =>
        path.StartsWith("artifacts/goal22-runs/axis-roles-panelization-build-v2/", StringComparison.Ordinal) ||
        AdditionalPanelizerSourcePaths.Contains(path, StringComparer.Ordinal) ||
        RequiredProductionSourcePaths.Contains(path, StringComparer.Ordinal) ||
        (scope == "public_synthetic_development" && DevOnlyPanelizerRuntimeSourcePaths.Contains(path, StringComparer.Ordinal));

    private List<PanelWorkItem> ReadAndCrossCheckPanelizationReport(
        byte[] reportBytes,
        string reportDirectory,
        SourceInventoryItem source,
        string sourceId,
        string sourceHash,
        string expectedStatus,
        int declaredPanelCount,
        JsonElement receiptPanels)
    {
        using JsonDocument report = JsonDocument.Parse(reportBytes);
        JsonElement root = report.RootElement;
        if (RequiredString(root, "schema") != "graphreader.public-axis-roles-production-panelization.v1" ||
            root.GetProperty("truth_read").GetBoolean() || root.GetProperty("sources").GetArrayLength() != 1)
        {
            throw new PanelEvidenceException("PANELIZATION_REPORT_SCHEMA_REJECTED", typeof(InvalidDataException), sourceId);
        }

        JsonElement reportSource = root.GetProperty("sources")[0];
        if (RequiredGuid(reportSource, "id") != sourceId ||
            RequiredHash(reportSource, "source_sha256") != sourceHash ||
            RequiredString(reportSource, "status") != expectedStatus ||
            reportSource.GetProperty("panel_count").GetInt32() != declaredPanelCount ||
            reportSource.GetProperty("panels").GetArrayLength() != declaredPanelCount ||
            receiptPanels.GetArrayLength() != declaredPanelCount)
        {
            throw new PanelEvidenceException("PANELIZATION_REPORT_RECEIPT_MISMATCH", typeof(InvalidDataException), sourceId);
        }

        var result = new List<PanelWorkItem>(declaredPanelCount);
        JsonElement reportPanels = reportSource.GetProperty("panels");
        for (int index = 0; index < reportPanels.GetArrayLength(); index++)
        {
            AxisCandidatePanelEvidenceRecord parsed = ParsePanel(reportPanels[index], reportDirectory, source, expectedStatus);
            AxisCandidatePanelEvidenceRecord receiptParsed = ParsePanel(receiptPanels[index], reportDirectory, source, expectedStatus);
            if (!PanelRecordsEqual(parsed, receiptParsed))
            {
                throw new PanelEvidenceException("PANELIZATION_PANEL_ROW_MISMATCH", typeof(InvalidDataException), parsed.PanelId);
            }
            result.Add(new PanelWorkItem(source, expectedStatus, parsed));
        }
        return result;
    }

    private AxisCandidatePanelEvidenceRecord ParsePanel(
        JsonElement panel,
        string reportDirectory,
        SourceInventoryItem source,
        string sourceStatus)
    {
        string panelId = RequiredGuid(panel, "panel_id");
        string sha = RequiredHash(panel, "sha256");
        string relativeName = RequiredString(panel, "path");
        if (Path.IsPathRooted(relativeName) || relativeName.Contains('\\') ||
            Path.GetFileName(relativeName) != relativeName || relativeName is "." or "..")
        {
            throw new PanelEvidenceException("PANEL_IMAGE_PATH_INVALID", typeof(InvalidDataException), panelId);
        }

        int width = panel.GetProperty("width").GetInt32();
        int height = panel.GetProperty("height").GetInt32();
        AxisCandidatePanelRect requested = ReadRect(panel.GetProperty("requested_crop"));
        AxisCandidatePanelRect encoded = ReadRect(panel.GetProperty("encoded_crop"));
        double[] sourceToPanel = ReadMatrix(panel.GetProperty("source_to_panel"));
        double[] panelToSource = ReadMatrix(panel.GetProperty("panel_to_source"));
        ValidateCropAndTransforms(source, width, height, requested, encoded, sourceToPanel, panelToSource, panelId);

        string reportRelative = Path.GetRelativePath(_projectRoot, reportDirectory).Replace('\\', '/');
        string panelRelative = $"{reportRelative}/{relativeName}";
        string panelPath = ResolveProjectPath(panelRelative);
        if (HashPath(panelPath) != sha)
        {
            throw new PanelEvidenceException("PANEL_IMAGE_HASH_MISMATCH", typeof(InvalidDataException), panelId);
        }
        IReadOnlyList<string> warnings = ReadStringArray(panel.GetProperty("warnings"));
        string origin = sourceStatus == "no_panel_fallback" ? "full_image_review_fallback" : "detected_panel";
        return new AxisCandidatePanelEvidenceRecord(
            source.SourceId,
            source.Path,
            source.Sha256,
            source.Width,
            source.Height,
            panelId,
            origin,
            panelPath,
            panelRelative,
            sha,
            width,
            height,
            requested,
            encoded,
            Array.AsReadOnly(sourceToPanel),
            Array.AsReadOnly(panelToSource),
            warnings);
    }

    private static bool PanelRecordsEqual(AxisCandidatePanelEvidenceRecord left, AxisCandidatePanelEvidenceRecord right) =>
        left.PanelId == right.PanelId && left.PanelSha256 == right.PanelSha256 &&
        left.PanelRelativePath == right.PanelRelativePath && left.Width == right.Width && left.Height == right.Height &&
        left.RequestedCrop == right.RequestedCrop && left.EncodedCrop == right.EncodedCrop &&
        left.SourceToPanelMatrix.SequenceEqual(right.SourceToPanelMatrix) &&
        left.PanelToSourceMatrix.SequenceEqual(right.PanelToSourceMatrix) &&
        left.PanelizationWarnings.SequenceEqual(right.PanelizationWarnings);

    private static AxisCandidatePanelRect ReadRect(JsonElement element)
    {
        var rect = new AxisCandidatePanelRect(
            RequiredFinite(element, "X"),
            RequiredFinite(element, "Y"),
            RequiredFinite(element, "Width"),
            RequiredFinite(element, "Height"),
            RequiredFinite(element, "Right"),
            RequiredFinite(element, "Bottom"),
            RequiredFinite(element, "Area"),
            element.GetProperty("IsValid").GetBoolean());
        if (!rect.IsValid || rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 ||
            rect.Right != rect.X + rect.Width || rect.Bottom != rect.Y + rect.Height || rect.Area != rect.Width * rect.Height ||
            rect.X != Math.Floor(rect.X) || rect.Y != Math.Floor(rect.Y) ||
            rect.Width != Math.Floor(rect.Width) || rect.Height != Math.Floor(rect.Height))
        {
            throw new PanelEvidenceException("PANEL_CROP_INVALID", typeof(InvalidDataException));
        }
        return rect;
    }

    private static double[] ReadMatrix(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 9)
        {
            throw new PanelEvidenceException("PANEL_MATRIX_SHAPE_INVALID", typeof(InvalidDataException));
        }
        return element.EnumerateArray().Select(static value =>
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            {
                throw new PanelEvidenceException("PANEL_MATRIX_VALUE_INVALID", typeof(InvalidDataException));
            }
            return number;
        }).ToArray();
    }

    private static void ValidateCropAndTransforms(
        SourceInventoryItem source,
        int panelWidth,
        int panelHeight,
        AxisCandidatePanelRect requested,
        AxisCandidatePanelRect encoded,
        IReadOnlyList<double> sourceToPanel,
        IReadOnlyList<double> panelToSource,
        string panelId)
    {
        if (panelWidth <= 0 || panelHeight <= 0 || panelWidth != encoded.Width || panelHeight != encoded.Height ||
            encoded.Right > source.Width || encoded.Bottom > source.Height ||
            requested.X < encoded.X || requested.Y < encoded.Y ||
            requested.Right > encoded.Right || requested.Bottom > encoded.Bottom)
        {
            throw new PanelEvidenceException("PANEL_CROP_BOUNDS_INVALID", typeof(InvalidDataException), panelId);
        }
        double[] expectedForward = [1, 0, -encoded.X, 0, 1, -encoded.Y, 0, 0, 1];
        double[] expectedInverse = [1, 0, encoded.X, 0, 1, encoded.Y, 0, 0, 1];
        if (!sourceToPanel.SequenceEqual(expectedForward) || !panelToSource.SequenceEqual(expectedInverse))
        {
            throw new PanelEvidenceException("PANEL_TRANSFORM_NOT_PRODUCTION_CROP_TRANSLATION", typeof(InvalidDataException), panelId);
        }
    }

    private async Task<AxisCandidatePanelResult> ProcessPanelAsync(PanelWorkItem item, CancellationToken cancellationToken)
    {
        AxisCandidatePanelEvidenceRecord panel = item.Panel;
        string sourcePath = ResolveProjectPath(panel.SourceImagePath);
        (string sourceHash, long sourceLength) = HashPathWithLength(sourcePath);
        if (sourceHash != panel.SourceSha256)
        {
            throw new PanelEvidenceException("SOURCE_IMAGE_HASH_MISMATCH", typeof(InvalidDataException), panel.SourceImagePath);
        }

        byte[] panelBytes = Program.ReadLocked(panel.PanelImagePath);
        if (Hash(panelBytes) != panel.PanelSha256)
        {
            throw new PanelEvidenceException("PANEL_IMAGE_HASH_MISMATCH", typeof(InvalidDataException), panel.PanelRelativePath);
        }

        var image = new WorkflowImageEvidence(
            panel.PanelRelativePath,
            panel.PanelSha256,
            panel.Width,
            panel.Height,
            WorkflowImageVariant.Original);
        Guid sourceId = Guid.Parse(panel.SourceId);
        Guid panelId = Guid.Parse(panel.PanelId);
        var imported = new WorkflowImportedPanel(panelId, sourceId, Path.GetFileName(panel.PanelRelativePath), image);
        var prepared = new WorkflowPreparedPanel(imported, image, null);
        var request = new ProductionWorkflowDetectionRequest(
            prepared,
            image,
            WorkflowImageVariant.Original,
            panelId,
            sourceId,
            panelBytes);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoder = new ProductionRasterFrameDecoder();
            ProductionDecodedRaster decoded = decoder.Decode(request, cancellationToken);
            GrayscaleLineCandidateFrame frame = decoded.CreateAxisFrame();
            if (decoded.Width != panel.Width || decoded.Height != panel.Height ||
                decoded.InputSha256 != panel.PanelSha256 || frame.CoordinateSpace != AxisGeometryCoordinateSpaces.OriginalPixels ||
                frame.Stride != panel.Width || frame.Pixels.Length != checked(panel.Width * panel.Height))
            {
                throw new PanelEvidenceException("PRODUCTION_AXIS_FRAME_IDENTITY_MISMATCH", typeof(InvalidDataException), panel.PanelId);
            }

            IReadOnlyList<GeometryLineCandidate> candidates = await _candidateProvider
                .DetectLinesAsync(frame, cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count > 16384)
            {
                throw new PanelEvidenceException("CANDIDATE_COUNT_LIMIT_EXCEEDED", typeof(InvalidDataException), panel.PanelId);
            }

            var observer = new CandidateObserver();
            var axisRequest = new AxisGeometryRequest(
                frame.Width,
                frame.Height,
                candidates,
                _binding.GeometryOptions,
                AxisGeometryCoordinateSpaces.OriginalPixels)
            {
                CandidateObserver = observer,
            };
            _ = await _detector.DetectAsync(axisRequest, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AxisGeometryCandidateSnapshot> snapshots = observer.Candidates ??
                throw new PanelEvidenceException("RANKED_CANDIDATES_NOT_CAPTURED", typeof(InvalidDataException), panel.PanelId);
            AxisCandidateEvidenceFeatureMatrix features = FeatureNormalization.Build(panel.Width, panel.Height, snapshots);
            EnsureSameHash(sourcePath, panel.SourceSha256, "source image after detection");
            EnsureSameHash(panel.PanelImagePath, panel.PanelSha256, "panel image after detection");
            return CreatePanelResult(item, sourceLength, "success", snapshots, null, features);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            return CreatePanelResult(item, sourceLength, "cancelled", [],
                new AxisCandidateEvidenceFailure("cancelled", "REQUEST_CANCELLED", exception.GetType().FullName ?? exception.GetType().Name, null));
        }
        catch (AxisGeometryDetectionException exception)
        {
            return CreatePanelResult(item, sourceLength, "failed", [],
                new AxisCandidateEvidenceFailure("axis_geometry_failure", exception.Code, exception.GetType().FullName ?? exception.GetType().Name, null));
        }
        catch (ProductionWorkflowStageException exception)
        {
            return CreatePanelResult(item, sourceLength, "failed", [],
                new AxisCandidateEvidenceFailure("production_raster_decode_failure", exception.Failure.Code, exception.GetType().FullName ?? exception.GetType().Name, exception.Message));
        }
        catch (PanelEvidenceException)
        {
            throw;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return CreatePanelResult(item, sourceLength, "failed", [],
                new AxisCandidateEvidenceFailure("panel_processing_failure", null, exception.GetType().FullName ?? exception.GetType().Name, exception.Message));
        }
    }

    private static AxisCandidatePanelResult CreatePanelResult(
        PanelWorkItem item,
        long sourceLength,
        string status,
        IReadOnlyList<AxisGeometryCandidateSnapshot> candidates,
        AxisCandidateEvidenceFailure? failure,
        AxisCandidateEvidenceFeatureMatrix? features = null)
    {
        AxisCandidatePanelEvidenceRecord panel = item.Panel;
        return new AxisCandidatePanelResult(
            panel.SourceId,
            panel.SourceImagePath,
            panel.SourceSha256,
            sourceLength,
            panel.SourceWidth,
            panel.SourceHeight,
            panel.PanelId,
            panel.PanelOriginKind,
            panel.PanelRelativePath,
            panel.PanelSha256,
            new FileInfo(panel.PanelImagePath).Length,
            panel.Width,
            panel.Height,
            panel.RequestedCrop,
            panel.EncodedCrop,
            panel.SourceToPanelMatrix,
            panel.PanelToSourceMatrix,
            panel.PanelizationWarnings,
            "panel_original_pixels",
            status,
            candidates,
            features,
            failure);
    }

    private static AxisCandidatePanelResult CreateUnprocessedResult(PanelWorkItem item, string code, Type exceptionType) =>
        CreatePanelResult(item, 0, "not_processed", [],
            new AxisCandidateEvidenceFailure("cancelled_before_panel", code, exceptionType.FullName ?? exceptionType.Name, null));

    private SortedDictionary<string, string> VerifyProductionSourceBindings() =>
        VerifyBoundFiles(_binding.ProductionSourceBindings, "PRODUCTION_SOURCE_HASH_MISMATCH");

    private void VerifyPanelImageInventory(PanelBatchContext context)
    {
        foreach (SourceWorkItem source in context.Sources)
        {
            string sourcePath = ResolveProjectPath(source.Source.Path);
            EnsureSameHash(sourcePath, source.Source.Sha256, "source image");
            foreach (PanelWorkItem panel in source.Panels)
            {
                EnsureSameHash(panel.Panel.PanelImagePath, panel.Panel.PanelSha256, "panel image");
            }
        }
    }

    private SortedDictionary<string, string> VerifyToolSourceBindings() =>
        VerifyBoundFiles(_binding.ToolSourceBindings, "TOOL_SOURCE_HASH_MISMATCH");

    private SortedDictionary<string, string> VerifyPanelizerRuntimeBindings(PanelBatchContext context)
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string path, string expected) in context.PanelizerRuntimeBindings)
        {
            string hash = HashPath(ResolveProjectPath(path));
            if (hash != expected)
            {
                throw new PanelEvidenceException("PANELIZER_RUNTIME_HASH_MISMATCH", typeof(InvalidDataException), path);
            }
            actual.Add(path, hash);
        }
        return actual;
    }

    private SortedDictionary<string, string> VerifyToolRuntimeBindings()
    {
        SortedDictionary<string, string> expected = new(_binding.ToolRuntimeBindings.ToDictionary(static pair => pair.Key, static pair => pair.Value), StringComparer.Ordinal);
        SortedDictionary<string, string> actual = ReadActualToolRuntimeBindings();
        if (!DictionaryEquals(expected, actual))
        {
            string missing = string.Join(",", expected.Keys.Except(actual.Keys, StringComparer.Ordinal));
            string extra = string.Join(",", actual.Keys.Except(expected.Keys, StringComparer.Ordinal));
            string changed = string.Join(",", expected.Keys.Intersect(actual.Keys, StringComparer.Ordinal)
                .Where(path => expected[path] != actual[path]));
            throw new PanelEvidenceException(
                "TOOL_RUNTIME_BINDING_MISMATCH",
                typeof(InvalidDataException),
                $"missing=[{missing}] extra=[{extra}] changed=[{changed}]");
        }
        return actual;
    }

    private SortedDictionary<string, string> ReadActualToolRuntimeBindings()
    {
        _ = Cv2.GetVersionString();
        (string toolPath, string toolHash) = HashAssembly(typeof(AxisCandidatePanelBatchEvidenceRunner).Assembly);
        (string appPath, string appHash) = HashAssembly(typeof(ProductionRasterFrameDecoder).Assembly);
        (string axisPath, string axisHash) = HashAssembly(typeof(AxisGeometryDetector).Assembly);
        (string cvPath, string cvHash) = HashAssembly(typeof(Mat).Assembly);
        (string nativePath, string nativeHash) = HashLoadedOpenCvNativeModule();
        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [toolPath] = toolHash,
            [appPath] = appHash,
            [axisPath] = axisHash,
            [cvPath] = cvHash,
            [nativePath] = nativeHash,
        };
    }

    private AxisCandidatePanelEvidenceRuntime CreateRuntime(
        IReadOnlyDictionary<string, string> productionSources,
        IReadOnlyDictionary<string, string> panelizerRuntime,
        IReadOnlyDictionary<string, string> toolSources,
        IReadOnlyDictionary<string, string> toolRuntime)
    {
        var tool = typeof(AxisCandidatePanelBatchEvidenceRunner).Assembly;
        var app = typeof(ProductionRasterFrameDecoder).Assembly;
        var axis = typeof(AxisGeometryDetector).Assembly;
        var cv = typeof(Mat).Assembly;
        string nativePath = HashLoadedOpenCvNativeModule().RelativePath;
        return new AxisCandidatePanelEvidenceRuntime(
            RelativeAssembly(tool), toolRuntime[RelativeAssembly(tool)],
            RelativeAssembly(app), toolRuntime[RelativeAssembly(app)],
            RelativeAssembly(axis), toolRuntime[RelativeAssembly(axis)],
            RelativeAssembly(cv), toolRuntime[RelativeAssembly(cv)],
            nativePath, toolRuntime[nativePath],
            productionSources,
            panelizerRuntime,
            toolSources,
            toolRuntime,
            _binding.OpenCvOptions,
            _binding.GeometryOptions);
    }

    private AxisCandidatePanelBatchReport CreateReport(
        PanelBatchContext context,
        IReadOnlyList<AxisCandidatePanelResult> panelResults,
        AxisCandidatePanelEvidenceRuntime? runtime,
        string status,
        AxisCandidatePanelBatchFailure? failure) => new(
            ReportSchema,
            status,
            _binding.Scope,
            _binding.Split,
            _manifestRelativePath,
            _manifestSha256,
            _binding.PanelizationAdmissionBindingSha256,
            _binding.PanelizationReceiptSha256,
            context.SourceCount,
            context.DetectedSourceCount,
            context.FallbackSourceCount,
            context.FailedSourceCount,
            context.PanelCount,
            context.SelectedPanels.Count,
            context.Sources.Select(source => new AxisCandidatePanelSourceSummary(
                source.Source.SourceId,
                source.Source.Sha256,
                source.Source.Width,
                source.Source.Height,
                source.Status,
                source.Warnings,
                source.Panels.Count,
                source.Panels.Count(panel => context.SelectedPanels.Contains(panel)))).ToArray(),
            panelResults,
            runtime,
            failure);

    private AxisCandidatePanelBatchReport Failure(
        PanelBatchContext? context,
        string kind,
        string? code,
        Type exceptionType,
        string? message) =>
        new(
            ReportSchema,
            kind == "cancelled" ? "cancelled" : "failed",
            _binding.Scope,
            _binding.Split,
            _manifestRelativePath,
            _manifestSha256,
            _binding.PanelizationAdmissionBindingSha256,
            _binding.PanelizationReceiptSha256,
            context?.SourceCount ?? 0,
            context?.DetectedSourceCount ?? 0,
            context?.FallbackSourceCount ?? 0,
            context?.FailedSourceCount ?? 0,
            context?.PanelCount ?? 0,
            context?.SelectedPanels.Count ?? 0,
            context?.Sources.Select(source => new AxisCandidatePanelSourceSummary(
                source.Source.SourceId,
                source.Source.Sha256,
                source.Source.Width,
                source.Source.Height,
                source.Status,
                source.Warnings,
                source.Panels.Count,
                source.Panels.Count(panel => context.SelectedPanels.Contains(panel)))).ToArray() ?? [],
            [],
            null,
            new AxisCandidatePanelBatchFailure(kind, code, exceptionType.FullName ?? exceptionType.Name, message));

    private SortedDictionary<string, string> VerifyBoundFiles(
        IReadOnlyDictionary<string, string> expected,
        string errorCode)
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string path, string sha) in expected)
        {
            string actualSha = HashPath(ResolveProjectPath(path));
            if (actualSha != sha)
            {
                throw new PanelEvidenceException(errorCode, typeof(InvalidDataException), path);
            }
            actual.Add(path, actualSha);
        }
        return actual;
    }

    private (string RelativePath, string Sha256) HashAssembly(Assembly assembly)
    {
        string path = assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new PanelEvidenceException("RUNTIME_ASSEMBLY_MISSING", typeof(InvalidDataException));
        }
        Program.EnsureNoReparsePoints(path, _projectRoot, allowMissing: false);
        return (RelativeAssembly(assembly), HashPath(path));
    }

    private string RelativeAssembly(Assembly assembly)
    {
        string path = Path.GetFullPath(assembly.Location);
        Program.EnsureNoReparsePoints(path, _projectRoot, allowMissing: false);
        return Path.GetRelativePath(_projectRoot, path).Replace('\\', '/');
    }

    private (string RelativePath, string Sha256) HashLoadedOpenCvNativeModule()
    {
        using Process process = Process.GetCurrentProcess();
        ProcessModule? module = process.Modules.Cast<ProcessModule>().FirstOrDefault(static item =>
            string.Equals(Path.GetFileName(item.FileName), "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase));
        if (module is null || string.IsNullOrWhiteSpace(module.FileName) || !File.Exists(module.FileName))
        {
            throw new PanelEvidenceException("OPEN_CV_NATIVE_MODULE_NOT_FOUND", typeof(InvalidDataException));
        }

        Program.EnsureNoReparsePoints(module.FileName, _projectRoot, allowMissing: false);
        return (Path.GetRelativePath(_projectRoot, module.FileName).Replace('\\', '/'), HashPath(module.FileName));
    }

    private void EnsureSameHash(string path, string expected, string name)
    {
        Program.EnsureNoReparsePoints(path, _projectRoot, allowMissing: false);
        if (HashPath(path) != expected)
        {
            throw new PanelEvidenceException("BOUND_INPUT_CHANGED", typeof(InvalidDataException), name);
        }
    }

    private string ResolveProjectPath(string relativePath) =>
        ResolveProjectPath(relativePath, _projectRoot, _binding.Scope == "synthetic_test_fixture");

    private static string ResolveProjectPath(string relativePath, string projectRoot, bool allowTestFixture = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
        {
            throw new PanelEvidenceException("PANEL_BOUND_PATH_INVALID", typeof(InvalidDataException));
        }
        string[] segments = relativePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." ||
            segment.Contains("private", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("sealed", StringComparison.OrdinalIgnoreCase) ||
            (!allowTestFixture && (segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                                   segment.Equals("tests", StringComparison.OrdinalIgnoreCase)))))
        {
            throw new PanelEvidenceException("PANEL_BOUND_PATH_SCOPE_REJECTED", typeof(InvalidDataException), string.Join("/", segments));
        }
        string full = Path.GetFullPath(Path.Combine(projectRoot, Path.Combine(segments)));
        Program.EnsureNoReparsePoints(full, projectRoot, allowMissing: false);
        return full;
    }

    private static ReadOnlyCollection<string> ReadStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new PanelEvidenceException("PANEL_WARNINGS_SHAPE_INVALID", typeof(InvalidDataException));
        }
        string[] values = element.EnumerateArray().Select(static value => value.GetString() ??
            throw new PanelEvidenceException("PANEL_WARNING_INVALID", typeof(InvalidDataException))).ToArray();
        return Array.AsReadOnly(values);
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new PanelEvidenceException("PANEL_FIELD_INVALID", typeof(InvalidDataException), name);

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static string RequiredGuid(JsonElement element, string name)
    {
        string value = RequiredString(element, name);
        return Guid.TryParse(value, out _) ? value : throw new PanelEvidenceException("PANEL_GUID_INVALID", typeof(InvalidDataException), name);
    }

    private static string RequiredRelativePath(JsonElement element, string name)
    {
        string value = RequiredString(element, name);
        return value.Contains('\\') || Path.IsPathRooted(value) || value.Split('/').Any(static part => part is "" or "." or "..")
            ? throw new PanelEvidenceException("PANEL_SOURCE_PATH_INVALID", typeof(InvalidDataException), name)
            : value;
    }

    private static string RequiredHash(JsonElement element, string name)
    {
        string value = RequiredString(element, name);
        return IsHash(value) ? value : throw new PanelEvidenceException("PANEL_HASH_INVALID", typeof(InvalidDataException), name);
    }

    private static double RequiredFinite(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number
            : throw new PanelEvidenceException("PANEL_NUMBER_INVALID", typeof(InvalidDataException), name);
    }

    private static bool ExactKeys(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool DictionaryEquals(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out string? value) && value == pair.Value);

    private static void EnsureEqual(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, string name)
    {
        if (!DictionaryEquals(before, after))
        {
            throw new PanelEvidenceException("INPUT_CHANGED_DURING_SCAN", typeof(InvalidDataException), name);
        }
    }

    private static void EnsureHash(byte[] bytes, string expected, string name)
    {
        if (Hash(bytes) != expected)
        {
            throw new PanelEvidenceException("BOUND_HASH_MISMATCH", typeof(InvalidDataException), name);
        }
    }

    private static (string Sha256, long ByteLength) HashPathWithLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        return (Convert.ToHexStringLower(SHA256.HashData(stream)), length);
    }

    private static string HashPath(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsNonFatal(Exception exception) => exception is not
        OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private static JsonSerializerOptions CreateInputJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record SourceInventoryItem(string SourceId, string Path, string Sha256, int Width, int Height);
    private sealed record SourceWorkItem(
        SourceInventoryItem Source,
        string Status,
        IReadOnlyList<string> Warnings,
        string? PanelizationReportPath,
        string? PanelizationReportSha256,
        IReadOnlyList<PanelWorkItem> Panels,
        string? ExceptionType);
    private sealed record AxisCandidatePanelEvidenceRecord(
        string SourceId,
        string SourceImagePath,
        string SourceSha256,
        int SourceWidth,
        int SourceHeight,
        string PanelId,
        string PanelOriginKind,
        string PanelImagePath,
        string PanelRelativePath,
        string PanelSha256,
        int Width,
        int Height,
        AxisCandidatePanelRect RequestedCrop,
        AxisCandidatePanelRect EncodedCrop,
        IReadOnlyList<double> SourceToPanelMatrix,
        IReadOnlyList<double> PanelToSourceMatrix,
        IReadOnlyList<string> PanelizationWarnings);
    private sealed record PanelWorkItem(
        SourceInventoryItem Source,
        string SourceStatus,
        AxisCandidatePanelEvidenceRecord Panel);
    private sealed record PanelBatchContext(
        string AdmissionBindingPath,
        string ReceiptPath,
        IReadOnlyList<SourceWorkItem> Sources,
        IReadOnlyList<PanelWorkItem> SelectedPanels,
        IReadOnlyDictionary<string, string> PanelizerRuntimeBindings,
        int SourceCount,
        int DetectedSourceCount,
        int FallbackSourceCount,
        int FailedSourceCount,
        int PanelCount,
        IReadOnlyList<BoundPath>? SupplementalBoundFiles = null);

    private sealed record BoundPath(string Path, string Sha256, string Name);

    private sealed class CandidateObserver : IAxisGeometryCandidateObserver
    {
        public IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }
        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) => Candidates = candidates;
    }

    private sealed class PanelEvidenceException(string code, Type exceptionType, string? detail = null)
        : Exception(detail is null ? code : $"{code}: {detail}")
    {
        public string Code { get; } = code;
        public Type ExceptionType { get; } = exceptionType;
    }
}

public static class AxisCandidatePanelBatchReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string WriteNew(string projectRoot, string relativeOutputPath, AxisCandidatePanelBatchReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOutputPath);
        ArgumentNullException.ThrowIfNull(report);
        string root = Path.GetFullPath(projectRoot);
        string normalized = relativeOutputPath.Replace('\\', '/');
        if (Path.IsPathRooted(relativeOutputPath) ||
            !normalized.StartsWith("artifacts/goal22-runs/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Output must be a fresh project-relative path under artifacts/goal22-runs.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        Program.EnsureNoReparsePoints(fullPath, root, allowMissing: true);
        string parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Output parent is missing.");
        Directory.CreateDirectory(parent);
        Program.EnsureNoReparsePoints(fullPath, root, allowMissing: true);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        return Hash(bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
