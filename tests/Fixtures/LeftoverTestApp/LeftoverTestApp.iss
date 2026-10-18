; "Leftover Test App": a harmless fake program for testing DeepUninstall.
;
; Its uninstaller is deliberately sloppy, like a lot of real software: it removes the files it
; installed but leaves behind everything marked "LEFTOVER" below. Install it, uninstall it with
; DeepUninstall, and check that every leftover was found and removed.
;
; Build:   ISCC.exe tests\Fixtures\LeftoverTestApp\LeftoverTestApp.iss
; Install: artifacts\fixtures\LeftoverTestApp-Setup.exe              (per user, no admin needed)
;          artifacts\fixtures\LeftoverTestApp-Setup.exe /ALLUSERS    (machine-wide: also adds a scheduled
;                                                                     task and HKLM entries; needs admin)

#define AppName "Leftover Test App"

[Setup]
AppId={{9F1C2B7E-5A4D-4E3B-8C6F-1A2B3C4D5E6F}
AppName={#AppName}
AppVersion=1.0.0
AppPublisher=DeepUninstall Tests
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\..\artifacts\fixtures
OutputBaseFilename=LeftoverTestApp-Setup
WizardStyle=modern

[Files]
; Installed and removed normally.
Source: "readme.txt"; DestDir: "{app}"; Flags: ignoreversion
; LEFTOVER: a log folder inside the install folder, so the install folder survives the uninstall.
Source: "readme.txt"; DestDir: "{app}\logs"; DestName: "app.log"; Flags: ignoreversion uninsneveruninstall
; LEFTOVER: user settings and cache.
Source: "readme.txt"; DestDir: "{userappdata}\{#AppName}"; DestName: "settings.ini"; Flags: ignoreversion uninsneveruninstall
Source: "readme.txt"; DestDir: "{localappdata}\{#AppName}\Cache"; DestName: "cache.dat"; Flags: ignoreversion uninsneveruninstall

[Icons]
; Removed normally.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\readme.txt"

[Registry]
; None of these have uninsdelete* flags, so they are all LEFTOVERS.
Root: HKCU; Subkey: "Software\{#AppName}"; ValueType: string; ValueName: "Theme"; ValueData: "dark"
Root: HKCU; Subkey: "Software\DeepUninstall Tests\{#AppName}"; ValueType: dword; ValueName: "LaunchCount"; ValueData: "42"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\LeftoverTestApp.exe"" --background"
Root: HKA; Subkey: "Software\Classes\LeftoverTestApp.Document"; ValueType: string; ValueData: "Leftover Test App document"
Root: HKA; Subkey: "Software\Classes\LeftoverTestApp.Document\shell\open\command"; ValueType: string; ValueData: """{app}\LeftoverTestApp.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.lefttest\OpenWithProgids"; ValueType: string; ValueName: "LeftoverTestApp.Document"; ValueData: ""
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\LeftoverTestApp.exe"; ValueType: string; ValueData: "{app}\LeftoverTestApp.exe"
Root: HKLM; Subkey: "Software\DeepUninstall Tests\{#AppName}"; ValueType: string; ValueName: "InstallMode"; ValueData: "machine"; Check: IsAdminInstallMode

[Run]
; LEFTOVER (machine-wide installs only): a scheduled task pointing into the install folder.
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /F /SC WEEKLY /TN ""{#AppName} Updater"" /TR ""'{app}\LeftoverTestApp.exe' --update"""; Flags: runhidden; Check: IsAdminInstallMode

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  { LEFTOVER: a desktop shortcut created by the program itself, which the uninstaller doesn't know about. }
  if CurStep = ssPostInstall then
    CreateShellLink(ExpandConstant('{autodesktop}\{#AppName}.lnk'), '{#AppName}',
      ExpandConstant('{app}\readme.txt'), '', ExpandConstant('{app}'), '', 0, SW_SHOWNORMAL);
end;
