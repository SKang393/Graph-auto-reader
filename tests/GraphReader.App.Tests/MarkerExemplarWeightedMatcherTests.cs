// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Services;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class MarkerExemplarWeightedMatcherTests
{
    [TestMethod]
    [DataRow(5)]
    [DataRow(13)]
    [DataRow(65)]
    public void GaussianWeightsRetainEveryPixelAndTrackFractionalSeedTranslation(int size)
    {
        double offsetX = size / 2d - .25, offsetY = size / 2d + .125;
        float[] weights = MarkerExemplarMatcher.CreateWeights(size, size, offsetX, offsetY);
        Assert.AreEqual(size * size, weights.Length);
        Assert.IsTrue(weights.All(value => float.IsFinite(value) && value > 0 && value <= 1));
        Assert.IsTrue(weights[0] < weights[(size / 2) * size + size / 2]);
        float[] translated = MarkerExemplarMatcher.CreateWeights(size, size, offsetX + 1, offsetY);
        int middle = (size / 2) * size + size / 2;
        Assert.AreEqual(weights[middle], translated[middle + 1]);
        Assert.AreEqual(weights[0], translated[1]);
    }

    [TestMethod]
    public void InvalidNativeScoresAndNeighborsCannotBecomeOrSuppressFinitePeaks()
    {
        float[] scores = [float.NaN, float.PositiveInfinity, 3, float.NegativeInfinity, .95f, -4, 0, 0, 0];
        Assert.IsTrue(MarkerExemplarMatcher.IsPeak(scores, 3, 3, 1, 1, out float score));
        Assert.AreEqual(.95f, score);
        Assert.IsFalse(MarkerExemplarMatcher.IsPeak(scores, 3, 3, 1, 0, out _));
        Assert.IsFalse(MarkerExemplarMatcher.IsPeak(scores, 3, 3, 2, 0, out _));
        scores[0] = .96f;
        Assert.IsFalse(MarkerExemplarMatcher.IsPeak(scores, 3, 3, 1, 1, out _), "A valid stronger neighbor still suppresses a peak.");
    }

    [TestMethod]
    public void OnlyDeclaredFloatRoundoffCanNormalizeToOne()
    {
        float[] scores = [1 + MarkerExemplarMatcher.CorrelationRoundoff / 2];
        Assert.IsTrue(MarkerExemplarMatcher.IsPeak(scores, 1, 1, 0, 0, out float accepted));
        Assert.AreEqual(1f, accepted);
        scores[0] = 1 + MarkerExemplarMatcher.CorrelationRoundoff * 2;
        Assert.IsFalse(MarkerExemplarMatcher.IsPeak(scores, 1, 1, 0, 0, out _));
        scores[0] = .899f;
        Assert.IsFalse(MarkerExemplarMatcher.IsPeak(scores, 1, 1, 0, 0, out _), "The .9 similarity cutoff remains unchanged.");
    }

    [TestMethod]
    public void HollowExampleMatchesHollowGlyphWithoutAcceptingFilledOrCrossDistractors()
    {
        const int width = 128, height = 96;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        foreach (int x in new[] { 24, 72, 104 })
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int radius = dx * dx + dy * dy;
                    if (radius <= 16 && (x == 104 || radius >= 9)) pixels[(48 + dy) * width + x + dx] = 0;
                }
        for (int delta = -8; delta <= 8; delta++)
        {
            pixels[20 * width + 48 + delta] = 0;
            pixels[(20 + delta) * width + 48] = 0;
        }
        byte[] before = pixels.ToArray();
        var request = new MarkerExemplarRequest("owned-weighted", new string('a', 64), new string('b', 64),
            new string('c', 64), "panel", width, height, new(18, 42, 13, 13), new(8, 8, 112, 80),
            "seed", "series", new(24.25, 48.5));
        IReadOnlyList<MarkerExemplarCandidate> matches = MarkerExemplarMatcher.Find(request, pixels, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.AreEqual(new MarkerExemplarPoint(72.25, 48.5), matches[0].Center);
        Assert.IsTrue(double.IsFinite(matches[0].Similarity) && matches[0].Similarity >= .9 && matches[0].Similarity <= 1);
        CollectionAssert.AreEqual(before, pixels);
    }

    [TestMethod]
    public void UniformExampleStillFailsBeforeWeightedNormalization()
    {
        var request = new MarkerExemplarRequest("owned-flat", new string('a', 64), new string('b', 64),
            new string('c', 64), "panel", 32, 32, new(10, 10, 13, 13), new(0, 0, 32, 32), "seed", "series", new(16, 16));
        MarkerExemplarException error = Assert.ThrowsExactly<MarkerExemplarException>(() =>
            MarkerExemplarMatcher.Find(request, Enumerable.Repeat((byte)255, 32 * 32).ToArray(), CancellationToken.None));
        Assert.AreEqual(MarkerExemplarReasonCodes.UninformativeExemplar, error.Reason);
    }
}
