// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace GraphReader.App.Integration.Workflow;

internal sealed record ProductionOriginalDbRuntimeFileDescriptor(
    string FileName,
    string Sha256);

internal enum ProductionOriginalDbRuntimeBindingKind
{
    LoadedManagedAssembly,
    LoadedNativeModule,
    PackagedNativeDependency,
}

internal sealed record ProductionOriginalDbRuntimeFileIdentity(
    string FileName,
    string FullPath,
    string Sha256,
    ProductionOriginalDbRuntimeBindingKind BindingKind);

internal sealed record ProductionOriginalDbRuntimeSnapshot(
    IReadOnlyList<ProductionOriginalDbRuntimeFileIdentity> Files);

/// <summary>
/// Binds the OCR candidate's ONNX Runtime and OpenCvSharp descriptors to the
/// files selected by the application host. Native dependencies that have not
/// loaded yet are bound to the application deployment root.
/// </summary>
internal static class ProductionOriginalDbRuntimeFiles
{
    private static readonly string[] ManagedFileNames =
    [
        "Microsoft.ML.OnnxRuntime.dll",
        "OpenCvSharp.dll",
    ];

    private static readonly string[] NativeFileNames =
    [
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
    ];

    internal static ProductionOriginalDbRuntimeSnapshot Validate(
        string applicationRuntimeRoot,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> managedFiles,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> nativeFiles) =>
        Validate(applicationRuntimeRoot, managedFiles, nativeFiles, FindLoadedNativeModule);

    internal static ProductionOriginalDbRuntimeSnapshot ValidateForTest(
        string applicationRuntimeRoot,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> managedFiles,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> nativeFiles,
        Func<string, string?> loadedNativeModule) =>
        Validate(applicationRuntimeRoot, managedFiles, nativeFiles, loadedNativeModule);

    private static ProductionOriginalDbRuntimeSnapshot Validate(
        string applicationRuntimeRoot,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> managedFiles,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> nativeFiles,
        Func<string, string?> loadedNativeModule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRuntimeRoot);
        ArgumentNullException.ThrowIfNull(managedFiles);
        ArgumentNullException.ThrowIfNull(nativeFiles);
        ArgumentNullException.ThrowIfNull(loadedNativeModule);

        string runtimeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRuntimeRoot));
        var identities = new List<ProductionOriginalDbRuntimeFileIdentity>(4);
        foreach (string fileName in ManagedFileNames)
        {
            ProductionOriginalDbRuntimeFileDescriptor descriptor =
                RequireDescriptor(managedFiles, fileName, "managed");
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(fileName)));
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                throw new InvalidDataException($"Required managed runtime '{fileName}' could not be loaded.", exception);
            }

            string loadedPath = RequireRuntimeRootPath(runtimeRoot, assembly.Location, fileName, "managed assembly");
            identities.Add(VerifyIdentity(
                descriptor,
                loadedPath,
                ProductionOriginalDbRuntimeBindingKind.LoadedManagedAssembly));
        }

        foreach (string fileName in NativeFileNames)
        {
            ProductionOriginalDbRuntimeFileDescriptor descriptor =
                RequireDescriptor(nativeFiles, fileName, "native");
            string packagedPath = Path.Combine(runtimeRoot, fileName);
            string? loadedPath = loadedNativeModule(fileName);
            string actualPath;
            ProductionOriginalDbRuntimeBindingKind bindingKind;
            if (loadedPath is null)
            {
                actualPath = packagedPath;
                bindingKind = ProductionOriginalDbRuntimeBindingKind.PackagedNativeDependency;
            }
            else
            {
                actualPath = RequireRuntimeRootPath(runtimeRoot, loadedPath, fileName, "native module");
                if (!string.Equals(actualPath, Path.GetFullPath(packagedPath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Loaded native module '{fileName}' does not come from the application runtime root.");
                }
                bindingKind = ProductionOriginalDbRuntimeBindingKind.LoadedNativeModule;
            }

            identities.Add(VerifyIdentity(descriptor, actualPath, bindingKind));
        }

        return new ProductionOriginalDbRuntimeSnapshot(identities.AsReadOnly());
    }

    private static ProductionOriginalDbRuntimeFileDescriptor RequireDescriptor(
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> descriptors,
        string expectedFileName,
        string label)
    {
        ProductionOriginalDbRuntimeFileDescriptor[] matches = descriptors.Where(descriptor =>
            string.Equals(Path.GetFileName(descriptor.FileName), expectedFileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Original-DB OCR {label} runtime requires exactly one '{expectedFileName}' descriptor.");
        }

        ProductionOriginalDbRuntimeFileDescriptor match = matches[0];
        if (string.IsNullOrWhiteSpace(match.Sha256) ||
            match.Sha256.Length != 64 ||
            match.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"Original-DB OCR runtime descriptor '{expectedFileName}' has an invalid SHA-256 value.");
        }

        return match;
    }

    private static string RequireRuntimeRootPath(
        string runtimeRoot,
        string path,
        string expectedFileName,
        string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException($"Loaded {label} '{expectedFileName}' has no file location.");
        }

        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(fullPath), expectedFileName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(fullPath), runtimeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Loaded {label} '{expectedFileName}' does not come from the application runtime root.");
        }

        return fullPath;
    }

    private static ProductionOriginalDbRuntimeFileIdentity VerifyIdentity(
        ProductionOriginalDbRuntimeFileDescriptor descriptor,
        string path,
        ProductionOriginalDbRuntimeBindingKind bindingKind)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A required OCR runtime dependency is missing.", path);
        }

        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(actualSha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Application runtime dependency '{Path.GetFileName(path)}' differs from the evaluated candidate.");
        }

        return new ProductionOriginalDbRuntimeFileIdentity(
            Path.GetFileName(path),
            path,
            actualSha256,
            bindingKind);
    }

    private static string? FindLoadedNativeModule(string fileName)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            string[] matches = process.Modules.Cast<ProcessModule>()
                .Where(module => string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase))
                .Select(module => module.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return matches.Length switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new InvalidDataException($"Multiple loaded native modules are named '{fileName}'."),
            };
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException("Loaded native runtime modules could not be inspected.", exception);
        }
    }
}
