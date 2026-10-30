// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevPositiveProposalSelfTest
{
    internal static object Run()
    {
        const int width = 16, height = 16;
        byte[] image = [1, 2, 3];
        string hash = Convert.ToHexStringLower(SHA256.HashData(image));
        EngaugePositiveCenter[] labels = [new(0, 0), new(7, 8), new(7.5, 8), new(4, 4), new(12, 4), new(15.1, 15.1)];
        var group = new EngaugeDevImageGroup(EngaugeDevStudyPartition.Train, hash, width, height, image, labels, []);
        var gray = new float[width * height];
        var text = new float[width * height];
        var artifact = new float[width * height];
        text[4 * width + 4] = 1;
        artifact[4 * width + 12] = 1;
        var planes = new EngaugeDevRuntimePlanes(hash, width, height, gray, text, artifact, labels);

        EngaugeDevPositiveProposals result = EngaugeDevPositiveProposalSelector.Select(planes, group, CancellationToken.None);
        MarkerPoint[] expected = [new(0, 0), new(4, 8), new(8, 8)];
        Require(result.ImageSha256 == hash && result.Coordinates.SequenceEqual(expected) &&
            result.UnmatchedCenterCount == 3, "positive_radius_mask_and_deduplication");
        int area = NormalizedMarkerProposalContract.PatchSize * NormalizedMarkerProposalContract.PatchSize;
        Require(result.Tensor.Length == expected.Length * 3 * area && result.Tensor.Span[0] == 0 &&
            result.Tensor.Span[16 * 33 + 16] == 1, "edge_padding_and_ink");
        int second = 3 * area;
        Require(result.Tensor.Span[second + area + 12 * 33 + 16] == 1 &&
            result.Tensor.Span[second + 2 * area + 12 * 33 + 24] == 1,
            "runtime_text_and_artifact_channels");

        var wrongIdentity = new EngaugeDevRuntimePlanes(new string('0', 64), width, height, gray, text, artifact, labels);
        ExpectMismatch(wrongIdentity, group);
        var wrongLabels = new EngaugeDevRuntimePlanes(hash, width, height, gray, text, artifact, [new(1, 1)]);
        ExpectMismatch(wrongLabels, group);
        var dev = new EngaugeDevImageGroup(EngaugeDevStudyPartition.Development, hash, width, height, image, labels, []);
        ExpectMismatch(planes, dev);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            _ = EngaugeDevPositiveProposalSelector.Select(planes, group, canceled.Token);
            throw new InvalidOperationException("ENGAUGE_POSITIVE_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        return new { status = "pass", scope = "synthetic-in-memory-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0, training_authorized = false };
    }

    private static void ExpectMismatch(EngaugeDevRuntimePlanes planes, EngaugeDevImageGroup group)
    {
        try { _ = EngaugeDevPositiveProposalSelector.Select(planes, group, CancellationToken.None); }
        catch (InvalidDataException exception) when (exception.Message == "ENGAUGE_POSITIVE_SOURCE_MISMATCH") { return; }
        throw new InvalidOperationException("ENGAUGE_POSITIVE_SOURCE_MISMATCH_NOT_REJECTED");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("ENGAUGE_POSITIVE_TEST_" + check);
    }
}
