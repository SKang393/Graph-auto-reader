# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$TagName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
else {
    $RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
}
if ([string]::IsNullOrWhiteSpace($TagName)) {
    $TagName = [Environment]::GetEnvironmentVariable('GITHUB_REF_NAME')
}
if ([string]::IsNullOrWhiteSpace($TagName)) {
    throw 'A release tag name is required.'
}

$version = Get-GraphReaderCentralVersion -RepositoryRoot $RepositoryRoot
$expectedTag = "v$($version.Value)"
if ($TagName -cne $expectedTag) {
    throw "Release tag '$TagName' does not match central version '$expectedTag'."
}
if (-not $version.ReleaseEligible) {
    throw "Version '$($version.Value)' is not eligible. Public releases begin with the 2.0.0 promotion exception and then follow the twentieth-checkpoint cadence."
}

$tagRef = "refs/tags/$TagName"
$tagTypeOutput = @(& git -C $RepositoryRoot cat-file -t $tagRef 2>$null)
$tagTypeExitCode = $LASTEXITCODE
$tagType = $tagTypeOutput | Select-Object -First 1
if ($tagTypeExitCode -ne 0 -or ([string]$tagType).Trim() -cne 'tag') {
    throw "Release tag '$TagName' must be an annotated tag. Resolved object type: '$(([string]$tagType).Trim())'."
}

$tagCommitOutput = @(& git -C $RepositoryRoot rev-parse "$TagName^{commit}" 2>$null)
$tagCommitExitCode = $LASTEXITCODE
$tagCommit = $tagCommitOutput | Select-Object -First 1
if ($tagCommitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace([string]$tagCommit)) {
    throw "Release tag '$TagName' cannot be resolved to a commit."
}
$headCommitOutput = @(& git -C $RepositoryRoot rev-parse HEAD 2>$null)
$headCommitExitCode = $LASTEXITCODE
$headCommit = $headCommitOutput | Select-Object -First 1
if ($headCommitExitCode -ne 0 -or [string]$tagCommit -cne [string]$headCommit) {
    throw "Release tag '$TagName' does not point to the checked-out HEAD commit."
}

$status = & git -C $RepositoryRoot status --porcelain --untracked-files=normal 2>$null
if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($status -join [Environment]::NewLine))) {
    throw 'A GitHub release must be published from a clean checkout.'
}

Write-Host "Release tag verified: $TagName -> $headCommit"
