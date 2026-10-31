// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record FullSource(Guid SourceId, Guid CaseId, Guid SceneId, string SceneSha256, string ImageSha256,
    int Seed, string FileName, int Width, int Height, byte[] Bytes);
internal sealed record FullSmokeReferenceSource(Guid SourceId, string ImageSha256, int PanelCount, int MarkerCount, int SamplerViewCount);

internal sealed class FullBinding : IDisposable
{
    private const int MaxSourcePngBytes = 1_048_576;
    private const string RootPath = @"E:\Graph-auto-reader";
    private const string ProtocolPath = "tools/GraphReader.MarkerContextFullCapture/full-capture-protocol.json";
    private const string BaseBindingPath = "artifacts/goal22-runs/marker-context-bound-input-binding-v1.json";
    private const string BindingPath = "artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4/capture-binding.json";
    private const string CorpusBindingPath = "artifacts/goal22-runs/marker-context-train-corpus-binding-v5.json";
    private const string CompletePath = "artifacts/goal22-runs/marker-context-train-corpus-v5-full/COMPLETE.json";
    private const string IdentityPath = "artifacts/goal22-runs/marker-context-train-corpus-v5-full/identity-metadata.json";
    private const string RasterManifestPath = "artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only/manifest.json";
    private const string SmokeReportPath = "artifacts/goal22-runs/marker-context-bound-input-smoke-capture-v4/report.json";
    private const string ExpectedBaseBindingSha = "704f5448f7dead44b898d2f9eb709e1a0040c86ad085cde7003fec0a3b15bc8d";
    private const string ExpectedCorpusBindingSha = "f095bac6f76a9a2d444869ba4d75538e76237c399d3b98997c97292b4cebebd4";
    private const string ExpectedCompleteSha = "957e2bec1d45b49665e05c6442e90fa4285d27e02140b8458f2f5066de001019";
    private const string ExpectedIdentitySha = "2ca7fce16eee67d7f040e62f6979ebc76c83bcec6bafad03e67e381ce83d2055";
    private const string ExpectedRasterSha = "be0d63fb22796fd07ba83e5ad714888f136875b180883c1b36d0481cbf5433f2";
    private const string ExpectedSmokeReportSha = "c5e48426d47cfa256f36b8dcdea615a9a7debc35f475b0584e6b92064d6dede9";
    private const string ExpectedProtocolSha = "aa131bfa849d8332917ce7ad4c001bf00daea1e9a6f27861a20a0b0d06b5c4d4";
    private bool reviewApprovalValidated;
    private readonly List<FileStream> sourceLocks;

    internal string Root { get; } = RootPath;
    internal string Digest { get; }
    internal string ProtocolSha { get; }
    internal string AssemblySha { get; }
    internal string ParentBindingSha { get; } = ExpectedBaseBindingSha;
    internal string RuntimeBindingSha { get; }
    internal string CorpusBindingSha { get; } = ExpectedCorpusBindingSha;
    internal string SmokeReportSha { get; }
    internal string OutputRelativePath { get; }
    internal IReadOnlyList<FullSource> Sources { get; }
    internal IReadOnlyList<FullSmokeReferenceSource> SmokeReference { get; }
    internal CenterBinding BaseBinding { get; }
    internal bool ReviewApprovalValidated => reviewApprovalValidated;
    internal static string RuntimeSummary => "reviewed detector, recognizer, and V27 center model/license closure; serial CPU inference after external review receipt; marker classifier excluded";

    private FullBinding(string digest, string protocolSha, string assemblySha, string runtimeBindingSha, string smokeReportSha, string output,
        IReadOnlyList<FullSource> sources, IReadOnlyList<FullSmokeReferenceSource> smokeReference,
        List<FileStream> sourceLocks, CenterBinding baseBinding)
    {
        Digest = digest;
        ProtocolSha = protocolSha;
        AssemblySha = assemblySha;
        RuntimeBindingSha = runtimeBindingSha;
        SmokeReportSha = smokeReportSha;
        OutputRelativePath = output;
        Sources = sources;
        SmokeReference = smokeReference;
        this.sourceLocks = sourceLocks;
        BaseBinding = baseBinding;
    }

