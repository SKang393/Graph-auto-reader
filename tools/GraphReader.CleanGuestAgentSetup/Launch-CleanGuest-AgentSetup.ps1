# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

# Interactive launcher only. The user chooses whether to run guest installers;
# this script does not execute or install software inside the guest.
$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$disk = Join-Path $testRoot 'clean-guest-win11-20261031-72ce4a\system.qcow2'
$qemu = Join-Path $testRoot 'qemu\qemu-system-x86_64.exe'
$iso = Join-Path $testRoot 'downloads\virtio-win-0.1.302-stable-20261031.iso'
foreach ($path in @($disk, $qemu, $iso)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required guest file is missing: $path" }
}
if ((Get-FileHash -LiteralPath $iso -Algorithm SHA256).Hash -ne
    '303F7AE40DAD495D6AE474FDC571DF58958A4DBC5C37A522D80F9A203867949D') {
    throw 'The virtio-win ISO does not match the staged file.'
}
$running = Get-CimInstance Win32_Process -Filter "Name='qemu-system-x86_64.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($disk, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
if ($running) { throw 'Close the existing GraphAutoReaderCleanWindows QEMU window first.' }

Write-Host 'The VM will open with the virtio-win CD and an isolated guest-agent channel.'
Write-Host 'No guest installer is started by this launcher. Networking stays disabled.'
& $qemu '-machine' 'q35,accel=tcg' '-m' '4096' '-smp' '2' '-name' 'GraphAutoReaderCleanWindows' `
    '-uuid' 'b995fe13-65fd-4857-9d1e-aa37b8bb15d8' '-boot' 'order=c,menu=on' '-cdrom' $iso `
    '-drive' "file=$disk,format=qcow2,if=ide" '-nic' 'none' `
    '-device' 'virtio-serial-pci' `
    '-chardev' 'socket,id=qga,host=127.0.0.1,port=44555,server=on,wait=off' `
    '-device' 'virtserialport,chardev=qga,name=org.qemu.guest_agent.0' `
    '-qmp' 'tcp:127.0.0.1:44556,server=on,wait=off'
if ($LASTEXITCODE -ne 0) { throw "QEMU exited with code $LASTEXITCODE." }
