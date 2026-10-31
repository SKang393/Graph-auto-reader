// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text;
using System.Text.Json;

namespace GraphReader.Installer;

internal static class UninstallStatus
{
    internal const int PendingExitCode = 10;

    internal static void Write(string path, string state, string original, string preserved, string? error = null)
    {
        if (state is not ("pending" or "succeeded" or "failed")) throw new ArgumentException("Invalid uninstall status.");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(new { schema = "graphreader.uninstall-status.v1", state,
            original_path = original, preserved_path = preserved, error });
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
