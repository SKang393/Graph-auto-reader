// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string PostGroupingTraceSchema = "graphreader.real-dev-source-post-grouping-association-trace.v1";
    private const string PostGroupingBaseCommit = "bc513f79b09134184d5e557f473f69348ea69b07";
    private const string PostGroupingAppExeHash = "0777827c284e6dbbfc6bf675d4662c23564f93a7cab276e0fa211c56942177e4";
    private const string PostGroupingAppDllHash = "68025084f67e6bd5ab7087a94e19904316dc3adee8948869625defc170c44683";
    private const string PostGroupingRustHash = "cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd";

    // Framework-dependent source output, with the reviewed OpenCV native staged
    // before execution. It is not the delivered self-contained 452 package.
    private static readonly FrozenDictionary<string, string> PostGroupingRuntimeLibraries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GraphReader.App.dll"] = "68025084f67e6bd5ab7087a94e19904316dc3adee8948869625defc170c44683",
        ["GraphReader.Axis.dll"] = "43027c76f09df9e34cccc30bc514ee25f75ce30fd4dd788f98c34e9bc708717d",
        ["GraphReader.Domain.dll"] = "6739b72976e3bf342fa162ed2f9e150f1e4e5059190c596ec32f119036a4722f",
        ["GraphReader.Export.dll"] = "417fd33b4b4a99cf674e7c8b87a18b9fcb8076a9dddd0ec2a5d4545ad73ee90f",
        ["GraphReader.Imaging.dll"] = "9090e0fd5a5bb168dfe3a5be37b3288db4b81a0afb67f705575748eb8235da66",
        ["GraphReader.Inference.dll"] = "b301ab856d4830ca845a03b48610c4768a1ff2a91c7a57d9bdeeebdb355e807f",
        ["GraphReader.Legends.dll"] = "44ab946e9beb8398551b283a2f0ad803b103890504b0c223194bef86585b892d",
        ["GraphReader.Markers.dll"] = "3ce798d28f7607671e53642f7ef72ee477a49d9323a3ed9de02d7cb68b84c88f",
        ["GraphReader.Ocr.dll"] = "f43dfb3a29e98b10a34f91ecd3dee7f4f9fae18279223315894faa0c617fcbe1",
        ["GraphReader.Pdf.dll"] = "f2cfe7b84692933d9e3af4328ee684ba8c028b8efc232d39045adc2bc7297fda",
        ["GraphReader.Phases.dll"] = "afdc3183e2d62c85279b90d41154b3714d82da99b18037a54638b341685bd71f",
        ["GraphReader.SuperResolution.dll"] = "3afe623bfd80aef023eac9ef6a1ab06fdfe8d2b03a85750d92df1936403bc597",
        ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
        ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
        ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
        ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
        ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
        ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
        ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
        ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object PostGroupingTraceIdentity() => new
    {
        kind = "unpackaged_source",
        base_source_commit = PostGroupingBaseCommit,
        additional_source_change = "rust_ambiguity_native_review_fallback",
        runtime_deployment = "framework_dependent_net_windows_desktop",
        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        windows_desktop_assembly_version = typeof(System.Windows.Application).Assembly.GetName().Version?.ToString(),
        app_exe_sha256 = PostGroupingAppExeHash,
        app_dll_sha256 = PostGroupingAppDllHash,
        rust_sha256 = PostGroupingRustHash,
        runtime_files = PostGroupingRuntimeLibraries,
        measured_tick_context = "X_and_Y",
    };

    private static CalibrationOcrRequest TraceRequest(string projectHash, string panelHash, int width, int height,
        RustAxisGeometry geometry, bool includeMeasuredY) =>
        new("dev-trace", projectHash, projectHash, panelHash, width, height,
            new(geometry.Left, geometry.Top, geometry.Right, geometry.Bottom))
        {
            MeasuredXTicks = geometry.XTickPositions.Count > 0 ? geometry.XTickPositions.ToArray() : null,
            MeasuredYTicks = includeMeasuredY && geometry.YTickPositions.Count > 0 ? geometry.YTickPositions.ToArray() : null,
        };

    internal static object PostGroupingTraceSelfTest()
    {
        _ = Trace452SelfTest();
        var geometry = new RustAxisGeometry(10, 90, 110, 10, .9)
            { XTickPositions = [20, 60, 100], YTickPositions = [20, 50, 80] };
        string hash = new('a', 64);
        CalibrationOcrRequest legacy = TraceRequest(hash, hash, 120, 120, geometry, includeMeasuredY: false);
        CalibrationOcrRequest source = TraceRequest(hash, hash, 120, 120, geometry, includeMeasuredY: true);
        CalibrationOcrRequest roundTrip = System.Text.Json.JsonSerializer.Deserialize<CalibrationOcrRequest>(
            System.Text.Json.JsonSerializer.Serialize(source, Program.Json), Program.Json) ?? throw new InvalidDataException();
        CalibrationOcrRequest changed = roundTrip with { MeasuredYTicks = [20, 50, 81] };
        if (legacy.MeasuredYTicks is not null || source.MeasuredYTicks is null || source.MeasuredXTicks is null ||
            !source.MeasuredYTicks.SequenceEqual(geometry.YTickPositions) ||
            !source.MeasuredXTicks.SequenceEqual(geometry.XTickPositions) ||
            ReferenceEquals(source.MeasuredYTicks, geometry.YTickPositions) ||
            !source.Matches(roundTrip) || source.Matches(changed))
            throw new InvalidDataException();
        return new { schema = "graphreader.real-dev-source-post-grouping-trace-self-test.v1",
            legacy_x_only = true, source_x_y_context = true, round_trip_echo_bound = true,
            terminal_status_accounting = true, private_reads = 0 };
    }
}

