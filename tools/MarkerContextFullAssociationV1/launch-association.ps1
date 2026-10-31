# SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$FakeSidecarSelfTest,
    [switch]$RealTruthAssociation,
    [string]$LaunchProtocolSha256,
    [string]$CaptureReportSha256
)
$ErrorActionPreference = 'Stop'
$Root = 'E:\Graph-auto-reader'
$LaunchProtocolPath = Join-Path $Root 'tools\MarkerContextFullAssociationV1\launcher-protocol.json'
$LaunchBindingPath = Join-Path $Root 'artifacts\goal22-runs\marker-context-train-association-v15-launch-binding.json'
$OutputPath = Join-Path $Root 'artifacts\goal22-runs\marker-context-train-association-v15-real-reviewed'
$held = [System.Collections.Generic.List[System.IO.FileStream]]::new()

function Get-Sha256Bytes([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Assert-Hash([string]$Name, [string]$Expected, [string]$Actual) {
    if ($Expected -notmatch '^[0-9a-f]{64}$' -or $Actual -cne $Expected) {
        throw "$Name SHA-256 differs"
    }
}

function Assert-BundleMap([hashtable]$Expected, [hashtable]$BytesByName) {
    foreach ($name in $Expected.Keys) {
        if (-not $BytesByName.ContainsKey($name)) { throw "Missing bundle item: $name" }
        Assert-Hash $name $Expected[$name] (Get-Sha256Bytes $BytesByName[$name])
    }
}

function Assert-ProjectPath([string]$Path, [bool]$MustExist = $true) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Project path escaped E:\Graph-auto-reader'
    }
    $relative = $full.Substring($rootPrefix.Length)
    $cursor = $Root
    foreach ($part in $relative.Split([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)) {
        if (-not $part) { continue }
        $cursor = Join-Path $cursor $part
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse-point path denied: $cursor"
            }
        } elseif ($MustExist) {
            throw "Required project path missing: $cursor"
        }
    }
    return $full
}

function Open-ReadLocked([string]$Path, [bool]$ProjectBound = $true) {
    if ($ProjectBound) { $Path = Assert-ProjectPath $Path }
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    $held.Add($stream)
    return $stream
}

function Read-StreamBytes([System.IO.FileStream]$Stream) {
    $Stream.Position = 0
    $memory = [System.IO.MemoryStream]::new()
    try {
        $Stream.CopyTo($memory)
        return $memory.ToArray()
    } finally { $memory.Dispose(); $Stream.Position = 0 }
}

function Get-PythonVersion([string]$PythonPath) {
    $versionLines = & $PythonPath -I -B -c 'import sys,json;print(json.dumps(sys.version))'
    if ($LASTEXITCODE -ne 0 -or @($versionLines).Count -ne 1) { throw 'Unable to bind Python runtime version' }
    $json = @($versionLines)[0]
    $version = ConvertFrom-Json -InputObject $json
    if (-not ($version -is [string]) -or $version.Length -lt 3) { throw 'Python runtime version response is invalid' }
    return $version
}

function Invoke-SelfTest {
    $good = [System.Text.Encoding]::UTF8.GetBytes('runner-v1')
    $other = [System.Text.Encoding]::UTF8.GetBytes('association-v1')
    $expected = @{ runner = Get-Sha256Bytes $good; association = Get-Sha256Bytes $other }
    $valid = @{ runner = $good; association = $other }
    Assert-BundleMap $expected $valid

    $truthReaderCalled = $false
    $tampered = @{ runner = [System.Text.Encoding]::UTF8.GetBytes('runner-v2'); association = $other }
    try {
        Assert-BundleMap $expected $tampered
        $truthReaderCalled = $true
        throw 'Tampered runner was not rejected before truth access'
    } catch {
        if ($_.Exception.Message -eq 'Tampered runner was not rejected before truth access') { throw }
    }
    if ($truthReaderCalled) { throw 'Truth callback ran on bundle mismatch' }
    $pythonCommand = Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $pythonVersion = Get-PythonVersion $pythonCommand.Source
    if (-not $pythonVersion.Contains('.')) { throw 'Python version self-test did not return a version string' }
    Write-Output 'ASSOCIATION_LAUNCHER_SELF_TEST=PASS; tampered_runner_rejected_before_truth=true; python_version_json_roundtrip=true; truth_access=NOT_STARTED'
}

