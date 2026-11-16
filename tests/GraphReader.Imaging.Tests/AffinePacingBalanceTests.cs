// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Imaging.Tests;

[TestClass]
public sealed class AffinePacingBalanceTests
{
    [TestMethod]
    public void EqualActiveAndWaitHaveNoDebt()
    {
        var balance = new AffinePacingBalance();
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), balance.RecordActive(TimeSpan.FromMilliseconds(2)));
        balance.RecordWait(TimeSpan.FromMilliseconds(2));
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), balance.RecordActive(TimeSpan.FromMilliseconds(2)));
    }

    [TestMethod]
    public void OversleepIsCreditedAgainstLaterActiveSlices()
    {
        var balance = new AffinePacingBalance();
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), balance.RecordActive(TimeSpan.FromMilliseconds(2)));
        balance.RecordWait(TimeSpan.FromMilliseconds(5));
        Assert.AreEqual(TimeSpan.Zero, balance.RecordActive(TimeSpan.FromMilliseconds(2)));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), balance.RecordActive(TimeSpan.FromMilliseconds(2)));
        balance.RecordWait(TimeSpan.FromMilliseconds(.5));
        Assert.AreEqual(TimeSpan.FromMilliseconds(2.5), balance.RecordActive(TimeSpan.FromMilliseconds(2)));
    }

    [TestMethod]
    public void LongWaitCreditIsCappedAtFourSlices()
    {
        var balance = new AffinePacingBalance();
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), balance.RecordActive(TimeSpan.FromMilliseconds(2)));
        balance.RecordWait(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 4; i++) Assert.AreEqual(TimeSpan.Zero, balance.RecordActive(TimeSpan.FromMilliseconds(2)));
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), balance.RecordActive(TimeSpan.FromMilliseconds(2)));
    }

    [TestMethod]
    public void InvalidDurationsAndUnrequestedWaitAreRejected()
    {
        var balance = new AffinePacingBalance();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => balance.RecordActive(TimeSpan.FromTicks(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => balance.RecordWait(TimeSpan.FromTicks(-1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => balance.RecordWait(TimeSpan.Zero));
        Assert.AreEqual(TimeSpan.Zero, balance.RecordActive(TimeSpan.Zero));
    }
}
