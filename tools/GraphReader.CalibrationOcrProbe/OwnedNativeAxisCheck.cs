// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.Services;
using GraphReader.Axis;

namespace GraphReader.CalibrationOcrProbe;

internal static class OwnedNativeAxisCheck
{
    internal static async Task<object> RunAsync(string app, string rust, CpuBudgetJob budget, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(app) || !File.Exists(app) || !Path.IsPathFullyQualified(rust) || !File.Exists(rust))
            throw new ArgumentException("Absolute existing executables required.");
        var native = new OpenCvAxisProposalService(app);
        var combined = new RustFirstAxisProposalService(new RustAxisProposalService(rust), native);
        using var productionInput = new MemoryStream([1]);
        using var productionOutput = new MemoryStream();
        int productionExit = await NativeAxisWorker.RunAsync(WorkflowRuntimeEnvironment.Production, productionInput, productionOutput);
        Require(productionExit == 2 && productionInput.Position == 0 && productionOutput.Length == 0);
        var results = new List<object>();
        foreach (string name in new[] { "clean", "padded", "connected_corner", "empty_corner", "two_panels", "blank" })
        {
            var f = OwnedAxisBackendCheck.Fixture(name);
            byte[] image = Encode(f.Width, f.Height, f.Pixels);
            var timer = Stopwatch.StartNew();
            RustAxisProposalResult result = await combined.SuggestAsync(image, token);
            if (name is "clean" or "connected_corner") Require(result.Geometry is not null && result.Provider == "rust");
            else if (name == "padded") Require(result.Geometry is not null && result.Provider == "opencv-review" &&
                result.Geometry.XTickPositions.Count == 0 && result.Geometry.YTickPositions.Count == 0 && result.ReviewReasons.Count > 0);
            else if (name == "two_panels") Require(result.Geometry is null && result.ErrorCode == "AMBIGUOUS_AXIS" && result.Provider == "rust");
            else Require(result.Geometry is null);
            results.Add(new { name, milliseconds = timer.Elapsed.TotalMilliseconds, result });
        }
        var two = OwnedAxisBackendCheck.Fixture("two_panels");
        RustAxisProposalResult direct = await native.SuggestAsync(Encode(two.Width, two.Height, two.Pixels), token);
        Require(direct.Geometry is null);

        using Process malformed = Start(app);
        using var malformedDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        malformedDeadline.CancelAfter(TimeSpan.FromSeconds(16));
        using var malformedStop = malformedDeadline.Token.Register(() => Kill(malformed));
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, -1);
        await malformed.StandardInput.BaseStream.WriteAsync(header, malformedDeadline.Token); malformed.StandardInput.Close();
        byte[] responseHeader = new byte[4];
        await malformed.StandardOutput.BaseStream.ReadExactlyAsync(responseHeader, malformedDeadline.Token);
        int responseLength = BinaryPrimitives.ReadInt32LittleEndian(responseHeader);
        Require(responseLength is > 0 and <= 4096);
        byte[] responseBytes = new byte[responseLength];
        await malformed.StandardOutput.BaseStream.ReadExactlyAsync(responseBytes, malformedDeadline.Token);
        await malformed.WaitForExitAsync(malformedDeadline.Token);
        using JsonDocument malformedJson = JsonDocument.Parse(responseBytes);
        Require(malformed.ExitCode == 0 && malformedJson.RootElement.GetProperty("Geometry").ValueKind == JsonValueKind.Null &&
            malformedJson.RootElement.GetProperty("ErrorCode").GetString() == "OPENCV_AXIS_UNAVAILABLE");

        using Process idle = Start(app);
        var idleWatch = Stopwatch.StartNew();
        using (var idleDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            idleDeadline.CancelAfter(TimeSpan.FromSeconds(16));
            try { await idle.WaitForExitAsync(idleDeadline.Token); }
            catch (OperationCanceledException) { Kill(idle); throw; }
        }
        idleWatch.Stop();
        Require(idle.ExitCode == 3);

        const int side = 2048;
        byte[] noise = new byte[side * side]; uint state = 393;
        for (int i = 0; i < noise.Length; i++) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; noise[i] = (byte)state; }
        byte[] noisyPng = Encode(side, side, noise);
        HashSet<int> before = MatchingProcesses(app);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<RustAxisProposalResult> pending = native.SuggestAsync(noisyPng, cancel.Token);
        Process? observed = null;
        var observation = Stopwatch.StartNew();
        while (observation.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted && observed is null)
        {
            int[] fresh = MatchingProcesses(app).Except(before).ToArray();
            if (fresh.Length > 1) throw new InvalidOperationException("More than one matching child; cannot identify it safely.");
            if (fresh.Length == 1) observed = Process.GetProcessById(fresh[0]);
            else await Task.Delay(10, token);
        }
        if (observed is null) { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } throw new InvalidOperationException("No child was observed before cancellation."); }
        observation.Stop();
        using (observed)
        {
            long? memory = null;
            try { observed.Refresh(); memory = observed.PeakWorkingSet64; } catch (InvalidOperationException) { }
            var cancellationWatch = Stopwatch.StartNew();
            cancel.Cancel();
            bool cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            bool exitedAtCompletion = observed.HasExited;
            Require(cancelled && exitedAtCompletion);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(3));
            await observed.WaitForExitAsync(wait.Token);
            cancellationWatch.Stop();
            Require(cancelled && observed.HasExited);
            var clean = OwnedAxisBackendCheck.Fixture("clean");
            RustAxisProposalResult restarted = await native.SuggestAsync(Encode(clean.Width, clean.Height, clean.Pixels), token);
            Require(restarted.Geometry is not null && restarted.Provider == "opencv-review");
            return new { review_only = true, accuracy_claim = false, production_guard = "in_process_guard_passed_no_packaged_production_binary",
                budget.LogicalProcessors, budget.Rate, budget.MaximumLogicalCores,
                app_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(app))),
                rust_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(rust))), cases = results,
                direct_two_panel_rejected = true, malformed_exit = malformed.ExitCode, idle_exit = idle.ExitCode,
                idle_milliseconds = idleWatch.Elapsed.TotalMilliseconds, cancelled_child_exited_at_completion = exitedAtCompletion,
                restart_succeeded = true,
                cancellation_observed_after_ms = observation.Elapsed.TotalMilliseconds, sampled_peak_working_set = memory,
                cancellation_to_exit_ms = cancellationWatch.Elapsed.TotalMilliseconds,
                cancellation_note = "Cancellation at observed startup does not prove cancellation during OpenCV computation.",
                memory_note = "Startup sample, not peak processing memory or a hard bound." };
        }
    }

    private static byte[] Encode(int width, int height, byte[] pixels)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static Process Start(string app)
    {
        var info = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--native-axis-worker");
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start owned worker.");
    }
    private static HashSet<int> MatchingProcesses(string app)
    {
        var result = new HashSet<int>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app)))
        {
            using (process) { try { if (string.Equals(process.MainModule?.FileName, app, StringComparison.OrdinalIgnoreCase) &&
                NtQueryInformationProcess(process.Handle, 0, out ProcessBasicInformation info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 &&
                info.ParentProcessId.ToInt64() == Environment.ProcessId) result.Add(process.Id); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        }
        return result;
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Owned native-axis check failed."); }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnedLength);
}
