# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Configure', 'Build', 'Collect', 'All')]
    [string]$Phase = 'Preflight',
    [string]$SourceRoot,
    [string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Join-Path $projectRoot 'artifacts\pdfium-source\sources' }
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = Join-Path $projectRoot 'artifacts\pdfium-source\evidence' }
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$lockPath = Join-Path $PSScriptRoot 'source-lock.json'
$argsPath = Join-Path $PSScriptRoot 'args.gn'
$nativeRoot = Join-Path $PSScriptRoot 'native'
$compatibilityPatch = Join-Path $nativeRoot 'pdfium-windows-hdc.patch'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$depotTools = Join-Path $SourceRoot 'depot_tools'
$pdfiumRoot = Join-Path $SourceRoot 'pdfium'
$outRoot = Join-Path $EvidenceRoot 'out'
$binRoot = Join-Path $EvidenceRoot 'bin'
$env:DEPOT_TOOLS_UPDATE = '0'
$env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
$pythonMarker = Join-Path $depotTools 'python3_bin_reldir.txt'
if (-not (Test-Path -LiteralPath $pythonMarker -PathType Leaf)) {
    throw 'Pinned depot_tools Python is missing. Run Initialize-PdfiumSource.ps1 first.'
}
$relativePython = (Get-Content -LiteralPath $pythonMarker -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($relativePython) -or [IO.Path]::IsPathRooted($relativePython)) {
    throw 'The depot_tools Python location must be a relative path.'
}
$manifestName = if (Test-Path -LiteralPath (Join-Path $depotTools '.bleeding_edge')) { 'manifest_bleeding_edge.txt' } else { 'manifest.txt' }
$bootstrapManifest = Join-Path $depotTools "bootstrap\$manifestName"
$pythonLines = @(Get-Content -LiteralPath $bootstrapManifest | Where-Object { $_ -match '^infra/3pp/tools/cpython3/\S+\s+version:(\S+)\s*$' })
if ($pythonLines.Count -ne 1 -or $pythonLines[0] -notmatch '^infra/3pp/tools/cpython3/\S+\s+version:(\S+)\s*$') {
    throw 'The pinned depot_tools bootstrap manifest must identify exactly one Python package.'
}
$expectedRelativePython = "bootstrap-$($Matches[1].Replace('.', '_'))_bin\python3\bin"
if (-not [string]::Equals($relativePython, $expectedRelativePython, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The depot_tools Python marker does not match the pinned bootstrap manifest. Run Initialize-PdfiumSource.ps1 again.'
}
$pythonBin = [IO.Path]::GetFullPath((Join-Path $depotTools $relativePython))
$depotPrefix = [IO.Path]::GetFullPath($depotTools).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $pythonBin.StartsWith($depotPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath (Join-Path $pythonBin 'python3.exe') -PathType Leaf)) {
    throw 'The depot_tools Python location must name an installed Python inside the pinned checkout.'
}
$cursor = Join-Path $pythonBin 'python3.exe'
while ($cursor) {
    $item = Get-Item -LiteralPath $cursor -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The pinned Python path contains a reparse point: $cursor"
    }
    $parent = Split-Path -Path $cursor -Parent
    if (-not $parent -or $parent -eq $cursor) { break }
    $cursor = $parent
}
$env:PATH = "$pythonBin;$depotTools;$env:PATH"

function Write-Json($Value, [string]$Path) {
    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
}

