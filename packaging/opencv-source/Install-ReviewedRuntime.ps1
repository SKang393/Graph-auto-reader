# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,

    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot,

    [string]$RepositoryRoot,

    [string]$OpenCvAttestationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
}
else {
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
}

$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
$profileRoot = Join-Path $RepositoryRoot 'packaging\opencv-source'
$policyPath = Join-Path $profileRoot 'review\source-build-review-policy.json'
$evidenceValidatorPath = Join-Path $profileRoot 'Test-SourceAuditEvidence.ps1'
$reviewValidatorPath = Join-Path $profileRoot 'review\Test-SourceBuildReviewPolicy.ps1'
$sourceDllPath = Join-Path $EvidenceRoot 'bin\OpenCvSharpExtern.dll'

foreach ($requiredFile in @($policyPath, $evidenceValidatorPath, $reviewValidatorPath, $sourceDllPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Reviewed OpenCV runtime input is missing: $requiredFile"
    }
}
if (-not (Test-Path -LiteralPath $DestinationRoot -PathType Container)) {
    throw "OpenCV runtime destination does not exist: $DestinationRoot"
}

function Invoke-Validator {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$AttestationPath
    )

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Path, '-EvidenceRoot', $EvidenceRoot)
    if (-not [string]::IsNullOrWhiteSpace($AttestationPath)) { $arguments += @('-AttestationPath', [IO.Path]::GetFullPath($AttestationPath)) }
    & powershell.exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Reviewed OpenCV validator failed with exit code ${LASTEXITCODE}: $Path"
    }
}

Invoke-Validator -Path $evidenceValidatorPath
Invoke-Validator -Path $reviewValidatorPath -AttestationPath $OpenCvAttestationPath

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
if ([int]$policy.schemaVersion -ne 1 -or
    [string]$policy.profileId -ne 'graphreader-axis-minimal-win-x64' -or
    [string]$policy.overallReviewStatus -ne 'reviewed-provenance-only' -or
    [string]$policy.noticeBundle.reviewStatus -ne 'complete') {
    throw 'Reviewed OpenCV policy is not the expected provenance-only completed profile.'
}
if ([string]::IsNullOrWhiteSpace([string]$policy.binarySha256) -or
    [string]$policy.binarySha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'Reviewed OpenCV policy does not contain a valid binary SHA-256.'
}

$sourceHash = (Get-FileHash -LiteralPath $sourceDllPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::Equals($sourceHash, [string]$policy.binarySha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Reviewed OpenCV runtime hash '$sourceHash' does not match policy '$($policy.binarySha256)'."
}

$destinationPrefix = $DestinationRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$destinationCandidates = [System.Collections.Generic.List[IO.FileInfo]]::new()
$directories = [System.Collections.Generic.Queue[IO.DirectoryInfo]]::new()
$destinationDirectory = Get-Item -LiteralPath $DestinationRoot -Force
if (($destinationDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Reviewed OpenCV runtime installation rejects a reparse-point destination root.'
}
$directories.Enqueue($destinationDirectory)
while ($directories.Count -gt 0) {
    $directory = $directories.Dequeue()
    foreach ($entry in Get-ChildItem -LiteralPath $directory.FullName -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reviewed OpenCV runtime installation rejects reparse-point destination '$($entry.FullName)'."
        }
        if ($entry.PSIsContainer) {
            $directories.Enqueue([IO.DirectoryInfo]$entry)
        }
        elseif ([string]::Equals($entry.Name, 'OpenCvSharpExtern.dll', [StringComparison]::OrdinalIgnoreCase)) {
            $destinationCandidates.Add([IO.FileInfo]$entry)
        }
    }
}
if ($destinationCandidates.Count -ne 1) {
    throw "Expected exactly one published OpenCvSharpExtern.dll, found $($destinationCandidates.Count)."
}

$destinationPath = [IO.Path]::GetFullPath($destinationCandidates[0].FullName)
if (-not $destinationPath.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Published OpenCvSharpExtern.dll resolves outside the destination root.'
}

$cursorPath = $destinationPath
while (-not [string]::IsNullOrWhiteSpace($cursorPath)) {
    $cursor = Get-Item -LiteralPath $cursorPath -Force
    if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reviewed OpenCV runtime installation rejects reparse-point destination '$($cursor.FullName)'."
    }
    if ([string]::Equals($cursor.FullName.TrimEnd('\', '/'), $DestinationRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        break
    }
    $cursorPath = Split-Path $cursor.FullName -Parent
}
if ([string]::IsNullOrWhiteSpace($cursorPath)) {
    throw 'Published OpenCvSharpExtern.dll ancestry did not reach the destination root.'
}

$replacedHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
Copy-Item -LiteralPath $sourceDllPath -Destination $destinationPath -Force
$installedHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::Equals($installedHash, $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Reviewed OpenCV runtime did not install with the expected checksum.'
}

$metadata = [ordered]@{
    schema = 'graphreader.reviewed-opencv-runtime.v1'
    runtimeId = 'opencvsharpextern-source-audited'
    profileId = [string]$policy.profileId
    evidenceRootName = [string]$policy.evidenceRootName
    binarySha256 = $installedHash
    replacedBinarySha256 = $replacedHash
    sourceRevisions = [ordered]@{
        openCvSharp = [string]$policy.sourceRevisions.openCvSharp
        openCv = [string]$policy.sourceRevisions.openCv
        vcpkg = [string]$policy.sourceRevisions.vcpkg
    }
    provenanceValidated = $true
    noticeReviewStatus = [string]$policy.noticeBundle.reviewStatus
    maintainerAttestationStatus = [string]$policy.microsoftStaticRuntimeAttestation.status
    cleanMachineEvidence = $false
    releaseApproved = $false
}
$metadataPath = Join-Path $DestinationRoot 'reviewed-opencv-runtime.json'
[IO.File]::WriteAllText(
    $metadataPath,
    ($metadata | ConvertTo-Json -Depth 10),
    [Text.UTF8Encoding]::new($false))

Write-Host "Reviewed OpenCV runtime installed: $destinationPath"
Write-Host "Reviewed OpenCV runtime SHA-256: $installedHash"
Write-Output ([pscustomobject]@{
    DestinationPath = $destinationPath
    MetadataPath = $metadataPath
    BinarySha256 = $installedHash
    ReleaseApproved = $false
})
