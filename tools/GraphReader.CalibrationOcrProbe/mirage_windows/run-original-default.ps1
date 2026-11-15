# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
$ErrorActionPreference='Stop'
$env:PYTHONDONTWRITEBYTECODE='1'
$python='E:/Graph-auto-reader/tools/gpu-training/venv/Scripts/python.exe'
$script=Join-Path $PSScriptRoot 'windows_original_default.py'
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'windows-original-default')){throw 'Original reproduction output already exists.'}
$clock=[Diagnostics.Stopwatch]::StartNew()
$owned=Start-Process -FilePath $python -ArgumentList @('-B','-u',$script) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $PSScriptRoot 'windows-original-default.stdout.json') -RedirectStandardError (Join-Path $PSScriptRoot 'windows-original-default.stderr.txt')
if(-not $owned.WaitForExit(175000)){
    $owned.Kill($true)
    [void]$owned.WaitForExit(2000)
    throw 'Original owned Mirage process exceeded the 175-second parent deadline.'
}
$owned.Refresh()
$clock.Stop()
[pscustomobject]@{pid=$owned.Id;exit_code=$owned.ExitCode;wall_seconds=$clock.Elapsed.TotalSeconds;mode='original-default'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'windows-original-default-process.json')
exit $owned.ExitCode
