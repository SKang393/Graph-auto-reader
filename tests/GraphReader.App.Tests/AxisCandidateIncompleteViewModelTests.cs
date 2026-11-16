// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Axis;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class AxisCandidateIncompleteViewModelTests
{
    [TestMethod]
    public void IncompleteSearchWarningIsVisibleBeforeSelectionAndSurvivesRefitAndCultureChange()
    {
        string directory = Path.Combine(Path.GetTempPath(), "axis-candidate-incomplete-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() => RunOnSta(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RunOnSta(string directory)
    {
        string path = Path.Combine(directory, "owned-test.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 120, 96, 96, PixelFormats.Gray8,
            null, Enumerable.Repeat((byte)255, 160 * 120).ToArray(), 160)));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);

        var provider = new IncompleteCandidateProvider();
        var ocr = new CalibrationOcrStub();
        using var workspace = new ManualPreviewWorkspaceService(axisProposalService: provider,
            calibrationOcrSuggestionService: ocr);
        WorkspaceTabViewModel tab = Pump(workspace.ImportImagesAsync([path], CancellationToken.None)).Single();
        var localization = new TestLocalizationService();
        using var vm = new MainWindowViewModel(workspace, localization);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } propertyName) changed.Add(propertyName);
        };

        Pump(vm.FindAxisCandidatesAsync(CancellationToken.None));

        Assert.HasCount(2, vm.AxisCandidates);
        Assert.IsNull(vm.SelectedAxisCandidate);
        Assert.IsFalse(vm.HasAxisSuggestion);
        Assert.IsFalse(vm.UseAxisCandidateCommand.CanExecute(null));
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsTrue(vm.AxisCandidatesStatusText.Contains("en-US:" + LocalizationKeys.ManualAxisCandidatesIncompleteWarning,
            StringComparison.Ordinal));
        Assert.IsTrue(vm.AxisCandidatesStatusText.Contains("en-US:" + LocalizationKeys.ManualAxisCandidatesInstructions,
            StringComparison.Ordinal));

        string englishStatus = vm.AxisCandidatesStatusText;
        changed.Clear();
        localization.ApplyCulture(CultureInfo.GetCultureInfo("fr-FR"));
        Assert.AreNotEqual(englishStatus, vm.AxisCandidatesStatusText);
        Assert.IsTrue(vm.AxisCandidatesStatusText.Contains("fr-FR:" + LocalizationKeys.ManualAxisCandidatesIncompleteWarning,
            StringComparison.Ordinal));
        Assert.IsTrue(changed.Contains(nameof(vm.AxisCandidatesStatusText)));

        vm.SelectedAxisCandidate = vm.AxisCandidates[1];
        Assert.IsTrue(vm.UseAxisCandidateCommand.CanExecute(null), "Selecting a row enables the explicit Use action.");
        Assert.IsFalse(vm.HasAxisSuggestion, "Selecting a row only previews its choice; it does not create a draft.");
        Assert.IsNull(tab.Calibration);
        vm.UseAxisCandidateCommand.Execute(null);

        Assert.IsTrue(vm.HasAxisSuggestion);
        Assert.IsFalse(vm.HasAxisCandidates);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.AreEqual(string.Empty, vm.SuggestedYMaximum);
        Assert.AreEqual(string.Empty, vm.SuggestedFirstSession);
        Assert.AreEqual(string.Empty, vm.SuggestedFinalSession);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsNull(tab.Calibration);
        Assert.IsTrue(vm.AxisGeometryReviewWarningText.Contains("fr-FR:" + LocalizationKeys.ManualAxisCandidatesIncompleteWarning,
            StringComparison.Ordinal));

        string frenchWarning = vm.AxisGeometryReviewWarningText;
        changed.Clear();
        Pump(vm.ReadCalibrationLabelsAsync(CancellationToken.None));
        Assert.AreEqual(1, ocr.CallCount);
        Assert.IsTrue(vm.HasCalibrationLabelSuggestion);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsNull(tab.Calibration);

        vm.RefitSelectedCalibrationLabels();
        Assert.IsTrue(vm.AxisGeometryReviewWarningText.Contains("fr-FR:" + LocalizationKeys.ManualAxisCandidatesIncompleteWarning,
            StringComparison.Ordinal));
        Assert.AreEqual(frenchWarning, vm.AxisGeometryReviewWarningText);
        Assert.IsFalse(vm.AxisSuggestionReviewed);
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsNull(tab.Calibration);

        changed.Clear();
        localization.ApplyCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.AreNotEqual(frenchWarning, vm.AxisGeometryReviewWarningText);
        Assert.IsTrue(vm.AxisGeometryReviewWarningText.Contains("en-US:" + LocalizationKeys.ManualAxisCandidatesIncompleteWarning,
            StringComparison.Ordinal));
        Assert.IsTrue(changed.Contains(nameof(vm.AxisGeometryReviewWarningText)));
        Assert.IsFalse(vm.AxisSuggestionReviewed, "Changing language must not acknowledge the incomplete search.");

        vm.SuggestedYMaximum = "10";
        vm.SuggestedFirstSession = "1";
        vm.SuggestedFinalSession = "24";
        Assert.IsFalse(vm.ApplyAxisSuggestionCommand.CanExecute(null), "Valid entered values alone are not review acknowledgment.");
        vm.AxisSuggestionReviewed = true;
        Assert.IsTrue(vm.ApplyAxisSuggestionCommand.CanExecute(null));
        Assert.IsNull(tab.Calibration, "A command becoming enabled must not apply calibration by itself.");
    }

    private static CalibrationOcrProposal[] Proposals() =>
    [
        Proposal("x-first", "1", 1, "XTick", "X", 20, 112),
        Proposal("x-mid", "12.5", 12.5, "XTick", "X", 80, 112),
        Proposal("x-final", "24", 24, "XTick", "X", 140, 112),
        Proposal("y-max", "10", 10, "YTick", "Y", 20, 2),
        Proposal("y-mid", "5", 5, "YTick", "Y", 60, 2),
        Proposal("y-zero", "0", 0, "YTick", "Y", 100, 2),
    ];

    private static CalibrationOcrProposal Proposal(string id, string text, double value, string role,
        string axis, int tick, int cross) => role == "XTick"
        ? new(id, [new(tick - 3, cross), new(tick + 3, cross), new(tick + 3, cross + 4)], text, .95,
            value, value.ToString("R", CultureInfo.InvariantCulture), true, role, axis, null)
        : new(id, [new(cross, tick - 2), new(cross + 5, tick - 2), new(cross + 5, tick + 2)], text, .95,
            value, value.ToString("R", CultureInfo.InvariantCulture), true, role, axis, null);

    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class IncompleteCandidateProvider : IAxisProposalService, IAxisCandidateReviewService
    {
        private static readonly IReadOnlyList<AxisReviewCandidate> CandidateChoices = Array.AsReadOnly(new[]
        {
            new AxisReviewCandidate(0, new RustAxisGeometry(10, 100, 150, 10, 0)),
            new AxisReviewCandidate(1, new RustAxisGeometry(12, 100, 148, 10, 0)),
        });

        public bool CanReviewCandidates => true;
        public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken) =>
            Task.FromResult(new RustAxisProposalResult(null, "AMBIGUOUS_AXIS"));
        public Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] originalImage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AxisCandidateReviewResult(CandidateChoices, null)
                { Provider = "opencv-review", EnumerationCompleted = false });
        }
    }

    private sealed class CalibrationOcrStub : ICalibrationOcrSuggestionService
    {
        public int CallCount { get; private set; }

        public Task<CalibrationOcrResponse> SuggestAsync(CalibrationOcrRequest request,
            ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            CalibrationOcrResponse template = CalibrationOcrClientTests.Success(request);
            return Task.FromResult(template with
            {
                Proposals = Proposals(),
                XSequence = new(false, 3, 3, 0, []),
                YSequence = new(false, 3, 3, 0, []),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestLocalizationService : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<CultureInfo> AvailableCultures { get; } =
            [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("fr-FR")];
        public event EventHandler<LocalizationChangedEventArgs>? CultureChanged;

        public string GetString(string key) => $"{CurrentCulture.Name}:{key}";

        public void ApplyCulture(CultureInfo culture)
        {
            CurrentCulture = culture;
            CultureChanged?.Invoke(this, new(culture));
        }
    }
}
