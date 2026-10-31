// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrTickLaneStrokeDiagnosticsTests
{
    [TestMethod]
    public void StrokePredicatesExplainEveryExistingRejectionWithoutChangingUsability()
    {
        var plot = new OcrRectangle(20, 20, 100, 80);
        var valid = new OcrXAxisTickStroke("tick", new(50, 100), new(50, 98), new(50, 103), 0.9, ["source"]);
        OcrXAxisTickStroke[] strokes =
        [
            valid,
            valid with { TickId = " " },
            valid with { Center = new(double.NaN, 100) },
            valid with { LineStart = new(50, double.PositiveInfinity) },
            valid with { LineEnd = new(double.NaN, 103) },
            valid with { Confidence = double.NaN },
            valid with { SupportingCandidateIds = [] },
            valid with { SupportingCandidateIds = [" "] },
            valid with { Center = new(50, 104.01) },
            valid with { LineEnd = new(56, 103) },
            valid with { Center = new(50, 104), Confidence = 1, LineEnd = new(55, 103) },
            null!,
        ];

        IReadOnlyList<OcrTickLaneStrokeDiagnostic> observations =
            TickLaneTextRegionRecovery.DescribeXAxisTickStrokes(strokes, plot);
        Assert.HasCount(strokes.Length, observations);
        Assert.IsTrue(observations[0].Usable);
        Assert.IsTrue(observations[1].InvalidTickId);
        Assert.IsTrue(observations[2].NonfiniteCenter);
        Assert.IsNull(observations[2].CenterX);
        Assert.IsTrue(observations[3].NonfiniteLineStart);
        Assert.IsTrue(observations[4].NonfiniteLineEnd);
        Assert.IsTrue(observations[5].InvalidConfidence);
        Assert.IsNull(observations[5].Confidence);
        Assert.IsTrue(observations[6].MissingSupportingCandidates);
        Assert.IsTrue(observations[7].InvalidSupportingCandidateId);
        Assert.IsTrue(observations[8].OutsideAxisDistance);
        Assert.IsTrue(observations[9].NotNearVertical);
        Assert.IsTrue(observations[10].Usable, "Axis-distance and direction equalities are accepted.");
        Assert.IsTrue(observations[11].MissingStroke);
        Assert.AreEqual(TickLaneTextRegionRecovery.CountUsableXAxisTickStrokeSeeds(strokes, plot),
            observations.Count(static item => item.Usable));
        for (int index = 0; index < strokes.Length; index++)
        {
            Assert.AreEqual(index, observations[index].SuppliedIndex);
            Assert.AreEqual(TickLaneTextRegionRecovery.HasUsableXAxisTickStrokeSeeds([strokes[index]], plot),
                observations[index].Usable);
        }
        _ = JsonSerializer.Serialize(observations);
    }
}
