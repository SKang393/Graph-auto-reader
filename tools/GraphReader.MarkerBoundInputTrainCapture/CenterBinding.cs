// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

/// <summary>
/// A no-classification-model binding. The unchanged shared model validator is
/// invoked for every model this tool loads, including its license checks.
/// </summary>
internal sealed class CenterBinding : IDisposable
{
    private static readonly string[] ToolSources =
    [
        "tools/GraphReader.MarkerBoundInputTrainCapture/GraphReader.MarkerBoundInputTrainCapture.csproj",
        "tools/GraphReader.MarkerBoundInputTrainCapture/Program.cs",
        "tools/GraphReader.MarkerBoundInputTrainCapture/CenterBinding.cs",
        "tools/GraphReader.MarkerBoundInputTrainCapture/CenterRunner.cs",
        "tools/GraphReader.MarkerBoundInputTrainCapture/CandidateInterfaces.cs",
        "tools/GraphReader.MarkerDualViewTrainCapture/SamplerCapture.cs",
        "tools/GraphReader.MarkerDualViewTrainCapture/PanelProvenance.cs",
        "tools/GraphReader.RealAcceptance.Ocr/FrozenCandidateBinding.cs",
        "tools/GraphReader.RealAcceptance.Ocr/NoPersistenceStageCache.cs",
        "tools/Shared/ComposedOcrExecutionIdentity.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionLegendSymbolInputs.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionAutomaticDetectionAdapter.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionOcrAdapter.cs",
        "src/GraphReader.App/Integration/Workflow/ProductionMarkerClassificationAdapter.cs",
        "src/GraphReader.Markers/Classification/MarkerPatchExtractor.cs",
        "src/GraphReader.Markers/Classification/MarkerClassificationContracts.cs",
    ];
    private static readonly string[] ModelFiles =
    [
        "artifacts/goal22-runs/ocr-scale-v46-evaluation-v1/detector-manifest.json",
        "artifacts/goal22-runs/ocr-scale-coverage-v46/P1/detector-scale-coverage-v46-p1.onnx",
        "artifacts/frozen-workflow-candidate-inputs/static-v1/licenses/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt",
        "artifacts/goal22-runs/ocr-scale-v46-evaluation-v1/MODEL_NOTICE.txt",
        "artifacts/goal22-runs/server-ocr-comparison-v4/models/recognizer.json",
        "artifacts/goal22-runs/server-ocr-conversion-v2/PP-OCRv5_server_rec/conversion-1.onnx",
        "artifacts/goal22-runs/server-ocr-intake-v1/PP-OCRv5_server_rec/Apache-2.0.txt",
        "artifacts/goal22-runs/server-ocr-intake-v1/PP-OCRv5_server_rec/MODEL_NOTICE.md",
        "artifacts/goal22-runs/ocr-complete-workflow-marker-cascade-010-v1/marker-center-manifest.json",
        "artifacts/goal22-runs/marker-v27-component-diversity/P1-recovery1/marker-center-component-diversity-v27-p1.onnx",
        "LICENSE",
        "artifacts/goal22-runs/ocr-complete-workflow-v1/marker/MODEL_NOTICE.md",
    ];
    private readonly List<FileStream> locks = [];
    internal FrozenCandidateModel Detector { get; private set; } = null!;
    internal FrozenCandidateModel Recognizer { get; private set; } = null!;
    internal FrozenCandidateModel Center { get; private set; } = null!;
    internal FrozenCandidateFile OpenCv { get; private set; } = null!;
    internal string RasterManifestSha { get; private set; } = "";
    internal string BuilderBindingSha { get; private set; } = "";
    internal string ArtifactConfigurationSha { get; private set; } = "";
    internal string ArtifactAppSha { get; private set; } = "";
    internal string ArtifactOcrSha { get; private set; } = "";
    internal string Digest { get; private set; } = "";

