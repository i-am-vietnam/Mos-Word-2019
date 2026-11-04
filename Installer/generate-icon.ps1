[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'branding\app-icon-source.png'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'branding\MosWord2019.ico'),
    [string]$ExpectedSha256 = '3b0040fb2c45f0fdc9be68b1905d14b7ea013046db0166a90230d7728432373a'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing
if ((Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash -ne $ExpectedSha256) {
    throw 'Icon source SHA256 does not match the approved artwork.'
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$source = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourcePath).Path)
try {
    $frames = foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = New-Object System.IO.MemoryStream
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = $size / [double][Math]::Max($source.Width, $source.Height)
            $width = [Math]::Max(1, [int][Math]::Round($source.Width * $scale))
            $height = [Math]::Max(1, [int][Math]::Round($source.Height * $scale))
            $rectangle = New-Object System.Drawing.Rectangle(([int](($size - $width) / 2)), ([int](($size - $height) / 2)), $width, $height)
            $graphics.DrawImage($source, $rectangle)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            ,$stream.ToArray()
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
# PNG frames preserve alpha, including the Windows-required PNG-compressed 256px entry.
$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = $sizes[$i] % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    [System.IO.File]::WriteAllBytes($OutputPath, $output.ToArray())
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output "Generated $OutputPath ($($sizes -join ', ')px)."
