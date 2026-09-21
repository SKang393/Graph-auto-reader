// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerEnclosedCenterRecoveryTests
{
    private static readonly MarkerPolygon Plot = new([new(1, 1), new(63, 1), new(63, 40), new(1, 40)]);

    [TestMethod]
    public void ConnectedOutlinesProduceTwoMeasuredInteriorCenters()
    {
        byte[] pixels = Blank();
        Ring(pixels, 10, 10, 9);
        Ring(pixels, 26, 10, 9);
        for (int x = 18; x <= 26; x++) pixels[14 * 64 + x] = 0;
        byte[] before = pixels.ToArray();
        IReadOnlyList<MarkerCenter> result = Find(pixels);
        CollectionAssert.AreEquivalent(new[] { new MarkerPoint(14, 14), new MarkerPoint(30, 14) },
            result.Select(static marker => marker.Center).ToArray());
        Assert.IsTrue(result.All(static marker => marker.Radius == 4.5 &&
            marker.ReviewState == MarkerReviewState.NeedsReview && marker.CenterConfidence == 0.5 &&
            marker.SourceImage == MarkerSourceImage.Original));
        CollectionAssert.AreEqual(before, pixels);
    }

    [TestMethod]
    public void OpenOutlineAndBlankPixelsCannotInventCenters()
    {
        byte[] pixels = Blank();
        Ring(pixels, 10, 10, 9);
        pixels[14 * 64 + 10] = 255;
        Assert.IsEmpty(Find(pixels));
        Assert.IsEmpty(Find(Blank()));
    }

    [TestMethod]
    public void ImageBoundaryCannotCloseAnOutline()
    {
        byte[] pixels = Blank();
        Ring(pixels, 0, 10, 9);
        for (int y = 11; y < 18; y++) pixels[y * 64] = 255;
        Assert.IsEmpty(Find(pixels));
    }

    [TestMethod]
    public void TinyAndOversizedInteriorsAreNotMarkerProposals()
    {
        byte[] pixels = Blank();
        Ring(pixels, 4, 4, 3);
        Ring(pixels, 20, 8, 27);
        Assert.IsEmpty(Find(pixels));
    }

    [TestMethod]
    public void TextLegendAndPlotExclusionsUseOriginalCoordinates()
    {
        byte[] pixels = Blank();
        Ring(pixels, 10, 10, 9);
        Ring(pixels, 26, 10, 9);
        Ring(pixels, 44, 40, 7);
        var text = new OcrRegion("annotation", OcrPolygon.FromRectangle(new(10, 10, 9, 9)), "note", [],
            OcrTextRole.Annotation, 0.9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        Assert.IsEmpty(ProductionMarkerEnclosedCenterRecovery.Find(Image(pixels), Plot, [text], [],
            [new OcrRectangle(26, 10, 9, 9)], CancellationToken.None));
    }

    [TestMethod]
    public void ExistingCentersRemainUnchangedAndAreNotDuplicated()
    {
        byte[] pixels = Blank();
        Ring(pixels, 10, 10, 9);
        var center = new MarkerCenter("existing", new(13, 14), 4.5, 0, 0.95, MarkerSourceImage.Original);
        var marker = new ClassifiedMarker(center, MarkerShape.Square, MarkerFill.Open,
            "square", "Open square", 0.01, 0.99, 0.99, []);
        Assert.IsEmpty(ProductionMarkerEnclosedCenterRecovery.Find(Image(pixels), Plot, [], [marker], [], CancellationToken.None));
        Assert.AreEqual(new MarkerPoint(13, 14), marker.Marker.Center);
        Assert.AreEqual(MarkerReviewState.Unreviewed, marker.Marker.ReviewState);
    }

    [TestMethod]
    public void PaddedImageStridePreservesMeasurements()
    {
        byte[] pixels = Blank();
        Ring(pixels, 10, 10, 9);
        byte[] padded = Enumerable.Repeat((byte)17, 70 * 48).ToArray();
        for (int y = 0; y < 48; y++) pixels.AsSpan(y * 64, 64).CopyTo(padded.AsSpan(y * 70, 64));
        var image = new OcrImage(64, 48, 70, padded, OcrSourceImage.Original, OcrFrameTransform.Identity);
        Assert.AreEqual(new MarkerPoint(14, 14), ProductionMarkerEnclosedCenterRecovery.Find(
            image, Plot, [], [], [], CancellationToken.None).Single().Center);
    }

    [TestMethod]
    public void InvalidCoordinatesAndCancellationFailBeforeRecovery()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerEnclosedCenterRecovery.Find(
            Image(Blank()) with { OriginalToImage = new(2, 2, 0, 0) }, Plot, [], [], [], CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionMarkerEnclosedCenterRecovery.Find(
            Image(Blank()), Plot, [], [], [], new CancellationToken(canceled: true)));
    }

    private static IReadOnlyList<MarkerCenter> Find(byte[] pixels) =>
        ProductionMarkerEnclosedCenterRecovery.Find(Image(pixels), Plot, [], [], [], CancellationToken.None);
    private static OcrImage Image(byte[] pixels) => new(64, 48, 64, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    private static byte[] Blank() => Enumerable.Repeat((byte)255, 64 * 48).ToArray();
    private static void Ring(byte[] pixels, int left, int top, int size)
    {
        for (int offset = 0; offset < size; offset++)
        {
            pixels[top * 64 + left + offset] = 0;
            pixels[(top + size - 1) * 64 + left + offset] = 0;
            pixels[(top + offset) * 64 + left] = 0;
            pixels[(top + offset) * 64 + left + size - 1] = 0;
        }
    }
}
