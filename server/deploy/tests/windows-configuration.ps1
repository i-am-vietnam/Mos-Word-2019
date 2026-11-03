# No network, signing key, build, or external modules are required.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\windows-common.ps1"
$script:count = 0
function Accepts([scriptblock] $Check) { & $Check | Out-Null; $script:count++ }
function Rejects([scriptblock] $Check) {
    $rejected = $false
    try { & $Check | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Đã chấp nhận cấu hình không hợp lệ.' }
    $script:count++
}
Accepts { Assert-IPv4 '192.0.2.25' }
Accepts { Get-ProductionOrigin '192.0.2.25' }
Accepts { Get-ProductionOrigin 'https://192.0.2.25/' }
$key = 'ghmSxvuC9WpPCKD1ywcYP52kL2sg8+VLa5GsFmjAwGM='
Accepts { Assert-PublicBytes $key 'public key' -RejectZero }
foreach ($value in @('', 'localhost', '::1', '192.0.2.999', '192.0.2.1;id', '0192.0.2.1', '-oProxyCommand=id')) {
    Rejects { Assert-IPv4 $value }
}
foreach ($value in @('http://192.0.2.1/', 'https://localhost/', 'https://192.0.2.1/admin', 'https://192.0.2.1/?a=1', 'https://192.0.2.1/#x', 'https://u:p@192.0.2.1/', 'https://192.0.2.1:8443/')) {
    Rejects { Get-ProductionOrigin $value }
}
foreach ($value in @('', 'not-base64', 'AQ==', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=', ($key + "`n"), $key.Replace('M=', 'N='))) {
    Rejects { Assert-PublicBytes $value 'public key' -RejectZero }
}
# Validate both the direct MSBuild guard and PowerShell's canonical input rule.
$project = [xml] [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\..\MosWord2019.Projects\MosWord2019.Projects.csproj'))
$ns = New-Object Xml.XmlNamespaceManager $project.NameTable
$ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
if (-not $project.SelectSingleNode('//m:Target[@Name="CompilePackageTrust"]', $ns)) { throw 'Thiếu target biên dịch public key.' }
Get-ChildItem (Join-Path $PSScriptRoot '..') -Filter '*.ps1' -File | ForEach-Object {
    $tokens = $null; $errors = $null
    [void] [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref] $tokens, [ref] $errors)
    if ($errors.Count) { throw ($errors | Out-String) }
    $script:count++
}
Write-Host "PASS: $script:count kiểm tra cấu hình Windows."
