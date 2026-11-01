param(
    [Parameter(Mandatory = $true)]
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.schema -ne 'graphreader.artifact-mask-intersection-center-recall-result.v1') { throw 'Unexpected report schema.' }
if ($report.scope.synthetic_only -ne $true -or $report.scope.training -ne $false -or
    $report.scope.private_reads -ne 0 -or $report.scope.sealed_reads -ne 0) { throw 'Unexpected scope disclosure.' }

$expected = @{
    'grid-spacing-9' = @{ total = 25; eligible = 9; skipped = 16; role = 'diagnostic-development' }
    'grid-spacing-10' = @{ total = 25; eligible = 25; skipped = 0; role = 'diagnostic-development' }
    'grid-spacing-11' = @{ total = 25; eligible = 25; skipped = 0; role = 'diagnostic-development' }
    'grid-spacing-8-val' = @{ total = 16; eligible = 4; skipped = 12; role = 'unseen-synthetic-validation' }
    'grid-spacing-12-val' = @{ total = 16; eligible = 16; skipped = 0; role = 'unseen-synthetic-validation' }
}
if ($report.cases.Count -ne $expected.Count) { throw 'Case count does not match the frozen protocol.' }
foreach ($case in $report.cases) {
    $rule = $expected[$case.case_id]
    if ($null -eq $rule -or $case.family_role -ne $rule.role -or
        $case.grid_intersections -ne $rule.total -or
        $case.baseline_intersection_regions -ne $rule.eligible -or
        $case.centers_masked -ne $rule.eligible -or
        $case.centers_with_marker_probability -ne 0 -or
        $case.centers_with_marker_probability_in_5x5_footprint -ne 0) {
        throw "Unexpected observation in case $($case.case_id)."
    }
    $skipped = @($case.center_diagnostics | Where-Object { -not $_.baseline_center_masked }).Count
    if ($skipped -ne $rule.skipped) { throw "Unexpected center mask count in case $($case.case_id)." }
}
if ($report.aggregate.GridIntersections -ne 107 -or
    $report.aggregate.BaselineIntersectionRegions -ne 79 -or
    $report.aggregate.CentersMasked -ne 79 -or
    $report.aggregate.CentersWithMarkerProbability -ne 0 -or
    $report.aggregate.CentersWithMarkerProbabilityIn5x5Footprint -ne 0) {
    throw 'Unexpected aggregate values.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$source = Get-Content -LiteralPath (Join-Path $root 'src\GraphReader.App\Integration\Workflow\RasterResidualArtifactMaskProvider.cs') -Raw
if ($source -notmatch 'MinimumIntersectionArm\s*=\s*5' -or
    $source -notmatch 'forward\s*>=\s*MinimumIntersectionArm\s*&&\s*backward\s*>=\s*MinimumIntersectionArm' -or
    $source -notmatch 'if\s*\(pairCount\s*>=\s*2\)') {
    throw 'The source no longer contains the ray eligibility rule bound by this diagnostic.'
}
Write-Output 'PASS: observed center counts match the five-pixel bidirectional-arm eligibility rule; marker conflict is absent.'
