// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerTemplateRecoveryTests
{
    private static readonly MarkerPolygon Plot = new([new(1, 1), new(63, 1), new(63, 35), new(1, 35)]);
    private static readonly IReadOnlyDictionary<string, MarkerRectangle> NoLegends = new Dictionary<string, MarkerRectangle>();

    [TestMethod]
    public void OffCenterDetectionUsesTheCompleteMeasuredGlyph()
    {
        OcrImage image = Image(new OcrRectangle(10, 10, 7, 7));
        IReadOnlyList<OcrRectangle> templates = ProductionMarkerTemplateRecovery.SelectTemplates(
            image, Plot, [Marker("off-center", 11, 11)], NoLegends, CancellationToken.None);
        Assert.AreEqual(new OcrRectangle(10, 10, 7, 7), templates.Single());
        Assert.AreEqual(new OcrPoint(13.5, 13.5), templates.Single().Center);
    }

    [TestMethod]
    public void DetectionBetweenTwoGlyphsCannotBecomeATemplate()
    {
        OcrImage image = Image(new(10, 10, 7, 7), new(21, 10, 7, 7));
        Assert.IsEmpty(ProductionMarkerTemplateRecovery.SelectTemplates(
            image, Plot, [Marker("gap", 19, 13)], NoLegends, CancellationToken.None));
    }

    [TestMethod]
    public void AmbiguousAndConnectedComponentsCannotBecomeTemplates()
    {
        OcrImage image = Image(new(10, 10, 7, 7), new(16, 13, 35, 1), new(0, 20, 7, 7));
        Assert.IsEmpty(ProductionMarkerTemplateRecovery.SelectTemplates(
            image, Plot, [Marker("connected", 13, 13), Marker("edge", 3, 23)], NoLegends, CancellationToken.None));
        Assert.IsEmpty(ProductionMarkerTemplateRecovery.SelectTemplates(
            Image(new OcrRectangle(10, 10, 7, 7)), Plot,
            [Marker("first", 12, 12), Marker("second", 14, 14)], NoLegends, CancellationToken.None));
    }

    [TestMethod]
    public void MatchingRecoversOriginalPixelsAndExcludesTextAndOutsidePlot()
    {
        OcrImage image = Image(new(10, 10, 7, 7), new(30, 20, 7, 7), new(50, 15, 7, 7), new(20, 40, 7, 7));
        byte[] before = image.Pixels.ToArray();
        OcrRegion text = new("annotation", OcrPolygon.FromRectangle(new(49, 14, 9, 9)), "note", [],
            OcrTextRole.Annotation, 0.9, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);
        IReadOnlyList<MarkerCenter> result = ProductionMarkerTemplateRecovery.Find(
            image, Plot, [text], [Marker("existing", 13.5, 13.5)], NoLegends, [], CancellationToken.None);
        Assert.HasCount(1, result);
        Assert.AreEqual(new MarkerPoint(33.5, 23.5), result[0].Center);
        Assert.AreEqual(MarkerSourceImage.Original, result[0].SourceImage);
        CollectionAssert.AreEqual(before, image.Pixels.ToArray());
    }

    [TestMethod]
    public void ARepeatedLegendGlyphStaysOutsidePlottedObservations()
    {
        OcrImage image = Image(new(10, 10, 7, 7), new(30, 20, 7, 7));
        IReadOnlyList<MarkerCenter> result = ProductionMarkerTemplateRecovery.Find(
            image, Plot, [], [Marker("existing", 13.5, 13.5)], NoLegends,
            [new(29, 19, 9, 9)], CancellationToken.None);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void ClassifierRejectionAndDuplicateSuppressionPreserveExistingCenters()
    {
        ClassifiedMarker existing = Marker("existing", 10, 10);
        ClassifiedMarker recovered = Marker("new", 30, 20);
        IReadOnlyList<ClassifiedMarker> result = ProductionMarkerTemplateRecovery.SelectNew(
            [existing], [Marker("near-existing", 11, 10), recovered, Marker("duplicate", 31, 20),
                Marker("artifact", 50, 20, artifact: 0.5)], 0.5, CancellationToken.None);
        Assert.HasCount(1, result);
        Assert.AreSame(recovered, result[0]);
        Assert.AreEqual(new MarkerPoint(10, 10), existing.Marker.Center);
        Assert.AreEqual(0.01, existing.ArtifactProbability);
    }

    [TestMethod]
    public void MissingPixelEvidenceCannotInventCenters()
    {
        Assert.IsEmpty(ProductionMarkerTemplateRecovery.Find(
            Image(), Plot, [], [Marker("no-ink", 10, 10)], NoLegends, [], CancellationToken.None));
    }

    [TestMethod]
    public void NonOriginalCoordinatesAndCancellationFailBeforeSearch()
    {
        OcrImage transformed = Image(new OcrRectangle(10, 10, 7, 7)) with { OriginalToImage = new(2, 2, 0, 0) };
        Assert.ThrowsExactly<ArgumentException>(() => ProductionMarkerTemplateRecovery.Find(
            transformed, Plot, [], [], NoLegends, [], CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionMarkerTemplateRecovery.Find(
            Image(), Plot, [], [], NoLegends, [], new CancellationToken(canceled: true)));
    }

    private static OcrImage Image(params OcrRectangle[] boxes)
    {
        const int width = 64, height = 48;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        foreach (OcrRectangle box in boxes)
            for (int y = (int)box.Top; y < box.Bottom; y++)
                for (int x = (int)box.Left; x < box.Right; x++) pixels[y * width + x] = 0;
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }

    private static ClassifiedMarker Marker(string id, double x, double y, double artifact = 0.01) => new(
        new MarkerCenter(id, new(x, y), 3.5, 0, 0.95, MarkerSourceImage.Original),
        MarkerShape.Square, MarkerFill.Filled, "square", "Filled square", artifact, 0.99, 0.99, []);
}
