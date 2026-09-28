#define MyAppName "حافظة"
#define MyAppVersion "1.1.1"
#define MyAppExeName "Hafiza.exe"

[Setup]
AppId={{D02BB8C6-8493-4809-B332-1D095C88F4CF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\Hafiza
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
OutputDir=Release
OutputBaseFilename=Hafiza-Setup-{#MyAppVersion}-win-x64
SetupIconFile=Hafiza\Assets\hafiza.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "Release\Hafiza-Standalone\Hafiza.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
