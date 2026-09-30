# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$GuardPath=(Join-Path $PSScriptRoot 'Run-TrainingCpuBudget.ps1')
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
if (-not $IsWindows) { throw 'The CPU budget integration check requires Windows.' }
if ('Goal22CpuBudget' -as [type]) { throw 'Run this check in a fresh PowerShell process.' }
$GuardPath=(Resolve-Path -LiteralPath $GuardPath).Path
New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop|Out-Null
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
$child=Join-Path $OutputDirectory 'child.ps1'
@'
param([string]$Output,[int]$ExitCode)
$process=[Diagnostics.Process]::GetCurrentProcess()
[ordered]@{
    pid=$PID
    affinity=$process.ProcessorAffinity.ToInt64()
    priority=[string]$process.PriorityClass
    omp=$env:OMP_NUM_THREADS
    mkl=$env:MKL_NUM_THREADS
    openblas=$env:OPENBLAS_NUM_THREADS
    numexpr=$env:NUMEXPR_NUM_THREADS
    ceiling=$env:GOAL22_CPU_CEILING_PERCENT
    duty=$env:GOAL22_TRAINING_DUTY_PERCENT
}|ConvertTo-Json|Set-Content -LiteralPath $Output -Encoding utf8
exit $ExitCode
'@|Set-Content -LiteralPath $child -Encoding utf8
$watch=[Diagnostics.Stopwatch]::StartNew()
$pwsh=(Get-Process -Id $PID).Path
$receipts=[Collections.Generic.List[object]]::new()
$firstJob=[IntPtr]::Zero
for($iteration=0;$iteration -lt 8;$iteration++){
    $result=Join-Path $OutputDirectory "child-$iteration.json"
    $expectedExit=if($iteration -eq 7){7}else{0}
    & $GuardPath -Program $pwsh -ProgramArguments @('-NoProfile','-File',$child,'-Output',$result,'-ExitCode',[string]$expectedExit)
    if($LASTEXITCODE -ne $expectedExit){throw "Child exit code changed on iteration $iteration"}
    $job=[Goal22CpuBudget]::Job
    if($iteration -eq 0){$firstJob=$job}
    if($job -eq [IntPtr]::Zero -or $job -ne $firstJob){throw 'Repeated calls created another CPU job'}
    $rate=[Goal22CpuBudget+CpuRate]::new()
    if(-not [Goal22CpuBudget]::QueryInformationJobObject($job,15,[ref]$rate,8,[IntPtr]::Zero) -or $rate.Flags -ne 5 -or $rate.Rate -ne 8000){throw 'CPU limit changed'}
    $count=[Goal22CpuBudget]::GetActiveProcessorCount([ushort]::MaxValue)
    $affinity=[long]([math]::Pow(2,$count)-1)
    $record=Get-Content -LiteralPath $result -Raw|ConvertFrom-Json
    if($record.affinity -ne $affinity -or $record.priority -ne 'Idle'){throw 'Child scheduling changed'}
    foreach($name in @('omp','mkl','openblas','numexpr')){if([int]$record.$name -ne $count){throw "Child $name lost hardware CPUs"}}
    if($record.ceiling -ne '80' -or $record.duty -ne '80'){throw 'Child budget environment changed'}
    $receipts.Add([ordered]@{iteration=$iteration;job_handle=$job.ToInt64();flags=$rate.Flags;rate=$rate.Rate;affinity=$affinity;exit_code=$LASTEXITCODE;child=$record})
}
# A cached handle must not bypass validation of the actual operating-system cap.
$tampered=[Goal22CpuBudget+CpuRate]::new()
$tampered.Flags=5
$tampered.Rate=7000
if(-not [Goal22CpuBudget]::SetInformationJobObject($firstJob,15,[ref]$tampered,8)){throw 'Unable to prepare cap validation check'}
$blocked=$false
$forbidden=Join-Path $OutputDirectory 'must-not-run.json'
try{
    try{& $GuardPath -Program $pwsh -ProgramArguments @('-NoProfile','-File',$child,'-Output',$forbidden,'-ExitCode','0')}
    catch{if($_.Exception.Message -ne 'CPU ceiling verification failed.'){throw};$blocked=$true}
}finally{
    $tampered.Rate=8000
    if(-not [Goal22CpuBudget]::SetInformationJobObject($firstJob,15,[ref]$tampered,8)){throw 'Unable to restore the CPU cap'}
}
if(-not $blocked -or (Test-Path -LiteralPath $forbidden)){throw 'Changed cap did not fail before child execution'}
[ordered]@{
    status='passed'
    guard=[ordered]@{path=$GuardPath;sha256=(Get-FileHash -LiteralPath $GuardPath).Hash.ToLowerInvariant()}
    source=[ordered]@{path=$PSCommandPath;sha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()}
    repeated_calls=8
    job_handles_created=1
    changed_cap_blocked_before_execution=$blocked
    exit_code_preserved=$true
    seconds=$watch.Elapsed.TotalSeconds
    receipts=$receipts
}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding utf8
Write-Output "PASS: eight calls reuse one verified 80% CPU job; child settings and exit codes are preserved; changed cap blocks execution."
exit 0
