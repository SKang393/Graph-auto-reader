// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GraphReader.App;
using GraphReader.App.Localization;
using GraphReader.App.ViewModels;
using GraphReader.App.Services;
using GraphReader.Domain;
using GraphReader.Export;

namespace GraphReader.ReviewedPhaseExport;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var exitCode = 1;
        dispatcher.InvokeAsync(async () =>
        {
            try { exitCode = await RunAsync(args); }
            catch (Exception e) { Console.Error.WriteLine(e.Message); }
            finally { dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        bool acknowledge = args.LastOrDefault() == "--acknowledge-warnings";
        if (acknowledge) args = args[..^1];
        bool list = args is ["list", _];
        if (!list && args is not ["export", _, _, _, _, _, _, _, _, "--confirm-ticks"])
        {
            Console.Error.WriteLine("list PROJECT\nexport PROJECT PANEL_ID PHASE_ID LEFT_PIXEL LEFT_VALUE RIGHT_PIXEL RIGHT_VALUE NEW_DIRECTORY --confirm-ticks [--acknowledge-warnings]");
            return 2;
        }

        string project = Path.GetFullPath(args[1]);
        // Keep the same saved revision open throughout inspection, export and receipt creation.
        using var source = new FileStream(project, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source));
        PhaseTicks? ticks = list ? null : new(Guid.Parse(args[3]), Number(args[4]), Number(args[5]), Number(args[6]), Number(args[7]));
        using var workspace = new ManualPreviewWorkspaceService(exportService: ticks is null ? null : new PhaseExport(ticks));
        var tabs = await workspace.OpenProjectAsync(project, CancellationToken.None);
        if (list)
        {
            foreach (var panel in workspace.CurrentProject.Panels)
                foreach (var phase in panel.Phases.OrderBy(p => p.Order))
                    Console.WriteLine($"{panel.PanelId.Value:D}\t{panel.DisplayName}\t{phase.PhaseId.Value:D}\t{phase.Code}\t{phase.LabelText}");
            return 0;
        }

        Guid panelId = Guid.Parse(args[2]);
        var saved = workspace.CurrentProject.Panels.SingleOrDefault(p => p.PanelId.Value == panelId)
            ?? throw new InvalidDataException("Unknown panel ID in this project.");
        var tab = tabs.Single(t => Guid.Parse(t.PanelId!) == panelId);
        if (saved.Calibration is not { UserConfirmed: true, Status: CalibrationStatus.Valid } calibration ||
            tab.Calibration is not { XTransform: not null, YTransform: not null, UsesPrintedXCoordinates: false })
            throw new InvalidDataException("This restart workflow requires a confirmed axis-aligned Y calibration on an observation-order panel.");

        Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
        using var review = new MainWindowViewModel(workspace, localizationService: new LocalizationService(new ResourceDictionary()));
        review.SelectedTab = tab;
        review.OpenExportPreviewCommand.Execute(null);
        var summary = review.ExportSummary!;
        foreach (var issue in summary.BlockingIssues.Concat(summary.WarningIssues))
            Console.Error.WriteLine(issue.Title);
        summary.WarningsAcknowledged = acknowledge;
        if (!summary.CanExport)
            throw new InvalidDataException("Resolve export blockers or review the warnings and supply --acknowledge-warnings.");

        var selected = saved.Points.Where(p => p.PhaseId?.Value == ticks!.PhaseId).ToDictionary(p => p.PointId.Value);
        string destination = Path.GetFullPath(args[8]);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Choose a new output directory.");
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose an output directory below an existing parent.");
        string staging = Path.Combine(parent, $".phase-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var result = await workspace.ExportAsync(tab.TabId, staging, CancellationToken.None);
            if (!result.Succeeded || result.Preview.FinalExportBlocked)
                throw new InvalidDataException(string.Join(" | ", result.Failures.Select(f => f.Code + " " + f.TechnicalMessage)));
            var rows = result.AuditArtifacts.Where(a => a.Format == ExportAuditArtifactFormat.Json).SelectMany(a => a.Rows).ToArray();
            if (!selected.Keys.ToHashSet().SetEquals(rows.Select(r => r.PointId)) ||
                rows.Any(r => r.PhaseId != ticks!.PhaseId || r.SourceSeriesId != selected[r.PointId].SeriesId?.Value ||
                    r.OriginalPixel.X != selected[r.PointId].OriginalPixel.X || r.OriginalPixel.Y != selected[r.PointId].OriginalPixel.Y ||
                    r.YValue != selected[r.PointId].GraphY || selected[r.PointId].ReviewStatus != ReviewStatus.Accepted))
                throw new InvalidDataException("The exported rows or coordinates disagree with the selected saved points.");
            var receipt = new { result.RunId, result.ProjectId, result.PanelId, ProjectSha256 = hash,
                CalibrationId = calibration.CalibrationId.Value, ConfirmedPhaseTicks = ticks, AcknowledgedWarnings = summary.AcknowledgedWarningIssues.Select(i => i.IssueId), PixelSpace = "original_source",
                XSource = "estimated_from_reviewer_supplied_ticks", DistinctPlacedPoints = selected.Count };
            await File.WriteAllTextAsync(Path.Combine(staging, "phase-scope.json"), JsonSerializer.Serialize(receipt));
            Directory.Move(staging, destination);
            Console.WriteLine($"Exported {selected.Count} distinct reviewed points to {destination}.");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return 0;
    }

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
