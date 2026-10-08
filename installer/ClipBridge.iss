#define AppName "ClipBridge"
#define AppVersion "1.0.0"
#define AppPublisher "ClipBridge"
#define AppExeName "ClipBridge.exe"
#define PublishDir "..\artifacts\ClipBridge"

[Setup]
AppId={{8A32940D-17B9-4D1B-9E87-09D44CC0BF4E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={code:GetPreviousInstallDir}
UsePreviousAppDir=yes
UsePreviousTasks=no
DisableDirPage=no
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputDir=..\artifacts\ClipBridge-Installers
OutputBaseFilename=ClipBridge-Setup
SetupIconFile=..\src\client\ClipBridge.App\Assets\Icons\clipbridge.ico
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
DisableProgramGroupPage=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: "autostart"; Description: "开机时自动启动"

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ClipBridge"; Filename: "{app}\{#AppExeName}"

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "--background"; Description: "安装完成后启动 ClipBridge（托盘后台运行）"; Flags: postinstall nowait skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ClipBridge"; ValueData: """{app}\{#AppExeName}"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Code]
var
  RemovePersonalData: Boolean;

function GetPreviousInstallDir(Param: String): String;
begin
  if RegQueryStringValue(HKCU, 'Software\ClipBridge\Installer', 'InstallDir', Result) and
     (Result <> '') then
    Exit;

  Result := ExpandConstant('{localappdata}\Programs\ClipBridge');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegWriteStringValue(HKCU, 'Software\ClipBridge\Installer', 'InstallDir', ExpandConstant('{app}'));
end;

function InitializeUninstall(): Boolean;
begin
  RemovePersonalData := MsgBox(
    '卸载 ClipBridge 时是否删除个人数据？' + #13#10 + #13#10 +
    '个人数据包括剪贴板历史、图片和配对设置。' + #13#10 +
    '选择“否”将保留这些数据（推荐）。',
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  UserDataDirectory: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if Exec(
      ExpandConstant('{sys}\taskkill.exe'),
      '/F /T /IM "ClipBridge.exe"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
      Log(Format('Stopped ClipBridge.exe before uninstall (exit code %d).', [ResultCode]));
  end;

  if (CurUninstallStep = usPostUninstall) and RemovePersonalData then
  begin
    UserDataDirectory := ExpandConstant('{localappdata}\ClipBridge');
    if DirExists(UserDataDirectory) and not DelTree(UserDataDirectory, True, True, True) then
      MsgBox(
        '程序已卸载，但部分个人数据未能删除。请检查目录：' + #13#10 + UserDataDirectory,
        mbError,
        MB_OK);
  end;
end;
