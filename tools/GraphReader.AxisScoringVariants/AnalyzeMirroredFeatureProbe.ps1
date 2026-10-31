# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
param(
    [Parameter(Mandatory = $true)][string]$ExpectedReportSha256,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = 'E:\Graph-auto-reader'
if ((Get-Location).Path -ine $projectRoot) { throw 'Run from the authorized E project root.' }
$reportPath = Join-Path $projectRoot 'artifacts\goal22-runs\axis-scoring-synthetic-variants-v5\report.json'
$outputFull = [System.IO.Path]::GetFullPath($OutputPath)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\goal22-runs')) + [System.IO.Path]::DirectorySeparatorChar
if (!$outputFull.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or !$outputFull.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be a new JSON file under project artifacts/goal22-runs.'
}
if ($ExpectedReportSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Expected input report SHA-256 is invalid.' }
$reportHash = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($reportHash -ine $ExpectedReportSha256) { throw 'Input report hash does not match the caller pin.' }
$scriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($report.schema -ne 'graphreader.axis-scoring-synthetic-variants.v2' -or
    $report.scope -ne 'generated_line_candidates_only' -or $report.corpus_reads -ne 0 -or
    $report.model_inference -ne 0 -or $report.training_steps -ne 0 -or $report.results.Count -ne 33) {
    throw 'Input scope or fixture count differs from the generated diagnostic.'
}
$rules = @(
    @{name='top2_smaller_intersection_y'; property='intersection_y'; direction='min'},
    @{name='top2_larger_intersection_y'; property='intersection_y'; direction='max'},
    @{name='top2_shorter_x_span'; property='x_span'; direction='min'},
    @{name='top2_longer_x_span'; property='x_span'; direction='max'},
    @{name='top2_smaller_y_span'; property='y_span'; direction='min'},
    @{name='top2_larger_y_span'; property='y_span'; direction='max'},
    @{name='top2_lower_x_rms'; property='x_rms_pixels'; direction='min'},
    @{name='top2_higher_x_coverage'; property='x_coverage'; direction='max'},
    @{name='top2_more_x_supports'; property='x_support_count'; direction='max'}
)
$evaluations = foreach ($rule in $rules) {
    $rows = foreach ($sample in $report.results) {
        if ($sample.status -ne 'completed' -or $sample.candidates.Count -lt 2) { throw 'All fixture comparisons require two ranked candidates.' }
        $first = $sample.candidates[0]
        $second = $sample.candidates[1]
        if ($first.rank -ne 0 -or $second.rank -ne 1) { throw 'Candidate ordering mismatch.' }
        # Selection uses only the observed feature, before looking at reference errors.
        $chosen = $first
        if (($rule.direction -eq 'min' -and $second.($rule.property) -lt $first.($rule.property)) -or
            ($rule.direction -eq 'max' -and $second.($rule.property) -gt $first.($rule.property))) { $chosen = $second }
        [ordered]@{
            fixture = $sample.name
            chosen_rank = $chosen.rank
            reference_max_corner_error_pixels = $chosen.maximum_corner_error_pixels
            within_one_pixel = $chosen.maximum_corner_error_pixels -le 1
        }
    }
    [ordered]@{
        name = $rule.name
        fixture_success_count = @($rows | Where-Object { $_.within_one_pixel }).Count
        competing_success_count = @($rows | Where-Object { $_.fixture.StartsWith('competing-') -and $_.within_one_pixel }).Count
        mirror_success_count = @($rows | Where-Object { $_.fixture.StartsWith('mirror-') -and $_.within_one_pixel }).Count
        other_success_count = @($rows | Where-Object { !$_.fixture.StartsWith('competing-') -and !$_.fixture.StartsWith('mirror-') -and $_.within_one_pixel }).Count
        rows = @($rows)
    }
}
if ((Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $reportHash -or
    (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $scriptHash) {
    throw 'Input report or analysis source changed during evaluation.'
}
$output = [ordered]@{
    schema = 'graphreader.axis-generated-feature-probe.v1'
    scope = 'generated_candidates_posthoc_exploration_only'
    input_report_sha256 = $reportHash
    analysis_script_sha256 = $scriptHash
    feature_evaluations = @($evaluations)
    limitations = @(
        'Only 33 generated line-candidate fixtures were examined; horizontal offsets are related variants, not independent examples.',
        'The six competing cases put the incorrect line below the true axis; the six mirrored cases put it above, by construction.',
        'No raster decoding, OpenCV extraction, marker classification, corpus data, inference or training was performed.',
        'Reference corner errors are used only for post-selection measurement and must not be used as detector inputs.',
        'The best in-sample feature is exploratory and does not justify changing or approving the production scorer.'
    )
}
$directory = [System.IO.Path]::GetDirectoryName($outputFull)
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($output | ConvertTo-Json -Depth 20))
$stream = [System.IO.File]::Open($outputFull, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
Write-Output ('evaluated_rules=' + $evaluations.Count + ' fixture_count=' + $report.results.Count)