    internal static FullBinding Load(string descriptorPath, string expectedSha, CancellationToken token)
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
        var sourceLocks = new List<FileStream>(64);
        try
        {
            descriptorDocument = JsonDocument.Parse(descriptorBytes);
            JsonElement descriptor = descriptorDocument.RootElement;
            CenterBinding.Exact(descriptor, "schema", "scope", "protocol_path", "protocol_sha256",
                "base_binding_path", "base_binding_sha256", "runtime_binding_path", "runtime_binding_sha256",
                "corpus_binding_path", "corpus_binding_sha256", "smoke_report_path", "smoke_report_sha256",
                "complete_path", "complete_sha256",
                "identity_path", "identity_sha256", "raster_manifest_path", "raster_manifest_sha256",
                "source_images", "tool_sources", "assembly_path", "assembly_sha256",
                "capture_output_path", "training_approved", "marker_classifier_model_included");
            if (descriptor.GetProperty("schema").GetString() != "graphreader.marker-context-bound-input-full-capture-binding.v1" ||
                descriptor.GetProperty("scope").GetString() != "64-source-public-synthetic-train-raster-capture-no-marker-classifier" ||
                descriptor.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
                descriptor.GetProperty("marker_classifier_model_included").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Capture binding scope is not the fixed no-classifier full-corpus scope.");

            string protocolPath = CanonicalPath(descriptor, "protocol_path", ProtocolPath);
            byte[] protocolBytes = ReadLocked(root, protocolPath);
            string protocolSha = Sha(descriptor, "protocol_sha256");
            if (protocolSha != ExpectedProtocolSha || Hash(protocolBytes) != protocolSha)
                throw new InvalidDataException("Capture protocol hash differs from the reviewed full-capture protocol.");
            using JsonDocument protocolDocument = JsonDocument.Parse(protocolBytes);
            JsonElement protocol = protocolDocument.RootElement;
            ValidateProtocol(protocol);

            string basePath = CanonicalPath(descriptor, "base_binding_path", BaseBindingPath);
            string baseSha = Sha(descriptor, "base_binding_sha256");
            if (!string.Equals(baseSha, ExpectedBaseBindingSha, StringComparison.Ordinal))
                throw new InvalidDataException("Reviewed model/runtime binding identity differs.");
            byte[] parentBindingBytes = ReadLocked(root, basePath);
            if (Hash(parentBindingBytes) != baseSha) throw new InvalidDataException("Reviewed parent binding bytes differ.");
            string runtimeBindingPath = CanonicalPath(descriptor, "runtime_binding_path", "artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4/runtime-binding.json");
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
            ValidatePinnedInput(descriptor, "corpus_binding_path", "corpus_binding_sha256", protocol, "corpus_binding_path", "corpus_binding_sha256", CorpusBindingPath);
            _ = CanonicalPath(descriptor, "smoke_report_path", SmokeReportPath);
            JsonElement smokeReferenceElement = protocol.GetProperty("smoke_reference");
            if (smokeReferenceElement.GetProperty("report_path").GetString() != SmokeReportPath ||
                smokeReferenceElement.GetProperty("report_sha256").GetString() != ExpectedSmokeReportSha ||
                Sha(descriptor, "smoke_report_sha256") != ExpectedSmokeReportSha)
                throw new InvalidDataException("Pinned smoke comparison identity differs.");
            byte[] completeBytes = ReadLocked(root, CenterBinding.Under(root, CompletePath));
            byte[] identityBytes = ReadLocked(root, CenterBinding.Under(root, IdentityPath));
            byte[] rasterBytes = ReadLocked(root, CenterBinding.Under(root, RasterManifestPath));
            byte[] corpusBindingBytes = ReadLocked(root, CenterBinding.Under(root, CorpusBindingPath));
            byte[] smokeReportBytes = ReadLocked(root, CenterBinding.Under(root, SmokeReportPath));
            if (Hash(completeBytes) != Sha(descriptor, "complete_sha256") ||
                Hash(identityBytes) != Sha(descriptor, "identity_sha256") ||
                Hash(rasterBytes) != Sha(descriptor, "raster_manifest_sha256") ||
                Hash(corpusBindingBytes) != Sha(descriptor, "corpus_binding_sha256") ||
                Hash(smokeReportBytes) != Sha(descriptor, "smoke_report_sha256"))
                throw new InvalidDataException("Pinned full-corpus input or smoke reference hashes changed during preflight.");
            using JsonDocument completeDocument = JsonDocument.Parse(completeBytes);
            using JsonDocument identityDocument = JsonDocument.Parse(identityBytes);
            using JsonDocument rasterDocument = JsonDocument.Parse(rasterBytes);
            using JsonDocument corpusBindingDocument = JsonDocument.Parse(corpusBindingBytes);
            using JsonDocument smokeReportDocument = JsonDocument.Parse(smokeReportBytes);
            ValidateComplete(completeDocument.RootElement, identityDocument.RootElement, rasterDocument.RootElement,
                corpusBindingDocument.RootElement, protocol);
            ValidateSmokeReference(smokeReportDocument.RootElement, protocol);
            List<FullSource> sources = ReadSources(root, rasterDocument.RootElement, identityDocument.RootElement,
                protocol, descriptor.GetProperty("source_images"), sourceLocks);
            List<FullSmokeReferenceSource> smokeReference = ReadSmokeReference(protocol.GetProperty("smoke_reference"));
            ValidateToolSources(root, descriptor.GetProperty("tool_sources"), baseBinding);
            string assemblyRelative = CurrentAssemblyRelativePath(root);
            string protocolAssemblyPath = protocol.GetProperty("assembly_path").GetString() ?? "";
            string declaredAssembly = CanonicalPath(descriptor, "assembly_path", protocolAssemblyPath);
            if (!string.Equals(Path.GetRelativePath(root, declaredAssembly).Replace('\\', '/'), assemblyRelative, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Capture assembly location differs from descriptor.");
            string assemblySha = Sha(descriptor, "assembly_sha256");
            byte[] assemblyBytes = baseBinding.LockRead(root, CenterBinding.Under(root, declaredAssembly));
            if (Hash(assemblyBytes) != assemblySha) throw new InvalidDataException("Executing assembly hash differs.");
            string output = CanonicalPath(descriptor, "capture_output_path", "artifacts/goal22-runs/marker-context-bound-input-full-capture-v4");
            string outputFull = CenterBinding.Under(root, output);
            string expectedOutput = Path.Combine(root, "artifacts", "goal22-runs", "marker-context-bound-input-full-capture-v4");
            if (!string.Equals(outputFull, expectedOutput, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only the fixed fresh full-capture output is accepted.");
            if (File.Exists(outputFull) || Directory.Exists(outputFull)) throw new IOException("Capture output must be fresh.");
            string outputParent = Path.GetDirectoryName(outputFull) ?? throw new InvalidDataException("Output parent missing.");
            if (!Directory.Exists(outputParent)) throw new DirectoryNotFoundException("Output parent must already exist.");
            CenterBinding.RejectReparse(root, outputParent);
            var result = new FullBinding(digest, protocolSha, assemblySha, runtimeBindingSha, ExpectedSmokeReportSha,
                output, sources, smokeReference, sourceLocks, baseBinding);
            baseBinding = null;
            sourceLocks = [];
            return result;
        }
        catch
        {
            foreach (FileStream stream in sourceLocks) stream.Dispose();
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
        const string required = "artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4/review-receipt.json";
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
        if (approval.GetProperty("schema").GetString() != "graphreader.marker-context-bound-input-full-review.v1" ||
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
            "runtime_binding_path", "runtime_directory", "assembly_path", "corpus_binding_path", "corpus_binding_sha256",
            "complete_path", "complete_sha256", "identity_path", "identity_sha256", "raster_manifest_path",
            "raster_manifest_sha256", "source_count", "sources", "smoke_reference", "training_approved",
            "marker_classifier_model_included", "inference_before_review", "capture_output_path");
        if (protocol.GetProperty("schema").GetString() != "graphreader.marker-context-bound-input-full-capture-protocol.v1" ||
            protocol.GetProperty("scope").GetString() != "64-source-public-synthetic-train-raster-capture-no-marker-classifier" ||
            protocol.GetProperty("project_root").GetString() != "E:/Graph-auto-reader" ||
            protocol.GetProperty("base_binding_path").GetString() != BaseBindingPath ||
            protocol.GetProperty("base_binding_sha256").GetString() != ExpectedBaseBindingSha ||
            protocol.GetProperty("runtime_binding_path").GetString() != "artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4/runtime-binding.json" ||
            protocol.GetProperty("runtime_directory").GetString() != "artifacts/goal22-runs/marker-context-bound-input-full-runtime-v4" ||
            protocol.GetProperty("assembly_path").GetString() != "artifacts/goal22-runs/marker-context-bound-input-full-runtime-v4/GraphReader.RealAcceptance.Ocr.dll" ||
            protocol.GetProperty("corpus_binding_path").GetString() != CorpusBindingPath ||
            protocol.GetProperty("corpus_binding_sha256").GetString() != ExpectedCorpusBindingSha ||
            protocol.GetProperty("complete_path").GetString() != CompletePath ||
            protocol.GetProperty("complete_sha256").GetString() != ExpectedCompleteSha ||
            protocol.GetProperty("identity_path").GetString() != IdentityPath ||
            protocol.GetProperty("identity_sha256").GetString() != ExpectedIdentitySha ||
            protocol.GetProperty("raster_manifest_path").GetString() != RasterManifestPath ||
            protocol.GetProperty("raster_manifest_sha256").GetString() != ExpectedRasterSha ||
            protocol.GetProperty("source_count").GetInt32() != 64 ||
            protocol.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
            protocol.GetProperty("marker_classifier_model_included").ValueKind != JsonValueKind.False ||
            protocol.GetProperty("inference_before_review").ValueKind != JsonValueKind.False ||
            protocol.GetProperty("capture_output_path").GetString() != "artifacts/goal22-runs/marker-context-bound-input-full-capture-v4")
            throw new InvalidDataException("Fixed 64-source full-capture protocol identity differs.");
        ValidateScopeMetadata("full", protocol.GetProperty("source_count").GetInt32(),
            protocol.GetProperty("training_approved").GetBoolean(), protocol.GetProperty("marker_classifier_model_included").GetBoolean());
        JsonElement smoke = protocol.GetProperty("smoke_reference");
        CenterBinding.Exact(smoke, "report_path", "report_sha256", "source_count", "sources");
        if (smoke.GetProperty("report_path").GetString() != SmokeReportPath ||
            smoke.GetProperty("report_sha256").GetString() != ExpectedSmokeReportSha ||
            smoke.GetProperty("source_count").GetInt32() != 2 ||
            smoke.GetProperty("sources").ValueKind != JsonValueKind.Array || smoke.GetProperty("sources").GetArrayLength() != 2)
            throw new InvalidDataException("Pinned two-source smoke reference is malformed.");
    }

    private static void ValidateComplete(JsonElement complete, JsonElement identity, JsonElement raster,
        JsonElement corpusBinding, JsonElement protocol)
    {
        if (complete.GetProperty("schema").GetString() != "marker-context-train-corpus-complete-v5" ||
            complete.GetProperty("mode").GetString() != "full" || complete.GetProperty("source_count").GetInt32() != 64 ||
            complete.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
            complete.GetProperty("identity_sha256").GetString() != protocol.GetProperty("identity_sha256").GetString() ||
            complete.GetProperty("raster_manifest_sha256").GetString() != protocol.GetProperty("raster_manifest_sha256").GetString() ||
            complete.GetProperty("builder_binding_sha256").GetString() != ExpectedCorpusBindingSha ||
            raster.GetProperty("schema").GetString() != "marker-context-train-raster-v5" ||
            raster.GetProperty("mode").GetString() != "full" || raster.GetProperty("sources").GetArrayLength() != 64 ||
            raster.GetProperty("project_id").GetString() != "a2c2b740-45b4-565b-8d32-9c83aab84016" ||
            raster.GetProperty("builder_binding_sha256").GetString() != ExpectedCorpusBindingSha ||
            raster.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
            identity.GetProperty("schema").GetString() != "marker-context-train-identity-v5" ||
            identity.GetProperty("revision").GetString() != "marker-context-train-corpus-v5" ||
            identity.GetProperty("mode").GetString() != "full" || identity.GetProperty("sources").GetArrayLength() != 64 ||
            identity.GetProperty("project_id").GetString() != raster.GetProperty("project_id").GetString() ||
            identity.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
            corpusBinding.GetProperty("schema").GetString() != "marker-context-train-corpus-binding-v5" ||
            corpusBinding.GetProperty("revision").GetString() != "marker-context-train-corpus-v5" ||
            corpusBinding.GetProperty("execution_mode").GetString() != "full_only" ||
            corpusBinding.GetProperty("training_approved").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("V5 full corpus receipts or nonapproved scope differ from fixed protocol.");
        JsonElement trainSeeds = corpusBinding.GetProperty("train_seeds");
        if (trainSeeds.ValueKind != JsonValueKind.Array || trainSeeds.GetArrayLength() != 64)
            throw new InvalidDataException("V5 corpus binding does not contain exactly 64 train seeds.");
        var seedSet = trainSeeds.EnumerateArray().Select(value => value.GetInt32()).ToHashSet();
        var identitySeedSet = identity.GetProperty("sources").EnumerateArray().Select(value => value.GetProperty("seed").GetInt32()).ToHashSet();
        if (seedSet.Count != 64 || !seedSet.SetEquals(identitySeedSet))
            throw new InvalidDataException("V5 identity rows do not match the fixed train seed set.");
        ValidateScopeMetadata(complete.GetProperty("mode").GetString() ?? "", complete.GetProperty("source_count").GetInt32(),
            complete.GetProperty("training_approved").GetBoolean(), classifierModelIncluded: false);
        // The completion schema may carry a truth digest, but this tool never reads its value or resolves a truth path.
    }

    private static List<FullSource> ReadSources(string root, JsonElement raster, JsonElement identity,
        JsonElement protocol, JsonElement descriptorSources, List<FileStream> sourceLocks)
    {
        JsonElement expectedSources = protocol.GetProperty("sources");
        JsonElement rasterSources = raster.GetProperty("sources");
        JsonElement identitySources = identity.GetProperty("sources");
        if (expectedSources.ValueKind != JsonValueKind.Array || expectedSources.GetArrayLength() != 64 ||
            descriptorSources.ValueKind != JsonValueKind.Array || descriptorSources.GetArrayLength() != 64 ||
            rasterSources.ValueKind != JsonValueKind.Array || rasterSources.GetArrayLength() != 64 ||
            identitySources.ValueKind != JsonValueKind.Array || identitySources.GetArrayLength() != 64)
            throw new InvalidDataException("Full capture requires exactly 64 authenticated synthetic train sources.");
        string rasterDirectory = Path.Combine(root, Path.GetDirectoryName(RasterManifestPath)!.Replace('/', Path.DirectorySeparatorChar));
        CenterBinding.RejectReparse(root, rasterDirectory);
        string[] presentNames = Directory.EnumerateFileSystemEntries(rasterDirectory)
            .Select(entry => Path.GetFileName(entry) ?? string.Empty).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var output = new List<FullSource>(64);
        var sourceIds = new HashSet<Guid>();
        var caseIds = new HashSet<Guid>();
        var sceneIds = new HashSet<Guid>();
        var imageHashes = new HashSet<string>(StringComparer.Ordinal);
        var seeds = new HashSet<int>();
        for (int i = 0; i < 64; i++)
        {
            JsonElement expected = expectedSources[i];
            JsonElement descriptorSource = descriptorSources[i];
            JsonElement rasterSource = rasterSources[i];
            JsonElement identitySource = identitySources[i];
            CenterBinding.Exact(expected, "source_id", "path", "scene_id", "scene_sha256", "image_sha256", "width", "height", "case_id", "seed");
            CenterBinding.Exact(descriptorSource, "source_id", "path", "sha256", "width", "height");
            CenterBinding.Exact(rasterSource, "file", "height", "sha256", "source_id", "width");
            CenterBinding.Exact(identitySource, "case_id", "image_sha256", "scene_id", "scene_sha256", "seed", "source_id", "source_id_status");
            string idText = expected.GetProperty("source_id").GetString() ?? "";
            string caseText = expected.GetProperty("case_id").GetString() ?? "";
            string sceneText = expected.GetProperty("scene_id").GetString() ?? "";
            string imageSha = expected.GetProperty("image_sha256").GetString() ?? "";
            string sceneSha = expected.GetProperty("scene_sha256").GetString() ?? "";
            string file = imageSha + ".png";
            string expectedPath = RasterManifestPath[..(RasterManifestPath.LastIndexOf('/') + 1)] + file;
            int width = expected.GetProperty("width").GetInt32();
            int height = expected.GetProperty("height").GetInt32();
            int seed = expected.GetProperty("seed").GetInt32();
            if (!Guid.TryParseExact(idText, "D", out Guid sourceId) ||
                !Guid.TryParseExact(caseText, "D", out Guid caseId) ||
                !Guid.TryParseExact(sceneText, "D", out Guid sceneId) ||
                !FixedSha(imageSha) || !FixedSha(sceneSha) || width != 1200 || height != 350 ||
                expected.GetProperty("path").GetString() != expectedPath ||
                descriptorSource.GetProperty("source_id").GetString() != idText ||
                descriptorSource.GetProperty("path").GetString() != expectedPath ||
                descriptorSource.GetProperty("sha256").GetString() != imageSha ||
                descriptorSource.GetProperty("width").GetInt32() != width || descriptorSource.GetProperty("height").GetInt32() != height ||
                rasterSource.GetProperty("source_id").GetString() != idText || rasterSource.GetProperty("file").GetString() != file ||
                rasterSource.GetProperty("sha256").GetString() != imageSha || rasterSource.GetProperty("width").GetInt32() != width || rasterSource.GetProperty("height").GetInt32() != height ||
                identitySource.GetProperty("source_id").GetString() != idText || identitySource.GetProperty("case_id").GetString() != caseText ||
                identitySource.GetProperty("scene_id").GetString() != sceneText || identitySource.GetProperty("scene_sha256").GetString() != sceneSha ||
                identitySource.GetProperty("image_sha256").GetString() != imageSha || identitySource.GetProperty("seed").GetInt32() != seed ||
                identitySource.GetProperty("source_id_status").GetString() != "predeclared_not_yet_imported" ||
                !sourceIds.Add(sourceId) || !caseIds.Add(caseId) || !sceneIds.Add(sceneId) || !imageHashes.Add(imageSha) || !seeds.Add(seed))
                throw new InvalidDataException($"Raster, protocol, and identity rows differ or duplicate at source index {i}.");

            string imagePath = CenterBinding.Under(root, expectedPath);
            byte[] bytes = LockSourceRead(root, imagePath, sourceLocks);
            if (Hash(bytes) != imageSha) throw new InvalidDataException($"Full source image hash differs at index {i}.");
            using var imageStream = new MemoryStream(bytes, writable: false);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(imageStream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != width || decoder.Frames[0].PixelHeight != height)
                throw new InvalidDataException($"Full source dimensions differ at index {i}.");
            output.Add(new FullSource(sourceId, caseId, sceneId, sceneSha, imageSha, seed, file, width, height, bytes));
        }
        string[] expectedNames = ["manifest.json", .. output.Select(source => source.FileName)];
        if (!presentNames.SequenceEqual(expectedNames.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("V5 raster-only directory contains missing or extra entries.");
        return output;
    }

    private static byte[] LockSourceRead(string root, string path, List<FileStream> locks)
    {
        CenterBinding.RejectReparse(root, path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length is <= 0 or > MaxSourcePngBytes)
                throw new InvalidDataException("A raster source exceeds the fixed 1 MiB per-image bound.");
            using var buffer = new MemoryStream(checked((int)stream.Length));
            stream.CopyTo(buffer);
            if (buffer.Length != stream.Length)
                throw new InvalidDataException("Raster source length changed while locked.");
            locks.Add(stream);
            return buffer.ToArray();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateSmokeReference(JsonElement report, JsonElement protocol)
    {
        JsonElement expected = protocol.GetProperty("smoke_reference");
        if (report.GetProperty("schema").GetString() != "graphreader.marker-context-bound-input-smoke-capture-report.v1" ||
            report.GetProperty("capture_binding_sha256").GetString() != "9058dabd5cd3ba9d5f821989c49e9f4b4d7066470560ff388af46a65b13b8f41" ||
            report.GetProperty("source_count").GetInt32() != 2 || report.GetProperty("event_count").GetInt32() != 2 ||
            report.GetProperty("training_approved").ValueKind != JsonValueKind.False ||
            report.GetProperty("marker_classifier_model_loaded").ValueKind != JsonValueKind.False ||
            report.GetProperty("classification_executed").ValueKind != JsonValueKind.False ||
            report.GetProperty("truth_path_resolved").ValueKind != JsonValueKind.False ||
            report.GetProperty("truth_content_read").ValueKind != JsonValueKind.False ||
            report.GetProperty("private_input_supplied").ValueKind != JsonValueKind.False ||
            report.GetProperty("sealed_input_supplied").ValueKind != JsonValueKind.False ||
            report.GetProperty("dev_input_supplied").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Reviewed smoke comparison report does not match its fixed identity/scope.");
        JsonElement referenceSources = expected.GetProperty("sources");
        JsonElement reportSources = report.GetProperty("sources");
        if (referenceSources.GetArrayLength() != 2 || reportSources.GetArrayLength() != 2)
            throw new InvalidDataException("Reviewed smoke comparison source count differs.");
        var referenceHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in referenceSources.EnumerateArray())
        {
            CenterBinding.Exact(row, "source_id", "image_sha256", "smoke_source_status", "smoke_panel_count", "smoke_marker_count", "smoke_sampler_view_count");
            string sha = row.GetProperty("image_sha256").GetString() ?? "";
            string expectedSourceId = row.GetProperty("source_id").GetString() ?? "";
            if (!FixedSha(sha) || !Guid.TryParseExact(expectedSourceId, "D", out _) || !referenceHashes.Add(sha) ||
                row.GetProperty("smoke_source_status").GetString() != "completed" ||
                row.GetProperty("smoke_panel_count").GetInt32() != 1 ||
                row.GetProperty("smoke_marker_count").GetInt32() != row.GetProperty("smoke_sampler_view_count").GetInt32())
                throw new InvalidDataException("Smoke reference source identity/result is invalid or duplicated.");
            JsonElement actual = reportSources.EnumerateArray().SingleOrDefault(source => source.GetProperty("image_sha256").GetString() == sha);
            if (actual.ValueKind != JsonValueKind.Object || actual.GetProperty("status").GetString() != row.GetProperty("smoke_source_status").GetString() ||
                actual.GetProperty("panel_count").GetInt32() != row.GetProperty("smoke_panel_count").GetInt32())
                throw new InvalidDataException("Smoke reference source result differs.");
            JsonElement panel = actual.GetProperty("panels").EnumerateArray().SingleOrDefault(item => item.GetProperty("status").GetString() == "completed");
            if (panel.ValueKind != JsonValueKind.Object || panel.GetProperty("marker_count").GetInt32() != row.GetProperty("smoke_marker_count").GetInt32() ||
                panel.GetProperty("sampler_view_count").GetInt32() != row.GetProperty("smoke_sampler_view_count").GetInt32())
                throw new InvalidDataException("Smoke reference candidate/view counts differ.");
        }
        JsonElement fullSources = protocol.GetProperty("sources");
        foreach (JsonElement row in referenceSources.EnumerateArray())
        {
            JsonElement full = fullSources.EnumerateArray().SingleOrDefault(source => source.GetProperty("image_sha256").GetString() == row.GetProperty("image_sha256").GetString());
            if (full.ValueKind != JsonValueKind.Object || full.GetProperty("source_id").GetString() != row.GetProperty("source_id").GetString())
                throw new InvalidDataException("Smoke reference does not uniquely join to the expected v5 full source.");
        }
    }

    private static List<FullSmokeReferenceSource> ReadSmokeReference(JsonElement smoke)
    {
        var result = new List<FullSmokeReferenceSource>(2);
        foreach (JsonElement row in smoke.GetProperty("sources").EnumerateArray())
        {
            if (!Guid.TryParseExact(row.GetProperty("source_id").GetString(), "D", out Guid sourceId))
                throw new InvalidDataException("Smoke reference full-corpus source ID is invalid.");
            result.Add(new FullSmokeReferenceSource(sourceId,
                Sha(row, "image_sha256"),
                row.GetProperty("smoke_panel_count").GetInt32(),
                row.GetProperty("smoke_marker_count").GetInt32(),
                row.GetProperty("smoke_sampler_view_count").GetInt32()));
        }
        if (result.Count != 2 || result.Select(row => row.ImageSha256).Distinct(StringComparer.Ordinal).Count() != 2)
            throw new InvalidDataException("Smoke comparison must identify exactly two unique source images.");
        return result;
    }

    private static void ValidateToolSources(string root, JsonElement rows, CenterBinding baseBinding)
    {
        string[] expected =
        [
            "tools/GraphReader.MarkerContextFullCapture/GraphReader.MarkerContextFullCapture.csproj",
            "tools/GraphReader.MarkerContextFullCapture/Program.cs",
            "tools/GraphReader.MarkerContextFullCapture/FullBinding.cs",
            "tools/GraphReader.MarkerContextFullCapture/FullRunner.cs",
            "tools/GraphReader.MarkerContextFullCapture/FullCaptureSelfTests.cs",
            "tools/GraphReader.MarkerContextFullCapture/full-capture-protocol.json",
            "tools/GraphReader.MarkerContextFullCapture/Prepare-FullCaptureBinding.ps1",
            "tools/GraphReader.MarkerContextFullCapture/README.md",
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
        const string expectedRuntimeDirectory = "artifacts/goal22-runs/marker-context-bound-input-full-runtime-v4";
        const string expectedAssemblyPath = expectedRuntimeDirectory + "/GraphReader.RealAcceptance.Ocr.dll";
        if (runtimeDirectory != expectedRuntimeDirectory || assemblyPath != expectedAssemblyPath || !FixedSha(assemblySha))
            throw new InvalidDataException("Derived runtime location or executable identity is not the fixed full-capture runtime.");
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
        if (!string.Equals(mode, "full", StringComparison.Ordinal) || count != 64 || trainingApproved || classifierModelIncluded)
            throw new InvalidDataException("Full corpus capture scope must remain nonapproved and classifier-free.");
    }
    private static byte[] ReadLocked(string root, string path)
    {
        CenterBinding.RejectReparse(root, path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16_000_000) throw new InvalidDataException("Full capture binding input exceeds limit.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    public void Dispose()
    {
        foreach (FileStream stream in sourceLocks) stream.Dispose();
        BaseBinding.Dispose();
    }
}
