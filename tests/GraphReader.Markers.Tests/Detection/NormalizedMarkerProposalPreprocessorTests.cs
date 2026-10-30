// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Markers.Tests.Detection;

[TestClass]
public sealed class NormalizedMarkerProposalPreprocessorTests
{
    [TestMethod]
    public void SelectedProposalsMatchFullProductionPatchesAndEligibility()
    {
        const int size = 33;
        float[] luminance = Enumerable.Repeat(1f, size * size).ToArray();
        luminance[0] = 0f;
        luminance[16 * size + 16] = 0f;
        float[] text = new float[size * size];
        text[4] = 0.35f;
        text[8 * size + 8] = 1f;
        float[] artifact = new float[size * size];
        artifact[20 * size + 20] = 0.75f;
        var frame = new MarkerImageFrame(size, size, 1, luminance, MarkerSourceImage.Original,
            MarkerAffineTransform.Identity, new MarkerMask(size, size, text), new MarkerMask(size, size, artifact));
        var requested = new HashSet<MarkerPoint>
        {
            new(0, 0), new(0, 0), new(4, 0), new(8, 8), new(12, 16),
            new(16, 16), new(20, 20), new(32, 32),
        };

        NormalizedMarkerProposalBatch full = NormalizedMarkerProposalPreprocessor.Prepare(frame, CancellationToken.None);
        NormalizedMarkerProposalBatch selected = NormalizedMarkerProposalPreprocessor.PrepareSelected(
            frame, requested, CancellationToken.None);
        MarkerPoint[] expected = full.Coordinates.Where(requested.Contains).ToArray();
        CollectionAssert.AreEqual(expected, selected.Coordinates.ToArray());
        CollectionAssert.Contains(expected, new MarkerPoint(0, 0));
        CollectionAssert.Contains(expected, new MarkerPoint(4, 0));
        CollectionAssert.DoesNotContain(expected, new MarkerPoint(8, 8));
        CollectionAssert.DoesNotContain(expected, new MarkerPoint(20, 20));
        Assert.IsGreaterThan(selected.Count, full.Count);
        int patchValues = 3 * NormalizedMarkerProposalContract.PatchSize * NormalizedMarkerProposalContract.PatchSize;
        for (int i = 0; i < expected.Length; i++)
        {
            int source = Array.IndexOf(full.Coordinates.ToArray(), expected[i]);
            CollectionAssert.AreEqual(full.Tensor.Slice(source * patchValues, patchValues).ToArray(),
                selected.Tensor.Slice(i * patchValues, patchValues).ToArray());
        }
        Assert.AreEqual(0f, selected.Tensor.Span[0], "Out-of-image ink remains zero padded.");
        Assert.AreEqual(1f, selected.Tensor.Span[16 * size + 16], "Edge anchor retains its ink pixel.");

        NormalizedMarkerProposalBatch empty = NormalizedMarkerProposalPreprocessor.PrepareSelected(
            frame, new HashSet<MarkerPoint>(), CancellationToken.None);
        Assert.AreEqual(0, empty.Count);
        Assert.AreEqual(0, empty.Tensor.Length);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            NormalizedMarkerProposalPreprocessor.PrepareSelected(frame, requested, new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public void FrozenPythonFixtureProducesExactCoordinatesAndTensorBytes()
    {
        const int size = 33;
        float[] ink = Enumerable.Repeat(0.04f, size * size).ToArray();
        for (var y = 15; y < 18; y++)
        {
            for (var x = 15; x < 18; x++)
            {
                ink[(y * size) + x] = 0.90f;
            }
        }

        float[] text = new float[size * size];
        for (var y = 4; y < 8; y++)
        {
            for (var x = 4; x < 8; x++)
            {
                text[(y * size) + x] = 1f;
            }
        }

        float[] artifact = new float[size * size];
        for (var y = 25; y < 29; y++)
        {
            for (var x = 25; x < 29; x++)
            {
                artifact[(y * size) + x] = 0.75f;
            }
        }

        NormalizedMarkerProposalBatch batch = NormalizedMarkerProposalPreprocessor.Prepare(
            size,
            size,
            ink,
            new MarkerMask(size, size, text),
            new MarkerMask(size, size, artifact),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                new MarkerPoint(12, 8), new MarkerPoint(16, 8),
                new MarkerPoint(20, 8), new MarkerPoint(24, 8), new MarkerPoint(8, 12),
                new MarkerPoint(12, 12), new MarkerPoint(16, 12), new MarkerPoint(20, 12),
                new MarkerPoint(24, 12), new MarkerPoint(8, 16), new MarkerPoint(12, 16),
                new MarkerPoint(16, 16), new MarkerPoint(20, 16), new MarkerPoint(24, 16),
                new MarkerPoint(8, 20), new MarkerPoint(12, 20), new MarkerPoint(16, 20),
                new MarkerPoint(20, 20), new MarkerPoint(24, 20), new MarkerPoint(8, 24),
                new MarkerPoint(12, 24), new MarkerPoint(16, 24), new MarkerPoint(20, 24),
            },
            batch.Coordinates.ToArray());
        Assert.AreEqual(
            "93a4523015551a2be1340874f870073964f1dc2bc5338f25e40313865d663711",
            batch.TensorSha256);
        CollectionAssert.AreEqual(new long[] { 23, 3, 33, 33 }, batch.Shape.ToArray());

        int patchPixels = size * size;
        int centerPatchOffset = 11 * 3 * patchPixels;
        Assert.AreEqual(0f, batch.Tensor.Span[centerPatchOffset]);
        Assert.AreEqual(0.85999995f, batch.Tensor.Span[centerPatchOffset + (16 * size) + 16]);
        Assert.AreEqual(1f, batch.Tensor.Span[centerPatchOffset + patchPixels + (4 * size) + 4]);
        Assert.AreEqual(0.75f, batch.Tensor.Span[centerPatchOffset + (2 * patchPixels) + (25 * size) + 25]);
    }

    [TestMethod]
    public void ProposalAndMaskThresholdsAreInclusive()
    {
        const int size = 9;
        float[] ink = new float[size * size];
        ink[(4 * size) + 4] = NormalizedMarkerProposalContract.InkSupportThreshold;
        float[] text = new float[size * size];
        text[(4 * size) + 4] = 0.35f;

        NormalizedMarkerProposalBatch accepted = NormalizedMarkerProposalPreprocessor.Prepare(
            size,
            size,
            ink,
            new MarkerMask(size, size, text),
            MarkerMask.Empty(size, size),
            CancellationToken.None);

        Assert.HasCount(9, accepted.Coordinates);

        text[(4 * size) + 4] = MathF.BitIncrement(0.35f);
        NormalizedMarkerProposalBatch rejected = NormalizedMarkerProposalPreprocessor.Prepare(
            size,
            size,
            ink,
            new MarkerMask(size, size, text),
            MarkerMask.Empty(size, size),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                new MarkerPoint(0, 0), new MarkerPoint(4, 0), new MarkerPoint(8, 0),
                new MarkerPoint(0, 4), new MarkerPoint(8, 4),
                new MarkerPoint(0, 8), new MarkerPoint(4, 8), new MarkerPoint(8, 8),
            },
            rejected.Coordinates.ToArray());
    }

