# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$reviewRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectRoot = [IO.Path]::GetFullPath((Join-Path $reviewRoot '..\..\..'))
$validator = Join-Path $reviewRoot 'Test-PdfiumDependencyReviewPolicy.ps1'
$sourcePolicy = Join-Path $reviewRoot 'dependency-review-policy.json'
$sourceNotice = Join-Path $reviewRoot 'third-party-notices.dependency-mapped.txt'
$sourceEvidence = Join-Path $projectRoot 'artifacts\pdfium-source\evidence-first'
$sourceCheckout = Join-Path $projectRoot 'artifacts\pdfium-source\sources\pdfium'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryParent ('GraphReader-PdfiumReviewTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

$passed = 0

function Write-JsonUtf8NoBom {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] $Value)
    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function New-EvidenceFixture {
    param([Parameter(Mandatory)] [string]$Name)

    $root = Join-Path $temporaryRoot $Name
    [IO.Directory]::CreateDirectory((Join-Path $root 'bin')) | Out-Null
    foreach ($name in @('source-lock.json', 'build-manifest.json', 'target-dependencies.txt', 'pe-imports.txt')) {
        Copy-Item -LiteralPath (Join-Path $sourceEvidence $name) -Destination (Join-Path $root $name)
    }
    Copy-Item -LiteralPath (Join-Path $sourceEvidence 'bin\graphreader_pdfium_renderer.exe') -Destination (Join-Path $root 'bin\graphreader_pdfium_renderer.exe')
    $policyPath = Join-Path $root 'policy.json'
    $noticePath = Join-Path $root 'notice.txt'
    Copy-Item -LiteralPath $sourcePolicy -Destination $policyPath
    Copy-Item -LiteralPath $sourceNotice -Destination $noticePath
    Copy-Item -LiteralPath (Join-Path $reviewRoot 'target-dependencies.reviewed.txt') -Destination (Join-Path $root 'target-dependencies.reviewed.txt')
    Copy-Item -LiteralPath (Join-Path $reviewRoot 'pe-imports.reviewed.txt') -Destination (Join-Path $root 'pe-imports.reviewed.txt')
    return [pscustomobject]@{
        Root = $root
        Policy = $policyPath
        Notice = $noticePath
    }
}

function Invoke-Validator {
    param(
        [Parameter(Mandatory)] [string]$EvidenceRoot,
        [Parameter(Mandatory)] [string]$PolicyPath,
        [Parameter(Mandatory)] [string]$NoticePath
    )

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $validator `
            -EvidenceRoot $EvidenceRoot `
            -SourceRoot $sourceCheckout `
            -PolicyPath $PolicyPath `
            -NoticePath $NoticePath 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output -join [Environment]::NewLine)
    }
}

function Assert-Case {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [scriptblock]$Action
    )

    & $Action
    $script:passed++
    Write-Host "PASS $Name"
}

