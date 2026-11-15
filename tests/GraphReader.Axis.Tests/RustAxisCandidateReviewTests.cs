// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Reflection;
using System.Diagnostics;
using System.Text;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class RustAxisCandidateReviewTests
{
    private static readonly MethodInfo Parser = typeof(RustAxisCandidateReview).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;

    [TestMethod]
    public void CompleteDistinctBoxesPreserveOrderWithoutInventingEvidence()
    {
        RustAxisCandidateReviewResult result = Parse("REVIEW1 2\nBOX 10 90 50 10 10,30,50 10,50,90\nBOX 60 90 90 10 60,75,90 10,50,90\n", 0);
        Assert.IsNull(result.ErrorCode);
        Assert.HasCount(2, result.Candidates);
        Assert.AreEqual(10, result.Candidates[0].Left);
        Assert.AreEqual(60, result.Candidates[1].Left);
        Assert.IsTrue(result.Candidates.All(box => box.Confidence == 0 && box.FirstTickX == box.XTickPositions[0] &&
            box.LastTickX == box.XTickPositions[^1] && box.XTickPositions.Count == 3 && box.YTickPositions.Count == 3));
    }

    [TestMethod]
    [DataRow("REVIEW1 2\nBOX 10 90 50 10 10,30,50 10,50,90\n", 0)]
    [DataRow("REVIEW1 1\nBOX 10 90 50 10 10,30,50 10,50,90", 0)]
    [DataRow("REVIEW1 1\nBOX 10 90 50 10 10,30,50 10,50,90\nEXTRA\n", 0)]
    [DataRow("REVIEW1 1\nBOX 10 90 100 10 10,30,50 10,50,90\n", 0)]
    [DataRow("REVIEW1 2\nBOX 10 90 50 10 10,30,50 10,50,90\nBOX 10 90 50 10 10,30,50 10,50,90\n", 0)]
    [DataRow("REVIEW1 1\nBOX 10 90 50 10 10,30,50 10,50,90\n", 1)]
    [DataRow("REVIEW1 1\nBOX 10 90 50 10 10,30,30 10,50,90\n", 0)]
    [DataRow("REVIEW1 1\nBOX 10 90 50 10 10,30 10,50,90\n", 0)]
    [DataRow("REVIEW1 0\n", 0)]
    [DataRow("ERR NO_AXIS\n", 0)]
    public void MalformedIncompleteOrContradictoryResponsesNeverReturnChoices(string response, int exitCode)
    {
        RustAxisCandidateReviewResult result = Parse(response, exitCode);
        Assert.AreEqual("INVALID_RESPONSE", result.ErrorCode);
        Assert.IsEmpty(result.Candidates);
    }

    [TestMethod]
    public void MaximumListAndTypedFailureAreBounded()
    {
        var text = new StringBuilder("REVIEW1 128\n");
        for (int i = 0; i < 128; i++) text.Append("BOX ").Append(i).Append(" 90 500 10 200,300,500 10,50,90\n");
        RustAxisCandidateReviewResult result = Parse(text.ToString(), 0, width: 600);
        Assert.HasCount(128, result.Candidates);
        Assert.IsNull(result.ErrorCode);
        RustAxisCandidateReviewResult failure = Parse("ERR CPU_BUDGET_UNAVAILABLE\n", 1);
        Assert.AreEqual("CPU_BUDGET_UNAVAILABLE", failure.ErrorCode);
        Assert.IsEmpty(failure.Candidates);
    }

    [TestMethod]
    public async Task CancellationBeforeStartNeverLaunchesWorker()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await RustAxisCandidateReview.RunAsync("unused", 32, 32, new byte[1024], canceled.Token));
    }

    [TestMethod]
    public async Task MissingWorkerIsExplicitlyUnavailable()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows worker transport only."); return; }
        RustAxisCandidateReviewResult result = await RustAxisCandidateReview.RunAsync(
            Path.GetFullPath("missing-axis-review-worker.exe"), 32, 32, new byte[1024]);
        Assert.AreEqual("WORKER_START_FAILED", result.ErrorCode);
        Assert.IsEmpty(result.Candidates);
    }

    [TestMethod]
    public async Task BoundedReaderRejectsOverflowAndHonorsCancellation()
    {
        MethodInfo reader = typeof(RustAxisCandidateReview).GetMethod("ReadBoundedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var process = new Process();
        using var oversized = new MemoryStream(new byte[65_537]);
        var result = (Task<string?>)reader.Invoke(null, [oversized, 65_536, true, process, CancellationToken.None])!;
        Assert.IsNull(await result);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var input = new MemoryStream(new byte[1]);
        var pending = (Task<string?>)reader.Invoke(null, [input, 65_536, true, process, canceled.Token])!;
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
    }

    private static RustAxisCandidateReviewResult Parse(string text, int code, int width = 100) =>
        (RustAxisCandidateReviewResult)Parser.Invoke(null, [text, code, width, 100])!;
}
