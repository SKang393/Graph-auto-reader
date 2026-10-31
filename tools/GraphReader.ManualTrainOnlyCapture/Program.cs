using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.ManualTrainOnlyCapture;

internal static class Program
{
    private const string Schema = "graphreader.manual-train-only-source-capture.v1";
    private const string ProtocolSchema = "graphreader.manual-train-only-source-capture-protocol.v1";
    private const string AuthorizedProjectRoot = @"E:\Graph-auto-reader";
    private const string ProtocolRelativePath = "tools/GraphReader.ManualTrainOnlyCapture/protocol.json";
    private static readonly string[] BoundSourcePaths =
    [
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "src/GraphReader.Export/ExportContracts.cs",
        "src/GraphReader.Export/GraphReader.Export.csproj",
        "tools/GraphReader.ManualTrainOnlyCapture/GraphReader.ManualTrainOnlyCapture.csproj",
        "tools/GraphReader.ManualTrainOnlyCapture/Program.cs",
        "tools/GraphReader.ManualTrainOnlyCapture/TrainOnlySourceCapture.cs",
        "tools/GraphReader.ManualTrainOnlyCapture/TrainOnlySourceCaptureSelfTest.cs",
        "tools/GraphReader.RealAcceptance.Ocr/EngaugeDevSourceMaterializer.cs",
        "tools/GraphReader.RealAcceptance.Ocr/EngaugeDigWholeWorkflowTruthAdapter.cs",
        "tools/GraphReader.RealAcceptance.Ocr/FrozenDevTrainingSelector.cs",
        "tools/GraphReader.RealAcceptance.Ocr/FrozenRealCorpusInventory.cs",
        "tools/GraphReader.RealAcceptance.Ocr/WholeWorkflowCsvEvaluator.cs",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--self-test" && args[1] == "--expected-protocol-sha256")
            {
                string root = FindProjectRoot();
                Protocol protocol = LoadAndVerifyProtocol(root, args[2], out _);
                VerifySourceSnapshot(root, protocol.source_files);
                VerifyRuntimeBindingSelfTest(root, protocol.runtime_assemblies);
                Console.WriteLine(JsonSerializer.Serialize(TrainOnlySourceCaptureSelfTest.Run(), JsonOptions));
                return 0;
            }

            if (args.Length == 5 && args[0] == "--capture" && args[1] == "--expected-protocol-sha256" && args[3] == "--output")
            {
                string root = FindProjectRoot();
                Protocol protocol = LoadAndVerifyProtocol(root, args[2], out string protocolDigest);
                VerifySourceSnapshot(root, protocol.source_files);
                string output = ResolveNewOutput(root, args[4]);
                using RuntimeBindingLease runtimeLease = AcquireRuntimeBinding(root, protocol.runtime_assemblies);
                RuntimeBinding runtimeBefore = runtimeLease.Binding;
                using AttemptJournal journal = AttemptJournal.Create(root, output, protocolDigest);
                bool manifestCommitted = false;
                string failurePhase = "inventory_selection";
                try
                {
                    string corpusRoot = Path.Combine(root, "data", "manual data");
                    FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpusRoot, CancellationToken.None);
                    failurePhase = "train_source_capture";
                    TrainOnlyCaptureResult capture = TrainOnlySourceCapture.Capture(selector, root, CancellationToken.None, journal.RecordSource);
                    failurePhase = "post_capture_source_revalidation";
                    VerifySourceSnapshot(root, protocol.source_files);
                    if (!string.Equals(HashFile(Path.Combine(root, ProtocolRelativePath)), protocolDigest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Protocol changed during the train-only source capture.");
                    RuntimeBinding runtimeAfter = runtimeLease.Revalidate(root, protocol.runtime_assemblies);
                    if (!SameRuntimeBinding(runtimeBefore, runtimeAfter))
                        throw new InvalidDataException("Loaded runtime assemblies changed during the train-only source capture.");
                    journal.Append("capture_sources_complete", new
                    {
                        train_projects = capture.trainProjectCount,
                        development_metadata_projects = capture.developmentProjectCount,
                        development_payload_reads = 0,
                        sealed_payload_reads = 0,
                        curve_point_annotations = capture.curvePointCount,
                    });
                    journal.Append("manifest_commit_started", new { manifest_name = "manifest.json" });
                    failurePhase = "manifest_commit";
                    string attemptSha256 = journal.ComputeSha256();
                    var manifest = new
                    {
                        schema = Schema,
                        status = "completed_train_only_source_capture_unapproved",
                        split = "train",
                        protocol_sha256 = protocolDigest,
                        source_files = protocol.source_files,
                        source_inventory = new
                        {
                            assignment_sha256 = capture.assignmentSha256,
                            selected_inventory_sha256 = capture.selectedInventorySha256,
                            partition_rule = capture.partitionRule,
                            source_hash_timing = "first-observed-during-this-capture; not independently preregistered per-source",
                            cross_split_image_overlap = "unverified",
                        },
                        capture_attempt = new
                        {
                            path = "capture-attempt.jsonl",
                            sha256 = attemptSha256,
                            event_count = journal.EventCount,
                            failure_accounting = "append-only; per-source status and error type; no source paths or exception messages",
                        },
                        counts = new
                        {
                            selected_real_dev_metadata_projects = capture.selectedRealDevProjectCount,
                            train_projects = capture.trainProjectCount,
                            development_projects = capture.developmentProjectCount,
                            train_project_payloads_opened = capture.trainProjectReadCount,
                            development_project_payloads_opened = capture.developmentProjectReadCount,
                            duplicate_embedded_image_groups = capture.duplicateEmbeddedImageGroups,
                            duplicate_embedded_image_rows = capture.duplicateEmbeddedImageRows,
                            curve_point_annotations = capture.curvePointCount,
                            negative_labels = 0,
                        },
                        labeling = new
                        {
                            scope = "curve-point-centers-only; marker-glyph-correspondence-unverified",
                            candidate_authentication = false,
                            negative_labels_available = false,
                            image_duplicate_grouping_required = true,
                        },
                        scope = new
                        {
                            train_payload_reads = capture.trainProjectReadCount,
                            development_payload_reads = 0,
                            sealed_payload_reads = 0,
                            inference_runs = 0,
                            optimizer_steps = 0,
                            training_admitted = false,
                        },
                        rows = capture.rows,
                        runtime = runtimeAfter,
                    };
                    await WriteNewJsonAsync(root, output, manifest, CancellationToken.None);
                    manifestCommitted = true;
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "completed", train_project_count = capture.trainProjectCount, development_project_payloads_opened = 0, optimizer_steps = 0 }, JsonOptions));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    if (!manifestCommitted)
                    {
                        try { journal.Append("run_failed", new { phase = failurePhase, error_type = exception.GetType().Name }); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    throw;
                }
                return 0;
            }

            Console.Error.WriteLine("Usage: --self-test --expected-protocol-sha256 <64-hex> | --capture --expected-protocol-sha256 <64-hex> --output <new project-local json path>");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"BLOCKED: {exception.GetType().Name}");
            return 2;
        }
    }

    private static Protocol LoadAndVerifyProtocol(string root, string expectedSha256, out string protocolDigest)
    {
        if (!IsSha256(expectedSha256)) throw new InvalidDataException("Expected protocol digest must be 64 hexadecimal characters.");
        string path = ResolveExistingProjectFile(root, ProtocolRelativePath);
        string actual = HashFile(path);
        protocolDigest = actual;
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Protocol bytes do not match the externally supplied digest.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement rootElement = document.RootElement;
        RequireObjectKeys(rootElement, "schema", "status", "source_files", "runtime_assemblies", "split", "labels", "scope");
        string? schemaValue = rootElement.GetProperty("schema").GetString();
        string? statusValue = rootElement.GetProperty("status").GetString();
        if (!string.Equals(schemaValue, ProtocolSchema, StringComparison.Ordinal) ||
            !string.Equals(statusValue, "source_capture_only_unapproved", StringComparison.Ordinal))
            throw new InvalidDataException($"Protocol schema or status is invalid (schema={schemaValue ?? "<null>"}; status={statusValue ?? "<null>"}).");
        JsonElement split = rootElement.GetProperty("split");
        RequireObjectKeys(split, "inventory", "partition_rule", "expected_inventory_projects", "expected_train_projects", "expected_development_projects", "development_payload_reads", "sealed_payload_reads");
        if (split.GetProperty("inventory").GetString() != "real-dev" ||
            split.GetProperty("partition_rule").GetString() != EngaugeDevSourceMaterializer.PartitionRule ||
            split.GetProperty("expected_inventory_projects").GetInt32() != 120 ||
            split.GetProperty("expected_train_projects").GetInt32() != 104 ||
            split.GetProperty("expected_development_projects").GetInt32() != 16 ||
            split.GetProperty("development_payload_reads").GetInt32() != 0 ||
            split.GetProperty("sealed_payload_reads").GetInt32() != 0)
            throw new InvalidDataException("Protocol split contract is invalid.");
        JsonElement labels = rootElement.GetProperty("labels");
        RequireObjectKeys(labels, "source", "positive_label", "marker_glyph_correspondence", "negative_labels", "duplicate_image_grouping_required");
        if (labels.GetProperty("source").GetString() != "Engauge curve-point screen coordinates" ||
            labels.GetProperty("positive_label").GetString() != "curve-point center only" ||
            labels.GetProperty("marker_glyph_correspondence").GetString() != "unverified" ||
            labels.GetProperty("negative_labels").GetInt32() != 0 ||
            !labels.GetProperty("duplicate_image_grouping_required").GetBoolean())
            throw new InvalidDataException("Protocol label limitations are invalid.");
        JsonElement scope = rootElement.GetProperty("scope");
        RequireObjectKeys(scope, "inference_runs", "optimizer_steps", "development_payload_reads", "sealed_payload_reads", "training_admitted", "candidate_authorized");
        if (scope.GetProperty("inference_runs").GetInt32() != 0 || scope.GetProperty("optimizer_steps").GetInt32() != 0 ||
            scope.GetProperty("development_payload_reads").GetInt32() != 0 || scope.GetProperty("sealed_payload_reads").GetInt32() != 0 ||
            scope.GetProperty("training_admitted").GetBoolean() || scope.GetProperty("candidate_authorized").GetBoolean())
            throw new InvalidDataException("Protocol scope is invalid.");
        JsonElement sourceFilesElement = rootElement.GetProperty("source_files");
        if (sourceFilesElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Protocol source file map is invalid.");
        var sourceFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty field in sourceFilesElement.EnumerateObject())
        {
            if (!sourceFiles.TryAdd(field.Name, field.Value.GetString() ?? string.Empty))
                throw new InvalidDataException("Protocol contains a duplicate source path.");
        }
        JsonElement runtimeElement = rootElement.GetProperty("runtime_assemblies");
        RequireObjectKeys(runtimeElement, "GraphReader.Export", "GraphReader.ManualTrainOnlyCapture");
        var runtimeAssemblies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty field in runtimeElement.EnumerateObject())
        {
            if (!runtimeAssemblies.TryAdd(field.Name, field.Value.GetString() ?? string.Empty))
                throw new InvalidDataException("Protocol contains a duplicate runtime assembly name.");
        }
        var protocol = new Protocol(sourceFiles, runtimeAssemblies);
        string[] expected = BoundSourcePaths.Order(StringComparer.Ordinal).ToArray();
        if (!protocol.source_files.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Protocol source file set differs from the fixed tool closure.");
        if (protocol.source_files.Values.Any(static digest => !IsSha256(digest)))
            throw new InvalidDataException("Protocol contains a malformed source hash.");
        if (protocol.runtime_assemblies.Values.Any(static digest => !IsSha256(digest)))
            throw new InvalidDataException("Protocol contains a malformed runtime assembly hash.");
        return protocol;
    }

    private static void RequireObjectKeys(JsonElement element, params string[] expectedKeys)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedKeys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Protocol object has missing or unexpected fields.");
    }

    private static void VerifySourceSnapshot(string root, IReadOnlyDictionary<string, string> sourceFiles)
    {
        foreach ((string relative, string expected) in sourceFiles.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            string path = ResolveExistingProjectFile(root, relative);
            if (!string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Bound tool source changed: {relative}");
        }
    }

    private static string ResolveExistingProjectFile(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderRoot(root, path);
        RejectReparseChain(root, path);
        if (!File.Exists(path)) throw new FileNotFoundException("Bound project file is missing.", relative);
        return path;
    }

    private static string ResolveNewOutput(string root, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("Output path is required.");
        string allowed = Path.GetFullPath(Path.Combine(root, "artifacts", "goal22-runs"));
        string path = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
        EnsureUnderRoot(allowed, path);
        if (!string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Output must be named manifest.json inside a new run directory.");
        string runDirectory = Path.GetDirectoryName(path)!;
        if (!string.Equals(Path.GetDirectoryName(runDirectory), allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Output must be in a direct child of goal22-runs.");
        RejectReparseChain(root, allowed);
        if (Directory.Exists(runDirectory) || File.Exists(runDirectory) || File.Exists(path))
            throw new IOException("Run directory must be new.");
        return path;
    }

    private static void EnsureUnderRoot(string root, string path)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path escapes its authorized project directory.");
    }

    private static void RejectReparseChain(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string current = Path.GetFullPath(path);
        while (current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse-point path is not allowed.");
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Path has no parent.");
        }
        throw new InvalidDataException("Path escapes the project root.");
    }

    private static string FindProjectRoot()
    {
        string root = Path.GetFullPath(AuthorizedProjectRoot);
        string assemblyPath = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidDataException("Entry assembly has no path.");
        ValidateExecutionBinding(root, assemblyPath);
        return root;
    }

    internal static void ValidateExecutionBindingForTest(string root, string assemblyPath) => ValidateExecutionBinding(root, assemblyPath);

    private static void ValidateExecutionBinding(string root, string assemblyPath)
    {
        string authorizedRoot = Path.GetFullPath(AuthorizedProjectRoot);
        string resolvedRoot = Path.GetFullPath(root);
        if (!string.Equals(resolvedRoot, authorizedRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(resolvedRoot, "CODEX_START_HERE.md")) ||
            !File.Exists(Path.Combine(resolvedRoot, "AGENTS.md")))
            throw new InvalidDataException("Tool execution is restricted to the authorized E project checkout.");
        string resolvedAssembly = Path.GetFullPath(assemblyPath);
        EnsureUnderRoot(resolvedRoot, resolvedAssembly);
        RejectReparseChain(resolvedRoot, resolvedAssembly);
    }

    private static RuntimeBindingLease AcquireRuntimeBinding(string root, IReadOnlyDictionary<string, string> expectedHashes)
    {
        string[] expectedNames = ["GraphReader.Export", "GraphReader.ManualTrainOnlyCapture"];
        Assembly entryAssembly = Assembly.GetEntryAssembly() ?? throw new InvalidDataException("Entry assembly is unavailable.");
        string entryPath = Path.GetFullPath(entryAssembly.Location);
        ValidateExecutionBinding(root, entryPath);
        string entryName = entryAssembly.GetName().Name ?? throw new InvalidDataException("Entry assembly name is missing.");
        string expectedExportPath = Path.Combine(Path.GetDirectoryName(entryPath)!, "GraphReader.Export.dll");
        RejectReparseChain(root, expectedExportPath);
        if (!File.Exists(expectedExportPath)) throw new FileNotFoundException("Bound Export runtime assembly is missing.");
        if (!expectedHashes.TryGetValue(entryName, out string? expectedEntryHash) ||
            !expectedHashes.TryGetValue("GraphReader.Export", out string? expectedExportHash))
            throw new InvalidDataException("Protocol runtime assembly set is incomplete.");

        FileStream? entryStream = null;
        FileStream? exportStream = null;
        try
        {
            entryStream = OpenRuntimeAssemblyReadLock(root, entryPath);
            exportStream = OpenRuntimeAssemblyReadLock(root, expectedExportPath);
            if (!string.Equals(HashStream(entryStream), expectedEntryHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Entry assembly bytes differ from the protocol pin.");
            if (!string.Equals(HashStream(exportStream), expectedExportHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Export assembly bytes differ from the protocol pin.");

            Assembly? exportAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .SingleOrDefault(static assembly => string.Equals(assembly.GetName().Name, "GraphReader.Export", StringComparison.Ordinal));
            if (exportAssembly is not null && !string.Equals(Path.GetFullPath(exportAssembly.Location), Path.GetFullPath(expectedExportPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loaded Export assembly path differs from the protocol-bound runtime.");
            exportAssembly ??= Assembly.LoadFrom(expectedExportPath);
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(static assembly => assembly.GetName().Name is "GraphReader.Export" or "GraphReader.ManualTrainOnlyCapture")
                .OrderBy(static assembly => assembly.GetName().Name, StringComparer.Ordinal)
                .ToArray();
            if (!loaded.Select(static assembly => assembly.GetName().Name).SequenceEqual(expectedNames, StringComparer.Ordinal) ||
                !ReferenceEquals(loaded.Single(assembly => assembly.GetName().Name == "GraphReader.Export"), exportAssembly) ||
                !ReferenceEquals(loaded.Single(assembly => assembly.GetName().Name == "GraphReader.ManualTrainOnlyCapture"), entryAssembly))
                throw new InvalidDataException("Loaded GraphReader runtime assembly set differs from the fixed tool closure.");

            RuntimeBinding binding = CreateRuntimeBinding(root, loaded, entryPath, expectedExportPath, entryStream, exportStream, expectedHashes);
            return new RuntimeBindingLease(binding, loaded, entryStream, exportStream);
        }
        catch
        {
            entryStream?.Dispose();
            exportStream?.Dispose();
            throw;
        }
    }

    internal static void VerifyRuntimeBindingSelfTest(string root, IReadOnlyDictionary<string, string> expectedHashes)
    {
        using RuntimeBindingLease lease = AcquireRuntimeBinding(root, expectedHashes);
        RuntimeBinding first = lease.Binding;
        RuntimeBinding second = lease.Revalidate(root, expectedHashes);
        if (!SameRuntimeBinding(first, second) || first.assemblies.Count != 2 ||
            first.assemblies.Any(static item => !IsSha256(item.sha256)) || !RuntimeFilesAreWriteLocked(root, first))
            throw new InvalidOperationException("TRAIN_ONLY_SELF_TEST_RUNTIME_BINDING_FAILED");

        var wrongExportPin = new SortedDictionary<string, string>(
            expectedHashes.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal)
        {
            ["GraphReader.Export"] = new string('0', 64)
        };
        bool rejectedBeforeRuntimeUse = false;
        try
        {
            _ = lease.Revalidate(root, wrongExportPin);
        }
        catch (InvalidDataException)
        {
            rejectedBeforeRuntimeUse = true;
        }
        bool rejectedDuringAcquisition = false;
        try
        {
            using RuntimeBindingLease _ = AcquireRuntimeBinding(root, wrongExportPin);
        }
        catch (InvalidDataException)
        {
            rejectedDuringAcquisition = true;
        }
        if (!rejectedBeforeRuntimeUse || !rejectedDuringAcquisition)
            throw new InvalidOperationException("TRAIN_ONLY_SELF_TEST_RUNTIME_PIN_GUARD_FAILED");
    }

    private static FileStream OpenRuntimeAssemblyReadLock(string root, string path)
    {
        ValidateExecutionBinding(root, path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    }

    private static RuntimeBinding CreateRuntimeBinding(
        string root,
        Assembly[] loaded,
        string entryPath,
        string exportPath,
        FileStream entryStream,
        FileStream exportStream,
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        var assemblies = new List<RuntimeAssemblyBinding>(loaded.Length);
        foreach (Assembly assembly in loaded.OrderBy(static item => item.GetName().Name, StringComparer.Ordinal))
        {
            string name = assembly.GetName().Name ?? throw new InvalidDataException("Loaded assembly name is missing.");
            string path = Path.GetFullPath(assembly.Location);
            EnsureUnderRoot(root, path);
            RejectReparseChain(root, path);
            string expectedPath = name == "GraphReader.Export" ? Path.GetFullPath(exportPath) : Path.GetFullPath(entryPath);
            if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loaded runtime assembly path differs from the expected project-local assembly.");
            FileStream heldStream = name == "GraphReader.Export" ? exportStream : entryStream;
            string digest = HashStream(heldStream);
            if (!expectedHashes.TryGetValue(name, out string? expectedDigest) ||
                !string.Equals(digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loaded runtime assembly bytes differ from the protocol pin.");
            assemblies.Add(new RuntimeAssemblyBinding(name, Path.GetRelativePath(root, path).Replace('\\', '/'), digest));
        }
        return new RuntimeBinding(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, assemblies);
    }

    private static bool RuntimeFilesAreWriteLocked(string root, RuntimeBinding binding)
    {
        foreach (RuntimeAssemblyBinding assembly in binding.assemblies)
        {
            string path = Path.Combine(root, assembly.path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                using var writable = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                return false;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return true;
    }

    private static bool SameRuntimeBinding(RuntimeBinding left, RuntimeBinding right) =>
        string.Equals(left.framework, right.framework, StringComparison.Ordinal) &&
        left.assemblies.SequenceEqual(right.assemblies);

    private sealed class RuntimeBindingLease : IDisposable
    {
        private readonly Assembly[] loadedAssemblies;
        private readonly string entryPath;
        private readonly string exportPath;
        private readonly FileStream entryStream;
        private readonly FileStream exportStream;
        private bool disposed;

        public RuntimeBindingLease(RuntimeBinding binding, Assembly[] loadedAssemblies, FileStream entryStream, FileStream exportStream)
        {
            Binding = binding;
            this.loadedAssemblies = loadedAssemblies;
            Assembly entry = loadedAssemblies.Single(static assembly => assembly.GetName().Name == "GraphReader.ManualTrainOnlyCapture");
            Assembly export = loadedAssemblies.Single(static assembly => assembly.GetName().Name == "GraphReader.Export");
            entryPath = Path.GetFullPath(entry.Location);
            exportPath = Path.GetFullPath(export.Location);
            this.entryStream = entryStream;
            this.exportStream = exportStream;
        }

        public RuntimeBinding Binding { get; }

        public RuntimeBinding Revalidate(string root, IReadOnlyDictionary<string, string> expectedHashes)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RuntimeBinding current = CreateRuntimeBinding(root, loadedAssemblies, entryPath, exportPath, entryStream, exportStream, expectedHashes);
            if (!SameRuntimeBinding(Binding, current))
                throw new InvalidDataException("Loaded runtime assembly bytes changed while the capture was running.");
            return current;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            exportStream.Dispose();
            entryStream.Dispose();
        }
    }

    private static async Task WriteNewJsonAsync(string root, string path, object value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string directory = Path.GetDirectoryName(path)!;
        RejectReparseChain(root, directory);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Output path was created concurrently.");
        string stagingPath = path + ".partial-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
            stream.Flush(flushToDisk: true);
        }
        RejectReparseChain(root, directory);
        File.Move(stagingPath, path);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return HashStream(stream);
    }

    private static string HashStream(FileStream stream)
    {
        long originalPosition = stream.Position;
        stream.Position = 0;
        string digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = originalPosition;
        return digest;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static c => char.IsAsciiHexDigit(c));

    internal static object VerifyAttemptJournalSynthetic(string root)
    {
        string testRoot = Path.Combine(root, "artifacts", "test-temp");
        Directory.CreateDirectory(testRoot);
        string runDirectory = Path.Combine(testRoot, "manual-capture-journal-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(runDirectory, "manifest.json");
        try
        {
            string digest;
            int count;
            using (AttemptJournal journal = AttemptJournal.Create(root, output, new string('a', 64)))
            {
                journal.RecordSource(new TrainOnlySourceProgress(new string('b', 64), "started", null, null, null, null));
                journal.RecordSource(new TrainOnlySourceProgress(new string('b', 64), "completed", new string('c', 64), new string('d', 64), 2, null));
                digest = journal.ComputeSha256();
                count = journal.EventCount;
            }
            string journalPath = Path.Combine(runDirectory, "capture-attempt.jsonl");
            string text = File.ReadAllText(journalPath);
            if (!IsSha256(digest) || count != 3 || text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != count ||
                text.Contains(root, StringComparison.OrdinalIgnoreCase) || text.Contains("manual data", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TRAIN_ONLY_ATTEMPT_JOURNAL_SELF_TEST_FAILED");
            return new { status = "pass", events = count, source_paths_in_log = false };
        }
        finally
        {
            if (Directory.Exists(runDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(runDirectory)) File.Delete(file);
                Directory.Delete(runDirectory, recursive: false);
            }
        }
    }

    private sealed class AttemptJournal : IDisposable
    {
        private readonly FileStream stream;
        private int sequence;

        private AttemptJournal(FileStream stream) => this.stream = stream;

        public int EventCount => sequence;

        public static AttemptJournal Create(string root, string outputPath, string protocolSha256)
        {
            string directory = Path.GetDirectoryName(outputPath)!;
            RejectReparseChain(root, Path.GetDirectoryName(directory)!);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Run directory already exists.");
            Directory.CreateDirectory(directory);
            RejectReparseChain(root, directory);
            string path = Path.Combine(directory, "capture-attempt.jsonl");
            var journal = new AttemptJournal(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough));
            try
            {
                journal.Append("run_started", new { protocol_sha256 = protocolSha256, status = "started_before_source_inventory_access" });
                return journal;
            }
            catch
            {
                journal.Dispose();
                throw;
            }
        }

        public void RecordSource(TrainOnlySourceProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);
            Append("source_" + progress.stage, new
            {
                case_id = progress.caseId,
                project_sha256 = progress.projectSha256,
                embedded_image_sha256 = progress.embeddedImageSha256,
                curve_point_count = progress.curvePointCount,
                error_type = progress.errorType,
            });
        }

        public void Append(string eventType, object payload)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { sequence = sequence + 1, event_type = eventType, payload });
            byte[] line = new byte[json.Length + 1];
            Buffer.BlockCopy(json, 0, line, 0, json.Length);
            line[^1] = (byte)'\n';
            stream.Write(line);
            stream.Flush(flushToDisk: true);
            sequence++;
        }

        public string ComputeSha256()
        {
            stream.Flush(flushToDisk: true);
            long end = stream.Length;
            stream.Position = 0;
            string digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            stream.Position = end;
            return digest;
        }

        public void Dispose() => stream.Dispose();
    }

    private sealed record Protocol(SortedDictionary<string, string> source_files, SortedDictionary<string, string> runtime_assemblies);
    private sealed record RuntimeAssemblyBinding(string name, string path, string sha256);
    private sealed record RuntimeBinding(string framework, IReadOnlyList<RuntimeAssemblyBinding> assemblies);
}
