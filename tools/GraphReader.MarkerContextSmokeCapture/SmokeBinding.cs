// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record SmokeSource(Guid SourceId, Guid SceneId, string SceneSha256, string ImageSha256,
    string FileName, int Width, int Height, byte[] Bytes);

internal sealed class SmokeBinding : IDisposable
{
    private const string RootPath = @"E:\Graph-auto-reader";
    private const string ProtocolPath = "tools/GraphReader.MarkerContextSmokeCapture/capture-protocol.json";
    private const string BaseBindingPath = "artifacts/goal22-runs/marker-context-bound-input-binding-v1.json";
    private const string BindingPath = "artifacts/goal22-runs/marker-context-bound-input-smoke-preflight-v4/capture-binding.json";
    private const string CompletePath = "artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/COMPLETE.json";
    private const string IdentityPath = "artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/identity-metadata.json";
    private const string RasterManifestPath = "artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/raster-only/manifest.json";
    private const string ExpectedBaseBindingSha = "704f5448f7dead44b898d2f9eb709e1a0040c86ad085cde7003fec0a3b15bc8d";
    private bool reviewApprovalValidated;

    internal string Root { get; } = RootPath;
    internal string Digest { get; }
    internal string ProtocolSha { get; }
    internal string AssemblySha { get; }
    internal string ParentBindingSha { get; } = ExpectedBaseBindingSha;
    internal string RuntimeBindingSha { get; }
    internal string OutputRelativePath { get; }
    internal IReadOnlyList<SmokeSource> Sources { get; }
    internal CenterBinding BaseBinding { get; }
    internal bool ReviewApprovalValidated => reviewApprovalValidated;
    internal static string RuntimeSummary => "reviewed base managed/native closure; CPU inference only after external approval receipt";

    private SmokeBinding(string digest, string protocolSha, string assemblySha, string runtimeBindingSha, string output,
        IReadOnlyList<SmokeSource> sources, CenterBinding baseBinding)
    {
        Digest = digest;
        ProtocolSha = protocolSha;
        AssemblySha = assemblySha;
        RuntimeBindingSha = runtimeBindingSha;
        OutputRelativePath = output;
        Sources = sources;
        BaseBinding = baseBinding;
    }

