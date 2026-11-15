# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
param([ValidateSet('default','text-only')][string]$Mode='default')
$ErrorActionPreference='Stop'
$env:PYTHONDONTWRITEBYTECODE='1'
$python='E:/Graph-auto-reader/tools/gpu-training/venv/Scripts/python.exe'
$script=Join-Path $PSScriptRoot 'windows_probe.py'
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot "windows-$Mode")){throw 'Owned output already exists; no implicit rerun.'}
$clock=[Diagnostics.Stopwatch]::StartNew()
$owned=Start-Process -FilePath $python -ArgumentList @($script,'--mode',$Mode) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $PSScriptRoot "windows-$Mode.stdout.json") -RedirectStandardError (Join-Path $PSScriptRoot "windows-$Mode.stderr.txt")
if(-not $owned.WaitForExit(175000)){
    $owned.Kill($true)
    [void]$owned.WaitForExit(2000)
    throw 'Owned Mirage process exceeded the 175-second parent deadline.'
}
$owned.Refresh()
$clock.Stop()
[pscustomobject]@{pid=$owned.Id;exit_code=$owned.ExitCode;wall_seconds=$clock.Elapsed.TotalSeconds;mode=$Mode}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot "windows-$Mode-process.json")
exit $owned.ExitCode