function Set-LocalWindowsSdkOverlay {
    $installedRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $debuggerSource = Join-Path $projectRoot 'artifacts\pdfium-source\windows-sdk-debuggers-x64\Windows Kits\10\Debuggers'
    $overlayRoot = Join-Path $projectRoot 'artifacts\pdfium-source\windows-sdk-overlay'
    if (-not (Test-Path -LiteralPath (Join-Path $debuggerSource 'x64\dbghelp.dll') -PathType Leaf)) {
        throw 'Pinned local Windows SDK Debugging Tools are missing. Run Initialize-WindowsSdkDebuggingTools.ps1.'
    }
    New-Item -ItemType Directory -Path $overlayRoot -Force | Out-Null
    function Ensure-Junction([string]$Path, [string]$Target) {
        if (Test-Path -LiteralPath $Path) {
            $item = Get-Item -LiteralPath $Path -Force
            $resolvedTarget = [IO.Path]::GetFullPath([string]$item.Target)
            if ($item.LinkType -ne 'Junction' -or $resolvedTarget -ne [IO.Path]::GetFullPath($Target)) {
                throw "Windows SDK overlay entry is not the expected junction: $Path"
            }
            return
        }
        New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null
    }
    foreach ($directory in Get-ChildItem -LiteralPath $installedRoot -Directory) {
        if ($directory.Name -eq 'Debuggers') { continue }
        $link = Join-Path $overlayRoot $directory.Name
        Ensure-Junction -Path $link -Target $directory.FullName
    }
    $debuggerLink = Join-Path $overlayRoot 'Debuggers'
    Ensure-Junction -Path $debuggerLink -Target $debuggerSource
    $env:WINDOWSSDKDIR = $overlayRoot
}

function Add-GraphReaderRootTarget {
    $rootBuild = Join-Path $pdfiumRoot 'BUILD.gn'
    $rootTarget = Join-Path $nativeRoot 'root-target.gn'
    $currentBlob = (& git -C $pdfiumRoot hash-object BUILD.gn | Out-String).Trim()
    if ($currentBlob -eq [string]$lock.sources.pdfium.rootBuildGnBlob) {
        $addition = [Environment]::NewLine + (Get-Content -LiteralPath $rootTarget -Raw)
        [IO.File]::AppendAllText($rootBuild, $addition, (New-Object Text.UTF8Encoding($false)))
        return
    }
    $content = Get-Content -LiteralPath $rootBuild -Raw
    if (-not $content.EndsWith((Get-Content -LiteralPath $rootTarget -Raw), [StringComparison]::Ordinal)) {
        throw 'PDFium root BUILD.gn contains an unexpected change and will not be overwritten.'
    }
}

function Get-PdfiumRootBuildBlob {
    $blob = (& git -C $pdfiumRoot hash-object BUILD.gn | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($blob)) {
        throw 'Unable to hash the pinned PDFium root BUILD.gn.'
    }
    return $blob
}

