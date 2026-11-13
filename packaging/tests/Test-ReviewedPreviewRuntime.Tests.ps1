# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script = Join-Path $repo 'packaging\opencv-source\Import-ReviewedPreviewRuntime.ps1'
$root = Join-Path $repo ('artifacts\preview-runtime-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Write-Json($Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false)) }
function Assert-True($Condition, $Message) { if (-not $Condition) { throw $Message } }
try {
    $fixtureRepo = Join-Path $root 'fixture-repo'
    New-Item -ItemType Directory -Path (Join-Path $fixtureRepo 'docs'), (Join-Path $fixtureRepo 'packaging\opencv-source\review') -Force | Out-Null
    $dllBytes = [Text.Encoding]::UTF8.GetBytes('synthetic-fixture-opencv')
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $dllHash = ([BitConverter]::ToString($hasher.ComputeHash($dllBytes))).Replace('-', '').ToLowerInvariant() } finally { $hasher.Dispose() }
    $revisions = @{ openCvSharp='a'; openCv='b'; vcpkg='c' }
    $policy = @{schemaVersion=1; profileId='fixture'; binarySha256=$dllHash; overallReviewStatus='reviewed-provenance-only'; sourceRevisions=$revisions; noticeBundle=@{reviewStatus='complete'}; microsoftStaticRuntimeAttestation=@{status='recorded-private'}}
    Write-Json (Join-Path $fixtureRepo 'packaging\opencv-source\review\source-build-review-policy.json') $policy
    $runtime = @{schema='graphreader.reviewed-opencv-runtime.v1'; runtimeId='opencvsharpextern-source-audited'; profileId='fixture'; binarySha256=$dllHash; sourceRevisions=$revisions; provenanceValidated=$true; noticeReviewStatus='complete'; maintainerAttestationStatus='recorded-private'; cleanMachineEvidence=$false; releaseApproved=$false}
    $info = @{schemaVersion=2; version='0.4.37'; commit=('a'*40); dirty=$false; runtimeMode='ManualPreview'; executableSha256=('b'*64); openCvRuntime=$runtime}
    $noticeNames = @('LICENSES/OpenCV-4.13.0-License.txt','LICENSES/OpenCvSharp-4.13.0.20260627-License.txt','LICENSES/OpenCvSharpExtern-SourceBuilt-ThirdPartyNotices.txt','LICENSES/Microsoft-Visual-Cpp-2022-Redistribution-Reference.md')
    function New-Fixture([string]$Case) {
        $archive = Join-Path $root ("$Case.zip")
        $file = [IO.File]::Open($archive, [IO.FileMode]::CreateNew)
        $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $files = [ordered]@{'build-info.json'=($info | ConvertTo-Json -Depth 20); 'reviewed-opencv-runtime.json'=($runtime | ConvertTo-Json -Depth 20); 'OpenCvSharpExtern.dll'='synthetic-fixture-opencv'}
            foreach ($notice in $noticeNames) { $files[$notice] = 'synthetic-fixture-notice' }
            if ($Case -eq 'missing') { $files.Remove($noticeNames[0]) }
            if ($Case -eq 'invalid-metadata') { $files['build-info.json'] = ($info | ConvertTo-Json -Depth 20).Replace('ManualPreview','Production') }
            if ($Case -eq 'wrong-dll') { $files['OpenCvSharpExtern.dll'] = 'altered-fixture' }
            if ($Case -eq 'traversal') { $files['../escape'] = 'bad' }
            foreach ($name in $files.Keys) { $entry=$zip.CreateEntry($name); $writer=[IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false)); try{$writer.Write([string]$files[$name])}finally{$writer.Dispose()} }
            if ($Case -eq 'duplicate') { $entry=$zip.CreateEntry('LICENSES/opencv-4.13.0-license.txt'); $writer=[IO.StreamWriter]::new($entry.Open()); try{$writer.Write('bad')}finally{$writer.Dispose()} }
        } finally { $zip.Dispose(); $file.Dispose() }
        $archiveHash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Case -eq 'tampered-ledger') { $archiveHash = '0'*64 }
        $build = @{version='0.4.37';commit=('a'*40);executableSha256=('b'*64);openCvRuntimeSha256=$dllHash;releaseStatus='internal';recordIncomplete=$false;packages=@(@{name=[IO.Path]::GetFileName($archive);sha256=$archiveHash})}
        Write-Json (Join-Path $fixtureRepo 'docs\BUILD_LEDGER.json') @{schemaVersion=1;builds=@($build)}
        return $archive
    }
    foreach ($case in @('valid','tampered-ledger','missing','invalid-metadata','wrong-dll','traversal','duplicate')) {
        $archive=New-Fixture $case
        $output=Join-Path $root ("output-$case"); New-Item -ItemType Directory -Path $output | Out-Null
        $previous=$ErrorActionPreference; $ErrorActionPreference='Continue'
        try { $message = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -ArchivePath $archive -DestinationRoot $output -RepositoryRoot $fixtureRepo 2>&1; $code=$LASTEXITCODE } finally { $ErrorActionPreference=$previous }
        if ($case -eq 'valid') {
            Assert-True ($code -eq 0) "valid fixture failed: $message"
            Assert-True ((Get-FileHash (Join-Path $output 'OpenCvSharpExtern.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $dllHash) 'fixture DLL mismatch'
            $lineage=Get-Content (Join-Path $output 'reviewed-opencv-preview-lineage.json') -Raw | ConvertFrom-Json
            Assert-True (-not $lineage.releaseApproved -and -not $lineage.cleanMachineEvidence) 'fixture incorrectly approved'
        } else { Assert-True ($code -ne 0) "$case fixture unexpectedly accepted"; Assert-True (-not (Test-Path (Join-Path $output 'OpenCvSharpExtern.dll'))) "$case fixture wrote DLL" }
        Write-Host "PASS $case"
    }
} finally {
    if (([IO.Path]::GetFullPath($root)).StartsWith(([IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
