// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenCandidateBindingSelfTest
{
    private static readonly string[] CpuProviders = ["cpu"];

    internal static void Run(string repositoryRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        string artifactsRoot = Path.Combine(repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactsRoot);
        string root = Path.Combine(
            artifactsRoot,
            $"frozen-candidate-binding-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var fixture = new Fixture(repositoryRoot, root);
            JsonObject valid = fixture.CreateBinding();
            FrozenCandidateBinding loaded = fixture.Load(valid);
            Require(
                loaded.OcrDetection.ReviewedLicenseInputs
                    .Select(static input => input.File.RelativePath)
                    .SequenceEqual(
                        loaded.OcrRecognition.ReviewedLicenseInputs
                            .Select(static input => input.File.RelativePath),
                        StringComparer.OrdinalIgnoreCase),
                "FROZEN_CANDIDATE_BINDING_SELF_TEST_DID_NOT_SHARE_OCR_LICENSE_INPUTS");
            Require(
                loaded.ManagedFiles.Count == FrozenCandidateBinding.ActualManagedExecutablePaths().Count &&
                loaded.NativeFiles.Count == FrozenCandidateBinding.ActualNativeExecutablePaths().Count,
                "FROZEN_CANDIDATE_BINDING_SELF_TEST_DID_NOT_BIND_RUNNING_EXECUTABLE");

            JsonObject thirdLicense = Clone(valid);
            JsonArray licenses = RequiredArray(thirdLicense, "ocr_detection", "reviewed_license_inputs");
            string extraLicense = fixture.Write("licenses/EXTRA.txt", "extra reviewed license input\n"u8.ToArray());
            licenses.Add(new JsonObject
            {
                ["role"] = "license_text",
                ["declared_path"] = "licenses/EXTRA.txt",
                ["file"] = extraLicense,
                ["sha256"] = fixture.HashFile(extraLicense),
            });
            fixture.ExpectRejected(thirdLicense, "three reviewed license inputs");

            fixture.ExpectFileTamperRejected(
                valid,
                fixture.DetectionPayloadPath,
                "changed model payload"u8.ToArray(),
                "changed payload");
            fixture.ExpectFileTamperRejected(
                valid,
                fixture.DetectionManifestPath,
                "{}"u8.ToArray(),
                "changed manifest");

            JsonObject nativeHash = Clone(valid);
            FirstObject(RequiredArray(nativeHash, "native_files"))["sha256"] = new string('0', 64);
            fixture.ExpectRejected(nativeHash, "changed native hash");

            JsonObject executableHash = Clone(valid);
            FirstObject(RequiredArray(executableHash, "managed_files"))["sha256"] = new string('0', 64);
            fixture.ExpectRejected(executableHash, "changed executable hash");

            JsonObject unknownField = Clone(valid);
            unknownField["unexpected"] = true;
            fixture.ExpectRejected(unknownField, "unknown binding field");

            JsonObject unsafePath = Clone(valid);
            RequiredObject(unsafePath, "protocol")["file"] = "../foreign-protocol.json";
            fixture.ExpectRejected(unsafePath, "unsafe path");

            JsonObject incorrectManifest = Clone(valid);
            fixture.ReplaceDetectionManifest(
                incorrectManifest,
                fixture.CreateModelManifest(
                    "wrong_task",
                    "ocr-detector-fixture",
                    "1",
                    fixture.DetectionPayloadPath,
                    productionApproved: false),
                "models/detection/incorrect-contract.json");
            fixture.ExpectRejected(incorrectManifest, "incorrect manifest contract");

            JsonObject approvedBenchmark = Clone(valid);
            fixture.ReplaceDetectionManifest(
                approvedBenchmark,
                fixture.CreateModelManifest(
                    "ocr_detection",
                    "ocr-detector-fixture",
                    "1",
                    fixture.DetectionPayloadPath,
                    productionApproved: true),
                "models/detection/production-approved.json");
            fixture.ExpectRejected(approvedBenchmark, "production-approved benchmark");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonObject Clone(JsonObject value) =>
        (JsonObject)value.DeepClone();

    private static JsonObject RequiredObject(JsonObject parent, string name) =>
        parent[name] as JsonObject ??
        throw new InvalidOperationException($"Self-test fixture '{name}' is not an object.");

    private static JsonArray RequiredArray(JsonObject parent, string objectName, string arrayName) =>
        RequiredObject(parent, objectName)[arrayName] as JsonArray ??
        throw new InvalidOperationException($"Self-test fixture '{objectName}.{arrayName}' is not an array.");

    private static JsonArray RequiredArray(JsonObject parent, string name) =>
        parent[name] as JsonArray ??
        throw new InvalidOperationException($"Self-test fixture '{name}' is not an array.");

    private static JsonObject FirstObject(JsonArray values) =>
        values.FirstOrDefault() as JsonObject ??
        throw new InvalidOperationException("Self-test fixture array is empty.");

    private static void Require(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private sealed class Fixture
    {
        private readonly string repositoryRoot;
        private readonly string root;
        private readonly string protocolPath;
        private readonly string licensePath;
        private readonly string noticePath;
        private readonly string markerPayloadPath;
        private readonly string markerManifestPath;
        private readonly string detectionManifestPath;
        private readonly string recognitionPayloadPath;
        private readonly string recognitionManifestPath;
        private readonly string classifierStoreRoot;

        internal Fixture(string repositoryRoot, string root)
        {
            this.repositoryRoot = repositoryRoot;
            this.root = root;
            protocolPath = Write("protocol.json", "{\"scope\":\"synthetic-self-test\"}\n"u8.ToArray());
            licensePath = Write("licenses/LICENSE.txt", "Apache License 2.0 fixture\n"u8.ToArray());
            noticePath = Write("licenses/NOTICE.txt", "Synthetic fixture notice\n"u8.ToArray());
            DetectionPayloadPath = Write("models/detection/model.onnx", "detector fixture payload"u8.ToArray());
            recognitionPayloadPath = Write("models/recognition/model.onnx", "recognizer fixture payload"u8.ToArray());
            markerPayloadPath = Write("models/marker/model.onnx", "marker fixture payload"u8.ToArray());
            detectionManifestPath = Write(
                "models/detection/manifest.json",
                CreateModelManifest(
                    "ocr_detection", "ocr-detector-fixture", "1",
                    DetectionPayloadPath, productionApproved: false));
            recognitionManifestPath = Write(
                "models/recognition/manifest.json",
                CreateModelManifest(
                    "ocr_recognition", "ocr-recognizer-fixture", "1",
                    recognitionPayloadPath, productionApproved: false));
            markerManifestPath = Write(
                "models/marker/manifest.json",
                CreateModelManifest(
                    "marker_center", "marker-center-fixture", "1",
                    markerPayloadPath, productionApproved: false));
            classifierStoreRoot = Relative(Path.Combine(root, "classifier-store"));
            Write("classifier-store/production-model-index.json", "{\"schema\":1}\n"u8.ToArray());
        }

        internal string DetectionPayloadPath { get; }
        internal string DetectionManifestPath => detectionManifestPath;

        internal JsonObject CreateBinding()
        {
            IReadOnlyList<string> managedPaths = FrozenCandidateBinding.ActualManagedExecutablePaths();
            IReadOnlyList<string> nativePaths = FrozenCandidateBinding.ActualNativeExecutablePaths();
            string appAssemblySha = ExecutableSha(managedPaths, "GraphReader.App.dll");
            string ocrAssemblySha = ExecutableSha(managedPaths, "GraphReader.Ocr.dll");
            return new JsonObject
            {
                ["schema"] = FrozenCandidateBinding.Schema,
                ["candidate_id"] = "frozen-binding-self-test-candidate",
                ["revision"] = "frozen-binding-self-test-v1",
                ["protocol"] = FileRecord(protocolPath),
                ["runtime"] = new JsonObject
                {
                    ["execution_provider"] = "cpu",
                    ["graph_optimization"] = "disabled",
                    ["intra_operation_threads"] = 1,
                    ["inter_operation_threads"] = 1,
                    ["queue_capacity"] = 1,
                    ["worker_count"] = 1,
                },
                ["ocr_detection"] = ModelRecord(
                    "ocr_detection", "ocr-detector-fixture", DetectionPayloadPath,
                    detectionManifestPath),
                ["ocr_recognition"] = ModelRecord(
                    "ocr_recognition", "ocr-recognizer-fixture", recognitionPayloadPath,
                    recognitionManifestPath),
                ["marker_center"] = ModelRecord(
                    "marker_center", "marker-center-fixture", markerPayloadPath,
                    markerManifestPath),
                ["marker_classifier"] = new JsonObject
                {
                    ["store_root"] = classifierStoreRoot,
                    ["model_id"] = "marker-classifier-fixture",
                    ["version"] = "1",
                    ["model_sha256"] = new string('1', 64),
                    ["manifest_sha256"] = new string('2', 64),
                    ["notice_sha256"] = new string('3', 64),
                    ["benchmark_sha256"] = new string('4', 64),
                    ["package_index_sha256"] = HashFile(
                        $"{classifierStoreRoot}/production-model-index.json"),
                },
                ["native_files"] = ExecutableRecords(nativePaths, includeRoles: true),
                ["managed_files"] = ExecutableRecords(managedPaths, includeRoles: false),
                ["algorithms"] = new JsonObject
                {
                    ["axis_stage_version"] = "self-test-axis-v1",
                    ["ocr_output_geometry"] = "model_polygon",
                    ["ocr_composition_version"] = "self-test-ocr-v1",
                    ["artifact_algorithm_id"] = "self-test-artifact",
                    ["artifact_algorithm_version"] = "1",
                    ["artifact_configuration_sha256"] = new string('5', 64),
                    ["artifact_app_assembly_sha256"] = appAssemblySha,
                    ["artifact_ocr_assembly_sha256"] = ocrAssemblySha,
                    ["marker_center_revision"] = "self-test-marker-v1",
                    ["marker_center_candidate_id"] = "self-test-marker-candidate",
                    ["marker_classifier_adapter_id"] = "self-test-marker-adapter",
                    ["legend_adapter_id"] = "self-test-legend-adapter",
                    ["phase_adapter_id"] = "self-test-phase-adapter",
                },
            };
        }

        internal byte[] CreateModelManifest(
            string task,
            string modelId,
            string version,
            string payloadPath,
            bool productionApproved) =>
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                manifest_version = 1,
                source = new
                {
                    name = "synthetic self-test fixture",
                    url = "https://example.invalid/frozen-binding-self-test",
                    revision = "fixture-v1",
                },
                task,
                model_id = modelId,
                model_version = version,
                sha256 = HashFile(payloadPath),
                license = new
                {
                    spdx = "Apache-2.0",
                    reviewed = true,
                    notice_path = "licenses/NOTICE.txt",
                },
                commercial_use = true,
                redistribution = true,
                providers = CpuProviders,
                benchmarks = new[] { new { production_approved = productionApproved } },
                files = new[] { Path.GetFileName(payloadPath) },
            });

        internal string Write(string relativeToFixture, byte[] bytes)
        {
            string fullPath = Path.Combine(root, relativeToFixture.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            return Relative(fullPath);
        }

        internal string HashFile(string relativePath) =>
            FrozenCandidateBinding.Hash(File.ReadAllBytes(Full(relativePath)));

        internal FrozenCandidateBinding Load(JsonObject binding)
        {
            (string path, string sha256) = WriteBinding(binding);
            return FrozenCandidateBinding.Load(
                repositoryRoot, path, sha256, CancellationToken.None);
        }

        internal void ExpectRejected(JsonObject binding, string scenario)
        {
            try
            {
                _ = Load(binding);
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException(
                $"FROZEN_CANDIDATE_BINDING_SELF_TEST_ACCEPTED_{scenario.Replace(' ', '_').ToUpperInvariant()}");
        }

        internal void ExpectFileTamperRejected(
            JsonObject binding,
            string relativePath,
            byte[] changedBytes,
            string scenario)
        {
            (string bindingPath, string bindingSha) = WriteBinding(binding);
            string fullPath = Full(relativePath);
            byte[] original = File.ReadAllBytes(fullPath);
            try
            {
                File.WriteAllBytes(fullPath, changedBytes);
                try
                {
                    _ = FrozenCandidateBinding.Load(
                        repositoryRoot, bindingPath, bindingSha, CancellationToken.None);
                }
                catch (InvalidDataException)
                {
                    return;
                }
                throw new InvalidOperationException(
                    $"FROZEN_CANDIDATE_BINDING_SELF_TEST_ACCEPTED_{scenario.Replace(' ', '_').ToUpperInvariant()}");
            }
            finally
            {
                File.WriteAllBytes(fullPath, original);
            }
        }

        internal void ReplaceDetectionManifest(
            JsonObject binding,
            byte[] manifestBytes,
            string relativeToFixture)
        {
            string path = Write(relativeToFixture, manifestBytes);
            RequiredObject(binding, "ocr_detection")["manifest"] = FileRecord(path);
        }

        private JsonObject ModelRecord(
            string task,
            string modelId,
            string payloadPath,
            string manifestPath) =>
            new()
            {
                ["task"] = task,
                ["model_id"] = modelId,
                ["version"] = "1",
                ["payload"] = FileRecord(payloadPath),
                ["manifest"] = FileRecord(manifestPath),
                ["reviewed_license_inputs"] = new JsonArray
                {
                    LicenseRecord("license_text", "licenses/LICENSE.txt", licensePath),
                    LicenseRecord("notice", "licenses/NOTICE.txt", noticePath),
                },
            };

        private JsonObject LicenseRecord(string role, string declaredPath, string path) =>
            new()
            {
                ["role"] = role,
                ["declared_path"] = declaredPath,
                ["file"] = path,
                ["sha256"] = HashFile(path),
            };

        private JsonObject FileRecord(string path) =>
            new()
            {
                ["file"] = path,
                ["sha256"] = HashFile(path),
            };

        private JsonArray ExecutableRecords(
            IReadOnlyList<string> paths,
            bool includeRoles)
        {
            var records = new JsonArray();
            for (int index = 0; index < paths.Count; index++)
            {
                string path = paths[index];
                var record = new JsonObject
                {
                    ["file"] = Relative(path),
                    ["sha256"] = FrozenCandidateBinding.Hash(File.ReadAllBytes(path)),
                };
                if (includeRoles)
                {
                    record["role"] = string.Equals(
                        Path.GetFileName(path),
                        "OpenCvSharpExtern.dll",
                        StringComparison.OrdinalIgnoreCase)
                        ? "opencvsharp_extern"
                        : $"native_{index}";
                }
                records.Add(record);
            }
            return records;
        }

        private static string ExecutableSha(IReadOnlyList<string> paths, string name)
        {
            string path = paths.Single(item => string.Equals(
                Path.GetFileName(item), name, StringComparison.OrdinalIgnoreCase));
            return FrozenCandidateBinding.Hash(File.ReadAllBytes(path));
        }

        private (string Path, string Sha256) WriteBinding(JsonObject binding)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(binding);
            string path = Write($"bindings/{Guid.NewGuid():N}.json", bytes);
            return (path, FrozenCandidateBinding.Hash(bytes));
        }

        private string Full(string relativePath) =>
            Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        private string Relative(string fullPath) =>
            Path.GetRelativePath(repositoryRoot, Path.GetFullPath(fullPath)).Replace('\\', '/');
    }
}
