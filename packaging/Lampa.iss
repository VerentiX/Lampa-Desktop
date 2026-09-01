#define MyAppName "Lampa Desktop"
#define MyAppVersion "1.0.9"
#define MyAppPublisher "Lampa"
#define MyAppExeName "Lampa.exe"

[Setup]
AppId={{8F3C1B2A-6D47-4E9F-A1C8-9B0E4D7F2A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}.0
UsePreviousAppDir=yes
DefaultDirName={autopf}\Lampa
DefaultGroupName=Lampa
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=LampaSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Lampa.Desktop\Assets\hottabych-genie-v2.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
MinVersion=10.0
SetupLogging=yes
DisableWelcomePage=no
InfoBeforeFile=info-before.txt

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительно:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Lampa Desktop"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Lampa Desktop"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Lampa"; Flags: uninsdeletevalue

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--shutdown-for-uninstall"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "GracefulShutdown"
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /T /F >nul 2>&1"; Flags: runhidden waituntilterminated; RunOnceId: "ForcedShutdownFallback"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Lampa Desktop"; Verb: "runas"; Flags: shellexec nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Lampa');
    if DirExists(DataDir) and
       (MsgBox('Удалить настройки, подписку, правила и кэш Lampa?'#13#10 +
               'Если вы планируете установить приложение снова, выберите «Нет».',
               mbConfirmation, MB_YESNO) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
