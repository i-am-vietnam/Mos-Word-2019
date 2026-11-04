# Build and inspect the installer. Never execute Setup or the uninstaller here.
[CmdletBinding()]
param([string] $ISCCPath, [string] $InnoExtractPath)
. "$PSScriptRoot\windows-common.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$serverUrl = 'https://103.77.209.191/'
$spkiPin = 'Qh9AulK0G+LcgtbfXXDdpTh0sh/LEVuJ8r3XUHvNBL0='
$publicKey = 'aJOoROxX9W+UisP+YNxlWkbq3lFVI2mKznwQT/scj/Y='
$runtime = @('MosWord2019.exe', 'MosWord2019.exe.config', 'MosWord2019.Core.dll', 'MosWord2019.Data.dll', 'MosWord2019.Projects.dll', 'MosWord2019.Word.dll', 'Newtonsoft.Json.dll', 'BouncyCastle.Cryptography.dll')
function Assert-CleanTree {
    $status = @(& git -C $repo status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $status.Count) { throw 'Repository phải sạch trước khi tạo Setup (tệp ignored được phép).' }
}
function Assert-NoReparse([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Từ chối đường dẫn junction/symlink/reparse.' }
        if ($item -is [IO.DirectoryInfo]) { $item = $item.Parent } else { $item = $item.Directory }
    }
}
function New-SafeDirectory([string] $Path) {
    $ancestor = $Path
    while (-not (Test-Path -LiteralPath $ancestor)) { $ancestor = Split-Path -Parent $ancestor }
    Assert-NoReparse $ancestor
    [void](New-Item -ItemType Directory -Path $Path -Force)
    Assert-NoReparse $Path
}
function Invoke-Script([string] $Name, [string[]] $Parameters) {
    Invoke-Checked 'powershell.exe' (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $Name)) + $Parameters)
}
function Find-ISCC {
    $candidates = @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
}
function Get-Extractor {
    # Console extractor only, pinned to a tested Inno 6.6/6.7-compatible upstream build.
    $toolRoot = Join-Path $repo 'artifacts\tools'
    $tool = Join-Path $toolRoot 'innounp-2'
    $executable = Join-Path $tool 'innounp.exe'
    $parent = $repo
    if (Test-Path -LiteralPath $toolRoot) { $parent = $toolRoot }
    Assert-NoReparse $parent
    if (-not (Test-Path -LiteralPath $tool)) {
        [void](New-Item -ItemType Directory -Path $toolRoot -Force)
        $download = Join-Path $toolRoot ('innounp-2-' + [Guid]::NewGuid().ToString('N') + '.zip')
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing 'https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/ui_2_2_13/innounp-2.zip' -OutFile $download
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne 'f8e45c9676bebc7d7570108ab572673b541fcf7a7d3d24a04319ac6b5315b4e8') { throw 'Hash bộ kiểm payload không khớp.' }
            Expand-Archive -LiteralPath $download -DestinationPath $tool
        } catch { throw 'Không chuẩn bị được InnoUnpacker để kiểm payload. Kiểm Internet/quyền ghi artifacts rồi chạy lại; không chạy Setup để thay bước này.' }
    }
    Assert-NoReparse $executable
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne 'ab58400460cfda503ec88702dd3d82f6ee207b28e0548c9f8861ef008f85b644') { throw 'Bộ kiểm payload đã thay đổi; không chạy công cụ sai hash.' }
    return $executable
}
Assert-CleanTree
# A previous production receipt is required: never silently choose a new trust identity.
$productionRoot = Join-Path $repo 'artifacts\client-production'
if (-not (Test-Path -LiteralPath $productionRoot -PathType Container)) { throw 'Thiếu thư mục production và biên nhận trust đã chấp nhận để đối chiếu.' }
Assert-NoReparse $productionRoot
$previous = Get-ChildItem -LiteralPath $productionRoot -Directory | Sort-Object Name -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'public-trust.json') -PathType Leaf } | Select-Object -First 1
if (-not $previous) { throw 'Thiếu public-trust.json production đã chấp nhận để đối chiếu.' }
$receiptPath = Join-Path $previous.FullName 'public-trust.json'
Assert-NoReparse $receiptPath
Assert-NoReparse "$receiptPath.sha256"
$receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
if ($receipt.configuration -cne 'Release' -or $receipt.serverUrl -cne $serverUrl -or $receipt.spkiSha256 -cne $spkiPin -or $receipt.manifestPublicKey -cne $publicKey) { throw 'Trust production mới nhất không khớp các hằng số đã chốt; không build hoặc xoay khóa/pin.' }
$digest = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ([IO.File]::ReadAllText("$receiptPath.sha256").Trim() -cne "$digest  public-trust.json") { throw 'Hash biên nhận production không khớp.' }
[xml] $versionDocument = [IO.File]::ReadAllText((Join-Path $repo 'Installer\Version.props'))
$version = [string] $versionDocument.Project.PropertyGroup.MosWordProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Phiên bản trong Installer/Version.props không hợp lệ.' }
if (-not $ISCCPath) { $ISCCPath = Find-ISCC }
if (-not $ISCCPath) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { throw 'Không tìm thấy Inno Setup 6 hoặc winget. Cài App Installer/winget và Inno Setup 6 cho user hiện tại rồi chạy lại.' }
    & $winget.Source install --id JRSoftware.InnoSetup -e --scope user --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw 'Cài Inno Setup 6 qua winget thất bại; cài Inno Setup 6 cho user hiện tại rồi chạy lại.' }
    $ISCCPath = Find-ISCC
}
if (-not $ISCCPath -or -not (Test-Path -LiteralPath $ISCCPath -PathType Leaf)) { throw 'Không tìm thấy ISCC.exe của Inno Setup 6 sau khi cài.' }
$compilerHelp = @(& $ISCCPath /? 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0 -or $compilerHelp -notmatch 'Inno Setup 6') { throw 'Cần đúng trình biên dịch Inno Setup 6.' }
$useInnoExtract = [bool] $InnoExtractPath
if (-not $InnoExtractPath) { $InnoExtractPath = Get-Extractor }
if (-not (Test-Path -LiteralPath $InnoExtractPath -PathType Leaf)) { throw 'Thiếu innoextract để kiểm payload Setup.' }
$release = Join-Path $repo 'artifacts\release'
New-SafeDirectory $release
& git -C $repo check-ignore -q -- artifacts/release/setup-probe
if ($LASTEXITCODE -ne 0) { throw 'artifacts/release phải được Git ignore.' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$work = Join-Path $repo "artifacts\installer-build\$stamp"
New-SafeDirectory $work
$production = Join-Path $productionRoot $stamp
Invoke-Script 'build-client.ps1' @('-ServerUrl', $serverUrl, '-SpkiPin', $spkiPin, '-ManifestPublicKey', $publicKey, '-OutputDirectory', $production)
Invoke-Script 'verify-client-trust.ps1' @('-OutputDirectory', $production, '-ServerUrl', $serverUrl, '-SpkiPin', $spkiPin, '-ManifestPublicKey', $publicKey)
$suffix = [Guid]::NewGuid().ToString('N').Substring(0,8)
Invoke-Script 'package-client.ps1' @('-OutputDirectory', $production, '-ArchiveSuffix', $suffix)
$archives = @(Get-ChildItem -LiteralPath $release -Filter "MosWord2019-*-$suffix.zip" -File)
if ($archives.Count -ne 1) { throw 'Không xác định được ZIP runtime vừa kiểm.' }
Assert-NoReparse $archives[0].FullName
$stage = Join-Path $work 'runtime'
[void](New-Item -ItemType Directory -Path $stage)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archives[0].FullName)
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    if ($names.Count -ne $runtime.Count -or @(Compare-Object ($names | Sort-Object) ($runtime | Sort-Object) -CaseSensitive).Count) { throw 'ZIP runtime không đúng allow-list tám tệp.' }
    foreach ($entry in $zip.Entries) { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $stage $entry.FullName)) }
} finally { $zip.Dispose() }
foreach ($name in $runtime) {
    if ((Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $production $name) -Algorithm SHA256).Hash) { throw 'Hash ZIP runtime không khớp bản Release vừa build.' }
}
Invoke-Script 'verify-client-trust.ps1' @('-OutputDirectory', $stage, '-ServerUrl', $serverUrl, '-SpkiPin', $spkiPin, '-ManifestPublicKey', $publicKey)
$compiled = Join-Path $work 'compiled'
[void](New-Item -ItemType Directory -Path $compiled)
Invoke-Checked $ISCCPath @("/DAppVersion=$version", "/DRuntimeDirectory=$stage", "/DReleaseDirectory=$compiled", (Join-Path $repo 'Installer\MosWord2019.iss'))
$setupName = "MosWord2019-Setup-$version.exe"
$built = Join-Path $compiled $setupName
if (-not (Test-Path -LiteralPath $built -PathType Leaf) -or (Get-Item -LiteralPath $built).Length -eq 0) { throw 'ISCC không tạo Setup.exe hợp lệ.' }
Assert-NoReparse $built
$extracted = Join-Path $work 'extracted'
# This executes the extractor only, never the newly compiled Setup.exe.
$payloadRoot = 'app'
if ($useInnoExtract) {
    Invoke-Checked $InnoExtractPath @('--extract', '--output-dir', $extracted, $built)
} else {
    Invoke-Checked $InnoExtractPath @('-x', '-b', "-d$extracted", $built)
    $payloadRoot = '{app}'
    # InnoUnpacker generates this inspection report; it is not an installed payload file.
    $inspectionScript = Join-Path $extracted 'install_script.iss'
    Assert-NoReparse $inspectionScript
    if (-not [IO.File]::ReadAllText($inspectionScript).StartsWith('; Created by "innounp" version 2.71.3')) { throw 'Thiếu báo cáo kiểm payload hợp lệ.' }
}
Assert-NoReparse $extracted
$payload = @(Get-ChildItem -LiteralPath $extracted -Recurse -Force -File | Where-Object { $useInnoExtract -or $_.FullName -cne $inspectionScript })
if ($payload.Count -ne $runtime.Count) { throw 'Payload Setup sai số tệp; không phát hành.' }
foreach ($file in $payload) {
    Assert-NoReparse $file.FullName
    $relative = $file.FullName.Substring($extracted.Length + 1).Replace('\', '/')
    if ($runtime -cnotcontains $file.Name -or $relative -cne "$payloadRoot/$($file.Name)" -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $stage $file.Name) -Algorithm SHA256).Hash) { throw 'Payload Setup chứa tệp ngoài allow-list hoặc sai hash.' }
}
$directories = @(Get-ChildItem -LiteralPath $extracted -Recurse -Force -Directory)
if ($directories.Count -ne 1 -or $directories[0].Name -cne $payloadRoot) { throw 'Payload Setup chứa thư mục ngoài allow-list.' }
Assert-CleanTree
$destination = Join-Path $release $setupName
$sums = Join-Path $release 'SHA256SUMS.txt'
foreach ($path in @($destination, $sums)) { if (Test-Path -LiteralPath $path) { Assert-NoReparse $path } }
$sha256 = (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash.ToLowerInvariant()
$lines = @()
if (Test-Path -LiteralPath $sums) { $lines = @([IO.File]::ReadAllLines($sums) | Where-Object { $_ -notmatch ('^[a-fA-F0-9]{64}  ' + [Regex]::Escape($setupName) + '$') }) }
$sumStage = Join-Path $work 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumStage, (($lines + "$sha256  $setupName") -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
# Keep an existing published installer until the replacement passed every inspection.
$setupStage = Join-Path $release ('.setup-' + [Guid]::NewGuid().ToString('N') + '.tmp')
Copy-Item -LiteralPath $built -Destination $setupStage
if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($setupStage, $destination, [NullString]::Value) } else { [IO.File]::Move($setupStage, $destination) }
$sumTarget = Join-Path $release ('.sums-' + [Guid]::NewGuid().ToString('N') + '.tmp')
Copy-Item -LiteralPath $sumStage -Destination $sumTarget
if (Test-Path -LiteralPath $sums) { [IO.File]::Replace($sumTarget, $sums, [NullString]::Value) } else { [IO.File]::Move($sumTarget, $sums) }
Write-Host "Setup.exe: $destination"
Write-Host "SHA-256: $sha256"
Write-Host 'Copy file này sang máy học viên và chạy để cài đặt.'
