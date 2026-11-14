// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Integration.CalibrationOcr;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Ocr;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrWorkerTests
{
    [TestMethod]
    public void ExplicitGpuRequiresMatchingHardwareAndAtLeast256MiBLocalBudgetHeadroom()
    {
        var selection = new CalibrationOcrDeviceSelection("DirectMl", 2, 42, "Fixture GPU");
        DxgiAdapter[] devices = [new(0, "Other", 1, 1, 11, false), new(2, "Fixture GPU", 1, 2, 42, false)];
        var valid = CalibrationOcrSession.ResolveAdapter(selection, devices, _ => new(600UL * 1024 * 1024, 300UL * 1024 * 1024, 0, 0));
        Assert.AreEqual(2, valid.Adapter!.Index);
        Assert.IsNull(valid.Reason);
        Assert.AreEqual("CALIBRATION_OCR_ADAPTER_UNAVAILABLE", CalibrationOcrSession.ResolveAdapter(selection, [], _ => null).Reason);
        Assert.AreEqual("CALIBRATION_OCR_GPU_MEMORY_BUDGET_UNAVAILABLE", CalibrationOcrSession.ResolveAdapter(selection, devices, _ => null).Reason);
        Assert.AreEqual("CALIBRATION_OCR_GPU_MEMORY_HEADROOM_LOW", CalibrationOcrSession.ResolveAdapter(selection, devices, _ => new(500UL * 1024 * 1024, 300UL * 1024 * 1024, 0, 0)).Reason);
        Assert.AreEqual("CALIBRATION_OCR_GPU_MEMORY_HEADROOM_LOW", CalibrationOcrSession.ResolveAdapter(selection, devices, _ => new(100, 101, 0, 0)).Reason);
        Assert.AreEqual("CALIBRATION_OCR_ADAPTER_UNAVAILABLE", CalibrationOcrSession.ResolveAdapter(selection with { AdapterLuid = 43 }, devices, _ => null).Reason);
    }
    [TestMethod]
    public async Task WireRoundTripsMetadataAndRejectsOversizeAndTruncatedFrames()
    {
        byte[] payload = [1, 2, 3];
        CalibrationOcrRequest request = Request(payload);
        using var pipe = new MemoryStream();
        await CalibrationOcrWire.WriteRequestAsync(pipe, request, payload, CancellationToken.None);
        pipe.Position = 0;
        CalibrationOcrWorkItem? decoded = await CalibrationOcrWire.ReadRequestAsync(pipe, CancellationToken.None);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(request, decoded.Request);
        CollectionAssert.AreEqual(payload, decoded.EncodedImage);
        Assert.IsNull(await CalibrationOcrWire.ReadRequestAsync(pipe, CancellationToken.None));

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, CalibrationOcrWire.MaximumMetadataBytes + 1);
        using var oversized = new MemoryStream(prefix);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await CalibrationOcrWire.ReadRequestAsync(oversized, CancellationToken.None));
        using var truncated = new MemoryStream(pipe.ToArray()[..^1]);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
            await CalibrationOcrWire.ReadRequestAsync(truncated, CancellationToken.None));
        using var responsePipe = new MemoryStream();
        await CalibrationOcrWire.WriteResponseAsync(responsePipe,
            CalibrationOcrResponse.Failure(request, CalibrationOcrReasonCodes.InvalidInput), CancellationToken.None);
        responsePipe.Position = 0;
        CalibrationOcrResponse response = await CalibrationOcrWire.ReadResponseAsync(responsePipe, CancellationToken.None);
        Assert.AreEqual(request, response.Request);
        Assert.AreEqual(CalibrationOcrReasonCodes.InvalidInput, response.FailureCode);
    }

    [TestMethod]
    public void PersistentLoopReusesOneReaderRetainsEveryProposalAndDisposesOnEof() => StaTestHost.Run(() => Pump(RunAsync()));

    [TestMethod]
    public void FailedGpuAttemptCountsBothSessionsAndPreservesTime() => StaTestHost.Run(() => Pump(FailedGpuAttemptAsync()));

    private static async Task FailedGpuAttemptAsync()
    {
        byte[] image = Image();
        var gpuOwner = new FakeOwner();
        var cpuOwner = new FakeOwner();
        var selection = new CalibrationOcrDeviceSelection("DirectMl", 2, 42, "Fixture GPU");
        await using var session = new CalibrationOcrSession(new(12, 416, .4992, true),
            _ => Task.FromResult(new CalibrationOcrReader(new ThrowingRecognizer(),
                new OcrPipelineOptions { CropWidth = 320, CropHeight = 48 }, gpuOwner, [], "DirectMl", new(2, 42), "Fixture GPU")),
            selection,
            (reason, _) =>
            {
                Assert.IsTrue(gpuOwner.Disposed, "The GPU owner must be disposed before CPU creation.");
                return Task.FromResult(new CalibrationOcrReader(new FakeRecognizer(),
                    new OcrPipelineOptions { CropWidth = 320, CropHeight = 48 }, cpuOwner, [], fallbackReason: reason));
            },
            () => null);
        CalibrationOcrResponse result = await session.RecognizeAsync(new(Request(image), image), CancellationToken.None);
        Assert.AreEqual("Cpu", result.Runtime!.Provider);
        Assert.AreEqual("DirectMl", result.Runtime.RequestedProvider);
        Assert.AreEqual("CALIBRATION_OCR_DIRECTML_INFERENCE_FAILED", result.Runtime.FallbackReason);
        Assert.AreEqual(2, result.Runtime.SessionCreationCount);
        Assert.IsGreaterThanOrEqualTo(15d, result.Timings!.RecognitionMilliseconds);
        Assert.IsGreaterThanOrEqualTo(result.Timings.RecognitionMilliseconds, result.Timings.TotalMilliseconds);
    }

    [TestMethod]
    public void LaterBudgetFallbackCountsBothSessions() => StaTestHost.Run(() => Pump(LaterBudgetFallbackAsync()));

    private static async Task LaterBudgetFallbackAsync()
    {
        byte[] image = Image();
        var gpuOwner = new FakeOwner();
        string? unavailable = null;
        await using var session = new CalibrationOcrSession(new(12, 416, .4992, true),
            _ => Task.FromResult(new CalibrationOcrReader(new FakeRecognizer(),
                new OcrPipelineOptions { CropWidth = 320, CropHeight = 48 }, gpuOwner, [], "DirectMl", new(2, 42), "Fixture GPU")),
            new("DirectMl", 2, 42, "Fixture GPU"),
            (reason, _) =>
            {
                Assert.IsTrue(gpuOwner.Disposed);
                return Task.FromResult(new CalibrationOcrReader(new FakeRecognizer(),
                    new OcrPipelineOptions { CropWidth = 320, CropHeight = 48 }, new FakeOwner(), [], fallbackReason: reason));
            },
            () => unavailable);
        CalibrationOcrResponse first = await session.RecognizeAsync(new(Request(image), image), CancellationToken.None);
        Assert.AreEqual("DirectMl", first.Runtime!.Provider);
        Assert.AreEqual(1, first.Runtime.SessionCreationCount);
        unavailable = "CALIBRATION_OCR_GPU_MEMORY_HEADROOM_LOW";
        CalibrationOcrResponse second = await session.RecognizeAsync(new(Request(image), image), CancellationToken.None);
        Assert.AreEqual("Cpu", second.Runtime!.Provider);
        Assert.AreEqual(2, second.Runtime.SessionCreationCount);
        Assert.AreEqual(unavailable, second.Runtime.FallbackReason);
    }

    private static async Task RunAsync()
    {
        byte[] image = Image();
        var owner = new FakeOwner();
        var recognizer = new FakeRecognizer();
        int creations = 0;
        var session = new CalibrationOcrSession(new(12, 416, .4992, true), _ =>
        {
            creations++;
            return Task.FromResult(new CalibrationOcrReader(recognizer,
                new OcrPipelineOptions { CropWidth = 320, CropHeight = 48 }, owner, []));
        });
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        CalibrationOcrRequest first = Request(image), second = first with { RequestId = "second", Binding = "second-binding" };
        await CalibrationOcrWire.WriteRequestAsync(input, first, image, CancellationToken.None);
        await CalibrationOcrWire.WriteRequestAsync(input, second, image, CancellationToken.None);
        input.Position = 0;
        Assert.AreEqual(0, await CalibrationOcrWorker.RunLoopAsync(input, output, session, CancellationToken.None));
        Assert.AreEqual(1, creations);
        Assert.AreEqual(2, recognizer.Calls);
        Assert.IsTrue(owner.Disposed, "EOF must dispose the retained reader/session.");
        output.Position = 0;
        CalibrationOcrResponse cold = await CalibrationOcrWire.ReadResponseAsync(output, CancellationToken.None);
        CalibrationOcrResponse reused = await CalibrationOcrWire.ReadResponseAsync(output, CancellationToken.None);
        Assert.AreEqual(first, cold.Request);
        Assert.AreEqual(second, reused.Request);
        Assert.IsNull(cold.FailureCode);
        Assert.IsNotNull(cold.Timings);
        Assert.IsNotNull(reused.Timings);
        Assert.IsFalse(cold.Timings.ReusedSession);
        Assert.IsTrue(reused.Timings.ReusedSession);
        Assert.AreEqual(0d, reused.Timings.InitializationMilliseconds);
        Assert.HasCount(2, cold.Proposals);
        Assert.IsTrue(cold.Proposals.All(proposal => proposal.RequiresReview));
        CalibrationOcrProposal failed = cold.Proposals.Single(proposal => proposal.FailureCode is not null);
        Assert.AreEqual("Outcome", failed.Text);
        Assert.IsNull(failed.NumericValue);
        Assert.IsTrue(cold.YSequence!.NeedsReview);
        Assert.HasCount(1, cold.Proposals.Where(proposal => proposal.NumericLiteral).ToArray());
        Assert.IsTrue(cold.Proposals.SelectMany(proposal => proposal.Polygon).All(point => point.X is >= 0 and <= 96 && point.Y is >= 0 and <= 96));
        Assert.AreNotEqual(first.SourceFileSha256, first.PanelImageSha256, "Source-file and cropped-panel identity are distinct.");
        Assert.IsNotNull(cold.Runtime);
        Assert.AreEqual(1, cold.Runtime.SessionCreationCount);
        Assert.IsGreaterThan(0L, cold.Runtime.Memory.PrivateBytesAfter);
        Assert.AreEqual(output.Length, output.Position);
    }

    [TestMethod]
    public void StaleHashDimensionsAndPlotAreRejectedWithoutOpeningReader() => StaTestHost.Run(() => Pump(ValidateAsync()));

    private static async Task ValidateAsync()
    {
        int opened = 0;
        await using var session = new CalibrationOcrSession(new(12, 416, .4992, true), _ =>
        {
            opened++;
            throw new InvalidOperationException("A rejected input must not open a reader.");
        });
        byte[] image = Image();
        CalibrationOcrRequest valid = Request(image);
        foreach (CalibrationOcrRequest invalid in new[]
        {
            valid with { PanelImageSha256 = new string('0', 64) },
            valid with { Width = 95 },
            valid with { Plot = new(-1, 10, 80, 70) },
            valid with { ProtocolVersion = 2 },
        })
        {
            CalibrationOcrException failure = await Assert.ThrowsExactlyAsync<CalibrationOcrException>(
                async () => await session.RecognizeAsync(new(invalid, image), CancellationToken.None));
            Assert.AreEqual(CalibrationOcrReasonCodes.InvalidInput, failure.Reason);
        }
        Assert.AreEqual(0, opened);
    }

    [TestMethod]
    public void SequenceRejectionsCannotHideBehindValidBestChain()
    {
        CalibrationOcrProposal[] proposals = [Tick("a", 10, 1), Tick("b", 20, 2), Tick("c", 30, 1)];
        TickResolutionResult existing = MonotonicTickResolver.Resolve(proposals.Select(row =>
            new TickCandidate(row.RegionId, row.Polygon.Average(point => point.X), row.NumericValue!.Value, row.Confidence)).ToArray(),
            TickAxisDirection.IncreasingWithPixels);
        Assert.IsFalse(existing.NeedsReview, "This fixture reproduces the best-chain-only safety gap.");
        Assert.IsGreaterThan(0, existing.RejectedTicks.Count);
        CalibrationOcrSequenceCheck result = CalibrationOcrSession.CheckSequence(proposals, "X");
        Assert.IsTrue(result.NeedsReview);
        Assert.AreEqual(3, result.CandidateCount);
        Assert.AreEqual(1, result.RejectedCount);
        CollectionAssert.Contains(result.Reasons.ToArray(), CalibrationOcrReasonCodes.SequenceConflict);
    }

    [TestMethod]
    public async Task ProductionWorkerEntryFailsClosedBeforeBudgetOrReaderInitialization()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        Assert.AreEqual(2, await CalibrationOcrWorker.RunAsync(Path.GetTempPath(), WorkflowRuntimeEnvironment.Production,
            input, output, cancellationToken: CancellationToken.None));
        output.Position = 0;
        CalibrationOcrResponse response = await CalibrationOcrWire.ReadResponseAsync(output, CancellationToken.None);
        Assert.IsNull(response.Request);
        Assert.AreEqual(CalibrationOcrReasonCodes.Unavailable, response.FailureCode);
    }

    private static CalibrationOcrRequest Request(byte[] image) => new("first", "binding", new string('f', 64),
        Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), 96, 96, new(10, 10, 80, 70));

    private static CalibrationOcrProposal Tick(string id, int x, double value) => new(id,
        [new(x - 1, 75), new(x + 1, 75), new(x + 1, 82), new(x - 1, 82)],
        value.ToString(System.Globalization.CultureInfo.InvariantCulture), .9, value,
        value.ToString(System.Globalization.CultureInfo.InvariantCulture), true, "XTick", "X", null);

    private static byte[] Image()
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 96 * 96).ToArray();
        foreach (var (x, y) in new[] { (2, 30), (20, 75), (35, 30) })
            for (int row = y; row < y + 7; row++)
                for (int column = x; column < x + 4; column++) pixels[row * 96 + column] = 0;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(96, 96, 96, 96, PixelFormats.Gray8, null, pixels, 96)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class FakeRecognizer : ITextRecognizer
    {
        public string ModelId => "test";
        public string ModelVersion => "test";
        public string ModelSha256 => new('a', 64);
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(IReadOnlyList<OcrCrop> crops, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<OcrRecognition>>(crops.Select((crop, index) =>
                new OcrRecognition(crop.RegionId, OcrSourceImage.Original,
                    [new(index == 0 ? "Outcome" : "1", .9, OcrSourceImage.Original)], 1,
                    index == 0 ? new("test_failure", "error", "test", "test", true, "review") : null)).ToArray());
        }
    }

    private sealed class ThrowingRecognizer : ITextRecognizer
    {
        public string ModelId => "fixture";
        public string ModelVersion => "fixture";
        public string ModelSha256 => new('a', 64);
        public async ValueTask<IReadOnlyList<OcrRecognition>> RecognizeBatchAsync(IReadOnlyList<OcrCrop> crops, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken);
            throw new InvalidOperationException("Synthetic GPU failure.");
        }
    }

    private sealed class FakeOwner : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
