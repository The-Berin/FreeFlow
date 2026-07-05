; FreeFlow installer — per-user (no admin prompt), Start Menu + optional desktop/autostart
[Setup]
AppName=FreeFlow
AppVersion=2.0.0
AppPublisher=Baron
AppPublisherURL=https://github.com/The-Berin/FreeFlow
DefaultDirName={autopf}\FreeFlow
DefaultGroupName=FreeFlow
UninstallDisplayIcon={app}\FreeFlow.exe
OutputDir=..\dist
OutputBaseFilename=FreeFlow-Setup-2.0.0
SetupIconFile=..\src\FreeFlow\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
CloseApplications=yes
PrivilegesRequired=lowest
WizardStyle=modern
DisableProgramGroupPage=yes

[Files]
Source: "..\publish\FreeFlow.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"
Name: autostart; Description: "Start FreeFlow when Windows starts"

[Icons]
Name: "{group}\FreeFlow"; Filename: "{app}\FreeFlow.exe"
Name: "{autodesktop}\FreeFlow"; Filename: "{app}\FreeFlow.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FreeFlow"; ValueData: """{app}\FreeFlow.exe"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\FreeFlow.exe"; Description: "Launch FreeFlow"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im FreeFlow.exe"; Flags: runhidden; RunOnceId: "KillFreeFlow"
