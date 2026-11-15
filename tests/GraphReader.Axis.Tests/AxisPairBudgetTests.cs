// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisPairBudgetTests
{
    [TestMethod]
    public async Task ExhaustedPairBudgetFailsWithoutReturningPartialGeometry()
    {
        AxisGeometryRequest request = new AxisFixtureBuilder().CleanAxes()
            .Line(400, 300, 400, 50, id: "second-y").Build();
        var detector = new AxisGeometryDetector();
        AxisGeometryResult unrestricted = await detector.DetectAsync(request);
        Assert.IsNotNull(unrestricted);
        AxisGeometryDetectionException failure = await Assert.ThrowsExactlyAsync<AxisGeometryDetectionException>(async () =>
            await detector.DetectAsync(request with { MaximumExaminedPairs = 1 }));
        Assert.AreEqual(AxisGeometryFailureReason.PairBudgetExceeded, failure.Reason);
        Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", failure.Code);
    }

    [TestMethod]
    public void ExistingStringConstructorRetainsItsCodeAndUnspecifiedReason()
    {
        var failure = new AxisGeometryDetectionException("owned");
        Assert.AreEqual("AXIS_GEOMETRY_NOT_FOUND", failure.Code);
        Assert.AreEqual(AxisGeometryFailureReason.Unspecified, failure.Reason);
        Assert.AreEqual("owned", failure.Message);
    }
}
