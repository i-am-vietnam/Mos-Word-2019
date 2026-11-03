[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [Parameter(Mandatory = $true)][string] $ServerUrl,
    [Parameter(Mandatory = $true)][string] $SpkiPin,
    [Parameter(Mandatory = $true)][string] $ManifestPublicKey
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Run in a fresh Windows PowerShell process so framework assemblies are isolated.
$projects = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'MosWord2019.Projects.dll'))
$type = $projects.GetType('MosWord2019.Projects.OnlinePackages.PackageTrust', $true)
if ($type.GetField('PublicKey').GetRawConstantValue() -cne $ManifestPublicKey) { throw 'Khóa public đã biên dịch không khớp.' }
$app = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'MosWord2019.exe'))
$settings = $app.GetType('MosWord2019.CompiledServerSettings', $true)
$flags = [Reflection.BindingFlags] 'Static,NonPublic'
if ($settings.GetField('Origin', $flags).GetRawConstantValue() -cne $ServerUrl -or
    $settings.GetField('SpkiPin', $flags).GetRawConstantValue() -cne $SpkiPin) { throw 'URL/pin đã biên dịch không khớp.' }
Write-Host 'Đã xác minh URL, pin và khóa public trong assembly Release.'
