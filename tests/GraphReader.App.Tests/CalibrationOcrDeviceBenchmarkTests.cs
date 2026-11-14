// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Services;
using GraphReader.Ocr;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrDeviceBenchmarkTests
{
    private static readonly CalibrationOcrDeviceSelection Gpu = new("DirectMl", 0, 42, "Fixture GPU");

    [TestMethod]
    public async Task MatchingDeviceMeasuresFourReadsAndReusesWorker()
    {
        var workers = new List<Fake>();
        var benchmark = new CalibrationOcrDeviceBenchmark(d => { var worker = new Fake(d); workers.Add(worker); return worker; });
        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([Gpu], CancellationToken.None);
        Assert.AreEqual(2, result.Measurements.Count);
        Assert.IsTrue(result.Measurements[1].OutputMatchesCpu);
        Assert.IsNull(result.Measurements[1].FailureCode);
        Assert.IsTrue(workers.All(worker => worker.Disposed));
        var fixtures = CalibrationOcrBenchmarkFixture.CreateWorkloads();
        Assert.AreNotEqual(fixtures[0].Request.PanelImageSha256, fixtures[1].Request.PanelImageSha256);
        foreach (Fake worker in workers)
        {
            Assert.HasCount(4, worker.Requests);
            for (int read = 0; read < 4; read++)
            {
                var fixture = fixtures[read % 2];
                Assert.AreEqual(fixture.Request, worker.Requests[read].Request);
                Assert.AreEqual(fixture.Request.PanelImageSha256, worker.Requests[read].ImageHash);
            }
        }
    }

    [TestMethod]
    public void OwnedFixturesHaveDistinctSizesAndActualComponentCropWidths()
    {
        StaTestHost.Run(() =>
        {
            var fixtures = CalibrationOcrBenchmarkFixture.CreateWorkloads();
            Assert.HasCount(2, fixtures);
            var maximumWidths = new List<int>();
            foreach (var fixture in fixtures)
            {
                using var stream = new MemoryStream(fixture.Image, writable: false);
                BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
                Assert.AreEqual(fixture.Request.Width, frame.PixelWidth);
                Assert.AreEqual(fixture.Request.Height, frame.PixelHeight);
                var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
                byte[] pixels = new byte[frame.PixelWidth * frame.PixelHeight];
                gray.CopyPixels(pixels, frame.PixelWidth, 0);
                var image = new OcrImage(frame.PixelWidth, frame.PixelHeight, frame.PixelWidth,
                    pixels, OcrSourceImage.Original, OcrFrameTransform.Identity);
                var plot = new OcrRectangle(fixture.Request.Plot.Left, fixture.Request.Plot.Top,
                    fixture.Request.Plot.Right - fixture.Request.Plot.Left,
                    fixture.Request.Plot.Bottom - fixture.Request.Plot.Top);
                OcrDetectedRegion[] candidates = new ConnectedComponentTextRegionDetector()
                    .DetectAsync(image, CancellationToken.None).AsTask().GetAwaiter().GetResult()
                    .Where(region => GraphTextRoleClassifier.Classify(region with
                        { Context = new OcrRegionContext(NumericExpected: true) }, "", plot).Role
                        is OcrTextRole.XTick or OcrTextRole.YTick).ToArray();
                Assert.IsTrue(candidates.Length >= 3);
                var batches = OcrCropBatcher.CreateBatches(image, candidates, new OcrCropBatcherOptions
                {
                    TargetWidth = 320,
                    TargetHeight = 48,
                    WidthMode = OcrCropWidthMode.PaddleBatchMaximumAspectRatio,
                    MaximumTargetWidth = 1024,
                    MaximumPaddingOverhead = 2,
                    PaddingPixels = 0,
                });
                maximumWidths.Add(batches.SelectMany(static batch => batch).Max(static crop => crop.Width));
            }

            Assert.AreEqual(320, maximumWidths[0]);
            Assert.IsTrue(maximumWidths[1] > maximumWidths[0]);
        });
    }

    [TestMethod]
    [DataRow("FailureCode")]
    [DataRow("Role")]
    [DataRow("SuggestedAxis")]
    [DataRow("NumericLiteral")]
    [DataRow("NormalizedText")]
    [DataRow("NumericValue")]
    [DataRow("Text")]
    [DataRow("Polygon")]
    public async Task MismatchOnlyOnWideFixtureIsNeverEligible(string field)
    {
        var benchmark = new CalibrationOcrDeviceBenchmark(device => new Fake(device,
            change: (read, row) => device.Provider == "DirectMl" && read == 1 ? field switch
            {
                "FailureCode" => row with { FailureCode = "fixture_failure" },
                "Role" => row with { Role = "Other" },
                "SuggestedAxis" => row with { SuggestedAxis = "Y" },
                "NumericLiteral" => row with { NumericLiteral = false },
                "NormalizedText" => row with { NormalizedText = "9" },
                "NumericValue" => row with { NumericValue = 9 },
                "Text" => row with { Text = "9" },
                "Polygon" => row with { Polygon = [new(20, 20), new(21, 20), new(21, 21)] },
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            } : row));

        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([Gpu], CancellationToken.None);

        Assert.IsNull(result.Measurements[0].FailureCode);
        Assert.AreEqual("CALIBRATION_OCR_BENCHMARK_OUTPUT_MISMATCH", result.Measurements[1].FailureCode);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, result.SelectedDevice);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task ChangedCpuRepeatRejectsBaselineBeforeAnyGpu(int changedRead)
    {
        var workers = new List<Fake>();
        var benchmark = new CalibrationOcrDeviceBenchmark(device =>
        {
            var worker = new Fake(device, change: (read, row) =>
                read == changedRead ? row with { Text = "9" } : row);
            workers.Add(worker);
            return worker;
        });

        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([Gpu], CancellationToken.None);

        Assert.HasCount(1, workers);
        Assert.IsTrue(workers[0].Disposed);
        Assert.HasCount(1, result.Measurements);
        Assert.AreEqual("CALIBRATION_OCR_BENCHMARK_OUTPUT_MISMATCH", result.Measurements[0].FailureCode);
        Assert.IsFalse(result.Measurements[0].OutputMatchesCpu);
    }

    [TestMethod]
    public async Task ConfidenceDifferencesDoNotChangeOutputParity()
    {
        var benchmark = new CalibrationOcrDeviceBenchmark(device => new Fake(device,
            change: (_, row) => device.Provider == "DirectMl" ? row with { Confidence = .6 } : row));

        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([Gpu], CancellationToken.None);

        Assert.IsTrue(result.Measurements.All(static measurement => measurement.OutputMatchesCpu));
    }

    [TestMethod]
    public async Task MismatchedGpuOutputIsNeverEligible()
    {
        var benchmark = new CalibrationOcrDeviceBenchmark(d => new Fake(d, mismatch: d.Provider == "DirectMl"));
        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([Gpu], CancellationToken.None);
        Assert.AreEqual(CalibrationOcrDeviceSelection.Cpu, result.SelectedDevice);
        Assert.IsNull(result.Measurements[0].FailureCode);
        Assert.AreEqual("CALIBRATION_OCR_BENCHMARK_OUTPUT_MISMATCH", result.Measurements[1].FailureCode);
        Assert.IsFalse(result.Measurements[1].OutputMatchesCpu);
    }

    [TestMethod]
    public async Task DeviceDeadlineCancelsAndDisposesWorker()
    {
        Fake? worker = null;
        var benchmark = new CalibrationOcrDeviceBenchmark(d => worker = new Fake(d, block: true),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
        CalibrationOcrDeviceBenchmarkResult result = await benchmark.RunAsync([], CancellationToken.None);
        Assert.AreEqual(CalibrationOcrReasonCodes.Timeout, result.Measurements[0].FailureCode);
        Assert.IsTrue(worker!.Disposed);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndDisposesWorker()
    {
        Fake? worker = null;
        var benchmark = new CalibrationOcrDeviceBenchmark(d => worker = new Fake(d, block: true));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await benchmark.RunAsync([], cancellation.Token));
        Assert.IsTrue(worker!.Disposed);
    }

    private sealed class Fake(CalibrationOcrDeviceSelection device, bool mismatch = false, bool block = false,
        Func<int, CalibrationOcrProposal, CalibrationOcrProposal>? change = null) : ICalibrationOcrSuggestionService
    {
        private int _reads;
        public bool Disposed { get; private set; }
        public List<(CalibrationOcrRequest Request, string ImageHash)> Requests { get; } = [];
        public async Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> image,
            CancellationToken cancellationToken)
        {
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Requests.Add((request, Convert.ToHexString(SHA256.HashData(image.Span)).ToLowerInvariant()));
            CalibrationOcrResponse response = CalibrationOcrClientTests.Success(request);
            CalibrationOcrProposal original = response.Proposals[0];
            string text = request.Width == 320 ? "1" : "123";
            CalibrationOcrProposal[] rows = Enumerable.Range(0, 3).Select(i => original with
                { RegionId = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Text = mismatch && i == 0 ? "9" : text,
                    NumericValue = request.Width == 320 ? 1 : 123, NormalizedText = text }).ToArray();
            if (change is not null) rows[0] = change(_reads, rows[0]);
            return response with { Proposals = rows, Timings = response.Timings! with { ReusedSession = _reads++ > 0 }, Runtime = response.Runtime! with
                { Provider = device.Provider, RequestedProvider = device.Provider, AdapterIndex = device.AdapterIndex,
                    AdapterLuid = device.AdapterLuid, AdapterName = device.AdapterName } };
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
