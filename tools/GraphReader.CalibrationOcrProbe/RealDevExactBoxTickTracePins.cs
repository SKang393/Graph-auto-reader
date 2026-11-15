// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string ExactBoxTickTraceSchema = "graphreader.real-dev-exact-box-tick-trace.v1";
    private const string ExactBoxTickBaseCommit = "1ca57645e2eda111143ce9c09895f8ef661db482";
    // Frozen source runtime, independently hashed before selector access.
    private const string ExactBoxTickAppExeHash = "843b614c434aafd98d9818ea0e3aed0cd309fb433f5261553300b3d0f9710933";
    private const string ExactBoxTickAppDllHash = "33a0047728c9184273bf7a3ce45ce304f36eae9f4b03e66657194dd561b403bd";
    private const string ExactBoxTickRustHash = "958a14dd74db3cef3c3c726d46581f8a4b35c04b01d71b4349a0dc04c5263e52";
    private static readonly FrozenDictionary<string, string> ExactBoxTickRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "33a0047728c9184273bf7a3ce45ce304f36eae9f4b03e66657194dd561b403bd",
            ["GraphReader.Axis.dll"] = "e92ac585d9c4690507b82752455a3ec0ade0d0b41aafe68aede89d22236cea84",
            ["GraphReader.Domain.dll"] = "839e3e1a7ee552323790a14b9b038b36fbb916372567833ad3063c598c2da6c9",
            ["GraphReader.Export.dll"] = "97457c5c0026cd1b0f287f7386c4abc7d224aca5f96b9dd980e670ae0c58c8a2",
            ["GraphReader.Imaging.dll"] = "d5c34834bce3fdefec677503e17e3bd27f956c11ae6568be62c2e28e36477f95",
            ["GraphReader.Inference.dll"] = "2ea0f1d3db18f75d8e7d425b0b5a88e7cdc8de6cc7ff34d9ad9cb69cb4abe0e5",
            ["GraphReader.Legends.dll"] = "56e2638e5db7f435273007a6143dd2c3f475de6dff974ece8061ad38d8d75652",
            ["GraphReader.Markers.dll"] = "d57d6f0a595ed77d689470f7f0406db0adf5d581ea805d6f7c758587dc6ea489",
            ["GraphReader.Ocr.dll"] = "94a86738374cb4724ee98e30403fc9619f43b10a5ec9e27d4ba6028cfe2a4c67",
            ["GraphReader.Pdf.dll"] = "244347238132c8e3efbc1e4c877cbd1e7d81e262d9c9adf8afe8811dc0dba691",
            ["GraphReader.Phases.dll"] = "4c86263daba7733567ab708ec1e3c23a8ee82ebc02d5c40949ad35aabb913878",
            ["GraphReader.SuperResolution.dll"] = "b5f3c7319bdf286509cc8c89913f85df307653ebb57a50bcfb1a331d23ae606e",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object ExactBoxTickTraceIdentity() => new {
        kind = "unpackaged_exact_box_tick_source_runtime", base_source_commit = ExactBoxTickBaseCommit,
        app_exe_sha256 = ExactBoxTickAppExeHash, app_dll_sha256 = ExactBoxTickAppDllHash,
        rust_sha256 = ExactBoxTickRustHash, runtime_files = ExactBoxTickRuntimeLibraries,
        model_sha256 = Program.ModelHash, manifest_sha256 = Program.ManifestHash,
        diagnostic_scope = "automatic_geometry_exact_box_ticks_literal_ocr_and_review_drafts",
        binding = "production_helper_on_transient_suggestion_not_workspace_currentness" };

    private static MethodInfo ResolveExactBoxBinder()
    {
        MethodInfo? method = typeof(ManualPreviewWorkspaceService).GetMethod("TryBindMeasuredTicks",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null,
            [typeof(AxisTickMeasurementResult), typeof(WorkspaceAxisSuggestion), typeof(string), typeof(int), typeof(int)], null);
        if (method is null || method.ReturnType != typeof(RustAxisGeometry)) throw new InvalidDataException();
        return method;
    }

    private sealed class ExactBoxTickTrace(string rust, MethodInfo binder)
    {
        private readonly RustAxisTickMeasurementService service = new(rust);
        internal async Task<(RustAxisGeometry Geometry, object Stage)> MeasureAsync(byte[] image, int width, int height,
            RustAxisGeometry geometry, CancellationToken token)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string hash = Convert.ToHexStringLower(SHA256.HashData(image));
            AxisTickMeasurementResult measurement = await service.MeasureAsync(image, geometry, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            stopwatch.Stop();
            return BindExactBox(binder, geometry, hash, width, height, measurement, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static (RustAxisGeometry Geometry, object Stage) BindExactBox(MethodInfo binder, RustAxisGeometry geometry,
        string hash, int width, int height, AxisTickMeasurementResult measurement, double milliseconds)
    {
        var suggestion = new WorkspaceAxisSuggestion("diagnostic", "diagnostic", geometry) { Provider = "opencv-review" };
        RustAxisGeometry? measured = (RustAxisGeometry?)binder.Invoke(null, [measurement, suggestion, hash, width, height]);
        string? error = measured is null ? SafeTickMeasurementError(measurement.ErrorCode ?? "INVALID_TICK_EVIDENCE") : null;
        return (measured ?? geometry, new { attempted = true, error_code = error,
            bound = measured is not null, x_tick_count = measured?.XTickPositions.Count ?? 0,
            y_tick_count = measured?.YTickPositions.Count ?? 0, milliseconds });
    }

    private static string? SafeTickMeasurementError(string? error) => error switch {
        null => null, "INVALID_INPUT" or "INVALID_PROTOCOL" or "CPU_BUDGET_UNAVAILABLE" or "AXIS_BOX_NOT_OBSERVED" or
        "UNSUPPORTED_PLATFORM" or "WORKER_START_FAILED" or "WORKER_TIMEOUT" or "WORKER_START_OR_IO_FAILED" or
        "TICK_IMAGE_DECODE_FAILED" or "INVALID_TICK_EVIDENCE" => error, _ => "UNKNOWN_FAILURE" };

    internal static object ExactBoxTickTraceSelfTest()
    {
        _ = RankedReviewTraceSelfTest();
        try { RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal)); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        MethodInfo binder = ResolveExactBoxBinder();
        var geometry = new RustAxisGeometry(10, 90, 90, 10, .5);
        string hash = new('a', 64);
        var evidence = new RustAxisTickEvidence(10, 90, 90, 10, [20, 50, 80], [20, 50, 80]);
        var valid = new AxisTickMeasurementResult(hash, 100, 100, evidence, null);
        var (bound, boundStage) = BindExactBox(binder, geometry, hash, 100, 100, valid, 0);
        if (!bound.XTickPositions.SequenceEqual(evidence.XTickPositions) || !bound.YTickPositions.SequenceEqual(evidence.YTickPositions)) throw new InvalidDataException();
        var (mismatch, _) = BindExactBox(binder, geometry, hash, 100, 100, valid with { PanelImageSha256 = new string('b', 64) }, 0);
        var (failure, failureStage) = BindExactBox(binder, geometry, hash, 100, 100, valid with { ErrorCode = "AXIS_BOX_NOT_OBSERVED" }, 0);
        var stage = System.Text.Json.JsonSerializer.SerializeToElement(failureStage, Program.Json);
        var successful = System.Text.Json.JsonSerializer.SerializeToElement(boundStage, Program.Json);
        if (!ReferenceEquals(mismatch, geometry) || !ReferenceEquals(failure, geometry) ||
            stage.GetProperty("error_code").GetString() != "AXIS_BOX_NOT_OBSERVED" || stage.GetProperty("bound").GetBoolean() ||
            successful.GetProperty("x_tick_count").GetInt32() != 3 || successful.GetProperty("y_tick_count").GetInt32() != 3) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-exact-box-tick-trace-self-test.v1", binding_helper_checked = true,
            mismatch_and_errors_rejected = true, missing_pins_rejected = true, private_reads = 0 };
    }
}
