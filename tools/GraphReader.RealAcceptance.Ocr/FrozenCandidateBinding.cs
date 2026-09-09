// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FrozenCandidateFile(string RelativePath, string Sha256, byte[] Bytes)
{
    internal byte[] CopyBytes() => (byte[])Bytes.Clone();
}

internal sealed record FrozenCandidateModel(
    string Task,
    string ModelId,
    string Version,
    FrozenCandidateFile Payload,
    FrozenCandidateFile Manifest,
    IReadOnlyList<FrozenCandidateReviewedLicense> ReviewedLicenseInputs);

internal sealed record FrozenCandidateReviewedLicense(
    string Role,
    string DeclaredPath,
    FrozenCandidateFile File);

internal sealed record FrozenCandidateClassifier(
    string StoreRoot,
    string ModelId,
    string Version,
    string ModelSha256,
    string ManifestSha256,
    string NoticeSha256,
    string BenchmarkSha256,
    string PackageIndexSha256);

internal sealed record FrozenCandidateRuntime(
    string ExecutionProvider,
    string GraphOptimization,
    int IntraOperationThreads,
    int InterOperationThreads,
    int QueueCapacity,
    int WorkerCount);

internal sealed record FrozenCandidateAlgorithms(
    string AxisStageVersion,
    string OcrOutputGeometry,
    string OcrCompositionVersion,
    string ArtifactAlgorithmId,
    string ArtifactAlgorithmVersion,
    string ArtifactConfigurationSha256,
    string ArtifactAppAssemblySha256,
    string ArtifactOcrAssemblySha256,
    string MarkerCenterRevision,
    string MarkerCenterCandidateId,
    string MarkerClassifierAdapterId,
    string LegendAdapterId,
    string PhaseAdapterId);

internal sealed class FrozenCandidateBinding
{
    internal const string Schema = "graphreader.real-acceptance-frozen-candidate.v1";
    private static readonly HashSet<string> ReviewedLicenseAllowlist = new(StringComparer.Ordinal)
    {
        "Apache-2.0", "MIT", "BSD-2-Clause", "BSD-3-Clause", "ISC", "Zlib",
        "BSL-1.0", "Unlicense", "CC0-1.0",
    };
    private FrozenCandidateBinding(
        string candidateId,
        string revision,
        FrozenCandidateFile protocol,
        FrozenCandidateRuntime runtime,
        FrozenCandidateModel ocrDetection,
        FrozenCandidateModel ocrRecognition,
        FrozenCandidateModel markerCenter,
        FrozenCandidateClassifier markerClassifier,
        FrozenCandidateFile openCvNative,
        IReadOnlyList<FrozenCandidateFile> nativeFiles,
        IReadOnlyList<FrozenCandidateFile> managedFiles,
        FrozenCandidateAlgorithms algorithms,
        string sha256,
        byte[] documentBytes)
    {
        CandidateId = candidateId;
        Revision = revision;
        Protocol = protocol;
        Runtime = runtime;
        OcrDetection = ocrDetection;
        OcrRecognition = ocrRecognition;
        MarkerCenter = markerCenter;
        MarkerClassifier = markerClassifier;
        OpenCvNative = openCvNative;
        NativeFiles = nativeFiles;
        ManagedFiles = managedFiles;
        Algorithms = algorithms;
        Sha256 = sha256;
        this.documentBytes = (byte[])documentBytes.Clone();
    }

    internal string CandidateId { get; }
    internal string Revision { get; }
    internal FrozenCandidateFile Protocol { get; }
    internal FrozenCandidateRuntime Runtime { get; }
    internal FrozenCandidateModel OcrDetection { get; }
    internal FrozenCandidateModel OcrRecognition { get; }
    internal FrozenCandidateModel MarkerCenter { get; }
    internal FrozenCandidateClassifier MarkerClassifier { get; }
    internal FrozenCandidateFile OpenCvNative { get; }
    internal IReadOnlyList<FrozenCandidateFile> NativeFiles { get; }
    internal IReadOnlyList<FrozenCandidateFile> ManagedFiles { get; }
    internal FrozenCandidateAlgorithms Algorithms { get; }
    internal string Sha256 { get; }
    private readonly byte[] documentBytes;

    internal byte[] CopyDocumentBytes() => (byte[])documentBytes.Clone();

