# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

<#
Runs the existing noninteractive portable smoke on a non-clean Windows host.
The output is development evidence only; this is not clean-VM validation.
The archive hash and executable hash must identify a previously reviewed build.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$ArchiveSha256,
    [Parameter(Mandatory)][string]$ExecutableSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$task = [IO.Path]::GetFullPath($TaskRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$archive = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $task -PathType Container) -or
    -not [string]::Equals([IO.Path]::GetDirectoryName($output), $task, [StringComparison]::OrdinalIgnoreCase) -or
    -not $archive.StartsWith($task + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The archive and new output must be within the supplied task root.'
}
if (Test-Path -LiteralPath $output) { throw 'Output already exists; refusing to modify it.' }
if ((Get-Item -LiteralPath $task -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Task root cannot be a reparse point.'
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $ArchiveSha256) {
    throw 'Preview archive hash mismatch.'
}

function Invoke-Smoke([string]$Executable, [string]$WorkingDirectory) {
    $process = Start-Process -FilePath $Executable -WorkingDirectory $WorkingDirectory `
        -ArgumentList '--portable-smoke' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) {
        $process.Kill()
        throw 'Portable smoke process exceeded 45 seconds.'
    }
    $process.Refresh()
    return $process.ExitCode
}

New-Item -ItemType Directory -Path $output | Out-Null
$portable = Join-Path $output 'portable'
Expand-Archive -LiteralPath $archive -DestinationPath $portable
$executable = Join-Path $portable 'GraphReader.App.exe'
$sentinel = Join-Path $portable 'portable.mode'
if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf) -or
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $ExecutableSha256) {
    throw 'Extracted preview executable or portable sentinel does not match.'
}

# Do not inherit a development override into the tested process.
$env:GRAPHREADER_DEV_PORTABLE_DATA_ROOT = $null
$normalExit = Invoke-Smoke $executable $portable
$data = Join-Path $portable 'Data'
$directoryNames = @('Settings', 'Cache', 'Logs', 'Autosave', 'Recovery')
$created = @($directoryNames | Where-Object { Test-Path -LiteralPath (Join-Path $data $_) -PathType Container })
if ($normalExit -ne 0 -or $created.Count -ne $directoryNames.Count) {
    throw "Normal portable smoke failed: exit=$normalExit, created=$($created.Count)."
}

$originalAcl = Get-Acl -LiteralPath $data
$originalSddl = $originalAcl.Sddl
[IO.File]::WriteAllText((Join-Path $output 'original-data-acl.sddl'), $originalSddl)
$denied = $false
$deniedExit = $null
$restored = $false
try {
    $modified = Get-Acl -LiteralPath $data
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $sid, [Security.AccessControl.FileSystemRights]::Write, $inherit,
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny)
    $modified.AddAccessRule($rule)
    Set-Acl -LiteralPath $data -AclObject $modified
    try {
        [IO.File]::WriteAllText((Join-Path (Join-Path $data 'Settings') 'write-denial-probe.txt'), 'probe')
    }
    catch {
        if ($_.Exception -is [UnauthorizedAccessException] -or
            $_.Exception.InnerException -is [UnauthorizedAccessException]) {
            $denied = $true
        }
        else { throw }
    }
    if (-not $denied) { throw 'The intended write-deny ACL was not effective.' }
    $deniedExit = Invoke-Smoke $executable $portable
}
finally {
    Set-Acl -LiteralPath $data -AclObject $originalAcl
    $restored = (Get-Acl -LiteralPath $data).Sddl -eq $originalSddl
}
if (-not $restored) { throw 'The test directory ACL was not restored to its original descriptor.' }

$report = [ordered]@{
    schema = 'graphreader.portable-host-path-probe.v1'
    archiveSha256 = $ArchiveSha256.ToLowerInvariant()
    executableSha256 = $ExecutableSha256.ToLowerInvariant()
    normalExitCode = $normalExit
    createdDataDirectories = $created
    actualWriteDenied = $denied
    deniedSmokeExitCode = $deniedExit
    dataRootAclRestored = $restored
    cleanWindowsEvidence = $false
    visualMessageObserved = $false
    releaseEligible = $false
    status = if ($deniedExit -eq 2) { 'passed-host-only' } else { 'failed-host-only' }
    limitations = @(
        'Existing Windows host and internal ManualPreview package, not a clean Windows VM or production build.',
        'The smoke exit code does not verify the visible localized read-only message.',
        'The test does not exercise a full import, recognition, review, export, or installer workflow.'
    )
}
$json = $report | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $output 'report.json'), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output $json
if ($deniedExit -ne 2) { exit 1 }
