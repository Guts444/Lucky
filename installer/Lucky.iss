; Inno Setup script — builds Lucky-*-Setup.exe from the self-contained publish output.
; Invoked by scripts/publish-release.ps1 after the app is published.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName "Lucky"
#define MyAppPublisher "Guts444"
#define MyAppURL "https://github.com/Guts444/Lucky"
#define MyAppExeName "Lucky.exe"

[Setup]
AppId={{A7F3C2E1-9B84-4D6A-8E21-5C0F1A2B3D4E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=License.rtf
OutputDir={#OutputDir}
OutputBaseFilename=Lucky-{#MyAppVersion}-win-x64-Setup
SetupIconFile=AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
InfoBeforeFile=
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
