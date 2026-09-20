// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Integration.Workflow;
using GraphReader.Axis;
using GraphReader.Ocr;
using GraphReader.Phases;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionPhaseGeometryContextTests
{
    private static readonly double[] ExpectedBoundaries = [60, 110, 160];
    private static readonly string[] ExpectedCodes = ["a1", "b1", "a2", "b2"];
    private static readonly OcrRectangle LegendFrame = new(180, 70, 45, 28);

    [TestMethod]
    public async Task HeadingRowResolvesMeasuredBoundariesWithoutChangingTextOrGuessingLabels()
    {
        AxisGeometryResult axis = Axis();
        OcrRegion[] text = Headings();
        PhaseGeometryContextResult resolved = ProductionPhaseGeometryContext.Resolve(axis, Image(), text, [], CancellationToken.None);
        CollectionAssert.AreEqual(ExpectedBoundaries, resolved.Dividers.Select(static item => item.Line.Midpoint.X).ToArray());
        Assert.HasCount(3, resolved.Warnings);
        Assert.IsEmpty(axis.PhaseDividers, "Original geometry must remain unchanged.");
        Assert.HasCount(3, axis.AmbiguousGridOrDividers);
        Assert.AreEqual(OcrTextRole.Other, text[0].Role);
        Assert.AreEqual("Unresolved first heading", text[0].Text);
        for (int i = 0; i < resolved.Dividers.Count; i++)
        {
            Assert.AreEqual(axis.AmbiguousGridOrDividers[i].Line, resolved.Dividers[i].Line);
            CollectionAssert.AreEqual(axis.AmbiguousGridOrDividers[i].SupportingCandidateIds.ToArray(),
                resolved.Dividers[i].SupportingCandidateIds.ToArray());
        }
        const string panel = "21111111-1111-1111-1111-111111111111";
        var request = new PhaseReasoningRequest("11111111-1111-1111-1111-111111111111", panel,
            new string('a', 64), new PhaseRectangle(10, 40, 200, 100),
            resolved.Dividers.Select((item, index) => new PhaseDividerSegment(
                $"31111111-1111-1111-1111-{index + 1:000000000000}", panel,
                new PhasePoint(item.Line.Start.X, item.Line.Start.Y), new PhasePoint(item.Line.End.X, item.Line.End.Y),
                1, PhaseDividerStyle.Solid, item.Confidence)),
            text.Where(static item => item.Role == OcrTextRole.PhaseHeading).Select((item, index) =>
                new PhaseHeadingEvidence($"41111111-1111-1111-1111-{index + 1:000000000000}", panel,
                    new PhaseRectangle(item.Polygon.Bounds.X, item.Polygon.Bounds.Y, item.Polygon.Bounds.Width, item.Polygon.Bounds.Height),
                    item.Text, item.Confidence)), [], []);
        PhaseReasoningResult phases = await new PhaseReasoningService().ResolveAsync(request, CancellationToken.None);
        Assert.IsTrue(phases.Succeeded, phases.Failure?.TechnicalMessage);
        Assert.HasCount(4, phases.Payload.Phases);
        CollectionAssert.AreEqual(ExpectedCodes, phases.Payload.Phases.Select(static item => item.Code).ToArray());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void GridLinesRemainAmbiguousWithoutACorroboratingHeadingRow(int missingEvidence)
    {
        OcrRegion[] text = Headings();
        if (missingEvidence == 0) text = text.Select(static item => item with { Role = OcrTextRole.Other }).ToArray();
        if (missingEvidence == 1) text = text[1..];
        if (missingEvidence == 2) text[3] = text[3] with { Polygon = OcrPolygon.FromRectangle(new(165, 4, 30, 10)) };
        if (missingEvidence == 3) text[3] = text[3] with { ReviewStatus = OcrReviewStatus.Rejected };
        PhaseGeometryContextResult result = ProductionPhaseGeometryContext.Resolve(Axis(), Image(), text, [], CancellationToken.None);
        Assert.IsEmpty(result.Dividers);
        Assert.IsTrue(result.Warnings.All(static warning => warning.StartsWith("phase_grid_ambiguity_requires_review:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void HeadingsCannotCreateLinesThatWereNeverMeasured()
    {
        PhaseGeometryContextResult result = ProductionPhaseGeometryContext.Resolve(
            Axis() with { AmbiguousGridOrDividers = [] }, Image(), Headings(), [], CancellationToken.None);
        Assert.IsEmpty(result.Dividers);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AClosedLegendFrameAndTextCannotBecomeAFullHeightDivider(bool ambiguous)
    {
        AxisGeometryResult axis = FrameAxis(ambiguous);
        OcrRegion[] text = [Region("top-text", 175, 47, "Top note", OcrTextRole.Annotation),
            Region("bottom-text", 175, 116, "Bottom note", OcrTextRole.Annotation)];
        PhaseGeometryContextResult result = ProductionPhaseGeometryContext.Resolve(
            axis, Image(frame: true, textFragments: true), text, [LegendFrame], CancellationToken.None);
        Assert.IsEmpty(result.Dividers);
        Assert.IsTrue(result.Warnings.Single().StartsWith("phase_line_excluded_by_legend_context:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ARealDividerPassingBehindALegendRetainsItsOutsidePixelSupport()
    {
        PhaseGeometryContextResult result = ProductionPhaseGeometryContext.Resolve(
            FrameAxis(false), Image(frame: true, realDivider: true), [], [LegendFrame], CancellationToken.None);
        Assert.HasCount(1, result.Dividers);
        Assert.AreEqual(180, result.Dividers[0].Line.Midpoint.X);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void NonOriginalEvidenceAndCancellationFailClosed()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProductionPhaseGeometryContext.Resolve(Axis(),
            Image() with { SourceImage = OcrSourceImage.Enhanced }, Headings(), [], CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => ProductionPhaseGeometryContext.Resolve(Axis(), Image(),
            [Headings()[0] with { CoordinateSpace = "enhanced_pixels" }], [], CancellationToken.None));
        Assert.ThrowsExactly<OperationCanceledException>(() => ProductionPhaseGeometryContext.Resolve(
            Axis(), Image(), Headings(), [], new CancellationToken(canceled: true)));
    }

    private static AxisGeometryResult FrameAxis(bool ambiguous) => Axis() with
    {
        PhaseDividers = ambiguous ? [] : [new("legend-side", Line(180), DividerStyle.Dashed, 0.65, 0.48, 0.40, ["frame-line"])],
        AmbiguousGridOrDividers = ambiguous ? [new("legend-side", Line(180), 0.8, 0.48, 0.9, ["frame-line"])] : [],
    };

    private static OcrRegion[] Headings() => [
        Region("first", 15, 24, "Unresolved first heading", OcrTextRole.Other),
        Region("second", 65, 24, "Unresolved second heading", OcrTextRole.Other),
        Region("third", 115, 24, "A", OcrTextRole.PhaseHeading),
        Region("fourth", 165, 24, "B", OcrTextRole.PhaseHeading)];

    private static OcrRegion Region(string id, double x, double y, string text, OcrTextRole role) => new(
        id, OcrPolygon.FromRectangle(new(x, y, 30, 10)), text, [], role, 0.95,
        OcrSourceImage.Original, OcrReviewStatus.Unreviewed);

    private static GeometryLineSegment Line(double x) => new(new(x, 40), new(x, 140));

    private static AxisGeometryResult Axis() => new(AxisGeometryCoordinateSpaces.OriginalPixels,
        new PlotPolygon(new(10, 140), new(210, 140), new(210, 40), new(10, 40)),
        new AxisLineFit(new(new(10, 140), new(210, 140)), 0.95, 0, 1, ["x"]),
        new AxisLineFit(Line(10), 0.95, 0, 1, ["y"]), [], [],
        ExpectedBoundaries.Select((x, index) => new AmbiguousGridOrDividerGeometry(
            $"measured-{index}", Line(x), 0.95, 1, 1, [$"segment-{index}"])).ToArray(),
        0.95, new(0, 0, 1, true, ["grid_or_phase_divider_ambiguous"]),
        new(5, 5, 0, 1, 4, 0, 0, 3, TimeSpan.Zero, []));

    private static OcrImage Image(bool frame = false, bool realDivider = false, bool textFragments = false)
    {
        const int width = 240, height = 160;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        if (frame)
        {
            for (int y = 70; y <= 97; y++) { pixels[y * width + 180] = 0; pixels[y * width + 224] = 0; }
            for (int x = 180; x <= 224; x++) { pixels[70 * width + x] = 0; pixels[97 * width + x] = 0; }
        }
        if (realDivider) for (int y = 40; y <= 140; y++) pixels[y * width + 180] = 0;
        if (textFragments)
        {
            for (int y = 47; y <= 56; y++) pixels[y * width + 180] = 0;
            for (int y = 116; y <= 125; y++) pixels[y * width + 180] = 0;
        }
        return new(width, height, width, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
    }
}
