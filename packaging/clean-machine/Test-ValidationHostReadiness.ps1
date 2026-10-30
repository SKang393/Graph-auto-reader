# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$QemuExecutablePath,
    [Parameter(Mandatory = $true)][string]$QemuInstallerPath,
    [Parameter(Mandatory = $true)][string]$WindowsIsoPath,
    [Parameter(Mandatory = $true)][string]$ExpectedOfficialIsoSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPrefix = $projectRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$blockers = [Collections.Generic.List[string]]::new()

function Get-CheckedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$AllowInstalledProgram
    )

    try {
        if (-not [IO.Path]::IsPathRooted($Path)) {
            throw 'An absolute path is required.'
        }
        $fullPath = [IO.Path]::GetFullPath($Path)
        $allowed = $fullPath.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)
        if ($AllowInstalledProgram -and -not $allowed) {
            foreach ($programRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
                if (-not [string]::IsNullOrWhiteSpace($programRoot)) {
                    $prefix = [IO.Path]::GetFullPath($programRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                    if ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                        $allowed = $true
                    }
                }
            }
        }
        if (-not $allowed) {
            throw 'The file must be in the project folder, or an existing installed program under Program Files.'
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw 'The file does not exist.'
        }
        $item = Get-Item -LiteralPath $fullPath -Force
        if ($item.Length -eq 0) {
            throw 'The file is empty.'
        }
        # Refuse paths containing links rather than silently crossing the project boundary.
        $cursor = $item
        while ($null -ne $cursor) {
            if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The path includes a reparse point.'
            }
            $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
        }
        return [ordered]@{
            path = $fullPath
            bytes = $item.Length
            sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    catch {
        $blockers.Add("${Description}: $($_.Exception.Message)")
        return $null
    }
}

$qemu = Get-CheckedFile -Path $QemuExecutablePath -Description 'QEMU executable' -AllowInstalledProgram
$installer = Get-CheckedFile -Path $QemuInstallerPath -Description 'QEMU installer'
$iso = Get-CheckedFile -Path $WindowsIsoPath -Description 'Windows evaluation ISO'
$expectedHash = $ExpectedOfficialIsoSha256.ToLowerInvariant()
if ($expectedHash -cnotmatch '^[0-9a-f]{64}$') {
    $blockers.Add('The expected official ISO checksum must be a SHA-256 value.')
}
elseif ($null -ne $iso -and $iso.sha256 -cne $expectedHash) {
    $blockers.Add('The Windows ISO checksum does not match the supplied official checksum.')
}

$qemuVersion = $null
if ($null -ne $qemu) {
    $qemuLock = $null
    try {
        if ([IO.Path]::GetFileName($qemu.path) -ine 'qemu-system-x86_64.exe') {
            throw 'The executable must be qemu-system-x86_64.exe.'
        }
        # Keep one read handle open while checking the version. Its sharing
        # mode denies writes and deletion until the probe completes.
        $qemuLock = [IO.File]::Open($qemu.path, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $lockedHash = (Get-FileHash -InputStream $qemuLock -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($lockedHash -cne $qemu.sha256) {
            throw 'The executable changed between the initial hash and the version check.'
        }
        if ($null -eq ('GraphReader.CleanMachine.QemuVersionProbe' -as [type])) {
            # Windows PowerShell may compile Add-Type via temporary files. Keep
            # those inside this project and restore the caller's environment.
            $previousTemp = [Environment]::GetEnvironmentVariable('TEMP', 'Process')
            $previousTmp = [Environment]::GetEnvironmentVariable('TMP', 'Process')
            try {
                [Environment]::SetEnvironmentVariable('TEMP', $projectRoot, 'Process')
                [Environment]::SetEnvironmentVariable('TMP', $projectRoot, 'Process')
                Add-Type -Path (Join-Path $PSScriptRoot 'QemuVersionProbe.cs')
            }
            finally {
                [Environment]::SetEnvironmentVariable('TEMP', $previousTemp, 'Process')
                [Environment]::SetEnvironmentVariable('TMP', $previousTmp, 'Process')
            }
        }
        $qemuVersion = [GraphReader.CleanMachine.QemuVersionProbe]::ReadVersion($qemu.path, $projectRoot, 10000)
        $qemuLock.Position = 0
        $finalHash = (Get-FileHash -InputStream $qemuLock -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($finalHash -cne $qemu.sha256) {
            throw 'The executable changed during the version check.'
        }
        if ([string]::IsNullOrWhiteSpace($qemuVersion) -or
            $qemuVersion -notmatch '^QEMU emulator version ') {
            throw 'QEMU did not report a valid version.'
        }
    }
    catch {
        $qemuVersion = $null
        $blockers.Add("QEMU version: $($_.Exception.Message)")
    }
    finally {
        if ($null -ne $qemuLock) { $qemuLock.Dispose() }
    }
}

if (-not [Environment]::Is64BitOperatingSystem -or
    [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    $blockers.Add('The validation host must run 64-bit Windows.')
}

[pscustomobject]@{
    schema = 'graphreader.clean-machine-host-preflight.v1'
    status = if ($blockers.Count -eq 0) { 'inputs_checked_manual_verification_needed' } else { 'blocked' }
    projectRoot = $projectRoot
    qemuExecutable = $qemu
    qemuInstaller = $installer
    qemuVersion = $qemuVersion
    windowsIso = $iso
    suppliedOfficialIsoSha256 = $expectedHash
    isoMatchesSuppliedChecksum = $null -ne $iso -and $iso.sha256 -ceq $expectedHash
    releaseEvidence = $false
    blockers = $blockers.ToArray()
}
