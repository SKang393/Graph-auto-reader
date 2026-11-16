// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections.Frozen;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal const string AffineTickRefusalSchema = "graphreader.real-dev-affine-tick-refusal.v1";
    internal const string AffineSpanPropertiesSchema = "graphreader.real-dev-affine-span-properties.v1";
    private const string AffineSpanSelectorHash = "689e013d039971febc72a31b50442b314bcdb7729217e061c387aca0dbea8781";
    private const string AffineSpanReportHash = "ab503d592c58c1e1ab1ac315dfe5511bfa4b4b89ae9bea04be39a851f2cea717";
    private const string AffineTickRefusalExeHash = "d79bac03aef6e3cb61d110e6506220aabdbfb445d8eda1a9e67361bb9f26b898";
    private const string AffineTickRefusalDllHash = "811d3c5c84c34f5112595dc52f25c90769212ac678896eb9949099c2e6d1b787";
    private const string AffineTickRefusalGeometryRustHash = "2cc6f263a00544243291f2605031c4df7e147175bd345480bd0a44f92fde0b7a";
    private const string AffineTickRefusalDiagnosticRustHash = "922bbc579e7cb593527c3c0a9bb9bb5274f6b164625bb5f2715b1aa49bb68f84";
    private static readonly FrozenDictionary<string, string> AffineTickRefusalLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GraphReader.App.dll"] = "811d3c5c84c34f5112595dc52f25c90769212ac678896eb9949099c2e6d1b787",
            ["GraphReader.Axis.dll"] = "50bfe7a205affee5b2dff59ffdf73ebcbf19460d074b03dd03971b6190b682f9",
            ["GraphReader.Domain.dll"] = "75351973fae3ca7c436f5430a0f162f0852b5943c34aacd7e28bdcab4115296e",
            ["GraphReader.Export.dll"] = "4db8ba6c1322f46956b13b5f44b6324ffc75705bd4d9d87098b2ca64238ec31a",
            ["GraphReader.Imaging.dll"] = "27d5044e20f2baaef3ee872d882331cd6d97c555add091c076ce8ae703c05b1d",
            ["GraphReader.Inference.dll"] = "d613ab5dec78b383c08fbe066035f5b23d8178d16a44fbdb36090c2cdc5ee14f",
            ["GraphReader.Legends.dll"] = "e830aadce846b5bc2661e8747525967c568d8c523d108d0e9becb60f578a0fa3",
            ["GraphReader.Markers.dll"] = "0a56a1f984311e9aaed01341102f19b407179aa87535cdd233d960eac08d7b2e",
            ["GraphReader.Ocr.dll"] = "c8b9a684cb3056fc80cc807d1fe60e0ea9a45a07aee74a802d0fcfbcf5625633",
            ["GraphReader.Pdf.dll"] = "f9eacff563e25fbceebe59e943e7607f3a9daa5e8cc1d5608a06b1666b740aa7",
            ["GraphReader.Phases.dll"] = "e87b2bc245062502dff2624fc8ed4fd7cbb18ca369c9eb75a13f13ea79326ba9",
            ["GraphReader.SuperResolution.dll"] = "1ae0c0261f6681425e2a925969a2cbd5eb658bcaff81724b7680de080b573a18",
            ["Microsoft.ML.OnnxRuntime.dll"] = "5e83437cb5c97d17fc959f35982ce1f0e90281515dafbacbc158b27bc2e972a3",
            ["onnxruntime.dll"] = "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            ["onnxruntime_providers_shared.dll"] = "265c8daf29637cb259cac8be9f08f2cd45f3883f0f0e4949cbfddd5b4cbec3b6",
            ["OpenCvSharp.dll"] = "a26fd4bfff04fe2a351b3b0b417cc9847ef8e10721a8e484e3b33850f510b509",
            ["OpenCvSharpExtern.dll"] = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31",
            ["libwebp.dll"] = "3de66254c8c0f47f299c7d55750589bf0e9fb6ab65778ed004a81e958ad1c024",
            ["libwebpdemux.dll"] = "af9124fc82821b7bef5d526d4dc4d857de7601833305f5d01ad68089706852c9",
            ["libwebpmux.dll"] = "60947b85e84cb1e96f12e5863dab742641e51846713857063274eb93326672a6",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private sealed partial class Candidate
    {
        internal static Candidate VerifyAffineTickRefusal(string app, string assets, string geometryRust,
            string diagnosticRust, CancellationToken token)
        {
            RequireNativeRejectionPins(AffineTickRefusalExeHash, AffineTickRefusalDllHash,
                AffineTickRefusalGeometryRustHash, AffineTickRefusalLibraries);
            Candidate result = VerifyPinned(app, assets, geometryRust, AffineTickRefusalExeHash,
                AffineTickRefusalDllHash, AffineTickRefusalGeometryRustHash, AffineTickRefusalLibraries, token);
            try { _ = result.Pin(diagnosticRust, AffineTickRefusalDiagnosticRustHash, token); return result; }
            catch { result.Dispose(); throw; }
        }
    }
}
