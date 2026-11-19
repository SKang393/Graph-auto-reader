// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.Ocr;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionTickLabelGeometryTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InsetStrokeAtEndOfRailRequiresTwoInteriorFlankSamples(bool railPresent)
    {
        var axis = Axis();
        axis = axis with { YAxis = axis.YAxis with { Line = new(new(20, 39.1), new(20, 65)) } };
        var image = Image([], [40]);
        byte[] pixels = image.Pixels.ToArray();
        pixels[40 * image.Stride + 14] = byte.MaxValue; // Five-pixel outward tick.
        pixels[39 * image.Stride + 20] = byte.MaxValue; // Outside the fit.
        if (!railPresent) pixels[42 * image.Stride + 20] = byte.MaxValue;
        var strokes = ProductionAttachedTickStroke.Find(axis, image with { Pixels = pixels }, false, 128, new(), default);
        Assert.AreEqual(railPresent, strokes.Any(s => s.Position == 40));
    }

    [TestMethod]
    [DataRow(true, false, 0.25, true, true)]
    [DataRow(true, true, 0.25, true, true)]
    [DataRow(false, false, 0.25, true, true)]
    [DataRow(false, true, 0.25, true, true)]
    [DataRow(false, false, 0.75, true, false)]
    [DataRow(true, true, 0.75, true, false)]
    [DataRow(false, false, 0.25, false, false)]
    public void AttachedStrokeUsesSourcePixelContainingFittedEndpoint(bool horizontal, bool reverse,
        double inset, bool continuous, bool expected)
    {
        var axis = Axis();
        var image = Image(horizontal ? [50] : [], horizontal ? [] : [40], paddedStride: true);
        if (!horizontal)
        {
            byte[] pixels = image.Pixels.ToArray();
            pixels[40 * image.Stride + 14] = byte.MaxValue;
            if (!continuous) pixels[40 * image.Stride + 19] = byte.MaxValue;
            image = image with { Pixels = pixels };
        }
        // Place the stroke at the upper horizontal or lower vertical sampling bound.
        var start = horizontal ? new PixelPoint(20, 70) : new PixelPoint(20, 40 + inset);
        var end = horizontal ? new PixelPoint(50 - inset, 70) : new PixelPoint(20, 70);
        var axisLine = reverse ? new GeometryLineSegment(end, start) : new GeometryLineSegment(start, end);
        axis = horizontal ? axis with { XAxis = axis.XAxis with { Line = axisLine } }
            : axis with { YAxis = axis.YAxis with { Line = axisLine } };
        var strokes = ProductionAttachedTickStroke.Find(axis, image, horizontal, 128, new(), default);
        Assert.AreEqual(expected, strokes.Any(s => s.Position == (horizontal ? 50 : 40)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NarrowGlyphCentersAreAlignedToOriginalTickPixels(bool paddedStride)
    {
        OcrRegion[] labels = [XLabel("x"), YLabel("y")];
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], [40], paddedStride), labels, CancellationToken.None);
        Assert.AreEqual(50, result.Positions["x"]);
        Assert.AreEqual(40, result.Positions["y"]);
        Assert.AreEqual(44, labels[0].Polygon.Bounds.Center.X);
        Assert.AreEqual(37, labels[1].Polygon.Bounds.Center.Y);
        Assert.AreEqual("1", labels[0].Text);
        Assert.AreEqual("20", labels[1].Text);
        Assert.AreEqual(0.95, labels[0].Confidence);
        Assert.HasCount(2, result.Warnings);
    }

    [TestMethod]
    public void RecoveredYLabelWithoutIndependentStrokeCannotCalibrateFromItsBox()
    {
        var seed = YLabel(TickLaneTextRegionRecovery.YStrokeSeedPrefix + "glyph");
        var second = seed with { RegionId = "other", Text = "0",
            Polygon = OcrPolygon.FromRectangle(new OcrRectangle(4, 56, 8, 8)) };
        var geometry = ProductionTickLabelGeometry.Resolve(Axis(), Image([], []), [seed, second], default);
        var ocr = new OcrResult(OcrContract.Version, "run", "project", "panel", "ocr", "test", "hash",
            OcrContract.CoordinateSpace, [seed, second], [], new(0, 0, 0, 0), .95, [], new(false, "", 0, 0), null);
        var calibration = ProductionAutomaticDetectionAdapter.FitCalibration(Axis(), ocr, geometry, [], default);
        Assert.IsFalse(calibration.YTransform.IsValid);
        CollectionAssert.Contains(calibration.Reasons.ToArray(),
            $"ocr_tick_sequence_needs_review:YTick:seeded_label_unassociated:{seed.RegionId}");
    }

    [TestMethod]
    public void MissingTicksDoNotManufacturePositionEvidence()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([], []), [XLabel("x"), YLabel("y")], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void SeveralVisibleTicksRemainAmbiguous()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([40, 50], []), [XLabel("x")], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
        Assert.AreEqual("tick_label_multiple_visible_ticks:x", result.Warnings.Single());
    }

    [TestMethod]
    public void SeveralLabelsCannotClaimTheSameTick()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], []), [XLabel("first"), XLabel("second")], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
        Assert.HasCount(2, result.Warnings);
        Assert.IsTrue(result.Warnings.All(static warning => warning.StartsWith("tick_label_shared_visible_tick:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RejectedAndNonTickTextDoNotContributePositionEvidence()
    {
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(Axis(), Image([50], []),
            [XLabel("rejected") with { ReviewStatus = OcrReviewStatus.Rejected },
             XLabel("annotation") with { Role = OcrTextRole.Annotation }], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
    }

    [TestMethod]
    public void TextInTheSamplingBandDoesNotBecomeATick()
    {
        OcrRegion overlapping = XLabel("text") with { Polygon = OcrPolygon.FromRectangle(new(42, 72, 4, 12)) };
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], []), [overlapping], CancellationToken.None);
        Assert.IsEmpty(result.Positions);
    }

    [TestMethod]
    public void SlantedAxisIsSampledInItsOriginalCoordinates()
    {
        AxisGeometryResult axis = Axis() with
        {
            XAxis = new(new(new(20, 70), new(80, 73)), 0.95, 0, 1, ["slanted"]),
        };
        byte[] pixels = Enumerable.Repeat((byte)255, 100 * 100).ToArray();
        for (int y = 72; y <= 77; y++) pixels[y * 100 + 50] = 0;
        var image = new OcrImage(100, 100, 100, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(axis, image, [XLabel("x")], CancellationToken.None);
        Assert.AreEqual(50, result.Positions["x"]);
    }

    [TestMethod]
    public void WrongCoordinateSpaceAndCancellationAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProductionTickLabelGeometry.Resolve(Axis(),
            Image([50], []) with { SourceImage = OcrSourceImage.Enhanced }, [XLabel("x")], CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionTickLabelGeometry.Resolve(Axis(),
            Image([50], []) with { OriginalToImage = new(2, 2, 0, 0) }, [XLabel("x")], CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionTickLabelGeometry.Resolve(
            Axis(), Image([50], []), [XLabel("x")], new CancellationToken(canceled: true)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NumericYReadingConnectedToAxisRequiresReviewWithoutRemovingIt(bool paddedStride)
    {
        OcrImage image = Image([], [], paddedStride);
        byte[] pixels = image.Pixels.ToArray();
        for (int x = 6; x <= 20; x++) pixels[(37 * image.Stride) + x] = 0;
        OcrRegion label = YLabel("connected");
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), image with { Pixels = pixels }, [label], CancellationToken.None);
        Assert.Contains("ocr_tick_sequence_needs_review:YTick:axis_connected_text:connected", result.Warnings);
        Assert.AreEqual("20", label.Text);
        Assert.AreEqual(OcrReviewStatus.Unreviewed, label.ReviewStatus);
        TickLabelGeometryResult accepted = ProductionTickLabelGeometry.Resolve(
            Axis(), image with { Pixels = pixels }, [label with { ReviewStatus = OcrReviewStatus.Accepted }], CancellationToken.None);
        Assert.IsFalse(accepted.Warnings.Any(static warning => warning.Contains("axis_connected_text", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DetachedTextAndUniformPixelsDoNotCreateConnectivityEvidence()
    {
        OcrImage image = Image([], []);
        byte[] pixels = image.Pixels.ToArray();
        for (int y = 34; y < 40; y++) for (int x = 6; x < 10; x++) pixels[(y * image.Stride) + x] = 0;
        OcrRectangle box = YLabel("detached").Polygon.Bounds;
        Assert.IsFalse(ProductionAxisConnectedTextReview.HasConnectedInk(
            image with { Pixels = pixels }, box, Axis().YAxis.Line, CancellationToken.None));
        Assert.IsFalse(ProductionAxisConnectedTextReview.HasConnectedInk(
            image with { Pixels = new byte[image.Stride * image.Height] }, box, Axis().YAxis.Line, CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionAxisConnectedTextReview.HasConnectedInk(
            image, box, Axis().YAxis.Line, new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public void DistantNumericYReadingRequiresReviewUnlessConfirmed()
    {
        OcrRegion[] labels =
        [
            YLabel("near-a") with { Polygon = OcrPolygon.FromRectangle(new(9, 15, 5, 5)), Text = "10" },
            YLabel("near-b") with { Polygon = OcrPolygon.FromRectangle(new(9, 30, 5, 5)), Text = "20" },
            YLabel("near-c") with { Polygon = OcrPolygon.FromRectangle(new(9, 45, 5, 5)), Text = "30" },
            YLabel("far") with { Polygon = OcrPolygon.FromRectangle(new(0, 55, 3, 5)), Text = "40" },
        ];
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(Axis(), Image([], []), labels, CancellationToken.None);
        Assert.Contains("ocr_tick_sequence_needs_review:YTick:outlying_text_lane:far", result.Warnings);
        Assert.IsFalse(result.Warnings.Any(static warning => warning.Contains("outlying_text_lane:near", StringComparison.Ordinal)));
        Assert.AreEqual("40", labels[3].Text);
        labels[3] = labels[3] with { ReviewStatus = OcrReviewStatus.Accepted };
        TickLabelGeometryResult accepted = ProductionTickLabelGeometry.Resolve(Axis(), Image([], []), labels, CancellationToken.None);
        Assert.IsFalse(accepted.Warnings.Any(static warning => warning.Contains("outlying_text_lane", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DistantReadingWithoutThreeNumericYRegionsHasNoLaneComparison()
    {
        OcrRegion near = YLabel("near") with { Polygon = OcrPolygon.FromRectangle(new(9, 15, 5, 5)) };
        OcrRegion far = YLabel("far") with { Polygon = OcrPolygon.FromRectangle(new(0, 55, 3, 5)) };
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(Axis(), Image([], []), [near, far], CancellationToken.None);
        Assert.IsFalse(result.Warnings.Any(static warning => warning.Contains("outlying_text_lane", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RejectedAndUnweightedReadingsCannotCreateALaneReference()
    {
        OcrRegion near = YLabel("near") with { Polygon = OcrPolygon.FromRectangle(new(9, 15, 5, 5)) };
        OcrRegion far = YLabel("far") with { Polygon = OcrPolygon.FromRectangle(new(0, 55, 3, 5)) };
        OcrRegion rejected = YLabel("rejected") with { ReviewStatus = OcrReviewStatus.Rejected };
        OcrRegion unweighted = YLabel("unweighted") with { Confidence = 0 };
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), Image([], []), [near, far, rejected, unweighted], CancellationToken.None);
        Assert.IsFalse(result.Warnings.Any(static warning => warning.Contains("outlying_text_lane", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void YTickLaneUsesTheSlantedAxisAtEachReading()
    {
        AxisGeometryResult axis = Axis() with
        {
            YAxis = new(new(new(10, 10), new(30, 70)), 0.95, 0, 1, ["slanted"]),
        };
        OcrRegion[] labels =
        [
            YLabel("near-a") with { Polygon = OcrPolygon.FromRectangle(new(1.5, 15, 5, 5)) },
            YLabel("near-b") with { Polygon = OcrPolygon.FromRectangle(new(6.5, 30, 5, 5)) },
            YLabel("near-c") with { Polygon = OcrPolygon.FromRectangle(new(11.5, 45, 5, 5)) },
            YLabel("far") with { Polygon = OcrPolygon.FromRectangle(new(5, 55, 3, 5)) },
        ];
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(axis, Image([], []), labels, CancellationToken.None);
        Assert.Contains("ocr_tick_sequence_needs_review:YTick:outlying_text_lane:far", result.Warnings);
        Assert.IsFalse(result.Warnings.Any(static warning => warning.Contains("outlying_text_lane:near", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdjacentInkRequiresReviewForUnreviewedNumericTick(bool paddedStride)
    {
        OcrImage image = Image([], [], paddedStride);
        byte[] pixels = image.Pixels.ToArray();
        for (int y = 33; y <= 40; y++) for (int x = 2; x <= 3; x++) pixels[(y * image.Stride) + x] = 0;
        OcrRegion label = YLabel("nearby-ink");
        TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
            Axis(), image with { Pixels = pixels }, [label], CancellationToken.None);
        Assert.Contains("ocr_tick_sequence_needs_review:YTick:adjacent_ink:nearby-ink", result.Warnings);
        Assert.AreEqual("20", label.Text);
        TickLabelGeometryResult accepted = ProductionTickLabelGeometry.Resolve(
            Axis(), image with { Pixels = pixels }, [label with { ReviewStatus = OcrReviewStatus.Accepted }], CancellationToken.None);
        Assert.IsFalse(accepted.Warnings.Any(static warning => warning.Contains("adjacent_ink", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void IsolatedNoiseAndUniformBlackDoNotProvideAdjacentInkEvidence()
    {
        OcrImage image = Image([], []);
        byte[] pixels = image.Pixels.ToArray();
        pixels[(35 * image.Stride) + 3] = 0;
        foreach (byte[] raster in new[] { pixels, new byte[image.Stride * image.Height] })
        {
            TickLabelGeometryResult result = ProductionTickLabelGeometry.Resolve(
                Axis(), image with { Pixels = raster }, [YLabel("noise")], CancellationToken.None);
            Assert.IsFalse(result.Warnings.Any(static warning => warning.Contains("adjacent_ink", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void AdjacentInkSearchRejectsDistantInkInvalidBoundsAndOversizedStrips()
    {
        OcrImage image = Image([], []);
        byte[] pixels = image.Pixels.ToArray();
        for (int y = 33; y <= 40; y++) pixels[(y * image.Stride) + 25] = 0;
        Assert.IsFalse(ProductionAdjacentTickInkReview.HasAdjacentInk(
            image with { Pixels = pixels }, new OcrRectangle(40, 31, 10, 12), CancellationToken.None));
        Assert.IsFalse(ProductionAdjacentTickInkReview.HasAdjacentInk(
            image, new OcrRectangle(-1, 31, 10, 12), CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionAdjacentTickInkReview.HasAdjacentInk(
            image, new OcrRectangle(40, 31, 10, 12), new CancellationToken(canceled: true)));
        var large = new OcrImage(1200, 1500, 1200, new byte[1200 * 1500], OcrSourceImage.Original, OcrFrameTransform.Identity);
        Assert.IsFalse(ProductionAdjacentTickInkReview.HasAdjacentInk(
            large, new OcrRectangle(1100, 0, 10, 1000), CancellationToken.None));
    }

    [TestMethod]
    [DataRow("nested", true)]
    [DataRow("unmasked-word", false)]
    [DataRow("partial-overlap", false)]
    [DataRow("corrected-label", false)]
    [DataRow("word-has-digit", false)]
    [DataRow("no-word", false)]
    [DataRow("staggered", false)]
    [DataRow("detached", false)]
    [DataRow("missing-axis", false)]
    [DataRow("few-anchors", false)]
    public void AttachedRowPreservesLabelsAndRequiresIndependentEvidenceForANestedFragment(
        string example, bool shouldExclude)
    {
        var image = RowImage(example == "detached", missingAxis: example == "missing-axis");
        var labels = new List<OcrRegion> { RowLabel("left", 30, "2"), RowLabel("middle", 50, "12") };
        if (example != "few-anchors") labels.Add(RowLabel("right", 70, "22", example == "staggered" ? 90 : 78));
        var fragment = XLabel("fragment") with
        {
            Polygon = OcrPolygon.FromRectangle(new(46, 118, 4, 4)),
            ReviewStatus = example == "corrected-label" ? OcrReviewStatus.Corrected : OcrReviewStatus.Unreviewed,
            Alternatives = [new("1", .7, OcrSourceImage.Original), new("7", .3, OcrSourceImage.Original)],
        };
        labels.Add(fragment);
        var word = XLabel("caption") with
        {
            Text = example == "word-has-digit" ? "test 7" : "caption",
            Polygon = OcrPolygon.FromRectangle(new(example == "partial-overlap" ? 48 : 40, 114, 32, 14)),
            Role = OcrTextRole.AxisTitle,
        };
        if (example != "no-word") labels.Add(word);
        OcrMask[] masks = example == "unmasked-word" ? [] : [new(word.RegionId, word.Polygon, .95)];
        var result = ProductionTickLabelGeometry.Resolve(Axis(), image, labels, default, masks);
        Assert.AreEqual(shouldExclude, result.PrintedXRow?.NestedTextIds.Contains("fragment") == true);
        if (example is "staggered" or "detached" or "missing-axis" or "few-anchors") Assert.IsNull(result.PrintedXRow);
        else
        {
            Assert.IsNotNull(result.PrintedXRow);
            Assert.AreEqual(30d, result.Positions["left"]);
            Assert.AreEqual(50d, result.Positions["middle"]);
            Assert.AreEqual(70d, result.Positions["right"]);
            Assert.IsFalse(result.Positions.ContainsKey("fragment"));
        }
        Assert.HasCount(2, fragment.Alternatives);
    }

    [TestMethod]
    public void TwoStrokesWithinOneLabelRemainAmbiguousAndSpineRequiresSeparateInk()
    {
        var labels = new List<OcrRegion> { RowLabel("a",30,"1"), RowLabel("b",50,"11"), RowLabel("c",70,"21") };
        labels.Add(RowLabel("disputed",40,"6") with { Polygon = OcrPolygon.FromRectangle(new(35, 78, 10, 9)) });
        var axis = Axis() with
        {
            Ticks = [new("spine", TickAxis.XAxis, new(20, 66), new(new(20, 62), new(20, 70)), .9, ["line"]),
                new("inward", TickAxis.XAxis, new(35, 67), new(new(35, 64), new(35, 70)), .9, ["other"])],
        };
        var result = ProductionTickLabelGeometry.Resolve(axis, RowImage(), labels, default);
        Assert.IsNotNull(result.PrintedXRow);
        Assert.AreEqual("spine", result.PrintedXRow.SpineSegmentIds.Single());
        Assert.IsFalse(result.Positions.ContainsKey("disputed"));
        CollectionAssert.Contains(result.Warnings.ToArray(), "ocr_tick_sequence_needs_review:XTick:multiple_attached_strokes:disputed");
        var actualCornerTick = ProductionTickLabelGeometry.Resolve(axis, RowImage(corner: true), labels, default);
        Assert.IsNotNull(actualCornerTick.PrintedXRow);
        Assert.IsEmpty(actualCornerTick.PrintedXRow.SpineSegmentIds);
    }

    [TestMethod]
    [DataRow(true, false, true)]
    [DataRow(false, false, false)]
    [DataRow(true, true, false)]
    public void CalibrationRequiresReviewForUnexplainedNumbersAndGenuineEndpointDisagreements(
        bool wordHasMask, bool endpointDisputed, bool valid)
    {
        var endpoint = RowLabel("origin", 30, "1");
        if (endpointDisputed) endpoint = endpoint with { Alternatives = [new("7", .8, OcrSourceImage.Original)] };
        var word = XLabel("word") with { Text = "caption", Role = OcrTextRole.AxisTitle,
            Polygon = OcrPolygon.FromRectangle(new(40, 114, 32, 14)) };
        var fragment = RowLabel("fragment", 48, "7") with { Polygon = OcrPolygon.FromRectangle(new(46, 118, 4, 4)),
            Alternatives = [new("9", .8, OcrSourceImage.Original)] };
        OcrRegion[] regions = [endpoint, RowLabel("middle", 50, "3"), RowLabel("end", 70, "5"), word, fragment,
            YLabel("low") with { Text = "0", Polygon = OcrPolygon.FromRectangle(new(4, 65, 8, 10)) },
            YLabel("high") with { Text = "60", Polygon = OcrPolygon.FromRectangle(new(4, 5, 8, 10)) }];
        OcrMask[] masks = wordHasMask ? [new("word", word.Polygon, .95)] : [];
        var ocr = new OcrResult(OcrContract.Version, "run", "project", "panel", "ocr", "test", "hash",
            OcrContract.CoordinateSpace, regions, masks, new(0, 0, 0, 0), .95, [], new(false, "", 0, 0), null);
        var markers = Enumerable.Range(0, 5).Select(i => new ClassifiedMarker(
            new MarkerCenter($"p{i}", new MarkerPoint(30 + 10 * i, 50), 3, 0, .95, MarkerSourceImage.Original),
            MarkerShape.Circle, MarkerFill.Filled, "circle", "Circle", 0, .95, .95, new float[12])).ToArray();
        var axis = Axis();
        var geometry = ProductionTickLabelGeometry.Resolve(axis, RowImage(), regions, default, masks);
        var calibration = ProductionAutomaticDetectionAdapter.FitCalibration(axis, ocr, geometry, markers, default);
        Assert.AreEqual(valid, calibration.Validity == CalibrationValidity.Valid);
        if (valid)
        {
            Assert.AreEqual(2d, calibration.XTransform!.Transform!.PixelToGraph(40), 1e-9);
            Assert.AreEqual(20d, calibration.YTransform.Transform!.PixelToGraph(50), 1e-9);
            Assert.AreEqual(3, calibration.XTransform.Diagnostics.InputCount);
        }
        else
        {
            string disputed = endpointDisputed ? "origin" : "fragment";
            CollectionAssert.Contains(calibration.Reasons.ToArray(), $"ocr_tick_sequence_needs_review:XTick:competing_numeric_readings:{disputed}");
        }
    }

    [TestMethod]
    [DataRow("measured", true)]
    [DataRow("detached", false)]
    [DataRow("disputed-y", false)]
    [DataRow("skew", false)]
    [DataRow("uncertain-geometry", false)]
    public void MeasuredCalibrationDraftRequiresAttachedOriginalTicksAndVerifiedVerticalScale(string example, bool offered)
    {
        var axis = Axis();
        var image = RowImage();
        byte[] pixels = image.Pixels.ToArray();
        var labels = new List<OcrRegion> { RowLabel("left", 30, "1") with
            { Alternatives = [new("10", .8, OcrSourceImage.Original)] }, RowLabel("mid", 50, "3"), RowLabel("right", 70, "5") };
        for (int y = 20; y <= 60; y += 10)
        {
            if (example != "detached")
                for (int x = 15; x <= 20; x++) pixels[y * image.Stride + x] = 0;
            labels.Add(YLabel($"y{y}") with
            {
                Text = (60 - y).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Polygon = OcrPolygon.FromRectangle(new(3, y - 3, 9, 6)),
                Alternatives = example == "disputed-y" && y == 60 ? [new("70", .9, OcrSourceImage.Original)] : [],
            });
        }
        var geometry = ProductionTickLabelGeometry.Resolve(axis, image with { Pixels = pixels }, labels, default);
        if (example == "skew") axis = axis with { PlotPolygon = axis.PlotPolygon with { TopLeft = new(29, 10) } };
        if (example == "uncertain-geometry") axis = axis with { Uncertainty = axis.Uncertainty with { NeedsReview = true } };
        var ocr = new OcrResult(OcrContract.Version, "run", "project", "panel", "ocr", "test", "hash",
            OcrContract.CoordinateSpace, labels, [], new(0, 0, 0, 0), .95, [], new(false, "", 0, 0), null);
        var fit = ProductionAutomaticDetectionAdapter.FitCalibration(axis, ocr, geometry, [], default);
        var envelope = new WorkflowVisionEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "markers", "test", new string('c', 64), null, new(0, 0, 0, 0), .95);
        var draft = MeasuredCalibrationDraft.From(envelope, axis, ocr, geometry, fit);
        Assert.AreEqual(offered, draft is not null,
            $"Y fit {fit.YTransform.Validity}, strokes {string.Join(",", geometry.AttachedStrokeIds)}, reasons {string.Join(" | ", fit.Reasons)}");
        if (draft is not null)
        {
            Assert.AreEqual(30, draft.LeftX);
            Assert.AreEqual(70, draft.RightX);
            Assert.AreEqual(60, draft.ZeroY);
            Assert.AreEqual(20, draft.UpperY);
            Assert.AreEqual(40, draft.UpperValue);
            Assert.AreEqual("1 / 10", draft.LeftReadings);
            Assert.AreNotEqual(CalibrationValidity.Valid, fit.Validity);
        }
    }

    [TestMethod]
    [DataRow("whole")]
    [DataRow("partial")]
    [DataRow("disputed")]
    [DataRow("mixed")]
    public void IntervalLabelsCannotSnapToTheirBoundaryOrSuppressDisagreement(string example)
    {
        var image = Image([24, 40, 56, 72], []);
        byte[] pixels = image.Pixels.ToArray();
        for (int y = 80; y < 86; y++)
            foreach (int x in new[] { 30, 31, 46, 47 }) pixels[y * image.Stride + x] = 0;
        if (example == "partial") pixels[82 * image.Stride + 53] = 0;
        image = image with { Pixels = pixels };
        var left = XLabel("one") with { Polygon = OcrPolygon.FromRectangle(new(29, 80, 6, 6)) };
        var next = XLabel("two") with { Text = "2", Polygon = OcrPolygon.FromRectangle(new(45, 79, 7, 7)),
            Alternatives = example == "disputed" ? [new("20", .8, OcrSourceImage.Original)] : [] };
        var labels = new List<OcrRegion> { left, next, YLabel("upper"), YLabel("zero") with
            { Text = "0", Polygon = OcrPolygon.FromRectangle(new(4, 54, 10, 12)) } };
        if (example == "mixed") labels.Add(RowLabel("on-stroke", 72, "4"));
        var axis = Axis();
        var geometry = ProductionTickLabelGeometry.Resolve(axis, image, labels, default);
        Assert.IsFalse(geometry.Positions.ContainsKey("two"), "A boundary must not become the point location of the interval's printed label.");
        Assert.IsFalse(geometry.AttachedStrokeIds.Contains("one"));
        var ocr = new OcrResult(OcrContract.Version, "run", "project", "panel", "ocr", "test", "hash",
            OcrContract.CoordinateSpace, labels, [], new(0, 0, 0, 0), .95, [], new(false, "", 0, 0), null);
        // Two series share the first and last session. One skips the middle session.
        var markers = new[] { (32, 45, true), (48, 43, true), (64, 42, true), (32, 51, false), (64, 49, false) }
            .Select((point, i) => new ClassifiedMarker(
                new MarkerCenter($"p{i}", new MarkerPoint(point.Item1, point.Item2), 3, 0, .95, MarkerSourceImage.Original),
                point.Item3 ? MarkerShape.Circle : MarkerShape.Square, point.Item3 ? MarkerFill.Filled : MarkerFill.Open,
                "symbol", "Symbol", 0, .95, .95, new float[12])).ToArray();
        var fit = ProductionAutomaticDetectionAdapter.FitCalibration(axis, ocr, geometry, markers, default);
        Assert.AreEqual(3, fit.Lattice.Diagnostics.UniqueColumnCount);
        Assert.AreEqual(example == "whole", fit.Validity == CalibrationValidity.Valid, string.Join(" | ", fit.Reasons));
        if (example == "whole")
            CollectionAssert.AreEqual(new double?[] { 1, 2, 3 }, fit.Lattice.Assignments.Select(a => a.PrintedX ?? a.EstimatedX).ToArray());
    }


    [TestMethod]
    public void IntervalCropRequiresVisibleBoundariesOnTheFittedAxis()
    {
        var cropped = RowLabel(TickLaneTextRegionRecovery.XIntervalPrefix + "crop", 48, "4");
        var geometry = ProductionTickLabelGeometry.Resolve(Axis(), Image([], []), [cropped], default);
        CollectionAssert.Contains(geometry.Warnings.ToArray(),
            $"ocr_tick_sequence_needs_review:XTick:interval_crop_unassociated:{cropped.RegionId}");
    }

    private static OcrRegion RowLabel(string id, int x, string text, int y = 78) => XLabel(id) with
    {
        Text = text,
        Polygon = OcrPolygon.FromRectangle(new(x - 4, y, 9, 9)),
    };

    private static OcrImage RowImage(bool detached = false, bool corner = false, bool missingAxis = false)
    {
        var image = Image([], [], paddedStride: true);
        byte[] pixels = Enumerable.Repeat((byte)255, 140 * image.Stride).ToArray();
        if (!missingAxis) image.Pixels.CopyTo(pixels);
        foreach (int x in new[] { 24, 30, 36, 44, 50, 56, 64, 70, 76 })
            for (int y = detached ? 72 : 70; y <= 75; y++) pixels[y * image.Stride + x] = 0;
        if (corner)
            for (int y = 70; y <= 75; y++) pixels[y * image.Stride + 20] = 0;
        return image with { Height = 140, Pixels = pixels };
    }

    private static OcrRegion XLabel(string id) => new(id,
        OcrPolygon.FromRectangle(new(42, 78, 4, 12)), "1", [], OcrTextRole.XTick,
        0.95, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static OcrRegion YLabel(string id) => new(id,
        OcrPolygon.FromRectangle(new(4, 31, 10, 12)), "20", [], OcrTextRole.YTick,
        0.95, OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static AxisGeometryResult Axis() => new(AxisGeometryCoordinateSpaces.OriginalPixels,
        new(new(20, 70), new(80, 70), new(80, 10), new(20, 10)),
        new(new(new(20, 70), new(80, 70)), 0.95, 0, 1, ["x"]),
        new(new(new(20, 10), new(20, 70)), 0.95, 0, 1, ["y"]), [], [], [], 0.95,
        new(0, 0, 1, false, []), new(2, 2, 0, 1, 1, 0, 0, 0, TimeSpan.Zero, []));

    private static OcrImage Image(int[] xTicks, int[] yTicks, bool paddedStride = false)
    {
        int stride = paddedStride ? 104 : 100;
        byte[] pixels = Enumerable.Repeat((byte)255, stride * 100).ToArray();
        for (int x = 20; x <= 80; x++) pixels[70 * stride + x] = 0;
        for (int y = 10; y <= 70; y++) pixels[y * stride + 20] = 0;
        foreach (int x in xTicks) for (int y = 70; y <= 75; y++) pixels[y * stride + x] = 0;
        foreach (int y in yTicks) for (int x = 14; x <= 20; x++) pixels[y * stride + x] = 0;
        return new(100, 100, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }
}
