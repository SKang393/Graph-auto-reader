// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevProposalCoverageSelfTest
{
    internal static object Run()
    {
        var audit = new EngaugeDevProposalCoverageAccumulator();
        static void Add(EngaugeDevProposalCoverageAccumulator target, byte id, int width, int height,
            EngaugePositiveCenter center, float luminance, float[]? text = null, float[]? artifact = null)
        {
            byte[] image = [id];
            string hash = Convert.ToHexStringLower(SHA256.HashData(image));
            EngaugePositiveCenter[] labels = [center];
            var group = new EngaugeDevImageGroup(EngaugeDevStudyPartition.Train, hash, width, height, image, labels, []);
            var planes = new EngaugeDevRuntimePlanes(hash, width, height,
                Enumerable.Repeat(luminance, width * height).ToArray(),
                text ?? new float[width * height], artifact ?? new float[width * height], labels);
            target.Add(planes, group, CancellationToken.None);
        }
        Add(audit, 1, 4, 4, new(0, 0), 0);
        Add(audit, 2, 4, 4, new(3.9, 3.9), 0);
        Add(audit, 3, 4, 4, new(0, 0), 0, text: [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Add(audit, 4, 4, 4, new(0, 0), 0, artifact: [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Add(audit, 5, 4, 4, new(0, 0), 1);
        float[] splitText = new float[32], splitArtifact = new float[32];
        splitText[0] = 1; splitArtifact[4] = 1;
        Add(audit, 6, 8, 4, new(2, 0), 0, splitText, splitArtifact);
        float[] belowThreshold = new float[16];
        belowThreshold[0] = 0.35f;
        Add(audit, 7, 4, 4, new(0, 0), 0, belowThreshold);
        float[] aboveThreshold = new float[16];
        aboveThreshold[0] = MathF.BitIncrement(0.35f);
        Add(audit, 8, 4, 4, new(0, 0), 0, aboveThreshold);
        try { Add(audit, 8, 4, 4, new(0, 0), 0, aboveThreshold); }
        catch (InvalidDataException error) when (error.Message == "ENGAUGE_COVERAGE_DUPLICATE_IMAGE") { goto duplicateRejected; }
        throw new InvalidOperationException("ENGAUGE_COVERAGE_TEST_DUPLICATE_NOT_REJECTED");
        duplicateRejected:
        var canceledAudit = new EngaugeDevProposalCoverageAccumulator();
        byte[] image = [9];
        string digest = Convert.ToHexStringLower(SHA256.HashData(image));
        EngaugePositiveCenter[] cancelLabels = [new(0, 0)];
        var cancelGroup = new EngaugeDevImageGroup(EngaugeDevStudyPartition.Train, digest, 1, 1, image, cancelLabels, []);
        var cancelPlanes = new EngaugeDevRuntimePlanes(digest, 1, 1, [0], [0], [0], cancelLabels);
        try
        {
            canceledAudit.Add(cancelPlanes, cancelGroup, new CancellationToken(canceled: true));
            throw new InvalidOperationException("ENGAUGE_COVERAGE_TEST_CANCELLATION_NOT_OBSERVED");
        }
        catch (OperationCanceledException) { }
        if (canceledAudit.Finish().CenterCount != 0)
            throw new InvalidOperationException("ENGAUGE_COVERAGE_TEST_PARTIAL_COMMIT");
        EngaugeDevProposalCoverageCounts result = audit.Finish();
        if (result.CenterCount != 8 || result.Matched != 2 || result.NoNearbyGrid != 1 || result.TextOnly != 2 ||
            result.ArtifactOnly != 1 || result.MissingInkOnly != 1 || result.Mixed != 1 || result.Unmatched != 6)
            throw new InvalidOperationException("ENGAUGE_COVERAGE_TEST_COUNTS");
        return new { status = "pass", scope = "synthetic-in-memory-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0 };
    }
}
