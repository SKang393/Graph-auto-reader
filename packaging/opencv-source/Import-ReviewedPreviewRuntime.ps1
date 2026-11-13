# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
if (-not (Test-Path -LiteralPath $DestinationRoot -PathType Container)) { throw 'Preview runtime destination must already exist.' }
$policy = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'packaging\opencv-source\review\source-build-review-policy.json') -Raw | ConvertFrom-Json
$ledger = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'docs\BUILD_LEDGER.json') -Raw | ConvertFrom-Json
if ($ledger.schemaVersion -ne 1 -or $policy.schemaVersion -ne 1 -or
    $policy.overallReviewStatus -cne 'reviewed-provenance-only' -or
    $policy.noticeBundle.reviewStatus -cne 'complete' -or
    $policy.microsoftStaticRuntimeAttestation.status -cne 'recorded-private') { throw 'Preview runtime ledger or review policy is invalid.' }
$stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $archiveHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
    $ledgerMatches = @($ledger.builds | ForEach-Object { $build = $_; if (-not $build.PSObject.Properties['packages']) { return }; foreach ($package in @($build.packages)) {
        if ([string]$package.sha256 -ceq $archiveHash -and [string]$package.name -ceq [IO.Path]::GetFileName($ArchivePath)) { [pscustomobject]@{ Build = $build; Package = $package } }
    } })
    if ($ledgerMatches.Count -ne 1) { throw 'Preview archive must match exactly one package name and SHA-256 in the tracked build ledger.' }
    $build = $ledgerMatches[0].Build
    if ([string]$build.releaseStatus -cne 'internal' -or $build.recordIncomplete -isnot [bool] -or $build.recordIncomplete -ne $false -or
        [string]$build.openCvRuntimeSha256 -cne [string]$policy.binarySha256 -or
        [string]$build.commit -notmatch '^[0-9a-f]{40}$') { throw 'Preview ledger identity or OpenCV runtime is invalid.' }
    $stream.Position = 0
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name.Contains('\') -or $name.StartsWith('/') -or $name -match '(^|/)(\.|\.\.|)(/|$)' -or $name.Contains(':')) { throw "Unsafe preview archive entry: $name" }
            if ($entries.ContainsKey($name)) { throw "Duplicate preview archive entry: $name" }
            $entries.Add($name, $entry)
        }
        $required = @('build-info.json', 'reviewed-opencv-runtime.json', 'OpenCvSharpExtern.dll', 'LICENSES/OpenCV-4.13.0-License.txt', 'LICENSES/OpenCvSharp-4.13.0.20260627-License.txt', 'LICENSES/OpenCvSharpExtern-SourceBuilt-ThirdPartyNotices.txt', 'LICENSES/Microsoft-Visual-Cpp-2022-Redistribution-Reference.md')
        foreach ($name in $required) { if (-not $entries.ContainsKey($name) -or $entries[$name].Length -le 0) { throw "Required preview archive entry missing: $name" } }
        function Read-JsonEntry($entry) {
            if ($entry.Length -gt 1048576) { throw 'Preview metadata exceeds size limit.' }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
        }
        $info = Read-JsonEntry $entries['build-info.json']
        $runtime = Read-JsonEntry $entries['reviewed-opencv-runtime.json']
        if ($info.schemaVersion -ne 2 -or [string]$info.version -cne [string]$build.version -or
            [string]$info.commit -cne [string]$build.commit -or $info.dirty -isnot [bool] -or $info.dirty -ne $false -or
            [string]$info.runtimeMode -cne 'ManualPreview' -or [string]$info.executableSha256 -cne [string]$build.executableSha256) { throw 'Preview build identity does not match the ledger.' }
        foreach ($item in @($runtime, $info.openCvRuntime)) {
            if ([string]$item.schema -cne 'graphreader.reviewed-opencv-runtime.v1' -or
                [string]$item.runtimeId -cne 'opencvsharpextern-source-audited' -or
                [string]$item.profileId -cne [string]$policy.profileId -or
                [string]$item.binarySha256 -cne [string]$policy.binarySha256 -or
                $item.provenanceValidated -isnot [bool] -or $item.provenanceValidated -ne $true -or
                [string]$item.noticeReviewStatus -cne 'complete' -or [string]$item.maintainerAttestationStatus -cne 'recorded-private' -or
                $item.cleanMachineEvidence -isnot [bool] -or $item.cleanMachineEvidence -ne $false -or
                $item.releaseApproved -isnot [bool] -or $item.releaseApproved -ne $false) { throw 'Preview OpenCV review metadata is invalid.' }
            foreach ($component in @('openCvSharp', 'openCv', 'vcpkg')) {
                if ([string]$item.sourceRevisions.$component -cne [string]$policy.sourceRevisions.$component) { throw 'Preview OpenCV source revision mismatch.' }
            }
        }
        $dll = $entries['OpenCvSharpExtern.dll'].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $dllHash = ([BitConverter]::ToString($sha.ComputeHash($dll))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose(); $dll.Dispose() }
        if ($dllHash -cne [string]$policy.binarySha256) { throw 'Preview OpenCV DLL hash does not match reviewed policy.' }
        foreach ($dir in @($DestinationRoot, (Join-Path $DestinationRoot 'LICENSES'))) {
            if (Test-Path -LiteralPath $dir) { if (((Get-Item -LiteralPath $dir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Preview destination rejects reparse points.' } }
            else { New-Item -ItemType Directory -Path $dir | Out-Null }
        }
        foreach ($name in $required | Where-Object { $_ -ne 'build-info.json' }) {
            $target = Join-Path $DestinationRoot $name.Replace('/', '\')
            if ((Test-Path -LiteralPath $target) -and (((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Preview destination rejects reparse points.' }
            $inputStream = $entries[$name].Open()
            try { $output = [IO.File]::Open($target, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None); try { $inputStream.CopyTo($output) } finally { $output.Dispose() } } finally { $inputStream.Dispose() }
        }
        $lineage = [ordered]@{ schema = 'graphreader.preview-opencv-lineage.v1'; sourceVersion = [string]$build.version; sourceCommit = [string]$build.commit; archiveName = [string]$ledgerMatches[0].Package.name; archiveSha256 = $archiveHash; binarySha256 = $dllHash; cleanMachineEvidence = $false; releaseApproved = $false }
        $lineagePath = Join-Path $DestinationRoot 'reviewed-opencv-preview-lineage.json'
        if ((Test-Path -LiteralPath $lineagePath) -and (((Get-Item -LiteralPath $lineagePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Preview destination rejects reparse points.' }
        [IO.File]::WriteAllText($lineagePath, ($lineage | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Write-Host "Reviewed preview OpenCV imported: $dllHash"
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
