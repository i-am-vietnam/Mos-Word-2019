# Static contracts and optional compiler smoke only. Never run Setup/Uninstall.
[CmdletBinding()]
param([string] $Compiler, [string] $RuntimeDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$definition = Join-Path $repo 'Installer\MosWord2019.iss'
$text = [IO.File]::ReadAllText($definition)
function Assert-Definition([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
$runtime = @('MosWord2019.exe', 'MosWord2019.exe.config', 'MosWord2019.Core.dll', 'MosWord2019.Data.dll', 'MosWord2019.Projects.dll', 'MosWord2019.Word.dll', 'Newtonsoft.Json.dll', 'BouncyCastle.Cryptography.dll')
$files = @([regex]::Matches($text, '(?m)^Source: "\{#RuntimeDirectory\}\\([^"\r\n]+)";') | ForEach-Object { $_.Groups[1].Value })
Assert-Definition (($files.Count -eq 8) -and -not (Compare-Object $runtime $files -CaseSensitive)) 'Allowlist bộ cài phải đúng tám runtime C7.'
$contracts = @(
    'PrivilegesRequired=lowest', 'DefaultDirName={localappdata}\Programs\MosWord2019',
    'UsePreviousAppDir=no', 'MinVersion=10.0', 'AppMutex=Local\MosWord2019.Trainer',
    'AppId={{5A497F38-92D0-4C9B-BCF1-D3C8E28A42B0}',
    'OutputBaseFilename=MosWord2019-Setup-{#AppVersion}',
    'Release >= 461808', 'HasNetInView(HKLM32)', 'HasNetInView(HKLM64)',
    'HasWordInView(HKLM32)', 'HasWordInView(HKLM64)',
    'SOFTWARE\Classes\CLSID\{000209FF-0000-0000-C000-000000000046}\LocalServer32',
    'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WINWORD.EXE',
    'if UninstallSilent then exit;', 'MB_YESNO or MB_DEFBUTTON2',
    'if CurUninstallStep <> usPostUninstall then exit;',
    "LocalData := ExpandConstant('{localappdata}\MosWord2019');",
    "Working := ExpandConstant('{userdocs}\MosWord2019\Working');",
    'if not SafeExistingPath(Path) then exit;',
    '((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0)',
    '((Entry.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0)',
    'Current := RemoveBackslashUnlessRoot(ExtractFileDir(Current));',
    'OpenDirectory(Current, 1, 1, 0, 3, $02200000, 0)',
    'QueryAttributeTag(Handle, 9, Info, 8)', 'UnlockDirectories(Handles);',
    'LaunchApp=Chạy MOS Word 2019',
    'Name: "vietnamese"; MessagesFile: "Languages\Vietnamese.isl"',
    'https://dotnet.microsoft.com/download/dotnet-framework/net472',
    'WindowsVersionNotSupported=MOS Word 2019 yêu cầu Windows 10 hoặc Windows 11.'
)
foreach ($contract in $contracts) {
    Assert-Definition ($text.Contains($contract)) "Thiếu hợp đồng bộ cài: $contract"
}
foreach ($translationFile in @(
    @{ Name='Vietnamese.isl'; Hash='826bc509b612d96ccfec89b81956640a3b56b547d194404e5ff95ae1a85babf0' },
    @{ Name='LICENSE-Inno-Setup.txt'; Hash='92f662bffd07b3d432e1399412877da2419e4962128a0a8f4db7a0522a6c9f62' }
)) {
    $path = Join-Path $repo ('Installer\Languages\' + $translationFile.Name)
    Assert-Definition ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $translationFile.Hash) 'Bản dịch hoặc giấy phép upstream đã bị thay đổi.'
}
Assert-Definition ($text -notmatch '(?im)^\[(UninstallDelete|InstallDelete|Registry)\]|\bDelTree\b|HKCU|HKEY_CURRENT_USER|recurseallsubdirs|Flags:.*unchecked') 'Bộ cài chứa ghi registry/xóa rộng hoặc shortcut không mặc định.'
Assert-Definition ($text -notmatch '(?im)^procedure CurStepChanged|^function PrepareToInstall') 'Không được thêm cleanup khi cài/nâng cấp.'
Assert-Definition ($text.Contains('Name: "{userprograms}\MOS Word 2019"; Filename: "{app}\MosWord2019.exe"') -and $text -notmatch 'Tasks: startmenu|Name: "startmenu"') 'Start Menu phải luôn được tạo.'
Assert-Definition (([regex]::Matches($text, 'DeleteSafeTree\((LocalData|Working), \1\)').Count) -eq 2) 'Cleanup phải dùng đúng hai root dữ liệu.'
$lifetime = [IO.File]::ReadAllText((Join-Path $repo 'MosWord2019.Projects\ApplicationLifetime.cs'))
Assert-Definition ($lifetime.Contains('@"Local\MosWord2019.Trainer"')) 'Mutex bộ cài không khớp runtime.'
# Registry-view truth table: only HKLM release >=472 and either Word registration qualifies.
foreach ($release32 in @(0, 461807, 461808, 528040)) {
    foreach ($release64 in @(0, 461807, 461808, 528040)) {
        foreach ($word32 in @($false, $true)) {
            foreach ($word64 in @($false, $true)) {
                foreach ($is64 in @($false, $true)) {
                    $net = ($release32 -ge 461808) -or ($is64 -and $release64 -ge 461808)
                    $word = $word32 -or ($is64 -and $word64)
                    $expected = (($release32 -ge 461808) -or ($is64 -and $release64 -ge 461808)) -and ($word32 -or ($is64 -and $word64))
                    Assert-Definition (($net -and $word) -eq $expected) 'Sai mô hình prerequisite.'
                }
            }
        }
    }
}
# Containment model: separator boundary rejects sibling prefixes and traversal.
$root = 'C:\Users\Fixture\AppData\Local\MosWord2019'
foreach ($case in @(
    @{ Path=$root; Safe=$true }, @{ Path="$root\OnlineResults\pending.bin"; Safe=$true },
    @{ Path="${root}Other\personal.docx"; Safe=$false },
    @{ Path="$root\..\personal.docx"; Safe=$false },
    @{ Path='\\server\share\MosWord2019'; Safe=$false }
)) {
    $path = $case.Path
    $safe = ($path -match '^[A-Za-z]:\\') -and ([IO.Path]::GetFullPath($path) -eq $path) -and
        (($path -eq $root) -or $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase))
    Assert-Definition ($safe -eq $case.Safe) "Sai mô hình containment: $path"
}
Write-Host 'PASS: static contracts, prerequisite/containment models; không chạy installer/uninstaller.'
# Exercise the Win32 guard contract on disposable directories, never on user data.
if (-not ('InstallerDirectoryGuard' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class InstallerDirectoryGuard {
    [StructLayout(LayoutKind.Sequential)] public struct TagInfo { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out TagInfo info, uint size);
    public static SafeFileHandle Open(string path) {
        var handle = CreateFileW(path, 1, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        TagInfo info;
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out info, 8) ||
            (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) {
            handle.Dispose(); return null;
        }
        return handle;
    }
}
'@
}
$fixture = Join-Path $repo ('artifacts\installer-safety-' + [Guid]::NewGuid().ToString('N'))
$inside = Join-Path $fixture 'inside'
$outside = Join-Path $fixture 'outside'
$junction = Join-Path $inside 'redirect'
[void] (New-Item -ItemType Directory -Path $inside, $outside -Force)
$sentinel = Join-Path $outside 'personal.txt'
[IO.File]::WriteAllText($sentinel, 'preserve')
try {
    [void] (New-Item -ItemType Junction -Path $junction -Target $outside)
    $reparseHandle = [InstallerDirectoryGuard]::Open($junction)
    if ($null -ne $reparseHandle) { $reparseHandle.Dispose(); throw 'Guard đã chấp nhận junction.' }
    $guard = [InstallerDirectoryGuard]::Open($inside)
    Assert-Definition ($null -ne $guard) 'Không mở được guard thư mục thường.'
    try {
        $writeHandle = [InstallerDirectoryGuard]::CreateFileW($inside, 2, 7, [IntPtr]::Zero, 3, 0x02200000, [IntPtr]::Zero)
        try { Assert-Definition ($writeHandle.IsInvalid) 'Directory guard không chặn thay đổi reparse tag qua write handle.' } finally { $writeHandle.Dispose() }
        $child = Join-Path $inside 'owned-test.txt'
        [IO.File]::WriteAllText($child, 'fixture')
        [IO.File]::Delete($child)
        Assert-Definition (-not (Test-Path -LiteralPath $child)) 'Directory guard chặn xóa child thường.'
        $renamed = $false
        try { [IO.Directory]::Move($inside, (Join-Path $fixture 'replaced')); $renamed = $true } catch [IO.IOException] { }
        Assert-Definition (-not $renamed) 'Directory guard không chặn rename/replacement.'
    } finally { $guard.Dispose() }
    Assert-Definition ([IO.File]::ReadAllText($sentinel) -ceq 'preserve') 'Sentinel ngoài root đã bị thay đổi.'
    Write-Host 'PASS: native no-delete-share guard, junction rejection, outside sentinel preserved.'
} finally {
    # Exact disposable fixture paths, checked to stay inside this repository.
    Assert-Definition ($fixture.StartsWith((Join-Path $repo 'artifacts\installer-safety-'), [StringComparison]::OrdinalIgnoreCase)) 'Fixture cleanup ngoài repository.'
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
if ($Compiler) {
    if (-not $RuntimeDirectory) { throw 'Compiler smoke cần RuntimeDirectory.' }
    [xml] $versionProps = [IO.File]::ReadAllText((Join-Path $repo 'Installer\Version.props'))
    $version = [string] $versionProps.Project.PropertyGroup.MosWordProductVersion
    Assert-Definition ($version -match '^\d+\.\d+\.\d+$') 'Phiên bản sản phẩm không hợp lệ.'
    $output = Join-Path $repo ('artifacts\installer-definition-' + [Guid]::NewGuid().ToString('N'))
    [void] (New-Item -ItemType Directory -Path $output)
    & $Compiler "/DRuntimeDirectory=$([IO.Path]::GetFullPath($RuntimeDirectory))" "/DReleaseDirectory=$output" "/DAppVersion=$version" $definition
    Assert-Definition ($LASTEXITCODE -eq 0) 'ISCC compile thất bại.'
    Assert-Definition (Test-Path -LiteralPath (Join-Path $output "MosWord2019-Setup-$version.exe") -PathType Leaf) 'Thiếu Setup output.'
    Write-Host "PASS: ISCC compile only; artifact $output"
}
