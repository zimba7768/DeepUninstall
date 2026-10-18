; Inno Setup script for DeepUninstall.
; Build with installer\build.ps1, which publishes the binaries into artifacts\ first.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0"
#endif

#define AppName "DeepUninstall"
#define AppUrl "https://github.com/zimba7768/DeepUninstall"
#define Artifacts "..\artifacts"

[Setup]
AppId={{B6E5C7A1-3F2D-4C8B-9E1A-6D4F2B8C9A71}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DeepUninstall contributors
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#Artifacts}\installer
OutputBaseFilename=DeepUninstall-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
ChangesEnvironment=yes
UninstallDisplayIcon={app}\DeepUninstall.exe
SetupIconFile=..\src\DeepUninstall.App\Assets\DeepUninstall.ico
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add the deepuninstall command-line tool to PATH"; GroupDescription: "Command line:"; Flags: unchecked

[Files]
Source: "{#Artifacts}\app\DeepUninstall.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Artifacts}\cli\deepuninstall.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\DeepUninstall.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\DeepUninstall.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\DeepUninstall.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function NeedsAddPath(Dir: string): Boolean;
var
  Paths: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Paths) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;

{ Removes the install folder from the machine PATH when DeepUninstall is uninstalled. }
procedure RemoveFromPath(Dir: string);
var
  Paths: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Paths) then
    exit;
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';');
  if P = 0 then
    exit;
  if P > 1 then
    Delete(Paths, P - 1, Length(Dir) + 1)  { the entry and the ';' before it }
  else
    Delete(Paths, 1, Length(Dir) + 1);     { first entry: the entry and the ';' after it }
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Paths);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
