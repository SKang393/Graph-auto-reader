# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory)][string]$Path)

    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point is not permitted: $current"
            }
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-RequiredEnvironmentValue {
    param([Parameter(Mandatory)][string]$Name)

    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Required process environment variable is missing: $Name" }
    return $value
}

function Read-ZipJson {
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)

    if ($Entry.Length -gt 1MB) { throw "ZIP metadata is too large: $($Entry.FullName)" }
    $stream = $Entry.Open()
    try {
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $false)
        try { return ConvertFrom-Json -InputObject $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-ZipEntrySha256 {
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)

    $stream = $Entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Assert-SafeZipEntries {
    param([Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive)

    if ($Archive.Entries.Count -gt 50000) { throw 'Preview ZIP contains too many entries.' }
    $totalLength = [long]0
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $forbiddenExtensions = @(
        '.dig', '.garproj', '.csv', '.xlsx', '.pdf', '.png', '.jpg', '.jpeg', '.bmp', '.tif', '.tiff',
        '.cs', '.csproj', '.sln', '.slnx', '.cpp', '.c', '.h', '.hpp',
        '.ps1', '.psm1', '.py', '.pdb', '.key', '.pem', '.pfx', '.p12', '.snk', '.ppk',
        '.cmake', '.bat', '.cmd', '.sh', '.js', '.ts', '.html', '.css', '.log', '.trx'
    )

    foreach ($entry in $Archive.Entries) {
        $name = [string]$entry.FullName
        if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or
            $name -match '^[A-Za-z]:' -or $name.Contains([char]0)) {
            throw "Unsafe ZIP entry path: $name"
        }
        $segments = @($name.TrimEnd('/').Split('/'))
        if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
            throw "Unsafe ZIP entry path: $name"
        }
        foreach ($segment in $segments) {
            if ($segment.EndsWith('.') -or $segment.EndsWith(' ') -or
                $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' -or
                $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
                throw "ZIP entry path is unsafe on Windows: $name"
            }
        }
        if (-not $seen.Add($name)) { throw "Duplicate ZIP entry path: $name" }

        $attributeBits = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$entry.ExternalAttributes), 0)
        $unixType = ($attributeBits -shr 16) -band 0xF000
        if ($unixType -eq 0xA000) { throw "ZIP symbolic links are not permitted: $name" }
        $isDirectory = $name.EndsWith('/')
        if (-not $isDirectory) {
            $totalLength += [long]$entry.Length
            if ($totalLength -gt 4GB) { throw 'Preview ZIP expands beyond the 4 GiB validation limit.' }
            $leaf = $segments[-1]
            $extension = [IO.Path]::GetExtension($leaf)
            if ($extension -in $forbiddenExtensions) {
                throw "Forbidden project, source, debug, or key file in preview ZIP: $name"
            }
            if ($leaf -match '^(?i:.*\.(autosave\.)?garproj)$') {
                throw "User project data is not permitted in preview ZIP: $name"
            }
            if ($leaf -match '^(?i:id_rsa|id_ed25519|credentials\.json|secrets\.json|\.env)$' -or
                $leaf -match '(?i)attestation') {
                throw "Private key or credential file is not permitted in preview ZIP: $name"
            }
        }
        if ($segments -contains 'Data' -or $segments -contains 'provenance-private' -or
            $segments -contains '.git' -or $segments -contains 'private' -or
            @($segments | Where-Object { $_ -match '(?i)provenance|private' }).Count -gt 0 -or
            $segments -contains 'evidence' -or $segments -contains 'artifacts' -or
            $segments -contains 'sources' -or $segments -contains 'source' -or
            $segments -contains 'src' -or $segments -contains 'build' -or
            $segments -contains 'builds' -or $segments -contains 'datasets' -or
            $segments -contains 'test-results') {
            throw "Mutable user data or private provenance is not permitted in preview ZIP: $name"
        }
    }
}

$root = [IO.Path]::GetFullPath((Get-RequiredEnvironmentValue 'GRAPHREADER_INTERNAL_UPLOAD_ROOT'))
$expectedWorkflowCommit = (Get-RequiredEnvironmentValue 'GRAPHREADER_INTERNAL_UPLOAD_WORKFLOW_SHA').ToLowerInvariant()
$expectedSourceCommit = (Get-RequiredEnvironmentValue 'GRAPHREADER_INTERNAL_UPLOAD_SOURCE_SHA').ToLowerInvariant()
$expectedVersion = Get-RequiredEnvironmentValue 'GRAPHREADER_INTERNAL_UPLOAD_VERSION'
$expectedPackageHash = (Get-RequiredEnvironmentValue 'GRAPHREADER_INTERNAL_UPLOAD_PACKAGE_SHA256').ToLowerInvariant()
$runnerLabel = Get-RequiredEnvironmentValue 'GRAPHREADER_INTERNAL_UPLOAD_RUNNER_LABEL'
$runnerTemp = [IO.Path]::GetFullPath((Get-RequiredEnvironmentValue 'RUNNER_TEMP'))