function Assert-PdfiumSourceCheckoutPristine {
    $rootBlob = Get-PdfiumRootBuildBlob
    if ($rootBlob -ne [string]$lock.sources.pdfium.rootBuildGnBlob) {
        throw "PDFium root BUILD.gn blob is $rootBlob, expected $($lock.sources.pdfium.rootBuildGnBlob)."
    }
    $renderHeaderBlob = (& git -C $pdfiumRoot hash-object core/fxge/cfx_renderdevice.h | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $renderHeaderBlob -ne [string]$lock.sources.pdfium.renderDeviceHeaderBlob) {
        throw "PDFium cfx_renderdevice.h blob is $renderHeaderBlob, expected $($lock.sources.pdfium.renderDeviceHeaderBlob)."
    }
    $status = @(& git -C $pdfiumRoot status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the pinned PDFium source checkout.' }
    if ($status.Count -ne 0) {
        throw "Pinned PDFium source checkout is not pristine: $($status -join '; ')"
    }
}

function New-GraphReaderSourceOverlay {
    Assert-PdfiumSourceCheckoutPristine
    $overlay = Join-Path $pdfiumRoot 'graphreader_pdfium_renderer'
    if (Test-Path -LiteralPath $overlay) {
        throw "Temporary PDFium source overlay already exists: $overlay"
    }
    New-Item -ItemType Directory -Path $overlay | Out-Null
    Copy-Item -LiteralPath (Join-Path $nativeRoot 'BUILD.gn') -Destination (Join-Path $overlay 'BUILD.gn')
    Copy-Item -LiteralPath (Join-Path $nativeRoot 'graphreader_pdfium_renderer.cc') -Destination (Join-Path $overlay 'graphreader_pdfium_renderer.cc')
    Add-GraphReaderRootTarget
    & git -C $pdfiumRoot apply --check --whitespace=error-all $compatibilityPatch
    if ($LASTEXITCODE -ne 0) { throw 'Pinned PDFium Windows compatibility patch no longer applies cleanly.' }
    & git -C $pdfiumRoot apply --whitespace=error-all $compatibilityPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply the pinned PDFium Windows compatibility patch.' }
    return $overlay
}

function Restore-PdfiumSourceOverlay {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$BeforeGitBlob,
        [Parameter(Mandatory = $true)][string]$BeforeSha256,
        [Parameter(Mandatory = $true)][string]$OverlayGitBlob,
        [Parameter(Mandatory = $true)][string]$OverlaySha256,
        [Parameter(Mandatory = $true)][string]$BeforeHeaderGitBlob,
        [Parameter(Mandatory = $true)][string]$BeforeHeaderSha256,
        [Parameter(Mandatory = $true)][string]$OverlayHeaderGitBlob,
        [Parameter(Mandatory = $true)][string]$OverlayHeaderSha256,
        [Parameter(Mandatory = $true)][string]$Outcome,
        [string]$OperationError
    )

    New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    $overlay = Join-Path $pdfiumRoot 'graphreader_pdfium_renderer'
    $retainedOverlayPath = $null
    $cleanupError = $null
    try {
        $headerPath = Join-Path $pdfiumRoot 'core\fxge\cfx_renderdevice.h'
        $headerChanged = @(& git -C $pdfiumRoot diff --name-only -- core/fxge/cfx_renderdevice.h).Count -gt 0
        if ((Test-Path -LiteralPath $overlay) -or $headerChanged) {
            $retainedRoot = Join-Path $EvidenceRoot 'retained-overlays'
            New-Item -ItemType Directory -Path $retainedRoot -Force | Out-Null
            $retainedOverlayPath = Join-Path $retainedRoot ("{0}-{1}-{2}" -f $Operation.ToLowerInvariant(), [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $retainedOverlayPath | Out-Null
            if (Test-Path -LiteralPath $overlay) {
                Move-Item -LiteralPath $overlay -Destination (Join-Path $retainedOverlayPath 'graphreader_pdfium_renderer')
            }
            if ($headerChanged) {
                $retainedHeader = Join-Path $retainedOverlayPath 'patched-core\fxge\cfx_renderdevice.h'
                New-Item -ItemType Directory -Path (Split-Path -Parent $retainedHeader) -Force | Out-Null
                Copy-Item -LiteralPath $headerPath -Destination $retainedHeader
            }
        }
        & git -C $pdfiumRoot restore --source=HEAD -- BUILD.gn core/fxge/cfx_renderdevice.h
        if ($LASTEXITCODE -ne 0) { throw 'Unable to restore the pinned PDFium build inputs.' }
        Assert-PdfiumSourceCheckoutPristine
    }
    catch {
        $cleanupError = $_.Exception.Message
    }

    $afterGitBlob = $null
    $afterSha256 = $null
    $afterHeaderGitBlob = $null
    $afterHeaderSha256 = $null
    if (Test-Path -LiteralPath (Join-Path $pdfiumRoot 'BUILD.gn') -PathType Leaf) {
        $afterGitBlob = Get-PdfiumRootBuildBlob
        $afterSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'BUILD.gn') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if (Test-Path -LiteralPath (Join-Path $pdfiumRoot 'core\fxge\cfx_renderdevice.h') -PathType Leaf) {
        $afterHeaderGitBlob = (& git -C $pdfiumRoot hash-object core/fxge/cfx_renderdevice.h | Out-String).Trim()
        $afterHeaderSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'core\fxge\cfx_renderdevice.h') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Write-Json ([ordered]@{
        schemaVersion = 1
        operation = $Operation
        outcome = $Outcome
        operationError = $OperationError
        cleanupError = $cleanupError
        beforeGitBlob = $BeforeGitBlob
        beforeSha256 = $BeforeSha256
        overlayGitBlob = $OverlayGitBlob
        overlaySha256 = $OverlaySha256
        afterGitBlob = $afterGitBlob
        afterSha256 = $afterSha256
        beforeHeaderGitBlob = $BeforeHeaderGitBlob
        beforeHeaderSha256 = $BeforeHeaderSha256
        overlayHeaderGitBlob = $OverlayHeaderGitBlob
        overlayHeaderSha256 = $OverlayHeaderSha256
        afterHeaderGitBlob = $afterHeaderGitBlob
        afterHeaderSha256 = $afterHeaderSha256
        compatibilityPatchSha256 = (Get-FileHash -LiteralPath $compatibilityPatch -Algorithm SHA256).Hash.ToLowerInvariant()
        retainedOverlayPath = $retainedOverlayPath
        sourceCheckoutPristine = [string]::IsNullOrWhiteSpace($cleanupError)
        checkedUtc = [DateTime]::UtcNow.ToString('o')
    }) (Join-Path $EvidenceRoot ("root-overlay-{0}.json" -f $Operation.ToLowerInvariant()))

    if (-not [string]::IsNullOrWhiteSpace($cleanupError)) {
        throw "PDFium source overlay cleanup failed: $cleanupError"
    }
}

function Assert-Preflight {
    foreach ($path in @($lockPath, $argsPath, (Join-Path $nativeRoot 'BUILD.gn'), (Join-Path $nativeRoot 'root-target.gn'), (Join-Path $nativeRoot 'graphreader_pdfium_renderer.cc'), $compatibilityPatch, $pdfiumRoot, $depotTools)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Pinned PDFium input is missing: $path" }
    }
    $patchHash = (Get-FileHash -LiteralPath $compatibilityPatch -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($patchHash -ne [string]$lock.compatibilityPatchSha256) {
        throw "PDFium compatibility patch SHA-256 is $patchHash, expected $($lock.compatibilityPatchSha256)."
    }
    $pdfiumRevision = (& git -C $pdfiumRoot rev-parse HEAD | Out-String).Trim()
    $depotRevision = (& git -C $depotTools rev-parse HEAD | Out-String).Trim()
    if ((& git -C $depotTools status --porcelain --untracked-files=all | Out-String).Trim()) {
        throw "Pinned depot_tools checkout contains changes: $depotTools"
    }
    if (-not [string]::Equals($pdfiumRevision, [string]$lock.sources.pdfium.revision, [StringComparison]::OrdinalIgnoreCase)) {
        throw "PDFium revision is $pdfiumRevision, expected $($lock.sources.pdfium.revision)."
    }
    if (-not [string]::Equals($depotRevision, [string]$lock.sources.depotTools.revision, [StringComparison]::OrdinalIgnoreCase)) {
        throw "depot_tools revision is $depotRevision, expected $($lock.sources.depotTools.revision)."
    }
    foreach ($tool in @('gn.bat', 'autoninja.bat')) {
        if (-not (Test-Path -LiteralPath (Join-Path $depotTools $tool))) { throw "Pinned build tool is missing: $tool" }
    }
    foreach ($tool in @(
        (Join-Path $pdfiumRoot 'buildtools\win\gn.exe'),
        (Join-Path $pdfiumRoot 'third_party\ninja\ninja.exe')
    )) {
        if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Pinned PDFium build executable is missing: $tool" }
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw "vswhere.exe is missing: $vswhere" }
    $vsVersion = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion | Out-String).Trim()
    $vsPath = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Out-String).Trim()
    if ($vsVersion -ne [string]$lock.toolchain.visualStudioInstallationVersion) {
        throw "Visual Studio version is $vsVersion, expected $($lock.toolchain.visualStudioInstallationVersion)."
    }
    if (-not (Test-Path -LiteralPath $vsPath)) { throw "Pinned Visual Studio path is missing: $vsPath" }
    $env:vs2022_install = $vsPath
    $env:GYP_MSVS_OVERRIDE_PATH = $vsPath
    Set-LocalWindowsSdkOverlay
    $sdk = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Lib\$($lock.toolchain.windowsSdkVersion)"
    if (-not (Test-Path -LiteralPath $sdk)) { throw "Pinned Windows SDK is missing: $sdk" }
    Assert-PdfiumSourceCheckoutPristine
}

function Invoke-Configure {
    Assert-Preflight
    New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
    Copy-Item -LiteralPath $argsPath -Destination (Join-Path $outRoot 'args.gn') -Force
    $beforeGitBlob = Get-PdfiumRootBuildBlob
    $beforeSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'BUILD.gn') -Algorithm SHA256).Hash.ToLowerInvariant()
    $beforeHeaderGitBlob = (& git -C $pdfiumRoot hash-object core/fxge/cfx_renderdevice.h | Out-String).Trim()
    $beforeHeaderSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'core\fxge\cfx_renderdevice.h') -Algorithm SHA256).Hash.ToLowerInvariant()
    $overlayGitBlob = 'unavailable'
    $overlaySha256 = 'unavailable'
    $overlayHeaderGitBlob = 'unavailable'
    $overlayHeaderSha256 = 'unavailable'
    $outcome = 'failed'
    $operationError = $null
    try {
        New-GraphReaderSourceOverlay | Out-Null
        $overlayGitBlob = Get-PdfiumRootBuildBlob
        $overlaySha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'BUILD.gn') -Algorithm SHA256).Hash.ToLowerInvariant()
        $overlayHeaderGitBlob = (& git -C $pdfiumRoot hash-object core/fxge/cfx_renderdevice.h | Out-String).Trim()
        $overlayHeaderSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'core\fxge\cfx_renderdevice.h') -Algorithm SHA256).Hash.ToLowerInvariant()
        & (Join-Path $pdfiumRoot 'buildtools\win\gn.exe') gen $outRoot "--root=$pdfiumRoot" --fail-on-unused-args
        if ($LASTEXITCODE -ne 0) { throw "Pinned PDFium GN configuration failed with exit code $LASTEXITCODE." }
        $outcome = 'succeeded'
    }
    catch {
        $operationError = $_.Exception.Message
        throw
    }
    finally {
        Restore-PdfiumSourceOverlay -Operation 'Configure' -BeforeGitBlob $beforeGitBlob -BeforeSha256 $beforeSha256 -OverlayGitBlob $overlayGitBlob -OverlaySha256 $overlaySha256 -BeforeHeaderGitBlob $beforeHeaderGitBlob -BeforeHeaderSha256 $beforeHeaderSha256 -OverlayHeaderGitBlob $overlayHeaderGitBlob -OverlayHeaderSha256 $overlayHeaderSha256 -Outcome $outcome -OperationError $operationError
    }
}