    internal static FrozenCandidateBinding Load(
        string repositoryRoot,
        string bindingPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        bindingPath = RequireUnderRoot(repositoryRoot, bindingPath, "candidate binding");
        expectedSha256 = RequireSha256(expectedSha256, nameof(expectedSha256));
        byte[] bytes = ReadExactBytes(bindingPath, cancellationToken);
        string actualSha256 = Hash(bytes);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Frozen candidate binding checksum mismatch.");
        }

        using JsonDocument document = Parse(bytes, "Frozen candidate binding");
        JsonElement root = document.RootElement;
        RequireExactProperties(root,
        [
            "schema", "candidate_id", "revision", "protocol", "runtime", "ocr_detection",
            "ocr_recognition", "marker_center", "marker_classifier", "native_files",
            "managed_files", "algorithms",
        ], "Frozen candidate binding");
        RequireString(root, "schema", Schema, "Frozen candidate binding");
        string candidateId = RequiredText(root, "candidate_id", "Frozen candidate binding");
        string revision = RequiredText(root, "revision", "Frozen candidate binding");
        FrozenCandidateFile protocol = ReadFile(repositoryRoot, RequiredObject(root, "protocol"), "protocol", cancellationToken);
        FrozenCandidateRuntime runtime = ReadRuntime(RequiredObject(root, "runtime"));
        FrozenCandidateModel detection = ReadModel(repositoryRoot, RequiredObject(root, "ocr_detection"), "ocr_detection", cancellationToken);
        FrozenCandidateModel recognition = ReadModel(repositoryRoot, RequiredObject(root, "ocr_recognition"), "ocr_recognition", cancellationToken);
        FrozenCandidateModel marker = ReadModel(repositoryRoot, RequiredObject(root, "marker_center"), "marker_center", cancellationToken);
        if (new[] { detection.Payload.Sha256, recognition.Payload.Sha256, marker.Payload.Sha256 }
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            throw new InvalidDataException("Frozen candidate model payloads must have distinct checksums.");
        }
        FrozenCandidateFile[] primaryModelFiles =
        [
            detection.Payload, detection.Manifest,
            recognition.Payload, recognition.Manifest,
            marker.Payload, marker.Manifest,
        ];
        if (primaryModelFiles.Select(static file => file.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != primaryModelFiles.Length ||
            new[] { detection.Manifest.Sha256, recognition.Manifest.Sha256, marker.Manifest.Sha256 }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            throw new InvalidDataException("Frozen candidate model payload and manifest identities must be distinct.");
        }

        FrozenCandidateClassifier classifier = ReadClassifier(repositoryRoot, RequiredObject(root, "marker_classifier"), cancellationToken);
        (IReadOnlyList<FrozenCandidateFile> nativeFiles, FrozenCandidateFile openCvNative) =
            ReadNativeFiles(repositoryRoot, RequiredArray(root, "native_files"), cancellationToken);
        IReadOnlyList<FrozenCandidateFile> managedFiles = ReadManagedFiles(
            repositoryRoot,
            RequiredArray(root, "managed_files"),
            cancellationToken);
        FrozenCandidateAlgorithms algorithms = ReadAlgorithms(RequiredObject(root, "algorithms"));

        FrozenCandidateFile[] allFiles = new[] { protocol }
            .Concat(ModelFiles(detection))
            .Concat(ModelFiles(recognition))
            .Concat(ModelFiles(marker))
            .Concat(nativeFiles)
            .Concat(managedFiles)
            .ToArray();
        if (allFiles.GroupBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(static file => file.Sha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1))
        {
            throw new InvalidDataException("Frozen candidate binding assigns conflicting hashes to one file path.");
        }

        return new FrozenCandidateBinding(
            candidateId, revision, protocol, runtime, detection, recognition, marker,
            classifier, openCvNative, nativeFiles, managedFiles, algorithms, actualSha256, bytes);
    }

    private static FrozenCandidateRuntime ReadRuntime(JsonElement value)
    {
        RequireExactProperties(value,
        [
            "execution_provider", "graph_optimization", "intra_operation_threads",
            "inter_operation_threads", "queue_capacity", "worker_count",
        ], "Frozen candidate runtime");
        RequireString(value, "execution_provider", "cpu", "Frozen candidate runtime");
        RequireString(value, "graph_optimization", "disabled", "Frozen candidate runtime");
        int intra = RequiredPositiveInt32(value, "intra_operation_threads", "Frozen candidate runtime");
        int inter = RequiredPositiveInt32(value, "inter_operation_threads", "Frozen candidate runtime");
        if (inter != 1)
        {
            throw new InvalidDataException("Frozen candidate CPU inter-operation threads must equal one.");
        }

        return new FrozenCandidateRuntime(
            "cpu", "disabled", intra, inter,
            RequiredPositiveInt32(value, "queue_capacity", "Frozen candidate runtime"),
            RequiredPositiveInt32(value, "worker_count", "Frozen candidate runtime"));
    }