if ($expectedWorkflowCommit -cnotmatch '^[0-9a-f]{40}$' -or $expectedSourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
    $expectedWorkflowCommit -cne $expectedSourceCommit) {
    throw 'Expected source SHA must be the full commit SHA that supplied this main-branch workflow.'
}
$null = ConvertTo-GraphReaderVersion -Version $expectedVersion
if ($expectedPackageHash -cnotmatch '^[0-9a-f]{64}$') { throw 'Expected package SHA-256 must be 64 lowercase hexadecimal characters.' }
if ($runnerLabel -cnotmatch '^graphreader-upload-[a-z0-9][a-z0-9-]{7,47}$') {
    throw 'Runner label must begin graphreader-upload- and use a task-specific lowercase suffix.'
}
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Configured internal upload root is missing: $root" }
Assert-NoReparseAncestors $root
Assert-NoReparseAncestors $runnerTemp

$zipFiles = @(Get-ChildItem -LiteralPath $root -File -Filter '*.zip')
$expectedName = "GraphAutoReader-$expectedVersion-win-x64-internal-preview.zip"
if ($zipFiles.Count -ne 1 -or $zipFiles[0].Name -cne $expectedName) {
    throw "Upload root must contain exactly one ZIP named '$expectedName'."
}
Assert-NoReparseAncestors $zipFiles[0].FullName

$stageDirectory = Join-Path $runnerTemp 'graphreader-internal-preview'
if (-not $stageDirectory.StartsWith($runnerTemp.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verified upload staging path escaped RUNNER_TEMP.'
}
if (Test-Path -LiteralPath $stageDirectory) { throw "Preview upload staging directory already exists: $stageDirectory" }
New-Item -ItemType Directory -Path $stageDirectory | Out-Null
$stagedZip = Join-Path $stageDirectory $expectedName
try {
    $sourceStream = [IO.File]::Open($zipFiles[0].FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $stageStream = [IO.File]::Open($stagedZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $sourceStream.CopyTo($stageStream) }
        finally { $stageStream.Dispose() }
    }
    finally { $sourceStream.Dispose() }

    if ((Get-Sha256 $stagedZip) -cne $expectedPackageHash) { throw 'Staged preview ZIP SHA-256 does not match the dispatch input.' }
    $fileStream = [IO.File]::OpenRead($stagedZip)
    try {
        $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            Assert-SafeZipEntries $archive
            $byName = @{}
            foreach ($entry in $archive.Entries) { $byName[$entry.FullName] = $entry }
            foreach ($required in @('build-info.json', 'reviewed-opencv-runtime.json', 'portable.mode',
                    'GraphReader.App.exe', 'GraphReader.App.dll', 'GraphReader.App.deps.json', 'GraphReader.App.runtimeconfig.json')) {
                if (-not $byName.ContainsKey($required)) { throw "Preview ZIP is missing root metadata: $required" }
            }
            $appEntries = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)GraphReader\.App\.exe$' })
            $nativeEntries = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)OpenCvSharpExtern\.dll$' })
            if ($appEntries.Count -ne 1 -or $nativeEntries.Count -ne 1) {
                throw 'Preview ZIP must contain exactly one application executable and one OpenCvSharp native runtime.'
            }

            $buildInfo = Read-ZipJson $byName['build-info.json']
            $nativeMetadata = Read-ZipJson $byName['reviewed-opencv-runtime.json']
            if ($buildInfo.schemaVersion -ne 2 -or $buildInfo.version -cne $expectedVersion -or
                $buildInfo.commit -cne $expectedSourceCommit -or $buildInfo.dirty -isnot [bool] -or
                $buildInfo.dirty -ne $false -or $buildInfo.runtimeMode -cne 'ManualPreview') {
                throw 'build-info.json must bind the expected clean ManualPreview version and source commit.'
            }
            $appHash = Get-ZipEntrySha256 $appEntries[0]
            if ([string]$buildInfo.executableSha256 -cne $appHash) { throw 'Application executable hash differs from build-info.json.' }

            $nativeHash = Get-ZipEntrySha256 $nativeEntries[0]
            $runtime = $buildInfo.openCvRuntime
            if ($nativeMetadata.schema -cne 'graphreader.reviewed-opencv-runtime.v1' -or
                $nativeMetadata.runtimeId -cne $runtime.runtimeId -or
                [string]$nativeMetadata.binarySha256 -cne $nativeHash -or
                [string]$runtime.binarySha256 -cne $nativeHash -or
                $nativeMetadata.provenanceValidated -isnot [bool] -or $nativeMetadata.provenanceValidated -ne $true -or
                $runtime.provenanceValidated -isnot [bool] -or $runtime.provenanceValidated -ne $true -or
                $nativeMetadata.cleanMachineEvidence -isnot [bool] -or $nativeMetadata.cleanMachineEvidence -ne $false -or
                $runtime.cleanMachineEvidence -isnot [bool] -or $runtime.cleanMachineEvidence -ne $false -or
                $nativeMetadata.noticeReviewStatus -cne 'complete' -or
                $nativeMetadata.maintainerAttestationStatus -cne 'recorded-private' -or
                $runtime.noticeReviewStatus -cne $nativeMetadata.noticeReviewStatus -or
                $runtime.maintainerAttestationStatus -cne $nativeMetadata.maintainerAttestationStatus -or
                $nativeMetadata.releaseApproved -isnot [bool] -or $nativeMetadata.releaseApproved -ne $false -or
                $runtime.releaseApproved -isnot [bool] -or $runtime.releaseApproved -ne $false) {
                throw 'Reviewed native runtime bytes or nonproduction flags differ from build metadata.'
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $fileStream.Dispose() }

    Write-Output $stagedZip
}
catch {
    if (Test-Path -LiteralPath $stageDirectory -PathType Container) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force
    }
    throw
}
