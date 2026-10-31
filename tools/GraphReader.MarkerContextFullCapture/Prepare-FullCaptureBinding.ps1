# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($root -ne 'E:\Graph-auto-reader') { throw 'E project root required.' }

function Assert-NoReparse([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $rootPath = [IO.Path]::GetPathRoot($full)
    $cursor = $rootPath
    foreach ($part in $full.Substring($rootPath.Length).Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $part
        if (Test-Path -LiteralPath $cursor) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point rejected: $cursor" }
        }
    }
}

function Get-ProjectFile([string]$relative, [long]$maxBytes = 500000000) {
    $path = Join-Path $root $relative
    Assert-NoReparse $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required regular file is absent: $relative" }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Length -gt $maxBytes) { throw "Input exceeds byte bound: $relative" }
    return [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Write-NewJson([string]$path, [object]$value, [int]$depth = 24) {
    $json = ConvertTo-Json -InputObject $value -Depth $depth
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

$base = Get-ProjectFile 'artifacts/goal22-runs/marker-context-bound-input-binding-v1.json'
$corpusBinding = Get-ProjectFile 'artifacts/goal22-runs/marker-context-train-corpus-binding-v5.json' 2000000
$complete = Get-ProjectFile 'artifacts/goal22-runs/marker-context-train-corpus-v5-full/COMPLETE.json' 1000000
$identity = Get-ProjectFile 'artifacts/goal22-runs/marker-context-train-corpus-v5-full/identity-metadata.json' 5000000
$raster = Get-ProjectFile 'artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only/manifest.json' 5000000
$smokeReport = Get-ProjectFile 'artifacts/goal22-runs/marker-context-bound-input-smoke-capture-v4/report.json' 1000000
$protocol = Get-ProjectFile 'tools/GraphReader.MarkerContextFullCapture/full-capture-protocol.json' 1000000
if ($base.sha256 -ne '704f5448f7dead44b898d2f9eb709e1a0040c86ad085cde7003fec0a3b15bc8d' -or
    $corpusBinding.sha256 -ne 'f095bac6f76a9a2d444869ba4d75538e76237c399d3b98997c97292b4cebebd4' -or
    $complete.sha256 -ne '957e2bec1d45b49665e05c6442e90fa4285d27e02140b8458f2f5066de001019' -or
    $identity.sha256 -ne '2ca7fce16eee67d7f040e62f6979ebc76c83bcec6bafad03e67e381ce83d2055' -or
    $raster.sha256 -ne 'be0d63fb22796fd07ba83e5ad714888f136875b180883c1b36d0481cbf5433f2' -or
    $smokeReport.sha256 -ne 'c5e48426d47cfa256f36b8dcdea615a9a7debc35f475b0584e6b92064d6dede9' -or
    $protocol.sha256 -ne 'aa131bfa849d8332917ce7ad4c001bf00daea1e9a6f27861a20a0b0d06b5c4d4') {
    throw 'A fixed parent, corpus, smoke, or full-capture protocol identity differs.'
}

$parentBinding = Get-Content -LiteralPath (Join-Path $root $base.path) -Raw | ConvertFrom-Json
$corpusBindingValue = Get-Content -LiteralPath (Join-Path $root $corpusBinding.path) -Raw | ConvertFrom-Json
$completeValue = Get-Content -LiteralPath (Join-Path $root $complete.path) -Raw | ConvertFrom-Json
$identityValue = Get-Content -LiteralPath (Join-Path $root $identity.path) -Raw | ConvertFrom-Json
$rasterValue = Get-Content -LiteralPath (Join-Path $root $raster.path) -Raw | ConvertFrom-Json
$smokeValue = Get-Content -LiteralPath (Join-Path $root $smokeReport.path) -Raw | ConvertFrom-Json
$protocolValue = Get-Content -LiteralPath (Join-Path $root $protocol.path) -Raw | ConvertFrom-Json
if ($parentBinding.schema -ne 'graphreader.marker-bound-input-train-binding.v1' -or $parentBinding.training_approved -ne $false -or
    $corpusBindingValue.schema -ne 'marker-context-train-corpus-binding-v5' -or $corpusBindingValue.execution_mode -ne 'full_only' -or $corpusBindingValue.training_approved -ne $false -or
    $completeValue.schema -ne 'marker-context-train-corpus-complete-v5' -or $completeValue.mode -ne 'full' -or $completeValue.source_count -ne 64 -or $completeValue.training_approved -ne $false -or
    $completeValue.builder_binding_sha256 -ne $corpusBinding.sha256 -or $completeValue.identity_sha256 -ne $identity.sha256 -or $completeValue.raster_manifest_sha256 -ne $raster.sha256 -or
    $rasterValue.schema -ne 'marker-context-train-raster-v5' -or $rasterValue.mode -ne 'full' -or $rasterValue.training_approved -ne $false -or $rasterValue.builder_binding_sha256 -ne $corpusBinding.sha256 -or
    $identityValue.schema -ne 'marker-context-train-identity-v5' -or $identityValue.mode -ne 'full' -or $identityValue.training_approved -ne $false -or
    $rasterValue.sources.Count -ne 64 -or $identityValue.sources.Count -ne 64 -or $protocolValue.sources.Count -ne 64) {
    throw 'Full v5 train-only corpus cross-bindings or nonapproved scope differ.'
}

$rasterDirectory = Join-Path $root 'artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only'
Assert-NoReparse $rasterDirectory
$seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenImages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenSeeds = [Collections.Generic.HashSet[int]]::new()
for ($i = 0; $i -lt 64; $i++) {
    $row = $rasterValue.sources[$i]
    $id = $identityValue.sources[$i]
    $protocolRow = $protocolValue.sources[$i]
    $path = Join-Path $rasterDirectory $row.file
    Assert-NoReparse $path
    $file = Get-ProjectFile "artifacts/goal22-runs/marker-context-train-corpus-v5-full/raster-only/$($row.file)" 1048576
    if ($row.width -ne 1200 -or $row.height -ne 350 -or $row.sha256 -ne $file.sha256 -or
        $row.source_id -ne $id.source_id -or $id.image_sha256 -ne $row.sha256 -or
        $id.source_id_status -ne 'predeclared_not_yet_imported' -or
        $protocolRow.source_id -ne $id.source_id -or $protocolRow.case_id -ne $id.case_id -or
        $protocolRow.seed -ne $id.seed -or $protocolRow.scene_id -ne $id.scene_id -or
        $protocolRow.scene_sha256 -ne $id.scene_sha256 -or $protocolRow.image_sha256 -ne $row.sha256 -or
        -not $seenIds.Add($id.source_id) -or -not $seenImages.Add($row.sha256) -or -not $seenSeeds.Add([int]$id.seed)) {
        throw "Full source/image/identity binding failed at index $i."
    }
}
$actualRasterEntries = @(Get-ChildItem -LiteralPath $rasterDirectory -Force | ForEach-Object Name | Sort-Object -CaseSensitive)
$expectedRasterEntries = @('manifest.json') + @($rasterValue.sources | ForEach-Object file)
$expectedRasterEntries = @($expectedRasterEntries | Sort-Object -CaseSensitive)
if (($actualRasterEntries -join "`n") -cne ($expectedRasterEntries -join "`n")) { throw 'Raster-only directory contains missing or extra entries.' }

$runtimeRelative = 'artifacts/goal22-runs/marker-context-bound-input-full-runtime-v4'
$preflightRelative = 'artifacts/goal22-runs/marker-context-bound-input-full-preflight-v4'
$runtimeDirectory = Join-Path $root $runtimeRelative
$preflightDirectory = Join-Path $root $preflightRelative
Assert-NoReparse $runtimeDirectory
Assert-NoReparse $preflightDirectory
if ((Test-Path -LiteralPath $runtimeDirectory) -or (Test-Path -LiteralPath $preflightDirectory)) { throw 'Full-capture runtime and descriptor paths must be fresh.' }
$oldRuntimeDirectory = Join-Path $root 'artifacts/goal22-runs/marker-context-bound-input-runtime-v1'
Assert-NoReparse $oldRuntimeDirectory
$oldNames = @($parentBinding.managed_files | ForEach-Object { [IO.Path]::GetFileName($_.file) }) +
    @($parentBinding.native_files | ForEach-Object { [IO.Path]::GetFileName($_.file) })
if (($oldNames | Sort-Object -Unique).Count -ne $oldNames.Count) { throw 'Parent runtime binding contains duplicate executable names.' }
New-Item -ItemType Directory -Path $runtimeDirectory | Out-Null
foreach ($row in @($parentBinding.managed_files) + @($parentBinding.native_files)) {
    $name = [IO.Path]::GetFileName($row.file)
    $oldPath = Join-Path $oldRuntimeDirectory $name
    Assert-NoReparse $oldPath
    if (-not (Test-Path -LiteralPath $oldPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $oldPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) {
        throw "Parent runtime byte identity differs: $name"
    }
    Copy-Item -LiteralPath $oldPath -Destination (Join-Path $runtimeDirectory $name)
}

$buildDirectory = Join-Path $root 'tools/GraphReader.MarkerContextFullCapture/bin/Release/net10.0-windows'
$replaceNames = @('GraphReader.RealAcceptance.Ocr.dll','GraphReader.RealAcceptance.Ocr.deps.json','GraphReader.RealAcceptance.Ocr.runtimeconfig.json')
foreach ($row in $parentBinding.managed_files) {
    $name = [IO.Path]::GetFileName($row.file)
    if ($name -in $replaceNames) { continue }
    $built = Join-Path $buildDirectory $name
    Assert-NoReparse $built
    if (-not (Test-Path -LiteralPath $built -PathType Leaf) -or
        (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) {
        throw "Current build dependency differs from the reviewed parent runtime: $name"
    }
}
foreach ($name in $replaceNames) {
    $built = Join-Path $buildDirectory $name
    Assert-NoReparse $built
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "Built friend assembly output is absent: $name" }
    Copy-Item -LiteralPath $built -Destination (Join-Path $runtimeDirectory $name) -Force
}
$runtimeNames = @(Get-ChildItem -LiteralPath $runtimeDirectory -Force | ForEach-Object Name | Sort-Object -CaseSensitive)
$expectedNames = @($oldNames | Sort-Object -CaseSensitive)
if (($runtimeNames -join "`n") -cne ($expectedNames -join "`n")) { throw 'Derived runtime has missing or unexpected files.' }

$derivedBinding = $parentBinding
$derivedBinding.managed_files = @($parentBinding.managed_files | ForEach-Object {
    $name = [IO.Path]::GetFileName($_.file)
    [ordered]@{ file = "$runtimeRelative/$name"; sha256 = (Get-FileHash -LiteralPath (Join-Path $runtimeDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$derivedBinding.native_files = @($parentBinding.native_files | ForEach-Object {
    $name = [IO.Path]::GetFileName($_.file)
    [ordered]@{ file = "$runtimeRelative/$name"; role = $_.role; sha256 = (Get-FileHash -LiteralPath (Join-Path $runtimeDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
})
New-Item -ItemType Directory -Path $preflightDirectory | Out-Null
$runtimeBindingPath = Join-Path $preflightDirectory 'runtime-binding.json'
Write-NewJson $runtimeBindingPath $derivedBinding 32
$runtimeBinding = Get-ProjectFile "$preflightRelative/runtime-binding.json" 2000000

$toolRelativePaths = @(
    'tools/GraphReader.MarkerContextFullCapture/GraphReader.MarkerContextFullCapture.csproj',
    'tools/GraphReader.MarkerContextFullCapture/Program.cs',
    'tools/GraphReader.MarkerContextFullCapture/FullBinding.cs',
    'tools/GraphReader.MarkerContextFullCapture/FullRunner.cs',
    'tools/GraphReader.MarkerContextFullCapture/FullCaptureSelfTests.cs',
    'tools/GraphReader.MarkerContextFullCapture/full-capture-protocol.json',
    'tools/GraphReader.MarkerContextFullCapture/Prepare-FullCaptureBinding.ps1',
    'tools/GraphReader.MarkerContextFullCapture/README.md'
)
$toolSources = @($toolRelativePaths | ForEach-Object { Get-ProjectFile $_ 5000000 })
$assemblyPath = "$runtimeRelative/GraphReader.RealAcceptance.Ocr.dll"
$assembly = Get-ProjectFile $assemblyPath
$sourceImages = @($protocolValue.sources | ForEach-Object {
    [ordered]@{ source_id = $_.source_id; path = $_.path; sha256 = $_.image_sha256; width = $_.width; height = $_.height }
})
$descriptor = [ordered]@{
    schema = 'graphreader.marker-context-bound-input-full-capture-binding.v1'
    scope = '64-source-public-synthetic-train-raster-capture-no-marker-classifier'
    protocol_path = $protocol.path
    protocol_sha256 = $protocol.sha256
    base_binding_path = $base.path
    base_binding_sha256 = $base.sha256
    runtime_binding_path = "$preflightRelative/runtime-binding.json"
    runtime_binding_sha256 = $runtimeBinding.sha256
    corpus_binding_path = $corpusBinding.path
    corpus_binding_sha256 = $corpusBinding.sha256
    smoke_report_path = $smokeReport.path
    smoke_report_sha256 = $smokeReport.sha256
    complete_path = $complete.path
    complete_sha256 = $complete.sha256
    identity_path = $identity.path
    identity_sha256 = $identity.sha256
    raster_manifest_path = $raster.path
    raster_manifest_sha256 = $raster.sha256
    source_images = $sourceImages
    tool_sources = @($toolSources | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256 } })
    assembly_path = $assembly.path
    assembly_sha256 = $assembly.sha256
    capture_output_path = 'artifacts/goal22-runs/marker-context-bound-input-full-capture-v4'
    training_approved = $false
    marker_classifier_model_included = $false
}
$descriptorPath = Join-Path $preflightDirectory 'capture-binding.json'
Write-NewJson $descriptorPath $descriptor 20
[ordered]@{
    descriptor_path = $descriptorPath
    descriptor_sha256 = (Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash.ToLowerInvariant()
    protocol_sha256 = $protocol.sha256
    parent_binding_sha256 = $base.sha256
    corpus_binding_sha256 = $corpusBinding.sha256
    runtime_binding_sha256 = $runtimeBinding.sha256
    assembly_sha256 = $assembly.sha256
    source_count = 64
    smoke_reference_sha256 = $smokeReport.sha256
    model_inference = 'not run'
    training_approved = $false
    marker_classifier_model_loaded = $false
} | ConvertTo-Json -Depth 5
