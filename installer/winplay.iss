; SPDX-License-Identifier: GPL-3.0-or-later
; WinPlay installer (Inno Setup). Per-user install — no administrator rights, so Windows
; does not show a UAC prompt. Bundles the self-contained build (no .NET prerequisite).
;
; Build:  ISCC.exe /DSourceDir=<publish folder> /DAppVersion=0.1.1 winplay.iss

#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

#define AppName "WinPlay"
#define AppPublisher "Dinesh Dhotrad"
#define AppUrl "https://github.com/dineshdhotrad/WinPlay"
#define AppExe "WinPlay.App.exe"

[Setup]
AppId={{8F3B6A1C-9D2E-4C7A-B5E1-WINPLAY000001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\WinPlay
DefaultGroupName=WinPlay
DisableProgramGroupPage=yes
DisableDirPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputBaseFilename=WinPlay-{#AppVersion}-Setup
OutputDir=dist
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\WinPlay.App\Assets\winplay.ico
; Per-user install: no admin, no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start WinPlay when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\WinPlay"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall WinPlay"; Filename: "{uninstallexe}"
Name: "{userdesktop}\WinPlay"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "WinPlay"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch WinPlay"; Flags: nowait postinstall skipifsilent
