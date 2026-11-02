#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $env:LOCALAPPDATA 'MosWord2019.Server/secrets')
)

# Invoke in a child scope so dot-sourcing retains only process environment values,
# never password/key variables. No certificate is installed in a trust store.
& {
    param([string] $Directory)
    $ErrorActionPreference = 'Stop'
    if (-not $IsWindows) { throw 'This development helper requires Windows and PowerShell 7.' }
    $root = [IO.Path]::GetFullPath($Directory)
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\', '/')
    if ($root.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Certificate secrets must remain outside this repository.'
    }

    function Assert-SafePath([string] $Path) {
        for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
            if (Test-Path -LiteralPath $current) {
                $item = Get-Item -LiteralPath $current -Force
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Certificate paths must not traverse reparse points.'
                }
            }
            if ((Test-Path -LiteralPath (Join-Path $current '.git'))) {
                throw 'Certificate secrets must remain outside every repository.'
            }
        }
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    function Set-PrivateAcl([string] $Path, [bool] $IsDirectory) {
        Assert-SafePath $Path
        if ($IsDirectory) {
            $acl = [Security.AccessControl.DirectorySecurity]::new()
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
        }
        else {
            $acl = [Security.AccessControl.FileSecurity]::new()
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow')
        }
        $acl.SetOwner($sid)
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule($rule)
        if ($IsDirectory) { [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl) }
        else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path), $acl) }
    }
    Assert-SafePath $root
    if (-not (Test-Path -LiteralPath $root)) {
        New-Item -ItemType Directory -Path $root -Force | Out-Null
    }
    Set-PrivateAcl $root $true
    $pfxPath = Join-Path $root 'dev-server.pfx'
    $passwordPath = Join-Path $root 'dev-server-password.secret'
    $publicPath = Join-Path $root 'dev-server.cer'
    foreach ($path in @($pfxPath, $passwordPath, $publicPath)) { Assert-SafePath $path }
    $hasPfx = Test-Path -LiteralPath $pfxPath
    $hasPassword = Test-Path -LiteralPath $passwordPath
    if ($hasPfx -ne $hasPassword) {
        throw 'Incomplete development certificate pair. Recover the matching external password/PFX, or explicitly remove the old development files and rerun. Existing files were not overwritten.'
    }
    $certificate = $null
    $rsa = $null
    $passwordBytes = $null
    $pfxBytes = $null
    $created = [Collections.Generic.List[string]]::new()
    try {
        if ($hasPfx) {
            Set-PrivateAcl $pfxPath $false
            Set-PrivateAcl $passwordPath $false
            if ((Get-Item -LiteralPath $passwordPath).Length -gt 1024 -or
                (Get-Item -LiteralPath $pfxPath).Length -gt 65536) { throw 'Invalid development certificate file size.' }
            $password = [IO.File]::ReadAllText($passwordPath)
            if ([string]::IsNullOrWhiteSpace($password)) { throw 'Development certificate password is missing.' }
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $pfxPath, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
            if (-not $certificate.HasPrivateKey -or [DateTime]::UtcNow -lt $certificate.NotBefore.ToUniversalTime() -or
                [DateTime]::UtcNow -ge $certificate.NotAfter.ToUniversalTime()) {
                throw 'Development certificate is invalid or expired. Explicitly remove the old development files and rerun to replace it.'
            }
            if (Test-Path -LiteralPath $publicPath) {
                $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publicPath)
                try {
                    if ($publicCertificate.Thumbprint -ne $certificate.Thumbprint) {
                        throw 'Public certificate does not match the existing PFX.'
                    }
                }
                finally { $publicCertificate.Dispose() }
            }
        }
        else {
            if (Test-Path -LiteralPath $publicPath) { throw 'A public certificate already exists without its PFX. Recover or explicitly remove old development files before recreating them.' }
            $rsa = [Security.Cryptography.RSA]::Create()
            $rsa.KeySize = 3072
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
                'CN=localhost', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
            $san.AddDnsName('localhost')
            $san.AddIpAddress([Net.IPAddress]::Loopback)
            $san.AddIpAddress([Net.IPAddress]::IPv6Loopback)
            $request.CertificateExtensions.Add($san.Build())
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
            $usage = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($usage, $true))
            $eku = [Security.Cryptography.OidCollection]::new()
            $null = $eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1', 'Server Authentication'))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
            $now = [DateTimeOffset]::UtcNow
            $certificate = $request.CreateSelfSigned($now.AddMinutes(-5), $now.AddYears(1))
            $passwordBytes = [byte[]]::new(32)
            $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($passwordBytes) }
            finally { $rng.Dispose() }
            $password = [Convert]::ToBase64String($passwordBytes)
            $pfxBytes = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)
            # CreateNew prevents overwriting existing material, including a racing invocation.
            foreach ($record in @(
                @{ Path = $passwordPath; Bytes = [Text.Encoding]::UTF8.GetBytes($password) },
                @{ Path = $pfxPath; Bytes = $pfxBytes },
                @{ Path = $publicPath; Bytes = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert) }
            )) {
                Assert-SafePath $record.Path
                $stream = [IO.File]::Open($record.Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $created.Add($record.Path)
                try { $stream.Write($record.Bytes, 0, $record.Bytes.Length); $stream.Flush($true) }
                finally { $stream.Dispose() }
                Set-PrivateAcl $record.Path $false
            }
        }
        $publicRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        try { $spki = $publicRsa.ExportSubjectPublicKeyInfo() }
        finally { $publicRsa.Dispose() }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $pin = [Convert]::ToBase64String($sha.ComputeHash($spki)) }
        finally { $sha.Dispose() }
        $env:ASPNETCORE_Kestrel__Certificates__Default__Path = $pfxPath
        $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $password
        $env:ASPNETCORE_URLS = 'https://localhost:7118'
        [pscustomobject]@{ CertificatePath = $pfxPath; SpkiSha256 = $pin }
    }
    catch {
        foreach ($path in $created) { Assert-SafePath $path; Remove-Item -LiteralPath $path -Force }
        # Do not expose crypto exception details that could contain supplied secret values.
        throw 'Development certificate setup failed. Check external file permissions and matching PFX/password files. Recover the matching pair, or explicitly remove old development certificate files and rerun; no existing certificate was overwritten.'
    }
    finally {
        if ($certificate) { $certificate.Dispose() }
        if ($rsa) { $rsa.Dispose() }
        if ($passwordBytes) { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
        if ($pfxBytes) { [Array]::Clear($pfxBytes, 0, $pfxBytes.Length) }
        $password = $null
    }
} $OutputDirectory
