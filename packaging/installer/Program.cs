// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GraphReader.Installer;

internal static class Program
{
    private const string PayloadResourceName = "GraphAutoReader.payload.zip";
    private const string ProductDirectoryName = "GraphAutoReader";
    private const string ProductDisplayName = "Graph Auto Reader";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GraphAutoReader";
    private const int VerificationUsageExitCode = 2;
    private const int VerificationFailureExitCode = 3;
    private const int MaximumPayloadEntries = 100_000;
    private const long MaximumPayloadBytes = 4L * 1024 * 1024 * 1024;

    private static readonly string InstallRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        ProductDirectoryName);

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductDirectoryName);

    private static readonly string StartMenuShortcut = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs",
        ProductDisplayName + ".lnk");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--verify-payload", StringComparison.OrdinalIgnoreCase))
        {
            return VerifyPayload(args);
        }

        bool quiet = DeferredUninstallExecution.IsQuiet(args);
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "--finalize-uninstall", StringComparison.Ordinal))
            {
                string? status = null;
                string preserved = args.Length > 2 ? args[2] : string.Empty;
                DeferredUninstallResult result = DeferredUninstallExecution.Run(() =>
                {
                    if (args.Length != 6 || !int.TryParse(args[1], out int parentId) || parentId <= 0 || parentId == Environment.ProcessId ||
                        args[5] is not ("quiet" or "interactive"))
                        throw new ArgumentException("Invalid uninstall finalizer arguments.");
                    status = ValidateUninstallStatusPath(args[4]);
                    try { using Process parent = Process.GetProcessById(parentId); if (!parent.WaitForExit(120_000)) throw new TimeoutException("The previous setup process did not exit."); }
                    catch (ArgumentException) { /* Parent already exited. */ }
                    using (InstallCoordination.Acquire(InstallRoot))
                    {
                        InstallCoordination.RequireIdentity(InstallRoot, args[3]);
                        FinalizeUninstall(args[2]);
                    }
                }, () => UninstallStatus.Write(status!, "succeeded", InstallRoot, preserved),
                error => { if (status is not null) UninstallStatus.Write(status, "failed", InstallRoot, preserved, error.Message); });
                if (!result.Completed || result.StatusError is not null)
                {
                    string description = result.Completed ? "Uninstall completed, but its status could not be updated." : "Uninstall did not complete.";
                    string log = Path.Combine(DataRoot, "installer-uninstall.log");
                    try { Directory.CreateDirectory(DataRoot); File.AppendAllText(Path.Combine(DataRoot, "installer-uninstall.log"),
                        $"{DateTime.UtcNow:O} {description} Original path: {InstallRoot}; intended path: {preserved}; operation error: {result.OperationError}; status error: {result.StatusError}{Environment.NewLine}", new UTF8Encoding(false)); }
                    catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
                    { Console.Error.WriteLine($"Could not write uninstall log: {logError.Message}"); }
                    ShowMessage($"{description} Check the uninstall log:{Environment.NewLine}{log}", quiet, true);
                }
                return result.Completed ? 0 : 1;
            }
            using IDisposable operation = InstallCoordination.Acquire(InstallRoot);
            if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                UninstallResult result = Uninstall(quiet);
                ShowMessage(
                    result.StatusPath is not null ?
                    $"Uninstall is pending. Check its status at:{Environment.NewLine}{result.StatusPath}{Environment.NewLine}Files will be preserved at:{Environment.NewLine}{result.Preserved}" :
                    result.Preserved is null ? "Graph Auto Reader was removed. User data was preserved." :
                    $"Graph Auto Reader was removed. Installation files were preserved at:{Environment.NewLine}{result.Preserved}",
                    quiet,
                    false);
                return result.StatusPath is null ? 0 : UninstallStatus.PendingExitCode;
            }

            bool allowDowngrade = args.Contains("--allow-downgrade", StringComparer.OrdinalIgnoreCase);
            string? backup = Install(allowDowngrade);
            string message = "Graph Auto Reader was installed for the current user.";
            if (backup is not null)
            {
                message += $"{Environment.NewLine}Previous installation retained at:{Environment.NewLine}{backup}";
                Console.Out.WriteLine($"Previous installation retained at: {backup}");
            }
            ShowMessage(message, quiet, false);
            return 0;
        }
        catch (Exception exception)
        {
            string logPath = Path.Combine(Path.GetTempPath(), "GraphAutoReader-Installer.log");
            File.AppendAllText(
                logPath,
                $"{DateTime.UtcNow:O} {exception}{Environment.NewLine}",
                new UTF8Encoding(false));
            ShowMessage($"Setup could not complete. Details were written to:{Environment.NewLine}{logPath}", quiet, true);
            return 1;
        }
    }

    private static int VerifyPayload(string[] args)
    {
        if (args.Length != 2 || !IsSha256(args[1]))
        {
            Console.Error.WriteLine(
                "Payload verification ERROR: expected --verify-payload followed by one 64-character SHA-256 digest.");
            return VerificationUsageExitCode;
        }

        string expectedDigest = args[1].ToLowerInvariant();
        try
        {
            using Stream payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
                ?? throw new InvalidDataException("the embedded payload resource is missing");
            using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, false);
            string actualDigest = ComputePayloadDigest(archive);
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Payload verification FAIL: expected {expectedDigest}, computed {actualDigest}.");
                return VerificationFailureExitCode;
            }

            Console.Out.WriteLine($"Payload verification PASS: {actualDigest}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Payload verification FAIL: {exception.Message}");
            return VerificationFailureExitCode;
        }
    }

    private static string? Install(bool allowDowngrade)
    {
        ValidateInstallRoot();
        string stagingRoot = Path.Combine(Path.GetTempPath(), "GraphAutoReader-Install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);

        try
        {
            ExtractPayload(stagingRoot);
            string payloadExecutable = Path.Combine(stagingRoot, "GraphReader.App.exe");
            string payloadMetadata = Path.Combine(stagingRoot, "build-metadata.json");
            if (!File.Exists(payloadExecutable) || !File.Exists(payloadMetadata))
            {
                throw new InvalidDataException("The embedded payload is incomplete.");
            }

            Version payloadVersion = ReadVersion(payloadMetadata);
            string installedMetadata = Path.Combine(InstallRoot, "build-metadata.json");
            if (File.Exists(installedMetadata))
            {
                Version installedVersion = ReadVersion(installedMetadata);
                if (installedVersion > payloadVersion && !allowDowngrade)
                {
                    throw new InvalidOperationException(
                        $"Downgrade from {installedVersion} to {payloadVersion} is blocked. " +
                        "Run setup with --allow-downgrade only after confirming project compatibility.");
                }
            }

            string runningInstaller = Environment.ProcessPath
                ?? throw new InvalidOperationException("The installer executable path is unavailable.");
            if (runningInstaller.StartsWith(InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Repair and upgrade must be run from the downloaded setup executable, not Uninstall.exe.");
            }

            InstallerMetadataSnapshot metadata = InstallerMetadataSnapshot.Capture(UninstallRegistryPath, StartMenuShortcut);
            string? retained = InstallDirectoryTransaction.Execute(
                InstallRoot,
                replacement =>
                {
                    CopyDirectory(stagingRoot, replacement);
                    File.Copy(runningInstaller, Path.Combine(replacement, "Uninstall.exe"), true);
                    InstallCoordination.CreateFreshIdentity(replacement);
                    WriteOwnershipInventory(replacement);
                },
                () =>
                {
                    CreateStartMenuShortcut(Path.Combine(InstallRoot, "GraphReader.App.exe"));
                    RegisterUninstaller(payloadVersion);
                }, metadata.Restore);
            if (retained is not null)
            {
                try
                {
                    Directory.CreateDirectory(DataRoot);
                    File.AppendAllText(Path.Combine(DataRoot, "installer-retained.log"),
                        $"{DateTime.UtcNow:O} Previous installation retained at: {retained}{Environment.NewLine}",
                        new UTF8Encoding(false));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not write retained-installation log: {error.Message}");
                }
            }
            return retained;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
        }
    }

    private static UninstallResult Uninstall(bool quiet)
    {
        ValidateInstallRoot();
        if (!Directory.Exists(InstallRoot))
        {
            InstallerMetadataSnapshot stale = InstallerMetadataSnapshot.Capture(UninstallRegistryPath, StartMenuShortcut);
            try { stale.RemoveRegistration(); }
            catch { stale.Restore(); throw; }
            return new UninstallResult(null, null);
        }

        string preserved = InstallRoot + ".uninstalled-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(preserved) || File.Exists(preserved))
            throw new IOException("The uninstall preservation path is unavailable.");
        Console.Out.WriteLine($"Installation files will be retained at: {preserved}");
        string currentProcessPath = Environment.ProcessPath ?? string.Empty;
        if (!currentProcessPath.StartsWith(InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            FinalizeUninstall(preserved);
            return new UninstallResult(preserved, null);
        }

        string identity = InstallCoordination.ReadOrCreateLegacyIdentity(InstallRoot);
        string statusPath = StartDeferredUninstall(preserved, currentProcessPath, identity, quiet);
        Console.Out.WriteLine($"Uninstall pending; status: {statusPath}");
        return new UninstallResult(preserved, statusPath);
    }

    private static void FinalizeUninstall(string preserved)
    {
        ValidateInstallRoot();
        InstallerMetadataSnapshot metadata = InstallerMetadataSnapshot.Capture(UninstallRegistryPath, StartMenuShortcut);
        InstallDirectoryTransaction.Uninstall(InstallRoot, preserved, metadata.RemoveRegistration, metadata.Restore);
    }

    private static void WriteOwnershipInventory(string replacement)
    {
        string inventoryPath = Path.Combine(replacement, ".graphreader-owned-files.json");
        if (File.Exists(inventoryPath) || Directory.Exists(inventoryPath))
            throw new InvalidDataException("The payload uses the reserved ownership inventory path.");
        var entries = Directory.EnumerateFiles(replacement, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                path = Path.GetRelativePath(replacement, path).Replace('\\', '/'),
                sha256 = HashFile(path),
            })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllText(inventoryPath, JsonSerializer.Serialize(new
        {
            schema = "graphreader.installed-payload-inventory.v1", files = entries,
        }), new UTF8Encoding(false));
    }

    private static string HashFile(string path)
    {
        using Stream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void ExtractPayload(string destination)
    {
        using Stream payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidDataException("The installer payload resource is missing.");
        using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, false);
        string destinationPrefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int entryCount = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            entryCount++;
            if (entryCount > MaximumPayloadEntries)
            {
                throw new InvalidDataException("The installer payload contains too many entries.");
            }

            string normalizedPath = ValidatePayloadEntry(entry, uniquePaths);
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumPayloadBytes)
            {
                throw new InvalidDataException("The installer payload exceeds the allowed uncompressed size.");
            }

            string target = Path.GetFullPath(Path.Combine(
                destination,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe path in installer payload: {entry.FullName}");
            }

            if (IsDirectoryEntry(entry))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)
                ?? throw new InvalidDataException($"Invalid payload path: {entry.FullName}"));
            entry.ExtractToFile(target, true);
        }
    }

    private static string ComputePayloadDigest(ZipArchive archive)
    {
        var records = new List<PayloadFileRecord>();
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int entryCount = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            entryCount++;
            if (entryCount > MaximumPayloadEntries)
            {
                throw new InvalidDataException("the payload contains too many entries");
            }

            string normalizedPath = ValidatePayloadEntry(entry, uniquePaths);
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumPayloadBytes)
            {
                throw new InvalidDataException("the payload exceeds the allowed uncompressed size");
            }

            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            using Stream stream = entry.Open();
            string fileDigest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            records.Add(new PayloadFileRecord(normalizedPath, fileDigest));
        }

        records.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        string manifest = string.Join('\n', records.Select(static record => $"{record.Sha256}  {record.Path}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
    }

    private static string ValidatePayloadEntry(ZipArchiveEntry entry, HashSet<string> uniquePaths)
    {
        string normalized = entry.FullName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/'))
        {
            throw new InvalidDataException($"unsafe or empty payload path '{entry.FullName}'");
        }

        string canonical = normalized.TrimEnd('/');
        string[] segments = canonical.Split('/');
        if (segments.Length == 0 || segments.Any(static segment =>
                string.IsNullOrEmpty(segment) ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsReservedWindowsName(segment)))
        {
            throw new InvalidDataException($"unsafe payload path '{entry.FullName}'");
        }

        if (!uniquePaths.Add(canonical))
        {
            throw new InvalidDataException($"duplicate payload path '{entry.FullName}'");
        }

        return canonical;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            name[3] is >= '1' and <= '9';
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static Version ReadVersion(string metadataPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        string value = document.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidDataException($"Version is missing from {metadataPath}.");
        return Version.Parse(value);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException($"Invalid destination path: {target}"));
            File.Copy(file, target, true);
        }
    }

    private static void RegisterUninstaller(Version version)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, true)
            ?? throw new InvalidOperationException("The per-user uninstall registration could not be created.");
        string uninstallPath = Path.Combine(InstallRoot, "Uninstall.exe");
        key.SetValue("DisplayName", ProductDisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", version.ToString(3), RegistryValueKind.String);
        key.SetValue("Publisher", "Sungwoo Kang", RegistryValueKind.String);
        key.SetValue("InstallLocation", InstallRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", Path.Combine(InstallRoot, "GraphReader.App.exe"), RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{uninstallPath}\" --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{uninstallPath}\" --uninstall --quiet", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
    }

    private static void CreateStartMenuShortcut(string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StartMenuShortcut)
            ?? throw new InvalidOperationException("The Start Menu path is invalid."));
        string script =
            "$shell = New-Object -ComObject WScript.Shell; " +
            $"$shortcut = $shell.CreateShortcut('{EscapePowerShellLiteral(StartMenuShortcut)}'); " +
            $"$shortcut.TargetPath = '{EscapePowerShellLiteral(targetPath)}'; " +
            $"$shortcut.WorkingDirectory = '{EscapePowerShellLiteral(InstallRoot)}'; " +
            "$shortcut.Save()";
        RunPowerShell(script, true);
    }

    private static string StartDeferredUninstall(string preserved, string currentProcessPath, string identity, bool quiet)
    {
        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            ProductDirectoryName);
        if (!string.Equals(Path.GetFullPath(InstallRoot), Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected installation directory.");
        }

        string expectedPrefix = Path.GetFullPath(InstallRoot) + ".uninstalled-";
        if (!Path.GetFullPath(preserved).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(preserved), Path.GetDirectoryName(InstallRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing an unexpected uninstall preservation path.");

        Directory.CreateDirectory(DataRoot);
        string status = Path.Combine(DataRoot, "uninstall-status-" + Guid.NewGuid().ToString("N") + ".json");
        UninstallStatus.Write(status, "pending", InstallRoot, preserved);
        string helper = Path.Combine(DataRoot, "Uninstall-helper-" + Guid.NewGuid().ToString("N") + ".exe");
        DeferredUninstallExecution.Schedule(
            () => File.WriteAllText(Path.Combine(DataRoot, "uninstall-latest.json"),
                JsonSerializer.Serialize(new { schema = "graphreader.uninstall-latest.v1", status_path = status }), new UTF8Encoding(false)),
            () =>
            {
            File.Copy(currentProcessPath, helper, false);
            var start = new ProcessStartInfo { FileName = helper, WorkingDirectory = DataRoot,
                UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--finalize-uninstall");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(preserved);
            start.ArgumentList.Add(identity);
            start.ArgumentList.Add(status);
            start.ArgumentList.Add(quiet ? "quiet" : "interactive");
            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("The uninstall finalizer could not be started.");
            }, error => UninstallStatus.Write(status, "failed", InstallRoot, preserved, error.Message));
        return status;
    }

    private static string ValidateUninstallStatusPath(string value)
    {
        string path = Path.GetFullPath(value);
        string filename = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(DataRoot), StringComparison.OrdinalIgnoreCase) ||
            !filename.StartsWith("uninstall-status-", StringComparison.Ordinal) || !filename.EndsWith(".json", StringComparison.Ordinal) ||
            !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Invalid uninstall status path.");
        return path;
    }

    private static void RunPowerShell(string script, bool waitForExit)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell could not be started.");
        if (!waitForExit)
        {
            return;
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows PowerShell failed with exit code {process.ExitCode}.");
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void ValidateInstallRoot()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string expected = Path.Combine(localApplicationData, "Programs", ProductDirectoryName);
        if (string.IsNullOrWhiteSpace(localApplicationData) ||
            !string.Equals(Path.GetFullPath(InstallRoot), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The per-user installation root is unavailable or unsafe.");
        }

        if (InstallRoot.StartsWith(DataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Application binaries and mutable user data must remain isolated.");
        }
        if (Directory.Exists(InstallRoot) && (File.GetAttributes(InstallRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The installation root cannot be a reparse point.");
    }

    private static void ShowMessage(string message, bool quiet, bool error)
    {
        if (quiet)
        {
            return;
        }

        uint icon = error ? 0x10u : 0x40u;
        _ = MessageBox(IntPtr.Zero, message, ProductDisplayName + " Setup", icon);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    private sealed record PayloadFileRecord(string Path, string Sha256);
    private sealed record UninstallResult(string? Preserved, string? StatusPath);
}
