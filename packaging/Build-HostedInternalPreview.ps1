# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
[CmdletBinding()]
param(
    [string]$ExpectedVersion,
    [string]$ExpectedSourceSha,
    [string]$ReviewedOpenCvPreviewZip
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')
. (Join-Path $PSScriptRoot 'Test-InternalPreviewUpload.ps1')

function Assert-HostedPreviewBuild {
    param([Parameter(Mandatory)][string]$BuildDirectory, [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$SourceSha)
    Assert-NoReparseAncestors $BuildDirectory
    foreach ($relative in @('build-info.json', 'reviewed-opencv-runtime.json', 'reviewed-opencv-preview-lineage.json',
            'GraphReader.App.exe', 'native/graphreader-engine.exe', 'native/rust-toolchain.json', 'native/Rust-StandardLibrary-Notices.html',
            'LICENSES/OpenCV-4.13.0-License.txt', 'LICENSES/OpenCvSharp-4.13.0.20260627-License.txt',
            'LICENSES/OpenCvSharpExtern-SourceBuilt-ThirdPartyNotices.txt', 'LICENSES/Microsoft-Visual-Cpp-2022-Redistribution-Reference.md')) {
        $file = Join-Path $BuildDirectory $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Hosted preview is missing $relative" }
        Assert-NoReparseAncestors $file
    }
    $info = Get-Content -LiteralPath (Join-Path $BuildDirectory 'build-info.json') -Raw | ConvertFrom-Json
    $worker = Get-Content -LiteralPath (Join-Path $BuildDirectory 'native/rust-toolchain.json') -Raw | ConvertFrom-Json
    if ($info.schemaVersion -ne 2 -or [string]$info.version -cne $Version -or [string]$info.commit -cne $SourceSha -or
        $info.dirty -isnot [bool] -or $info.dirty -ne $false -or [string]$info.runtimeMode -cne 'ManualPreview' -or
        [string]$info.executableSha256 -cne (Get-Sha256 (Join-Path $BuildDirectory 'GraphReader.App.exe'))) {
        throw 'Hosted preview build identity does not match the requested clean source.'
    }
    if ([string]$worker.schema -cne 'graphreader.rust-worker-build.v1' -or [string]$worker.crt_linkage -cne 'static' -or
        [string]$worker.worker_sha256 -cne (Get-Sha256 (Join-Path $BuildDirectory 'native/graphreader-engine.exe')) -or
        [string]$worker.standard_library_notice_sha256 -cne (Get-Sha256 (Join-Path $BuildDirectory 'native/Rust-StandardLibrary-Notices.html'))) {
        throw 'Hosted preview Rust worker or standard-library notice differs from its metadata.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if ([Environment]::GetEnvironmentVariable('GITHUB_ACTIONS', 'Process') -cne 'true' -or
        [Environment]::GetEnvironmentVariable('GITHUB_REF', 'Process') -cne 'refs/heads/main') {
        throw 'Hosted internal preview requires GitHub Actions on refs/heads/main.'
    }
    $runId = Get-RequiredEnvironmentValue 'GITHUB_RUN_ID'
    $workflowSha = (Get-RequiredEnvironmentValue 'GITHUB_SHA').ToLowerInvariant()
    $runnerTemp = [IO.Path]::GetFullPath((Get-RequiredEnvironmentValue 'RUNNER_TEMP'))
    $githubOutput = Get-RequiredEnvironmentValue 'GITHUB_OUTPUT'
    if ($runId -notmatch '^[0-9]+$' -or $ExpectedSourceSha -cnotmatch '^[0-9a-f]{40}$' -or
        $workflowSha -cne $ExpectedSourceSha) { throw 'Hosted run and full expected source SHA are invalid or differ.' }
    $null = ConvertTo-GraphReaderVersion -Version $ExpectedVersion
    $head = (& git -C $repo rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or ([string]$head).Trim() -cne $ExpectedSourceSha) { throw 'Hosted checkout HEAD differs from expected source.' }
    $status = (& git -C $repo status --porcelain --untracked-files=normal 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($status -join "`n"))) { throw 'Hosted source checkout must be clean.' }
    if ((Get-GraphReaderCentralVersion -RepositoryRoot $repo).Value -cne $ExpectedVersion) { throw 'Hosted central version differs from expected version.' }
    if (-not (Test-Path -LiteralPath $ReviewedOpenCvPreviewZip -PathType Leaf)) { throw 'Reviewed source preview ZIP is missing.' }
    $outputRoot = Join-Path $repo 'artifacts/hosted-internal-preview'
    if (Test-Path -LiteralPath $outputRoot) { throw 'Hosted preview output root already exists.' }
    Assert-NoReparseAncestors $runnerTemp
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-DevPortable.ps1') `
        -FastTestsOnly -OutputRoot $outputRoot -ReviewedOpenCvPreviewZip ([IO.Path]::GetFullPath($ReviewedOpenCvPreviewZip))
    if ($LASTEXITCODE -ne 0) { throw "Hosted portable build failed with exit code $LASTEXITCODE." }
    $latest = Get-Content -LiteralPath (Join-Path $outputRoot 'latest.json') -Raw | ConvertFrom-Json
    if ([string]$latest.version -cne $ExpectedVersion -or [string]$latest.commit -cne $ExpectedSourceSha -or
        [string]$latest.buildDirectory -notmatch '^builds/[A-Za-z0-9.-]+$') { throw 'Hosted build pointer is invalid.' }
    $buildDirectory = Join-Path $outputRoot ([string]$latest.buildDirectory).Replace('/', '\')
    Assert-HostedPreviewBuild -BuildDirectory $buildDirectory -Version $ExpectedVersion -SourceSha $ExpectedSourceSha
    $builds = @(Get-ChildItem -LiteralPath (Join-Path $outputRoot 'builds') -Directory)
    if ($builds.Count -ne 1 -or $builds[0].FullName -ine [IO.Path]::GetFullPath($buildDirectory)) { throw 'Hosted output must contain exactly the selected build directory.' }
    $packageRoot = Join-Path $outputRoot 'packages'
    New-Item -ItemType Directory -Path $packageRoot | Out-Null
    $package = Join-Path $packageRoot "GraphAutoReader-$ExpectedVersion-win-x64-internal-preview.zip"
    $prefix = [IO.Path]::GetFullPath($buildDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $files = @(Get-ChildItem -LiteralPath $buildDirectory -Recurse -File -Force | Where-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        @($relative.Split('/') | Where-Object { $_ -ieq 'Data' }).Count -eq 0
    } | Sort-Object FullName)
    $snapshot = @{}
    foreach ($file in $files) {
        Assert-NoReparseAncestors $file.FullName
        $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        $snapshot.Add($relative, (Get-Sha256 $file.FullName))
    }
    $archiveStream = [IO.File]::Open($package, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $files) {
                Assert-NoReparseAncestors $file.FullName
                $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
                $sourceHash = $snapshot[$relative]
                if ((Get-Sha256 $file.FullName) -cne $sourceHash) { throw "Hosted build file changed before packaging: $relative" }
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $inputStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try { $destination = $entry.Open(); try { $inputStream.CopyTo($destination) } finally { $destination.Dispose() } } finally { $inputStream.Dispose() }
                if ((Get-Sha256 $file.FullName) -cne $sourceHash) { throw "Hosted build file changed while packaging: $relative" }
            }
        } finally { $zip.Dispose() }
    } finally { $archiveStream.Dispose() }
    $finalFiles = @(Get-ChildItem -LiteralPath $buildDirectory -Recurse -File -Force | Where-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        @($relative.Split('/') | Where-Object { $_ -ieq 'Data' }).Count -eq 0
    })
    if ($finalFiles.Count -ne $snapshot.Count) { throw 'Hosted build file inventory changed while packaging.' }
    foreach ($file in $finalFiles) {
        $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        if (-not $snapshot.ContainsKey($relative) -or (Get-Sha256 $file.FullName) -cne $snapshot[$relative]) { throw "Hosted build changed while packaging: $relative" }
    }
    $checkStream = [IO.File]::OpenRead($package)
    try {
        $checkZip = [IO.Compression.ZipArchive]::new($checkStream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            if ($checkZip.Entries.Count -ne $snapshot.Count) { throw 'Hosted ZIP inventory differs from the build.' }
            foreach ($entry in $checkZip.Entries) {
                if (-not $snapshot.ContainsKey($entry.FullName) -or (Get-ZipEntrySha256 $entry) -cne $snapshot[$entry.FullName]) { throw "Hosted ZIP bytes differ from the build: $($entry.FullName)" }
            }
        } finally { $checkZip.Dispose() }
    } finally { $checkStream.Dispose() }
    $packageHash = Get-Sha256 $package
    $env:GRAPHREADER_INTERNAL_UPLOAD_ROOT = $packageRoot
    $env:GRAPHREADER_INTERNAL_UPLOAD_WORKFLOW_SHA = $workflowSha
    $env:GRAPHREADER_INTERNAL_UPLOAD_SOURCE_SHA = $ExpectedSourceSha
    $env:GRAPHREADER_INTERNAL_UPLOAD_VERSION = $ExpectedVersion
    $env:GRAPHREADER_INTERNAL_UPLOAD_PACKAGE_SHA256 = $packageHash
    $env:GRAPHREADER_INTERNAL_UPLOAD_RUNNER_LABEL = "graphreader-upload-hosted-$runId"
    $staged = & (Join-Path $PSScriptRoot 'Test-InternalPreviewUpload.ps1')
    $receipt = [ordered]@{ schema='graphreader.hosted-internal-preview.v1'; version=$ExpectedVersion; sourceSha=$ExpectedSourceSha; packageSha256=$packageHash; rustWorkerSha256=(Get-Sha256 (Join-Path $buildDirectory 'native/graphreader-engine.exe')); releaseApproved=$false }
    $receiptPath = Join-Path $runnerTemp 'graphreader-internal-preview/receipt.json'
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [IO.File]::AppendAllText($githubOutput, "version=$ExpectedVersion`npackage_sha256=$packageHash`npackage_path=$staged`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Hosted internal preview verified: $packageHash"
}
