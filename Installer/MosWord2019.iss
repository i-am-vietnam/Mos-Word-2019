; Inno Setup 6. Compile only: never execute Setup as a build/test step.
; AppVersion is supplied from Installer/Version.props by build-installer.ps1.
#ifndef AppVersion
  #error AppVersion must be supplied from Installer/Version.props
#endif
#ifndef RuntimeDirectory
  #error RuntimeDirectory must contain the verified C7 eight-file runtime
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory must be an explicit output directory
#endif

[Setup]
AppId={{5A497F38-92D0-4C9B-BCF1-D3C8E28A42B0}
AppName=MOS Word 2019
AppVersion={#AppVersion}
AppVerName=MOS Word 2019 {#AppVersion}
DefaultDirName={localappdata}\Programs\MosWord2019
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=MOS Word 2019
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
AppMutex=Local\MosWord2019.Trainer
SetupMutex=Local\MosWord2019.Setup
SetupIconFile=branding\MosWord2019.ico
UninstallDisplayIcon={app}\MosWord2019.exe
OutputDir={#ReleaseDirectory}
OutputBaseFilename=MosWord2019-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
Uninstallable=yes

[Languages]
; Upstream Vietnamese translation, provenance/license in Languages/README.md.
Name: "vietnamese"; MessagesFile: "Languages\Vietnamese.isl"

[CustomMessages]
DesktopShortcut=Tạo lối tắt trên Desktop
LaunchApp=Chạy MOS Word 2019
MissingNet=Máy cần .NET Framework 4.7.2 trở lên. Hãy cài .NET Framework rồi chạy lại bộ cài.%n%nTải từ Microsoft: https://dotnet.microsoft.com/download/dotnet-framework/net472
MissingWord=Máy cần Microsoft Word desktop đã cài đặt (MSI hoặc Click-to-Run). Word trên web không được hỗ trợ. Hãy cài Word desktop rồi chạy lại bộ cài.
RemoveWarning=Gỡ MOS Word 2019 sẽ xóa dữ liệu cục bộ, kết quả CHƯA GỬI và dữ liệu khôi phục bài đang làm. Hãy mở ứng dụng, gửi kết quả và sao lưu dữ liệu cần giữ trước khi tiếp tục.%n%nBạn có chắc muốn gỡ và xóa dữ liệu này không?
CleanupSkipped=Một số dữ liệu được giữ lại vì đường dẫn không an toàn (junction/symlink/reparse point), tệp bị khóa hoặc không thể xóa. Hãy kiểm tra thủ công dữ liệu MOS Word 2019 sau khi gỡ.

[Messages]
WindowsVersionNotSupported=MOS Word 2019 yêu cầu Windows 10 hoặc Windows 11. Phiên bản Windows hiện tại không được hỗ trợ.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"

[Files]
; Exact C7 runtime allowlist: no wildcard, symbols, receipts or exercise packages.
Source: "{#RuntimeDirectory}\MosWord2019.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\MosWord2019.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\MosWord2019.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\MosWord2019.Data.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\MosWord2019.Projects.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\MosWord2019.Word.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\Newtonsoft.Json.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\BouncyCastle.Cryptography.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\MOS Word 2019"; Filename: "{app}\MosWord2019.exe"
Name: "{userdesktop}\MOS Word 2019"; Filename: "{app}\MosWord2019.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MosWord2019.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
type
  TDirectoryHandles = array of THandle;
  TAttributeTagInfo = record
    Attributes: LongWord;
    ReparseTag: LongWord;
  end;

function GetFileAttributesW(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenDirectory(Name: String; Access, Sharing, Security, Disposition,
  Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function QueryAttributeTag(Handle: THandle; InfoClass: Integer;
  var Info: TAttributeTagInfo; Size: LongWord): Boolean;
  external 'GetFileInformationByHandleEx@kernel32.dll stdcall';
function CloseDirectory(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

procedure UnlockDirectories(var Handles: TDirectoryHandles);
var I: Integer;
begin
  for I := GetArrayLength(Handles) - 1 downto 0 do CloseDirectory(Handles[I]);
  SetArrayLength(Handles, 0);
end;

function LockDirectories(Path: String; var Handles: TDirectoryHandles): Boolean;
var
  I, Count: Integer;
  Boundary: Boolean;
  Current: String;
  Handle: THandle;
  Info: TAttributeTagInfo;
begin
  Result := False;
  SetArrayLength(Handles, 0);
  { Acquire root-to-leaf. OPEN_REPARSE_POINT inspects the node itself; omitting
    FILE_SHARE_DELETE pins each ancestor against rename/replacement while walking.
    FILE_SHARE_WRITE is also omitted to block rewriting the node's reparse tag. }
  for I := 3 to Length(Path) do begin
    Boundary := (I = 3) or (I = Length(Path));
    if not Boundary then Boundary := Path[I + 1] = '\';
    if Boundary then begin
      Current := Copy(Path, 1, I);
      Handle := OpenDirectory(Current, 1, 1, 0, 3, $02200000, 0);
      if Handle = THandle(-1) then begin UnlockDirectories(Handles); exit; end;
      Count := GetArrayLength(Handles);
      SetArrayLength(Handles, Count + 1);
      Handles[Count] := Handle;
      if not QueryAttributeTag(Handle, 9, Info, 8) then begin UnlockDirectories(Handles); exit; end;
      if ((Info.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) or
        ((Info.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) then begin
        UnlockDirectories(Handles);
        exit;
      end;
    end;
  end;
  Result := True;
end;

function HasNetInView(Root: Integer): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(Root,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release);
  if Result then Result := Release >= 461808;
end;

function HasWordInView(Root: Integer): Boolean;
var
  Value: String;
begin
  { Machine registration supports MSI and Click-to-Run, without activating COM. }
  Result := RegQueryStringValue(Root,
    'SOFTWARE\Classes\CLSID\{000209FF-0000-0000-C000-000000000046}\LocalServer32', '', Value);
  if Result then Result := Trim(Value) <> '';
  if not Result then begin
    Result := RegQueryStringValue(Root,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WINWORD.EXE', '', Value);
    if Result then Result := Trim(Value) <> '';
  end;
end;

function InitializeSetup: Boolean;
var
  NetPresent, WordPresent: Boolean;
begin
  NetPresent := HasNetInView(HKLM32);
  WordPresent := HasWordInView(HKLM32);
  if IsWin64 then begin
    NetPresent := HasNetInView(HKLM64) or NetPresent;
    WordPresent := HasWordInView(HKLM64) or WordPresent;
  end;
  Result := False;
  if not NetPresent then begin
    SuppressibleMsgBox(CustomMessage('MissingNet'), mbError, MB_OK, IDOK);
    exit;
  end;
  if not WordPresent then begin
    SuppressibleMsgBox(CustomMessage('MissingWord'), mbError, MB_OK, IDOK);
    exit;
  end;
  Result := True;
end;

function SafeExistingPath(Path: String): Boolean;
var
  Current: String;
  Attributes: LongWord;
begin
  Result := False;
  { Refuse UNC/device/relative paths; redirected or unavailable folders are retained. }
  if (Length(Path) < 3) or (Path[2] <> ':') or (Path[3] <> '\') then exit;
  if CompareText(ExpandFileName(Path), Path) <> 0 then exit;
  Current := Path;
  while Length(Current) >= 3 do begin
    Attributes := GetFileAttributesW(Current);
    if (Attributes = $FFFFFFFF) or
      ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then exit;
    if Length(Current) = 3 then break;
    Current := RemoveBackslashUnlessRoot(ExtractFileDir(Current));
  end;
  Result := True;
end;

function DeleteSafeTree(Path, AllowedRoot: String): Boolean;
var
  Entry: TFindRec;
  Child: String;
  Attributes: LongWord;
  Handles: TDirectoryHandles;
  Last: Integer;
begin
  Result := False;
  { Compare exact root or a separator-delimited descendant, never a sibling prefix. }
  if (CompareText(Path, AllowedRoot) <> 0) and
    (CompareText(Copy(Path, 1, Length(AllowedRoot) + 1), AllowedRoot + '\') <> 0) then exit;
  if not SafeExistingPath(Path) then exit;
  Attributes := GetFileAttributesW(Path);
  if (Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then begin
    if not LockDirectories(ExtractFileDir(Path), Handles) then exit;
    try
      if SafeExistingPath(Path) then Result := DeleteFile(Path);
    finally
      UnlockDirectories(Handles);
    end;
    exit;
  end;
  if not LockDirectories(Path, Handles) then exit;
  Result := True;
  try
    if FindFirst(Path + '\*', Entry) then begin
      try
        repeat
          if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
            Child := Path + '\' + Entry.Name;
            { Never recurse into or remove even a reparse node itself. }
            if ((Entry.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then
              Result := False
            else if not DeleteSafeTree(Child, AllowedRoot) then Result := False;
          end;
        until not FindNext(Entry);
      finally
        FindClose(Entry);
      end;
    end;
    { Release only the target before RemoveDir; all ancestors remain pinned, even
      for the top-level root. RemoveDir never traverses its final directory node. }
    Last := GetArrayLength(Handles) - 1;
    CloseDirectory(Handles[Last]);
    SetArrayLength(Handles, Last);
    if Result and SafeExistingPath(Path) then Result := RemoveDir(Path)
    else Result := False;
  finally
    UnlockDirectories(Handles);
  end;
end;

function InitializeUninstall: Boolean;
begin
  { Silent uninstalls cannot consent to irreversible recovery/result removal. }
  Result := False;
  if UninstallSilent then exit;
  Result := MsgBox(CustomMessage('RemoveWarning'), mbConfirmation,
    MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LocalData, Working: String;
  Cleaned: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  { Setup/upgrades never invoke cleanup. Only these two exact user-data roots qualify. }
  LocalData := ExpandConstant('{localappdata}\MosWord2019');
  Working := ExpandConstant('{userdocs}\MosWord2019\Working');
  Cleaned := True;
  if GetFileAttributesW(LocalData) <> $FFFFFFFF then
    if not DeleteSafeTree(LocalData, LocalData) then Cleaned := False;
  if GetFileAttributesW(Working) <> $FFFFFFFF then
    if not DeleteSafeTree(Working, Working) then Cleaned := False;
  if not Cleaned then MsgBox(CustomMessage('CleanupSkipped'), mbInformation, MB_OK);
end;
