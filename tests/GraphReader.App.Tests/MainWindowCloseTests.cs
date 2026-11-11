// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class MainWindowCloseTests
{
    [TestMethod]
    public void CleanWindowClosesWithoutPrompt()
    {
        StaTestHost.Run(() =>
        {
            var dialog = new CloseDialog();
            using var viewModel = new MainWindowViewModel(new UnavailableWorkspaceService(), localizationService: null, dialogService: dialog);
            viewModel.Tabs.Add(Tab("clean", false));
            Assert.IsTrue(viewModel.CanCloseWindow());
            Assert.AreEqual(0, dialog.Prompts);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DirtyNonselectedTabRequiresExplicitDiscard(bool confirm)
    {
        StaTestHost.Run(() =>
        {
            var dialog = new CloseDialog { Confirm = confirm };
            using var viewModel = new MainWindowViewModel(new UnavailableWorkspaceService(), localizationService: null, dialogService: dialog);
            var selected = Tab("selected", false);
            var otherProject = Tab("other-project", true);
            viewModel.Tabs.Add(selected);
            viewModel.Tabs.Add(otherProject);
            viewModel.SelectedTab = selected;
            Assert.AreEqual(confirm, viewModel.CanCloseWindow());
            Assert.AreEqual(1, dialog.Prompts);
            Assert.IsTrue(otherProject.IsDirty, "The guard must not mark unsaved edits as saved.");
        });
    }

    [TestMethod]
    public void DirtyWindowWithoutDialogStaysOpen()
    {
        StaTestHost.Run(() =>
        {
            using var viewModel = new MainWindowViewModel(new UnavailableWorkspaceService());
            viewModel.Tabs.Add(Tab("dirty", true));
            Assert.IsFalse(viewModel.CanCloseWindow());
        });
    }

    [TestMethod]
    public void BusyWindowStaysOpenWithoutPrompt()
    {
        StaTestHost.Run(() =>
        {
            var dialog = new CloseDialog { Confirm = true };
            using var viewModel = new MainWindowViewModel(new UnavailableWorkspaceService(), localizationService: null, dialogService: dialog);
            viewModel.Tabs.Add(Tab("dirty", true));
            typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.IsBusy))!.SetValue(viewModel, true);
            Assert.IsFalse(viewModel.CanCloseWindow());
            Assert.AreEqual(0, dialog.Prompts);
            Assert.AreEqual("Project.CloseBusyBlocked", viewModel.StatusMessage);
        });
    }

    private static WorkspaceTabViewModel Tab(string id, bool dirty) =>
        new(id, id, [], [], null, null, null) { IsDirty = dirty };

    private sealed class CloseDialog : IWorkspaceDialogService
    {
        public bool Confirm { get; init; }
        public int Prompts { get; private set; }
        public bool ConfirmDiscardUnsavedChanges() { Prompts++; return Confirm; }
        public IReadOnlyList<string> SelectImages() => [];
        public string? SelectProjectToOpen() => null;
        public string? SelectProjectToSave(string? currentPath) => null;
        public string? SelectExportDirectory() => null;
    }
}
