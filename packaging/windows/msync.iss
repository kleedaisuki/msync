; Defines are supplied by packaging/windows/build.ps1; no machine-local paths.
#ifndef AppVersion
  #error AppVersion must be provided by the release build
#endif
#ifndef SourceDir
  #error SourceDir must point to the staged portable payload
#endif
#ifndef OutputDir
  #error OutputDir must point to the release artifact directory
#endif
#ifndef OutputBaseName
  #define OutputBaseName "msync-" + AppVersion + "-windows-x86_64-setup"
#endif

[Setup]
AppId={{157D15D7-7899-4F94-9D69-3CCF769886E8}
AppName=msync
AppVersion={#AppVersion}
AppPublisher=msync contributors
AppPublisherURL=https://github.com/kleedaisuki/msync
AppSupportURL=https://github.com/kleedaisuki/msync/issues
AppComments=A typed file synchronization and byte-flow DSL
DefaultDirName={%USERPROFILE}\.msync
DefaultGroupName=msync
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
UninstallFilesDir={app}\installer
UninstallDisplayIcon={app}\bin\msync.exe
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=yes
RestartApplications=no
LicenseFile={#SourceDir}\share\doc\msync\LICENSE
SetupLogging=yes

[Messages]
FinishedLabel=msync has been installed. Restart your entire terminal application to refresh PATH; a new tab may still inherit its existing host environment. Your configuration and data are not changed.

[Tasks]
Name: addpath; Description: "Add msync to my user PATH (recommended; new terminals only)"; GroupDescription: "Command-line access:"

[Files]
Source: "{#SourceDir}\bin\msync.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#SourceDir}\share\doc\msync\*"; DestDir: "{app}\share\doc\msync"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\INSTALL.md"; DestDir: "{app}\share\doc\msync"; Flags: ignoreversion

[Icons]
Name: "{group}\msync REPL"; Filename: "{app}\bin\msync.exe"; Parameters: "repl"; WorkingDir: "{%USERPROFILE}"; Comment: "Open the interactive synchronization shell"
Name: "{group}\msync README"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\share\doc\msync\README.md"""; WorkingDir: "{app}\share\doc\msync"
Name: "{group}\Installation and command-line help"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\share\doc\msync\INSTALL.md"""; WorkingDir: "{app}\share\doc\msync"
Name: "{group}\Uninstall msync"; Filename: "{uninstallexe}"

[Code]
const
  OwnerKey = 'Software\msync\Installer';
  EnvironmentKey = 'Environment';
  RegSz = 1;
  RegExpandSz = 2;
  WaitObject = 0;
  WaitAbandoned = $80;

function RegGetValueType(Key: HKEY; SubKey, ValueName: String; Flags: Cardinal;
  var ValueType: Cardinal; Data: NativeUInt; var DataSize: Cardinal): Integer;
  external 'RegGetValueW@advapi32.dll stdcall';
function CreatePathMutex(Attributes: NativeUInt; InitialOwner: Boolean;
  Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitPathMutex(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleasePathMutex(Handle: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function ClosePathHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function BroadcastEnvironment(Wnd: HWND; Msg: Cardinal; WParam: WPARAM;
  Value: String; Flags, Timeout: Cardinal; var Reply: DWORD_PTR): LRESULT;
  external 'SendMessageTimeoutW@user32.dll stdcall';

procedure NotifyEnvironment;
var
  Reply: DWORD_PTR;
begin
  BroadcastEnvironment($FFFF, $001A, 0, 'Environment', $0002, 5000, Reply);
end;

function LockPath: THandle;
var
  Status: Cardinal;
begin
  Result := CreatePathMutex(0, False, 'Local\msync-installer-user-path');
  if Result = 0 then
    RaiseException('Cannot create the user PATH update lock.');
  Status := WaitPathMutex(Result, 30000);
  if (Status <> WaitObject) and (Status <> WaitAbandoned) then begin
    ClosePathHandle(Result);
    RaiseException('Another msync installer is updating PATH. Retry after it finishes.');
  end;
end;

procedure UnlockPath(Handle: THandle);
begin
  ReleasePathMutex(Handle);
  ClosePathHandle(Handle);
end;

function NormalPath(Value: String): String;
begin
  Value := Trim(Value);
  if (Length(Value) >= 2) and (Value[1] = '"') and
    (Value[Length(Value)] = '"') then
    Value := Copy(Value, 2, Length(Value) - 2);
  StringChangeEx(Value, '/', '\', True);
  Result := Lowercase(RemoveBackslashUnlessRoot(Value));
end;

function ComponentEnd(const Value: String; Start: Integer): Integer;
var
  Quoted: Boolean;
begin
  Result := Start;
  Quoted := False;
  while Result <= Length(Value) do begin
    if Value[Result] = '"' then Quoted := not Quoted;
    if (Value[Result] = ';') and not Quoted then Exit;
    Result := Result + 1;
  end;
end;

function HasComponent(const Value, Entry: String): Boolean;
var
  Start, Finish: Integer;
begin
  Result := False;
  Start := 1;
  while Start <= Length(Value) + 1 do begin
    Finish := ComponentEnd(Value, Start);
    if NormalPath(Copy(Value, Start, Finish - Start)) = NormalPath(Entry) then begin
      Result := True;
      Exit;
    end;
    Start := Finish + 1;
  end;
end;

function RemoveOwnedComponent(const Value, Entry: String): String;
var
  Start, Finish, OwnedStart, OwnedEnd: Integer;
begin
  Result := Value;
  OwnedStart := 0;
  OwnedEnd := 0;
  Start := 1;
  while Start <= Length(Value) + 1 do begin
    Finish := ComponentEnd(Value, Start);
    // Ownership authorizes removing one exact spelling, not every equivalent
    // user-added alias. Keep unrelated components and separators byte-for-byte.
    if Copy(Value, Start, Finish - Start) = Entry then begin
      OwnedStart := Start;
      OwnedEnd := Finish;
    end;
    Start := Finish + 1;
  end;
  if OwnedStart = 0 then Exit;
  if OwnedStart > 1 then
    Delete(Result, OwnedStart - 1, OwnedEnd - OwnedStart + 1)
  else if OwnedEnd <= Length(Value) then
    Delete(Result, 1, OwnedEnd)
  else
    Result := '';
end;

procedure ReadPath(var Value: String; var ValueType: Cardinal; var Missing: Boolean);
var
  Size: Cardinal;
begin
  Value := '';
  Missing := not RegValueExists(HKCU, EnvironmentKey, 'Path');
  ValueType := RegExpandSz;
  if Missing then Exit;
  Size := 0;
  // Query the existing type without expanding %VARIABLE% tokens.
  if RegGetValueType(HKCU, EnvironmentKey, 'Path', $10000006,
    ValueType, 0, Size) <> 0 then
    RaiseException('Cannot read the user PATH type; PATH was not changed.');
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Value) then
    RaiseException('Cannot read the user PATH; PATH was not changed.');
end;

procedure WritePath(const Value: String; ValueType: Cardinal; Missing: Boolean);
var
  Written: Boolean;
begin
  if Missing then
    Written := not RegValueExists(HKCU, EnvironmentKey, 'Path') or
      RegDeleteValue(HKCU, EnvironmentKey, 'Path')
  else if ValueType = RegSz then
    Written := RegWriteStringValue(HKCU, EnvironmentKey, 'Path', Value)
  else
    Written := RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Value);
  if not Written then
    RaiseException('Cannot write the user PATH registry value.');
end;

procedure ClearOwner;
begin
  RegDeleteValue(HKCU, OwnerKey, 'PathOwnerRoot');
  RegDeleteValue(HKCU, OwnerKey, 'PathOwnerEntry');
  RegDeleteValue(HKCU, OwnerKey, 'PathOriginallyMissing');
  RegDeleteKeyIfEmpty(HKCU, OwnerKey);
end;

procedure UpdatePath(Install: Boolean; Selected: Boolean);
var
  Lock: THandle;
  Value, Original, BeforeRemoval, Current, Root, Entry, OwnerRoot, OwnerEntry: String;
  ValueType, CurrentType, OwnerMissing: Cardinal;
  Missing, OriginallyMissing, CurrentMissing, KeepOwner, Written: Boolean;
begin
  Lock := LockPath;
  try
    ReadPath(Value, ValueType, Missing);
    Original := Value;
    OriginallyMissing := Missing;
    Root := ExpandConstant('{app}');
    Entry := AddBackslash(Root) + 'bin';
    if Pos(';', Entry) > 0 then Entry := '"' + Entry + '"';
    OwnerRoot := '';
    OwnerEntry := '';
    OwnerMissing := 0;
    RegQueryStringValue(HKCU, OwnerKey, 'PathOwnerRoot', OwnerRoot);
    RegQueryStringValue(HKCU, OwnerKey, 'PathOwnerEntry', OwnerEntry);
    RegQueryDWordValue(HKCU, OwnerKey, 'PathOriginallyMissing', OwnerMissing);
    // An old uninstaller cannot remove the active installation's PATH entry.
    if not Install and (NormalPath(OwnerRoot) <> NormalPath(Root)) then Exit;
    KeepOwner := Install and Selected and
      (NormalPath(OwnerRoot) = NormalPath(Root)) and
      (OwnerEntry = Entry) and HasComponent(Value, Entry);
    if (OwnerEntry <> '') and not KeepOwner then begin
      BeforeRemoval := Value;
      Value := RemoveOwnedComponent(Value, OwnerEntry);
      if Value <> BeforeRemoval then
        Missing := (BeforeRemoval = OwnerEntry) and (OwnerMissing <> 0);
    end;
    if Install and Selected and not KeepOwner then begin
      if not HasComponent(Value, Entry) then begin
        OwnerMissing := Ord(Missing);
        if Missing then Value := Entry else Value := Value + ';' + Entry;
        Missing := False;
        KeepOwner := True;
      end;
    end;
    // The mutex serializes our installers. Refuse to overwrite a concurrent
    // external editor that changed the registry while this update was prepared.
    ReadPath(Current, CurrentType, CurrentMissing);
    if (Current <> Original) or (CurrentType <> ValueType) or
      (CurrentMissing <> OriginallyMissing) then
      RaiseException('User PATH changed concurrently. Retry the installer.');
    if (Value <> Original) or (Missing <> OriginallyMissing) then
      WritePath(Value, ValueType, Missing);
    if KeepOwner then begin
      Written := RegWriteStringValue(HKCU, OwnerKey, 'PathOwnerRoot', Root) and
        RegWriteStringValue(HKCU, OwnerKey, 'PathOwnerEntry', Entry) and
        RegWriteDWordValue(HKCU, OwnerKey, 'PathOriginallyMissing', OwnerMissing);
      if not Written then begin
        WritePath(Original, ValueType, OriginallyMissing);
        RaiseException('Cannot record PATH ownership; the PATH update was reverted.');
      end;
    end else ClearOwner;
  finally
    UnlockPath(Lock);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then UpdatePath(True, WizardIsTaskSelected('addpath'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then UpdatePath(False, False);
  if CurUninstallStep = usPostUninstall then NotifyEnvironment;
end;
