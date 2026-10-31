// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using GraphReader.App.Integration.Workflow;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevProposalCoverageSelfTest
{
    internal static object Run()
    {
        var audit = new EngaugeDevProposalCoverageAccumulator();
        static void Add(EngaugeDevProposalCoverageAccumulator target, byte id, int width, int height,
            EngaugePositiveCenter center, float luminance, float[]? text = null, float[]? artifactSeed = null,
            float[]? residualArtifact = null, byte[]? residualCategories = null)
        {
            byte[] image = [id];
            string hash = Convert.ToHexStringLower(SHA256.HashData(image));
            EngaugePositiveCenter[] labels = [center];
            var group = new EngaugeDevImageGroup(EngaugeDevStudyPartition.Train, hash, width, height, image, labels, []);
            artifactSeed ??= new float[width * height];
            residualArtifact ??= new float[width * height];
            residualCategories ??= new byte[width * height];
            float[] artifact = artifactSeed.Zip(residualArtifact, Math.Max).ToArray();
            var planes = new EngaugeDevRuntimePlanes(hash, width, height,
                Enumerable.Repeat(luminance, width * height).ToArray(), text ?? new float[width * height], artifact,
                labels, artifactSeed, residualArtifact, residualCategories);
            target.Add(planes, group, CancellationToken.None);
        }
        Add(audit, 1, 4, 4, new(0, 0), 0);
        Add(audit, 2, 4, 4, new(3.9, 3.9), 0);
        Add(audit, 3, 4, 4, new(0, 0), 0, text: [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Add(audit, 4, 4, 4, new(0, 0), 0, artifactSeed: [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
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
        Add(audit, 9, 4, 4, new(0, 0), 0,
            residualArtifact: [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            residualCategories: [(byte)RasterResidualArtifactCategoryBits.AnnotationArrow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Add(audit, 10, 4, 4, new(0, 0), 0,
            artifactSeed: [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            residualArtifact: [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            residualCategories: [0, (byte)RasterResidualArtifactCategoryBits.Bracket, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
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
        if (result.CenterCount != 10 || result.Matched != 2 || result.NoNearbyGrid != 1 || result.TextOnly != 2 ||
            result.ArtifactOnly != 3 || result.MissingInkOnly != 1 || result.Mixed != 1 || result.Unmatched != 8 ||
            result.GeometrySeedArtifactOnly != 1 || result.ResidualArtifactOnly != 1 || result.BothArtifactOnly != 1 ||
            result.ResidualArrowOnly != 1 || result.ResidualBracketOnly != 0 || result.ResidualLegendOnly != 0 ||
            result.ResidualIntersectionOnly != 0 || result.ResidualMultiple != 0 ||
            result.ArtifactOnlyProvenanceTotal != result.ArtifactOnly ||
            result.ResidualOnlyCategoryTotal != result.ResidualArtifactOnly)
            throw new InvalidOperationException("ENGAUGE_COVERAGE_TEST_COUNTS");
        return new { status = "pass", scope = "synthetic-in-memory-only", private_payload_reads = 0,
            sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0 };
    }
}
