// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Authenticates and locks candidate files before constructing a non-persistent CPU runtime.</summary>
internal static class OriginalDbOcrMemoryRuntime
{
    internal static async Task<T> RunAsync<T>(string root, string candidatePath, string candidateSha256,
        Func<ProductionOcrAdapter, LocalOnnxTextRegionDetector, ProductionAxisGeometryAdapter,
            CancellationToken, Task<T>> evaluate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        var locks = new List<FileStream>();
        nint native = nint.Zero;
        string Inside(string path)
        {
            string full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OCR_RUNTIME_PATH_INVALID");
            return full;
        }
        byte[] Lock(string path, string expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new InvalidDataException("OCR_RUNTIME_HASH_INVALID");
            var stream = new FileStream(Inside(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            locks.Add(stream);
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OCR_RUNTIME_FILE_CHANGED");
            // Only small JSON documents are materialized; model weights remain locked on disk.
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return [];
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("OCR_RUNTIME_JSON_TOO_LARGE");
            stream.Position = 0;
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        string Text(JsonElement row, string key) => row.GetProperty(key).GetString()
            ?? throw new InvalidDataException("OCR_RUNTIME_FIELD_MISSING");
        try
        {
            using JsonDocument document = JsonDocument.Parse(Lock(candidatePath, candidateSha256));
            JsonElement candidate = document.RootElement;
            if (Text(candidate, "schema") != "graphreader.frozen-db-head-ocr-candidate.v1" ||
                Text(candidate, "scope") != "project-owned-synthetic-train-dev-unapproved-frozen-candidate" ||
                Text(candidate, "composition_version") != ProductionOcrAdapter.OriginalDbCandidateCompositionVersion ||
                Text(candidate, "native_scope") != "reviewed-source-runtime-local-diagnostic" ||
                candidate.GetProperty("production_approved").GetBoolean() ||
                candidate.GetProperty("training_input_ready").GetBoolean())
                throw new InvalidDataException("OCR_RUNTIME_CANDIDATE_SCOPE_INVALID");
            foreach (var assembly in OfficialHeadCandidateEvaluation.ReadExecutionAssemblies(
                         candidate.GetProperty("execution_assemblies"), root))
                Lock(assembly.Path, assembly.Sha256);
            JsonElement licenses = candidate.GetProperty("license_inputs");
            if (licenses.GetArrayLength() == 0) throw new InvalidDataException("OCR_RUNTIME_LICENSE_BINDING_MISSING");
            foreach (JsonElement license in licenses.EnumerateArray()) Lock(Text(license, "path"), Text(license, "sha256"));
            FrozenCandidateOcrModelDescriptor Model(string key, string task)
            {
                JsonElement row = candidate.GetProperty(key);
                string modelPath = Text(row, "model_path");
                string modelHash = Text(row, "model_sha256");
                Lock(modelPath, modelHash);
                string manifestPath = Text(row, "manifest_path");
                string manifestHash = Text(row, "manifest_sha256");
                using JsonDocument manifestDocument = JsonDocument.Parse(Lock(manifestPath, manifestHash));
                JsonElement manifest = manifestDocument.RootElement;
                if (Text(manifest, "task") != task || Text(manifest, "sha256") != modelHash ||
                    Text(manifest, "model_id") != Text(row, "model_id") ||
                    Text(manifest, "model_version") != Text(row, "model_version"))
                    throw new InvalidDataException("OCR_RUNTIME_MANIFEST_IDENTITY_INVALID");
                return new(new ModelIdentity(Text(row, "model_id"), Text(row, "model_version"), modelHash,
                    Inside(modelPath)), Inside(manifestPath), manifestHash);
            }
            FrozenCandidateOcrModelDescriptor detection = Model("detector", "ocr_detection");
            FrozenCandidateOcrModelDescriptor recognition = Model("recognizer", "ocr_recognition");
            if (detection.Identity.Sha256 == recognition.Identity.Sha256)
                throw new InvalidDataException("OCR_RUNTIME_MODELS_NOT_DISTINCT");
            string nativeHash = Text(candidate, "native_sha256");
            string nativePath = Text(candidate, "native_path");
            Lock(nativePath, nativeHash);
            native = NativeLibrary.Load(Inside(nativePath));
            NativeLibrary.SetDllImportResolver(typeof(OpenCvSharp.Mat).Assembly,
                (name, _, _) => name == "OpenCvSharpExtern" ? native : nint.Zero);
            await using var runtime = new ProductionInferenceRuntimeHost(new OrtExecutionProviderDiscovery(),
                new WindowsExecutionProviderPolicy(),
                new OnnxInferenceSessionFactory(NoUiThreadGuard.Instance, OnnxGraphOptimizationMode.Disabled),
                CpuThreadConfiguration.Create(1), [InferenceProvider.Cpu],
                Path.Combine(root, "artifacts", "unused-ocr-memory-cache"), 1, 1, new NoPersistenceStageCache());
            ProductionOcrAdapter ocr = await ProductionOcrAdapter.CreateForFrozenDbHeadCandidateEvaluationAsync(
                detection, recognition, runtime, nativeHash, cancellationToken).ConfigureAwait(false);
            var raw = new LocalOnnxTextRegionDetector(runtime.Runtime,
                ProductionOcrAdapter.ReadDetectionOptions(detection.Identity, detection.ManifestPath) with
                { AllowedProviders = [InferenceProvider.Cpu] });
            return await evaluate(ocr, raw, new ProductionAxisGeometryAdapter(nativeHash, isApproved: false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (native != nint.Zero) NativeLibrary.Free(native);
            foreach (FileStream stream in locks) stream.Dispose();
        }
    }
}
