#define AppName "DRAQ"
#define AppVersion "1.0.0"
#define AppPublisher "Ali Faiz"

[Setup]
AppId={{84C4A3D5-5AAF-4D8F-9C35-7C4090D8C719}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/aliXtuts
AppSupportURL=https://github.com/aliXtuts/DRAQ/issues
DefaultDirName={autopf}\DRAQ
DefaultGroupName=DRAQ
UninstallDisplayIcon={app}\DRAQ.exe
ArchitecturesInstallIn64BitMode=x64
OutputDir=Output
OutputBaseFilename=DRAQ-Setup
SetupIconFile=..\assets\draq.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\DRAQ\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DRAQ"; Filename: "{app}\DRAQ.exe"
Name: "{autodesktop}\DRAQ"; Filename: "{app}\DRAQ.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DRAQ.exe"; Description: "Launch DRAQ"; Flags: postinstall skipifsilent

