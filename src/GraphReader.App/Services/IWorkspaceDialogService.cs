// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Windows;
using GraphReader.App.Localization;
using Microsoft.Win32;

namespace GraphReader.App.Services;

public interface IWorkspaceDialogService
{
    IReadOnlyList<string> SelectImages();

    string? SelectProjectToOpen();

    string? SelectProjectToSave(string? currentPath);

    string? SelectExportDirectory();

    bool ConfirmDiscardUnsavedChanges() => false;
}

public sealed class WindowsWorkspaceDialogService : IWorkspaceDialogService
{
    private readonly ILocalizationService? _localizationService;

    public WindowsWorkspaceDialogService(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
    }

    public IReadOnlyList<string> SelectImages()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Filter = $"{Text(LocalizationKeys.DialogGraphImages, "Graph images and PDFs")}|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp;*.pdf|{Text(LocalizationKeys.DialogAllFiles, "All files")}|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? SelectProjectToOpen()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = false,
            CheckFileExists = true,
            Filter = $"{Text(LocalizationKeys.DialogProjectFiles, "Graph Auto Reader project")}|*.garproj",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectProjectToSave(string? currentPath)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".garproj",
            Filter = $"{Text(LocalizationKeys.DialogProjectFiles, "Graph Auto Reader project")}|*.garproj",
            FileName = string.IsNullOrWhiteSpace(currentPath) ? "graph-project.garproj" : Path.GetFileName(currentPath),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectExportDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = Text(LocalizationKeys.DialogExportFolder, "Select export folder"),
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool ConfirmDiscardUnsavedChanges() => MessageBox.Show(
        Text(LocalizationKeys.ProjectConfirmDiscardAll, "Closing will discard unsaved changes across all open projects. Discard changes and close?"),
        Text(LocalizationKeys.AppTitle, "Graph Auto Reader"),
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private string Text(string key, string fallback) =>
        _localizationService?.GetString(key) ?? fallback;
}
