// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;

namespace GraphReader.Installer;

/// <summary>Stages an installation beside its destination and restores its predecessor on failure.</summary>
internal static class InstallDirectoryTransaction
{
    private const long MaximumOwnershipInventoryBytes = 64L * 1024 * 1024;

    internal static void PreserveForUninstall(string root, string destination)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullDestination = Path.GetFullPath(destination);
        if (!fullDestination.StartsWith(fullRoot + ".uninstalled-", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(fullRoot), Path.GetDirectoryName(fullDestination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid uninstall preservation path.");
        if (!Directory.Exists(fullRoot)) return;
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installation root cannot be a reparse point.");
        Directory.Move(fullRoot, fullDestination);
    }

    internal static void Uninstall(string root, string destination, Action unregister, Action restoreConfiguration)
    {
        if (!Directory.Exists(root)) return;
        bool moved = false;
        try
        {
            PreserveForUninstall(root, destination);
            moved = true;
            unregister();
        }
        catch (Exception uninstallError)
        {
            var rollbackErrors = new List<Exception>();
            if (moved)
            {
                try { Directory.Move(destination, root); }
                catch (Exception error) { rollbackErrors.Add(error); }
                try { restoreConfiguration(); }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException($"Uninstall failed and rollback could not complete. Inspect {root} and {destination}.",
                    new[] { uninstallError }.Concat(rollbackErrors));
            throw;
        }
    }

    internal static string? Execute(string installRoot, Action<string> populate, Action configure, Action restoreConfiguration)
    {
        string root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string suffix = Guid.NewGuid().ToString("N");
        string staging = root + ".staging-" + suffix;
        string backup = root + ".backup-" + suffix;
        string failed = root + ".failed-" + suffix;
        bool priorMoved = false;
        bool replacementMoved = false;
        bool configurationStarted = false;

        if (File.Exists(root) || Directory.Exists(staging) || Directory.Exists(backup) || Directory.Exists(failed))
        {
            throw new IOException("Installation staging paths are not available.");
        }

        try
        {
            Directory.CreateDirectory(staging);
            populate(staging);
            if (Directory.Exists(root))
            {
                Dictionary<string, string> ownedFiles = ReadOwnedPayloadFiles(root);
                CopyUnclaimedFiles(root, staging, root, ownedFiles);
                Directory.Move(root, backup);
                priorMoved = true;
            }

            Directory.Move(staging, root);
            replacementMoved = true;
            configurationStarted = true;
            configure();
        }
        catch (Exception installError)
        {
            var rollbackErrors = new List<Exception>();
            try
            {
                if (replacementMoved)
                {
                    Directory.Move(root, failed);
                }

                if (priorMoved)
                {
                    Directory.Move(backup, root);
                }
            }
            catch (Exception rollbackError)
            {
                rollbackErrors.Add(rollbackError);
            }

            if (configurationStarted)
            {
                try { restoreConfiguration(); }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    $"Installation failed and rollback could not complete. If moved, previous files remain at {backup}.",
                    new[] { installError }.Concat(rollbackErrors));
            }

            throw;
        }
        // Retain the prior directory: its contents may include files not owned by setup.
        return priorMoved ? backup : null;
    }

    private static Dictionary<string, string> ReadOwnedPayloadFiles(string installRoot)
    {
        string inventoryPath = Path.Combine(installRoot, ".graphreader-owned-files.json");
        if (!File.Exists(inventoryPath) || (File.GetAttributes(inventoryPath) & FileAttributes.ReparsePoint) != 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var inventoryFile = new FileInfo(inventoryPath);
            if (inventoryFile.Length <= 0 || inventoryFile.Length > MaximumOwnershipInventoryBytes)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(inventoryPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.String ||
                schema.GetString() != "graphreader.installed-payload-inventory.v1" ||
                !root.TryGetProperty("files", out JsonElement files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement entry in files.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("path", out JsonElement pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("sha256", out JsonElement hashElement) ||
                    hashElement.ValueKind != JsonValueKind.String)
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                string path = pathElement.GetString()!;
                string hash = hashElement.GetString()!;
                string[] segments = path.Split('/');
                if (path.Length == 0 || path.Contains('\\') || Path.IsPathRooted(path) ||
                    segments.Any(segment => segment.Length == 0 || segment is "." or "..") ||
                    hash.Length != 64 || !hash.All(Uri.IsHexDigit) || owned.ContainsKey(path))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                owned.Add(path, hash.ToLowerInvariant());
            }

            return owned;
        }
        catch (JsonException)
        {
            // An absent or invalid inventory is treated like a legacy install. Preserve its files.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void CopyUnclaimedFiles(
        string source,
        string destination,
        string installRoot,
        IReadOnlyDictionary<string, string> ownedFiles)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The previous installation contains a reparse point.");
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The previous installation contains a reparse point.");
            }
            string target = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(target);
                CopyUnclaimedFiles(path, target, installRoot, ownedFiles);
            }
            else if (!File.Exists(target) && !Directory.Exists(target))
            {
                string relativePath = Path.GetRelativePath(installRoot, path).Replace('\\', '/');
                if (ownedFiles.TryGetValue(relativePath, out string? recordedHash) &&
                    string.Equals(HashFile(path), recordedHash, StringComparison.Ordinal))
                {
                    continue;
                }

                File.Copy(path, target, false);
            }
        }
    }

    private static string HashFile(string path)
    {
        using Stream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
