; Inno Setup script: wraps the published ReceiptCapture.exe into a normal Windows installer.
; The GitHub workflow (.github/workflows/build.yml) runs this after "dotnet publish" and passes
; the version number in with /DAppVersion=...  You can also compile it by hand with Inno Setup.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "Office Toolkit"
#define AppExe  "ReceiptCapture.exe"

[Setup]
; AppId identifies this app to Windows so a newer installer upgrades the old one. Never change it.
AppId={{6F0B7C52-3D4E-4A8B-9C1F-5E2A7B8D4C19}
AppName={#AppName}
AppVersion={#AppVersion}
; TODO: put your company / your name here (it shows in "Installed apps" and on the installer).
AppPublisher=Apex Studio
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
; Installs just for the current user, so clients never see an administrator (UAC) prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-output
OutputBaseFilename=OfficeToolkit-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Lets the installer replace the app while an old copy is still running.
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
