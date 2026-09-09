# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ToolPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $InputManifestPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $CandidatePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $CandidateSha256
)

$ErrorActionPreference = 'Stop'

function Write-JsonFile([string] $Path, [object] $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Invoke-BoundaryCase([hashtable] $Case, [string] $ManifestPath, [string] $ExpectedSha) {
    $outputPath = if ($Case.ContainsKey('OutputPath')) { $Case.OutputPath } else { Join-Path $script:ScratchRoot ("output-" + $Case.Name) }
    if (Test-Path -LiteralPath $outputPath) {
        throw "Scratch output already exists: $outputPath"
    }

    $candidatePath = if ($Case.ContainsKey('CandidatePath')) { $Case.CandidatePath } else { $CandidatePath }
    $arguments = @($ToolPath, $ManifestPath, $candidatePath, $ExpectedSha, $outputPath)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $combined = ($stderr + "`n" + $stdout).Trim()
    $created = Test-Path -LiteralPath $outputPath
    $markerFound = $combined -match [regex]::Escape($Case.ErrorMarker)
    if ($process.ExitCode -eq 0 -or -not $markerFound -or $created) {
        throw ("exit={0}; marker={1}; output-created={2}; expected='{3}'; output='{4}'" -f `
            $process.ExitCode, $markerFound, $created, $Case.ErrorMarker, $combined)
    }

    [pscustomobject]@{
        Name = $Case.Name
        ExitCode = $process.ExitCode
        ErrorMarker = $Case.ErrorMarker
        OutputCreated = $created
    }
}

$toolFullPath = [IO.Path]::GetFullPath($ToolPath)
$inputFullPath = [IO.Path]::GetFullPath($InputManifestPath)
$candidateFullPath = [IO.Path]::GetFullPath($CandidatePath)
foreach ($requiredPath in @($toolFullPath, $inputFullPath, $candidateFullPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required input does not exist: $requiredPath"
    }
}
$ToolPath = $toolFullPath
$InputManifestPath = $inputFullPath
$CandidatePath = $candidateFullPath

$script:ScratchRoot = Join-Path $PSScriptRoot ("..\..\artifacts\test-temp\input-boundary-" + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
$script:ScratchRoot = [IO.Path]::GetFullPath($script:ScratchRoot)
New-Item -ItemType Directory -Path $script:ScratchRoot -Force | Out-Null

$sourceManifest = Get-Content -LiteralPath $InputManifestPath -Raw | ConvertFrom-Json
$firstImage = $sourceManifest.images[0]
$sourceImagePath = Join-Path (Split-Path -Parent $InputManifestPath) $firstImage.image
$scratchImagePath = Join-Path $script:ScratchRoot $firstImage.image
Copy-Item -LiteralPath $sourceImagePath -Destination $scratchImagePath

$baseManifest = [ordered]@{}
foreach ($property in $sourceManifest.PSObject.Properties) {
    if ($property.Name -eq 'images') {
        $baseManifest[$property.Name] = @($firstImage)
    } else {
        $baseManifest[$property.Name] = $property.Value
    }
}
$baseManifestJson = $baseManifest | ConvertTo-Json -Depth 20
$forgedCandidatePath = Join-Path $script:ScratchRoot 'candidate-forged-native-scope.json'
$forgedCandidate = Get-Content -LiteralPath $CandidatePath -Raw | ConvertFrom-Json
$forgedCandidate.native_scope = 'forged-local-diagnostic-scope'
Write-JsonFile $forgedCandidatePath $forgedCandidate
$forgedCandidateSha = (Get-FileHash -LiteralPath $forgedCandidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
$outsideOutputPath = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) `
    ('outside-artifacts-input-boundary-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))

$cases = @(
    @{ Name = 'sealed-split'; ErrorMarker = 'Only annotation-free project-owned synthetic train/dev raster inputs are accepted.'; Mutate = { param($m) $m.split = 'sealed' } },
    @{ Name = 'contains-truth'; ErrorMarker = 'Only annotation-free project-owned synthetic train/dev raster inputs are accepted.'; Mutate = { param($m) $m.contains_truth = $true } },
    @{ Name = 'precomputed-masks'; ErrorMarker = 'Only annotation-free project-owned synthetic train/dev raster inputs are accepted.'; Mutate = { param($m) $m.contains_precomputed_masks = $true } },
    @{ Name = 'extra-truth-field'; ErrorMarker = 'Unexpected or duplicate synthetic input fields'; Mutate = { param($m) Add-Member -InputObject $m -MemberType NoteProperty -Name truth -Value @() } },
    @{ Name = 'wrong-candidate-hash'; ErrorMarker = 'Candidate descriptor checksum mismatch.'; Mutate = { param($m) } },
    @{ Name = 'corrupt-png-hash'; ErrorMarker = 'Input checksum mismatch:'; Mutate = { param($m) } },
    @{ Name = 'escaped-image-basename'; ErrorMarker = 'Synthetic raster names must be unique local PNG basenames'; Mutate = { param($m) $m.images[0].image = '..\escaped.png' } },
    @{ Name = 'forged-native-scope'; ErrorMarker = 'Native scope does not match the pinned runtime bytes.'; CandidatePath = $forgedCandidatePath; CandidateSha256 = $forgedCandidateSha; Mutate = { param($m) } },
    @{ Name = 'outside-artifacts-output'; ErrorMarker = 'Synthetic evidence output must stay under'; OutputPath = $outsideOutputPath; Mutate = { param($m) } }
)

$results = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($case in $cases) {
    Copy-Item -LiteralPath $sourceImagePath -Destination $scratchImagePath -Force
    $manifest = $baseManifestJson | ConvertFrom-Json
    & $case.Mutate $manifest
    if ($case.Name -eq 'corrupt-png-hash') {
        $corruptBytes = [IO.File]::ReadAllBytes($scratchImagePath)
        $corruptBytes[0] = $corruptBytes[0] -bxor 0x01
        [IO.File]::WriteAllBytes($scratchImagePath, $corruptBytes)
    }
    $manifestPath = Join-Path $script:ScratchRoot ($case.Name + '.json')
    Write-JsonFile $manifestPath ([pscustomobject]$manifest)
    $expectedSha = if ($case.ContainsKey('CandidateSha256')) { $case.CandidateSha256 } else { $CandidateSha256 }
    if ($case.Name -eq 'wrong-candidate-hash') { $expectedSha = ('0' * 64) }
    try {
        $results.Add((Invoke-BoundaryCase $case $manifestPath $expectedSha))
    } catch {
        $failures.Add(("{0}: {1}" -f $case.Name, $_.Exception.Message))
    }
}

$report = [ordered]@{
    Schema = 'graphreader.synthetic-runtime-input-boundary-check.v1'
    Count = $cases.Count
    Passed = $results.Count
    Failed = $failures.Count
    ScratchRoot = $script:ScratchRoot
    Cases = $results
    Failures = $failures
}
$reportPath = Join-Path $script:ScratchRoot 'verification-report.json'
Write-JsonFile $reportPath ([pscustomobject]$report)
Write-Output ("Input boundary checks: {0}/{1} passed. Report: {2}" -f $results.Count, $cases.Count, $reportPath)
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}
exit 0
