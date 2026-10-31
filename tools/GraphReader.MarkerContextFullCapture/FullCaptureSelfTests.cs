// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Text.Json;
using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FullCaptureSelfTests
{
    internal static void Run()
    {
        AcceptsOnlyNonapprovedFullScope();
        ReviewReceiptIsExactAndClassifierFree();
        DerivedRuntimePreservesReviewedModels();
        CaptureOutcomeSeparatesAccountingFromSuccess();
        InitializationProbesPrecedeCorpusInference();
        NoClassificationIsAClosedStub();
    }

    private static void AcceptsOnlyNonapprovedFullScope()
    {
        FullBinding.ValidateScopeMetadata("full", 64, trainingApproved: false, classifierModelIncluded: false);
        Throws(() => FullBinding.ValidateScopeMetadata("smoke", 2, false, false), "Smoke scope must not pass full-capture admission.");
        Throws(() => FullBinding.ValidateScopeMetadata("dev", 64, false, false), "Dev inputs must not pass full-capture admission.");
        Throws(() => FullBinding.ValidateScopeMetadata("full", 63, false, false), "A truncated source denominator must fail closed.");
        Throws(() => FullBinding.ValidateScopeMetadata("full", 64, true, false), "Training approval must remain false.");
        Throws(() => FullBinding.ValidateScopeMetadata("full", 64, false, true), "Classifier model inclusion must remain false.");
    }

    private static void ReviewReceiptIsExactAndClassifierFree()
    {
        const string binding = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string protocol = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string assembly = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        using JsonDocument accepted = JsonDocument.Parse(Receipt(binding, protocol, assembly, false, false));
        FullBinding.ValidateReviewReceipt(accepted.RootElement, binding, protocol, assembly);
        using JsonDocument wrongBinding = JsonDocument.Parse(Receipt("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", protocol, assembly, false, false));
        Throws(() => FullBinding.ValidateReviewReceipt(wrongBinding.RootElement, binding, protocol, assembly),
            "A receipt for a different binding must be rejected.");
        using JsonDocument classifier = JsonDocument.Parse(Receipt(binding, protocol, assembly, true, false));
        Throws(() => FullBinding.ValidateReviewReceipt(classifier.RootElement, binding, protocol, assembly),
            "A receipt permitting classifier use must be rejected.");
        using JsonDocument training = JsonDocument.Parse(Receipt(binding, protocol, assembly, false, true));
        Throws(() => FullBinding.ValidateReviewReceipt(training.RootElement, binding, protocol, assembly),
            "A receipt approving training must be rejected.");
    }

    private static string Receipt(string binding, string protocol, string assembly, bool classifier, bool training) => $$"""
        {"schema":"graphreader.marker-context-bound-input-full-review.v1",
         "capture_binding_sha256":"{{binding}}","protocol_sha256":"{{protocol}}","assembly_sha256":"{{assembly}}",
         "reviewed":true,"allow_model_inference":true,"marker_classifier_model_loaded":{{classifier.ToString().ToLowerInvariant()}},"training_approved":{{training.ToString().ToLowerInvariant()}}}
        """;

    private static void DerivedRuntimePreservesReviewedModels()
    {
        const string runtime = "artifacts/goal22-runs/marker-context-bound-input-full-runtime-v4";
        const string assemblySha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        byte[] parent = RuntimeBinding("artifacts/goal22-runs/marker-context-bound-input-runtime-v1", "parent");
        byte[] derived = RuntimeBinding(runtime, "derived");
        FullBinding.ValidateRuntimeOverlay(parent, derived, runtime, runtime + "/GraphReader.RealAcceptance.Ocr.dll", assemblySha);
        Throws(() => FullBinding.ValidateRuntimeOverlay(parent, RuntimeBinding(runtime, "changed-app"), runtime,
            runtime + "/GraphReader.RealAcceptance.Ocr.dll", assemblySha), "Derived runtime must preserve app dependencies.");
        Throws(() => FullBinding.ValidateRuntimeOverlay(parent, RuntimeBinding(runtime, "changed-center"), runtime,
            runtime + "/GraphReader.RealAcceptance.Ocr.dll", assemblySha), "Derived runtime must preserve marker-center identity.");
        Throws(() => FullBinding.ValidateRuntimeOverlay(parent, derived, runtime + "-other",
            runtime + "/GraphReader.RealAcceptance.Ocr.dll", assemblySha), "Unbound runtime location must be rejected.");
    }

    private static byte[] RuntimeBinding(string directory, string variant)
    {
        string friend = variant == "parent" ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" :
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        string app = variant == "changed-app" ? "1111111111111111111111111111111111111111111111111111111111111111" :
            "1212121212121212121212121212121212121212121212121212121212121212";
        string center = variant == "changed-center" ? "different-center" : "center";
        string parentDir = "artifacts/goal22-runs/marker-context-bound-input-runtime-v1";
        string selectedDir = variant == "parent" ? parentDir : directory;
        var managed = new object[]
        {
            new { file = $"{selectedDir}/GraphReader.RealAcceptance.Ocr.dll", sha256 = friend },
            new { file = $"{selectedDir}/GraphReader.RealAcceptance.Ocr.deps.json", sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            new { file = $"{selectedDir}/GraphReader.RealAcceptance.Ocr.runtimeconfig.json", sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" },
            new { file = $"{selectedDir}/GraphReader.App.dll", sha256 = app },
        };
        var native = new object[]
        {
            new { role = "apphost", file = $"{selectedDir}/GraphReader.RealAcceptance.Ocr.exe", sha256 = "1313131313131313131313131313131313131313131313131313131313131313" },
            new { role = "opencvsharp_extern", file = $"{selectedDir}/OpenCvSharpExtern.dll", sha256 = "1414141414141414141414141414141414141414141414141414141414141414" },
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
            marker_center = new { model_id = center }, managed_files = managed, native_files = native,
            tool_sources = Array.Empty<object>(), training_approved = false,
        });
    }

    private static void NoClassificationIsAClosedStub()
    {
        var stub = new NoClassification();
        Assert(!stub.IsApproved, "Classifier stub must remain unapproved.");
        Assert(ThrowsGetter(() => _ = stub.AdapterId), "Adapter identity must not expose a classifier.");
        Assert(ThrowsGetter(() => _ = stub.Model), "No classifier model identity may be available.");
    }

    private static void CaptureOutcomeSeparatesAccountingFromSuccess()
    {
        var success = new FullCaptureOutcome(64, 64, 0);
        Assert(success.SourceAccountingComplete && success.AllSourcesSucceeded &&
            success.Status == "completed_all_sources", "All-source success must have a distinct status.");
        var failures = new FullCaptureOutcome(64, 61, 3);
        Assert(failures.SourceAccountingComplete && !failures.AllSourcesSucceeded &&
            failures.Status == "completed_with_source_failures", "Accounted failures must not be reported as success.");
        var partial = new FullCaptureOutcome(17, 17, 0);
        Assert(!partial.SourceAccountingComplete && !partial.AllSourcesSucceeded &&
            partial.Status == "incomplete_source_accounting", "Partial source accounting must not be reported as complete.");
    }

    private static void InitializationProbesPrecedeCorpusInference()
    {
        var state = new FullCaptureInferenceState();
        Assert(!state.InitializationProbeStarted && !state.InitializationProbeCompleted &&
            state.CorpusPanelInferenceStartCount == 0, "Inference starts must be false before model initialization.");
        ThrowsInvalidOperation(() => state.MarkCorpusPanelInferenceStarted(), "Corpus inference must wait for initialization probes.");
        state.MarkInitializationProbeStarted();
        Assert(state.InitializationProbeStarted && !state.InitializationProbeCompleted,
            "Initialization probe start must be recorded before adapter construction.");
        state.MarkInitializationProbeCompleted();
        state.MarkCorpusPanelInferenceStarted();
        state.MarkCorpusPanelInferenceStarted();
        Assert(state.InitializationProbeCompleted && state.CorpusPanelInferenceStartCount == 2,
            "Corpus panel inference must be counted separately from initialization probes.");
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static bool ThrowsGetter(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }

    private static void ThrowsInvalidOperation(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
