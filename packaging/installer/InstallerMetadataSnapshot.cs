// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.Win32;

namespace GraphReader.Installer;

/// <summary>Captures only the shortcut and uninstall values setup modifies.</summary>
internal sealed class InstallerMetadataSnapshot
{
    private static readonly string[] ValueNames =
    [
        "DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "DisplayIcon",
        "UninstallString", "QuietUninstallString", "NoModify", "NoRepair",
    ];

    private readonly IInstallerRegistryStore registry;
    private readonly string shortcutPath;
    private readonly byte[]? shortcut;
    private readonly bool keyExisted;
    private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> values;

    private InstallerMetadataSnapshot(IInstallerRegistryStore registry, string shortcutPath, byte[]? shortcut,
        bool keyExisted, Dictionary<string, (object Value, RegistryValueKind Kind)> values)
    {
        this.registry = registry;
        this.shortcutPath = shortcutPath;
        this.shortcut = shortcut;
        this.keyExisted = keyExisted;
        this.values = values;
    }

    internal static InstallerMetadataSnapshot Capture(string registryPath, string shortcutPath) =>
        Capture(new WindowsInstallerRegistryStore(registryPath), shortcutPath);

    internal static InstallerMetadataSnapshot Capture(IInstallerRegistryStore registry, string shortcutPath)
    {
        byte[]? shortcut = null;
        if (File.Exists(shortcutPath))
        {
            if ((File.GetAttributes(shortcutPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The existing Start Menu shortcut is a reparse point.");
            shortcut = File.ReadAllBytes(shortcutPath);
        }
        var values = new Dictionary<string, (object Value, RegistryValueKind Kind)>(StringComparer.OrdinalIgnoreCase);
        bool keyExisted = registry.Exists();
        if (keyExisted)
        {
            var names = new HashSet<string>(registry.GetValueNames(), StringComparer.OrdinalIgnoreCase);
            foreach (string name in ValueNames)
            {
                if (!names.Contains(name)) continue;
                values.Add(name, registry.GetValue(name));
            }
        }
        return new InstallerMetadataSnapshot(registry, shortcutPath, shortcut, keyExisted, values);
    }

    internal void Restore()
    {
        var errors = new List<Exception>();
        try
        {
            if (shortcut is null)
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
                File.WriteAllBytes(shortcutPath, shortcut);
            }
        }
        catch (Exception error) { errors.Add(error); }
        try
        {
            if (keyExisted) registry.EnsureKey();
            if (registry.Exists())
            {
                foreach (string name in ValueNames)
                {
                    if (values.TryGetValue(name, out var previous))
                        registry.SetValue(name, previous.Value, previous.Kind);
                    else
                        registry.DeleteValue(name);
                }
                if (!keyExisted) registry.DeleteKeyIfEmpty();
            }
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Installer metadata could not be restored.", errors);
    }

    internal void RemoveRegistration()
    {
        if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        if (!registry.Exists()) return;
        foreach (string name in ValueNames) registry.DeleteValue(name);
        registry.DeleteKeyIfEmpty();
    }
}

internal interface IInstallerRegistryStore
{
    bool Exists();
    string[] GetValueNames();
    (object Value, RegistryValueKind Kind) GetValue(string name);
    void EnsureKey();
    void SetValue(string name, object value, RegistryValueKind kind);
    void DeleteValue(string name);
    void DeleteKeyIfEmpty();
}

internal sealed class WindowsInstallerRegistryStore(string path) : IInstallerRegistryStore
{
    public bool Exists() { using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, false); return key is not null; }
    public string[] GetValueNames() { using RegistryKey key = Open(false); return key.GetValueNames(); }
    public (object Value, RegistryValueKind Kind) GetValue(string name)
    {
        using RegistryKey key = Open(false);
        object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new IOException("An uninstall registry value could not be captured.");
        return (value, key.GetValueKind(name));
    }
    public void EnsureKey() { using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, true); }
    public void SetValue(string name, object value, RegistryValueKind kind) { using RegistryKey key = Open(true); key.SetValue(name, value, kind); }
    public void DeleteValue(string name) { using RegistryKey key = Open(true); key.DeleteValue(name, false); }
    public void DeleteKeyIfEmpty()
    {
        bool empty;
        using (RegistryKey key = Open(true)) { empty = key.ValueCount == 0 && key.SubKeyCount == 0; }
        if (empty) Registry.CurrentUser.DeleteSubKey(path, false);
    }
    private RegistryKey Open(bool writable) => Registry.CurrentUser.OpenSubKey(path, writable)
        ?? throw new IOException("The uninstall registry key is unavailable.");
}
