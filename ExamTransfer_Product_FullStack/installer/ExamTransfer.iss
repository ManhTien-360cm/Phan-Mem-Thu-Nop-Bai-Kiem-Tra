#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif
#ifndef MyAppId
  #define MyAppId "{{724D43BD-E4C5-4927-A3CF-8AC292F03D21}"
#endif
#ifndef MyDefaultDirName
  #define MyDefaultDirName "{autopf}\ExamTransfer"
#endif
#ifndef MyClientShortcutName
  #define MyClientShortcutName "ExamTransfer"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\artifacts\installer"
#endif
#ifndef MyReleaseRoot
  #define MyReleaseRoot "..\artifacts\release"
#endif
#ifndef MyPrivilegesRequired
  #define MyPrivilegesRequired "admin"
#endif
#ifndef MyRuntimeSettingsRoot
  #define MyRuntimeSettingsRoot "{commonappdata}\ExamTransfer"
#endif
#ifndef MyCanonicalStorageRoot
  #define MyCanonicalStorageRoot "%ProgramData%/ExamTransfer"
#endif
#ifndef MyLegacyDiscoveryPortPrimary
  #define MyLegacyDiscoveryPortPrimary "5050"
#endif
#ifndef MyLegacyDiscoveryPortSecondary
  #define MyLegacyDiscoveryPortSecondary "5051"
