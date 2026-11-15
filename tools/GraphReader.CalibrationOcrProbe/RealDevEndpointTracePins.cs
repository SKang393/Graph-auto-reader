// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;
using System.IO;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    private const string EndpointTraceSchema = "graphreader.real-dev-endpoint-review-trace.v2";
    // Frozen unpackaged endpoint source runtime, independently hashed.
    private const string EndpointAppExeHash = "47b68cee094370d6d8a5c291ec73bf5cd2bc6234c69ab5f330360e42dc4a2199";
    private const string EndpointAppDllHash = "b585892bd357067cc8086d610364a559c85e069475ed0a806893db6fd35125d8";
    private const string EndpointRustHash = "9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9";
    private static readonly FrozenDictionary<string, string> EndpointRuntimeLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "b585892bd357067cc8086d610364a559c85e069475ed0a806893db6fd35125d8",
            ["GraphReader.Axis.dll"] = "ffc2a5ae5aed62bfcb7278add536c4c9f6d08f5e6ab4268f3bbf0c368bfdd924",
            ["GraphReader.Domain.dll"] = "25e92e3850edbdd05dfa944b8e5c13cebebd46be7ec6d9468c8dfd44ebeb08d8",
            ["GraphReader.Export.dll"] = "fcca0881aad8b5213a98b988bad142b3d81eb31698dceded7f03a74c685ce4ba",
            ["GraphReader.Imaging.dll"] = "552ca2e2f745f4ae5b5b0c64274ad4d012008eaffd1d772e0079f2dd6817f080",
            ["GraphReader.Inference.dll"] = "21641e6ebc05dcdecd0ce19ea4748e992742f052dcf0ed747571bc73a6519dd0",
            ["GraphReader.Legends.dll"] = "cc8d7935878e52af2c5e33bb727c305a79f61444979c56d76e85fbfb93a82e76",
            ["GraphReader.Markers.dll"] = "df3e44b7b8992daf4386e19990019368703f744ee01f6412e0aebc0e0aac8de7",
            ["GraphReader.Ocr.dll"] = "81a01e9bf0e51a8523f569d745a69ded76860db1aee32fb4bb5ae998a29c076d",
            ["GraphReader.Pdf.dll"] = "d89605e7760c3d8827b183c9cdd9ea1414edf7187b2a1c3c8a969681dba10c02",
            ["GraphReader.Phases.dll"] = "518c33e5cbb3727cb7be2643d04cb84952a788940680ce79c2043a18e66e7eeb",
            ["GraphReader.SuperResolution.dll"] = "6002062daa265a1fad61b7457784e12606f6c07baf2485580ad4b93426d5d7ba",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static object EndpointTraceIdentity() => new {
        kind = "unpackaged_endpoint_source_runtime", app_exe_sha256 = EndpointAppExeHash,
        app_dll_sha256 = EndpointAppDllHash, rust_sha256 = EndpointRustHash,
        runtime_files = EndpointRuntimeLibraries, model_sha256 = Program.ModelHash,
        manifest_sha256 = Program.ManifestHash, diagnostic_scope = "automatic_geometry_literal_ocr_and_review_drafts",
        association_tolerance_policy = "legacy_symmetric_plus_directional_toward_label_offset" };

    private static void ValidateEndpointTraceFlags(bool sourcePostGrouping, bool geometryOnly, bool incompatible)
    {
        if (!sourcePostGrouping || geometryOnly || incompatible) throw new InvalidDataException();
    }

    internal static object EndpointTraceSelfTest()
    {
        _ = SpatialTraceSelfTest();
        ValidateEndpointTraceFlags(true, false, false);
        foreach (var flags in new[] { (false, false, false), (true, true, false), (true, false, true) })
        {
            try { ValidateEndpointTraceFlags(flags.Item1, flags.Item2, flags.Item3); throw new InvalidOperationException(); }
            catch (InvalidDataException) { }
        }
        try { RequireNativeRejectionPins("", "", "", new Dictionary<string, string>(StringComparer.Ordinal)); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        TestDirectionalAssociation();
        return new { schema = "graphreader.real-dev-endpoint-trace-self-test.v1", incompatible_flags_rejected = true,
            missing_pins_rejected = true, printed_x_and_terminal_accounting = true, directional_association_checked = true, private_reads = 0 };
    }

    private static void TestDirectionalAssociation()
    {
        var geometry = new GraphReader.Axis.RustAxisGeometry(10, 90, 110, 10, .9) { XTickPositions = [10, 20, 60, 100] };
        var request = new GraphReader.App.Services.CalibrationOcrRequest("owned", "owned", new string('a', 64), new string('a', 64), 130, 120, new(10, 10, 110, 90));
        Check(27, true, 5, 10, 1);
        Check(40, false, null, null, 2);
        Check(110, true, 10, 10, 1);

        void Check(double center, bool expectedAssociated, double? legacy, double? directional, int ties)
        {
            var row = new GraphReader.App.Services.CalibrationOcrProposal("owned", [new(center - 6, 94), new(center + 6, 94),
                new(center + 6, 104), new(center - 6, 104)], "1", .9, 1, "1", true, "XTick", "X", null);
            var response = new GraphReader.App.Services.CalibrationOcrResponse(request, [row], null, null, null, null, null);
            var proposal = GraphReader.App.Services.CalibrationTickProposal.CreateReviewDraft(geometry, response);
            var trace = System.Text.Json.JsonSerializer.SerializeToElement(TraceRow(row, geometry, proposal, true), Program.Json);
            if (trace.GetProperty("nearest_tick_ties").GetInt32() != ties || trace.GetProperty("associated").GetBoolean() != expectedAssociated ||
                Number(trace.GetProperty("association_tolerance")) != legacy || Number(trace.GetProperty("directional_association_tolerance")) != directional)
                throw new InvalidDataException();
            var historical = System.Text.Json.JsonSerializer.SerializeToElement(TraceRow(row, geometry, proposal), Program.Json);
            if (historical.TryGetProperty("directional_association_tolerance", out _)) throw new InvalidDataException();
        }
        static double? Number(System.Text.Json.JsonElement value) => value.ValueKind == System.Text.Json.JsonValueKind.Null ? null : value.GetDouble();
    }
}