    internal static CenterBinding Load(string root, string path, string expectedSha, CancellationToken token)
    {
        var result = new CenterBinding();
        try
        {
            path = Under(root, path);
            byte[] bytes = result.LockRead(root, path);
            if (Hash(bytes) != expectedSha) throw new InvalidDataException("Center-only binding digest differs.");
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement binding = document.RootElement;
            Exact(binding, "schema", "scope", "raster_manifest_sha256", "builder_binding_sha256",
                "artifact_configuration_sha256", "artifact_app_sha256", "artifact_ocr_sha256",
                "ocr_detection", "ocr_recognition", "marker_center", "managed_files", "native_files", "tool_sources", "training_approved");
            if (binding.GetProperty("schema").GetString() != "graphreader.marker-bound-input-train-binding.v1" ||
                binding.GetProperty("scope").GetString() != "train-only-no-truth-no-classification-model" ||
                binding.GetProperty("training_approved").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Center-only scope differs.");
            result.Digest = expectedSha;
            result.RasterManifestSha = Sha(binding, "raster_manifest_sha256");
            result.BuilderBindingSha = Sha(binding, "builder_binding_sha256");
            result.ArtifactConfigurationSha = Sha(binding, "artifact_configuration_sha256");
            result.ArtifactAppSha = Sha(binding, "artifact_app_sha256");
            result.ArtifactOcrSha = Sha(binding, "artifact_ocr_sha256");
            var allowed = new HashSet<string>(ToolSources.Concat(ModelFiles), StringComparer.OrdinalIgnoreCase);
            foreach (string executable in FrozenCandidateBinding.ActualManagedExecutablePaths().Concat(FrozenCandidateBinding.ActualNativeExecutablePaths()))
                allowed.Add(Path.GetRelativePath(root, Under(root, executable)).Replace('\\', '/'));
            result.LockReferences(root, binding, allowed);
            result.Detector = ReadModel(root, binding.GetProperty("ocr_detection"), "ocr_detection", token);
            result.Recognizer = ReadModel(root, binding.GetProperty("ocr_recognition"), "ocr_recognition", token);
            result.Center = ReadModel(root, binding.GetProperty("marker_center"), "marker_center", token);
            if (result.Detector.Payload.Sha256 != "497a3527820ce0359496084e5afb1e0d3241aca79e192187f7ab737b27818b7f" ||
                result.Recognizer.Payload.Sha256 != "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418" ||
                result.Center.Payload.Sha256 != "4979777a404be48c6c75b490218d1de31d2edb2550f5bc71c7aa6894c1bcca04")
                throw new InvalidDataException("Reviewed candidate model identities differ.");
            _ = Invoke("ReadManagedFiles", root, binding.GetProperty("managed_files"), token);
            var native = ((IReadOnlyList<FrozenCandidateFile>, FrozenCandidateFile))Invoke("ReadNativeFiles", root, binding.GetProperty("native_files"), token);
            result.OpenCv = native.Item2;
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement row in binding.GetProperty("tool_sources").EnumerateArray())
            {
                Exact(row, "file", "sha256");
                if (!sources.Add(row.GetProperty("file").GetString() ?? "")) throw new InvalidDataException("Duplicate tool source.");
            }
            if (!sources.SetEquals(ToolSources)) throw new InvalidDataException("Tool source inventory differs.");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static FrozenCandidateModel ReadModel(string root, JsonElement item, string task, CancellationToken token) =>
        (FrozenCandidateModel)Invoke("ReadModel", root, item, task, token);

    private static object Invoke(string name, params object[] args)
    {
        MethodInfo method = typeof(FrozenCandidateBinding).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Unchanged shared model validator is unavailable.");
        try { return method.Invoke(null, args) ?? throw new InvalidDataException("Shared validator returned no result."); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private void LockReferences(string root, JsonElement value, HashSet<string> allowed)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) LockReferences(root, item, allowed);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("file", out JsonElement file) && value.TryGetProperty("sha256", out JsonElement sha))
            {
                string relative = file.GetString() ?? "";
                if (Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".."))
                    throw new InvalidDataException("Bound file path is not canonical.");
                if (!allowed.Contains(relative)) throw new InvalidDataException("Bound file is not in the reviewed source/model/runtime allowlist.");
                string actual = Hash(LockRead(root, Under(root, Path.Combine(root, relative))));
                if (actual != sha.GetString()) throw new InvalidDataException("Bound file hash differs.");
            }
            foreach (JsonProperty property in value.EnumerateObject()) LockReferences(root, property.Value, allowed);
        }
    }

    internal byte[] LockRead(string root, string path)
    {
        RejectReparse(root, path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        locks.Add(stream);
        if (stream.Length > 500_000_000) throw new InvalidDataException("Bound file is too large.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Under(string root, string path)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : throw new InvalidDataException("Path escapes project.");
    }
    internal static void RejectReparse(string root, string path)
    {
        string cursor = Path.GetFullPath(root);
        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse project root.");
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".") return;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            cursor = Path.Combine(cursor, part);
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse path denied.");
        }
    }
    internal static void Exact(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected object.");
        string[] keys = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Length != expected.Length || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
            !keys.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Missing, extra or duplicate property.");
    }
    private static string Sha(JsonElement value, string name)
    {
        string text = value.GetProperty(name).GetString() ?? "";
        return text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? text : throw new InvalidDataException("Invalid SHA-256.");
    }
    public void Dispose() { foreach (FileStream stream in locks) stream.Dispose(); }
}
