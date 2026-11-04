# Package an existing production build; never rebuild or copy exercise packages.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [ValidatePattern('^[a-f0-9]{8}$')][string] $ArchiveSuffix
)
. "$PSScriptRoot\windows-common.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
function Assert-CleanTree {
    $status = @(& git -C $repo status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Repository phải sạch (kể cả tệp untracked không ignored) trước khi đóng gói.' }
}
function Assert-NoReparse([string] $Path) {
    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Từ chối đường dẫn reparse/junction/symlink.' }
        if ($current -is [IO.DirectoryInfo]) { $current = $current.Parent } else { $current = $current.Directory }
    }
}
Assert-CleanTree
$commit = (& git -C $repo rev-parse --short=8 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{8,40}$') { throw 'Không xác định được commit hiện tại.' }
$source = Get-Item -LiteralPath $OutputDirectory -Force
if (-not $source.PSIsContainer) { throw 'OutputDirectory phải là thư mục production đã xác minh.' }
Assert-NoReparse $source.FullName
$runtime = @('MosWord2019.exe', 'MosWord2019.exe.config', 'MosWord2019.Core.dll', 'MosWord2019.Data.dll', 'MosWord2019.Projects.dll', 'MosWord2019.Word.dll', 'Newtonsoft.Json.dll', 'BouncyCastle.Cryptography.dll')
$extras = @('MosWord2019.pdb', 'MosWord2019.Core.pdb', 'MosWord2019.Data.pdb', 'MosWord2019.Projects.pdb', 'MosWord2019.Word.pdb', 'Newtonsoft.Json.xml', 'BouncyCastle.Cryptography.xml', 'public-trust.json', 'public-trust.json.sha256')
$inputFiles = @(Get-ChildItem -LiteralPath $source.FullName -Force)
foreach ($file in $inputFiles) {
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($runtime -cnotcontains $file.Name -and $extras -cnotcontains $file.Name)) { throw 'Production output chứa mục ngoài allowlist; không đóng gói.' }
}
foreach ($name in ($runtime + @('public-trust.json', 'public-trust.json.sha256'))) {
    if (-not (Test-Path -LiteralPath (Join-Path $source.FullName $name) -PathType Leaf)) { throw "Thiếu tệp bắt buộc: $name" }
}
$receiptPath = Join-Path $source.FullName 'public-trust.json'
$digest = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
$receiptHash = [IO.File]::ReadAllText("$receiptPath.sha256").Trim()
if ($receiptHash -cne "$digest  public-trust.json") { throw 'SHA256 biên nhận trust không khớp.' }
$receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
if ($receipt.configuration -cne 'Release') { throw 'Chỉ đóng gói production Release.' }
$origin = Get-ProductionOrigin $receipt.serverUrl
$pin = Assert-PublicBytes $receipt.spkiSha256 'SpkiPin' -RejectZero
$key = Assert-PublicBytes $receipt.manifestPublicKey 'ManifestPublicKey' -RejectZero
$recorded = @{}
foreach ($entry in $receipt.files) {
    if ($runtime -cnotcontains $entry.file -and $extras -cnotcontains $entry.file) { throw 'Biên nhận chứa tên tệp ngoài allowlist.' }
    if ($recorded.ContainsKey($entry.file) -or $entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Biên nhận chứa hash/tên trùng không hợp lệ.' }
    $recorded[$entry.file] = $entry.sha256
}
foreach ($name in $runtime) {
    if (-not $recorded.ContainsKey($name) -or (Get-FileHash -LiteralPath (Join-Path $source.FullName $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $recorded[$name]) { throw "Hash production không khớp: $name" }
}
$releaseRoot = Join-Path $repo 'artifacts\release'
# Check existing ancestors before creating anything through a junction.
$ancestor = $releaseRoot
while (-not (Test-Path -LiteralPath $ancestor)) { $ancestor = Split-Path -Parent $ancestor }
Assert-NoReparse $ancestor
[void] (New-Item -ItemType Directory -Path $releaseRoot -Force)
Assert-NoReparse $releaseRoot
& git -C $repo check-ignore -q -- 'artifacts/release/package-probe'
if ($LASTEXITCODE -ne 0) { throw 'artifacts/release phải được Git ignore.' }
$suffix = if ($ArchiveSuffix) { "-$ArchiveSuffix" } else { '' }
$archive = Join-Path $releaseRoot ("MosWord2019-" + [DateTime]::UtcNow.ToString('yyyyMMdd') + "-$commit$suffix.zip")
if (Test-Path -LiteralPath $archive) { throw 'ZIP cùng ngày/commit đã tồn tại; không ghi đè.' }
$stage = Join-Path $releaseRoot ('.stage-' + [Guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $stage)
$createdArchive = $false
try {
    foreach ($name in $runtime) { Copy-Item -LiteralPath (Join-Path $source.FullName $name) -Destination (Join-Path $stage $name) }
    $staged = @(Get-ChildItem -LiteralPath $stage -Force)
    if ($staged.Count -ne $runtime.Count) { throw 'Stage sai số tệp runtime.' }
    foreach ($file in $staged) {
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $runtime -cnotcontains $file.Name -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $recorded[$file.Name]) { throw 'Stage chứa tệp không hợp lệ/hash sai.' }
    }
    Invoke-Checked 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'verify-client-trust.ps1'), '-OutputDirectory', $stage, '-ServerUrl', $origin, '-SpkiPin', $pin, '-ManifestPublicKey', $key)
    Assert-CleanTree
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive)
    $createdArchive = $true
    $sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums = Join-Path $releaseRoot 'SHA256SUMS.txt'
    if (Test-Path -LiteralPath $sums) { Assert-NoReparse $sums }
    [IO.File]::AppendAllText($sums, "$sha256  $([IO.Path]::GetFileName($archive))`n", (New-Object Text.UTF8Encoding $false))
    Write-Host "Đã đóng gói client: $archive"
    Write-Host "SHA256: $sha256"
} catch {
    if ($createdArchive -and (Test-Path -LiteralPath $archive)) { Remove-Item -LiteralPath $archive -Force }
    throw
} finally {
    Assert-NoReparse $stage
    if ([IO.Path]::GetDirectoryName($stage) -cne $releaseRoot -or [IO.Path]::GetFileName($stage) -notmatch '^\.stage-[a-f0-9]{32}$') { throw 'Từ chối dọn stage ngoài release root.' }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
