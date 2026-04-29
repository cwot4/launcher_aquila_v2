#define MyAppName      "Aquila"
#define MyAppPublisher "Aquila"
#define MyAppExeName   "Aquila.exe"
#define MyAppVersion   GetVersionNumbersString("..\dist\Aquila.exe")

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=..\dist
OutputBaseFilename=AquilaSetup
SetupIconFile=..\assets\icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
; Тихая установка без UAC — ставится в %LOCALAPPDATA%, не требует прав администратора
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
russian.NameAndVersion=%1

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные задачи:"; Flags: checkedonce

[Files]
Source: "..\dist\Aquila.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\configs\update.json"; DestDir: "{app}\configs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\configs\auth.json"; DestDir: "{app}\configs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\configs\ui.json"; DestDir: "{app}\configs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\configs\servers.json"; DestDir: "{app}\configs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\assets\*"; DestDir: "{app}\assets"; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Запись в реестр для Add/Remove Programs (без прав администратора — HKCU)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}"; \
  ValueType: string; ValueName: "DisplayName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}"; \
  ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#MyAppVersion}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}"; \
  ValueType: string; ValueName: "Publisher"; ValueData: "{#MyAppPublisher}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}"; \
  ValueType: string; ValueName: "UninstallString"; ValueData: "{uninstallexe}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}"; \
  ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}"; \
  ValueType: string; ValueName: "DisplayIcon"; ValueData: "{app}\{#MyAppExeName}"

[Run]
; Автозапуск через shellexec — Windows показывает SmartScreen вместо жёсткой блокировки SAC
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Parameters: "/FIRSTRUN"; Flags: nowait postinstall skipifsilent shellexec

[Code]
// При тихой установке (/SILENT или /VERYSILENT) — закрываем старый процесс перед заменой файла
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  SettingsPath: String;
  InstallFolder: String;
  Json: String;
begin
  if CurStep = ssInstall then
  begin
    // Завершаем запущенный лаунчер если он есть
    Exec('taskkill.exe', '/F /IM Aquila.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;

  if CurStep = ssPostInstall then
  begin
    SettingsPath := ExpandConstant('{app}\settings.json');
    InstallFolder := ExpandConstant('{app}');
    StringChangeEx(InstallFolder, '\', '\\', True);
    Json := '{"MemoryMb":4096,"LauncherName":"Aquila","InstallFolder":"' + InstallFolder + '","EnabledOptionalMods":{}}';
    SaveStringToFile(SettingsPath, Json, False);

    // Снимаем Zone.Identifier (Mark-of-the-Web) с exe чтобы SAC не блокировал запуск
    Exec('powershell.exe',
      '-NonInteractive -WindowStyle Hidden -Command "Unblock-File -Path ''' + ExpandConstant('{app}\{#MyAppExeName}') + '''"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