    private static FrozenCandidateModel ReadModel(
        string repositoryRoot,
        JsonElement value,
        string expectedTask,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(value,
        [
            "task", "model_id", "version", "payload", "manifest", "reviewed_license_inputs",
        ], $"Frozen candidate {expectedTask}");
        RequireString(value, "task", expectedTask, $"Frozen candidate {expectedTask}");
        string modelId = RequiredText(value, "model_id", $"Frozen candidate {expectedTask}");
        string version = RequiredText(value, "version", $"Frozen candidate {expectedTask}");
        FrozenCandidateFile payload = ReadFile(repositoryRoot, RequiredObject(value, "payload"), $"{expectedTask} payload", cancellationToken);
        FrozenCandidateFile manifest = ReadFile(repositoryRoot, RequiredObject(value, "manifest"), $"{expectedTask} manifest", cancellationToken);
        JsonElement licenses = RequiredArray(value, "reviewed_license_inputs");
        if (licenses.GetArrayLength() != 2)
        {
            throw new InvalidDataException(
                $"Frozen candidate {expectedTask} requires exactly two reviewed license inputs.");
        }

        var reviewed = new List<FrozenCandidateReviewedLicense>(licenses.GetArrayLength());
        foreach (JsonElement item in licenses.EnumerateArray())
        {
            RequireExactProperties(
                item,
                ["role", "declared_path", "file", "sha256"],
                $"Frozen candidate {expectedTask} reviewed license");
            string role = RequiredText(item, "role", $"Frozen candidate {expectedTask} reviewed license");
            if (role is not ("license_text" or "notice"))
            {
                throw new InvalidDataException($"Frozen candidate {expectedTask} reviewed license has an unsupported role.");
            }
            string declaredPath = RequiredText(
                item,
                "declared_path",
                $"Frozen candidate {expectedTask} reviewed license").Replace('\\', '/');
            if (Path.IsPathRooted(declaredPath) ||
                declaredPath.Split('/').Any(static part => part is "" or "." or ".."))
            {
                throw new InvalidDataException(
                    $"Frozen candidate {expectedTask} reviewed license declared_path must be canonical and repository-relative.");
            }
            var fileObject = JsonSerializer.SerializeToElement(new
            {
                file = RequiredText(item, "file", $"Frozen candidate {expectedTask} reviewed license"),
                sha256 = RequiredText(item, "sha256", $"Frozen candidate {expectedTask} reviewed license"),
            });
            FrozenCandidateFile file = ReadFile(
                repositoryRoot,
                fileObject,
                $"{expectedTask} reviewed license",
                cancellationToken);
            if (file.Bytes.Length == 0)
            {
                throw new InvalidDataException($"Frozen candidate {expectedTask} reviewed license input is empty.");
            }
            reviewed.Add(new FrozenCandidateReviewedLicense(role, declaredPath, file));
        }
        if (reviewed.Select(static input => input.Role).Distinct(StringComparer.Ordinal).Count() != 2 ||
            reviewed.Select(static input => input.File.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != reviewed.Count ||
            reviewed.Select(static input => input.DeclaredPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != reviewed.Count)
        {
            throw new InvalidDataException(
                $"Frozen candidate {expectedTask} requires distinct license_text and notice inputs.");
        }

        using JsonDocument manifestDocument = Parse(manifest.Bytes, $"Frozen candidate {expectedTask} manifest");
        JsonElement manifestRoot = manifestDocument.RootElement;
        if (!manifestRoot.TryGetProperty("manifest_version", out JsonElement manifestVersion) ||
            !manifestVersion.TryGetInt32(out int versionNumber) || versionNumber != 1)
        {
            throw new InvalidDataException($"Frozen candidate {expectedTask} manifest_version must equal one.");
        }
        JsonElement source = RequiredObject(manifestRoot, "source");
        _ = RequiredText(source, "name", $"Frozen candidate {expectedTask} manifest source");
        _ = RequiredText(source, "url", $"Frozen candidate {expectedTask} manifest source");
        _ = RequiredText(source, "revision", $"Frozen candidate {expectedTask} manifest source");
        RequireString(manifestRoot, "task", expectedTask, $"Frozen candidate {expectedTask} manifest");
        RequireString(manifestRoot, "model_id", modelId, $"Frozen candidate {expectedTask} manifest");
        RequireString(manifestRoot, "model_version", version, $"Frozen candidate {expectedTask} manifest");
        string declaredSha = RequireSha256(
            RequiredText(manifestRoot, "sha256", $"Frozen candidate {expectedTask} manifest"),
            "sha256");
        if (!string.Equals(declaredSha, payload.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Frozen candidate {expectedTask} manifest does not bind the model payload.");
        }
        ValidateCandidateManifestDistribution(
            manifestRoot, payload, reviewed, $"Frozen candidate {expectedTask} manifest");

        return new FrozenCandidateModel(expectedTask, modelId, version, payload, manifest, reviewed.AsReadOnly());
    }

    private static void ValidateCandidateManifestDistribution(
        JsonElement manifest,
        FrozenCandidateFile payload,
        IReadOnlyList<FrozenCandidateReviewedLicense> reviewedLicenseInputs,
        string label)
    {
        JsonElement license = RequiredObject(manifest, "license");
        string spdx = RequiredText(license, "spdx", label);
        if (!ReviewedLicenseAllowlist.Contains(spdx) ||
            !license.TryGetProperty("reviewed", out JsonElement reviewed) ||
            reviewed.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException($"{label} lacks a reviewed redistributable license.");
        }
        string noticePath = RequiredText(license, "notice_path", label).Replace('\\', '/');
        FrozenCandidateReviewedLicense notice = reviewedLicenseInputs.Single(input => input.Role == "notice");
        if (!string.Equals(notice.DeclaredPath, noticePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} notice_path is not a checksum-bound reviewed license input.");
        }
        if (!manifest.TryGetProperty("commercial_use", out JsonElement commercial) ||
            commercial.ValueKind != JsonValueKind.True ||
            !manifest.TryGetProperty("redistribution", out JsonElement redistribution) ||
            redistribution.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException($"{label} must explicitly permit commercial use and redistribution.");
        }
        JsonElement providers = RequiredArray(manifest, "providers");
        if (!providers.EnumerateArray().Any(static item =>
                item.ValueKind == JsonValueKind.String && item.GetString() == "cpu"))
        {
            throw new InvalidDataException($"{label} must declare the CPU execution provider.");
        }
        if (manifest.TryGetProperty("benchmarks", out JsonElement benchmarks))
        {
            if (benchmarks.ValueKind != JsonValueKind.Array || benchmarks.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.Object ||
                    (item.TryGetProperty("production_approved", out JsonElement approved) &&
                     approved.ValueKind == JsonValueKind.True)))
            {
                throw new InvalidDataException($"{label} contains invalid or production-approved benchmark evidence.");
            }
        }
        JsonElement files = RequiredArray(manifest, "files");
        string payloadName = Path.GetFileName(payload.RelativePath);
        if (!files.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String &&
                string.Equals(Path.GetFileName(item.GetString()), payloadName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"{label} files do not identify the checksum-bound payload.");
        }
    }

    private static FrozenCandidateClassifier ReadClassifier(
        string repositoryRoot,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(value,
        [
            "store_root", "model_id", "version", "model_sha256", "manifest_sha256",
            "notice_sha256", "benchmark_sha256", "package_index_sha256",
        ], "Frozen candidate marker classifier");
        string storeRoot = RequiredText(value, "store_root", "Frozen candidate marker classifier");
        string resolvedStoreRoot = RequireUnderRoot(repositoryRoot, storeRoot, "marker classifier store");
        string packageIndexPath = Path.Combine(resolvedStoreRoot, "production-model-index.json");
        byte[] packageIndex = ReadExactBytes(packageIndexPath, cancellationToken);
        string packageIndexSha = RequireSha256(
            RequiredText(value, "package_index_sha256", "Frozen candidate marker classifier"),
            "package_index_sha256");
        if (!string.Equals(Hash(packageIndex), packageIndexSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Frozen candidate classifier package index checksum mismatch.");
        }

        return new FrozenCandidateClassifier(
            Path.GetRelativePath(repositoryRoot, resolvedStoreRoot).Replace('\\', '/'),
            RequiredText(value, "model_id", "Frozen candidate marker classifier"),
            RequiredText(value, "version", "Frozen candidate marker classifier"),
            RequireSha256(RequiredText(value, "model_sha256", "Frozen candidate marker classifier"), "model_sha256"),
            RequireSha256(RequiredText(value, "manifest_sha256", "Frozen candidate marker classifier"), "manifest_sha256"),
            RequireSha256(RequiredText(value, "notice_sha256", "Frozen candidate marker classifier"), "notice_sha256"),
            RequireSha256(RequiredText(value, "benchmark_sha256", "Frozen candidate marker classifier"), "benchmark_sha256"),
            packageIndexSha);
    }

    private static (IReadOnlyList<FrozenCandidateFile>, FrozenCandidateFile) ReadNativeFiles(
        string repositoryRoot,
        JsonElement values,
        CancellationToken cancellationToken)
    {
        var files = new List<FrozenCandidateFile>(values.GetArrayLength());
        FrozenCandidateFile? openCv = null;
        foreach (JsonElement item in values.EnumerateArray())
        {
            RequireExactProperties(item, ["role", "file", "sha256"], "Frozen candidate native file");
            string role = RequiredText(item, "role", "Frozen candidate native file");
            var fileObject = JsonSerializer.SerializeToElement(new
            {
                file = RequiredText(item, "file", "Frozen candidate native file"),
                sha256 = RequiredText(item, "sha256", "Frozen candidate native file"),
            });
            FrozenCandidateFile file = ReadFile(repositoryRoot, fileObject, $"native {role}", cancellationToken);
            files.Add(file);
            if (string.Equals(role, "opencvsharp_extern", StringComparison.Ordinal))
            {
                if (openCv is not null)
                {
                    throw new InvalidDataException("Frozen candidate binding contains multiple OpenCvSharp native runtimes.");
                }
                if (!string.Equals(Path.GetFileName(file.RelativePath), "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("opencvsharp_extern must bind OpenCvSharpExtern.dll.");
                }
                openCv = file;
            }
        }

        RequireExactExecutableSet(files, ActualNativeExecutablePaths(), "native executable");

        return (files.AsReadOnly(), openCv ?? throw new InvalidDataException(
            "Frozen candidate binding requires one opencvsharp_extern native runtime."));
    }

    private static ReadOnlyCollection<FrozenCandidateFile> ReadManagedFiles(
        string repositoryRoot,
        JsonElement values,
        CancellationToken cancellationToken)
    {
        var files = new List<FrozenCandidateFile>(values.GetArrayLength());
        foreach (JsonElement item in values.EnumerateArray())
        {
            files.Add(ReadFile(repositoryRoot, item, "managed executable", cancellationToken));
        }

        RequireExactExecutableSet(files, ActualManagedExecutablePaths(), "managed executable");
        return files.AsReadOnly();
    }

    private static FrozenCandidateAlgorithms ReadAlgorithms(JsonElement value)
    {
        RequireExactProperties(value,
        [
            "axis_stage_version", "ocr_output_geometry", "ocr_composition_version",
            "artifact_algorithm_id", "artifact_algorithm_version", "artifact_configuration_sha256",
            "artifact_app_assembly_sha256", "artifact_ocr_assembly_sha256",
            "marker_center_revision", "marker_center_candidate_id", "marker_classifier_adapter_id",
            "legend_adapter_id", "phase_adapter_id",
        ], "Frozen candidate algorithms");
        return new FrozenCandidateAlgorithms(
            RequiredText(value, "axis_stage_version", "Frozen candidate algorithms"),
            RequiredText(value, "ocr_output_geometry", "Frozen candidate algorithms"),
            RequiredText(value, "ocr_composition_version", "Frozen candidate algorithms"),
            RequiredText(value, "artifact_algorithm_id", "Frozen candidate algorithms"),
            RequiredText(value, "artifact_algorithm_version", "Frozen candidate algorithms"),
            RequireSha256(RequiredText(value, "artifact_configuration_sha256", "Frozen candidate algorithms"), "artifact_configuration_sha256"),
            RequireSha256(RequiredText(value, "artifact_app_assembly_sha256", "Frozen candidate algorithms"), "artifact_app_assembly_sha256"),
            RequireSha256(RequiredText(value, "artifact_ocr_assembly_sha256", "Frozen candidate algorithms"), "artifact_ocr_assembly_sha256"),
            RequiredText(value, "marker_center_revision", "Frozen candidate algorithms"),
            RequiredText(value, "marker_center_candidate_id", "Frozen candidate algorithms"),
            RequiredText(value, "marker_classifier_adapter_id", "Frozen candidate algorithms"),
            RequiredText(value, "legend_adapter_id", "Frozen candidate algorithms"),
            RequiredText(value, "phase_adapter_id", "Frozen candidate algorithms"));
    }

    private static FrozenCandidateFile ReadFile(
        string repositoryRoot,
        JsonElement value,
        string label,
        CancellationToken cancellationToken)
    {
        RequireExactProperties(value, ["file", "sha256"], label);
        string relativePath = RequiredText(value, "file", label).Replace('\\', '/');
        if (Path.IsPathRooted(relativePath) || relativePath.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"{label} path must be a canonical repository-relative path.");
        }
        string expectedSha256 = RequireSha256(RequiredText(value, "sha256", label), "sha256");
        string path = RequireUnderRoot(repositoryRoot, relativePath, label);
        byte[] bytes = ReadExactBytes(path, cancellationToken);
        if (!string.Equals(Hash(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} checksum mismatch.");
        }
        return new FrozenCandidateFile(relativePath, expectedSha256, bytes);
    }

    private static IEnumerable<FrozenCandidateFile> ModelFiles(FrozenCandidateModel model) =>
        new[] { model.Payload, model.Manifest }.Concat(
            model.ReviewedLicenseInputs.Select(static input => input.File));

    private static void RequireExactExecutableSet(
        IReadOnlyList<FrozenCandidateFile> declared,
        IReadOnlyList<string> actualPaths,
        string label)
    {
        string[] declaredNames = declared.Select(static file => Path.GetFileName(file.RelativePath))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] actualNames = actualPaths.Select(static path => Path.GetFileName(path)!)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!declaredNames.SequenceEqual(actualNames, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Frozen candidate {label} binding does not match the complete running executable set.");
        }
        foreach (FrozenCandidateFile file in declared)
        {
            string actualPath = actualPaths.Single(path => string.Equals(
                Path.GetFileName(path), Path.GetFileName(file.RelativePath), StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(Hash(File.ReadAllBytes(actualPath)), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Running {label} '{Path.GetFileName(actualPath)}' differs from the frozen binding.");
            }
        }
    }

    internal static IReadOnlyList<string> ActualManagedExecutablePaths()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string executableName = typeof(Program).Assembly.GetName().Name!;
        return Directory.EnumerateFiles(baseDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsManagedAssembly(path) ||
                string.Equals(Path.GetFileName(path), $"{executableName}.deps.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), $"{executableName}.runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ActualNativeExecutablePaths() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) &&
                 !IsManagedAssembly(path)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsManagedAssembly(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            _ = AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileLoadException)
        {
            return false;
        }
    }

    private static byte[] ReadExactBytes(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A checksum-bound frozen candidate input is missing.", path);
        }
        byte[] bytes = File.ReadAllBytes(path);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    private static JsonDocument Parse(byte[] bytes, string label)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            RejectDuplicateProperties(document.RootElement, label);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{label} is malformed: {exception.Message}", exception);
        }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static string RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{parameterName} must contain exactly 64 hexadecimal characters.");
        }
        return value.ToLowerInvariant();
    }

    internal static string RequireUnderRoot(string root, string path, string label)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} leaves the repository root.");
        }
        return fullPath;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{name} must be an object.");
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{name} must be an array.");
        }
        return value;
    }

    private static int RequiredPositiveInt32(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int result) || result <= 0)
        {
            throw new InvalidDataException($"{label} {name} must be a positive integer.");
        }
        return result;
    }

    private static string RequiredText(JsonElement parent, string name, string label)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{label} {name} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static void RequireString(JsonElement parent, string name, string expected, string label)
    {
        string actual = RequiredText(parent, name, label);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} {name} must equal '{expected}'.");
        }
    }

    private static void RequireExactProperties(JsonElement value, string[] expected, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{label} must be an object.");
        }
        string[] actual = value.EnumerateObject().Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        string[] orderedExpected = expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(orderedExpected, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{label} fields do not match the frozen schema.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"{label} contains duplicate field '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value, label);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item, label);
            }
        }
    }
}
