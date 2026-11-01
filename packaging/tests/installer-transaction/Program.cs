// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Installer;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;

if (args.Length == 5 && args[0] == "--race-worker")
{
    File.WriteAllText(args[3], "ready");
    using (InstallCoordination.Acquire(args[1]))
    {
        try { InstallCoordination.RequireIdentity(args[1], args[2]); File.WriteAllText(args[4], "accepted"); }
        catch (IOException) { File.WriteAllText(args[4], "rejected"); }
    }
    return;
}

if (args.Length != 1 || Directory.Exists(args[0]))
{
    throw new ArgumentException("Provide a new test directory.");
}

string testRoot = Path.GetFullPath(args[0]);
Directory.CreateDirectory(testRoot);
string root = Path.Combine(testRoot, "GraphAutoReader");
Directory.CreateDirectory(root);

static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

File.WriteAllText(Path.Combine(root, "app.txt"), "old");
File.WriteAllText(Path.Combine(root, "user-file.txt"), "keep");
Directory.CreateDirectory(Path.Combine(root, "notes"));
File.WriteAllText(Path.Combine(root, "notes", "user.txt"), "nested");

void AssertOld()
{
    if (File.ReadAllText(Path.Combine(root, "app.txt")) != "old" ||
        File.ReadAllText(Path.Combine(root, "user-file.txt")) != "keep")
    {
        throw new InvalidOperationException("Previous installation was not preserved.");
    }
}

void ExpectFailure(Action action)
{
    try { action(); }
    catch (IOException) { return; }
    throw new InvalidOperationException("Expected the synthetic installation failure.");
}

