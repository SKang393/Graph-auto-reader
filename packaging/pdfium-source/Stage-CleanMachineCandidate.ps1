# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
<# Stages reviewed bytes for an isolated clean-machine candidate only. This is
not release staging, and does not run a build or alter a release gate. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$ExpectedExecutableSha256,
    [Parameter(Mandatory)][string]$ExpectedApplicationDllSha256,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$reviewedApprovalSha256 = '40f47b14c42748b8fb43d0905d1694746186d6b9018d5bce4c97a93305836936'
$sourceProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$CandidateRoot = [IO.Path]::GetFullPath($CandidateRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts'))

function Assert-Contained([string]$Parent, [string]$Path, [string]$Label) {
    $prefix = $Parent.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be strictly inside $Parent."
    }
    $current = $Path
    while ($current.Length -ge $Parent.Length) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label cannot contain a reparse point."
            }
        }
        if ($current.Equals($Parent, [StringComparison]::OrdinalIgnoreCase)) { return }
        $current = Split-Path -Parent $current
    }
    throw "$Label containment check failed."
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ValidHash([string]$Value) { $Value -cmatch '^[0-9a-f]{64}$' }
if (-not $RepositoryRoot.Equals($sourceProjectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Assert-Contained $sourceProjectRoot $RepositoryRoot 'Repository root'
}
if (-not (Test-Path -LiteralPath $CandidateRoot -PathType Container) -or
    -not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) {
    throw 'Existing candidate and evidence directories are required.'
}
Assert-Contained $artifactsRoot $CandidateRoot 'Candidate root'
Assert-Contained $artifactsRoot $EvidenceRoot 'Evidence root'
$candidatePrefix = $CandidateRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$evidencePrefix = $EvidenceRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if ($CandidateRoot.Equals($EvidenceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $CandidateRoot.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $EvidenceRoot.StartsWith($candidatePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Candidate and evidence directories must be disjoint.'
}
if (-not (ValidHash $ExpectedExecutableSha256) -or -not (ValidHash $ExpectedApplicationDllSha256)) {
    throw 'Expected application hashes must be lowercase SHA-256 values.'
}
$exe = Join-Path $CandidateRoot 'GraphReader.App.exe'
$dll = Join-Path $CandidateRoot 'GraphReader.App.dll'
foreach ($path in @($exe, $dll)) {
    Assert-Contained $CandidateRoot $path 'Application input'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Application file missing: $path" }
}
if ((Hash $exe) -cne $ExpectedExecutableSha256 -or (Hash $dll) -cne $ExpectedApplicationDllSha256) {
    throw 'Candidate application bytes do not match the caller-pinned hashes.'
}
if (Test-Path -LiteralPath (Join-Path $CandidateRoot 'portable.mode')) {
    throw 'A portable preview payload cannot be used as a clean-machine production candidate.'
}
$target = Join-Path $CandidateRoot 'pdfium'
$receiptPath = Join-Path $CandidateRoot 'pdfium-clean-machine-candidate.json'
if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $receiptPath)) {
    throw 'Candidate PDFium target and receipt must not already exist.'
}
$approvalPath = Join-Path $EvidenceRoot 'reviewed-approval.json'
Assert-Contained $EvidenceRoot $approvalPath 'PDFium approval'
if (-not (Test-Path -LiteralPath $approvalPath -PathType Leaf)) { throw 'Reviewed approval is missing.' }
$approvalHash = Hash $approvalPath
if ($approvalHash -cne $reviewedApprovalSha256) {
    throw 'Reviewed PDFium approval does not match the independently pinned approval SHA-256.'
}
$approval = Get-Content -LiteralPath $approvalPath -Raw | ConvertFrom-Json
foreach ($field in @('reviewApproved','redistributionApproved','bundlingApproved')) {
    if ($approval.$field -isnot [bool] -or $approval.$field -ne $true) { throw "Approval field $field is not true." }
}
if ($approval.schemaVersion -ne 1 -or $approval.rendererId -cne 'graphreader-pdfium-renderer' -or
    $approval.rendererVersion -cne '2870fa9244b0f0f69fb743fab1e08deefcb07b2b' -or
    $approval.sourceRevision -cne '2870fa9244b0f0f69fb743fab1e08deefcb07b2b' -or
    $approval.source -cne 'https://pdfium.googlesource.com/pdfium' -or $approval.licenseSpdx -cne 'BSD-3-Clause') {
    throw 'Reviewed PDFium approval identity differs.'
}
$auditPath = Join-Path $RepositoryRoot 'packaging\common\release-audit.json'
$audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
$components = @($audit.components | Where-Object { $_.id -ceq 'pdfium-native' })
if ($components.Count -ne 1 -or $components[0].reviewStatus -cne 'reviewed' -or
    $components[0].checksumPolicy -cne 'exact-binary' -or
    $components[0].artifactSha256 -cne $approval.binarySha256) {
    throw 'PDFium approval does not match the reviewed audit component.'
}
$resources = @(
    @{ Path = $approval.binaryPath; Hash = $approval.binarySha256 },
    @{ Path = $approval.sourceLockPath; Hash = $approval.sourceLockSha256 },
    @{ Path = $approval.buildManifestPath; Hash = $approval.buildManifestSha256 },
    @{ Path = $approval.noticePath; Hash = $approval.noticeSha256 }
)
$selected = @{}
foreach ($resource in $resources) {
    $relative = [string]$resource.Path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains(':') -or [IO.Path]::IsPathRooted($relative) -or
        @($relative -split '[\\/]' | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
        -not (ValidHash ([string]$resource.Hash))) { throw 'Invalid approved PDFium resource path or hash.' }
    $source = [IO.Path]::GetFullPath((Join-Path $EvidenceRoot $relative))
    Assert-Contained $EvidenceRoot $source 'PDFium resource'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Hash $source) -cne $resource.Hash) {
        throw "Approved PDFium resource missing or changed: $relative"
    }
    if ($relative -ieq 'reviewed-approval.json' -or $selected.ContainsKey($relative.ToLowerInvariant())) {
        throw 'PDFium resource paths must be distinct from each other and the approval.'
    }
    $selected[$relative.ToLowerInvariant()] = $true
    $resource.Source = $source
}
if ((Get-Content -LiteralPath (Join-Path $EvidenceRoot $approval.noticePath) -TotalCount 1).Trim() -cne 'REVIEW STATUS: COMPLETE') {
    throw 'PDFium notice review is incomplete.'
}
# Leave partial staging in place on failure for inspection. Never touch release
# audit gates or mark this candidate as release approved.
New-Item -ItemType Directory -Path $target | Out-Null
foreach ($resource in $resources) {
    $destination = [IO.Path]::GetFullPath((Join-Path $target $resource.Path))
    Assert-Contained $target $destination 'Candidate resource'
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    $inputStream = [IO.File]::Open($resource.Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $outputStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
    if ((Hash $destination) -cne $resource.Hash) { throw 'Candidate PDFium resource changed during copy.' }
}
$approvalDestination = Join-Path $target 'reviewed-approval.json'
$inputStream = [IO.File]::Open($approvalPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $outputStream = [IO.File]::Open($approvalDestination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
} finally { $inputStream.Dispose() }
if ((Hash (Join-Path $target 'reviewed-approval.json')) -cne $approvalHash -or
    (Hash $exe) -cne $ExpectedExecutableSha256 -or (Hash $dll) -cne $ExpectedApplicationDllSha256) {
    throw 'Candidate application or approval changed during staging.'
}
$receipt = [ordered]@{ schema = 'graphreader.pdfium-clean-machine-candidate.v1'; status = 'diagnostic-only';
    releaseApproved = $false; cleanMachineEvidence = $false; applicationExecutableSha256 = $ExpectedExecutableSha256;
    applicationDllSha256 = $ExpectedApplicationDllSha256; rendererSha256 = [string]$approval.binarySha256;
    approvalSha256 = $approvalHash }
$receiptBytes = [Text.UTF8Encoding]::new($false).GetBytes((($receipt | ConvertTo-Json -Depth 4) + [Environment]::NewLine))
$receiptStream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $receiptStream.Write($receiptBytes, 0, $receiptBytes.Length) } finally { $receiptStream.Dispose() }
Write-Host 'PDFium clean-machine candidate staged. This is not a release package or passing evidence.'
