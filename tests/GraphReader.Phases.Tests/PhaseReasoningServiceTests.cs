// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Phases.Tests;

[TestClass]
public sealed class PhaseReasoningServiceTests
{
    [TestMethod]
    public async Task MissingHeadingsDefaultsFirstTwoRegionsToAb()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(segments: [PhaseTestFixture.Segment("divider", 260)]));

        Assert.IsTrue(result.Succeeded);
        string[] expectedCodes = ["a", "b"];
        CollectionAssert.AreEqual(expectedCodes, result.Payload.Phases.Select(item => item.Code).ToArray());
        CollectionAssert.AreEqual(
            new[] { PhaseNormalizedType.Baseline, PhaseNormalizedType.Intervention },
            result.Payload.Phases.Select(item => item.NormalizedType).ToArray());
        Assert.IsTrue(result.Payload.Phases.All(item => item.Source == PhaseEvidenceSource.ProfilePrior));
        CollectionAssert.Contains(result.Warnings.ToArray(), "phase_heading_not_detected");
    }

    [TestMethod]
    public async Task ConflictingHeadingsCannotOverrideFirstTwoAbProfilePhases()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("divider", 260)],
                headings:
                [
                    PhaseTestFixture.Heading("heading-m", "Maintenance", 130),
                    PhaseTestFixture.Heading("heading-a", "Baseline", 390),
                ]));

        Assert.IsTrue(result.Succeeded);
        string[] expectedCodes = ["a", "b"];
        CollectionAssert.AreEqual(
            expectedCodes,
            result.Payload.Phases.Select(item => item.Code).ToArray());
        CollectionAssert.AreEqual(
            new[] { PhaseNormalizedType.Baseline, PhaseNormalizedType.Intervention },
            result.Payload.Phases.Select(item => item.NormalizedType).ToArray());
        CollectionAssert.Contains(result.Warnings.ToArray(), "phase_heading_conflicts_with_ab_profile");
    }

    [TestMethod]
    public async Task RelabelOnlyPersistsWhenAggregateCrossesCoordinateRoundingBoundary()
    {
        PhaseDividerSegment initialSegment = PhaseTestFixture.Segment("divider-primary", 240.49);
        PhaseReasoningResult initial = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(segments: [initialSegment]));
        PhaseRegion intervention = initial.Payload.Phases[1];
        PhaseEditResult relabeled = new PhaseManualEditor().Apply(
            initial.Payload.ManualOverrides,
            new RelabelPhaseCommand(
                CommandId(72),
                intervention.PhaseId,
                "confirmed-b",
                PhaseNormalizedType.Intervention,
                "Confirmed intervention",
                intervention.OriginalXMinimum,
                intervention.OriginalXMaximum),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);

        PhaseReasoningResult rerun = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments:
                [
                    initialSegment,
                    PhaseTestFixture.Segment("divider-support", 240.8),
                ],
                overrides: relabeled.Overrides));

        Assert.IsTrue(rerun.Succeeded);
        Assert.AreNotEqual(initial.Payload.Dividers[0].DividerId, rerun.Payload.Dividers[0].DividerId);
        Assert.AreEqual("confirmed-b", rerun.Payload.Phases[1].Code);
        Assert.AreEqual(PhaseEvidenceSource.Manual, rerun.Payload.Phases[1].Source);
        CollectionAssert.Contains(rerun.Warnings.ToArray(), "manual_phase_label_matched_by_geometry");
        CollectionAssert.DoesNotContain(rerun.Warnings.ToArray(), "manual_phase_label_target_not_found");
    }

    [TestMethod]
    public async Task UnknownLaterPhaseUsesPhase3WithoutHallucinatingMaintenanceOrGeneralization()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("divider-1", 180),
                    PhaseTestFixture.Segment("divider-2", 340),
                ]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("phase3", result.Payload.Phases[2].Code);
        Assert.AreEqual(PhaseNormalizedType.Unknown, result.Payload.Phases[2].NormalizedType);
        CollectionAssert.Contains(result.Warnings.ToArray(), "later_phase_semantic_unknown");
    }

    [TestMethod]
    public async Task AbabHeadingsNormalizeRepeatedPhaseNumbering()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("divider-1", 140),
                    PhaseTestFixture.Segment("divider-2", 260),
                    PhaseTestFixture.Segment("divider-3", 380),
                ],
                headings:
                [
                    PhaseTestFixture.Heading("heading-a1", "A", 80),
                    PhaseTestFixture.Heading("heading-b1", "B", 200),
                    PhaseTestFixture.Heading("heading-a2", "Baseline", 320),
                    PhaseTestFixture.Heading("heading-b2", "Treatment", 440),
                ]));

        Assert.IsTrue(result.Succeeded);
        string[] expectedCodes = ["a1", "b1", "a2", "b2"];
        CollectionAssert.AreEqual(expectedCodes, result.Payload.Phases.Select(item => item.Code).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                PhaseNormalizedType.Baseline,
                PhaseNormalizedType.Intervention,
                PhaseNormalizedType.Baseline,
                PhaseNormalizedType.Intervention,
            },
            result.Payload.Phases.Select(item => item.NormalizedType).ToArray());
    }

    [TestMethod]
    [DataRow("Withdrawal", "Reintroduction")]
    [DataRow("Withdrawal continued", "Reintroduction continued")]
    [DataRow("Withdrawalcontinued", "Reintroductioncontinued")]
    [DataRow("Withdrawal   continued", "Reintroduction\tcontinued")]
    public async Task ExplicitWithdrawalAndReintroductionHeadingsPreserveAbabMeaning(string withdrawal, string reintroduction)
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("divider-1", 140),
                    PhaseTestFixture.Segment("divider-2", 260),
                    PhaseTestFixture.Segment("divider-3", 380),
                ],
                headings:
                [
                    PhaseTestFixture.Heading("heading-a1", "Baseline", 80),
                    PhaseTestFixture.Heading("heading-b1", "Intervention", 200),
                    PhaseTestFixture.Heading("heading-a2", withdrawal, 320),
                    PhaseTestFixture.Heading("heading-b2", reintroduction, 440),
                ]));

        Assert.IsTrue(result.Succeeded);
        string[] expectedCodes = ["a1", "b1", "a2", "b2"];
        CollectionAssert.AreEqual(expectedCodes, result.Payload.Phases.Select(static p => p.Code).ToArray());
        Assert.IsTrue(result.Payload.Phases.All(static p => p.Source == PhaseEvidenceSource.Ocr));
        Assert.AreEqual(withdrawal, result.Payload.Phases[2].LabelText);
        Assert.AreEqual(reintroduction, result.Payload.Phases[3].LabelText);
    }

    [TestMethod]
    [DataRow("Withdrwal")]
    [DataRow("Withdrawal symptoms")]
    [DataRow("Reintroduction was recorded")]
    [DataRow("Wirhdrreal continped")]
    [DataRow("Withdrawalcontinued symptoms")]
    [DataRow("Reintroductioncontinues")]
    public async Task UnclearOrIncidentalWithdrawalTextDoesNotSupplyALaterPhaseMeaning(string text)
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(PhaseTestFixture.Request(
            segments: [PhaseTestFixture.Segment("first", 180), PhaseTestFixture.Segment("second", 340)],
            headings: [PhaseTestFixture.Heading("unclear", text, 420)]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("phase3", result.Payload.Phases[2].Code);
        Assert.AreEqual(PhaseNormalizedType.Unknown, result.Payload.Phases[2].NormalizedType);
    }

    [TestMethod]
    public async Task MaintenanceHeadingSupportsMaintenancePhaseOnlyWithEvidence()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("divider-1", 180),
                    PhaseTestFixture.Segment("divider-2", 340),
                ],
                headings:
                [
                    PhaseTestFixture.Heading("heading-a", "Baseline", 100),
                    PhaseTestFixture.Heading("heading-b", "Intervention", 260),
                    PhaseTestFixture.Heading("heading-m", "Maintenance", 420),
                ]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("m", result.Payload.Phases[2].Code);
        Assert.AreEqual(PhaseNormalizedType.Maintenance, result.Payload.Phases[2].NormalizedType);
        Assert.AreEqual(PhaseEvidenceSource.Ocr, result.Payload.Phases[2].Source);
    }

    [TestMethod]
    public async Task RepeatedMaintenanceAndGeneralizationPhasesReceiveStableNumbering()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("divider-1", 100),
                    PhaseTestFixture.Segment("divider-2", 180),
                    PhaseTestFixture.Segment("divider-3", 260),
                    PhaseTestFixture.Segment("divider-4", 340),
                    PhaseTestFixture.Segment("divider-5", 420),
                ],
                headings:
                [
                    PhaseTestFixture.Heading("heading-a", "A", 60),
                    PhaseTestFixture.Heading("heading-b", "B", 140),
                    PhaseTestFixture.Heading("heading-g1", "Generalization", 220),
                    PhaseTestFixture.Heading("heading-m1", "Maintenance", 300),
                    PhaseTestFixture.Heading("heading-g2", "G", 380),
                    PhaseTestFixture.Heading("heading-m2", "M", 460),
                ]));

        Assert.IsTrue(result.Succeeded);
        string[] expectedCodes = ["a", "b", "g1", "m1", "g2", "m2"];
        CollectionAssert.AreEqual(expectedCodes, result.Payload.Phases.Select(item => item.Code).ToArray());
    }

    [TestMethod]
    public async Task TopPanelOnlyHeadingsAndDividerPropagateToAlignedPanel()
    {
        var topPanel = new PhasePanelEvidence(
            PhaseTestFixture.PeerPanelId,
            PhaseTestFixture.PlotBounds,
            [PhaseTestFixture.Segment("top-divider", 260, panelId: PhaseTestFixture.PeerPanelId)],
            [
                PhaseTestFixture.Heading("top-a", "Baseline", 130, PhaseTestFixture.PeerPanelId),
                PhaseTestFixture.Heading("top-b", "Intervention", 390, PhaseTestFixture.PeerPanelId),
            ],
            shareDividersWithTarget: true);

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(alignedPanels: [topPanel]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Payload.Dividers.Count);
        Assert.AreEqual(PhaseEvidenceSource.CrossPanel, result.Payload.Dividers[0].Source);
        string[] expectedCodes = ["a", "b"];
        CollectionAssert.AreEqual(expectedCodes, result.Payload.Phases.Select(item => item.Code).ToArray());
        Assert.IsTrue(result.Payload.Phases.All(item => item.Source == PhaseEvidenceSource.CrossPanel));
        CollectionAssert.Contains(result.Warnings.ToArray(), "phase_heading_propagated_from_aligned_panel");
    }

    [TestMethod]
    public async Task ExtraAnnotationLinesDoNotCreateAdditionalPhases()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments:
                [
                    PhaseTestFixture.Segment("divider", 260),
                    PhaseTestFixture.Segment("callout", 130, kind: PhaseSegmentKind.AnnotationStroke),
                    PhaseTestFixture.Segment("axis", 80, kind: PhaseSegmentKind.YAxis),
                    PhaseTestFixture.Segment("border", 490, kind: PhaseSegmentKind.PanelBorder),
                ]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Payload.Dividers.Count);
        Assert.AreEqual(2, result.Payload.Phases.Count);
    }

    [TestMethod]
    public async Task AssignmentsUseOriginalXAndBoundaryBelongsToFollowingPhase()
    {
        string beforeId = PointId(1);
        string boundaryId = PointId(2);
        string afterId = PointId(3);
        PhasePointEvidence[] points =
        [
            PhaseTestFixture.Point(beforeId, PhaseTestFixture.InterventionOneId, 259.999),
            PhaseTestFixture.Point(boundaryId, PhaseTestFixture.InterventionOneId, 260),
            PhaseTestFixture.Point(afterId, PhaseTestFixture.InterventionOneId, 261),
        ];
        PhaseSeriesEvidence[] series =
        [PhaseTestFixture.Series(PhaseTestFixture.InterventionOneId, PhaseNormalizedType.Intervention, points.Select(item => item.PointId))];

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("divider", 260)],
                points: points,
                series: series));

        Assert.IsTrue(result.Succeeded);
        string firstPhase = result.Payload.Phases[0].PhaseId;
        string secondPhase = result.Payload.Phases[1].PhaseId;
        Assert.AreEqual(firstPhase, result.Payload.Assignments.Single(item => item.PointId == beforeId).PhaseId);
        Assert.AreEqual(secondPhase, result.Payload.Assignments.Single(item => item.PointId == boundaryId).PhaseId);
        Assert.AreEqual(secondPhase, result.Payload.Assignments.Single(item => item.PointId == afterId).PhaseId);
        double[] expectedOriginalX = [259.999, 260, 261];
        CollectionAssert.AreEqual(
            expectedOriginalX,
            result.Payload.Assignments.Select(item => item.OriginalX).ToArray());
    }

    [TestMethod]
    public async Task MultipleBaselineRelationsShareReferencesWithoutCopyingPoints()
    {
        string baselinePoint = PointId(10);
        string interventionOnePoint = PointId(11);
        string interventionTwoPoint = PointId(12);
        PhasePointEvidence[] points =
        [
            PhaseTestFixture.Point(baselinePoint, PhaseTestFixture.BaselineSeriesId, 100),
            PhaseTestFixture.Point(interventionOnePoint, PhaseTestFixture.InterventionOneId, 300),
            PhaseTestFixture.Point(interventionTwoPoint, PhaseTestFixture.InterventionTwoId, 340),
        ];
        PhaseSeriesEvidence[] series =
        [
            PhaseTestFixture.Series(
                PhaseTestFixture.BaselineSeriesId,
                PhaseNormalizedType.Baseline,
                [baselinePoint]),
            PhaseTestFixture.Series(
                PhaseTestFixture.InterventionOneId,
                PhaseNormalizedType.Intervention,
                [interventionOnePoint]),
            PhaseTestFixture.Series(
                PhaseTestFixture.InterventionTwoId,
                PhaseNormalizedType.Intervention,
                [interventionTwoPoint]),
        ];

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("divider", 240)],
                points: points,
                series: series));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, result.Payload.SeriesRelations.Count);
        Assert.IsTrue(result.Payload.SeriesRelations.All(
            relation => relation.SharedBaselineSeriesId == PhaseTestFixture.BaselineSeriesId));
        Assert.AreEqual(points.Length, result.Payload.Assignments.Count);
        Assert.AreEqual(points.Length, result.Payload.Assignments.Select(item => item.PointId).Distinct().Count());
    }

    [TestMethod]
    public async Task StaggeredMultipleBaselinePanelsKeepIndependentPhaseOnsets()
    {
        var staggeredPeer = new PhasePanelEvidence(
            PhaseTestFixture.PeerPanelId,
            PhaseTestFixture.PlotBounds,
            [PhaseTestFixture.Segment("peer-onset", 340, panelId: PhaseTestFixture.PeerPanelId)],
            Array.Empty<PhaseHeadingEvidence>());

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("target-onset", 180)],
                alignedPanels: [staggeredPeer]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Payload.Dividers.Count);
        Assert.AreEqual(180, result.Payload.Dividers[0].OriginalX, 0.001);
        string[] expectedCodes = ["a", "b"];
        CollectionAssert.AreEqual(expectedCodes, result.Payload.Phases.Select(item => item.Code).ToArray());
    }

    [TestMethod]
    public async Task MultipleTargetedBaselinesRemainScopedToTheirInterventions()
    {
        const string secondBaselineSeriesId = "30000000-0000-0000-0000-000000000006";
        string baselineOnePoint = PointId(13);
        string baselineTwoPoint = PointId(14);
        string interventionOnePoint = PointId(15);
        string interventionTwoPoint = PointId(16);
        PhasePointEvidence[] points =
        [
            PhaseTestFixture.Point(baselineOnePoint, PhaseTestFixture.BaselineSeriesId, 100),
            PhaseTestFixture.Point(baselineTwoPoint, secondBaselineSeriesId, 120),
            PhaseTestFixture.Point(interventionOnePoint, PhaseTestFixture.InterventionOneId, 300),
            PhaseTestFixture.Point(interventionTwoPoint, PhaseTestFixture.InterventionTwoId, 340),
        ];
        PhaseSeriesEvidence[] series =
        [
            PhaseTestFixture.Series(
                PhaseTestFixture.BaselineSeriesId,
                PhaseNormalizedType.Baseline,
                [baselineOnePoint],
                [PhaseTestFixture.InterventionOneId]),
            PhaseTestFixture.Series(
                secondBaselineSeriesId,
                PhaseNormalizedType.Baseline,
                [baselineTwoPoint],
                [PhaseTestFixture.InterventionTwoId]),
            PhaseTestFixture.Series(
                PhaseTestFixture.InterventionOneId,
                PhaseNormalizedType.Intervention,
                [interventionOnePoint]),
            PhaseTestFixture.Series(
                PhaseTestFixture.InterventionTwoId,
                PhaseNormalizedType.Intervention,
                [interventionTwoPoint]),
        ];

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("divider", 240)],
                points: points,
                series: series));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(
            PhaseTestFixture.BaselineSeriesId,
            result.Payload.SeriesRelations.Single(
                relation => relation.InterventionSeriesId == PhaseTestFixture.InterventionOneId)
                .SharedBaselineSeriesId);
        Assert.AreEqual(
            secondBaselineSeriesId,
            result.Payload.SeriesRelations.Single(
                relation => relation.InterventionSeriesId == PhaseTestFixture.InterventionTwoId)
                .SharedBaselineSeriesId);
        Assert.AreEqual(points.Length, result.Payload.Assignments.Select(item => item.PointId).Distinct().Count());
    }

    [TestMethod]
    public async Task MultipleProbeRelationsRemainScopedToApplicableInterventions()
    {
        string interventionOnePoint = PointId(20);
        string interventionTwoPoint = PointId(21);
        string maintenancePoint = PointId(22);
        string generalizationPoint = PointId(23);
        PhasePointEvidence[] points =
        [
            PhaseTestFixture.Point(interventionOnePoint, PhaseTestFixture.InterventionOneId, 280),
            PhaseTestFixture.Point(interventionTwoPoint, PhaseTestFixture.InterventionTwoId, 300),
            PhaseTestFixture.Point(maintenancePoint, PhaseTestFixture.MaintenanceSeriesId, 360),
            PhaseTestFixture.Point(generalizationPoint, PhaseTestFixture.GeneralizationSeriesId, 380),
        ];
        PhaseSeriesEvidence[] series =
        [
            PhaseTestFixture.Series(
                PhaseTestFixture.InterventionOneId,
                PhaseNormalizedType.Intervention,
                [interventionOnePoint]),
            PhaseTestFixture.Series(
                PhaseTestFixture.InterventionTwoId,
                PhaseNormalizedType.Intervention,
                [interventionTwoPoint]),
            PhaseTestFixture.Series(
                PhaseTestFixture.MaintenanceSeriesId,
                PhaseNormalizedType.Maintenance,
                [maintenancePoint],
                [PhaseTestFixture.InterventionOneId]),
            PhaseTestFixture.Series(
                PhaseTestFixture.GeneralizationSeriesId,
                PhaseNormalizedType.Generalization,
                [generalizationPoint],
                [PhaseTestFixture.InterventionOneId, PhaseTestFixture.InterventionTwoId]),
        ];

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("divider", 240)],
                points: points,
                series: series));

        Assert.IsTrue(result.Succeeded);
        PhaseSeriesRelation relationOne = result.Payload.SeriesRelations.Single(
            relation => relation.InterventionSeriesId == PhaseTestFixture.InterventionOneId);
        PhaseSeriesRelation relationTwo = result.Payload.SeriesRelations.Single(
            relation => relation.InterventionSeriesId == PhaseTestFixture.InterventionTwoId);
        CollectionAssert.AreEqual(
            new[] { PhaseTestFixture.GeneralizationSeriesId, PhaseTestFixture.MaintenanceSeriesId },
            relationOne.ApplicableProbeSeriesIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { PhaseTestFixture.GeneralizationSeriesId },
            relationTwo.ApplicableProbeSeriesIds.ToArray());
    }

    [TestMethod]
    public async Task GeneralizationProbeWithinInterventionKeepsInterventionPhaseAssignment()
    {
        string interventionPoint = PointId(30);
        string probePoint = PointId(31);
        PhasePointEvidence[] points =
        [
            PhaseTestFixture.Point(interventionPoint, PhaseTestFixture.InterventionOneId, 300),
            PhaseTestFixture.Point(probePoint, PhaseTestFixture.GeneralizationSeriesId, 360),
        ];
        PhaseSeriesEvidence[] series =
        [
            PhaseTestFixture.Series(
                PhaseTestFixture.InterventionOneId,
                PhaseNormalizedType.Intervention,
                [interventionPoint]),
            PhaseTestFixture.Series(
                PhaseTestFixture.GeneralizationSeriesId,
                PhaseNormalizedType.Generalization,
                [probePoint],
                [PhaseTestFixture.InterventionOneId]),
        ];

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: [PhaseTestFixture.Segment("divider", 240)],
                headings:
                [
                    PhaseTestFixture.Heading("heading-a", "Baseline", 120),
                    PhaseTestFixture.Heading("heading-b", "Intervention", 360),
                ],
                points: points,
                series: series));

        Assert.IsTrue(result.Succeeded);
        string interventionPhaseId = result.Payload.Phases.Single(phase => phase.Code == "b").PhaseId;
        Assert.AreEqual(
            interventionPhaseId,
            result.Payload.Assignments.Single(item => item.PointId == probePoint).PhaseId);
        CollectionAssert.AreEqual(
            new[] { PhaseTestFixture.GeneralizationSeriesId },
            result.Payload.SeriesRelations.Single().ApplicableProbeSeriesIds.ToArray());
    }

    [TestMethod]
    public async Task ManualMoveAndRelabelPersistAcrossRerun()
    {
        PhaseReasoningRequest initialRequest = PhaseTestFixture.Request(
            segments: [PhaseTestFixture.Segment("divider", 240)]);
        PhaseReasoningResult initial = await PhaseTestFixture.ResolveAsync(initialRequest);
        var editor = new PhaseManualEditor();
        PhaseEditResult moved = editor.Apply(
            initial.Payload.ManualOverrides,
            new MovePhaseDividerCommand(
                CommandId(40),
                initial.Payload.Dividers.Single().DividerId,
                280,
                initial.Payload.Dividers.Single().Style,
                initial.Payload.Dividers.Single().OriginalX),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);
        PhaseEditResult relabeled = editor.Apply(
            moved.Overrides,
            new RelabelPhaseCommand(
                CommandId(41),
                initial.Payload.Phases[1].PhaseId,
                "user-b",
                PhaseNormalizedType.Intervention,
                "Confirmed intervention",
                initial.Payload.Phases[1].OriginalXMinimum,
                initial.Payload.Phases[1].OriginalXMaximum),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);

        PhaseReasoningResult rerun = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: initialRequest.Segments,
                overrides: relabeled.Overrides));

        Assert.IsTrue(rerun.Succeeded);
        PhaseDivider divider = rerun.Payload.Dividers.Single();
        Assert.AreEqual(280, divider.OriginalX);
        Assert.AreEqual(PhaseEvidenceSource.Manual, divider.Source);
        PhaseRegion second = rerun.Payload.Phases[1];
        Assert.AreEqual(initial.Payload.Phases[1].PhaseId, second.PhaseId);
        Assert.AreEqual("user-b", second.Code);
        Assert.AreEqual("Confirmed intervention", second.LabelText);
        Assert.AreEqual(PhaseEvidenceSource.Manual, second.Source);
    }

    [TestMethod]
    public async Task UuidVariantsAndMovedDottedStylePersistAcrossRerun()
    {
        PhaseReasoningRequest initialRequest = PhaseTestFixture.Request(
            segments:
            [
                PhaseTestFixture.Segment(
                    "divider-dot-1",
                    240,
                    30,
                    100,
                    PhaseDividerStyle.Dotted),
                PhaseTestFixture.Segment(
                    "divider-dot-2",
                    240,
                    110,
                    180,
                    PhaseDividerStyle.Dotted),
                PhaseTestFixture.Segment(
                    "divider-dot-3",
                    240,
                    190,
                    250,
                    PhaseDividerStyle.Dotted),
            ]);
        PhaseReasoningResult initial = await PhaseTestFixture.ResolveAsync(initialRequest);
        PhaseDivider detected = initial.Payload.Dividers.Single();
        var editor = new PhaseManualEditor();
        PhaseEditResult moved = editor.Apply(
            initial.Payload.ManualOverrides,
            new MovePhaseDividerCommand(
                CommandId(60).ToUpperInvariant(),
                detected.DividerId.ToUpperInvariant(),
                280,
                detected.Style,
                detected.OriginalX),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);
        PhaseEditResult relabeled = editor.Apply(
            moved.Overrides,
            new RelabelPhaseCommand(
                CommandId(61).ToUpperInvariant(),
                initial.Payload.Phases[1].PhaseId.ToUpperInvariant(),
                "confirmed-b",
                PhaseNormalizedType.Intervention,
                "Confirmed intervention",
                initial.Payload.Phases[1].OriginalXMinimum,
                initial.Payload.Phases[1].OriginalXMaximum),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);

        PhaseDividerSegment[] changedSegments = initialRequest.Segments
            .Append(PhaseTestFixture.Segment(
                "divider-dot-4",
                240,
                252,
                258,
                PhaseDividerStyle.Dotted))
            .ToArray();
        PhaseReasoningResult rerun = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: changedSegments,
                overrides: relabeled.Overrides));

        Assert.IsTrue(rerun.Succeeded);
        PhaseDivider divider = rerun.Payload.Dividers.Single();
        Assert.AreEqual(280, divider.OriginalX);
        Assert.AreEqual(PhaseDividerStyle.Dotted, divider.Style);
        Assert.AreEqual(detected.DividerId, divider.DividerId);
        Assert.AreEqual("confirmed-b", rerun.Payload.Phases[1].Code);
        Assert.AreEqual(detected.DividerId, moved.Audit?.TargetId);

        PhaseEditResult deleted = editor.Apply(
            rerun.Payload.ManualOverrides,
            new DeletePhaseDividerCommand(
                CommandId(62).ToUpperInvariant(),
                divider.DividerId.ToUpperInvariant(),
                divider.OriginalX),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);
        PhaseReasoningResult afterDelete = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: changedSegments,
                overrides: deleted.Overrides));

        Assert.IsTrue(deleted.Succeeded);
        Assert.AreEqual(divider.DividerId, deleted.Audit?.TargetId);
        Assert.IsTrue(afterDelete.Succeeded);
        Assert.AreEqual(0, afterDelete.Payload.Dividers.Count);
        Assert.AreEqual(1, afterDelete.Payload.Phases.Count);
    }

    [TestMethod]
    public async Task RecoveredEarlierBoundaryDoesNotStealMovedOrDeletedDividerLineage()
    {
        PhaseReasoningRequest initialRequest = PhaseTestFixture.Request(
            segments: [PhaseTestFixture.Segment("right-boundary", 340)]);
        PhaseReasoningResult initial = await PhaseTestFixture.ResolveAsync(initialRequest);
        PhaseDivider detected = initial.Payload.Dividers.Single();
        var editor = new PhaseManualEditor();
        PhaseEditResult moved = editor.Apply(
            initial.Payload.ManualOverrides,
            new MovePhaseDividerCommand(
                CommandId(70),
                detected.DividerId,
                360,
                detected.Style,
                detected.OriginalX),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);
        PhaseDividerSegment[] recoveredSegments =
        [
            PhaseTestFixture.Segment("recovered-left-boundary", 180),
            PhaseTestFixture.Segment("right-boundary", 340),
        ];

        PhaseReasoningResult rerun = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: recoveredSegments,
                overrides: moved.Overrides));

        Assert.IsTrue(rerun.Succeeded);
        double[] expectedMovedCoordinates = [180, 360];
        CollectionAssert.AreEqual(
            expectedMovedCoordinates,
            rerun.Payload.Dividers.Select(item => item.OriginalX).ToArray());
        Assert.AreEqual(PhaseEvidenceSource.ProfilePrior, rerun.Payload.Dividers[0].Source);
        Assert.AreEqual(PhaseEvidenceSource.Manual, rerun.Payload.Dividers[1].Source);

        PhaseDivider movedDivider = rerun.Payload.Dividers[1];
        PhaseEditResult deleted = editor.Apply(
            rerun.Payload.ManualOverrides,
            new DeletePhaseDividerCommand(
                CommandId(71),
                movedDivider.DividerId,
                movedDivider.OriginalX),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);
        PhaseReasoningResult afterDelete = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: recoveredSegments,
                overrides: deleted.Overrides));

        Assert.IsTrue(afterDelete.Succeeded);
        Assert.AreEqual(1, afterDelete.Payload.Dividers.Count);
        Assert.AreEqual(180, afterDelete.Payload.Dividers[0].OriginalX);
        Assert.AreEqual(2, afterDelete.Payload.Phases.Count);
    }

    [TestMethod]
    public async Task ManualAddAndDeletePersistAcrossRerun()
    {
        PhaseReasoningRequest initialRequest = PhaseTestFixture.Request(
            segments: [PhaseTestFixture.Segment("divider", 240)]);
        PhaseReasoningResult initial = await PhaseTestFixture.ResolveAsync(initialRequest);
        var editor = new PhaseManualEditor();
        PhaseEditResult added = editor.Apply(
            initial.Payload.ManualOverrides,
            new AddPhaseDividerCommand(
                CommandId(50),
                "40000000-0000-0000-0000-000000000050",
                380,
                PhaseDividerStyle.Dotted),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);
        PhaseEditResult deleted = editor.Apply(
            added.Overrides,
            new DeletePhaseDividerCommand(
                CommandId(51).ToUpperInvariant(),
                initial.Payload.Dividers.Single().DividerId.ToUpperInvariant(),
                initial.Payload.Dividers.Single().OriginalX),
            PhaseTestFixture.PlotBounds,
            CancellationToken.None);

        PhaseReasoningResult rerun = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(
                segments: initialRequest.Segments,
                overrides: deleted.Overrides));

        Assert.IsTrue(rerun.Succeeded);
        PhaseDivider divider = rerun.Payload.Dividers.Single();
        Assert.AreEqual("40000000-0000-0000-0000-000000000050", divider.DividerId);
        Assert.AreEqual(380, divider.OriginalX);
        Assert.AreEqual(PhaseDividerStyle.Dotted, divider.Style);
        Assert.AreEqual(PhaseEvidenceSource.Manual, divider.Source);
        Assert.AreEqual(initial.Payload.Dividers.Single().DividerId, deleted.Audit?.TargetId);
    }

    [TestMethod]
    public async Task InvalidEnvelopeReturnsStructuredFailureAndEmptyPayload()
    {
        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(contractVersion: PhaseReasoningContract.Version + 1));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("PHASE_CONTRACT_UNSUPPORTED", result.Failure?.Code);
        Assert.AreEqual(0, result.Payload.Dividers.Count);
        Assert.AreEqual(0, result.Payload.Phases.Count);
        Assert.AreEqual(0, result.Payload.Assignments.Count);
        Assert.AreEqual(PhaseReasoningContract.CoordinateSpace, result.CoordinateSpace);
    }

    [TestMethod]
    public async Task InvalidManualDividerIdentityReturnsStructuredFailure()
    {
        var overrides = new PhaseManualOverrides(
            [new PhaseManualDivider("not-a-uuid", 240, PhaseDividerStyle.Solid)]);

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(
            PhaseTestFixture.Request(overrides: overrides));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("PHASE_INVALID_MANUAL_OVERRIDE", result.Failure?.Code);
        Assert.AreEqual(0, result.Payload.Dividers.Count);
    }

    [TestMethod]
    public async Task InvalidIdsAndHashReturnSchemaSafeFailureEnvelope()
    {
        var request = new PhaseReasoningRequest(
            "not-a-project-uuid",
            "not-a-panel-uuid",
            "not-a-hash",
            PhaseTestFixture.PlotBounds,
            [],
            [],
            [],
            []);

        PhaseReasoningResult result = await PhaseTestFixture.ResolveAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(Guid.Empty.ToString(), result.ProjectId);
        Assert.AreEqual(Guid.Empty.ToString(), result.PanelId);
        Assert.AreEqual(new string('0', 64), result.InputSha256);
        Assert.IsTrue(Guid.TryParseExact(result.RunId, "D", out _));
    }

    [TestMethod]
    public async Task ResolveHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await PhaseTestFixture.ResolveAsync(
                PhaseTestFixture.Request(segments: [PhaseTestFixture.Segment("divider", 240)]),
                cancellation.Token));
    }

    [TestMethod]
    public async Task EquivalentRerunsRemainDeterministicAndDefensivelyFrozen()
    {
        PhaseDividerSegment[] callerSegments = [PhaseTestFixture.Segment("divider", 240)];
        PhaseReasoningRequest request = PhaseTestFixture.Request(segments: callerSegments);
        callerSegments[0] = PhaseTestFixture.Segment("mutated", 360);

        PhaseReasoningResult first = await PhaseTestFixture.ResolveAsync(request);
        PhaseReasoningResult second = await PhaseTestFixture.ResolveAsync(request);

        Assert.IsTrue(first.Succeeded);
        Assert.AreEqual(240, first.Payload.Dividers.Single().OriginalX, 0.001);
        CollectionAssert.AreEqual(
            first.Payload.Dividers.Select(item => item.DividerId).ToArray(),
            second.Payload.Dividers.Select(item => item.DividerId).ToArray());
        CollectionAssert.AreEqual(
            first.Payload.Phases.Select(item => item.PhaseId).ToArray(),
            second.Payload.Phases.Select(item => item.PhaseId).ToArray());
        Assert.AreNotEqual(first.RunId, second.RunId);
        Assert.IsTrue(Guid.TryParseExact(first.RunId, "D", out Guid firstRunId));
        Assert.IsTrue(Guid.TryParseExact(second.RunId, "D", out Guid secondRunId));
        Assert.AreNotEqual(Guid.Empty, firstRunId);
        Assert.AreNotEqual(Guid.Empty, secondRunId);
    }

    private static string PointId(int value) =>
        $"70000000-0000-0000-0000-{value:000000000000}";

    private static string CommandId(int value) =>
        $"80000000-0000-0000-0000-{value:000000000000}";
}
