// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphReader.App.Localization;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class PanelCropViewModelTests
{
    [TestMethod]
    public void CropRequiresExplicitCreateAndAddsUncalibratedChildWithoutChangingParent()
    {
        WithImage(180, 140, path =>
        {
            using var workspace = new ManualPreviewWorkspaceService();
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            WorkspaceTabViewModel parent = vm.SelectedTab!;
            workspace.Calibrate(parent.TabId, new(new(10, 120), new(10, 10), new(170, 120), 10, 24, 1));
            var series = workspace.AddSeries(parent.TabId,
                new("Intervention", "●", MarkerShape.Circle, MarkerFill.Filled, SemanticRole.Intervention));
            workspace.AddPoint(parent.TabId, series.SeriesId, 80, 60);
            var original = parent.ImageSource;
            var calibration = parent.Calibration;
            GraphReader.App.Models.GraphPoint[] points = parent.Points.ToArray();

            vm.BeginPanelCropCommand.Execute(null);
            Assert.IsTrue(vm.IsPanelCropActive);
            Assert.IsFalse(vm.HasPanelCropRectangle);
            Pump(vm.HandleCanvasPointAsync(new(35, 25)));
            Assert.IsFalse(vm.HasPanelCropRectangle);
            Assert.AreEqual(1, vm.Tabs.Count, "The first corner only starts a preview selection.");

            Pump(vm.HandleCanvasPointAsync(new(105, 95)));
            Assert.IsTrue(vm.HasPanelCropRectangle);
            Assert.IsTrue(vm.CreatePanelCropCommand.CanExecute(null));
            Assert.AreEqual(1, vm.Tabs.Count, "A valid outline still awaits explicit Create panel.");

            Pump(vm.CreatePanelCropAsync(CancellationToken.None));

            Assert.AreEqual(2, vm.Tabs.Count);
            WorkspaceTabViewModel child = vm.SelectedTab!;
            Assert.AreNotSame(parent, child);
            Assert.AreEqual(71, child.PixelWidth);
            Assert.AreEqual(71, child.PixelHeight);
            Assert.IsNull(child.Calibration, "A child crop must start without inferred calibration.");
            Assert.AreEqual(0, child.Points.Count, "The parent digitization is not cloned into the crop.");
            Assert.AreSame(original, parent.ImageSource);
            Assert.AreSame(calibration, parent.Calibration);
            CollectionAssert.AreEqual(points, parent.Points.ToArray());

            vm.CloseTabCommand.Execute(parent);
            Assert.AreEqual(2, vm.Tabs.Count, "A required source tab stays visible.");
            Assert.AreEqual(LocalizationKeys.ManualPanelCropParentRequired, vm.StatusMessage);
            Assert.IsTrue(Pump(workspace.SaveProjectAsync(Path.ChangeExtension(path, ".garproj"), CancellationToken.None)).IsSuccess);
            parent.IsDirty = false;
            child.IsDirty = false;
            vm.CloseTabCommand.Execute(child);
            Assert.AreEqual(1, vm.Tabs.Count);
            vm.CloseTabCommand.Execute(parent);
            Assert.AreEqual(0, vm.Tabs.Count, "Saved children can close first, then their source.");
        });
    }

    [TestMethod]
    public void InvalidRectangleCannotBeCreatedAndEscapeClearsSelection()
    {
        WithImage(180, 140, path =>
        {
            using var workspace = new ManualPreviewWorkspaceService();
            Pump(workspace.ImportImagesAsync([path], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);

            vm.BeginPanelCropCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new(20, 20)));
            Pump(vm.HandleCanvasPointAsync(new(50, 51)));
            Assert.IsTrue(vm.HasPanelCropRectangle);
            Assert.IsFalse(vm.CreatePanelCropCommand.CanExecute(null), "The width is below the supported minimum.");
            Assert.AreEqual(1, vm.Tabs.Count);

            vm.CancelCommand.Execute(null);
            Assert.IsFalse(vm.IsPanelCropActive);
            Assert.IsFalse(vm.HasPanelCropRectangle);
            Assert.AreEqual(1, vm.Tabs.Count);
        });
    }

    [TestMethod]
    public void RegisteredChildIsShownWithoutChangingSelectionWhenUserSwitchesTabsDuringCompletion()
    {
        WithImages(180, 140, (sourcePath, otherPath) =>
        {
            using var workspace = new DelayedRegistrationWorkspace();
            Pump(workspace.ImportImagesAsync([sourcePath, otherPath], CancellationToken.None));
            using var vm = new MainWindowViewModel(workspace);
            Assert.AreEqual(2, vm.Tabs.Count);
            WorkspaceTabViewModel parent = vm.Tabs[0];
            WorkspaceTabViewModel other = vm.Tabs[1];
            vm.SelectedTab = parent;

            vm.BeginPanelCropCommand.Execute(null);
            Pump(vm.HandleCanvasPointAsync(new(30, 25)));
            Pump(vm.HandleCanvasPointAsync(new(100, 95)));
            Task create = vm.CreatePanelCropAsync(CancellationToken.None);

            WorkspaceTabViewModel registeredChild = Pump(workspace.RegisteredChild.Task);
            Assert.AreEqual(2, vm.Tabs.Count, "The child is registered in the workspace before the UI continuation resumes.");
            vm.SelectedTab = other;
            Assert.AreSame(other, vm.SelectedTab);
            workspace.AllowReturn.TrySetResult();
            Pump(create);

            Assert.AreEqual(3, vm.Tabs.Count, "The already-registered child must not be orphaned from visible tabs.");
            Assert.IsTrue(vm.Tabs.Contains(registeredChild));
            Assert.AreSame(other, vm.SelectedTab, "Finishing the crop must preserve the tab the user selected.");
        });
    }

    private static void WithImage(int width, int height, Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "panel-crop-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() =>
            {
                string path = Path.Combine(directory, "owned-test.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Gray8, null, Enumerable.Repeat((byte)255, checked(width * height)).ToArray(), width)));
                using (FileStream stream = File.Create(path)) encoder.Save(stream);
                action(path);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WithImages(int width, int height, Action<string, string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "panel-crop-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StaTestHost.Run(() =>
            {
                string first = Path.Combine(directory, "source.png");
                string second = Path.Combine(directory, "other.png");
                SaveImage(first, width, height, 255);
                SaveImage(second, width, height, 128);
                action(first, second);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SaveImage(string path, int width, int height, byte value)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Gray8, null, Enumerable.Repeat(value, checked(width * height)).ToArray(), width)));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
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

    private sealed class DelayedRegistrationWorkspace : ManualPreviewWorkspaceService, IPanelCropWorkspace
    {
        public TaskCompletionSource<WorkspaceTabViewModel> RegisteredChild { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<WorkspaceTabViewModel> IPanelCropWorkspace.CreatePanelCropAsync(
            PanelCropContext context, MarkerExemplarRectangle rectangle, CancellationToken cancellationToken)
        {
            WorkspaceTabViewModel child = await base.CreatePanelCropAsync(context, rectangle, cancellationToken);
            RegisteredChild.SetResult(child);
            await AllowReturn.Task;
            return child;
        }
    }
}