#endif
#define MyAppName "ExamTransfer"
#define MyAppPublisher "ExamTransfer"
#define MyClientExe "ExamTransfer.Desktop.exe"
#define MyServerExe "ExamTransfer.LocalServer.exe"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={#MyDefaultDirName}
DefaultGroupName=ExamTransfer
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=ExamTransfer-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired={#MyPrivilegesRequired}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Client\{#MyClientExe}
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
UsePreviousPrivileges=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài màn hình"; Flags: unchecked

[Files]
Source: "{#MyReleaseRoot}\Client\*"; DestDir: "{app}\Client"; Excludes: "publiccloud.runtime.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyReleaseRoot}\Client\publiccloud.runtime.json"; DestDir: "{app}\Client"; Attribs: readonly; Flags: ignoreversion overwritereadonly uninsremovereadonly
Source: "{#MyReleaseRoot}\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyReleaseRoot}\release-manifest.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\installer-localserver-guard.ps1"; DestDir: "{app}\Support"; Flags: ignoreversion
Source: "..\scripts\installer-localserver-guard.ps1"; Flags: dontcopy

[InstallDelete]
#ifndef MyDisableLegacyCleanup
Type: files; Name: "{app}\install-role.ini"
Type: files; Name: "{autoprograms}\ExamTransfer Local Server.lnk"
#endif

[Icons]
Name: "{autoprograms}\{#MyClientShortcutName}"; Filename: "{app}\Client\{#MyClientExe}"
Name: "{autodesktop}\{#MyClientShortcutName}"; Filename: "{app}\Client\{#MyClientExe}"; Tasks: desktopicon

[Run]
#ifndef MyDisableFirewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer.LocalServer"" program=""{app}\Server\{#MyServerExe}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer TCP 5048"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ExamTransfer TCP 5048"" dir=in action=allow protocol=TCP localport=5048 profile=private,domain remoteip=LocalSubnet program=""{app}\Server\{#MyServerExe}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 5050"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 40550"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ExamTransfer UDP 40550"" dir=in action=allow protocol=UDP localport=40550 profile=private,domain remoteip=LocalSubnet program=""{app}\Server\{#MyServerExe}"""; Flags: runhidden
#endif
Filename: "{app}\Client\{#MyClientExe}"; Description: "Mở ExamTransfer"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanLaunchClient

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Support\installer-localserver-guard.ps1"" -Mode StopOnly -InstalledServerPath ""{app}\Server\{#MyServerExe}"""; Flags: runhidden waituntilterminated; RunOnceId: "StopExactExamTransferServer"
#ifndef MyDisableFirewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer TCP 5048"""; Flags: runhidden; RunOnceId: "RemoveExamTransferTcpRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 40550"""; Flags: runhidden; RunOnceId: "RemoveExamTransferUdpRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 5050"""; Flags: runhidden; RunOnceId: "RemoveLegacyExamTransferUdpRule"
#endif

[UninstallDelete]
Type: files; Name: "{app}\install-role.ini"
Type: dirifempty; Name: "{app}\Client"
Type: dirifempty; Name: "{app}"

[Code]
var
  InstallValidationExitCode: Integer;

function RunLocalServerGuard(Mode: String; ManifestPath: String; var ResultCode: Integer): Boolean;
var
  ScriptPath: String;
  Parameters: String;
begin
  ExtractTemporaryFile('installer-localserver-guard.ps1');
  ScriptPath := ExpandConstant('{tmp}\installer-localserver-guard.ps1');
  Parameters :=
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Mode ' + Mode +
    ' -InstalledServerPath "' + ExpandConstant('{app}\Server\{#MyServerExe}') + '"' +
    ' -DiagnosticLogPath "' + ExpandConstant('{#MyRuntimeSettingsRoot}\logs\installer-localserver-guard.log') + '"';
  if ManifestPath <> '' then
    Parameters := Parameters + ' -ManifestPath "' + ManifestPath + '"';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function RunRuntimeSettingsUpgrade(var ResultCode: Integer): Boolean;
var
  ScriptPath: String;
  Parameters: String;
begin
  ExtractTemporaryFile('installer-localserver-guard.ps1');
  ScriptPath := ExpandConstant('{tmp}\installer-localserver-guard.ps1');
  Parameters :=
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Mode UpgradeRuntimeSettings' +
    ' -InstalledServerPath "' + ExpandConstant('{app}\Server\{#MyServerExe}') + '"' +
    ' -RuntimeSettingsPath "' + ExpandConstant('{#MyRuntimeSettingsRoot}\config\runtime-settings.json') + '"' +
    ' -PublicConfigPath "' + ExpandConstant('{app}\Client\publiccloud.runtime.json') + '"' +
    ' -CanonicalStorageRoot "{#MyCanonicalStorageRoot}"' +
    ' -LegacyDiscoveryPorts "{#MyLegacyDiscoveryPortPrimary},{#MyLegacyDiscoveryPortSecondary}"' +
    ' -MigrationLogPath "' + ExpandConstant('{#MyRuntimeSettingsRoot}\logs\installer-runtime-settings.log') + '"';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters,
    ExpandConstant('{app}\Client'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not RunLocalServerGuard('StopOnly', '', ResultCode) then
  begin
    Result := 'Không thể dừng Local Server cũ trước khi cập nhật.';
    exit;
  end;
  if ResultCode <> 0 then
    Result := 'Không thể dừng đúng Local Server đã cài đặt hoặc kiểm tra cổng thất bại.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if (not RunRuntimeSettingsUpgrade(ResultCode)) or (ResultCode <> 0) then
    begin
      InstallValidationExitCode := 44;
      if not WizardSilent then
        MsgBox(
          'RUNTIME_SETTINGS_UPGRADE_FAILED: Xem installer-runtime-settings.log.',
          mbError,
          MB_OK);
      exit;
    end;

#ifndef MyDisableLegacyCleanup
    DeleteFile(ExpandConstant('{app}\install-role.ini'));
    DeleteFile(ExpandConstant('{userstartup}\ExamTransfer Local Server.lnk'));
#endif
  end;
end;

function CanLaunchClient: Boolean;
begin
  Result := InstallValidationExitCode = 0;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := InstallValidationExitCode;
end;

// Không thêm [UninstallDelete] cho C:\ProgramData\ExamTransfer hoặc thư mục dữ
// liệu runtime. Dữ liệu phải được giữ khi cập nhật/gỡ cài đặt.
