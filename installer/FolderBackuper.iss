; Folder Backuper installer.
;
; Compile through installer\Build-Installer.ps1, which publishes the application first and then
; passes the publish directory in. The version is read from the built executable so that setup.exe
; can never drift from the binary it carries.
;
; The identifiers below are asserted against WindowsServiceMetadata by
; tests\FolderBackuper.Tests\InstallerScriptConsistencyTests.cs. Change both together.

#ifndef PublishDir
  #error PublishDir must be defined, for example: ISCC /DPublishDir=..\artifacts\publish FolderBackuper.iss
#endif

#if VER < EncodeVer(6,3,0)
  #error Inno Setup 6.3 or newer is required for ArchitecturesAllowed=x64compatible
#endif

#define AppExeName "FolderBackuper.exe"
#define AppVersion GetVersionNumbersString(AddBackslash(PublishDir) + AppExeName)
#define AppName "Folder Backuper"
#define ServiceName "FolderBackuper"
#define EventLogSource "Folder Backuper"
#define RegistryKey "SOFTWARE\FolderBackuper"
#define PortValueName "Port"
#define DefaultPort "5180"
#define DataRoot "{commonappdata}\FolderBackuper"

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
; Never change AppId: it is how Windows recognizes an upgrade of this product.
AppId={{8E0B4B37-7F5C-4C2B-9E4C-2A7A2C6C9E11}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Folder Backuper
AppCopyright=Copyright (c) Folder Backuper contributors
DefaultDirName={autopf}\FolderBackuper
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UsePreviousAppDir=yes
; The script stops and starts the service itself; Restart Manager guessing is worse.
CloseApplications=no
RestartApplications=no
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=FolderBackuper-{#AppVersion}-setup
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
#ifdef SIGN
SignTool=fbsign
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
; Inno does not clean the application directory. A self-contained publish that drops an assembly
; between versions would otherwise leave an orphan behind that the runtime can still load. Every
; file under {app} is installer-owned; all state lives under ProgramData.
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\runtimes"
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Filename is a URL, so Inno creates an internet shortcut. Icons are rewritten on every run, which
; keeps the shortcut correct after an upgrade or a port change.
Name: "{group}\{#AppName}"; Filename: "http://localhost:{code:GetSelectedPort}"; IconFilename: "{app}\{#AppExeName}"; IconIndex: 0
Name: "{group}\{#AppName} log folder"; Filename: "{#DataRoot}\logs"

[Registry]
Root: HKLM; Subkey: "{#RegistryKey}"; Flags: uninsdeletekeyifempty
Root: HKLM; Subkey: "{#RegistryKey}"; ValueType: string; ValueName: "{#PortValueName}"; ValueData: "{code:GetSelectedPort}"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "{#RegistryKey}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletevalue
; Registering the event source keeps startup diagnostics readable in Event Viewer. Without
; EventMessageFile every entry renders as "The description for Event ID ... cannot be found".
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventLogSource}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventLogSource}"; ValueType: expandsz; ValueName: "EventMessageFile"; ValueData: "{sys}\EventCreate.exe"
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventLogSource}"; ValueType: dword; ValueName: "TypesSupported"; ValueData: "7"

[Run]
Filename: "http://localhost:{code:GetSelectedPort}"; Description: "Open the {#AppName} web interface"; Flags: postinstall shellexec skipifsilent nowait runasoriginaluser

[UninstallRun]
; Uninstall run entries execute before any file is removed, so the service is stopped and deleted
; while its binaries still exist. RunOnceId is mandatory in Inno Setup 6.
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopFolderBackuperService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteFolderBackuperService"

[UninstallDelete]
Type: dirifempty; Name: "{group}"

