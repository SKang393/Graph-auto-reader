// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GraphReader.CalibrationOcrProbe;

// One independently capped child. Raw responses and image bytes stay in memory.
internal sealed class MiragePairedWorker : IMiragePairedWorker, IAsyncDisposable
{
    private const string Root = @"E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\mirage-comparison-20261115";
    private const string Python = @"E:\Graph-auto-reader\tools\gpu-training\venv\Scripts\python.exe";
    private const int MaximumResponseCharacters = 16 * 1024 * 1024 + 4096;
    private const int MaximumRawBytes = 16 * 1024 * 1024;
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
        ["runtime_sha256"] = "2b32201652e9bde6ee28a221464f52703b8b02bc03c16f18266aeedcdc61bdb2",
        ["model_sha256"] = "4249a0f7515272ff76b9414bec17d7e7daf84b15b66c7fb4f17fce1e66f9f237",
        ["manifest_sha256"] = "32e182f6f811acde1109084ee5f5c9a606098b95cc8774f27ece8bc1a506de3b",
        ["license_sha256"] = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
        ["adapter_sha256"] = "224a74de1ffccc11f438fbb6fbc7b2f1b74ef86434e254d418a557b6f199eccc",
        ["header_sha256"] = "9be7eb25b1d89180495d04a0d2c27053f282dac85add50cacc48d73302819d9d",
        ["model_card_sha256"] = "9c69381d336583e6b4069663f4028aeee9c0512d5abc6781c63d96ff54554d4f",
        ["support_sha256"] = "e4a7978e0c96e2aa798864c1157ff6182aab03ebfe8d2ebbe1da27c4e73cd350",
        ["worker_sha256"] = "050b63e76f9625fd647e4968d22a8c1cedab8c1ceec418efd925c52b45fc4867",
        ["score_diagnostic_sha256"] = "a53be74d1f8c3f634f73dd40fa8ca22d797e832d99964d34145a130cba4a7cbe",
    };
    private readonly Process process;
    private readonly List<FileStream> held;
    private readonly char[] buffer = new char[4096];
    private int offset, available;
    private readonly Task stderrDrain;
    private string budgetJson;
    private int nextId;
    private bool usable = true;
    public double StartupMilliseconds { get; private set; }
    internal static IReadOnlyDictionary<string, string> IdentityPins => Pins;

    private MiragePairedWorker(Process process, List<FileStream> held, string budgetJson, Task stderrDrain)
    { this.process = process; this.held = held; this.budgetJson = budgetJson; this.stderrDrain = stderrDrain; }
    public bool IsAlive => usable && !process.HasExited;

    internal static async Task<MiragePairedWorker> StartAsync(CancellationToken token)
    {
        var startup = Stopwatch.StartNew();
        (string Path, string Key)[] files = [("libcactus.dll", "runtime_sha256"), ("mirage.cact", "model_sha256"),
            ("release-manifest.json", "manifest_sha256"), ("LICENSE", "license_sha256"),
            ("supplied/response_adapter.py", "adapter_sha256"), ("cactus.h", "header_sha256"),
            ("model-card.md", "model_card_sha256"), ("windows_probe.py", "support_sha256"), ("windows_worker.py", "worker_sha256"),
            ("score_diagnostic.py", "score_diagnostic_sha256")];
        var held = new List<FileStream>(); Process? process = null;
        try
        {
            foreach (var item in files)
            {
                var file = new FileStream(Path.Combine(Root, item.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(file);
                string hash = Convert.ToHexStringLower(SHA256.HashData(file));
                if (hash != Pins[item.Key]) throw new InvalidDataException();
            }
            var start = new ProcessStartInfo(Python) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = Root };
            start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.Combine(Root, "windows_worker.py"));
            process = Process.Start(start) ?? throw new InvalidDataException();
            Process child = process;
            Task stderr = Task.Run(async () =>
            {
                char[] discarded = new char[4096]; int total = 0, n;
                while ((n = await child.StandardError.ReadAsync(discarded).ConfigureAwait(false)) > 0)
                { total += n; if (total > 65536) { try { child.Kill(true); } catch (InvalidOperationException) { } return; } }
            }, CancellationToken.None);
            // Temporary instance reads the bounded ready line before corpus selection.
            var worker = new MiragePairedWorker(process, held, "", stderr);
            using JsonDocument ready = JsonDocument.Parse(await worker.ReadLineAsync(token).ConfigureAwait(false));
            JsonElement obj = ready.RootElement;
            if (obj.GetProperty("type").GetString() != "ready" || obj.GetProperty("threads").GetInt32() != 1) throw new InvalidDataException();
            JsonElement ids = obj.GetProperty("identities");
            foreach (var pin in Pins) if (ids.GetProperty(pin.Key).GetString() != pin.Value) throw new InvalidDataException();
            JsonElement budget = obj.GetProperty("cpu_budget");
            ValidateBudget(budget);
            worker.budgetJson = budget.GetRawText();
            worker.StartupMilliseconds = startup.Elapsed.TotalMilliseconds;
            return worker;
        }
        catch
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { }
                process.Dispose();
            }
            foreach (FileStream file in held) file.Dispose();
            throw;
        }
    }

    public async Task<IMiragePairedWorker.Reply> ReadAsync(byte[] encoded, int width, int height, CancellationToken token)
    {
        try
        {
            var (image, _, _) = Program.Decode(encoded, [0, 0, width, height], token);
            byte[] rgb = image.BgrPixels?.Pixels.ToArray() ?? throw new InvalidDataException();
            if (rgb.Length != checked(width * height * 3)) throw new InvalidDataException();
            for (int i = 0; i < rgb.Length; i += 3) (rgb[i], rgb[i + 2]) = (rgb[i + 2], rgb[i]);
            string id = (++nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string request = JsonSerializer.Serialize(new { request_id = id, width, height, rgb_base64 = Convert.ToBase64String(rgb) });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(await ReadLineAsync(token).ConfigureAwait(false));
            JsonElement obj = document.RootElement;
            if (obj.GetProperty("request_id").GetString() != id || !obj.GetProperty("requires_review").GetBoolean() ||
                obj.GetProperty("cpu_budget").GetRawText() != budgetJson) throw new InvalidDataException();
            double elapsed = obj.GetProperty("elapsed_ms").GetDouble();
            string hash = obj.GetProperty("raw_sha256").GetString() ?? "";
            if (!double.IsFinite(elapsed) || elapsed < 0 || hash.Length != 64 || !hash.All(Uri.IsHexDigit) ||
                obj.GetProperty("raw_bytes").GetInt32() is < 0 or > MaximumRawBytes) throw new InvalidDataException();
            JsonElement error = obj.GetProperty("parse_error");
            string? parseError = error.ValueKind == JsonValueKind.Null ? null : error.GetString();
            JsonElement repairs = obj.GetProperty("repairs");
            if (repairs.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            JsonElement rows = obj.GetProperty("text_rows");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 128) throw new InvalidDataException();
            var result = new List<MirageComparisonMapping.Row>();
            foreach (JsonElement row in rows.EnumerateArray())
            {
                JsonElement box = row.GetProperty("bbox");
                if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4) throw new InvalidDataException();
                JsonElement score = row.GetProperty("score");
                result.Add(new(row.GetProperty("string").GetString() ?? throw new InvalidDataException(),
                    box.EnumerateArray().Select(value => value.GetDouble()).ToArray(),
                    score.ValueKind == JsonValueKind.Null ? null : score.GetDouble()));
            }
            return new(obj.GetProperty("status").GetInt32(), elapsed, obj.GetProperty("strict_json_valid").GetBoolean(),
                parseError, repairs.GetArrayLength(), true, result);
        }
        catch { usable = false; try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } throw; }
    }

    private static void ValidateBudget(JsonElement value)
    {
        int logical = value.GetProperty("logical_processors").GetInt32(), rate = value.GetProperty("rate").GetInt32();
        double maximum = value.GetProperty("maximum_logical_cores").GetDouble();
        if (logical <= 0 || rate <= 0 || rate != 5000 / logical || !double.IsFinite(maximum) ||
            maximum > .5 || Math.Abs(maximum - logical * rate / 10000d) > 1e-9 ||
            value.GetProperty("flags").GetInt32() != 5 || !value.GetProperty("verified").GetBoolean() ||
            value.GetProperty("priority_class").GetString() != "BelowNormal") throw new InvalidDataException();
    }

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (offset == available) { available = await process.StandardOutput.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); offset = 0; }
            if (available == 0) throw new EndOfStreamException();
            int end = Array.IndexOf(buffer, '\n', offset, available - offset);
            int length = (end < 0 ? available : end) - offset;
            if (line.Length + length > MaximumResponseCharacters) throw new InvalidDataException();
            line.Append(buffer, offset, length); offset += length;
            if (end >= 0) { offset++; return line.ToString(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        process.Dispose();
        foreach (FileStream file in held) file.Dispose();
    }
}