    [TestMethod]
    public void FrameEntryPointConvertsImmutableLuminanceAndBindsRevision()
    {
        const int size = 5;
        float[] luminance = Enumerable.Repeat(1f, size * size).ToArray();
        luminance[(2 * size) + 2] = 0f;
        var frame = new MarkerImageFrame(
            size,
            size,
            1,
            luminance,
            MarkerSourceImage.Original,
            MarkerAffineTransform.Identity,
            MarkerMask.Empty(size, size),
            MarkerMask.Empty(size, size));

        NormalizedMarkerProposalBatch batch =
            NormalizedMarkerProposalPreprocessor.Prepare(frame, CancellationToken.None);

        Assert.HasCount(4, batch.Coordinates);
        StringAssert.Contains(batch.CacheMaterial, NormalizedMarkerProposalContract.RuntimeRevision);
        StringAssert.Contains(batch.CacheMaterial, NormalizedMarkerProposalContract.PreprocessRevision);
        StringAssert.Contains(batch.CacheMaterial, $"tensor_sha256={batch.TensorSha256}");
        Assert.AreEqual(64, batch.TensorSha256.Length);
    }

    [TestMethod]
    public void InvalidOrCancelledInputFailsBeforeProposalMaterialization()
    {
        var cancellation = new CancellationToken(canceled: true);
        float[] valid = [0f];
        float[] invalid = [float.NaN];
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            NormalizedMarkerProposalPreprocessor.Prepare(
                1,
                1,
                valid,
                MarkerMask.Empty(1, 1),
                MarkerMask.Empty(1, 1),
                cancellation));
        Assert.ThrowsExactly<ArgumentException>(() =>
            NormalizedMarkerProposalPreprocessor.Prepare(
                1,
                1,
                invalid,
                MarkerMask.Empty(1, 1),
                MarkerMask.Empty(1, 1),
                CancellationToken.None));
    }
}
