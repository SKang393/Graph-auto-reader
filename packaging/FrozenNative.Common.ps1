# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

function Assert-FrozenNativePath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Frozen publish path contains a reparse point: $cursor" }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Get-FrozenNativeSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-FrozenNativeFiles([string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    Assert-FrozenNativePath $rootPath
    $items = @(Get-ChildItem -LiteralPath $rootPath -Force -Recurse)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw 'Frozen publish contains a reparse point.' }
    $paths = [string[]]@($items | Where-Object { -not $_.PSIsContainer } | ForEach-Object { $_.FullName.Substring($rootPath.Length).TrimStart('\','/').Replace('\','/') })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    return @($paths | ForEach-Object {
        $file = Get-Item -LiteralPath (Join-Path $rootPath $_)
        [pscustomobject]@{ path=$_; size=[long]$file.Length; sha256=(Get-FrozenNativeSha256 $file.FullName) }
    })
}

function Assert-FrozenNativeFiles([string]$Root, [object]$Receipt, [string]$Version) {
    if ($Receipt.schema -cne 'graphreader.frozen-native-publish.v1' -or $Receipt.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $Receipt.version -cne $Version -or $Receipt.runtimeMode -cne 'Production' -or $Receipt.files -isnot [Array]) { throw 'Frozen publish receipt identity is invalid.' }
    $actual = @(Get-FrozenNativeFiles $Root)
    if ($actual.Count -ne @($Receipt.files).Count -or $actual.Count -eq 0) { throw 'Frozen publish file count differs.' }
    for ($i=0; $i -lt $actual.Count; $i++) {
        if ($actual[$i].path -cne [string]$Receipt.files[$i].path -or $actual[$i].size -ne [long]$Receipt.files[$i].size -or $actual[$i].sha256 -cne [string]$Receipt.files[$i].sha256) { throw "Frozen publish file differs: $($actual[$i].path)" }
    }
    if (@($actual | Where-Object { $_.path -ceq 'GraphReader.App.exe' }).Count -ne 1 -or @($actual | Where-Object { $_.path -ceq 'GraphReader.App.dll' }).Count -ne 1) { throw 'Frozen publish requires one application EXE and DLL.' }
    return $actual
}

function Assert-FrozenOpenCvDiagnostic([string]$Root, [object]$Receipt, [string]$Version) {
    Assert-FrozenNativePath $Root
    $entries = @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw 'OpenCV diagnostic contains a reparse point.' }
    $diagnosticExePath = Join-Path $Root 'GraphReader.App.exe'
    $productionExe = @($Receipt.files | Where-Object { $_.path -ceq 'GraphReader.App.exe' })
    if (-not (Test-Path -LiteralPath $diagnosticExePath -PathType Leaf) -or $productionExe.Count -ne 1) { throw 'OpenCV manual-preview host executable does not match the frozen production host.' }
    $diagnosticHash = Get-FrozenNativeSha256 $diagnosticExePath
    if ($diagnosticHash -cne $productionExe[0].sha256) { throw 'OpenCV manual-preview host executable does not match the frozen production host.' }
    $infoPath = Join-Path $Root 'build-info.json'
    if (-not (Test-Path -LiteralPath $infoPath -PathType Leaf)) { throw 'OpenCV diagnostic build-info.json is missing.' }
    $info = Get-Content -Raw -LiteralPath $infoPath | ConvertFrom-Json
    if ($info.schemaVersion -ne 2 -or $info.runtimeMode -cne 'ManualPreview' -or $info.commit -cne $Receipt.sourceCommit -or $info.version -cne $Version -or $info.dirty -isnot [bool] -or $info.dirty -ne $false -or $info.executableSha256 -cne $diagnosticHash -or @($info.availableModelIds).Count -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Root 'portable.mode') -PathType Leaf) -or @($entries | Where-Object { -not $_.PSIsContainer -and $_.Extension -in @('.onnx','.param','.bin') }).Count -ne 0) { throw 'OpenCV diagnostic payload is not the clean model-free source-A preview.' }
}

function Assert-FrozenNativeAuditTransition([string]$RepositoryRoot, [string]$SourceCommit, [string]$EvidenceCommit) {
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $EvidenceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Frozen source and native evidence commits must be canonical.' }
    & git -C $RepositoryRoot merge-base --is-ancestor $SourceCommit $EvidenceCommit
    if ($LASTEXITCODE -ne 0) { throw 'Frozen source commit is not an ancestor of native evidence commit.' }
    $auditPath = 'packaging/common/release-audit.json'
    $sourceText = & git -C $RepositoryRoot show "$($SourceCommit):$auditPath"
    if ($LASTEXITCODE -ne 0) { throw 'Frozen source audit is unavailable.' }
    $evidenceText = & git -C $RepositoryRoot show "$($EvidenceCommit):$auditPath"
    if ($LASTEXITCODE -ne 0) { throw 'Native evidence audit is unavailable.' }
    $source = ($sourceText -join "`n").TrimStart([char]0xfeff) | ConvertFrom-Json
    $evidence = ($evidenceText -join "`n").TrimStart([char]0xfeff) | ConvertFrom-Json
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null = $allowed.Add($auditPath)
    foreach ($id in @('opencv-clean-machine-load','pdfium-clean-machine-load')) {
        $old = @($source.mandatoryEvidenceGates | Where-Object { $_.id -ceq $id })
        $new = @($evidence.mandatoryEvidenceGates | Where-Object { $_.id -ceq $id })
        if ($old.Count -ne 1 -or $new.Count -ne 1 -or $old[0].status -cne 'blocked' -or $new[0].status -cne 'pass' -or @($new[0].evidence).Count -ne 1) { throw "Native evidence transition is invalid: $id" }
        $report = [string]$new[0].evidence[0].path
        if ([IO.Path]::IsPathRooted($report) -or $report.Contains('\') -or $report.Contains(':') -or @($report.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or [string]$new[0].evidence[0].sha256 -cnotmatch '^[0-9a-f]{64}$') { throw "Native report path or checksum is invalid: $id" }
        Assert-FrozenNativePath (Join-Path $RepositoryRoot $report)
        $null = $allowed.Add($report)
        foreach ($field in @('status','evidence','notes')) { $old[0].$field = $new[0].$field }
    }
    if (($source | ConvertTo-Json -Depth 100 -Compress) -cne ($evidence | ConvertTo-Json -Depth 100 -Compress)) { throw 'Native evidence commit changes non-native audit content.' }
    $paths = @(& git -C $RepositoryRoot diff --no-renames --name-only $SourceCommit $EvidenceCommit)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the native evidence transition.' }
    foreach ($path in $paths) { if (-not $allowed.Contains([string]$path)) { throw "Native evidence commit changes source or unrelated files: $path" } }
}