[Messages]
SetupAppTitle={#AppName} Setup
SetupWindowTitle={#AppName} Setup

[Code]
const
  ERROR_SERVICE_DOES_NOT_EXIST = 1060;
  ERROR_SERVICE_NOT_ACTIVE = 1062;
  ServiceStopTimeoutSeconds = 60;

var
  PortPage: TInputQueryWizardPage;
  SelectedPort: String;

function RunSystemTool(const Tool, Parameters: String; var ResultCode: Integer): Boolean;
begin
  // The system directory is the real System32 directory because setup installs in 64-bit mode.
  // Both net.exe and sc.exe talk to the same service control manager in either view.
  Result := Exec(ExpandConstant('{sys}\' + Tool), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function ServiceExists: Boolean;
var
  ResultCode: Integer;
begin
  Result := RunSystemTool('sc.exe', 'query {#ServiceName}', ResultCode) and (ResultCode <> ERROR_SERVICE_DOES_NOT_EXIST);
end;

function ReadInstalledPort: String;
begin
  if not RegQueryStringValue(HKLM64, '{#RegistryKey}', '{#PortValueName}', Result) then
    Result := '';
end;

function GetSelectedPort(Param: String): String;
begin
  Result := SelectedPort;
end;

procedure InitializeWizard;
var
  Existing: String;
begin
  SelectedPort := '{#DefaultPort}';
  PortPage := CreateInputQueryPage(wpSelectDir,
    'Web Interface Port',
    'Choose the loopback port for the {#AppName} web interface.',
    'The web interface is reachable only from this computer, on http://localhost. Re-running this installer is also how you change the port later if another program takes it.');
  PortPage.Add('Port:', False);

  Existing := ReadInstalledPort;
  if Existing <> '' then
    PortPage.Values[0] := Existing
  else
    PortPage.Values[0] := '{#DefaultPort}';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if (PortPage = nil) or (CurPageID <> PortPage.ID) then
    Exit;

  Port := StrToIntDef(Trim(PortPage.Values[0]), -1);
  if (Port < 1024) or (Port > 65535) then
  begin
    MsgBox('Enter a port number between 1024 and 65535.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  SelectedPort := IntToStr(Port);
end;

function StopServiceAndWait(var Message: String): Boolean;
var
  ResultCode: Integer;
  Elapsed: Integer;
begin
  Result := True;
  Message := '';
  if not ServiceExists then
    Exit;

  // net.exe stop blocks until the service has stopped, unlike sc.exe stop.
  RunSystemTool('net.exe', 'stop {#ServiceName}', ResultCode);

  Elapsed := 0;
  while Elapsed < ServiceStopTimeoutSeconds do
  begin
    if RunSystemTool('sc.exe', 'stop {#ServiceName}', ResultCode) and
       ((ResultCode = ERROR_SERVICE_NOT_ACTIVE) or (ResultCode = ERROR_SERVICE_DOES_NOT_EXIST)) then
      Exit;
    Sleep(1000);
    Elapsed := Elapsed + 1;
  end;

  Result := False;
  Message := 'The {#AppName} service could not be stopped within ' + IntToStr(ServiceStopTimeoutSeconds) +
    ' seconds. Stop it manually and run setup again.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if not StopServiceAndWait(Result) then
    Exit;
  Result := '';
end;

function DescribeMaintenanceFailure(ResultCode: Integer): String;
begin
  case ResultCode of
    10: Result := 'The maintenance command was called incorrectly.';
    11: Result := 'The application data directory could not be created.';
    12: Result := 'Access controls could not be applied to the application data directory.';
    13: Result := 'Port ' + SelectedPort + ' is already in use. Run setup again and choose a different port.';
    14: Result := 'No free loopback port was available.';
    15: Result := 'The hosting configuration file could not be written.';
    20: Result := 'The service did not become ready in time.';
    21: Result := 'The service stopped before it became ready.';
    22: Result := 'No loopback port is configured.';
  else
    Result := 'The maintenance command failed with code ' + IntToStr(ResultCode) + '.';
  end;
end;

procedure ReportStartupProblem(const Detail: String);
begin
  if WizardSilent then
    Exit;

  MsgBox('{#AppName} was installed, but the service did not start.' + #13#10#13#10 +
    Detail + #13#10#13#10 +
    'Diagnostics:' + #13#10 +
    '  ' + ExpandConstant('{#DataRoot}\logs') + #13#10 +
    '  Event Viewer, Windows Logs, Application, source "{#EventLogSource}"',
    mbError, MB_OK);
end;

procedure ConfigureService;
var
  ResultCode: Integer;
  ExePath: String;
  ServiceArguments: String;
begin
  ExePath := ExpandConstant('{app}\{#AppExeName}');

  if not Exec(ExePath, '--configure-port --port=' + SelectedPort, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ReportStartupProblem('The hosting configuration could not be written.');
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    ReportStartupProblem(DescribeMaintenanceFailure(ResultCode));
    Exit;
  end;

  // Delayed automatic start keeps the service out of the boot contention window so the network
  // and SMB stacks have settled before startup recovery probes backup destinations.
  ServiceArguments := '{#ServiceName} binPath= "\"' + ExePath + '\"" start= delayed-auto obj= LocalSystem DisplayName= "{#AppName}"';

  if ServiceExists then
    RunSystemTool('sc.exe', 'config ' + ServiceArguments, ResultCode)
  else
    RunSystemTool('sc.exe', 'create ' + ServiceArguments, ResultCode);

  if ResultCode <> 0 then
  begin
    ReportStartupProblem('The Windows service could not be registered (code ' + IntToStr(ResultCode) + ').');
    Exit;
  end;

  RunSystemTool('sc.exe', 'description {#ServiceName} "Creates scheduled ZIP backups of local folders to local or SMB storage and hosts the localhost web interface."', ResultCode);

  // Recovery actions apply when a running service terminates unexpectedly. They deliberately do
  // not apply to start failures, and failureflag is not set, so a deterministic startup failure
  // such as a failed migration cannot become a restart loop.
  RunSystemTool('sc.exe', 'failure {#ServiceName} reset= 86400 actions= restart/60000/restart/120000/restart/300000', ResultCode);

  if not RunSystemTool('sc.exe', 'start {#ServiceName}', ResultCode) or (ResultCode <> 0) then
  begin
    ReportStartupProblem('The Windows service could not be started (code ' + IntToStr(ResultCode) + ').');
    Exit;
  end;

  if not Exec(ExePath, '--wait-ready --timeout-seconds=90 --port=' + SelectedPort, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ReportStartupProblem('The readiness check could not be run.');
    Exit;
  end;

  if ResultCode <> 0 then
    ReportStartupProblem(DescribeMaintenanceFailure(ResultCode));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ConfigureService;
end;

function CommandLineHasFlag(const Flag: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), Flag) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDirectory: String;
  Remove: Boolean;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  DataDirectory := ExpandConstant('{#DataRoot}');
  if not DirExists(DataDirectory) then
    Exit;

  // The param constant is unreliable in the uninstaller, so the switch is read from the raw
  // command line instead.
  if UninstallSilent then
    Remove := CommandLineHasFlag('/REMOVEDATA=1')
  else
    Remove := MsgBox('Delete all {#AppName} application data?' + #13#10#13#10 +
      DataDirectory + #13#10#13#10 +
      'This permanently removes every job, destination, backup history record and log file.' + #13#10 +
      'Archives already written to a backup destination are not touched.' + #13#10#13#10 +
      'Choose No to keep the data for a future installation.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

  if Remove then
    DelTree(DataDirectory, True, True, True)
  else if not UninstallSilent then
    MsgBox('Application data was kept in:' + #13#10#13#10 + DataDirectory, mbInformation, MB_OK);
end;
