# Exercise command construction without opening a socket or invoking a build.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:WindowsPublishTestCommands = @()
function dotnet { $global:WindowsPublishTestCommands += ,(@('dotnet') + $args); $global:LASTEXITCODE = 0 }
function tar { $global:WindowsPublishTestCommands += ,(@('tar') + $args); [IO.File]::WriteAllText($args[1], 'fixture'); $global:LASTEXITCODE = 0 }
$global:WindowsPublishReceiptMode = 'match'
$global:WindowsPublishPromptSeen = $false
$global:WindowsPublishStamp = ''
function ssh {
    $global:WindowsPublishTestCommands += ,(@('ssh') + $args)
    $global:LASTEXITCODE = 0
    if (($args -join ' ') -match '03-install-release.sh /home/mosword/mosword-upload/([^ ]+)\.tar\.gz') {
        $global:WindowsPublishStamp = $Matches[1]
        'SUDO_PROMPT_FIXTURE'
        if ($global:WindowsPublishReceiptMode -eq 'nonzero') { $global:LASTEXITCODE = 7 }
        return
    }
    if (($args -join ' ') -match 'systemctl is-active --quiet mosword.service') {
        if (-not $global:WindowsPublishPromptSeen) { throw 'Interactive prompt was captured or delayed.' }
        $stamp = $global:WindowsPublishStamp
        switch ($global:WindowsPublishReceiptMode) {
            'query-nonzero' { $global:LASTEXITCODE = 8; return }
            'missing' { return }
            'mismatch' { $stamp = '20200101T000000Z-00000000' }
        }
        "MOSWORD_ACTIVE_RELEASE=/opt/mosword/releases/$stamp"
        'MOSWORD_SERVICE_STARTED_AT=Wed 2026-11-04 12:00:00 UTC'
    }
}
function scp { $global:WindowsPublishTestCommands += ,(@('scp') + $args); $global:LASTEXITCODE = 0 }
$publish = Join-Path $PSScriptRoot '..\publish.ps1'
& $publish -PrepareOnly
if (@($global:WindowsPublishTestCommands | Where-Object { $_[0] -in @('ssh', 'scp') }).Count -ne 0) { throw 'PrepareOnly đã dùng SSH/SCP.' }
$dotnet = @($global:WindowsPublishTestCommands | Where-Object { $_[0] -eq 'dotnet' })[0]
if (($dotnet -join ' ') -notmatch 'publish .* -c Release -r linux-x64 --self-contained false -p:NuGetLockFilePath=obj/packages.linux-x64.lock.json -o ') { throw 'Sai publish arguments.' }
$global:WindowsPublishTestCommands = @()
& $publish -VpsHost '192.0.2.25' -SshUser 'mosword' | ForEach-Object { if ($_ -eq 'SUDO_PROMPT_FIXTURE') { $global:WindowsPublishPromptSeen = $true } }
$network = @($global:WindowsPublishTestCommands | Where-Object { $_[0] -in @('ssh', 'scp') })
if ($network.Count -ne 4) { throw 'Sai chuỗi SSH/SCP.' }
foreach ($command in $network) {
    if (($command -join ' ') -notmatch 'StrictHostKeyChecking=yes -o BatchMode=yes') { throw 'Thiếu host/authentication policy.' }
}
if (($network[1] -join ' ') -notmatch 'mosword@192\.0\.2\.25:/home/mosword/mosword-upload/[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}\.tar\.gz$') { throw 'Sai upload path.' }
if (($network[2] -join ' ') -notmatch ' -t mosword@192\.0\.2\.25 sudo /usr/local/lib/mosword-deploy/03-install-release\.sh /home/mosword/mosword-upload/[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}\.tar\.gz [a-f0-9]{64}$') { throw 'Sai sudo installer command.' }
if (($network[3] -join ' ') -notmatch ' -T mosword@192\.0\.2\.25 set -eu; systemctl is-active --quiet mosword.service;' -or ($network[3] -join ' ') -match 'sudo') { throw 'Confirmation phải noninteractive và không dùng sudo.' }
foreach ($parameters in @(@{VpsHost='192.0.2.25;id'}, @{VpsHost='192.0.2.25'; SshUser='root;id'}, @{VpsHost='192.0.2.25'; IdentityFile=(Join-Path $PSScriptRoot 'missing-identity')})) {
    $before = $global:WindowsPublishTestCommands.Count
    $rejected = $false
    try { & $publish @parameters } catch { $rejected = $true }
    if (-not $rejected -or $global:WindowsPublishTestCommands.Count -ne $before) { throw 'Không chặn tham số không an toàn trước khi chạy lệnh.' }
}
foreach ($mode in @('nonzero', 'query-nonzero', 'mismatch', 'missing')) {
    $global:WindowsPublishReceiptMode = $mode
    $rejected = $false
    try { & $publish -VpsHost '192.0.2.25' } catch {
        if ($_.Exception.Message -notmatch 'Triển khai VPS') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Không từ chối remote $mode." }
}
Write-Host 'PASS: PrepareOnly không kết nối mạng; live interactive prompt, separate unprivileged confirmation, unsafe inputs, install/query nonzero, mismatch/missing receipt.'
