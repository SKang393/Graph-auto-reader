// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text;

namespace GraphReader.Installer;

/// <summary>Coordinates cooperating setup processes and distinguishes reinstall generations.</summary>
internal static class InstallCoordination
{
    internal const string IdentityFile = ".graphreader-install-id";

    internal static IDisposable Acquire(string installRoot)
    {
        string normalized = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var mutex = new Mutex(false, @"Global\GraphAutoReader.Setup." + digest);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromMinutes(2))) throw new TimeoutException("Another setup operation is still running."); }
            catch (AbandonedMutexException) { /* Ownership was acquired. */ }
            return new HeldMutex(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    internal static void CreateFreshIdentity(string root)
    {
        string path = Path.Combine(root, IdentityFile);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(Guid.NewGuid().ToString("N"));
    }

    internal static string ReadOrCreateLegacyIdentity(string root)
    {
        string path = Path.Combine(root, IdentityFile);
        if (!File.Exists(path)) CreateFreshIdentity(root);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Installation identity cannot be a reparse point.");
        string value = File.ReadAllText(path);
        if (value.Length != 32 || !Guid.TryParseExact(value, "N", out _))
            throw new IOException("Installation identity is invalid.");
        return value;
    }

    internal static void RequireIdentity(string root, string expected)
    {
        string path = Path.Combine(root, IdentityFile);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            expected.Length != 32 || !Guid.TryParseExact(expected, "N", out _) ||
            !string.Equals(File.ReadAllText(path), expected, StringComparison.Ordinal))
            throw new IOException("The installation changed before deferred uninstall; it was left intact.");
    }

    private sealed class HeldMutex(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
