# Builds on the Windows PC; the VPS only installs the published runtime files.
[CmdletBinding()]
param(
    [string] $VpsHost,
    [string] $SshUser = 'mosword',
    [string] $IdentityFile,
    [switch] $PrepareOnly
)
. "$PSScriptRoot\windows-common.ps1"
if ($SshUser -notmatch '^[a-z_][a-z0-9_-]{0,31}$') { throw 'SshUser phải là tên tài khoản Unix chữ thường.' }
if (-not $PrepareOnly -or $VpsHost) { [void] (Assert-IPv4 $VpsHost) }
$sshOptions = @('-o', 'StrictHostKeyChecking=yes', '-o', 'BatchMode=yes')
if ($IdentityFile) {
    $identity = Get-Item -LiteralPath $IdentityFile
    if ($identity.PSIsContainer) { throw 'IdentityFile phải là tệp khóa SSH hiện có.' }
    $sshOptions += @('-i', $identity.FullName)
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$artifact = Join-Path $repo "artifacts\deploy\$stamp"
$publish = Join-Path $artifact 'publish'
[void] (New-Item -ItemType Directory -Path $publish)
$project = Join-Path $repo 'server\MosWord2019.Server\MosWord2019.Server.csproj'
# Seed a per-project ignored lock from the committed dependencies. RID restore
# can then add its target without rewriting the tracked package lock files.
foreach ($name in @('MosWord2019.Server', 'MosWord2019.ExamComposition')) {
    $directory = Join-Path $repo "server\$name"
    [void] (New-Item -ItemType Directory -Path (Join-Path $directory 'obj') -Force)
    Copy-Item -LiteralPath (Join-Path $directory 'packages.lock.json') -Destination (Join-Path $directory 'obj\packages.linux-x64.lock.json') -Force
}
Invoke-Checked 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'false', '-p:NuGetLockFilePath=obj/packages.linux-x64.lock.json', '-o', $publish)
# Include a byte-normalized copy of operational scripts, never local secrets.
$deployment = Join-Path $publish 'deploy'
[void] (New-Item -ItemType Directory -Path $deployment)
Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in @('.sh', '.service', '.timer') -or $_.Name -in @('50unattended-upgrades', 'fail2ban.local', 'journald.conf', 'nginx.conf.template') } | ForEach-Object {
    Write-Utf8Lf (Join-Path $deployment $_.Name) ([IO.File]::ReadAllText($_.FullName))
}
$archive = Join-Path $artifact "$stamp.tar.gz"
Invoke-Checked 'tar' @('-czf', $archive, '-C', $publish, '.')
$sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Utf8Lf "$archive.sha256" "$sha256  $stamp.tar.gz`n"
Write-Host "Đã chuẩn bị: $archive"
Write-Host "SHA256: $sha256"
if ($PrepareOnly) { Write-Host 'PrepareOnly: chưa kết nối VPS.'; return }
$remote = "$SshUser@$VpsHost"
$upload = "/home/mosword/mosword-upload/$stamp.tar.gz"
Invoke-Checked 'ssh' ($sshOptions + @($remote, 'mkdir -p /home/mosword/mosword-upload'))
Invoke-Checked 'scp' ($sshOptions + @($archive, "${remote}:$upload"))
Write-Host 'Cài đặt trên VPS; nhập mật khẩu sudo trực tiếp trong SSH nếu được yêu cầu.'
Invoke-Checked 'ssh' ($sshOptions + @('-t', $remote, "sudo /usr/local/lib/mosword-deploy/03-install-release.sh $upload $sha256"))
