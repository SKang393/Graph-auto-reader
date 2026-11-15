// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string NativeWorkTraceSchema = "graphreader.real-dev-native-work-geometry-trace.v1";
    // Unpackaged diagnostic source based on 20edf2f4; this is not a 0.4.53 package.
    private const string NativeWorkAppExeHash = "083d7b10c74057fda6ba0ccc036825ee52b284ea55b145db96659bfc4e837622";
    private const string NativeWorkAppDllHash = "956ca84ae81d40322872184f7be2960da67e0f63081a3ce33d615c31168c91b5";
    private const string NativeWorkRustHash = "cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd";
    private static readonly FrozenDictionary<string, string> NativeWorkRuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "956ca84ae81d40322872184f7be2960da67e0f63081a3ce33d615c31168c91b5",
        ["GraphReader.Axis.dll"] = "74c694b12cfb4ebc6ac87ce651972360832e17177ab88fb408fd4d41c92dfd23",
        ["GraphReader.Domain.dll"] = "ea7c59d75907308fbc85441325c67040ddc56c091cd9ba891f0f4da42a15b732",
        ["GraphReader.Export.dll"] = "e68176a239f2c26ded26024a38a31fd568d67e77aa6c8a1639001deb1817ffb4",
        ["GraphReader.Imaging.dll"] = "59a637ee734006f389c86218d79fd3476aa002d46cdb63eb6ce8fa46fe688e57",
        ["GraphReader.Inference.dll"] = "12872d874038b50f964b6eedf8255260eaf7a40774bbefe13d30826564c0ff91",
        ["GraphReader.Legends.dll"] = "c5e933ed5923f151a9dbc24711cb62103bdbf6552e31962f1e54bc65678c650e",
        ["GraphReader.Markers.dll"] = "8f36a07357cfbcfb49dbcf36b5fe958332bd392a1bb943f854375570953ffe14",
        ["GraphReader.Ocr.dll"] = "36af49542b57d237b632c35abcf20ffef9f275f985699dac332d5b8bd9f37984",
        ["GraphReader.Pdf.dll"] = "dce18f69b4def2dde22003e83104124314b1456aeba4acf9c606d90becc88363",
        ["GraphReader.Phases.dll"] = "f3c033347f659c3a68e6f2db78664424daabc54ad4979ed2008ca816a700a518",
        ["GraphReader.SuperResolution.dll"] = "8ec87bb31d5281b08aea53b6fd23671f5d736b0f1978b811a07839d96d6449c4",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object NativeWorkTraceIdentity() => new
    {
        kind = "unpackaged_source_runtime",
        app_exe_sha256 = NativeWorkAppExeHash,
        app_dll_sha256 = NativeWorkAppDllHash,
        rust_sha256 = NativeWorkRustHash,
        runtime_files = NativeWorkRuntimeLibraries,
        native_execution = "in_process_single_thread_parent_cpu_budget",
        native_timing_comparable_to_worker = false,
        ocr_calls = 0,
    };

    internal static async Task<object> NativeWorkTraceSelfTestAsync(string app, string assets, string rust, CancellationToken token)
    {
        _ = NativeRejectionTraceSelfTest();
        try
        {
            RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal));
            throw new InvalidOperationException();
        }
        catch (InvalidDataException) { }
        using Candidate candidate = Candidate.VerifyNativeWork(app, assets, rust, token);
        OpenCvSharp.Cv2.SetNumThreads(1);
        if (OpenCvSharp.Cv2.GetNumThreads() != 1) throw new InvalidDataException();
        const int width = 800, height = 400;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 100; x <= 700; x++) pixels[300 * width + x] = 0;
        for (int y = 50; y <= 300; y++) pixels[y * width + 100] = 0;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] bytes = stream.ToArray();
        RustAxisProposalResult withoutObserver = await NativeAxisWorker.ProcessAsync(bytes, token).ConfigureAwait(false);
        var observer = new NativeWorkTraceAxis();
        RustAxisProposalResult withObserver = await observer.SuggestAsync(bytes, token).ConfigureAwait(false);
        if (withoutObserver.Geometry is null || observer.LastSnapshot is not { EnumerationCompleted: true, Cancelled: false,
                FailureReason: null, BudgetKind: AxisGeometryBudgetKind.None, InputLineCount: > 0 } snapshot ||
            snapshot.AcceptedLineCount > snapshot.InputLineCount || snapshot.ExactPairRequests != snapshot.ExactPairAttempts ||
            snapshot.ExactPairAttempts > 128 || snapshot.AdmittedPairs > snapshot.ScoredPairs ||
            snapshot.HorizontalContactRejections + snapshot.VerticalContactRejections + snapshot.ScoredPairs != snapshot.ExactPairAttempts ||
            JsonSerializer.Serialize(withoutObserver) != JsonSerializer.Serialize(withObserver)) throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-native-work-self-test.v1",
            missing_pins_rejected_before_selector = true, owned_observer_on_off_parity = true,
            owned_count_stage_invariants = true, actual_loaded_opencv_verified = true, private_reads = 0 };
    }
}
