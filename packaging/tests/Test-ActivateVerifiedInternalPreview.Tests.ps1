# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Test-InternalPreviewUpload.ps1')
. (Join-Path $PSScriptRoot '../DevPortable.Common.ps1')
. (Join-Path $PSScriptRoot '../Activate-VerifiedInternalPreview.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Reject([scriptblock]$Operation) {
    $rejected = $false
    try { & $Operation } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected guarded operation to fail.' }
}

function Write-FixtureJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 12) + "`n"), [Text.UTF8Encoding]::new($false))
}

function New-FixtureArchive([string]$Tree, [string]$Destination) {
    $base = [IO.Path]::GetFullPath($Tree).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $archive = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-ActivationFiles $Tree)) {
            $name = $file.FullName.Substring($base.Length).Replace('\', '/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name)
        }
    } finally { $archive.Dispose() }
}

function New-TransactionFixture([string]$Root, [string]$LedgerSchema = 'valid', [bool]$IncludePortable = $true) {
    $repo = Join-Path $Root 'repo'; $bare = Join-Path $Root 'origin.git'
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    & git init --bare --initial-branch=main --quiet $bare
    if ($LASTEXITCODE -ne 0) { throw 'Fixture bare repository initialization failed.' }
    & git -C $repo init --initial-branch=main --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture repository initialization failed.' }
    & git -C $repo config user.email 'fixture@example.invalid'
    & git -C $repo config user.name 'Fixture'
    & git -C $repo remote add origin $bare
    [IO.File]::WriteAllText((Join-Path $repo 'source.txt'), 'owned fixture')
    & git -C $repo add source.txt
    & git -C $repo commit --quiet -m source
    if ($LASTEXITCODE -ne 0) { throw 'Fixture source commit failed.' }
    $source = (& git -C $repo rev-parse HEAD).Trim()
    $preview = Join-Path $repo 'artifacts/dev-portable'; $old = Join-Path $preview 'builds/old'; $stage = Join-Path $repo 'artifacts/stage'
    $tree = Join-Path $stage 'build'; $data = Join-Path $preview 'Data'
    foreach ($dir in @($old, $tree, $data, (Join-Path $repo 'docs'), (Join-Path $tree 'native'), (Join-Path $tree 'LICENSES'))) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [IO.File]::WriteAllText((Join-Path $old 'GraphReader.App.exe'), 'old')
    $oldHash = Get-Sha256 (Join-Path $old 'GraphReader.App.exe')
    $oldZipName='GraphAutoReader-0.4.56-win-x64-internal-preview.zip'; $oldArchive=Join-Path $Root 'old.zip'
    New-FixtureArchive $old $oldArchive
    New-Item -ItemType Directory -Path (Join-Path $old 'packages') | Out-Null
    [IO.File]::Move($oldArchive, (Join-Path $old ('packages/' + $oldZipName)))
    $oldZipHash = Get-Sha256 (Join-Path $old ('packages/' + $oldZipName))
    Write-FixtureJson (Join-Path $preview 'latest.json') @{schemaVersion=2;version='0.4.56';commit=$source;shortCommit=$source.Substring(0,8);buildTimeUtc='2026-01-01T00:00:00Z';buildDirectory='builds/old';executable='builds/old/GraphReader.App.exe';executableSha256=$oldHash;dataRoot='Data';openCvRuntime=@{}}
    foreach ($relative in @('portable.mode','GraphReader.App.exe','OpenCvSharpExtern.dll','reviewed-opencv-runtime.json','reviewed-opencv-preview-lineage.json','native/graphreader-engine.exe','native/Rust-StandardLibrary-Notices.html','LICENSES/OpenCV-4.13.0-License.txt','LICENSES/OpenCvSharp-4.13.0.20260627-License.txt','LICENSES/OpenCvSharpExtern-SourceBuilt-ThirdPartyNotices.txt','LICENSES/Microsoft-Visual-Cpp-2022-Redistribution-Reference.md')) { [IO.File]::WriteAllText((Join-Path $tree $relative), $relative) }
    if (-not $IncludePortable) { Remove-Item -LiteralPath (Join-Path $tree 'portable.mode') }
    $exe = Get-Sha256 (Join-Path $tree 'GraphReader.App.exe'); $native = Get-Sha256 (Join-Path $tree 'OpenCvSharpExtern.dll')
    Write-FixtureJson (Join-Path $tree 'native/rust-toolchain.json') @{schema='graphreader.rust-worker-build.v1';crt_linkage='static';worker_sha256=(Get-Sha256 (Join-Path $tree 'native/graphreader-engine.exe'));standard_library_notice_sha256=(Get-Sha256 (Join-Path $tree 'native/Rust-StandardLibrary-Notices.html'))}
    Write-FixtureJson (Join-Path $tree 'build-info.json') @{schemaVersion=2;version='0.4.57';commit=$source;shortCommit=$source.Substring(0,8);buildTimeUtc='2026-01-02T00:00:00Z';dirty=$false;runtimeMode='ManualPreview';executableSha256=$exe;openCvRuntime=@{releaseApproved=$false;binarySha256=$native}}
    $zipName='GraphAutoReader-0.4.57-win-x64-internal-preview.zip'; $zipPath=Join-Path $stage $zipName
    New-FixtureArchive $tree $zipPath
    $zipHash = Get-Sha256 $zipPath
    $ledgerFixture = @{builds=@(@{retained=$false},@{retained=$true;directory='new';version='0.4.57';commit=$source;executableSha256=$exe;openCvRuntimeSha256=$native;packages=@(@{name=$zipName;sha256=$zipHash})})}
    if ($LedgerSchema -eq 'valid') { $ledgerFixture.schemaVersion=1 }
    elseif ($LedgerSchema -eq 'invalid') { $ledgerFixture.schemaVersion=99 }
    Write-FixtureJson (Join-Path $repo 'docs/BUILD_LEDGER.json') $ledgerFixture
    & git -C $repo add docs/BUILD_LEDGER.json
    & git -C $repo commit --quiet -m ledger
    if ($LASTEXITCODE -ne 0) { throw 'Fixture ledger commit failed.' }
    $scope=(& git -C $repo rev-parse HEAD).Trim()
    & git -C $repo push --quiet -u origin main
    if ($LASTEXITCODE -ne 0) { throw 'Fixture local origin push failed.' }
    return @{RepositoryRoot=$repo;PrimaryRoot=$repo;StageRoot=$stage;Version='0.4.57';SourceSha=$source;ZipSha256=$zipHash;ScopeCommit=$scope;PreviousDirectory='old';PreviousExeSha256=$oldHash;PreviousZipSha256=$oldZipHash}
}

$fixtureParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))
$root = Join-Path $fixtureParent ('graphreader-activation-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $ledger = [pscustomobject]@{ builds = @(
        [pscustomobject]@{ retained=$false },
        [pscustomobject]@{ retained=$true; directory='new'; version='0.4.57'; commit=('a'*40); packages=@([pscustomobject]@{name='x.zip';sha256=('b'*64)}) }
    ) }
    Assert-ActivationLedger $ledger '0.4.57' ('a'*40) 'new' 'x.zip' ('b'*64)
    Reject { Assert-ActivationLedger $ledger '0.4.57' ('c'*40) 'new' 'x.zip' ('b'*64) }
    $pointer = Join-Path $root 'latest.json'
    Write-JsonFileAtomic $pointer @{ version='old' }
    Write-JsonFileAtomic $pointer @{ version='new' }
    if ((Get-Content $pointer -Raw | ConvertFrom-Json).version -ne 'new') { throw 'Pointer replacement failed.' }
    $tree = Join-Path $root 'tree'
    New-Item -ItemType Directory -Path $tree | Out-Null
    [IO.File]::WriteAllText((Join-Path $tree 'a.dll'), 'payload')
    $zipPath = Join-Path $root 'payload.zip'
    New-FixtureArchive $tree $zipPath
    if ((Assert-ActivationArchive $zipPath $tree '') -ne 1) { throw 'Archive count mismatch.' }
    $moved = Join-Path $root 'moved'
    [IO.Directory]::Move($tree, $moved)
    if ((Assert-ActivationArchive $zipPath $moved '') -ne 1) { throw 'Moved tree recovery failed.' }
    [IO.File]::WriteAllText((Join-Path $moved 'a.dll'), 'changed')
    Reject { Assert-ActivationArchive $zipPath $moved '' }
    $badZip = Join-Path $root 'bad.zip'
    $zip = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Create)
    try { $null = $zip.CreateEntry('../escape.dll'); $null = $zip.CreateEntry('Data/x.dll') } finally { $zip.Dispose() }
    $zip = [IO.Compression.ZipFile]::OpenRead($badZip)
    try { Reject { Assert-SafeZipEntries $zip } } finally { $zip.Dispose() }
    Assert-ActivationDataSnapshot @() @()
    Reject { Assert-ActivationDataSnapshot @() @('changed') }
    $helper = Join-Path $PSScriptRoot '../Activate-VerifiedInternalPreview.ps1'
    $normal = New-TransactionFixture (Join-Path $root 'normal')
    $latestPath = Join-Path $normal.RepositoryRoot 'artifacts/dev-portable/latest.json'
    $latestBytes = [IO.File]::ReadAllBytes($latestPath)
    foreach ($schema in @('missing', 'invalid')) {
        $latestObject = [Text.Encoding]::UTF8.GetString($latestBytes) | ConvertFrom-Json
        if ($schema -eq 'missing') { $latestObject.PSObject.Properties.Remove('schemaVersion') }
        else { $latestObject.schemaVersion = 99 }
        Write-FixtureJson $latestPath $latestObject
        $before = Get-Sha256 $latestPath
        Reject { & $helper -Action Activate @normal }
        if ((Get-Sha256 $latestPath) -cne $before -or -not (Test-Path -LiteralPath (Join-Path $normal.StageRoot 'build/GraphReader.App.exe')) -or (Test-Path -LiteralPath (Join-Path $normal.RepositoryRoot 'artifacts/dev-portable/builds/new'))) { throw 'Rejected pointer schema changed transaction state.' }
    }
    [IO.File]::WriteAllBytes($latestPath, $latestBytes)
    $noSentinel = New-TransactionFixture (Join-Path $root 'no-sentinel') 'valid' $false
    $noSentinelPointer = Join-Path $noSentinel.RepositoryRoot 'artifacts/dev-portable/latest.json'
    $before = Get-Sha256 $noSentinelPointer
    $sentinelFailure = ''
    try { & $helper -Action Activate @noSentinel } catch { $sentinelFailure = $_.Exception.Message }
    if ($sentinelFailure -cne 'Portable mode sentinel missing.') { throw "Unexpected sentinel refusal: $sentinelFailure" }
    if ((Get-Sha256 $noSentinelPointer) -cne $before -or -not (Test-Path -LiteralPath (Join-Path $noSentinel.StageRoot 'build/GraphReader.App.exe')) -or (Test-Path -LiteralPath (Join-Path $noSentinel.RepositoryRoot 'artifacts/dev-portable/builds/new'))) { throw 'Rejected sentinel changed transaction state.' }
    foreach ($schema in @('missing', 'invalid')) {
        $badSchema = New-TransactionFixture (Join-Path $root ('ledger-' + $schema)) $schema
        $badPointer = Join-Path $badSchema.RepositoryRoot 'artifacts/dev-portable/latest.json'
        $before = Get-Sha256 $badPointer
        Reject { & $helper -Action Activate @badSchema }
        if ((Get-Sha256 $badPointer) -cne $before -or -not (Test-Path -LiteralPath (Join-Path $badSchema.StageRoot 'build/GraphReader.App.exe')) -or (Test-Path -LiteralPath (Join-Path $badSchema.RepositoryRoot 'artifacts/dev-portable/builds/new'))) { throw 'Rejected ledger schema changed transaction state.' }
    }
    $wrong = $normal.Clone(); $wrong.ZipSha256 = 'f'*64
    Reject { & $helper -Action Activate @wrong }
    $wrong = $normal.Clone(); $wrong.ScopeCommit = 'f'*40
    Reject { & $helper -Action Activate @wrong }
    $ledgerFile = Join-Path $normal.RepositoryRoot 'docs/BUILD_LEDGER.json'
    $ledgerBytes = [IO.File]::ReadAllBytes($ledgerFile)
    [IO.File]::AppendAllText($ledgerFile, ' ')
    Reject { & $helper -Action Activate @normal }
    [IO.File]::WriteAllBytes($ledgerFile, $ledgerBytes)
    $badData = Join-Path $normal.StageRoot 'build/Data'
    New-Item -ItemType Directory -Path $badData | Out-Null
    Reject { & $helper -Action Activate @normal }
    Remove-Item -LiteralPath $badData
    if (-not (Test-Path -LiteralPath (Join-Path $normal.StageRoot 'build/GraphReader.App.exe'))) { throw 'A rejected transaction moved the payload.' }
    & $helper -Action Activate @normal
    $latest = Get-Content -LiteralPath (Join-Path $normal.RepositoryRoot 'artifacts/dev-portable/latest.json') -Raw | ConvertFrom-Json
    if ($latest.buildDirectory -cne 'builds/new' -or -not (Test-Path -LiteralPath (Join-Path $normal.RepositoryRoot 'artifacts/dev-portable/builds/old/GraphReader.App.exe'))) { throw 'Activation or old keeper preservation failed.' }
    $recovery = New-TransactionFixture (Join-Path $root 'recovery')
    [IO.Directory]::Move((Join-Path $recovery.StageRoot 'build'), (Join-Path $recovery.RepositoryRoot 'artifacts/dev-portable/builds/new'))
    & $helper -Action Activate @recovery
    $latest = Get-Content -LiteralPath (Join-Path $recovery.RepositoryRoot 'artifacts/dev-portable/latest.json') -Raw | ConvertFrom-Json
    if ($latest.buildDirectory -cne 'builds/new' -or -not (Test-Path -LiteralPath (Join-Path $recovery.RepositoryRoot 'artifacts/dev-portable/builds/old/GraphReader.App.exe'))) { throw 'Interrupted move recovery failed.' }
    Write-Host 'Activation primitive and transaction controls passed.'
} finally {
    Assert-ActivationChild $root $fixtureParent
    Assert-NoReparseAncestors $root
    $null = @(Get-ActivationFiles $root)
    Remove-Item -LiteralPath $root -Recurse -Force
}
