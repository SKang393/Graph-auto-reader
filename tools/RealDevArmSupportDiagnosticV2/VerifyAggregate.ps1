param(
    [Parameter(Mandatory = $true)] [string] $OriginalReport,
    [Parameter(Mandatory = $true)] [string] $CorrectedReport
)
$ErrorActionPreference = 'Stop'
$original = Get-Content -LiteralPath $OriginalReport -Raw | ConvertFrom-Json
$corrected = Get-Content -LiteralPath $CorrectedReport -Raw | ConvertFrom-Json
$snapshotPath = Join-Path $PSScriptRoot 'Program-executed.snapshot'
$snapshotHash = (Get-FileHash -LiteralPath $snapshotPath).Hash.ToLowerInvariant()
$originalHash = (Get-FileHash -LiteralPath $OriginalReport).Hash.ToLowerInvariant()
$protocolHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'protocol.json')).Hash.ToLowerInvariant()
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Program.cs') -Raw
if ($original.status -ne 'complete' -or $corrected.status -ne 'complete' -or
    $corrected.scope.selected_projects -ne 16 -or $corrected.aggregate.successful_images -ne 16 -or
    $corrected.scope.train_payloads_opened -or $corrected.scope.sealed_payloads_opened -or
    $corrected.scope.training -or $corrected.scope.gpu_inference -or
    $corrected.scope.case_level_output -or $corrected.identity.protocol_sha256 -ne $protocolHash -or
    $corrected.identity.program_sha256 -ne $snapshotHash -or
    $corrected.provenance_correction.original_report_sha256 -ne $originalHash -or
    $corrected.provenance_correction.executed_source_snapshot_sha256 -ne $snapshotHash -or
    $corrected.provenance_correction.fields_corrected.Count -ne 1 -or
    $corrected.provenance_correction.fields_corrected[0] -ne 'identity.program_sha256' -or
    $source -notmatch 'Program\.cs"\)\),') {
    throw 'Aggregate disclosure or corrected source binding is invalid.'
}
if ($corrected.aggregate.arm4_novel_candidates_beyond_arm5_nms_radius -ne
    ($corrected.aggregate.arm4_novel_candidates_near_engauge_point + $corrected.aggregate.arm4_novel_candidates_not_near_engauge_point) -or
    $corrected.aggregate.engauge_points_covered_only_by_arm4_masks -gt $corrected.aggregate.engauge_point_centers -or
    $corrected.aggregate.engauge_points_covered_only_by_arm5_masks -gt $corrected.aggregate.engauge_point_centers -or
    $corrected.identity.loaded_opencv_module_count -ne 1 -or
    $corrected.identity.opencv_native_sha256 -ne $corrected.identity.loaded_opencv_native_sha256) {
    throw 'Aggregate arithmetic or runtime receipt is invalid.'
}
if (($original.aggregate | ConvertTo-Json -Depth 10) -ne ($corrected.aggregate | ConvertTo-Json -Depth 10) -or
    ($original.scope | ConvertTo-Json -Depth 10) -ne ($corrected.scope | ConvertTo-Json -Depth 10) -or
    ($original.method | ConvertTo-Json -Depth 10) -ne ($corrected.method | ConvertTo-Json -Depth 10)) {
    throw 'Metrics, scope, or method changed in the corrected report.'
}
foreach ($property in $original.identity.PSObject.Properties) {
    if ($property.Name -ne 'program_sha256' -and $property.Value -ne $corrected.identity.($property.Name)) {
        throw "Unexpected identity change: $($property.Name)."
    }
}
Write-Output 'PASS: 16-case scope, aggregate arithmetic, OpenCV receipt, executed-source snapshot, and single-field metadata correction verified.'