string temporaryStaging = Path.Combine(testRoot, "temporary-staging");
Directory.CreateDirectory(temporaryStaging);
string lockedPath = Path.Combine(temporaryStaging, "locked.txt");
using (var locked = new FileStream(lockedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
{
    Exception? cleanupError = InstallDirectoryTransaction.TryRemoveTemporaryStaging(temporaryStaging);
    if (cleanupError is not IOException || !Directory.Exists(temporaryStaging))
        throw new InvalidOperationException("Locked staging cleanup did not return its failure without throwing.");
}
if (InstallDirectoryTransaction.TryRemoveTemporaryStaging(temporaryStaging) is not null ||
    InstallDirectoryTransaction.TryRemoveTemporaryStaging(temporaryStaging) is not null ||
    Directory.Exists(temporaryStaging))
    throw new InvalidOperationException("Staging cleanup did not remove unlocked or tolerate absent files.");

ExpectFailure(() => InstallDirectoryTransaction.Execute(root, stage =>
{
    File.WriteAllText(Path.Combine(stage, "partial.txt"), "partial");
    throw new IOException("copy failure");
}, () => throw new InvalidOperationException("Configuration must not run."), () => { }));
AssertOld();

string collisionRoot = Path.Combine(testRoot, "collision", "GraphAutoReader");
Directory.CreateDirectory(collisionRoot);
File.WriteAllText(Path.Combine(collisionRoot, "app.txt"), "old");
ExpectFailure(() => InstallDirectoryTransaction.Execute(collisionRoot, stage =>
{
    Directory.CreateDirectory(stage.Replace(".staging-", ".backup-", StringComparison.Ordinal));
}, () => throw new InvalidOperationException("Configuration must not run."), () => { }));
if (File.ReadAllText(Path.Combine(collisionRoot, "app.txt")) != "old")
{
    throw new InvalidOperationException("Failed directory swap changed the previous installation.");
}

string shortcut = Path.Combine(testRoot, "shortcut.lnk");
File.WriteAllText(shortcut, "old shortcut");
var registry = new FakeRegistry();
registry.SetValue("DisplayName", "old registry value", RegistryValueKind.String);
registry.SetValue("CustomValue", "user-owned value", RegistryValueKind.String);
InstallerMetadataSnapshot metadata = InstallerMetadataSnapshot.Capture(registry, shortcut);
ExpectFailure(() => InstallDirectoryTransaction.Execute(root,
    stage => File.WriteAllText(Path.Combine(stage, "app.txt"), "new"),
    () => { File.WriteAllText(shortcut, "new shortcut"); registry.SetValue("DisplayName", "new registry value", RegistryValueKind.String); registry.SetValue("NoRepair", 0, RegistryValueKind.DWord); throw new IOException("configuration failure"); },
    metadata.Restore));
AssertOld();
if (File.ReadAllText(shortcut) != "old shortcut" ||
    (string)registry.GetValue("DisplayName").Value != "old registry value" ||
    (string)registry.GetValue("CustomValue").Value != "user-owned value" ||
    registry.GetValueNames().Contains("NoRepair", StringComparer.Ordinal))
    throw new InvalidOperationException("Configuration rollback did not restore metadata.");
if (Directory.GetDirectories(testRoot, "GraphAutoReader.failed-*").Length == 0)
{
    throw new InvalidOperationException("Failed replacement was not preserved.");
}

InstallDirectoryTransaction.Execute(root,
    stage => { File.WriteAllText(Path.Combine(stage, "app.txt"), "new"); Directory.CreateDirectory(Path.Combine(stage, "notes")); File.WriteAllText(Path.Combine(stage, "notes", "payload.txt"), "new payload"); },
    () => { }, () => { });
if (File.ReadAllText(Path.Combine(root, "app.txt")) != "new")
{
    throw new InvalidOperationException("Replacement was not activated.");
}
if (File.ReadAllText(Path.Combine(root, "user-file.txt")) != "keep" ||
    File.ReadAllText(Path.Combine(root, "notes", "user.txt")) != "nested" ||
    File.ReadAllText(Path.Combine(root, "notes", "payload.txt")) != "new payload")
    throw new InvalidOperationException("User files were not copied into the active installation.");
string[] backups = Directory.GetDirectories(testRoot, "GraphAutoReader.backup-*");
if (backups.Length != 1 || File.ReadAllText(Path.Combine(backups[0], "user-file.txt")) != "keep")
{
    throw new InvalidOperationException("Previous user files were not retained.");
}

string ownedRoot = Path.Combine(testRoot, "owned-upgrade", "GraphAutoReader");
Directory.CreateDirectory(ownedRoot);
File.WriteAllText(Path.Combine(ownedRoot, "obsolete.dll"), "unchanged setup payload");
File.WriteAllText(Path.Combine(ownedRoot, "customized.dll"), "user-modified setup payload");
File.WriteAllText(Path.Combine(ownedRoot, "user-notes.txt"), "user-owned file");
File.WriteAllText(Path.Combine(ownedRoot, ".graphreader-owned-files.json"), JsonSerializer.Serialize(new
{
    schema = "graphreader.installed-payload-inventory.v1",
    files = new[]
    {
        new { path = "obsolete.dll", sha256 = HashText("unchanged setup payload") },
        new { path = "customized.dll", sha256 = HashText("original setup payload") },
    },
}));
InstallDirectoryTransaction.Execute(ownedRoot, stage =>
{
    File.WriteAllText(Path.Combine(stage, "new-app.txt"), "new payload");
    File.WriteAllText(Path.Combine(stage, ".graphreader-owned-files.json"), "new inventory");
}, () => { }, () => { });
if (File.Exists(Path.Combine(ownedRoot, "obsolete.dll")) ||
    File.ReadAllText(Path.Combine(ownedRoot, "customized.dll")) != "user-modified setup payload" ||
    File.ReadAllText(Path.Combine(ownedRoot, "user-notes.txt")) != "user-owned file")
{
    throw new InvalidOperationException("Upgrade did not omit unchanged obsolete payload while retaining user files.");
}
string ownedBackup = Directory.GetDirectories(Path.Combine(testRoot, "owned-upgrade"), "GraphAutoReader.backup-*").Single();
if (File.ReadAllText(Path.Combine(ownedBackup, "obsolete.dll")) != "unchanged setup payload" ||
    File.ReadAllText(Path.Combine(ownedBackup, "customized.dll")) != "user-modified setup payload")
{
    throw new InvalidOperationException("Upgrade did not preserve prior payload in the backup.");
}

string invalidInventoryRoot = Path.Combine(testRoot, "invalid-inventory-upgrade", "GraphAutoReader");
Directory.CreateDirectory(invalidInventoryRoot);
File.WriteAllText(Path.Combine(invalidInventoryRoot, "old-extra.dll"), "preserve on invalid inventory");
File.WriteAllText(Path.Combine(invalidInventoryRoot, ".graphreader-owned-files.json"), "not json");
InstallDirectoryTransaction.Execute(invalidInventoryRoot,
    stage => File.WriteAllText(Path.Combine(stage, "new-app.txt"), "new payload"), () => { }, () => { });
if (File.ReadAllText(Path.Combine(invalidInventoryRoot, "old-extra.dll")) != "preserve on invalid inventory")
{
    throw new InvalidOperationException("Invalid ownership inventory caused prior files to be omitted.");
}

string spacedRoot = Path.Combine(testRoot, "Install Space 한글", "GraphAutoReader");
Directory.CreateDirectory(spacedRoot);
File.WriteAllText(Path.Combine(spacedRoot, "user.txt"), "preserve during uninstall");
string preserved = spacedRoot + ".uninstalled-fixture";
InstallerMetadataSnapshot uninstallMetadata = InstallerMetadataSnapshot.Capture(registry, shortcut);
Directory.CreateDirectory(preserved);
bool unregisterCalled = false;
ExpectFailure(() => InstallDirectoryTransaction.Uninstall(spacedRoot, preserved,
    () => { unregisterCalled = true; uninstallMetadata.RemoveRegistration(); }, uninstallMetadata.Restore));
if (unregisterCalled || !File.Exists(shortcut) || (string)registry.GetValue("DisplayName").Value != "old registry value")
    throw new InvalidOperationException("A failed uninstall move changed registration.");
string preserved2 = spacedRoot + ".uninstalled-second";
ExpectFailure(() => InstallDirectoryTransaction.Uninstall(spacedRoot, preserved2,
    () => { uninstallMetadata.RemoveRegistration(); throw new IOException("unregistration failure"); }, uninstallMetadata.Restore));
if (!Directory.Exists(spacedRoot) || !File.Exists(shortcut) || (string)registry.GetValue("DisplayName").Value != "old registry value")
    throw new InvalidOperationException("Uninstall registration failure did not roll back.");
InstallDirectoryTransaction.Uninstall(spacedRoot, preserved2, uninstallMetadata.RemoveRegistration, uninstallMetadata.Restore);
InstallDirectoryTransaction.Uninstall(spacedRoot, preserved2, uninstallMetadata.RemoveRegistration, uninstallMetadata.Restore);
if (Directory.Exists(spacedRoot) || File.ReadAllText(Path.Combine(preserved2, "user.txt")) != "preserve during uninstall" ||
    File.Exists(shortcut) || registry.GetValueNames().Contains("DisplayName", StringComparer.Ordinal))
    throw new InvalidOperationException("Repeated uninstall did not preserve files under a spaced Unicode path.");

string reparseRoot = Path.Combine(testRoot, "reparse", "GraphAutoReader");
string linkTarget = Path.Combine(testRoot, "link target");
Directory.CreateDirectory(reparseRoot);
Directory.CreateDirectory(linkTarget);
File.WriteAllText(Path.Combine(linkTarget, "outside.txt"), "untouched");
try
{
    Directory.CreateSymbolicLink(Path.Combine(reparseRoot, "link"), linkTarget);
    ExpectFailure(() => InstallDirectoryTransaction.Execute(reparseRoot,
        stage => File.WriteAllText(Path.Combine(stage, "app.txt"), "new"), () => { }, () => { }));
    if (File.ReadAllText(Path.Combine(linkTarget, "outside.txt")) != "untouched" || !Directory.Exists(reparseRoot))
        throw new InvalidOperationException("Reparse-point rejection failed.");
    Console.WriteLine("PASS: reparse-point rejection");
}
catch (UnauthorizedAccessException)
{
    Console.WriteLine("SKIP: Windows did not permit creation of a synthetic symbolic link");
}
string raceRoot = Path.Combine(testRoot, "race", "GraphAutoReader");
Directory.CreateDirectory(raceRoot);
InstallCoordination.CreateFreshIdentity(raceRoot);
string originalIdentity = InstallCoordination.ReadOrCreateLegacyIdentity(raceRoot);
string readyPath = Path.Combine(testRoot, "race-ready.txt"), resultPath = Path.Combine(testRoot, "race-result.txt");
using (InstallCoordination.Acquire(raceRoot))
{
    var start = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false, CreateNoWindow = true };
    foreach (string argument in new[] { "--race-worker", raceRoot, originalIdentity, readyPath, resultPath })
        start.ArgumentList.Add(argument);
    using Process worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start race worker.");
    var deadline = Stopwatch.StartNew();
    while (!File.Exists(readyPath) && deadline.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(25);
    if (!File.Exists(readyPath) || File.Exists(resultPath)) throw new InvalidOperationException("The worker did not wait for the setup mutex.");
    Directory.Move(raceRoot, raceRoot + ".previous");
    Directory.CreateDirectory(raceRoot);
    InstallCoordination.CreateFreshIdentity(raceRoot);
}
var waitForResult = Stopwatch.StartNew();
while (!File.Exists(resultPath) && waitForResult.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(25);
if (!File.Exists(resultPath) || File.ReadAllText(resultPath) != "rejected" || !Directory.Exists(raceRoot))
    throw new InvalidOperationException("Deferred worker accepted a replacement installation.");

string statusPath = Path.Combine(testRoot, "uninstall-status.json");
UninstallStatus.Write(statusPath, "pending", raceRoot, raceRoot + ".uninstalled-test");
using (JsonDocument pending = JsonDocument.Parse(File.ReadAllText(statusPath)))
    if (pending.RootElement.GetProperty("state").GetString() != "pending" || UninstallStatus.PendingExitCode == 0)
        throw new InvalidOperationException("Pending uninstall was reported as completed.");
UninstallStatus.Write(statusPath, "failed", raceRoot, raceRoot + ".uninstalled-test", "identity changed");
using (JsonDocument failed = JsonDocument.Parse(File.ReadAllText(statusPath)))
    if (failed.RootElement.GetProperty("state").GetString() != "failed")
        throw new InvalidOperationException("Failed uninstall status was not recorded.");
string schedulingStatus = Path.Combine(testRoot, "uninstall-scheduling-status.json");
UninstallStatus.Write(schedulingStatus, "pending", raceRoot, raceRoot + ".uninstalled-test");
bool helperLaunched = false;
ExpectFailure(() => DeferredUninstallExecution.Schedule(
    () => throw new IOException("injected pointer write failure"),
    () => helperLaunched = true,
    error => UninstallStatus.Write(schedulingStatus, "failed", raceRoot, raceRoot + ".uninstalled-test", error.Message)));
using (JsonDocument scheduling = JsonDocument.Parse(File.ReadAllText(schedulingStatus)))
    if (helperLaunched || scheduling.RootElement.GetProperty("state").GetString() != "failed")
        throw new InvalidOperationException("Pointer failure left a pending record or launched a helper.");
if (!DeferredUninstallExecution.IsQuiet(["--finalize-uninstall", "123", "path", "identity", "invalid-status", "quiet"]))
    throw new InvalidOperationException("Quiet helper was not quiet before validation.");
bool failureRecorded = false;
DeferredUninstallResult invalidStatus = DeferredUninstallExecution.Run(
    () => throw new ArgumentException("invalid status path"), () => { }, _ => failureRecorded = true);
if (invalidStatus.Completed || !failureRecorded || invalidStatus.OperationError is not ArgumentException)
    throw new InvalidOperationException("Invalid helper status was not treated as an operation failure.");
failureRecorded = false;
DeferredUninstallResult statusWriteFailure = DeferredUninstallExecution.Run(
    () => { }, () => throw new IOException("injected success status write failure"), _ => failureRecorded = true);
if (!statusWriteFailure.Completed || statusWriteFailure.StatusError is not IOException || failureRecorded)
    throw new InvalidOperationException("Completed uninstall was incorrectly reported as failed.");
Console.WriteLine("PASS: failures restore files and metadata; upgrade and repeated uninstall preserve user files");
Console.WriteLine("PASS: verified unchanged obsolete payload stays in backup; modified and untracked files remain active");
Console.WriteLine("PASS: concurrent reinstall invalidates deferred identity; quiet validation and status-write failures classified");
Console.WriteLine("PASS: locked temporary staging reports cleanup failure without throwing");

internal sealed class FakeRegistry : IInstallerRegistryStore
{
    private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> values = new(StringComparer.OrdinalIgnoreCase);
    private bool exists = true;
    public bool Exists() => exists;
    public string[] GetValueNames() => values.Keys.ToArray();
    public (object Value, RegistryValueKind Kind) GetValue(string name) => values[name];
    public void EnsureKey() => exists = true;
    public void SetValue(string name, object value, RegistryValueKind kind) => values[name] = (value, kind);
    public void DeleteValue(string name) => values.Remove(name);
    public void DeleteKeyIfEmpty() { if (values.Count == 0) exists = false; }
}
