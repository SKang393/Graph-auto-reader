# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Activate')][string]$Action = 'Inspect',
    [string]$RepositoryRoot, [string]$PrimaryRoot, [string]$StageRoot,
    [string]$Version, [string]$SourceSha, [string]$ZipSha256,
    [string]$ScopeCommit, [string]$PreviousDirectory, [string]$PreviousExeSha256, [string]$PreviousZipSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ActivationChild([string]$Path, [string]$Root) {
    $child = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $parent = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not $child.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Activation path escaped its approved root.'
    }
}

function Get-ActivationFiles([string]$Root) {
    Assert-NoReparseAncestors $Root
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    while ($pending.Count -gt 0) {
        foreach ($item in [IO.DirectoryInfo]::new($pending.Pop()).EnumerateFileSystemInfos()) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point in activation tree.' }
            if (($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($item.FullName) }
            else { $files.Add([IO.FileInfo]$item) }
        }
    }
    return $files.ToArray()
}

function Assert-ActivationArchive([string]$ArchivePath, [string]$Tree, [string]$IgnoredRelative) {
    Assert-NoReparseAncestors $ArchivePath
    $base = [IO.Path]::GetFullPath($Tree).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $files = @{}
    foreach ($file in @(Get-ActivationFiles $Tree)) {
        $relative = $file.FullName.Substring($base.Length).Replace('\', '/')
        if ($relative -ieq $IgnoredRelative) { continue }
        if ($files.ContainsKey($relative)) { throw 'Duplicate extracted path.' }
        $files.Add($relative, $file)
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        Assert-SafeZipEntries $zip
        $count = 0
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not $files.ContainsKey($entry.FullName)) { throw 'Archive entry missing from extracted tree.' }
            $file = $files[$entry.FullName]
            if ($file.Length -ne $entry.Length -or (Get-Sha256 $file.FullName) -cne (Get-ZipEntrySha256 $entry)) { throw 'Archive payload mismatch.' }
            $count++
        }
        if ($count -ne $files.Count) { throw 'Unexpected extracted file.' }
        return $count
    } finally { $zip.Dispose() }
}

function Get-ActivationDataSnapshot([string]$Root) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return @(@(Get-ActivationFiles $Root) | Sort-Object FullName | ForEach-Object {
        '{0}|{1}|{2}' -f $_.FullName.Substring($base.Length), $_.Length, (Get-Sha256 $_.FullName)
    })
}

function Assert-ActivationDataSnapshot([string[]]$Expected, [string[]]$Actual) {
    if ($Expected.Count -ne $Actual.Count) { throw 'Shared Data changed.' }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Expected[$index] -cne $Actual[$index]) { throw 'Shared Data changed.' }
    }
}

