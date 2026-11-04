# Disposable repositories and tool doubles; never execute an actual installer.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$sandbox = Join-Path $repo ('artifacts\installer-tests-' + [Guid]::NewGuid().ToString('N'))
$deploy = Join-Path $sandbox 'server\deploy'
$tools = Join-Path $sandbox 'tools'
$source = Join-Path $sandbox 'artifacts\source'
foreach ($directory in @($deploy, $tools, $source, (Join-Path $sandbox 'Installer'))) { [void](New-Item -ItemType Directory -Path $directory -Force) }
foreach ($name in @('build-installer.ps1', 'verify-client-trust.ps1', 'windows-common.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\$name") -Destination $deploy }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\package-client.ps1') -Destination (Join-Path $deploy 'package-real.ps1')
function Write-Fixture([string] $Path, [string] $Value) { [IO.File]::WriteAllText($Path, $Value, (New-Object Text.UTF8Encoding $true)) }
Write-Fixture (Join-Path $sandbox '.gitignore') "artifacts/`n"
Write-Fixture (Join-Path $sandbox 'Installer\Version.props') '<Project><PropertyGroup><MosWordProductVersion>1.0.0</MosWordProductVersion></PropertyGroup></Project>'
Write-Fixture (Join-Path $sandbox 'Installer\MosWord2019.iss') '; synthetic compiler input'
$key = 'aJOoROxX9W+UisP+YNxlWkbq3lFVI2mKznwQT/scj/Y='
$pin = 'Qh9AulK0G+LcgtbfXXDdpTh0sh/LEVuJ8r3XUHvNBL0='
Add-Type -TypeDefinition "namespace MosWord2019.Projects.OnlinePackages { public static class PackageTrust { public const string PublicKey = `"$key`"; } }" -OutputAssembly (Join-Path $source 'MosWord2019.Projects.dll')
Add-Type -TypeDefinition "namespace MosWord2019 { internal static class CompiledServerSettings { internal const string Origin = `"https://103.77.209.191/`"; internal const string SpkiPin = `"$pin`"; } public class Entry { public static void Main() {} } }" -OutputAssembly (Join-Path $source 'MosWord2019.exe') -OutputType ConsoleApplication
foreach ($name in @('MosWord2019.exe.config', 'MosWord2019.Core.dll', 'MosWord2019.Data.dll', 'MosWord2019.Word.dll', 'Newtonsoft.Json.dll', 'BouncyCastle.Cryptography.dll')) { Write-Fixture (Join-Path $source $name) "fixture $name" }
$baseline = Join-Path $sandbox 'artifacts\client-production\zzzz-accepted'
[void](New-Item -ItemType Directory -Path $baseline -Force)
$baselineReceipt = [ordered]@{ configuration='Release';serverUrl='https://103.77.209.191/';spkiSha256=$pin;manifestPublicKey=$key }
function Save-Baseline {
    $path = Join-Path $baseline 'public-trust.json'
    Write-Fixture $path ($baselineReceipt | ConvertTo-Json)
    Write-Fixture "$path.sha256" ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + "  public-trust.json`n")
}
Save-Baseline
Write-Fixture (Join-Path $deploy 'build-client.ps1') @'
param([string]$ServerUrl,[string]$SpkiPin,[string]$ManifestPublicKey,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
[IO.File]::AppendAllText((Join-Path $repo 'artifacts\events.txt'), "build`n")
if ($ServerUrl -cne 'https://103.77.209.191/' -or $SpkiPin -cne 'Qh9AulK0G+LcgtbfXXDdpTh0sh/LEVuJ8r3XUHvNBL0=' -or $ManifestPublicKey -cne 'aJOoROxX9W+UisP+YNxlWkbq3lFVI2mKznwQT/scj/Y=') { throw 'Sai production trust.' }
[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
Copy-Item -Path (Join-Path $repo 'artifacts\source\*') -Destination $OutputDirectory
$receipt = [ordered]@{configuration='Release';serverUrl=$ServerUrl;spkiSha256=$SpkiPin;manifestPublicKey=$ManifestPublicKey;files=@(Get-ChildItem -LiteralPath $OutputDirectory -File | ForEach-Object { @{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })}
$path = Join-Path $OutputDirectory 'public-trust.json'
[IO.File]::WriteAllText($path, ($receipt | ConvertTo-Json -Depth 5))
[IO.File]::WriteAllText("$path.sha256", ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + "  public-trust.json`n"))
if ((Get-Content -LiteralPath (Join-Path $repo 'artifacts\mode.txt') -Raw).Trim() -eq 'trust-mismatch') { [IO.File]::WriteAllText((Join-Path $OutputDirectory 'MosWord2019.exe'), 'not a trusted assembly') }
'@
Write-Fixture (Join-Path $deploy 'package-client.ps1') @'
param([string]$OutputDirectory,[string]$ArchiveSuffix)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
[IO.File]::AppendAllText((Join-Path $repo 'artifacts\events.txt'), "package`n")
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\package-real.ps1" -OutputDirectory $OutputDirectory -ArchiveSuffix $ArchiveSuffix
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ((Get-Content -LiteralPath (Join-Path $repo 'artifacts\mode.txt') -Raw).Trim() -eq 'zip-extra') {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Get-ChildItem -LiteralPath (Join-Path $repo 'artifacts\release') -Filter "*-$ArchiveSuffix.zip"
    $archive = [IO.Compression.ZipFile]::Open($zip.FullName, [IO.Compression.ZipArchiveMode]::Update)
    try { $entry = $archive.CreateEntry('unexpected.dll'); $writer = New-Object IO.StreamWriter($entry.Open()); $writer.Write('unexpected'); $writer.Dispose() } finally { $archive.Dispose() }
    # Keep the checksum valid: the installer must validate the ZIP allowlist itself.
    $sums = Join-Path $zip.DirectoryName 'SHA256SUMS.txt'
    $lines = @(Get-Content -LiteralPath $sums | Where-Object { $_ -notmatch [Regex]::Escape($zip.Name) })
    $lines += (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + $zip.Name
    [IO.File]::WriteAllLines($sums, $lines)
}
'@
Write-Fixture (Join-Path $tools 'compiler.ps1') @'
$ErrorActionPreference = 'Stop'
if ($args -contains '/?') { Write-Output 'Inno Setup 6 Command-Line Compiler'; exit 0 }
$repo = Split-Path -Parent $PSScriptRoot
[IO.File]::AppendAllText((Join-Path $repo 'artifacts\events.txt'), "compile`n")
$mode = (Get-Content -LiteralPath (Join-Path $repo 'artifacts\mode.txt') -Raw).Trim()
if ($mode -eq 'compiler-failure') { exit 23 }
$runtime = @($args | Where-Object { $_ -like '/DRuntimeDirectory=*' })
$release = @($args | Where-Object { $_ -like '/DReleaseDirectory=*' })
if ($runtime.Count -ne 1 -or $release.Count -ne 1 -or $args -cnotcontains '/DAppVersion=1.0.0') { throw 'Sai tham số ISCC.' }
$output = $release[0].Substring('/DReleaseDirectory='.Length)
[void](New-Item -ItemType Directory -Path $output -Force)
[IO.File]::WriteAllText((Join-Path $repo 'artifacts\runtime-path.txt'), $runtime[0].Substring('/DRuntimeDirectory='.Length))
[IO.File]::WriteAllText((Join-Path $output 'MosWord2019-Setup-1.0.0.exe'), ('synthetic-setup-' + [Guid]::NewGuid().ToString('N')))
'@
Write-Fixture (Join-Path $tools 'extractor.ps1') @'
$ErrorActionPreference = 'Stop'
if ($args -contains '--version') { Write-Output 'innoextract 1.9'; exit 0 }
$repo = Split-Path -Parent $PSScriptRoot
[IO.File]::AppendAllText((Join-Path $repo 'artifacts\events.txt'), "extract`n")
$mode = (Get-Content -LiteralPath (Join-Path $repo 'artifacts\mode.txt') -Raw).Trim()
if ($mode -eq 'extractor-failure') { exit 24 }
if ($args.Count -ne 4 -or $args[0] -cne '--extract' -or $args[1] -cne '--output-dir' -or -not (Test-Path -LiteralPath $args[3] -PathType Leaf)) { throw 'Sai tham số extraction.' }
$app = Join-Path $args[2] 'app'
[void](New-Item -ItemType Directory -Path $app -Force)
$runtime = [IO.File]::ReadAllText((Join-Path $repo 'artifacts\runtime-path.txt'))
Copy-Item -Path (Join-Path $runtime '*') -Destination $app
switch ($mode) {
    'extract-extra' { [IO.File]::WriteAllText((Join-Path $app 'unexpected.dll'), 'unexpected') }
    'extract-missing' { Remove-Item -LiteralPath (Join-Path $app 'MosWord2019.Core.dll') }
    'extract-hash' { [IO.File]::AppendAllText((Join-Path $app 'MosWord2019.Core.dll'), 'changed') }
}
'@
foreach ($name in @('compiler', 'extractor')) {
    Write-Fixture (Join-Path $tools "$name.cmd") "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0$name.ps1`" %*`r`nexit /b %errorlevel%`r`n"
}
& git -C $sandbox init -q
& git -C $sandbox add .
& git -C $sandbox -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm fixture
if ($LASTEXITCODE -ne 0) { throw 'Không tạo được repository thử nghiệm.' }
$release = Join-Path $sandbox 'artifacts\release'
[void](New-Item -ItemType Directory -Path $release -Force)
$setup = Join-Path $release 'MosWord2019-Setup-1.0.0.exe'
Write-Fixture $setup 'previous verified installer'
$sums = Join-Path $release 'SHA256SUMS.txt'
Write-Fixture $sums ((('a' * 64) + "  retained.zip`n") + (('b' * 64) + "  MosWord2019-Setup-1.0.0.exe`n"))
$script:caseNumber = 0
function Run-Installer([string] $Mode, [bool] $Success, [string] $ExpectedStages, [string] $Diagnostic = '') {
    $script:caseNumber++
    Write-Fixture (Join-Path $sandbox 'artifacts\mode.txt') $Mode
    Write-Fixture (Join-Path $sandbox 'artifacts\events.txt') ''
    if ($Mode -eq 'latest-receipt-mismatch') { $baselineReceipt.serverUrl = 'https://192.0.2.25/'; Save-Baseline }
    if ($Mode -eq 'latest-receipt-hash') { [IO.File]::AppendAllText((Join-Path $baseline 'public-trust.json'), ' ') }
    $before = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    $beforeSetupEntry = @(Get-Content -LiteralPath $sums | Where-Object { $_ -match '  MosWord2019-Setup-1\.0\.0\.exe$' }) -join "`n"
    $log = Join-Path $sandbox ("artifacts\case-$script:caseNumber-$Mode.log")
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $deploy 'build-installer.ps1') -ISCCPath (Join-Path $tools 'compiler.cmd') -InnoExtractPath (Join-Path $tools 'extractor.cmd') *> $log
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedPreference
    $baselineReceipt.serverUrl = 'https://103.77.209.191/'
    Save-Baseline
    if (($exitCode -eq 0) -ne $Success) { throw "Kết quả installer không đúng kỳ vọng ($Mode); xem $log" }
    $actualStages = ([IO.File]::ReadAllText((Join-Path $sandbox 'artifacts\events.txt'))).Trim().Replace("`r", '').Replace("`n", ',')
    if ($actualStages -cne $ExpectedStages) { throw "Sai bước kiểm tra ($Mode): '$actualStages', cần '$ExpectedStages'; xem $log" }
    if ($Diagnostic -and (Get-Content -LiteralPath $log -Raw) -notmatch $Diagnostic) { throw "Lỗi không đúng nguyên nhân ($Mode); xem $log" }
    if (-not $Success -and (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -cne $before) { throw "Installer đã xác minh bị thay sau lỗi $Mode." }
    if (-not $Success -and ((@(Get-Content -LiteralPath $sums | Where-Object { $_ -match '  MosWord2019-Setup-1\.0\.0\.exe$' }) -join "`n") -cne $beforeSetupEntry)) { throw "Checksum installer cũ bị thay sau lỗi $Mode." }
    if ($Success) {
        $entries = @(Get-Content -LiteralPath $sums | Where-Object { $_ -match '  MosWord2019-Setup-1\.0\.0\.exe$' })
        $expected = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() + '  MosWord2019-Setup-1.0.0.exe'
        if ($entries.Count -ne 1 -or $entries[0] -cne $expected) { throw 'SHA256SUMS thiếu/sai/trùng installer entry.' }
        if (@(Get-Content -LiteralPath $sums | Where-Object { $_ -ceq (('a' * 64) + '  retained.zip') }).Count -ne 1) { throw 'Mất checksum không liên quan.' }
    }
    Write-Host "PASS: $Mode"
}
Write-Fixture (Join-Path $sandbox 'dirty.txt') 'dirty'
Run-Installer 'dirty-untracked' $false '' 'Repository phải sạch'
Remove-Item -LiteralPath (Join-Path $sandbox 'dirty.txt')
[IO.File]::AppendAllText((Join-Path $sandbox '.gitignore'), "# dirty tracked`n")
Run-Installer 'dirty-tracked' $false '' 'Repository phải sạch'
Write-Fixture (Join-Path $sandbox '.gitignore') "artifacts/`n"
Run-Installer 'latest-receipt-mismatch' $false '' 'Trust production mới nhất không khớp'
Run-Installer 'latest-receipt-hash' $false '' 'Hash biên nhận production không khớp'
Run-Installer 'trust-mismatch' $false 'build' 'verify-client-trust.ps1'
Run-Installer 'zip-extra' $false 'build,package' 'ZIP runtime không đúng allow-list'
Run-Installer 'compiler-failure' $false 'build,package,compile' 'mã 23'
Run-Installer 'extractor-failure' $false 'build,package,compile,extract' 'mã 24'
Run-Installer 'extract-extra' $false 'build,package,compile,extract' 'Payload Setup sai số tệp'
Run-Installer 'extract-missing' $false 'build,package,compile,extract' 'Payload Setup sai số tệp'
Run-Installer 'extract-hash' $false 'build,package,compile,extract' 'Payload Setup chứa tệp ngoài allow-list hoặc sai hash'
Run-Installer 'success' $true 'build,package,compile,extract'
Run-Installer 'replace-success' $true 'build,package,compile,extract'
Write-Host "PASS: disposable installer orchestration, failure preservation and SHA256 replacement. Evidence: $sandbox"
