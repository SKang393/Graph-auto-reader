# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [switch]$Wait,
    [switch]$PortableSmoke,
    [switch]$DisableLocalEnhancement,
    [switch]$DisableLocalPdfium,
    [string]$RepositoryRoot,
    [string]$ImagePath,
    [string]$RealEsrganRuntimeRoot,
    [string]$RealEsrganManifestPath,
    [string]$PdfiumApprovalPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$adjacentLatest = Join-Path $PSScriptRoot 'latest.json'
if (Test-Path -LiteralPath $adjacentLatest -PathType Leaf) {
    $outputRoot = $PSScriptRoot
    $inferredRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
}
else {
    $inferredRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $outputRoot = Join-Path $inferredRepositoryRoot 'artifacts\dev-portable'
}
$repositoryRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $inferredRepositoryRoot } else { [System.IO.Path]::GetFullPath($RepositoryRoot) }

$latestPath = Join-Path $outputRoot 'latest.json'
if (-not (Test-Path -LiteralPath $latestPath -PathType Leaf)) {
    throw "No successful development portable exists. Run packaging\Build-DevPortable.ps1 first: $latestPath"
}

$latest = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
if ([int]$latest.schemaVersion -ne 2) {
    throw "latest.json uses unsupported schema version '$($latest.schemaVersion)': $latestPath"
}
if ([string]::IsNullOrWhiteSpace([string]$latest.executable)) {
    throw "latest.json does not identify an executable: $latestPath"
}
$expectedExecutableSha256 = [string]$latest.executableSha256
if ($expectedExecutableSha256 -notmatch '^[0-9a-f]{64}$') {
    throw "latest.json does not contain a canonical executable SHA-256: $latestPath"
}

