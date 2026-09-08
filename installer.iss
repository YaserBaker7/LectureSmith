; Inno Setup Script for LectureSmith
; Generates a modern Windows Installer and Uninstaller registered in Control Panel / Installed Apps

#define MyAppName "LectureSmith"
#define MyAppVersion "3.0.0"
#define MyAppPublisher "LectureSmith"
#define MyAppURL "https://github.com/YaserBaker7/LectureSmith"
#define MyAppExeName "LectureSmith.exe"

[Setup]
AppId={{E86A9117-F730-4C96-B44B-A480D9F6948B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=LectureSmith-Setup-v{#MyAppVersion}
SetupIconFile=Assets\app-icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Publish output directory (run `dotnet publish -c Release -r win-x64 --self-contained true` before compiling)
Source: "bin\Release\net9.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up local user configuration, logs, and temporary caches during uninstall
Type: filesandordirs; Name: "{userappdata}\LectureSmith"
Type: filesandordirs; Name: "{localappdata}\LectureSmith"
Type: filesandordirs; Name: "{localappdata}\Temp\LectureSmith"
