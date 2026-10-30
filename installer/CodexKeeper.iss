#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\.publish\win-x64"
#endif
#ifndef ProjectRoot
  #define ProjectRoot ".."
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "Codex Keeper"
#define AppExeName "CodexKeeper.Windows.exe"

[Setup]
AppId={{A2422E96-31BC-4D7A-A187-441B99C6DA10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=OM-KEN
AppPublisherURL=https://github.com/zjqlyky/Codex-Keeper
AppSupportURL=https://github.com/zjqlyky/Codex-Keeper/issues
AppUpdatesURL=https://github.com/zjqlyky/Codex-Keeper/releases/latest
DefaultDirName={localappdata}\Programs\Codex Keeper
DefaultGroupName=Codex Keeper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763
AppMutex=CodexKeeper.Windows.SingleInstance
UsePreviousAppDir=yes
Uninstallable=yes
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile={#ProjectRoot}\LICENSE
SetupIconFile={#ProjectRoot}\windows\CodexKeeper.Windows\Resources\codex-keeper.ico
OutputDir={#OutputDir}
OutputBaseFilename=CodexKeeper-Setup-{#AppVersion}-win-x64
SetupLogging=yes
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
VersionInfoCompany=OM-KEN
VersionInfoDescription=Codex Keeper for Windows installer
VersionInfoProductName=Codex Keeper
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "{#ProjectRoot}\installer\Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Codex Keeper"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Codex Keeper"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Codex Keeper"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v CodexKeeper /f"; Flags: runhidden
