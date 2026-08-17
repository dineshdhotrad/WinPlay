; SPDX-License-Identifier: GPL-3.0-or-later
; WinPlay installer (Inno Setup). Per-user install — no administrator rights, so Windows
; does not show a UAC prompt. Bundles the self-contained build (no .NET prerequisite).
;
; Build:  ISCC.exe /DSourceDir=<publish folder> /DAppVersion=0.2.0 [/DArch=arm64] winplay.iss

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
; Target architecture: "x64" (default) or "arm64". The published binaries are
; architecture-specific, so the installer must refuse to install the wrong ones.
#ifndef Arch
  #define Arch "x64"
#endif

; VersionInfoVersion becomes a Windows binary version resource, which is strictly numeric
; (a.b.c.d). AppVersion is a SemVer string and may legitimately carry a pre-release suffix —
; "0.3.0-beta1", or the "0.0.0-ci" that CI compiles with to prove the installer still builds.
; Passing that straight through made ISCC reject the whole script, so the installer job failed
; for a version string that was perfectly valid everywhere else. Strip the suffix for the
; resource; AppVersion keeps its full form for everything the user actually sees.
#if Pos("-", AppVersion) > 0
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumericVersion AppVersion
#endif

#define AppName "WinPlay"
#define AppPublisher "Dinesh Dhotrad"
#define AppUrl "https://github.com/dineshdhotrad/WinPlay"
#define AppExe "WinPlay.App.exe"

[Setup]
; Upgrade detection keys off AppId and nothing else, so this value is frozen forever at whatever
; the FIRST public release wrote into the registry — here, 0.1.0's. It is not a well-formed GUID
; (the trailing group is "WINPLAY000001"), and it was briefly "corrected" to a real one during
; 0.2.0 development. That correction was the bug: Inno accepts any stable string here, but a
; changed AppId is a DIFFERENT application, so 0.2.0 would have installed alongside 0.1.0 on every
; existing machine — two tray icons, two mDNS responders competing for the same service name, and
; an orphaned 0.1.0 no longer reachable from Add/Remove Programs. Cosmetic validity is worth
; nothing next to a clean upgrade for people who already installed 0.1.0.
AppId={{8F3B6A1C-9D2E-4C7A-B5E1-WINPLAY000001}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (C) 2026 {#AppPublisher}. GPL-3.0-or-later.
DefaultDirName={autopf}\WinPlay
DefaultGroupName=WinPlay
DisableProgramGroupPage=yes
DisableDirPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; The architecture belongs in the name. Without it both builds wrote the same file, so building
; x64 and arm64 in one working copy silently left only whichever finished last — invisible until
; someone shipped the wrong one. CI renames per matrix job and its glob still matches this.
OutputBaseFilename=WinPlay-{#AppVersion}-{#Arch}-Setup
OutputDir=dist
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\WinPlay.App\Assets\winplay.ico
LicenseFile=..\LICENSE
; Per-user install: no admin, no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
#if Arch == "arm64"
; ARM64 binaries require a genuine ARM64 host — x64 emulation cannot run them.
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; Let Setup detect and close a running WinPlay instead of failing on locked files, and
; restart it afterwards. WinPlay is a tray app users leave running, so upgrading over a
; live instance is the normal case, not the exception.
CloseApplications=yes
RestartApplications=yes

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

[UninstallDelete]
; Logs and recovery state are WinPlay's own working files — remove them so an uninstall
; leaves nothing behind. Pairing credentials (%APPDATA%\WinPlay) are deliberately NOT
; deleted here: reinstalling should not force the user to re-pair every Apple TV. The
; uninstaller offers to remove them explicitly below.
Type: filesandordirs; Name: "{localappdata}\WinPlay"

[Code]
// Offer to delete pairing credentials and pinned receiver identities on uninstall. Asked
// rather than assumed: keeping them makes reinstall seamless, deleting them is what a user
// handing the PC on would want.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\WinPlay');
    if DirExists(DataDir) then
    begin
      if MsgBox('Also remove WinPlay''s saved pairings for your Apple TVs and HomePods?' + #13#10 +
                'Choose No to keep them, so reinstalling does not require pairing again.',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
