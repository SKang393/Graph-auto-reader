// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class GraphTextRoleClassifierTests
{
    private static readonly OcrRectangle Plot = new(30, 15, 110, 70);

    [TestMethod]
    public void RotatedYLabelIsAxisTitleRatherThanTickOrParticipant()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "y-title",
            3,
            30,
            12,
            42,
            orientationDegrees: -90,
            context: new OcrRegionContext(AxisTitleExpected: true));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Percentage Correct", Plot);

        Assert.AreEqual(OcrTextRole.AxisTitle, result.Role);
        Assert.IsGreaterThan(0.5d, result.Confidence);
    }

    [TestMethod]
    public void ParticipantBandTextIsParticipantMetadataEvidence()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "participant",
            62,
            2,
            48,
            10,
            context: new OcrRegionContext(InParticipantBand: true));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Chandler", Plot);

        Assert.AreEqual(OcrTextRole.Participant, result.Role);
        Assert.AreNotEqual(OcrTextRole.LegendText, result.Role);
    }

    [TestMethod]
    [DataRow(2d)]
    [DataRow(142d)]
    public void GenericParticipantLabelAtHorizontalPlotPeripheryIsParticipant(double x)
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("participant-label", x, 42, 24, 9);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Participant 01", Plot);

        Assert.AreEqual(OcrTextRole.Participant, result.Role);
        CollectionAssert.Contains(result.Reasons.ToArray(), "participant_label_and_peripheral_geometry");
    }

    [TestMethod]
    [DataRow("Morgan")]
    [DataRow("Outcome")]
    [DataRow("Training")]
    [DataRow("Partid�pant 01")]
    [DataRow("Participant")]
    public void AmbiguousPeripheralTextRequiresReview(string text)
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("ambiguous-peripheral", 2, 42, 24, 9);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, text, Plot);

        Assert.AreEqual(OcrTextRole.Other, result.Role);
        CollectionAssert.Contains(result.Reasons.ToArray(), "ambiguous_peripheral_text_requires_review");
    }

    [TestMethod]
    public void ParticipantCueInsidePlotRemainsAnnotation()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("inside", 62, 42, 24, 9);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Participant 01", Plot);

        Assert.AreEqual(OcrTextRole.Annotation, result.Role);
    }

    [TestMethod]
    public void RightPeripheralTextWithoutParticipantCueKeepsLegendFallback()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("right-label", 142, 42, 24, 9);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Treatment", Plot);

        Assert.AreEqual(OcrTextRole.LegendText, result.Role);
    }

    [TestMethod]
    public void ParticipantCueBelowPlotCornerIsNotParticipant()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("below-corner", 2, 86, 24, 9);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Participant 01", Plot);

        Assert.AreEqual(OcrTextRole.Other, result.Role);
    }

    [TestMethod]
    public void LegendContextWinsOverPeripheralParticipantCue()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "legend-participant-cue",
            142,
            42,
            24,
            9,
            context: new OcrRegionContext(NearLegendGlyph: true));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Participant 01", Plot);

        Assert.AreEqual(OcrTextRole.LegendText, result.Role);
    }

    [TestMethod]
    public void NumericContextWinsOverPeripheralParticipantCue()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "numeric-participant-cue",
            2,
            42,
            24,
            9,
            context: new OcrRegionContext(NumericExpected: true));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Participant 01", Plot);

        Assert.AreEqual(OcrTextRole.YTick, result.Role);
    }

    [TestMethod]
    public void VerticalPeripheralParticipantCueRemainsAxisTitle()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "vertical-participant-cue",
            2,
            30,
            9,
            42,
            orientationDegrees: -90);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Participant 01", Plot);

        Assert.AreEqual(OcrTextRole.AxisTitle, result.Role);
    }

    [TestMethod]
    public void LegendProximityClassifiesLegendTextWithoutUsingColor()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "legend",
            92,
            28,
            35,
            9,
            context: new OcrRegionContext(NearLegendGlyph: true));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Treatment", Plot);

        Assert.AreEqual(OcrTextRole.LegendText, result.Role);
    }

    [TestMethod]
    public void GeneralizationNearCalloutIsAnnotationTextNotMarkerSemantics()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "generalization",
            80,
            42,
            48,
            9,
            context: new OcrRegionContext(NearAnnotationArrow: true));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Generalization", Plot);

        Assert.AreEqual(OcrTextRole.Annotation, result.Role);
        Assert.AreNotEqual(OcrTextRole.LegendText, result.Role);
    }

    [TestMethod]
    public void GeneralizationNearPhaseDividerIsPhaseHeadingEvidence()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "generalization-phase",
            76,
            3,
            52,
            9,
            context: new OcrRegionContext(NearPhaseDivider: true));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Generalization", Plot);

        Assert.AreEqual(OcrTextRole.PhaseHeading, result.Role);
    }

    [TestMethod]
    public void PhaseBAbovePlotIsNotMisclassifiedAsNumericTickEight()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("phase-b", 72, 3, 8, 9);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "B", Plot);

        Assert.AreEqual(OcrTextRole.PhaseHeading, result.Role);
        Assert.IsFalse(GraphNumericParser.Parse("B").IsSuccess);
    }

    [TestMethod]
    public void FollowupAbovePlotMatchesFrozenCompositionRoleVocabulary()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region("followup", 72, 3, 44, 9);

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Followup", Plot);

        Assert.AreEqual(OcrTextRole.PhaseHeading, result.Role);
    }

    [TestMethod]
    public void PhaseHeadingRequiresDividerContext()
    {
        OcrDetectedRegion withDivider = OcrTestFixtures.Region(
            "phase",
            74,
            3,
            38,
            9,
            context: new OcrRegionContext(NearPhaseDivider: true));
        OcrDetectedRegion withoutDivider = OcrTestFixtures.Region(
            "plain",
            74,
            30,
            38,
            9);

        RoleClassification heading = GraphTextRoleClassifier.Classify(withDivider, "Intervention", Plot);
        RoleClassification plain = GraphTextRoleClassifier.Classify(withoutDivider, "Intervention", Plot);

        Assert.AreEqual(OcrTextRole.PhaseHeading, heading.Role);
        Assert.AreNotEqual(OcrTextRole.PhaseHeading, plain.Role);
    }

    [TestMethod]
    public void NumericLocationSeparatesXAndYTicks()
    {
        OcrDetectedRegion xRegion = OcrTestFixtures.Region(
            "x-tick",
            60,
            89,
            10,
            7,
            context: new OcrRegionContext(NumericExpected: true));
        OcrDetectedRegion yRegion = OcrTestFixtures.Region(
            "y-tick",
            15,
            42,
            11,
            7,
            context: new OcrRegionContext(NumericExpected: true));

        Assert.AreEqual(OcrTextRole.XTick, GraphTextRoleClassifier.Classify(xRegion, "10", Plot).Role);
        Assert.AreEqual(OcrTextRole.YTick, GraphTextRoleClassifier.Classify(yRegion, "50", Plot).Role);
    }

    [TestMethod]
    public void AmbiguousLetterInsidePlotStaysAnnotationUnlessNumericGeometryIsExplicit()
    {
        OcrDetectedRegion annotation = OcrTestFixtures.Region("letter", 72, 42, 8, 9);
        OcrDetectedRegion expectedTick = OcrTestFixtures.Region(
            "letter-tick",
            60,
            89,
            8,
            7,
            context: new OcrRegionContext(NumericExpected: true));

        Assert.AreEqual(
            OcrTextRole.Annotation,
            GraphTextRoleClassifier.Classify(annotation, "O", Plot).Role);
        Assert.AreEqual(
            OcrTextRole.XTick,
            GraphTextRoleClassifier.Classify(expectedTick, "O", Plot).Role);
    }

    [TestMethod]
    public void ExplicitRoleHintWinsWhenGeometricSignalsConflict()
    {
        OcrDetectedRegion region = OcrTestFixtures.Region(
            "confirmed-participant",
            95,
            30,
            30,
            8,
            context: new OcrRegionContext(
                NearLegendGlyph: true,
                InParticipantBand: true,
                ExplicitRoleHint: OcrTextRole.Participant));

        RoleClassification result = GraphTextRoleClassifier.Classify(region, "Morgan", Plot);

        Assert.AreEqual(OcrTextRole.Participant, result.Role);
        Assert.IsTrue(result.Reasons.Any(reason =>
            reason.Contains("explicit", StringComparison.OrdinalIgnoreCase)));
    }
}
