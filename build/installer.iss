; Inno Setup 6 script for Amiga SD Creator.
; Built by build\publish.ps1, which passes AppVersion and SourceDir (the published app folder).
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\app"
#endif

[Setup]
AppId={{45961258-2BD6-41D3-8B0C-1F2769A6A024}
AppName=Amiga SD Creator
AppVersion={#AppVersion}
AppPublisher=Amiga SD Creator contributors
DefaultDirName={autopf}\Amiga SD Creator
DefaultGroupName=Amiga SD Creator
UninstallDisplayIcon={app}\AmigaSdCreator.exe
OutputBaseFilename=AmigaSdCreator-{#AppVersion}-setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Compression=lzma2
SolidCompression=yes
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Amiga SD Creator"; Filename: "{app}\AmigaSdCreator.exe"
Name: "{autodesktop}\Amiga SD Creator"; Filename: "{app}\AmigaSdCreator.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\AmigaSdCreator.exe"; Description: "Start Amiga SD Creator"; Flags: nowait postinstall skipifsilent
