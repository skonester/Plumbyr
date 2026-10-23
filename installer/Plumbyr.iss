; Inno Setup script for the Plumbyr installer.
; Wraps the single-file dist\win-x64\Plumbyr.exe produced by scripts\Publish.ps1.
; Build: ISCC.exe /DAppVersion=1.0.0 installer\Plumbyr.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Plumbyr"
#define AppExe  "Plumbyr.exe"
#define AppUrl  "https://github.com/skonester/Plumbyr"

[Setup]
; Keep AppId fixed forever: upgrades and uninstall find earlier installs by it.
AppId={{6F1D2B7C-4C1E-4E8A-9B3F-5A2D8C7E1F40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Plumbyr contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (C) 2026 Plumbyr contributors
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist\release
OutputBaseFilename=Plumbyr-{#AppVersion}-setup
SetupIconFile=..\plumbyr\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
; The app manifest requires elevation, so install machine-wide.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\win-x64\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; shellexec so Windows shows the UAC prompt the app's manifest asks for.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
; Payload Plumbyr.exe extracts at runtime. History and logs in the parent folder are kept.
Type: filesandordirs; Name: "{localappdata}\Plumbyr\runtime"
