// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Installer;

internal sealed record DeferredUninstallResult(bool Completed, Exception? OperationError, Exception? StatusError);

internal static class DeferredUninstallExecution
{
    internal static void Schedule(Action writePointer, Action launch, Action<Exception> markFailed)
    {
        try
        {
            writePointer();
            launch();
        }
        catch (Exception error)
        {
            try { markFailed(error); }
            catch (Exception statusError)
            {
                throw new AggregateException("Uninstall was not scheduled and its status could not be updated.", error, statusError);
            }
            throw;
        }
    }

    internal static bool IsQuiet(string[] args) =>
        args.Contains("--quiet", StringComparer.OrdinalIgnoreCase) ||
        (args.Length > 5 && args[0] == "--finalize-uninstall" && args[5] == "quiet");

    internal static DeferredUninstallResult Run(Action operation, Action writeSucceeded, Action<Exception> writeFailed)
    {
        try { operation(); }
        catch (Exception error)
        {
            try { writeFailed(error); }
            catch (Exception statusError) { return new(false, error, statusError); }
            return new(false, error, null);
        }

        try { writeSucceeded(); }
        catch (Exception statusError) { return new(true, null, statusError); }
        return new(true, null, null);
    }
}
