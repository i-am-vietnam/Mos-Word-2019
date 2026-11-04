# Disposable repository and compiled public-trust fixtures; no production archive.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$sandbox = Join-Path $repo ('artifacts\package-tests-' + [Guid]::NewGuid().ToString('N'))
$deploy = Join-Path $sandbox 'server\deploy'
[void] (New-Item -ItemType Directory -Path $deploy -Force)
foreach ($name in @('package-client.ps1', 'verify-client-trust.ps1', 'windows-common.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\$name") -Destination $deploy }
[IO.File]::WriteAllText((Join-Path $sandbox '.gitignore'), "artifacts/`n")
& git -C $sandbox init -q
& git -C $sandbox add .
& git -C $sandbox -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm fixture
if ($LASTEXITCODE -ne 0) { throw 'Không tạo được repository thử nghiệm.' }
$source = Join-Path $sandbox 'artifacts\source'
[void] (New-Item -ItemType Directory -Path $source -Force)
$key = [Convert]::ToBase64String([byte[]](1..32))
$pin = [Convert]::ToBase64String([byte[]](32..63))
Add-Type -TypeDefinition "namespace MosWord2019.Projects.OnlinePackages { public static class PackageTrust { public const string PublicKey = `"$key`"; } }" -OutputAssembly (Join-Path $source 'MosWord2019.Projects.dll')
Add-Type -TypeDefinition "namespace MosWord2019 { internal static class CompiledServerSettings { internal const string Origin = `"https://192.0.2.25/`"; internal const string SpkiPin = `"$pin`"; } public class Entry { public static void Main() {} } }" -OutputAssembly (Join-Path $source 'MosWord2019.exe') -OutputType ConsoleApplication
foreach ($name in @('MosWord2019.exe.config', 'MosWord2019.Core.dll', 'MosWord2019.Data.dll', 'MosWord2019.Word.dll', 'Newtonsoft.Json.dll', 'BouncyCastle.Cryptography.dll', 'MosWord2019.pdb', 'Newtonsoft.Json.xml')) { [IO.File]::WriteAllText((Join-Path $source $name), 'fixture') }
$receipt = [ordered]@{ configuration = 'Release'; serverUrl = 'https://192.0.2.25/'; spkiSha256 = $pin; manifestPublicKey = $key; files = @(Get-ChildItem -LiteralPath $source -File | ForEach-Object { [ordered]@{ file=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }) }
function Save-Receipt {
    $path = Join-Path $source 'public-trust.json'
    [IO.File]::WriteAllText($path, ($receipt | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText("$path.sha256", ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + "  public-trust.json`n"))
}
Save-Receipt
function Run-Package([bool] $Success) {
    $log = Join-Path $sandbox 'artifacts\last-run.log'
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $deploy 'package-client.ps1') -OutputDirectory $source *> $log
    $ErrorActionPreference = $savedPreference
    if (($LASTEXITCODE -eq 0) -ne $Success) { throw "Kết quả package không đúng kỳ vọng; xem $log" }
}
[IO.File]::WriteAllText((Join-Path $sandbox 'dirty.txt'), 'dirty')
Run-Package $false
Remove-Item -LiteralPath (Join-Path $sandbox 'dirty.txt')
[IO.File]::AppendAllText((Join-Path $sandbox '.gitignore'), "# dirty tracked file`n")
Run-Package $false
[IO.File]::WriteAllText((Join-Path $sandbox '.gitignore'), "artifacts/`n")
[IO.File]::WriteAllText((Join-Path $source 'unexpected.dll'), 'unexpected')
Run-Package $false
Remove-Item -LiteralPath (Join-Path $source 'unexpected.dll')
[void] (New-Item -ItemType Directory -Path (Join-Path $source 'Projects'))
Run-Package $false
Remove-Item -LiteralPath (Join-Path $source 'Projects')
Move-Item -LiteralPath (Join-Path $source 'MosWord2019.Core.dll') -Destination (Join-Path $sandbox 'artifacts\missing-runtime.dll')
Run-Package $false
Move-Item -LiteralPath (Join-Path $sandbox 'artifacts\missing-runtime.dll') -Destination (Join-Path $source 'MosWord2019.Core.dll')
[IO.File]::AppendAllText((Join-Path $source 'MosWord2019.Core.dll'), 'changed')
Run-Package $false
[IO.File]::WriteAllText((Join-Path $source 'MosWord2019.Core.dll'), 'fixture')
$receipt.serverUrl = 'https://192.0.2.26/'
Save-Receipt
Run-Package $false
$receipt.serverUrl = 'https://192.0.2.25/'
Save-Receipt
New-Item -ItemType Junction -Path (Join-Path $sandbox 'artifacts\release') -Target $source | Out-Null
Run-Package $false
[IO.Directory]::Delete((Join-Path $sandbox 'artifacts\release'))
Run-Package $true
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = @(Get-ChildItem -LiteralPath (Join-Path $sandbox 'artifacts\release') -Filter '*.zip')
if ($zip.Count -ne 1) { throw 'Sai số ZIP.' }
$opened = [IO.Compression.ZipFile]::OpenRead($zip[0].FullName)
try {
    $expected = @('BouncyCastle.Cryptography.dll', 'MosWord2019.Core.dll', 'MosWord2019.Data.dll', 'MosWord2019.exe', 'MosWord2019.exe.config', 'MosWord2019.Projects.dll', 'MosWord2019.Word.dll', 'Newtonsoft.Json.dll')
    if (@(Compare-Object ($opened.Entries.FullName | Sort-Object) ($expected | Sort-Object)).Count) { throw 'ZIP không đúng runtime allowlist.' }
} finally { $opened.Dispose() }
$sha = (Get-FileHash -LiteralPath $zip[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ([IO.File]::ReadAllText((Join-Path $zip[0].DirectoryName 'SHA256SUMS.txt')).Trim() -cne "$sha  $($zip[0].Name)") { throw 'SHA256SUMS sai.' }
Run-Package $false # Never overwrite an existing archive.
if (@(Get-ChildItem -LiteralPath $zip[0].DirectoryName -Directory -Force).Count) { throw 'Còn stage tạm.' }
Write-Host 'PASS: dirty tracked/untracked, unexpected DLL, Projects, missing runtime, tampered hash, staged trust mismatch, junction, exact 8-file ZIP, SHA256, collision và cleanup.'
Write-Host "Evidence synthetic repository: $sandbox"
