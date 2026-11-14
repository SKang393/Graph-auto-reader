// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App;
using GraphReader.App.Services;
using GraphReader.Axis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class OpenCvAxisProposalTests
{
    [TestMethod]
    public void NativeWorkerArgumentsCannotBeMixed()
    {
        _ = StartupArguments.Parse(["--native-axis-worker"]);
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--native-axis-worker", "--portable-smoke"]));
    }

    [TestMethod]
    public void ResponseRejectsOutOfBoundsTicksAndUnknownProvider()
    {
        var valid = new RustAxisProposalResult(new(10, 90, 90, 10, .8), null) { Provider = "opencv-review" };
        Assert.IsTrue(OpenCvAxisProposalService.Valid(valid, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.Valid(valid with { Provider = "rust" }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.Valid(valid with { Geometry = valid.Geometry! with { Right = 100 } }, 100, 100));
        Assert.IsFalse(OpenCvAxisProposalService.Valid(valid with { Geometry = valid.Geometry! with { XTickPositions = [20] } }, 100, 100));
    }

    [TestMethod]
    public async Task CancellationBeforeStartDoesNotLaunchWorker()
    {
        var service = new OpenCvAxisProposalService(System.IO.Path.GetFullPath("missing-worker.exe"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.SuggestAsync([1], cancelled.Token));
    }
}
