; DriverWave installer (Inno Setup 6)
; Built by GitHub Actions: iscc /DMyAppVersion=1.0.N installer\DriverWave.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "DriverWave"
#define MyAppPublisher "SecondWave IT Systems"
#define MyAppURL "https://github.com/HackTheScript001/DriverWave"
#define MyAppExe "DriverWave.exe"
#define PublishDir "..\publish"

[Setup]
AppId={{6B1E3C2A-7D4F-4E8B-9A51-2F0C8D7E4B13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\output
OutputBaseFilename=DriverWave-{#MyAppVersion}-Setup
SetupIconFile=..\src\DriverWave\Assets\DriverWave.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
; Bundled network drivers for PCs that can't get online yet (folder may be empty)
Source: "..\OfflineDrivers\*"; DestDir: "{app}\OfflineDrivers"; Excludes: "README.md"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
; Normal install: offer to launch at the end
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; Silent in-app update: relaunch automatically
Filename: "{app}\{#MyAppExe}"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\OfflineDrivers"