$ledgerPath = Join-Path $repositoryRoot 'docs\BUILD_LEDGER.json'
if (-not (Test-Path -LiteralPath $ledgerPath -PathType Leaf)) {
    throw "Cannot verify the retained build: ledger missing at $ledgerPath. For a custom output root, pass -RepositoryRoot with the checkout path; restore the tracked ledger and reconcile latest.json before launching."
}
try {
    $ledger = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
    $retained = @($ledger.builds | Where-Object { $_.retained -eq $true })
    $buildDirectoryName = [System.IO.Path]::GetFileName(([string]$latest.buildDirectory).Replace('/', '\').TrimEnd('\'))
    $identityMatches = $ledger.schemaVersion -eq 1 -and $retained.Count -eq 1 -and
        -not [string]::IsNullOrWhiteSpace($buildDirectoryName) -and
        [string]$retained[0].directory -ceq $buildDirectoryName -and
        [string]$retained[0].version -ceq [string]$latest.version -and
        [string]$retained[0].commit -ceq [string]$latest.commit -and
        [string]$retained[0].executableSha256 -ceq $expectedExecutableSha256
}
catch {
    throw "Cannot verify retained build identity using $ledgerPath and ${latestPath}: $($_.Exception.Message)"
}
if (-not $identityMatches) {
    throw "latest.json does not match the single retained build in $ledgerPath. Reconcile the pointer and retained build before launching; do not regenerate the ledger from a stale pointer."
}

$executablePath = [System.IO.Path]::GetFullPath((Join-Path $outputRoot ([string]$latest.executable)))
$outputPrefix = [System.IO.Path]::GetFullPath($outputRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $executablePath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'latest.json points outside the development portable root.'
}
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "The latest development portable executable is missing: $executablePath"
}
$actualExecutableSha256 = Get-Sha256 -Path $executablePath
if (-not [string]::Equals(
        $actualExecutableSha256,
        $expectedExecutableSha256,
        [StringComparison]::Ordinal)) {
    throw "The latest development portable executable checksum does not match latest.json: $executablePath"
}

$buildDirectory = Split-Path -Parent $executablePath
if (-not [string]::Equals($buildDirectory, [System.IO.Path]::GetFullPath((Join-Path $outputRoot ([string]$latest.buildDirectory))), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'latest.json executable is not inside its declared buildDirectory.'
}
if (-not (Test-Path -LiteralPath (Join-Path $buildDirectory 'portable.mode') -PathType Leaf)) {
    throw "The latest development portable is missing portable.mode: $buildDirectory"
}

$sharedDataRoot = [System.IO.Path]::GetFullPath((Join-Path $outputRoot 'Data'))
New-Item -ItemType Directory -Path $sharedDataRoot -Force | Out-Null
$arguments = [System.Collections.Generic.List[string]]::new()
if ($PortableSmoke.IsPresent) {
    $arguments.Add('--portable-smoke')
}
if (-not [string]::IsNullOrWhiteSpace($ImagePath)) {
    $resolvedImagePath = [System.IO.Path]::GetFullPath($ImagePath)
    if (-not (Test-Path -LiteralPath $resolvedImagePath -PathType Leaf)) {
        throw "The requested startup image is missing: $resolvedImagePath"
    }
    $arguments.Add('--open-image')
    $arguments.Add(('"{0}"' -f $resolvedImagePath))
}

$previousDataRoot = [Environment]::GetEnvironmentVariable(
    'GRAPHREADER_DEV_PORTABLE_DATA_ROOT',
    'Process')
$previousEnhancementRuntimeRoot = [Environment]::GetEnvironmentVariable(
    'GRAPHREADER_REALESRGAN_RUNTIME_ROOT',
    'Process')
$previousEnhancementManifestPath = [Environment]::GetEnvironmentVariable(
    'GRAPHREADER_REALESRGAN_MANIFEST_PATH',
    'Process')
$previousPdfiumApprovalPath = [Environment]::GetEnvironmentVariable(
    'GRAPHREADER_PDFIUM_APPROVAL_PATH',
    'Process')
try {
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_DEV_PORTABLE_DATA_ROOT',
        $sharedDataRoot,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_REALESRGAN_RUNTIME_ROOT',
        $null,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_REALESRGAN_MANIFEST_PATH',
        $null,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_PDFIUM_APPROVAL_PATH',
        $null,
        'Process')
    if (-not $DisableLocalEnhancement.IsPresent) {
        if ([string]::IsNullOrWhiteSpace($RealEsrganRuntimeRoot)) {
            $RealEsrganRuntimeRoot = Join-Path $repositoryRoot 'artifacts\goal19-realesrgan\runtime-authorized-vcomp-14.44.35211'
        }
        if ([string]::IsNullOrWhiteSpace($RealEsrganManifestPath)) {
            $RealEsrganManifestPath = Join-Path $repositoryRoot 'models\manifest\super-resolution\realesr-animevideov3-ncnn-x2.json'
        }
        if ((Test-Path -LiteralPath $RealEsrganRuntimeRoot -PathType Container) -and
            (Test-Path -LiteralPath $RealEsrganManifestPath -PathType Leaf)) {
            [Environment]::SetEnvironmentVariable(
                'GRAPHREADER_REALESRGAN_RUNTIME_ROOT',
                [System.IO.Path]::GetFullPath($RealEsrganRuntimeRoot),
                'Process')
            [Environment]::SetEnvironmentVariable(
                'GRAPHREADER_REALESRGAN_MANIFEST_PATH',
                [System.IO.Path]::GetFullPath($RealEsrganManifestPath),
                'Process')
        }
    }
    if (-not $DisableLocalPdfium.IsPresent) {
        if ([string]::IsNullOrWhiteSpace($PdfiumApprovalPath)) {
            $PdfiumApprovalPath = Join-Path $repositoryRoot 'artifacts\pdfium-source\evidence\reviewed-approval.json'
        }
        if (Test-Path -LiteralPath $PdfiumApprovalPath -PathType Leaf) {
            [Environment]::SetEnvironmentVariable(
                'GRAPHREADER_PDFIUM_APPROVAL_PATH',
                [System.IO.Path]::GetFullPath($PdfiumApprovalPath),
                'Process')
        }
    }
    $startParameters = @{
        FilePath = $executablePath
        WorkingDirectory = $buildDirectory
        PassThru = $true
        Wait = $Wait.IsPresent
    }
    if ($arguments.Count -gt 0) {
        $startParameters.ArgumentList = $arguments
    }
    $process = Start-Process @startParameters
}
finally {
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_DEV_PORTABLE_DATA_ROOT',
        $previousDataRoot,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_REALESRGAN_RUNTIME_ROOT',
        $previousEnhancementRuntimeRoot,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_REALESRGAN_MANIFEST_PATH',
        $previousEnhancementManifestPath,
        'Process')
    [Environment]::SetEnvironmentVariable(
        'GRAPHREADER_PDFIUM_APPROVAL_PATH',
        $previousPdfiumApprovalPath,
        'Process')
}

Write-Host "Launched: $executablePath"
Write-Host "Shared data: $sharedDataRoot"
if (-not $DisableLocalEnhancement.IsPresent -and
    -not [string]::IsNullOrWhiteSpace($RealEsrganRuntimeRoot) -and
    (Test-Path -LiteralPath $RealEsrganRuntimeRoot -PathType Container)) {
    Write-Host "Local enhancement: realesr-animevideov3 x2 evaluation runtime configured outside the portable build"
}
if (-not $DisableLocalPdfium.IsPresent -and
    -not [string]::IsNullOrWhiteSpace($PdfiumApprovalPath) -and
    (Test-Path -LiteralPath $PdfiumApprovalPath -PathType Leaf)) {
    Write-Host 'Local PDFium: reviewed renderer approval configured outside the portable build'
}
if ($Wait.IsPresent -and $process.ExitCode -ne 0) {
    throw "Development portable exited with code $($process.ExitCode)."
}
