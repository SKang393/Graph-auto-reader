# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repo 'packaging\Build-HostedInternalPreview.ps1')
$root = Join-Path $repo ('artifacts\hosted-preview-helper-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root, (Join-Path $root 'native'), (Join-Path $root 'LICENSES') -Force | Out-Null
function Write-Text($Path, $Text) { [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false)) }
function Reject([scriptblock]$Action, [string]$Case) { $failed=$false; try { & $Action } catch { $failed=$true }; if (-not $failed) { throw "$Case unexpectedly succeeded" }; Write-Host "PASS $Case" }
try {
    foreach ($path in @('GraphReader.App.exe','reviewed-opencv-runtime.json','reviewed-opencv-preview-lineage.json',
        'native/graphreader-engine.exe','native/Rust-StandardLibrary-Notices.html', 'LICENSES/OpenCV-4.13.0-License.txt',
        'LICENSES/OpenCvSharp-4.13.0.20260627-License.txt', 'LICENSES/OpenCvSharpExtern-SourceBuilt-ThirdPartyNotices.txt',
        'LICENSES/Microsoft-Visual-Cpp-2022-Redistribution-Reference.md')) { Write-Text (Join-Path $root $path) "fixture $path" }
    $source = 'a'*40
    $info = @{schemaVersion=2;version='0.4.38';commit=$source;dirty=$false;runtimeMode='ManualPreview';executableSha256=(Get-Sha256 (Join-Path $root 'GraphReader.App.exe'))}
    $worker = @{schema='graphreader.rust-worker-build.v1';crt_linkage='static';worker_sha256=(Get-Sha256 (Join-Path $root 'native/graphreader-engine.exe'));standard_library_notice_sha256=(Get-Sha256 (Join-Path $root 'native/Rust-StandardLibrary-Notices.html'))}
    Write-Text (Join-Path $root 'build-info.json') ($info | ConvertTo-Json)
    Write-Text (Join-Path $root 'native/rust-toolchain.json') ($worker | ConvertTo-Json)
    Assert-HostedPreviewBuild -BuildDirectory $root -Version '0.4.38' -SourceSha $source
    Write-Host 'PASS valid fixture'
    Reject { Assert-HostedPreviewBuild -BuildDirectory $root -Version '0.4.39' -SourceSha $source } 'wrong version'
    Reject { Assert-HostedPreviewBuild -BuildDirectory $root -Version '0.4.38' -SourceSha ('b'*40) } 'wrong source'
    Write-Text (Join-Path $root 'native/graphreader-engine.exe') 'tampered fixture'
    Reject { Assert-HostedPreviewBuild -BuildDirectory $root -Version '0.4.38' -SourceSha $source } 'changed worker'
    Write-Text (Join-Path $root 'native/graphreader-engine.exe') 'fixture native/graphreader-engine.exe'
    Write-Text (Join-Path $root 'native/Rust-StandardLibrary-Notices.html') 'tampered notice'
    Reject { Assert-HostedPreviewBuild -BuildDirectory $root -Version '0.4.38' -SourceSha $source } 'changed notice'
} finally {
    if (([IO.Path]::GetFullPath($root)).StartsWith(([IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
