# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}
$repositoryPath = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$cratePath = Join-Path $repositoryPath 'native\graphreader-engine'
$targetPath = Join-Path $repositoryPath 'artifacts\native-engine'
$compiler = @(& rustc --version --verbose)
if ($LASTEXITCODE -ne 0 -or -not ($compiler -match '^host: x86_64-pc-windows-msvc$')) {
    throw 'Graph Auto Reader requires the Windows x64 Rust MSVC toolchain.'
}
$rustSysroot = & rustc --print sysroot
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the Rust toolchain notices.' }
$noticeSource = Join-Path $rustSysroot 'share\doc\rust\COPYRIGHT-library.html'
if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) {
    throw 'The Rust standard-library copyright notice is missing. Install the complete Rust toolchain.'
}

Push-Location $repositoryPath
try {
    & cargo build --manifest-path (Join-Path $cratePath 'Cargo.toml') --target-dir $targetPath --release --offline --locked
    if ($LASTEXITCODE -ne 0) { throw 'Rust worker build failed.' }
}
finally { Pop-Location }

$releasePath = Join-Path $targetPath 'release'
$workerPath = Join-Path $releasePath 'graphreader-engine.exe'
$noticePath = Join-Path $releasePath 'Rust-StandardLibrary-Notices.html'
Copy-Item -LiteralPath $noticeSource -Destination $noticePath -Force
$metadata = [ordered]@{
    schema = 'graphreader.rust-worker-build.v1'
    compiler = ($compiler -join [Environment]::NewLine)
    worker_sha256 = Get-Sha256 $workerPath
    standard_library_notice_sha256 = Get-Sha256 $noticePath
    cargo_lock_sha256 = Get-Sha256 (Join-Path $cratePath 'Cargo.lock')
    crt_linkage = 'static'
}
[IO.File]::WriteAllText((Join-Path $releasePath 'rust-toolchain.json'),
    ($metadata | ConvertTo-Json -Depth 3) + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false)))
Write-Output ("Rust worker: {0}; standard-library notices: {1}" -f $metadata.worker_sha256, $metadata.standard_library_notice_sha256)
