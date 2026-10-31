// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Text.Json;
using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal static class CaptureSelfTests
{
    internal static void Run()
    {
        ScopeValidation.AcceptsReviewedSmokeOnly();
        ScopeValidation.RejectsApprovalOrClassifier();
        ReviewValidation.RejectsMismatchedApproval();
        RuntimeOverlayValidation.PreservesReviewedClosure();
        NoClassificationIsAClosedStub();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static class ScopeValidation
    {
        internal static void AcceptsReviewedSmokeOnly()
        {
            using JsonDocument document = JsonDocument.Parse("""
                {"mode":"smoke","source_count":2,"training_approved":false,"marker_classifier_model_included":false,
                 "source_ids":["93b89c7d-a10c-f159-9ac2-b0c16c63a6c4","45d0a832-1b20-df51-b156-33bc36d50242"]}
                """);
            Validate(document.RootElement);
        }

        internal static void RejectsApprovalOrClassifier()
        {
            using JsonDocument approved = JsonDocument.Parse("""
                {"mode":"smoke","source_count":2,"training_approved":true,"marker_classifier_model_included":false,
                 "source_ids":["93b89c7d-a10c-f159-9ac2-b0c16c63a6c4","45d0a832-1b20-df51-b156-33bc36d50242"]}
                """);
            Throws(() => Validate(approved.RootElement), "Training-approved input must be rejected.");
            using JsonDocument classifier = JsonDocument.Parse("""
                {"mode":"smoke","source_count":2,"training_approved":false,"marker_classifier_model_included":true,
                 "source_ids":["93b89c7d-a10c-f159-9ac2-b0c16c63a6c4","45d0a832-1b20-df51-b156-33bc36d50242"]}
                """);
            Throws(() => Validate(classifier.RootElement), "Classifier model declaration must be rejected.");
            using JsonDocument dev = JsonDocument.Parse("""
                {"mode":"dev","source_count":2,"training_approved":false,"marker_classifier_model_included":false,
                 "source_ids":["93b89c7d-a10c-f159-9ac2-b0c16c63a6c4","45d0a832-1b20-df51-b156-33bc36d50242"]}
                """);
            Throws(() => Validate(dev.RootElement), "Non-smoke input must be rejected.");
            using JsonDocument duplicate = JsonDocument.Parse("""
                {"mode":"smoke","source_count":2,"training_approved":false,"marker_classifier_model_included":false,
                 "source_ids":["93b89c7d-a10c-f159-9ac2-b0c16c63a6c4","93b89c7d-a10c-f159-9ac2-b0c16c63a6c4"]}
                """);
            Throws(() => Validate(duplicate.RootElement), "Duplicate source identity must be rejected.");
        }

        private static void Validate(JsonElement value)
        {
            SmokeBinding.ValidateScopeMetadata(value.GetProperty("mode").GetString() ?? "",
                value.GetProperty("source_count").GetInt32(), value.GetProperty("training_approved").GetBoolean(),
                value.GetProperty("marker_classifier_model_included").GetBoolean());
            JsonElement ids = value.GetProperty("source_ids");
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 2 ||
                ids.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count() != 2)
                throw new InvalidDataException("Two unique source IDs are required.");
        }
    }

    private static class ReviewValidation
    {
        private const string Binding = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Protocol = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Assembly = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        internal static void RejectsMismatchedApproval()
        {
            using JsonDocument accepted = JsonDocument.Parse(Receipt(Binding, Protocol, Assembly, false));
            SmokeBinding.ValidateReviewReceipt(accepted.RootElement, Binding, Protocol, Assembly);
            using JsonDocument wrongBinding = JsonDocument.Parse(Receipt("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", Protocol, Assembly, false));
            Throws(() => SmokeBinding.ValidateReviewReceipt(wrongBinding.RootElement, Binding, Protocol, Assembly),
                "Approval for a different binding must be rejected.");
            using JsonDocument classifier = JsonDocument.Parse(Receipt(Binding, Protocol, Assembly, true));
            Throws(() => SmokeBinding.ValidateReviewReceipt(classifier.RootElement, Binding, Protocol, Assembly),
                "Approval permitting a classifier must be rejected.");
        }

        private static string Receipt(string binding, string protocol, string assembly, bool classifier) => $$"""
            {"schema":"graphreader.marker-context-bound-input-smoke-review.v1",
             "capture_binding_sha256":"{{binding}}","protocol_sha256":"{{protocol}}","assembly_sha256":"{{assembly}}",
             "reviewed":true,"allow_model_inference":true,"marker_classifier_model_loaded":{{classifier.ToString().ToLowerInvariant()}},"training_approved":false}
            """;
    }

    private static class RuntimeOverlayValidation
    {
        private const string Runtime = "artifacts/goal22-runs/marker-context-bound-input-smoke-runtime-v4";
        private const string AssemblySha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

        internal static void PreservesReviewedClosure()
        {
            byte[] parent = Binding("artifacts/goal22-runs/marker-context-bound-input-runtime-v1", "old");
            byte[] derived = Binding(Runtime, "new-same-host");
            SmokeBinding.ValidateRuntimeOverlay(parent, derived, Runtime,
                Runtime + "/GraphReader.RealAcceptance.Ocr.dll", AssemblySha);
            Throws(() => SmokeBinding.ValidateRuntimeOverlay(parent, Binding(Runtime, "tampered-app"), Runtime,
                Runtime + "/GraphReader.RealAcceptance.Ocr.dll", AssemblySha),
                "Unchanged parent app runtime content must remain byte-pinned.");
            Throws(() => SmokeBinding.ValidateRuntimeOverlay(parent, Binding(Runtime, "new", alteredMarkerId: true), Runtime,
                Runtime + "/GraphReader.RealAcceptance.Ocr.dll", AssemblySha),
                "Derived runtime must not alter the reviewed marker model identity.");
        }

        private static byte[] Binding(string directory, string version, bool alteredMarkerId = false)
        {
            string toolDll = version == "old" ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" : AssemblySha;
            string deps = version is "old" or "new-same-host" ? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" : "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            string runtimeConfig = version is "old" or "new-same-host" ? "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" : "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
            string app = version == "tampered-app" ? "1111111111111111111111111111111111111111111111111111111111111111" : "1212121212121212121212121212121212121212121212121212121212121212";
            var managed = new object[]
            {
                new { file = $"{(version == "old" ? "artifacts/goal22-runs/marker-context-bound-input-runtime-v1" : Runtime)}/GraphReader.RealAcceptance.Ocr.dll", sha256 = toolDll },
                new { file = $"{(version == "old" ? "artifacts/goal22-runs/marker-context-bound-input-runtime-v1" : Runtime)}/GraphReader.RealAcceptance.Ocr.deps.json", sha256 = deps },
                new { file = $"{(version == "old" ? "artifacts/goal22-runs/marker-context-bound-input-runtime-v1" : Runtime)}/GraphReader.RealAcceptance.Ocr.runtimeconfig.json", sha256 = runtimeConfig },
                new { file = $"{(version == "old" ? "artifacts/goal22-runs/marker-context-bound-input-runtime-v1" : Runtime)}/GraphReader.App.dll", sha256 = app },
            };
            var native = new object[]
            {
                new { role = "apphost", file = $"{(version == "old" ? "artifacts/goal22-runs/marker-context-bound-input-runtime-v1" : Runtime)}/GraphReader.RealAcceptance.Ocr.exe", sha256 = "1313131313131313131313131313131313131313131313131313131313131313" },
                new { role = "opencvsharp_extern", file = $"{(version == "old" ? "artifacts/goal22-runs/marker-context-bound-input-runtime-v1" : Runtime)}/OpenCvSharpExtern.dll", sha256 = "1414141414141414141414141414141414141414141414141414141414141414" },
            };
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "graphreader.marker-bound-input-train-binding.v1", scope = "train-only-no-truth-no-classification-model",
                raster_manifest_sha256 = "1515151515151515151515151515151515151515151515151515151515151515",
                builder_binding_sha256 = "1616161616161616161616161616161616161616161616161616161616161616",
                artifact_configuration_sha256 = "1717171717171717171717171717171717171717171717171717171717171717",
                artifact_app_sha256 = "1818181818181818181818181818181818181818181818181818181818181818",
                artifact_ocr_sha256 = "1919191919191919191919191919191919191919191919191919191919191919",
                ocr_detection = new { model_id = "detector" }, ocr_recognition = new { model_id = "recognizer" },
                marker_center = new { model_id = alteredMarkerId ? "different-center" : "center" },
                managed_files = managed, native_files = native, tool_sources = Array.Empty<object>(), training_approved = false,
            });
        }
    }

    private static void NoClassificationIsAClosedStub()
    {
        var stub = new NoClassification();
        Assert(!stub.IsApproved, "Classifier stub must remain unapproved.");
        Assert(ThrowsGetter(() => _ = stub.AdapterId), "Adapter identity must not expose a classifier.");
        Assert(ThrowsGetter(() => _ = stub.Model), "No classifier model identity may be available.");
    }

    private static bool ThrowsGetter(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }
}