function Invoke-Build {
    Assert-Preflight
    New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
    Copy-Item -LiteralPath $argsPath -Destination (Join-Path $outRoot 'args.gn') -Force
    $dependencyGraph = Join-Path $EvidenceRoot 'target-dependencies.txt'
    if (Test-Path -LiteralPath $dependencyGraph -PathType Leaf) { Remove-Item -LiteralPath $dependencyGraph -Force }
    $beforeGitBlob = Get-PdfiumRootBuildBlob
    $beforeSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'BUILD.gn') -Algorithm SHA256).Hash.ToLowerInvariant()
    $beforeHeaderGitBlob = (& git -C $pdfiumRoot hash-object core/fxge/cfx_renderdevice.h | Out-String).Trim()
    $beforeHeaderSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'core\fxge\cfx_renderdevice.h') -Algorithm SHA256).Hash.ToLowerInvariant()
    $overlayGitBlob = 'unavailable'
    $overlaySha256 = 'unavailable'
    $overlayHeaderGitBlob = 'unavailable'
    $overlayHeaderSha256 = 'unavailable'
    $outcome = 'failed'
    $operationError = $null
    try {
        New-GraphReaderSourceOverlay | Out-Null
        $overlayGitBlob = Get-PdfiumRootBuildBlob
        $overlaySha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'BUILD.gn') -Algorithm SHA256).Hash.ToLowerInvariant()
        $overlayHeaderGitBlob = (& git -C $pdfiumRoot hash-object core/fxge/cfx_renderdevice.h | Out-String).Trim()
        $overlayHeaderSha256 = (Get-FileHash -LiteralPath (Join-Path $pdfiumRoot 'core\fxge\cfx_renderdevice.h') -Algorithm SHA256).Hash.ToLowerInvariant()
        $gn = Join-Path $pdfiumRoot 'buildtools\win\gn.exe'
        & $gn gen $outRoot "--root=$pdfiumRoot" --fail-on-unused-args
        if ($LASTEXITCODE -ne 0) { throw "Pinned PDFium GN configuration failed with exit code $LASTEXITCODE." }
        & (Join-Path $pdfiumRoot 'third_party\ninja\ninja.exe') -C $outRoot -j ([int]$lock.target.maxParallelCompileJobs) graphreader_pdfium_renderer_build
        if ($LASTEXITCODE -ne 0) { throw "Pinned PDFium renderer build failed with exit code $LASTEXITCODE." }
        & $gn desc $outRoot '//graphreader_pdfium_renderer:graphreader_pdfium_renderer' deps --all "--root=$pdfiumRoot" | Out-File -LiteralPath $dependencyGraph -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw 'Unable to collect the PDFium target dependency graph.' }
        $outcome = 'succeeded'
    }
    catch {
        $operationError = $_.Exception.Message
        throw
    }
    finally {
        Restore-PdfiumSourceOverlay -Operation 'Build' -BeforeGitBlob $beforeGitBlob -BeforeSha256 $beforeSha256 -OverlayGitBlob $overlayGitBlob -OverlaySha256 $overlaySha256 -BeforeHeaderGitBlob $beforeHeaderGitBlob -BeforeHeaderSha256 $beforeHeaderSha256 -OverlayHeaderGitBlob $overlayHeaderGitBlob -OverlayHeaderSha256 $overlayHeaderSha256 -Outcome $outcome -OperationError $operationError
    }
}