function Assert-ActivationDisjoint([string]$First, [string]$Second) {
    $a = [IO.Path]::GetFullPath($First).TrimEnd('\', '/')
    $b = [IO.Path]::GetFullPath($Second).TrimEnd('\', '/')
    if ($a.Equals($b, [StringComparison]::OrdinalIgnoreCase) -or
        $a.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $b.StartsWith($a + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Activation paths overlap.' }
}

function Assert-ActivationNoConsumers([string[]]$Roots) {
    foreach ($process in @(Get-CimInstance Win32_Process -ErrorAction Stop)) {
        if ($process.ProcessId -eq $PID) { continue }
        $exe = [string]$process.ExecutablePath
        $command = [string]$process.CommandLine
        foreach ($root in $Roots) {
            if (($exe -and $exe.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) -or
                ($command -and $command.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0)) { throw 'Active preview consumer.' }
        }
        if ($process.Name -match '^(GraphReader\.|graphreader-engine)' -and (-not $exe -or -not $process.ProcessId)) { throw 'Unknown preview consumer identity.' }
    }
}

function Assert-ActivationLedger($Ledger, [string]$Version, [string]$Source, [string]$Directory, [string]$ZipName, [string]$ZipHash) {
    $retained = @($Ledger.builds | Where-Object { $_.retained -eq $true })
    if ($retained.Count -ne 1) { throw 'Ledger must retain exactly one build.' }
    $entry = $retained[0]
    if (-not $entry.PSObject.Properties['directory'] -or $entry.directory -cne $Directory -or
        $entry.version -cne $Version -or $entry.commit -cne $Source) { throw 'Retained ledger identity mismatch.' }
    $packages = @($entry.packages | Where-Object { $_.name -ceq $ZipName })
    if ($packages.Count -ne 1 -or $packages[0].sha256 -cne $ZipHash) { throw 'Ledger package identity mismatch.' }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($SourceSha -cnotmatch '^[0-9a-f]{40}$' -or $ScopeCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $ZipSha256 -cnotmatch '^[0-9a-f]{64}$' -or $PreviousExeSha256 -cnotmatch '^[0-9a-f]{64}$' -or $PreviousZipSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $Version -notmatch '^\d+\.\d+\.\d+$' -or $PreviousDirectory -notmatch '^[A-Za-z0-9.-]+$') { throw 'Missing or malformed activation identity.' }
    foreach ($path in @($RepositoryRoot, $PrimaryRoot, $StageRoot)) { if ([string]::IsNullOrWhiteSpace($path)) { throw 'Missing activation root.' } }
    $repo = [IO.Path]::GetFullPath($RepositoryRoot)
    $primary = [IO.Path]::GetFullPath($PrimaryRoot)
    $stage = [IO.Path]::GetFullPath($StageRoot)
    $preview = Join-Path $primary 'artifacts\dev-portable'
    $builds = Join-Path $preview 'builds'
    $data = Join-Path $preview 'Data'
    . (Join-Path $PSScriptRoot 'Build-HostedInternalPreview.ps1') -ExpectedVersion $Version -ExpectedSourceSha $SourceSha
    . (Join-Path $PSScriptRoot 'DevPortable.Common.ps1')
    Assert-ActivationChild $stage (Join-Path $repo 'artifacts')
    foreach ($path in @($repo, $primary, $stage, $preview, $builds, $data)) { Assert-NoReparseAncestors $path }
    $remote = @(& git -C $repo ls-remote origin refs/heads/main)
    if ($LASTEXITCODE -ne 0 -or $remote.Count -ne 1) { throw 'Cannot verify remote main.' }
    $remoteSha = ($remote[0] -split '\s+')[0]
    $tracking = (& git -C $repo rev-parse origin/main).Trim()
    if ($LASTEXITCODE -ne 0 -or $remoteSha -cne $tracking) { throw 'Remote main differs from tracking reference.' }
    & git -C $repo merge-base --is-ancestor $ScopeCommit $remoteSha
    if ($LASTEXITCODE -ne 0) { throw 'Scope commit is not pushed.' }
    & git -C $repo merge-base --is-ancestor $SourceSha $ScopeCommit
    if ($LASTEXITCODE -ne 0) { throw 'Build source is not in the pushed scope.' }
    $ledgerPath = 'docs/BUILD_LEDGER.json'
    $scopeBlob = (& git -C $repo rev-parse "${ScopeCommit}:$ledgerPath").Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Scope ledger missing.' }
    $remoteBlob = (& git -C $repo rev-parse "${remoteSha}:$ledgerPath").Trim()
    if ($LASTEXITCODE -ne 0 -or $remoteBlob -cne $scopeBlob) { throw 'Remote ledger differs from scope.' }
    foreach ($root in @($repo, $primary)) {
        $blob = (& git -C $root hash-object (Join-Path $root $ledgerPath)).Trim()
        if ($LASTEXITCODE -ne 0 -or $blob -cne $scopeBlob) { throw 'Working ledger differs from pushed scope.' }
    }
    $ledger = Get-Content -LiteralPath (Join-Path $repo $ledgerPath) -Raw | ConvertFrom-Json
    if (-not $ledger.PSObject.Properties['schemaVersion'] -or $ledger.schemaVersion -ne 1) { throw 'Unsupported ledger schema.' }
    $retainedEntries = @($ledger.builds | Where-Object { $_.retained -eq $true })
    if ($retainedEntries.Count -ne 1 -or -not $retainedEntries[0].PSObject.Properties['directory']) { throw 'Retained ledger target missing.' }
    $name = [string]$retainedEntries[0].directory
    if ($name -notmatch '^[A-Za-z0-9.-]+$' -or $name -ceq $PreviousDirectory) { throw 'Invalid target directory.' }
    $zipName = "GraphAutoReader-$Version-win-x64-internal-preview.zip"
    Assert-ActivationLedger $ledger $Version $SourceSha $name $zipName $ZipSha256
    $target = Join-Path $builds $name
    $old = Join-Path $builds $PreviousDirectory
    $stageTree = Join-Path $stage 'build'
    $stageZip = Join-Path $stage $zipName
    $targetZip = Join-Path $target ('packages\' + $zipName)
    foreach ($protected in @($old, $target, $data)) { Assert-ActivationDisjoint $stage $protected }
    foreach ($path in @($target, $old)) { Assert-ActivationChild $path $builds; Assert-NoReparseAncestors $path }
    Assert-NoReparseAncestors $stageTree
    Assert-NoReparseAncestors $stageZip
    $latestPath = Join-Path $preview 'latest.json'
    Assert-NoReparseAncestors $latestPath
    $latest = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
    if (-not $latest.PSObject.Properties['schemaVersion'] -or $latest.schemaVersion -ne 2) { throw 'Unsupported latest pointer schema.' }
    if ($latest.buildDirectory -cne ('builds/' + $PreviousDirectory) -or $latest.executableSha256 -cne $PreviousExeSha256 -or
        (Get-Sha256 (Join-Path $old 'GraphReader.App.exe')) -cne $PreviousExeSha256) { throw 'Previous keeper or pointer mismatch.' }
    if ($latest.version -notmatch '^\d+\.\d+\.\d+$' -or (Test-Path -LiteralPath (Join-Path $old 'Data'))) { throw 'Previous keeper identity or Data invalid.' }
    $oldZipName = 'GraphAutoReader-' + $latest.version + '-win-x64-internal-preview.zip'
    $oldZip = Join-Path $old ('packages\' + $oldZipName)
    if ((Get-Sha256 $oldZip) -cne $PreviousZipSha256) { throw 'Previous keeper ZIP mismatch.' }
    $null = Assert-ActivationArchive $oldZip $old ('packages/' + $oldZipName)
    $treeInStage = Test-Path -LiteralPath $stageTree -PathType Container
    $treeInTarget = Test-Path -LiteralPath $target -PathType Container
    if ($treeInStage -eq $treeInTarget) { throw 'Expected exactly one staged or moved tree.' }
    $zipInStage = Test-Path -LiteralPath $stageZip -PathType Leaf
    $zipInTarget = Test-Path -LiteralPath $targetZip -PathType Leaf
    if ($zipInStage -eq $zipInTarget -or ($treeInStage -and $zipInTarget)) { throw 'Expected exactly one staged or moved ZIP.' }
    $tree = if ($treeInStage) { $stageTree } else { $target }
    $zip = if ($zipInStage) { $stageZip } else { $targetZip }
    if (Test-Path -LiteralPath (Join-Path $tree 'Data')) { throw 'Mutable Data found in package.' }
    if (-not (Test-Path -LiteralPath (Join-Path $tree 'portable.mode') -PathType Leaf)) { throw 'Portable mode sentinel missing.' }
    if ((Get-Sha256 $zip) -cne $ZipSha256) { throw 'Package hash mismatch.' }
    $ignored = if ($zipInTarget) { 'packages/' + $zipName } else { '' }
    $count = Assert-ActivationArchive $zip $tree $ignored
    Assert-HostedPreviewBuild -BuildDirectory $tree -Version $Version -SourceSha $SourceSha
    $info = Get-Content -LiteralPath (Join-Path $tree 'build-info.json') -Raw | ConvertFrom-Json
    if ($info.openCvRuntime.releaseApproved -ne $false) { throw 'Runtime approval state is not ManualPreview.' }
    if (-not $retainedEntries[0].PSObject.Properties['executableSha256'] -or
        $retainedEntries[0].executableSha256 -cne $info.executableSha256 -or
        (Get-Sha256 (Join-Path $tree 'GraphReader.App.exe')) -cne $info.executableSha256) { throw 'Ledger executable mismatch.' }
    if ($retainedEntries[0].PSObject.Properties['openCvRuntimeSha256'] -and
        ($retainedEntries[0].openCvRuntimeSha256 -cne (Get-Sha256 (Join-Path $tree 'OpenCvSharpExtern.dll')) -or
         $retainedEntries[0].openCvRuntimeSha256 -cne $info.openCvRuntime.binarySha256)) { throw 'Ledger runtime mismatch.' }
    Assert-ActivationNoConsumers @($stage, $old, $target)
    $before = @(Get-ActivationDataSnapshot $data)
    if ($Action -eq 'Activate') {
        if ($treeInStage) { [IO.Directory]::Move($stageTree, $target) }
        if ($zipInStage) {
            $packages = Join-Path $target 'packages'
            if (-not (Test-Path -LiteralPath $packages)) { [IO.Directory]::CreateDirectory($packages) | Out-Null }
            Assert-NoReparseAncestors $packages
            [IO.File]::Move($stageZip, $targetZip)
        }
        if ((Get-Sha256 $targetZip) -cne $ZipSha256) { throw 'Moved ZIP hash mismatch.' }
        $null = Assert-ActivationArchive $targetZip $target ('packages/' + $zipName)
        Assert-ActivationDataSnapshot $before @(Get-ActivationDataSnapshot $data)
        $latest.version = $info.version; $latest.commit = $info.commit; $latest.shortCommit = $info.shortCommit
        $latest.buildTimeUtc = $info.buildTimeUtc; $latest.buildDirectory = 'builds/' + $name
        $latest.executable = $latest.buildDirectory + '/GraphReader.App.exe'; $latest.executableSha256 = $info.executableSha256
        $latest.openCvRuntime = $info.openCvRuntime
        Write-JsonFileAtomic -Path $latestPath -Value $latest
        Assert-ActivationDataSnapshot $before @(Get-ActivationDataSnapshot $data)
    }
    [pscustomobject]@{ action=$Action; target=$name; payloadFiles=$count; sharedDataFiles=$before.Count; previousRetained=$true } | ConvertTo-Json -Compress
}
