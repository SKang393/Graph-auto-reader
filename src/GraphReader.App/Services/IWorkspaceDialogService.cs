// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Windows;
using GraphReader.App.Localization;
using Microsoft.Win32;

namespace GraphReader.App.Services;

public enum ProjectCloseAction
{
    Cancel,
    Save,
    Discard,
}

public interface IWorkspaceDialogService
{
    IReadOnlyList<string> SelectImages();

    string? SelectProjectToOpen();

    string? SelectProjectToSave(string? currentPath);

    string? SelectExportDirectory();

    ProjectCloseAction ChooseProjectCloseAction() => ProjectCloseAction.Cancel;

    bool ConfirmObservationOrder() => false;
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

    public ProjectCloseAction ChooseProjectCloseAction() => MessageBox.Show(
        Text(LocalizationKeys.ProjectSaveBeforeClosing, "Save changes to the project before closing? Choose Yes to save, No to close without saving, or Cancel to keep editing. A recovery copy may still be available."),
        Text(LocalizationKeys.AppTitle, "Graph Auto Reader"),
        MessageBoxButton.YesNoCancel,
        MessageBoxImage.Question,
        MessageBoxResult.Cancel) switch
        {
            MessageBoxResult.Yes => ProjectCloseAction.Save,
            MessageBoxResult.No => ProjectCloseAction.Discard,
            _ => ProjectCloseAction.Cancel,
        };

    public bool ConfirmObservationOrder() => MessageBox.Show(
        Text(LocalizationKeys.ConfirmObservationOrder,
            "Use row numbers as X across phases? If printed session numbers restart or skip, choose No."),
        Text(LocalizationKeys.AppTitle, "Graph Auto Reader"),
        MessageBoxButton.YesNo,
        MessageBoxImage.Question,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private string Text(string key, string fallback) =>
        _localizationService?.GetString(key) ?? fallback;
}