try {
    if ($SelfTest) { Invoke-SelfTest; exit 0 }
    if (($FakeSidecarSelfTest -and $RealTruthAssociation) -or (-not $FakeSidecarSelfTest -and -not $RealTruthAssociation)) {
        throw 'Select exactly one of fake-sidecar self-test or real-truth association'
    }
    if ($LaunchProtocolSha256 -notmatch '^[0-9a-f]{64}$' -or $CaptureReportSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Supply reviewed launcher-protocol and completed capture-report SHA-256 values'
    }
    $null = Assert-ProjectPath $LaunchProtocolPath
    $protocolStream = Open-ReadLocked $LaunchProtocolPath
    $protocolBytes = Read-StreamBytes $protocolStream
    Assert-Hash 'launcher protocol' $LaunchProtocolSha256 (Get-Sha256Bytes $protocolBytes)
    $protocol = [System.Text.Encoding]::UTF8.GetString($protocolBytes) | ConvertFrom-Json
    if ($protocol.schema -ne 'graphreader.marker-context-full-train-association-launch-protocol.v4' -or
        $protocol.mode -ne 'real_association_review_pending' -or
        $protocol.scope -ne 'public-synthetic-v5-full-train-only-postcapture-diagnostic' -or
        $protocol.training_approved -ne $false -or $protocol.output_path -ne 'artifacts/goal22-runs/marker-context-train-association-v15-real-reviewed' -or
        $protocol.launch_binding_path -ne 'artifacts/goal22-runs/marker-context-train-association-v15-launch-binding.json') {
        throw 'Fixed launcher protocol scope differs'
    }

    $relativeFiles = @{
        launcher = $protocol.launcher_path
        runner = $protocol.runner_path
        association = $protocol.association_path
        association_protocol = $protocol.association_protocol_path
        real_association_protocol = $protocol.real_association_protocol_path
        capture_validator = $protocol.capture_validator_path
        truth_adapter = $protocol.truth_adapter_path
        capture_binding = $protocol.capture_binding_path
        capture_protocol = $protocol.capture_protocol_path
    }
    $expectedHashes = @{
        launcher = $protocol.launcher_sha256
        runner = $protocol.runner_sha256
        association = $protocol.association_sha256
        association_protocol = $protocol.association_protocol_sha256
        real_association_protocol = $protocol.real_association_protocol_sha256
        capture_validator = $protocol.capture_validator_sha256
        truth_adapter = $protocol.truth_adapter_sha256
        capture_binding = $protocol.capture_binding_sha256
        capture_protocol = $protocol.capture_protocol_sha256
    }
    $bundleBytes = @{}
    foreach ($name in $relativeFiles.Keys) {
        $path = Join-Path $Root $relativeFiles[$name]
        if ($name -eq 'launcher') { $path = $PSCommandPath }
        $stream = Open-ReadLocked $path
        $bytes = Read-StreamBytes $stream
        $bundleBytes[$name] = $bytes
        Assert-Hash $name $expectedHashes[$name] (Get-Sha256Bytes $bytes)
    }
    # This same complete-bundle gate is exercised by -SelfTest with an altered
    # runner and a truth callback that must remain untouched on mismatch.
    Assert-BundleMap $expectedHashes $bundleBytes
    $baseProtocol = [System.Text.Encoding]::UTF8.GetString($bundleBytes.association_protocol) | ConvertFrom-Json
    $realProtocol = [System.Text.Encoding]::UTF8.GetString($bundleBytes.real_association_protocol) | ConvertFrom-Json
    if ($RealTruthAssociation -and
        ($realProtocol.real_truth_access_enabled -ne $true -or $realProtocol.status -ne 'reviewed_and_authorized')) {
        throw 'Real truth association is blocked until the real-association protocol is independently reviewed and authorized'
    }
    if ($baseProtocol.match_tolerance_px -ne 2.0 -or $baseProtocol.training_approved -ne $false -or
        $baseProtocol.optimizer_steps -ne 0 -or $protocol.association_protocol_sha256 -ne '4e351d67be11ec23cb4f91ff7f5cb23e15de125048701dbea885f17e0ae44d7b' -or
        $protocol.association_sha256 -ne '90eb3f1017b30c47f86361975408931ab86f76bd093864d89ea91ce37aebe991') {
        throw 'Association protocol or source binding differs'
    }

    $reportPath = Join-Path $Root $protocol.capture_report_path
    $reportStream = Open-ReadLocked $reportPath
    $reportBytes = Read-StreamBytes $reportStream
    Assert-Hash 'capture report' $CaptureReportSha256 (Get-Sha256Bytes $reportBytes)
    $report = [System.Text.Encoding]::UTF8.GetString($reportBytes) | ConvertFrom-Json
    if ($report.schema -ne 'graphreader.marker-context-bound-input-full-capture-report.v1' -or
        $report.source_accounting_complete -ne $true -or $report.source_count -ne 64 -or
        $report.truth_content_read -ne $false -or $report.private_input_supplied -ne $false -or
        $report.sealed_input_supplied -ne $false -or $report.dev_input_supplied -ne $false -or
        $report.training_approved -ne $false -or $report.optimizer_steps -ne 0) {
        throw 'Completed 64-source capture report is absent or outside scope'
    }
    if (Test-Path -LiteralPath $OutputPath) { throw 'Association output must be fresh' }
    $requestedOperation = if ($FakeSidecarSelfTest) { 'fake_sidecar_self_test' } else { 'real_truth_association' }
    $binding = [ordered]@{
        schema = 'graphreader.marker-context-full-train-association-launch-binding.v4'
        launcher_protocol_sha256 = $LaunchProtocolSha256
        capture_report_sha256 = $CaptureReportSha256
        runner_sha256 = $protocol.runner_sha256
        base_capture_validator_sha256 = $protocol.capture_validator_sha256
        truth_adapter_sha256 = $protocol.truth_adapter_sha256
        real_association_protocol_sha256 = $protocol.real_association_protocol_sha256
        association_sha256 = $protocol.association_sha256
        association_protocol_sha256 = $protocol.association_protocol_sha256
        launcher_sha256 = $protocol.launcher_sha256
        mode = 'real_association_review_pending'
        requested_operation = $requestedOperation
        python_executable_path = $null
        python_executable_sha256 = $null
        python_version = $null
        output_path = $protocol.output_path
        truth_content_read = $false
        training_approved = $false
    }
    $pythonCommand = Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1
    # The interpreter may be an already-installed external program. Its path and
    # bytes are recorded and locked, but project-specific outputs stay on E:.
    $pythonStream = Open-ReadLocked $pythonCommand.Source -ProjectBound $false
    $pythonBytes = Read-StreamBytes $pythonStream
    $binding.python_executable_path = [System.IO.Path]::GetFullPath($pythonCommand.Source)
    $binding.python_executable_sha256 = Get-Sha256Bytes $pythonBytes
    $binding.python_version = Get-PythonVersion $pythonCommand.Source
    $bindingRaw = [System.Text.Encoding]::UTF8.GetBytes(($binding | ConvertTo-Json -Compress -Depth 5))
    $null = Assert-ProjectPath $OutputPath -MustExist $false
    $null = Assert-ProjectPath $LaunchBindingPath -MustExist $false
    $bindingStream = [System.IO.File]::Open($LaunchBindingPath, [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    try { $bindingStream.Write($bindingRaw, 0, $bindingRaw.Length); $bindingStream.Flush($true) }
    finally { $bindingStream.Dispose() }
    $bindingSha = Get-Sha256Bytes $bindingRaw
    $null = Open-ReadLocked $LaunchBindingPath

    $runnerPath = Join-Path $Root $protocol.runner_path
    $operationFlag = if ($FakeSidecarSelfTest) { '--fake-sidecar-self-test' } else { '--real-truth-association' }
    & $pythonCommand.Source -I -B $runnerPath $CaptureReportSha256 $LaunchProtocolSha256 $bindingSha $operationFlag
    $exitCode = $LASTEXITCODE
    Write-Output "ASSOCIATION_LAUNCHER_EXIT=$exitCode; launch_binding_sha256=$bindingSha; capture_report_sha256=$CaptureReportSha256"
    exit $exitCode
}
finally {
    for ($i = $held.Count - 1; $i -ge 0; $i--) { $held[$i].Dispose() }
}
