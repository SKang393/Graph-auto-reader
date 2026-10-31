# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($root -ne 'E:\Graph-auto-reader') { throw 'E project root required.' }

function Assert-NoReparse([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $rootPath = [IO.Path]::GetPathRoot($full)
    $relative = $full.Substring($rootPath.Length)
    $cursor = $rootPath
    foreach ($part in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $part
        if (Test-Path -LiteralPath $cursor) {
            $attributes = [IO.File]::GetAttributes($cursor)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point rejected: $cursor" }
        }
    }
}

function Get-ProjectFile([string]$relative) {
    $path = Join-Path $root $relative
    Assert-NoReparse $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required regular file is absent: $relative" }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Length -gt 500000000) { throw "Input exceeds bound: $relative" }
    return [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

$base = Get-ProjectFile 'artifacts/goal22-runs/marker-context-bound-input-binding-v1.json'
if ($base.sha256 -ne '704f5448f7dead44b898d2f9eb709e1a0040c86ad085cde7003fec0a3b15bc8d') { throw 'Reviewed base binding SHA differs.' }
$complete = Get-ProjectFile 'artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/COMPLETE.json'
$identity = Get-ProjectFile 'artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/identity-metadata.json'
$raster = Get-ProjectFile 'artifacts/goal22-runs/marker-context-train-corpus-v3-smoke/raster-only/manifest.json'
if ($complete.sha256 -ne 'd3ec47441f2bd19504c6390a16ed2a94725751c84d5d7c43f6855328e7207dc6' -or
    $identity.sha256 -ne 'e0ea1b6894d119e4f3063dce4fd373af90585c91626ce4d84621dde15d125150' -or
    $raster.sha256 -ne '72975d61930bdafe9817f9c221d1699192c0f82e85c3c4c686edd5db0d7a99f3') { throw 'Pinned smoke receipt hash differs.' }

$originalBindingPath = Join-Path $root $base.path
$originalBinding = Get-Content -LiteralPath $originalBindingPath -Raw | ConvertFrom-Json
$oldRuntimeDirectory = Join-Path $root 'artifacts/goal22-runs/marker-context-bound-input-runtime-v1'
$runtimeRelative = 'artifacts/goal22-runs/marker-context-bound-input-smoke-runtime-v4'
$runtimeDirectory = Join-Path $root $runtimeRelative
Assert-NoReparse $oldRuntimeDirectory
Assert-NoReparse $runtimeDirectory
if (Test-Path -LiteralPath $runtimeDirectory) { throw 'Derived runtime directory must be fresh.' }
New-Item -ItemType Directory -Path $runtimeDirectory | Out-Null

$expectedRuntimeNames = @($originalBinding.managed_files | ForEach-Object { [IO.Path]::GetFileName($_.file) }) +
    @($originalBinding.native_files | ForEach-Object { [IO.Path]::GetFileName($_.file) })
if (($expectedRuntimeNames | Sort-Object -Unique).Count -ne $expectedRuntimeNames.Count) {
    throw 'Parent runtime binding contains duplicate executable names.'
}
foreach ($name in $expectedRuntimeNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $oldRuntimeDirectory $name) -PathType Leaf)) {
        throw "Parent runtime bundle is missing a bound file: $name"
    }
}
foreach ($row in @($originalBinding.managed_files) + @($originalBinding.native_files)) {
    $oldPath = Join-Path $oldRuntimeDirectory ([IO.Path]::GetFileName($row.file))
    Assert-NoReparse $oldPath
    if ((Get-FileHash -LiteralPath $oldPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) {
        throw "Parent runtime byte identity differs: $($row.file)"
    }
    Copy-Item -LiteralPath $oldPath -Destination (Join-Path $runtimeDirectory ([IO.Path]::GetFileName($row.file)))
}

$buildDirectory = Join-Path $root 'tools/GraphReader.MarkerContextSmokeCapture/bin/Release/net10.0-windows'
$allowedBuildReplacements = @('GraphReader.RealAcceptance.Ocr.dll', 'GraphReader.RealAcceptance.Ocr.deps.json', 'GraphReader.RealAcceptance.Ocr.runtimeconfig.json')
foreach ($row in $originalBinding.managed_files) {
    $name = [IO.Path]::GetFileName($row.file)
    if ($name -in $allowedBuildReplacements) { continue }
    $builtManaged = Join-Path $buildDirectory $name
    if (-not (Test-Path -LiteralPath $builtManaged -PathType Leaf) -or
        (Get-FileHash -LiteralPath $builtManaged -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) {
        throw "Current built managed dependency differs from reviewed runtime: $name"
    }
}
foreach ($runtimeName in @('GraphReader.RealAcceptance.Ocr.dll', 'GraphReader.RealAcceptance.Ocr.deps.json', 'GraphReader.RealAcceptance.Ocr.runtimeconfig.json')) {
    $built = Join-Path $buildDirectory $runtimeName
    Assert-NoReparse $built
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "Built friend assembly file missing: $runtimeName" }
    Copy-Item -LiteralPath $built -Destination (Join-Path $runtimeDirectory $runtimeName) -Force
}

$derivedBinding = $originalBinding
$derivedBinding.managed_files = @($originalBinding.managed_files | ForEach-Object {
    $name = [IO.Path]::GetFileName($_.file)
    $path = Join-Path $runtimeDirectory $name
    [ordered]@{ file = "$runtimeRelative/$name"; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$derivedBinding.native_files = @($originalBinding.native_files | ForEach-Object {
    $name = [IO.Path]::GetFileName($_.file)
    $path = Join-Path $runtimeDirectory $name
    [ordered]@{ file = "$runtimeRelative/$name"; role = $_.role; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$derivedBindingPath = Join-Path $root 'artifacts/goal22-runs/marker-context-bound-input-smoke-preflight-v4/runtime-binding.json'

$sourceHashes = @(
    Get-ProjectFile 'tools/GraphReader.MarkerContextSmokeCapture/GraphReader.MarkerContextSmokeCapture.csproj'
    Get-ProjectFile 'tools/GraphReader.MarkerContextSmokeCapture/Program.cs'
    Get-ProjectFile 'tools/GraphReader.MarkerContextSmokeCapture/SmokeBinding.cs'
    Get-ProjectFile 'tools/GraphReader.MarkerContextSmokeCapture/SmokeRunner.cs'
    Get-ProjectFile 'tools/GraphReader.MarkerContextSmokeCapture/CaptureSelfTests.cs'
    Get-ProjectFile 'tools/GraphReader.MarkerContextSmokeCapture/capture-protocol.json'
    Get-ProjectFile 'tools/GraphReader.MarkerContextSmokeCapture/Prepare-CaptureBinding.ps1'
)
$assemblyPath = "$runtimeRelative/GraphReader.RealAcceptance.Ocr.dll"
$assembly = Get-ProjectFile $assemblyPath
$protocolPath = 'tools/GraphReader.MarkerContextSmokeCapture/capture-protocol.json'
$protocolAbsolute = Join-Path $root $protocolPath
$protocolSha = (Get-FileHash -LiteralPath $protocolAbsolute -Algorithm SHA256).Hash.ToLowerInvariant()
$protocol = Get-Content -LiteralPath $protocolAbsolute -Raw | ConvertFrom-Json
$sourceImages = @($protocol.sources | ForEach-Object {
    [ordered]@{ source_id = $_.source_id; path = $_.path; sha256 = $_.image_sha256; width = $_.width; height = $_.height }
})
$descriptor = [ordered]@{
    schema = 'graphreader.marker-context-bound-input-smoke-capture-binding.v1'
    scope = 'two-source-nonsealed-train-smoke-no-marker-classifier'
    protocol_path = $protocolPath
    protocol_sha256 = $protocolSha
    base_binding_path = $base.path
    base_binding_sha256 = $base.sha256
    runtime_binding_path = 'artifacts/goal22-runs/marker-context-bound-input-smoke-preflight-v4/runtime-binding.json'
    runtime_binding_sha256 = ''
    complete_path = $complete.path
    complete_sha256 = $complete.sha256
    identity_path = $identity.path
    identity_sha256 = $identity.sha256
    raster_manifest_path = $raster.path
    raster_manifest_sha256 = $raster.sha256
    source_images = $sourceImages
    tool_sources = @($sourceHashes | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256 } })
    assembly_path = $assembly.path
    assembly_sha256 = $assembly.sha256
    capture_output_path = 'artifacts/goal22-runs/marker-context-bound-input-smoke-capture-v4'
    training_approved = $false
    marker_classifier_model_included = $false
}
$outputDirectory = Join-Path $root 'artifacts/goal22-runs/marker-context-bound-input-smoke-preflight-v4'
Assert-NoReparse $outputDirectory
if (-not (Test-Path -LiteralPath (Join-Path $root 'artifacts/goal22-runs') -PathType Container)) {
    throw 'Expected project-local goal22-runs parent is absent.'
}
if (Test-Path -LiteralPath $outputDirectory) { throw 'Preflight directory must be fresh.' }
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$runtimeJson = $derivedBinding | ConvertTo-Json -Depth 32
$runtimeBytes = [Text.UTF8Encoding]::new($false).GetBytes($runtimeJson + "`n")
$runtimeStream = [IO.File]::Open($derivedBindingPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $runtimeStream.Write($runtimeBytes, 0, $runtimeBytes.Length) } finally { $runtimeStream.Dispose() }
$descriptor.runtime_binding_sha256 = (Get-FileHash -LiteralPath $derivedBindingPath -Algorithm SHA256).Hash.ToLowerInvariant()
$outputFile = Join-Path $outputDirectory 'capture-binding.json'
$json = $descriptor | ConvertTo-Json -Depth 8
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
$stream = [IO.File]::Open($outputFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
[ordered]@{
    binding_path = $outputFile
    binding_sha256 = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
    assembly_sha256 = $assembly.sha256
    runtime_binding_sha256 = $descriptor.runtime_binding_sha256
    source_count = 2
    model_inference = 'not run'
    marker_classifier_model_loaded = $false
    training_approved = $false
} | ConvertTo-Json -Depth 4
