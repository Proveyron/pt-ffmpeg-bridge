; Inno Setup script for pt-ffmpeg-bridge. Build with build-installer.ps1 (it stages the files first).
;
; Installs the bridge and a bundled LGPL FFmpeg into Program Files, backs up Avid's
; QuickTimeServer\ProToolsQuickTimeServer.exe as ProToolsQuickTimeServer.orig.exe and puts the bridge
; in its place. Uninstalling (Apps & Features) puts Avid's original back.
;
; Command line: /PTDIR="D:\Avid\Pro Tools" selects a non-default Pro Tools folder; /VERYSILENT works.

#define AppName "pt-ffmpeg-bridge"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Stage
  #define Stage "..\build\stage"
#endif
#define RepoUrl "https://github.com/Proveyron/pt-ffmpeg-bridge"

[Setup]
AppId={{8C1F6E2A-5B7D-4E4B-9C2E-2F0D6B7A9E31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Proveyron
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\build\out
OutputBaseFilename={#AppName}-setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayName={#AppName} {#AppVersion} (FFmpeg import for Pro Tools)
UninstallDisplayIcon={app}\ProToolsQuickTimeServer.exe
CloseApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} setup

[Messages]
FinishedLabel=Pro Tools will now import FLAC, OGG, Opus, WavPack, ALAC, AAC and other formats through FFmpeg the next time it starts.%n%nQuickTime 7 is no longer needed; you can uninstall it from Apps & Features.

[Files]
Source: "{#Stage}\server\ProToolsQuickTimeServer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\PROTOCOL.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Registry]
Root: HKLM; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "ProToolsDir"; ValueData: "{code:GetProToolsDir}"; Flags: uninsdeletekey

[Code]
const
  ServerName = 'ProToolsQuickTimeServer.exe';
  BackupName = 'ProToolsQuickTimeServer.orig.exe';
  AvidMaxSize = 20 * 1024 * 1024; { Avid's helper is ~2 MB; ours is far larger }

var
  ProToolsPage: TInputDirWizardPage;
  ProToolsDir: String;

function HelperDir(Dir: String): String;
begin
  Result := AddBackslash(Dir) + 'QuickTimeServer\';
end;

function IsProToolsDir(Dir: String): Boolean;
begin
  Result := (Dir <> '') and FileExists(HelperDir(Dir) + ServerName);
end;

function IsProcessRunning(ExeName: String): Boolean;
var
  Locator, Service, Items: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name="' + ExeName + '"');
    Result := Items.Count > 0;
  except
    Log('Process check failed: ' + GetExceptionMessage);
  end;
end;

{ Pro Tools starts the helper once at launch, so it must be closed while the helper is swapped. }
function ProToolsClosed(Silent: Boolean): Boolean;
begin
  Result := True;
  while IsProcessRunning('ProTools.exe') do
  begin
    if Silent then
    begin
      Log('Pro Tools is running; aborting.');
      Result := False;
      exit;
    end;
    if MsgBox('Pro Tools is running.' + #13#10#13#10 + 'Save your work and quit Pro Tools, then click OK.', mbError, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  end;
end;

procedure StopHelper();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ServerName, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function DefaultProToolsDir(): String;
begin
  Result := ExpandConstant('{param:PTDIR|}');
  if Result = '' then
    if not RegQueryStringValue(HKLM, 'Software\{#AppName}', 'ProToolsDir', Result) then
      Result := ExpandConstant('{commonpf64}\Avid\Pro Tools');
end;

function GetProToolsDir(Param: String): String;
begin
  Result := ProToolsDir;
end;

function JsonPath(Path: String): String;
begin
  Result := Path;
  StringChangeEx(Result, '\', '\\', True);
end;

{ ---- install ---------------------------------------------------------------------------------- }

function InitializeSetup(): Boolean;
begin
  Result := ProToolsClosed(WizardSilent());
end;

procedure InitializeWizard();
begin
  ProToolsPage := CreateInputDirPage(wpSelectDir, 'Pro Tools location',
    'Which Pro Tools installation should use FFmpeg for imports?',
    'Select the Pro Tools folder (the one that contains the QuickTimeServer folder), then click Next.',
    False, '');
  ProToolsPage.Add('');
  ProToolsPage.Values[0] := DefaultProToolsDir();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ProToolsPage.ID) and not IsProToolsDir(ProToolsPage.Values[0]) then
  begin
    MsgBox('This folder does not contain QuickTimeServer\' + ServerName + '.' + #13#10#13#10 +
      'pt-ffmpeg-bridge needs a Pro Tools version that ships this QuickTime helper (Pro Tools 12.x for Windows).', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ProToolsDir := RemoveBackslashUnlessRoot(ProToolsPage.Values[0]);
  if not IsProToolsDir(ProToolsDir) then
    Result := 'Pro Tools was not found in "' + ProToolsDir + '" (no QuickTimeServer\' + ServerName + '). Use /PTDIR= to point setup at it.'
  else if not ProToolsClosed(WizardSilent()) then
    Result := 'Pro Tools is running. Quit it and run setup again.'
  else
    Result := '';
end;

procedure InstallIntoProTools();
var
  Dir, Config: String;
  Size: Integer;
begin
  Dir := HelperDir(ProToolsDir);
  StopHelper();

  { Keep Avid's original exactly once; never overwrite an existing backup with the bridge. }
  if not FileExists(Dir + BackupName) then
  begin
    if FileSize(Dir + ServerName, Size) and (Size < AvidMaxSize) and not FileExists(Dir + 'bridge.json') then
    begin
      if not FileCopy(Dir + ServerName, Dir + BackupName, True) then
        RaiseException('Could not back up Avid''s ' + ServerName + '. Nothing was changed.');
      Log('Backed up Avid helper to ' + Dir + BackupName);
    end
    else
      Log('No Avid original to back up (' + Dir + ServerName + ' is already a bridge build).');
  end;

  if not FileCopy(ExpandConstant('{app}\') + ServerName, Dir + ServerName, False) then
    RaiseException('Could not replace ' + Dir + ServerName + '. Is Pro Tools still running?');

  Config := '{' + #13#10 +
    '  "ffmpegPath": "' + JsonPath(ExpandConstant('{app}\ffmpeg\bin\ffmpeg.exe')) + '",' + #13#10 +
    '  "ffprobePath": "' + JsonPath(ExpandConstant('{app}\ffmpeg\bin\ffprobe.exe')) + '",' + #13#10 +
    '  "exactLengths": true,' + #13#10 +
    '  "debugLog": false' + #13#10 +
    '}' + #13#10;
  if not SaveStringToFile(Dir + 'bridge.json', Config, False) then
    RaiseException('Could not write ' + Dir + 'bridge.json');
  Log('Installed bridge into ' + Dir);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallIntoProTools();
end;

{ ---- uninstall -------------------------------------------------------------------------------- }

function InitializeUninstall(): Boolean;
begin
  Result := ProToolsClosed(UninstallSilent());
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep <> usUninstall then
    exit;
  if not RegQueryStringValue(HKLM, 'Software\{#AppName}', 'ProToolsDir', Dir) then
    exit;
  Dir := HelperDir(Dir);
  StopHelper();
  if FileExists(Dir + BackupName) then
  begin
    DeleteFile(Dir + ServerName);
    if RenameFile(Dir + BackupName, Dir + ServerName) then
      Log('Restored Avid''s original helper in ' + Dir)
    else if not UninstallSilent() then
      MsgBox('Could not restore ' + Dir + ServerName + ' from ' + BackupName + '. Rename it manually.', mbError, MB_OK);
  end
  else if not UninstallSilent() then
    MsgBox('No backup of Avid''s original helper was found in ' + Dir + '.' + #13#10 +
      'Reinstall Pro Tools to restore QuickTime-based imports.', mbInformation, MB_OK);
  DeleteFile(Dir + 'bridge.json');
end;
