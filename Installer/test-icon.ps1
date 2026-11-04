[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('MosWord2019-icon-' + [guid]::NewGuid().ToString('N') + '.ico')
try {
    & (Join-Path $PSScriptRoot 'generate-icon.ps1') -OutputPath $temporary
    $committed = Join-Path $PSScriptRoot 'branding\MosWord2019.ico'
    if ((Get-FileHash -LiteralPath $temporary).Hash -ne (Get-FileHash -LiteralPath $committed).Hash) {
        throw 'Regeneration does not reproduce the committed icon.'
    }
    $bytes = [System.IO.File]::ReadAllBytes($temporary)
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    if ([BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1 -or
        [BitConverter]::ToUInt16($bytes, 4) -ne $sizes.Count) { throw 'Invalid ICO header.' }
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $entry = 6 + 16 * $i
        $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
        $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
        if ($bytes[$entry] -ne ($sizes[$i] % 256) -or $bytes[$entry + 1] -ne ($sizes[$i] % 256) -or
            [BitConverter]::ToUInt16($bytes, $entry + 6) -ne 32 -or $offset + $length -gt $bytes.Length) { throw 'Invalid ICO frame.' }
        $frame = New-Object System.IO.MemoryStream(,$bytes[$offset..($offset + $length - 1)])
        $image = [System.Drawing.Image]::FromStream($frame)
        try {
            if ($image.Width -ne $sizes[$i] -or $image.Height -ne $sizes[$i] -or
                $image.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid) { throw 'Invalid PNG frame dimensions or encoding.' }
        } finally { $image.Dispose(); $frame.Dispose() }
    }
    Write-Output 'PASS: approved source hash, all nine 32-bit PNG frames, dimensions and reproducibility.'
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