function Invoke-Collect {
    Assert-Preflight
    $binary = Join-Path $outRoot ([string]$lock.target.binaryName)
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "PDFium renderer binary is missing: $binary" }
    New-Item -ItemType Directory -Path $binRoot -Force | Out-Null
    $collectedBinary = Join-Path $binRoot ([string]$lock.target.binaryName)
    Copy-Item -LiteralPath $binary -Destination $collectedBinary -Force
    $sourceLockHash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $binaryHash = (Get-FileHash -LiteralPath $collectedBinary -Algorithm SHA256).Hash.ToLowerInvariant()
    $argsHash = (Get-FileHash -LiteralPath $argsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $overlayBuildHash = (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'BUILD.gn') -Algorithm SHA256).Hash.ToLowerInvariant()
    $overlayRootTargetHash = (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'root-target.gn') -Algorithm SHA256).Hash.ToLowerInvariant()
    $overlaySourceHash = (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'graphreader_pdfium_renderer.cc') -Algorithm SHA256).Hash.ToLowerInvariant()
    $compatibilityPatchHash = (Get-FileHash -LiteralPath $compatibilityPatch -Algorithm SHA256).Hash.ToLowerInvariant()
    $dependencyGraph = Join-Path $EvidenceRoot 'target-dependencies.txt'
    if (-not (Test-Path -LiteralPath $dependencyGraph -PathType Leaf)) {
        throw "PDFium target dependency evidence is missing: $dependencyGraph"
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vsPath = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Out-String).Trim()
    $vcTools = Get-ChildItem -LiteralPath (Join-Path $vsPath 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($null -eq $vcTools) { throw 'No MSVC tools directory is available for PE import inspection.' }
    $dumpbin = Join-Path $vcTools.FullName 'bin\Hostx64\x64\dumpbin.exe'
    if (-not (Test-Path -LiteralPath $dumpbin -PathType Leaf)) { throw "dumpbin.exe is missing: $dumpbin" }
    $importReport = Join-Path $EvidenceRoot 'pe-imports.txt'
    $importLines = @(& $dumpbin /nologo /imports $collectedBinary)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to collect PDFium runner PE imports.' }
    $normalizedImportLines = @($importLines | ForEach-Object {
        if ($_ -match '^Dump of file ') { 'Dump of file graphreader_pdfium_renderer.exe' } else { $_ }
    })
    [IO.File]::WriteAllLines($importReport, $normalizedImportLines, (New-Object Text.UTF8Encoding($false)))

    Write-Json ([ordered]@{
        schemaVersion = 1
        profileId = [string]$lock.profileId
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        reviewStatus = 'requires-review'
        source = [string]$lock.sources.pdfium.repository
        sourceRevision = [string]$lock.sources.pdfium.revision
        sourceLockSha256 = $sourceLockHash
        argsGnSha256 = $argsHash
        overlayBuildSha256 = $overlayBuildHash
        overlayRootTargetSha256 = $overlayRootTargetHash
        overlaySourceSha256 = $overlaySourceHash
        compatibilityPatchSha256 = $compatibilityPatchHash
        targetDependenciesSha256 = (Get-FileHash -LiteralPath $dependencyGraph -Algorithm SHA256).Hash.ToLowerInvariant()
        peImportsSha256 = (Get-FileHash -LiteralPath $importReport -Algorithm SHA256).Hash.ToLowerInvariant()
        binarySha256 = $binaryHash
        features = [ordered]@{ v8 = $false; xfa = $false; skia = $false; icuDataFile = $false }
        warning = 'This mechanical manifest is not redistribution approval.'
    }) (Join-Path $EvidenceRoot 'build-manifest.json')

    $candidateNotice = Join-Path $EvidenceRoot 'third-party-notices.candidate.txt'
    $licenseFiles = @(Get-ChildItem -LiteralPath $pdfiumRoot -File -Recurse -ErrorAction Stop |
        Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)(\..*)?$' } |
        Sort-Object FullName)
    $writer = New-Object Text.StringBuilder
    [void]$writer.AppendLine('REVIEW STATUS: INCOMPLETE')
    [void]$writer.AppendLine('This source-collected candidate is not approved for redistribution.')
    foreach ($licenseFile in $licenseFiles) {
        [void]$writer.AppendLine()
        [void]$writer.AppendLine("===== $($licenseFile.FullName.Substring($pdfiumRoot.Length + 1)) =====")
        [void]$writer.AppendLine((Get-Content -LiteralPath $licenseFile.FullName -Raw))
    }
    [IO.File]::WriteAllText($candidateNotice, $writer.ToString(), (New-Object Text.UTF8Encoding($false)))

    Write-Json ([ordered]@{
        schemaVersion = 1
        rendererId = 'graphreader-pdfium-renderer'
        rendererVersion = [string]$lock.sources.pdfium.revision
        binaryPath = 'bin/graphreader_pdfium_renderer.exe'
        binarySha256 = $binaryHash
        source = [string]$lock.sources.pdfium.repository
        sourceRevision = [string]$lock.sources.pdfium.revision
        sourceLockPath = 'source-lock.json'
        sourceLockSha256 = $sourceLockHash
        buildManifestPath = 'build-manifest.json'
        buildManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $EvidenceRoot 'build-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        licenseSpdx = 'BSD-3-Clause'
        noticePath = 'third-party-notices.reviewed.txt'
        noticeSha256 = $null
        reviewApproved = $false
        redistributionApproved = $false
        bundlingApproved = $false
        warning = 'Candidate only. Independent review must supply the reviewed notice hash and set all approvals true.'
    }) (Join-Path $EvidenceRoot 'reviewed-approval.candidate.json')
    Copy-Item -LiteralPath $lockPath -Destination (Join-Path $EvidenceRoot 'source-lock.json') -Force
    Write-Host 'PDFium evidence collected. Approval remains blocked pending complete notice and independent review.'
}

switch ($Phase) {
    'Preflight' { Assert-Preflight }
    'Configure' { Invoke-Configure }
    'Build' { Invoke-Build }
    'Collect' { Invoke-Collect }
    'All' { Invoke-Configure; Invoke-Build; Invoke-Collect }
}
