# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$preflight = Join-Path $projectRoot 'packaging\clean-machine\Test-ValidationHostReadiness.ps1'
$testRoot = Join-Path $projectRoot ('artifacts\test-temp\validation-host-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

$callerTemp = [Environment]::GetEnvironmentVariable('TEMP', 'Process')
$callerTmp = [Environment]::GetEnvironmentVariable('TMP', 'Process')
try {
[Environment]::SetEnvironmentVariable('TEMP', $testRoot, 'Process')
[Environment]::SetEnvironmentVariable('TMP', $testRoot, 'Process')

$source = Join-Path $testRoot 'QemuFixture.cs'
$qemu = Join-Path $testRoot 'qemu-system-x86_64.exe'
$installer = Join-Path $testRoot 'qemu-installer.fixture'
$iso = Join-Path $testRoot 'windows-evaluation.fixture.iso'
[IO.File]::WriteAllText($source, @'
using System;
using System.Diagnostics;
using System.IO;
public static class QemuFixture {
    public static int Main(string[] args) {
        string executable = Process.GetCurrentProcess().MainModule.FileName;
        string directory = Path.GetDirectoryName(executable);
        string mutation = "unexpected-success";
        try {
            using (var stream = File.Open(executable, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete)) stream.WriteByte(0);
        } catch (IOException) { mutation = "denied"; }
          catch (UnauthorizedAccessException) { mutation = "denied"; }
        File.WriteAllText(Path.Combine(directory, "mutation-result.txt"), mutation);
        File.WriteAllText(Path.Combine(directory, "probe-temp.txt"),
            Environment.GetEnvironmentVariable("TEMP") + "|" + Environment.GetEnvironmentVariable("TMP"));
        Console.WriteLine("QEMU emulator version test-fixture");
        return 0;
    }
}
'@)
[IO.File]::WriteAllText($installer, 'synthetic installer fixture')
[IO.File]::WriteAllText($iso, 'synthetic ISO fixture')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$qemu" $source
if ($LASTEXITCODE -ne 0) { throw 'Could not build the synthetic QEMU test fixture.' }
$hash = (Get-FileHash -LiteralPath $iso -Algorithm SHA256).Hash.ToLowerInvariant()
$qemuHash = (Get-FileHash -LiteralPath $qemu -Algorithm SHA256).Hash.ToLowerInvariant()

$previousTemp = [Environment]::GetEnvironmentVariable('TEMP', 'Process')
$previousTmp = [Environment]::GetEnvironmentVariable('TMP', 'Process')
$valid = & $preflight -QemuExecutablePath $qemu -QemuInstallerPath $installer `
    -WindowsIsoPath $iso -ExpectedOfficialIsoSha256 $hash
if ($valid.status -cne 'inputs_checked_manual_verification_needed' -or $valid.releaseEvidence -ne $false -or
    -not $valid.isoMatchesSuppliedChecksum -or $valid.blockers.Count -ne 0 -or
    $valid.qemuVersion -cne 'QEMU emulator version test-fixture' -or
    [Environment]::GetEnvironmentVariable('TEMP', 'Process') -cne $previousTemp -or
    [Environment]::GetEnvironmentVariable('TMP', 'Process') -cne $previousTmp) {
    throw 'Valid synthetic inputs did not produce a clearly non-release host preflight.'
}
if ((Get-Content -LiteralPath (Join-Path $testRoot 'mutation-result.txt') -Raw) -cne 'denied' -or
    (Get-FileHash -LiteralPath $qemu -Algorithm SHA256).Hash.ToLowerInvariant() -cne $qemuHash) {
    throw 'An attempted executable modification was not blocked during the probe.'
}
if ((Get-Content -LiteralPath (Join-Path $testRoot 'probe-temp.txt') -Raw) -cne "$projectRoot|$projectRoot") {
    throw 'The QEMU version probe did not keep its temporary environment in the project.'
}

$mismatch = & $preflight -QemuExecutablePath $qemu -QemuInstallerPath $installer `
    -WindowsIsoPath $iso -ExpectedOfficialIsoSha256 ('0' * 64)
if ($mismatch.status -cne 'blocked' -or $mismatch.isoMatchesSuppliedChecksum -or
    -not ($mismatch.blockers -match 'does not match')) {
    throw 'The preflight failed to reject an incorrect ISO checksum.'
}

$missing = & $preflight -QemuExecutablePath (Join-Path $testRoot 'missing.exe') `
    -QemuInstallerPath $installer -WindowsIsoPath $iso -ExpectedOfficialIsoSha256 $hash
if ($missing.status -cne 'blocked' -or -not ($missing.blockers -match 'QEMU executable.*does not exist')) {
    throw 'The preflight failed to reject a missing QEMU executable.'
}

$outside = & $preflight -QemuExecutablePath $qemu -QemuInstallerPath $installer `
    -WindowsIsoPath (Join-Path $env:WINDIR 'System32\cmd.exe') -ExpectedOfficialIsoSha256 $hash
if ($outside.status -cne 'blocked' -or -not ($outside.blockers -match 'Windows evaluation ISO.*project folder')) {
    throw 'The preflight failed to reject an ISO outside the project.'
}

$slowDir = Join-Path $testRoot 'slow'
New-Item -ItemType Directory -Path $slowDir | Out-Null
$slowSource = Join-Path $slowDir 'SlowFixture.cs'
$slowQemu = Join-Path $slowDir 'qemu-system-x86_64.exe'
[IO.File]::WriteAllText($slowSource, @'
using System.Threading;
public static class SlowFixture {
    public static void Main() { Thread.Sleep(30000); }
}
'@)
& $compiler /nologo /target:exe "/out:$slowQemu" $slowSource
if ($LASTEXITCODE -ne 0) { throw 'Could not build the slow QEMU test fixture.' }
$slow = & $preflight -QemuExecutablePath $slowQemu -QemuInstallerPath $installer `
    -WindowsIsoPath $iso -ExpectedOfficialIsoSha256 $hash
if ($slow.status -cne 'blocked' -or -not ($slow.blockers -match 'QEMU version check timed out')) {
    throw 'The preflight failed to stop an unresponsive QEMU version check.'
}

$childDir = Join-Path $testRoot 'inherited-pipe'
New-Item -ItemType Directory -Path $childDir | Out-Null
$childSource = Join-Path $childDir 'ChildFixture.cs'
$childQemu = Join-Path $childDir 'qemu-system-x86_64.exe'
[IO.File]::WriteAllText($childSource, @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class ChildFixture {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct StartupInfo {
        public int cb; public string reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2; public IntPtr reserved2Pointer;
        public IntPtr stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process, thread; public int processId, threadId; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateProcess(string app, StringBuilder command, IntPtr a, IntPtr b,
        bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(IntPtr handle);
    public static int Main(string[] args) {
        if (args.Length > 0 && args[0] == "--child") { Thread.Sleep(30000); return 0; }
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), flags = 0x100,
            stdInput = GetStdHandle(-10), stdOutput = GetStdHandle(-11), stdError = GetStdHandle(-12) };
        ProcessInfo child;
        if (!CreateProcess(exe, new StringBuilder("\"" + exe + "\" --child"), IntPtr.Zero, IntPtr.Zero,
            true, 0x08000000, IntPtr.Zero, null, ref startup, out child)) return 3;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe), "child.pid"), child.processId.ToString());
        CloseHandle(child.thread); CloseHandle(child.process);
        Console.WriteLine("QEMU emulator version inherited-pipe-fixture");
        return 0;
    }
}
'@)
& $compiler /nologo /target:exe "/out:$childQemu" $childSource
if ($LASTEXITCODE -ne 0) { throw 'Could not build the inherited-pipe fixture.' }
$childResult = & $preflight -QemuExecutablePath $childQemu -QemuInstallerPath $installer `
    -WindowsIsoPath $iso -ExpectedOfficialIsoSha256 $hash
if ($childResult.status -cne 'blocked' -or -not ($childResult.blockers -match 'output did not close')) {
    throw 'The preflight failed to bound an inherited output pipe.'
}
$childPid = [int](Get-Content -LiteralPath (Join-Path $childDir 'child.pid') -Raw)
if ($null -ne (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) {
    throw 'The QEMU job left an inherited-pipe child running.'
}

$noisyDir = Join-Path $testRoot 'noisy'
New-Item -ItemType Directory -Path $noisyDir | Out-Null
$noisySource = Join-Path $noisyDir 'NoisyFixture.cs'
$noisyQemu = Join-Path $noisyDir 'qemu-system-x86_64.exe'
[IO.File]::WriteAllText($noisySource, @'
using System;
public static class NoisyFixture {
    public static void Main() {
        Console.WriteLine("QEMU emulator version noisy-fixture");
        Console.Write(new string('x', 20000));
        Console.Error.Write(new string('y', 20000));
    }
}
'@)
& $compiler /nologo /target:exe "/out:$noisyQemu" $noisySource
if ($LASTEXITCODE -ne 0) { throw 'Could not build the noisy QEMU test fixture.' }
$noisy = & $preflight -QemuExecutablePath $noisyQemu -QemuInstallerPath $installer `
    -WindowsIsoPath $iso -ExpectedOfficialIsoSha256 $hash
if ($noisy.status -cne 'blocked' -or -not ($noisy.blockers -match 'output exceeded 16384 characters')) {
    throw 'The preflight failed to reject excessive QEMU output.'
}

Write-Host 'PASS: host preflight validates paths, checksums, QEMU version, and the non-release boundary.'
Write-Host "Synthetic fixtures retained under $testRoot for review."
}
finally {
    [Environment]::SetEnvironmentVariable('TEMP', $callerTemp, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $callerTmp, 'Process')
}