try {
    Assert-Case 'Rebound renderer requires new exact clean-machine evidence' {
        $audit = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging\common\release-audit.json') -Raw | ConvertFrom-Json
        $renderer = @($audit.components | Where-Object { $_.id -eq 'pdfium-native' })
        $gate = @($audit.mandatoryEvidenceGates | Where-Object { $_.id -eq 'pdfium-clean-machine-load' })
        if ($renderer.Count -ne 1 -or $gate.Count -ne 1 -or
            $renderer[0].artifactSha256 -cne 'e61fa4e46ada9ae3e9040865674c35c3abdb5722031d5c9447a54155e483a468' -or
            $renderer[0].reviewStatus -cne 'reviewed' -or $gate[0].status -cne 'blocked' -or
            @($gate[0].evidence).Count -ne 0) {
            throw 'The E61 renderer identity or its blocked clean-machine gate differs.'
        }
    }

    Assert-Case 'Exact retained dependency evidence passes without approval' {
        $result = Invoke-Validator -EvidenceRoot $sourceEvidence -PolicyPath $sourcePolicy -NoticePath $sourceNotice
        if ($result.ExitCode -ne 0 -or $result.Output -notmatch 'approval remains false') {
            throw "Expected retained evidence to pass fail-closed review. $($result.Output)"
        }
    }

    Assert-Case 'Unknown target dependency remains unmapped' {
        $fixture = New-EvidenceFixture -Name 'unknown-dependency'
        [IO.File]::AppendAllText(
            (Join-Path $fixture.Root 'target-dependencies.txt'),
            '//third_party/unknown:linked' + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        $targetHash = (Get-FileHash -LiteralPath (Join-Path $fixture.Root 'target-dependencies.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifestPath = Join-Path $fixture.Root 'build-manifest.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifest.targetDependenciesSha256 = $targetHash
        Write-JsonUtf8NoBom -Path $manifestPath -Value $manifest
        $policy = Get-Content -LiteralPath $fixture.Policy -Raw | ConvertFrom-Json
        $policy.evidenceBinding.targetDependenciesSha256 = $targetHash
        Copy-Item -LiteralPath (Join-Path $fixture.Root 'target-dependencies.txt') -Destination (Join-Path $fixture.Root 'target-dependencies.reviewed.txt') -Force
        $policy.reviewInventory.targetDependenciesSha256 = $targetHash
        $policy.evidenceBinding.buildManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-JsonUtf8NoBom -Path $fixture.Policy -Value $policy
        $result = Invoke-Validator -EvidenceRoot $fixture.Root -PolicyPath $fixture.Policy -NoticePath $fixture.Notice
        if ($result.ExitCode -eq 0 -or $result.Output -notmatch 'map to exactly one component') {
            throw "Expected an unmapped dependency failure. $($result.Output)"
        }
    }

    Assert-Case 'Unexpected PE import is rejected' {
        $fixture = New-EvidenceFixture -Name 'unexpected-import'
        [IO.File]::AppendAllText(
            (Join-Path $fixture.Root 'pe-imports.txt'),
            '    NETWORK.dll' + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        $importsHash = (Get-FileHash -LiteralPath (Join-Path $fixture.Root 'pe-imports.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifestPath = Join-Path $fixture.Root 'build-manifest.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifest.peImportsSha256 = $importsHash
        Write-JsonUtf8NoBom -Path $manifestPath -Value $manifest
        $policy = Get-Content -LiteralPath $fixture.Policy -Raw | ConvertFrom-Json
        $policy.evidenceBinding.peImportsSha256 = $importsHash
        Copy-Item -LiteralPath (Join-Path $fixture.Root 'pe-imports.txt') -Destination (Join-Path $fixture.Root 'pe-imports.reviewed.txt') -Force
        $policy.reviewInventory.peImportsSha256 = $importsHash
        $policy.evidenceBinding.buildManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-JsonUtf8NoBom -Path $fixture.Policy -Value $policy
        $result = Invoke-Validator -EvidenceRoot $fixture.Root -PolicyPath $fixture.Policy -NoticePath $fixture.Notice
        if ($result.ExitCode -eq 0 -or $result.Output -notmatch 'PE import set') {
            throw "Expected an unexpected PE import failure. $($result.Output)"
        }
    }

    Assert-Case 'Notice tampering is rejected' {
        $fixture = New-EvidenceFixture -Name 'notice-tampering'
        [IO.File]::AppendAllText($fixture.Notice, 'tampered' + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        $result = Invoke-Validator -EvidenceRoot $fixture.Root -PolicyPath $fixture.Policy -NoticePath $fixture.Notice
        if ($result.ExitCode -eq 0 -or $result.Output -notmatch 'notice hash mismatch') {
            throw "Expected notice hash mismatch. $($result.Output)"
        }
    }
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $temporaryPrefix = $temporaryParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTemporaryRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith('GraphReader-PdfiumReviewTests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "PDFium dependency review policy tests: $passed passed, 0 failed."