    internal static SmokeBinding Load(string descriptorPath, string expectedSha, CancellationToken token)
    {
        string root = RootPath;
        if (!Directory.Exists(root) || !string.Equals(Path.GetFullPath(root), RootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("E project root is required.");
        CenterBinding.RejectReparse(root, root);
        descriptorPath = CenterBinding.Under(root, descriptorPath);
        if (!string.Equals(descriptorPath, Path.Combine(root, BindingPath.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only the fixed smoke capture descriptor is accepted.");
        byte[] descriptorBytes = ReadLocked(root, descriptorPath);
        string digest = Hash(descriptorBytes);
        if (!FixedSha(expectedSha) || !string.Equals(digest, expectedSha, StringComparison.Ordinal))
            throw new InvalidDataException("Capture descriptor digest differs from the caller pin.");

        JsonDocument? descriptorDocument = null;
        CenterBinding? baseBinding = null;
        try
        {
            descriptorDocument = JsonDocument.Parse(descriptorBytes);
            JsonElement descriptor = descriptorDocument.RootElement;
            CenterBinding.Exact(descriptor, "schema", "scope", "protocol_path", "protocol_sha256",
                "base_binding_path", "base_binding_sha256", "runtime_binding_path", "runtime_binding_sha256",
                "complete_path", "complete_sha256",
                "identity_path", "identity_sha256", "raster_manifest_path", "raster_manifest_sha256",
                "source_images", "tool_sources", "assembly_path", "assembly_sha256",
                "capture_output_path", "training_approved", "marker_classifier_model_included");
            if (descriptor.GetProperty("schema").GetString() != "graphreader.marker-context-bound-input-smoke-capture-binding.v1" ||
                descriptor.GetProperty("scope").GetString() != "two-source-nonsealed-train-smoke-no-marker-classifier" ||
                descriptor.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
                descriptor.GetProperty("marker_classifier_model_included").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Capture binding scope is not the fixed no-classifier smoke scope.");

            string protocolPath = CanonicalPath(descriptor, "protocol_path", ProtocolPath);
            byte[] protocolBytes = ReadLocked(root, protocolPath);
            string protocolSha = Sha(descriptor, "protocol_sha256");
            if (Hash(protocolBytes) != protocolSha) throw new InvalidDataException("Capture protocol hash differs.");
            using JsonDocument protocolDocument = JsonDocument.Parse(protocolBytes);
            JsonElement protocol = protocolDocument.RootElement;
            ValidateProtocol(protocol);

            string basePath = CanonicalPath(descriptor, "base_binding_path", BaseBindingPath);
            string baseSha = Sha(descriptor, "base_binding_sha256");
            if (!string.Equals(baseSha, ExpectedBaseBindingSha, StringComparison.Ordinal))
                throw new InvalidDataException("Reviewed model/runtime binding identity differs.");
            byte[] parentBindingBytes = ReadLocked(root, basePath);
            if (Hash(parentBindingBytes) != baseSha) throw new InvalidDataException("Reviewed parent binding bytes differ.");
            string runtimeBindingPath = CanonicalPath(descriptor, "runtime_binding_path", "artifacts/goal22-runs/marker-context-bound-input-smoke-preflight-v4/runtime-binding.json");
            string runtimeBindingSha = Sha(descriptor, "runtime_binding_sha256");
            byte[] runtimeBindingBytes = ReadLocked(root, runtimeBindingPath);
            if (Hash(runtimeBindingBytes) != runtimeBindingSha) throw new InvalidDataException("Derived runtime binding hash differs.");
            ValidateRuntimeOverlay(parentBindingBytes, runtimeBindingBytes,
                protocol.GetProperty("runtime_directory").GetString() ?? "", protocol.GetProperty("assembly_path").GetString() ?? "",
                Sha(descriptor, "assembly_sha256"));
            baseBinding = CenterBinding.Load(root, runtimeBindingPath, runtimeBindingSha, token);

            ValidatePinnedInput(descriptor, "complete_path", "complete_sha256", protocol, "complete_path", "complete_sha256", CompletePath);
            ValidatePinnedInput(descriptor, "identity_path", "identity_sha256", protocol, "identity_path", "identity_sha256", IdentityPath);
            ValidatePinnedInput(descriptor, "raster_manifest_path", "raster_manifest_sha256", protocol, "raster_manifest_path", "raster_manifest_sha256", RasterManifestPath);
            byte[] completeBytes = ReadLocked(root, CenterBinding.Under(root, CompletePath));
            byte[] identityBytes = ReadLocked(root, CenterBinding.Under(root, IdentityPath));
            byte[] rasterBytes = ReadLocked(root, CenterBinding.Under(root, RasterManifestPath));
            if (Hash(completeBytes) != Sha(descriptor, "complete_sha256") ||
                Hash(identityBytes) != Sha(descriptor, "identity_sha256") ||
                Hash(rasterBytes) != Sha(descriptor, "raster_manifest_sha256"))
                throw new InvalidDataException("Pinned smoke input hashes changed during preflight.");
            using JsonDocument completeDocument = JsonDocument.Parse(completeBytes);
            using JsonDocument identityDocument = JsonDocument.Parse(identityBytes);
            using JsonDocument rasterDocument = JsonDocument.Parse(rasterBytes);
            ValidateComplete(completeDocument.RootElement, protocol);
            List<SmokeSource> sources = ReadSources(root, rasterDocument.RootElement, identityDocument.RootElement,
                protocol, descriptor.GetProperty("source_images"), baseBinding);
            ValidateToolSources(root, descriptor.GetProperty("tool_sources"), baseBinding);
            string assemblyRelative = CurrentAssemblyRelativePath(root);
            string protocolAssemblyPath = protocol.GetProperty("assembly_path").GetString() ?? "";
            string declaredAssembly = CanonicalPath(descriptor, "assembly_path", protocolAssemblyPath);
            if (!string.Equals(Path.GetRelativePath(root, declaredAssembly).Replace('\\', '/'), assemblyRelative, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Capture assembly location differs from descriptor.");
            string assemblySha = Sha(descriptor, "assembly_sha256");
            byte[] assemblyBytes = baseBinding.LockRead(root, CenterBinding.Under(root, declaredAssembly));
            if (Hash(assemblyBytes) != assemblySha) throw new InvalidDataException("Executing assembly hash differs.");
            string output = CanonicalPath(descriptor, "capture_output_path", "artifacts/goal22-runs/marker-context-bound-input-smoke-capture-v4");
            string outputFull = CenterBinding.Under(root, output);
            string expectedOutput = Path.Combine(root, "artifacts", "goal22-runs", "marker-context-bound-input-smoke-capture-v4");
            if (!string.Equals(outputFull, expectedOutput, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only the fixed fresh smoke output is accepted.");
            if (File.Exists(outputFull) || Directory.Exists(outputFull)) throw new IOException("Capture output must be fresh.");
            string outputParent = Path.GetDirectoryName(outputFull) ?? throw new InvalidDataException("Output parent missing.");
            if (!Directory.Exists(outputParent)) throw new DirectoryNotFoundException("Output parent must already exist.");
            CenterBinding.RejectReparse(root, outputParent);
            var result = new SmokeBinding(digest, protocolSha, assemblySha, runtimeBindingSha, output, sources, baseBinding);
            baseBinding = null;
            return result;
        }
        catch
        {
            baseBinding?.Dispose();
            throw;
        }
        finally
        {
            descriptorDocument?.Dispose();
        }
    }

    internal void RequireReviewApproval(string approvalPath, string expectedSha)
    {
        string full = CenterBinding.Under(Root, approvalPath);
        const string required = "artifacts/goal22-runs/marker-context-bound-input-smoke-preflight-v4/review-approval.json";
        if (!string.Equals(full, Path.Combine(Root, required.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only the external reviewed approval receipt is accepted.");
        byte[] bytes = BaseBinding.LockRead(Root, full);
        if (!FixedSha(expectedSha) || Hash(bytes) != expectedSha) throw new InvalidDataException("Review receipt hash differs from caller pin.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        ValidateReviewReceipt(document.RootElement, Digest, ProtocolSha, AssemblySha);
        reviewApprovalValidated = true;
    }

    internal static void ValidateReviewReceipt(JsonElement approval, string bindingSha, string protocolSha, string assemblySha)
    {
        CenterBinding.Exact(approval, "schema", "capture_binding_sha256", "protocol_sha256", "assembly_sha256",
            "reviewed", "allow_model_inference", "marker_classifier_model_loaded", "training_approved");
        if (approval.GetProperty("schema").GetString() != "graphreader.marker-context-bound-input-smoke-review.v1" ||
            approval.GetProperty("capture_binding_sha256").GetString() != bindingSha ||
            approval.GetProperty("protocol_sha256").GetString() != protocolSha ||
            approval.GetProperty("assembly_sha256").GetString() != assemblySha ||
            approval.GetProperty("reviewed").ValueKind != JsonValueKind.True ||
            approval.GetProperty("allow_model_inference").ValueKind != JsonValueKind.True ||
            approval.GetProperty("marker_classifier_model_loaded").ValueKind != JsonValueKind.False ||
            approval.GetProperty("training_approved").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("A matching external review receipt is required before any model inference.");
    }

    private static void ValidateProtocol(JsonElement protocol)
    {
        CenterBinding.Exact(protocol, "schema", "scope", "project_root", "base_binding_path", "base_binding_sha256",
            "runtime_binding_path", "runtime_directory", "assembly_path",
            "complete_path", "complete_sha256", "identity_path", "identity_sha256", "raster_manifest_path",
            "raster_manifest_sha256", "source_count", "sources", "training_approved",
            "marker_classifier_model_included", "inference_before_review", "capture_output_path");
        if (protocol.GetProperty("schema").GetString() != "graphreader.marker-context-bound-input-smoke-capture-protocol.v1" ||
            protocol.GetProperty("scope").GetString() != "two-source-nonsealed-train-smoke-no-marker-classifier" ||
            protocol.GetProperty("project_root").GetString() != "E:/Graph-auto-reader" ||
            protocol.GetProperty("base_binding_path").GetString() != BaseBindingPath ||
            protocol.GetProperty("base_binding_sha256").GetString() != ExpectedBaseBindingSha ||
            protocol.GetProperty("runtime_binding_path").GetString() != "artifacts/goal22-runs/marker-context-bound-input-smoke-preflight-v4/runtime-binding.json" ||
            protocol.GetProperty("runtime_directory").GetString() != "artifacts/goal22-runs/marker-context-bound-input-smoke-runtime-v4" ||
            protocol.GetProperty("assembly_path").GetString() != "artifacts/goal22-runs/marker-context-bound-input-smoke-runtime-v4/GraphReader.RealAcceptance.Ocr.dll" ||
            protocol.GetProperty("complete_path").GetString() != CompletePath ||
            protocol.GetProperty("complete_sha256").GetString() != "d3ec47441f2bd19504c6390a16ed2a94725751c84d5d7c43f6855328e7207dc6" ||
            protocol.GetProperty("identity_path").GetString() != IdentityPath ||
            protocol.GetProperty("identity_sha256").GetString() != "e0ea1b6894d119e4f3063dce4fd373af90585c91626ce4d84621dde15d125150" ||
            protocol.GetProperty("raster_manifest_path").GetString() != RasterManifestPath ||
            protocol.GetProperty("raster_manifest_sha256").GetString() != "72975d61930bdafe9817f9c221d1699192c0f82e85c3c4c686edd5db0d7a99f3" ||
            protocol.GetProperty("source_count").GetInt32() != 2 ||
            protocol.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
            protocol.GetProperty("marker_classifier_model_included").ValueKind != JsonValueKind.False ||
            protocol.GetProperty("inference_before_review").ValueKind != JsonValueKind.False ||
            protocol.GetProperty("capture_output_path").GetString() != "artifacts/goal22-runs/marker-context-bound-input-smoke-capture-v4")
            throw new InvalidDataException("Fixed two-source smoke protocol identity differs.");
        ValidateScopeMetadata("smoke", protocol.GetProperty("source_count").GetInt32(),
            protocol.GetProperty("training_approved").GetBoolean(), protocol.GetProperty("marker_classifier_model_included").GetBoolean());
    }

    private static void ValidateComplete(JsonElement complete, JsonElement protocol)
    {
        if (complete.GetProperty("schema").GetString() != "marker-context-train-corpus-complete-v3" ||
            complete.GetProperty("mode").GetString() != "smoke" || complete.GetProperty("source_count").GetInt32() != 2 ||
            complete.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
            complete.GetProperty("identity_sha256").GetString() != protocol.GetProperty("identity_sha256").GetString() ||
            complete.GetProperty("raster_manifest_sha256").GetString() != protocol.GetProperty("raster_manifest_sha256").GetString())
            throw new InvalidDataException("Smoke completion receipt differs from fixed protocol.");
        ValidateScopeMetadata(complete.GetProperty("mode").GetString() ?? "", complete.GetProperty("source_count").GetInt32(),
            complete.GetProperty("training_approved").GetBoolean(), classifierModelIncluded: false);
        // truth_sha256 is deliberately not used as an input and the truth file is never resolved.
    }

    private static List<SmokeSource> ReadSources(string root, JsonElement raster, JsonElement identity,
        JsonElement protocol, JsonElement descriptorSources, CenterBinding baseBinding)
    {
        if (raster.GetProperty("schema").GetString() != "marker-context-train-raster-v3" ||
            raster.GetProperty("mode").GetString() != "smoke" ||
            raster.GetProperty("project_id").GetString() != "7e5293ff-4acb-4355-8045-b10d07fad29e" ||
            raster.GetProperty("builder_binding_sha256").GetString() != "2e34e02ba8e8499e4606cac06b7367251a17a0126db1596192b322dde51c751c" ||
            raster.GetProperty("training_approved").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Raster manifest is not the fixed nonapproved smoke manifest.");
        if (identity.GetProperty("schema").GetString() != "marker-context-train-identity-v3" ||
            identity.GetProperty("mode").GetString() != "smoke" ||
            identity.GetProperty("project_id").GetString() != raster.GetProperty("project_id").GetString() ||
            identity.GetProperty("training_approved").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Identity manifest is not the fixed smoke identity.");
        JsonElement expectedSources = protocol.GetProperty("sources");
        JsonElement rasterSources = raster.GetProperty("sources");
        JsonElement identitySources = identity.GetProperty("sources");
        if (expectedSources.ValueKind != JsonValueKind.Array || expectedSources.GetArrayLength() != 2 ||
            descriptorSources.ValueKind != JsonValueKind.Array || descriptorSources.GetArrayLength() != 2 ||
            rasterSources.ValueKind != JsonValueKind.Array || rasterSources.GetArrayLength() != 2 ||
            identitySources.ValueKind != JsonValueKind.Array || identitySources.GetArrayLength() != 2)
            throw new InvalidDataException("Exactly two public smoke sources are required.");
        string rasterDirectory = Path.Combine(root, Path.GetDirectoryName(RasterManifestPath)!.Replace('/', Path.DirectorySeparatorChar));
        CenterBinding.RejectReparse(root, rasterDirectory);
        string[] presentNames = Directory.EnumerateFileSystemEntries(rasterDirectory)
            .Select(entry => Path.GetFileName(entry) ?? string.Empty).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var output = new List<SmokeSource>(2);
        for (int i = 0; i < 2; i++)
        {
            JsonElement expected = expectedSources[i];
            JsonElement descriptorSource = descriptorSources[i];
            JsonElement rasterSource = rasterSources[i];
            JsonElement identitySource = identitySources[i];
            string idText = expected.GetProperty("source_id").GetString() ?? "";
            string imageSha = expected.GetProperty("image_sha256").GetString() ?? "";
            string file = imageSha + ".png";
            int width = expected.GetProperty("width").GetInt32();
            int height = expected.GetProperty("height").GetInt32();
            CenterBinding.Exact(descriptorSource, "source_id", "path", "sha256", "width", "height");
            string descriptorImagePath = descriptorSource.GetProperty("path").GetString() ?? "";
            if (descriptorSource.GetProperty("source_id").GetString() != idText ||
                descriptorSource.GetProperty("sha256").GetString() != imageSha ||
                descriptorSource.GetProperty("width").GetInt32() != width ||
                descriptorSource.GetProperty("height").GetInt32() != height ||
                descriptorImagePath != expected.GetProperty("path").GetString())
                throw new InvalidDataException("Descriptor source image binding differs from protocol.");
            _ = CenterBinding.Under(root, descriptorImagePath);
            if (!Guid.TryParseExact(idText, "D", out Guid sourceId) ||
                !Guid.TryParseExact(expected.GetProperty("scene_id").GetString(), "D", out Guid sceneId) ||
                !FixedSha(imageSha) || width != 1200 || height != 350 ||
                rasterSource.GetProperty("source_id").GetString() != idText ||
                rasterSource.GetProperty("file").GetString() != file ||
                rasterSource.GetProperty("sha256").GetString() != imageSha ||
                rasterSource.GetProperty("width").GetInt32() != width || rasterSource.GetProperty("height").GetInt32() != height ||
                identitySource.GetProperty("source_id").GetString() != idText ||
                identitySource.GetProperty("scene_id").GetString() != expected.GetProperty("scene_id").GetString() ||
                identitySource.GetProperty("scene_sha256").GetString() != expected.GetProperty("scene_sha256").GetString() ||
                identitySource.GetProperty("image_sha256").GetString() != imageSha ||
                identitySource.GetProperty("source_id_status").GetString() != "predeclared_not_yet_imported")
                throw new InvalidDataException("Raster and identity source records differ from protocol.");
            string imagePath = Path.Combine(rasterDirectory, file);
            byte[] bytes = baseBinding.LockRead(root, imagePath);
            if (Hash(bytes) != imageSha) throw new InvalidDataException("Smoke source image hash differs.");
            using var imageStream = new MemoryStream(bytes, writable: false);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(imageStream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != width || decoder.Frames[0].PixelHeight != height)
                throw new InvalidDataException("Smoke source dimensions differ.");
            output.Add(new SmokeSource(sourceId, sceneId, expected.GetProperty("scene_sha256").GetString()!, imageSha, file, width, height, bytes));
        }
        string[] expectedNames = ["manifest.json", .. output.Select(source => source.FileName)];
        if (!presentNames.SequenceEqual(expectedNames.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Raster-only directory contains missing or extra entries.");
        return output;
    }

    private static void ValidateToolSources(string root, JsonElement rows, CenterBinding baseBinding)
    {
        string[] expected =
        [
            "tools/GraphReader.MarkerContextSmokeCapture/GraphReader.MarkerContextSmokeCapture.csproj",
            "tools/GraphReader.MarkerContextSmokeCapture/Program.cs",
            "tools/GraphReader.MarkerContextSmokeCapture/SmokeBinding.cs",
            "tools/GraphReader.MarkerContextSmokeCapture/SmokeRunner.cs",
            "tools/GraphReader.MarkerContextSmokeCapture/CaptureSelfTests.cs",
            "tools/GraphReader.MarkerContextSmokeCapture/capture-protocol.json",
            "tools/GraphReader.MarkerContextSmokeCapture/Prepare-CaptureBinding.ps1",
        ];
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != expected.Length)
            throw new InvalidDataException("Capture tool source inventory differs.");
        for (int i = 0; i < expected.Length; i++)
        {
            JsonElement row = rows[i];
            CenterBinding.Exact(row, "path", "sha256");
            string path = row.GetProperty("path").GetString() ?? "";
            string sha = row.GetProperty("sha256").GetString() ?? "";
            if (!string.Equals(path, expected[i], StringComparison.Ordinal) || !FixedSha(sha))
                throw new InvalidDataException("Capture tool source row is not canonical.");
            byte[] bytes = baseBinding.LockRead(root, CenterBinding.Under(root, path));
            if (Hash(bytes) != sha) throw new InvalidDataException($"Capture source differs: {path}");
        }
    }

    private static void ValidatePinnedInput(JsonElement descriptor, string descriptorPathField, string descriptorShaField,
        JsonElement protocol, string protocolPathField, string protocolShaField, string requiredPath)
    {
        _ = CanonicalPath(descriptor, descriptorPathField, requiredPath);
        string expectedSha = protocol.GetProperty(protocolShaField).GetString() ?? "";
        if (!FixedSha(expectedSha) || Sha(descriptor, descriptorShaField) != expectedSha)
            throw new InvalidDataException($"Pinned {descriptorPathField} identity differs.");
    }

    private static string CanonicalPath(JsonElement item, string field, string expected)
    {
        string value = item.GetProperty(field).GetString() ?? "";
        if (!string.Equals(value.Replace('\\', '/'), expected.Replace('\\', '/'), StringComparison.Ordinal))
            throw new InvalidDataException($"Path {field} differs from fixed project-relative identity.");
        return CenterBinding.Under(RootPath, value);
    }

    internal static void ValidateRuntimeOverlay(byte[] parentBytes, byte[] derivedBytes, string runtimeDirectory,
        string assemblyPath, string assemblySha)
    {
        const string expectedRuntimeDirectory = "artifacts/goal22-runs/marker-context-bound-input-smoke-runtime-v4";
        const string expectedAssemblyPath = expectedRuntimeDirectory + "/GraphReader.RealAcceptance.Ocr.dll";
        if (runtimeDirectory != expectedRuntimeDirectory || assemblyPath != expectedAssemblyPath || !FixedSha(assemblySha))
            throw new InvalidDataException("Derived runtime location or executable identity is not the fixed smoke runtime.");
        using JsonDocument parentDocument = JsonDocument.Parse(parentBytes);
        using JsonDocument derivedDocument = JsonDocument.Parse(derivedBytes);
        JsonElement parent = parentDocument.RootElement;
        JsonElement derived = derivedDocument.RootElement;
        CenterBinding.Exact(parent, "schema", "scope", "raster_manifest_sha256", "builder_binding_sha256",
            "artifact_configuration_sha256", "artifact_app_sha256", "artifact_ocr_sha256", "ocr_detection",
            "ocr_recognition", "marker_center", "managed_files", "native_files", "tool_sources", "training_approved");
        CenterBinding.Exact(derived, "schema", "scope", "raster_manifest_sha256", "builder_binding_sha256",
            "artifact_configuration_sha256", "artifact_app_sha256", "artifact_ocr_sha256", "ocr_detection",
            "ocr_recognition", "marker_center", "managed_files", "native_files", "tool_sources", "training_approved");
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (property.Name is "managed_files" or "native_files") continue;
            if (!derived.TryGetProperty(property.Name, out JsonElement candidate) || !SameJson(property.Value, candidate))
                throw new InvalidDataException($"Derived runtime binding changed protected parent field {property.Name}.");
        }
        ValidateManagedOverlay(parent.GetProperty("managed_files"), derived.GetProperty("managed_files"), runtimeDirectory, assemblySha);
        ValidateNativeOverlay(parent.GetProperty("native_files"), derived.GetProperty("native_files"), runtimeDirectory);
    }

    private static void ValidateManagedOverlay(JsonElement parent, JsonElement derived, string runtimeDirectory, string assemblySha)
    {
        if (parent.ValueKind != JsonValueKind.Array || derived.ValueKind != JsonValueKind.Array ||
            parent.GetArrayLength() != derived.GetArrayLength())
            throw new InvalidDataException("Derived managed runtime inventory differs in size.");
        var permittedChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GraphReader.RealAcceptance.Ocr.dll", "GraphReader.RealAcceptance.Ocr.deps.json",
            "GraphReader.RealAcceptance.Ocr.runtimeconfig.json",
        };
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < parent.GetArrayLength(); i++)
        {
            JsonElement oldRow = parent[i]; JsonElement newRow = derived[i];
            CenterBinding.Exact(oldRow, "file", "sha256");
            CenterBinding.Exact(newRow, "file", "sha256");
            string oldName = Path.GetFileName(oldRow.GetProperty("file").GetString()) ?? throw new InvalidDataException("Managed parent filename is empty.");
            string newName = Path.GetFileName(newRow.GetProperty("file").GetString()) ?? throw new InvalidDataException("Managed derived filename is empty.");
            string oldSha = oldRow.GetProperty("sha256").GetString() ?? "";
            string newSha = newRow.GetProperty("sha256").GetString() ?? "";
            string expectedPath = runtimeDirectory + "/" + oldName;
            if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) ||
                newRow.GetProperty("file").GetString() != expectedPath || !FixedSha(newSha))
                throw new InvalidDataException("Derived managed runtime path or name differs.");
            if (!string.Equals(oldSha, newSha, StringComparison.Ordinal))
            {
                if (!permittedChanged.Contains(oldName)) throw new InvalidDataException($"Unapproved managed runtime change: {oldName}");
                changed.Add(oldName);
            }
            if (string.Equals(oldName, "GraphReader.RealAcceptance.Ocr.dll", StringComparison.OrdinalIgnoreCase) && newSha != assemblySha)
                throw new InvalidDataException("Derived friend assembly SHA differs from executing assembly.");
        }
        string[] names = derived.EnumerateArray().Select(row => Path.GetFileName(row.GetProperty("file").GetString()) ?? "")
            .ToArray();
        if (!changed.Contains("GraphReader.RealAcceptance.Ocr.dll") ||
            !names.Contains("GraphReader.RealAcceptance.Ocr.deps.json", StringComparer.OrdinalIgnoreCase) ||
            !names.Contains("GraphReader.RealAcceptance.Ocr.runtimeconfig.json", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Derived runtime must pin the new friend assembly and retain host metadata rows.");
    }

    private static void ValidateNativeOverlay(JsonElement parent, JsonElement derived, string runtimeDirectory)
    {
        if (parent.ValueKind != JsonValueKind.Array || derived.ValueKind != JsonValueKind.Array ||
            parent.GetArrayLength() != derived.GetArrayLength())
            throw new InvalidDataException("Derived native runtime inventory differs in size.");
        for (int i = 0; i < parent.GetArrayLength(); i++)
        {
            JsonElement oldRow = parent[i]; JsonElement newRow = derived[i];
            CenterBinding.Exact(oldRow, "role", "file", "sha256");
            CenterBinding.Exact(newRow, "role", "file", "sha256");
            string oldName = Path.GetFileName(oldRow.GetProperty("file").GetString()) ?? throw new InvalidDataException("Native parent filename is empty.");
            if (newRow.GetProperty("role").GetString() != oldRow.GetProperty("role").GetString() ||
                newRow.GetProperty("file").GetString() != runtimeDirectory + "/" + oldName ||
                newRow.GetProperty("sha256").GetString() != oldRow.GetProperty("sha256").GetString())
                throw new InvalidDataException($"Native runtime changed beyond relocation: {oldName}");
        }
    }

    private static bool SameJson(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] leftProperties = left.EnumerateObject().ToArray();
                JsonProperty[] rightProperties = right.EnumerateObject().ToArray();
                if (leftProperties.Length != rightProperties.Length ||
                    leftProperties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != leftProperties.Length ||
                    rightProperties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != rightProperties.Length) return false;
                foreach (JsonProperty property in leftProperties)
                    if (!right.TryGetProperty(property.Name, out JsonElement value) || !SameJson(property.Value, value)) return false;
                return true;
            case JsonValueKind.Array:
                JsonElement[] leftItems = left.EnumerateArray().ToArray();
                JsonElement[] rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length && leftItems.Select((value, index) => SameJson(value, rightItems[index])).All(equal => equal);
            case JsonValueKind.String: return left.GetString() == right.GetString();
            case JsonValueKind.Number: return left.GetDecimal() == right.GetDecimal();
            case JsonValueKind.True:
            case JsonValueKind.False: return left.GetBoolean() == right.GetBoolean();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return true;
            default: return false;
        }
    }

    private static string CurrentAssemblyRelativePath(string root)
    {
        string location = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(location)) throw new InvalidDataException("Executing assembly location is unavailable.");
        string full = CenterBinding.Under(root, location);
        CenterBinding.RejectReparse(root, full);
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    private static string Sha(JsonElement value, string field)
    {
        string result = value.GetProperty(field).GetString() ?? "";
        return FixedSha(result) ? result : throw new InvalidDataException($"Invalid SHA-256 field {field}.");
    }
    private static bool FixedSha(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void ValidateScopeMetadata(string mode, int count, bool trainingApproved, bool classifierModelIncluded)
    {
        if (!string.Equals(mode, "smoke", StringComparison.Ordinal) || count != 2 || trainingApproved || classifierModelIncluded)
            throw new InvalidDataException("Two-source smoke scope must remain nonapproved and classifier-free.");
    }
    private static byte[] ReadLocked(string root, string path)
    {
        CenterBinding.RejectReparse(root, path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16_000_000) throw new InvalidDataException("Smoke binding input exceeds limit.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    public void Dispose()
    {
        BaseBinding.Dispose();
    }
}
