#define MyAppName "Lampa Desktop"
#define MyAppVersion "1.0.3"
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
OutputBaseFilename=LampaSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
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

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Lampa Desktop"; Flags: nowait postinstall skipifsilent
