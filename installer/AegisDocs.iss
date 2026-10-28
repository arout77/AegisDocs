; Inno Setup script: wraps the published AegisDocs.exe into a normal Windows installer.
; The GitHub workflow (.github/workflows/build.yml) runs this after "dotnet publish" and passes
; the version number in with /DAppVersion=...  You can also compile it by hand with Inno Setup.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Aegis Docs"
#define AppExe  "AegisDocs.exe"

[Setup]
; AppId identifies this app to Windows so a newer installer upgrades the old one. Never change it.
AppId={{6F0B7C52-3D4E-4A8B-9C1F-5E2A7B8D4C19}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Aegis Labs
AppCopyright=Copyright (C) 2026 Aegis Labs. All rights reserved.
VersionInfoCompany=Aegis Labs
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=Copyright (C) 2026 Aegis Labs. All rights reserved.
; The license the person must accept before installing.
LicenseFile=..\LICENSE.txt
; Aegis Labs artwork on the wizard's welcome page (large) and page headers (small).
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
; The installer's own icon.
SetupIconFile=..\ReceiptCapture\Assets\AegisDocs.ico
; Installs just for the current user, so clients never see an administrator (UAC) prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-output
OutputBaseFilename=AegisDocs-Setup-{#AppVersion}
; The speech model is already compressed, so favour a quick build over squeezing it.
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
; Lets the installer replace the app while an old copy is still running.
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
; The whole published folder: the exe, the speech engine (runtimes) and the speech model (models).
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; The program's own updater runs this installer silently with /UPDATED=1: reopen the program when it finishes.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: StartedByUpdater

[Code]
function StartedByUpdater: Boolean;
begin
  Result := ExpandConstant('{param:UPDATED|0}') = '1';
end;
