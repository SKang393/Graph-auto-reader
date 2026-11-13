// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrWorkspaceTests
{
    [TestMethod]
    public void SendsOriginalBytesAndDiscardsResultAfterCalibrationChanges() => StaTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "calibration-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "fixture.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
            using (var stream = File.Create(imagePath)) encoder.Save(stream);
            var ocr = new FakeOcr();
            using var workspace = new ManualPreviewWorkspaceService(axisProposalService: new FakeAxes(), calibrationOcrSuggestionService: ocr);
            Pump(workspace.ImportImagesAsync([imagePath], CancellationToken.None));
            var tab = workspace.CreateWorkspace().Single();
            WorkspaceAxisSuggestion suggestion = Pump(workspace.SuggestAxesAsync(tab.TabId, CancellationToken.None))!;
            Task<WorkspaceCalibrationLabelSuggestion?> pending = workspace.ReadCalibrationLabelsAsync(suggestion, CancellationToken.None);
            Assert.IsNotNull(ocr.Request);
            CollectionAssert.AreEqual(File.ReadAllBytes(imagePath), ocr.Image!);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(ocr.Image!)).ToLowerInvariant(), ocr.Request.PanelImageSha256);
            Assert.AreEqual(new CalibrationOcrPlot(8, 5, 155, 110), ocr.Request.Plot);
            workspace.Calibrate(tab.TabId, new(new(10, 100), new(10, 10), new(150, 100), 100, 20));
            ocr.Pending.SetResult(CalibrationOcrClientTests.Success(ocr.Request));
            Assert.IsNull(Pump(pending));
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private sealed class FakeAxes : IAxisProposalService
    {
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken) =>
            Task.FromResult(new RustAxisProposalResult(new(8, 110, 155, 5, .8), null));
    }
    private sealed class FakeOcr : ICalibrationOcrSuggestionService
    {
        public CalibrationOcrRequest? Request { get; private set; }
        public byte[]? Image { get; private set; }
        public TaskCompletionSource<CalibrationOcrResponse> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request, ReadOnlyMemory<byte> encodedOriginalPanel, CancellationToken cancellationToken)
        { Request = request; Image = encodedOriginalPanel.ToArray(); return Pending.Task; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}
