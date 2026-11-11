# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$subject = Join-Path $projectRoot 'packaging\Test-InternalPreviewUpload.ps1'
$testRoot = Join-Path $projectRoot ('artifacts\internal-preview-upload-fixture-' + [Guid]::NewGuid().ToString('N'))
$inputRoot = Join-Path $testRoot 'local-root'
$runnerTemp = Join-Path $testRoot 'runner-temp'
$environmentNames = @(
    'GRAPHREADER_INTERNAL_UPLOAD_ROOT', 'GRAPHREADER_INTERNAL_UPLOAD_WORKFLOW_SHA',
    'GRAPHREADER_INTERNAL_UPLOAD_SOURCE_SHA', 'GRAPHREADER_INTERNAL_UPLOAD_VERSION',
    'GRAPHREADER_INTERNAL_UPLOAD_PACKAGE_SHA256', 'GRAPHREADER_INTERNAL_UPLOAD_RUNNER_LABEL', 'RUNNER_TEMP'
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$version = '0.4.36'
$sourceCommit = '1234567890123456789012345678901234567890'
$runnerLabel = 'graphreader-upload-fixture-0123456789'
$nativeBytes = [Text.Encoding]::UTF8.GetBytes('synthetic reviewed native fixture')
$exeBytes = [Text.Encoding]::UTF8.GetBytes('synthetic ManualPreview executable')
function Hash-Bytes([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Write-ZipEntry($Archive, [string]$Name, [byte[]]$Bytes) {
    $entry = $Archive.CreateEntry($Name)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
}
function New-Fixture {
    param([string[]]$ExtraEntries = @(), [string]$MetadataCommit = $sourceCommit, [switch]$MissingPortableMode)
    if (Test-Path -LiteralPath $inputRoot) { Remove-Item -LiteralPath $inputRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $inputRoot,$runnerTemp -Force | Out-Null
    $nativeHash = Hash-Bytes $nativeBytes
    $runtime = [ordered]@{
        schema = 'graphreader.reviewed-opencv-runtime.v1'
        runtimeId = 'opencvsharpextern-source-audited'
        binarySha256 = $nativeHash
        provenanceValidated = $true
        noticeReviewStatus = 'complete'
        maintainerAttestationStatus = 'recorded-private'
        cleanMachineEvidence = $false
        releaseApproved = $false
    }
    $buildInfo = [ordered]@{
        schemaVersion = 2
        version = $version
        commit = $MetadataCommit
        dirty = $false
        runtimeMode = 'ManualPreview'
        executableSha256 = Hash-Bytes $exeBytes
        openCvRuntime = $runtime
    }
    $name = "GraphAutoReader-$version-win-x64-internal-preview.zip"
    $zipPath = Join-Path $inputRoot $name
    $file = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $utf8 = [Text.UTF8Encoding]::new($false)
            Write-ZipEntry $archive 'build-info.json' $utf8.GetBytes(($buildInfo | ConvertTo-Json -Depth 10 -Compress))
            Write-ZipEntry $archive 'reviewed-opencv-runtime.json' $utf8.GetBytes(($runtime | ConvertTo-Json -Depth 10 -Compress))
            Write-ZipEntry $archive 'GraphReader.App.exe' $exeBytes
            foreach ($required in @('GraphReader.App.dll', 'GraphReader.App.deps.json', 'GraphReader.App.runtimeconfig.json')) {
                Write-ZipEntry $archive $required $utf8.GetBytes('synthetic required application file')
            }
            if (-not $MissingPortableMode) { Write-ZipEntry $archive 'portable.mode' $utf8.GetBytes('synthetic portable marker') }
            Write-ZipEntry $archive 'runtimes/win-x64/native/OpenCvSharpExtern.dll' $nativeBytes
            foreach ($extra in $ExtraEntries) { Write-ZipEntry $archive $extra $utf8.GetBytes('forbidden fixture content') }
        }
        finally { $archive.Dispose() }
    }
    finally { $file.Dispose() }
    return $zipPath
}
function Invoke-Subject([string]$Message) {
    try {
        & $subject | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*$Message*") { return }
        throw "Expected rejection containing '$Message', got: $($_.Exception.Message)"
    }
    throw "Expected rejection containing '$Message'."
}
function Set-Inputs([string]$ZipPath, [string]$Commit = $sourceCommit) {
    $env:GRAPHREADER_INTERNAL_UPLOAD_ROOT = $inputRoot
    $env:GRAPHREADER_INTERNAL_UPLOAD_WORKFLOW_SHA = $Commit
    $env:GRAPHREADER_INTERNAL_UPLOAD_SOURCE_SHA = $Commit
    $env:GRAPHREADER_INTERNAL_UPLOAD_VERSION = $version
    $env:GRAPHREADER_INTERNAL_UPLOAD_PACKAGE_SHA256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $env:GRAPHREADER_INTERNAL_UPLOAD_RUNNER_LABEL = $runnerLabel
    $env:RUNNER_TEMP = $runnerTemp
}
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    $zip = New-Fixture
    Set-Inputs $zip
    $staged = & $subject
    if (-not (Test-Path -LiteralPath $staged -PathType Leaf) -or
        (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant() -cne $env:GRAPHREADER_INTERNAL_UPLOAD_PACKAGE_SHA256) {
        throw 'Valid preview was not staged byte-for-byte.'
    }
    Remove-Item -LiteralPath (Join-Path $runnerTemp 'graphreader-internal-preview') -Recurse -Force

    Set-Inputs $zip
    $env:GRAPHREADER_INTERNAL_UPLOAD_PACKAGE_SHA256 = '0' * 64
    Invoke-Subject 'SHA-256 does not match'

    $zip = New-Fixture -MetadataCommit ('a' * 40)
    Set-Inputs $zip
    Invoke-Subject 'expected clean ManualPreview version and source commit'

    $zip = New-Fixture -MissingPortableMode
    Set-Inputs $zip
    Invoke-Subject 'missing root metadata: portable.mode'

    $zip = New-Fixture
    Set-Inputs $zip
    $env:GRAPHREADER_INTERNAL_UPLOAD_VERSION = '0.4.100'
    Invoke-Subject 'each component from 0 through 99'

    $zip = New-Fixture -ExtraEntries @('Data/settings.json')
    Set-Inputs $zip
    Invoke-Subject 'Mutable user data or private provenance'

    $zip = New-Fixture -ExtraEntries @('provenance-private/opencv-microsoft-static-runtime-attestation.json')
    Set-Inputs $zip
    Invoke-Subject 'Private key or credential file'

    $zip = New-Fixture -ExtraEntries @('GraphReader.App.pdb')
    Set-Inputs $zip
    Invoke-Subject 'Forbidden project, source, debug, or key file'

    $zip = New-Fixture -ExtraEntries @('../escaped.txt')
    Set-Inputs $zip
    Invoke-Subject 'Unsafe ZIP entry path'

    $zip = New-Fixture -ExtraEntries @('CON.txt')
    Set-Inputs $zip
    Invoke-Subject 'unsafe on Windows'

    Write-Host 'Internal preview upload verifier synthetic checks passed.'
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process') }
    $artifactPrefix = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
