Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-IPv4([string] $Value) {
    $address = $null
    if ($Value -notmatch '^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){3}$' -or
        -not [Net.IPAddress]::TryParse($Value, [ref] $address) -or
        $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'Địa chỉ VPS phải là IPv4 hợp lệ.'
    }
    return $Value
}

function Assert-PublicBytes([string] $Value, [string] $Name, [switch] $RejectZero) {
    try { $bytes = [Convert]::FromBase64String($Value) } catch { throw "$Name phải là Base64 của 32 byte." }
    if ($bytes.Length -ne 32 -or [Convert]::ToBase64String($bytes) -cne $Value) {
        throw "$Name phải là Base64 chuẩn của 32 byte."
    }
    if ($RejectZero -and -not ($bytes | Where-Object { $_ -ne 0 })) { throw "$Name không được bằng toàn số 0." }
    return $Value
}

function Get-ProductionOrigin([string] $Value) {
    if ($Value -notmatch '^https://') { return 'https://' + (Assert-IPv4 $Value) + '/' }
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref] $uri) -or
        $uri.Scheme -ne 'https' -or $uri.UserInfo -ne '' -or $uri.AbsolutePath -ne '/' -or
        $uri.Query -ne '' -or $uri.Fragment -ne '' -or $uri.Port -ne 443) {
        throw 'URL phải có dạng https://IPv4/ và dùng cổng 443.'
    }
    [void] (Assert-IPv4 $uri.Host)
    return $uri.AbsoluteUri
}

function Invoke-Checked([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Lệnh $Command thất bại (mã $LASTEXITCODE)." }
}

function Write-Utf8Lf([string] $Path, [string] $Value) {
    [IO.File]::WriteAllText($Path, $Value.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
}
