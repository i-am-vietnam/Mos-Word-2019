# Public values only: the signing private key never belongs on the build PC.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias('VpsIp')][string] $ServerUrl,
    [Parameter(Mandatory = $true)][string] $SpkiPin,
    [Parameter(Mandatory = $true)][string] $ManifestPublicKey,
    [string] $MSBuildPath,
    [string] $OutputDirectory
)
. "$PSScriptRoot\windows-common.ps1"
$origin = Get-ProductionOrigin $ServerUrl
$pin = Assert-PublicBytes $SpkiPin 'SpkiPin' -RejectZero
$key = Assert-PublicBytes $ManifestPublicKey 'ManifestPublicKey' -RejectZero
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts\client-production\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = (Join-Path $repo 'artifacts') + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory phải ở dưới artifacts của repository.' }
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory đã tồn tại; chọn thư mục mới để giữ nguyên bản build trước.' }
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Không tìm thấy vswhere; truyền -MSBuildPath tới Visual Studio MSBuild.' }
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) { throw 'Không tìm thấy Visual Studio MSBuild.' }
[void] (New-Item -ItemType Directory -Path $output)
# Rebuild all referenced assemblies so the selected key cannot reuse a stale binary.
Invoke-Checked $MSBuildPath @((Join-Path $repo 'MosWord2019.WinForms\MosWord2019.WinForms.csproj'), '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=AnyCPU', '/p:UseLocalPackages=false', "/p:OutDir=$output\", "/p:OnlineServerUrl=$origin", "/p:OnlineServerSpkiPin=$pin", "/p:PackageManifestPublicKey=$key", '/nologo', '/verbosity:minimal', '/clp:Summary')
Invoke-Checked 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'verify-client-trust.ps1'), '-OutputDirectory', $output, '-ServerUrl', $origin, '-SpkiPin', $pin, '-ManifestPublicKey', $key)
$files = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$receipt = [ordered]@{ serverUrl = $origin; spkiSha256 = $pin; manifestPublicKey = $key; configuration = 'Release'; files = $files }
$receiptPath = Join-Path $output 'public-trust.json'
Write-Utf8Lf $receiptPath (($receipt | ConvertTo-Json -Depth 5) + "`n")
Write-Utf8Lf "$receiptPath.sha256" (((Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()) + "  public-trust.json`n")
Write-Host "Đã build client Release: $output"
Write-Host "Biên nhận giá trị công khai: $receiptPath"
