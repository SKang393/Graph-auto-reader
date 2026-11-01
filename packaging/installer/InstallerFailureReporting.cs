// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text;

namespace GraphReader.Installer;

internal static class InstallerFailureReporting
{
    internal static string Describe(Exception failure, Func<string> temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(temporaryDirectory);
        try
        {
            string logPath = Path.Combine(temporaryDirectory(), "GraphAutoReader-Installer.log");
            File.AppendAllText(logPath, $"{DateTime.UtcNow:O} {failure}{Environment.NewLine}", new UTF8Encoding(false));
            return $"Setup could not complete. Details were written to:{Environment.NewLine}{logPath}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return $"Setup could not complete: {failure.Message}{Environment.NewLine}" +
                "The diagnostic log could not be written.";
        }
    }
}
