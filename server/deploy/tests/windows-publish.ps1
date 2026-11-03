# Exercise command construction without opening a socket or invoking a build.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:WindowsPublishTestCommands = @()
function dotnet { $global:WindowsPublishTestCommands += ,(@('dotnet') + $args); $global:LASTEXITCODE = 0 }
function tar { $global:WindowsPublishTestCommands += ,(@('tar') + $args); [IO.File]::WriteAllText($args[1], 'fixture'); $global:LASTEXITCODE = 0 }
function ssh { $global:WindowsPublishTestCommands += ,(@('ssh') + $args); $global:LASTEXITCODE = 0 }
function scp { $global:WindowsPublishTestCommands += ,(@('scp') + $args); $global:LASTEXITCODE = 0 }
$publish = Join-Path $PSScriptRoot '..\publish.ps1'
& $publish -PrepareOnly
if (@($global:WindowsPublishTestCommands | Where-Object { $_[0] -in @('ssh', 'scp') }).Count -ne 0) { throw 'PrepareOnly đã dùng SSH/SCP.' }
$dotnet = @($global:WindowsPublishTestCommands | Where-Object { $_[0] -eq 'dotnet' })[0]
if (($dotnet -join ' ') -notmatch 'publish .* -c Release -r linux-x64 --self-contained false -p:NuGetLockFilePath=obj/packages.linux-x64.lock.json -o ') { throw 'Sai publish arguments.' }
$global:WindowsPublishTestCommands = @()
& $publish -VpsHost '192.0.2.25' -SshUser 'mosword'
$network = @($global:WindowsPublishTestCommands | Where-Object { $_[0] -in @('ssh', 'scp') })
if ($network.Count -ne 3) { throw 'Sai chuỗi SSH/SCP.' }
foreach ($command in $network) {
    if (($command -join ' ') -notmatch 'StrictHostKeyChecking=yes -o BatchMode=yes') { throw 'Thiếu host/authentication policy.' }
}
if (($network[1] -join ' ') -notmatch 'mosword@192\.0\.2\.25:/home/mosword/mosword-upload/[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}\.tar\.gz$') { throw 'Sai upload path.' }
if (($network[2] -join ' ') -notmatch ' -t mosword@192\.0\.2\.25 sudo /usr/local/lib/mosword-deploy/03-install-release\.sh /home/mosword/mosword-upload/[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}\.tar\.gz [a-f0-9]{64}$') { throw 'Sai sudo installer command.' }
foreach ($parameters in @(@{VpsHost='192.0.2.25;id'}, @{VpsHost='192.0.2.25'; SshUser='root;id'}, @{VpsHost='192.0.2.25'; IdentityFile=(Join-Path $PSScriptRoot 'missing-identity')})) {
    $before = $global:WindowsPublishTestCommands.Count
    $rejected = $false
    try { & $publish @parameters } catch { $rejected = $true }
    if (-not $rejected -or $global:WindowsPublishTestCommands.Count -ne $before) { throw 'Không chặn tham số không an toàn trước khi chạy lệnh.' }
}
Write-Host 'PASS: PrepareOnly không kết nối mạng; publish arguments và 3 trường hợp từ chối.'
