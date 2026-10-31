// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

namespace GraphReader.Installer;

/// <summary>Stages an installation beside its destination and restores its predecessor on failure.</summary>
internal static class InstallDirectoryTransaction
{
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
                CopyUnclaimedFiles(root, staging);
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

    private static void CopyUnclaimedFiles(string source, string destination)
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
                CopyUnclaimedFiles(path, target);
            }
            else if (!File.Exists(target) && !Directory.Exists(target))
            {
                File.Copy(path, target, false);
            }
        }
    }
}
