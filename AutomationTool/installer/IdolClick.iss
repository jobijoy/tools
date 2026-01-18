; IdolClick Installer Script for Inno Setup 6.x
; =============================================
; Download Inno Setup from: https://jrsoftware.org/isdl.php
; 
; To build: 
;   Option 1: Open this file in Inno Setup Compiler and click Build > Compile
;   Option 2: Run: ISCC.exe IdolClick.iss
;   Option 3: Run: .\build-installer.bat

#define MyAppName "IdolClick"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Jobi Joy"
#define MyAppURL "https://github.com/jobijoy/tools"
#define MyAppExeName "IdolClick.exe"
#define MyAppDescription "Rule-based Windows UI Automation Tool"
#define MyAppCopyright "Copyright (C) 2024-2026 Jobi Joy"

[Setup]
; NOTE: AppId uniquely identifies this application. Do not change between versions.
AppId={{8E5F4A2B-9C3D-4E6F-A1B2-C3D4E5F6A7B8}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppCopyright={#MyAppCopyright}
AppComments={#MyAppDescription}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppDescription}
VersionInfoCopyright={#MyAppCopyright}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

; Installation directories - Program Files requires admin
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; Output settings
OutputDir=output
OutputBaseFilename=IdolClickSetup-{#MyAppVersion}
SetupIconFile=assets\idolclick.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4

; Appearance
WizardStyle=modern
WizardSizePercent=100
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp

; Privileges - admin for Program Files, allow user override
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline

; Minimum Windows version (Windows 10 1809+)
MinVersion=10.0.17763

; Restart behavior
CloseApplications=yes
RestartApplications=yes
CloseApplicationsFilter=*.exe

; License (uncomment if you have a license file)
; LicenseFile=..\LICENSE
; InfoBeforeFile=..\README.md

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startupicon"; Description: "Start {#MyAppName} when Windows starts"; GroupDescription: "Startup behavior:"; Flags: checkedonce
Name: "createhotkey"; Description: "Assign Ctrl+Alt+T hotkey to desktop shortcut"; GroupDescription: "Hotkey options:"; Flags: unchecked

[Files]
; Main executable (self-contained)
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; App icon for shortcuts
Source: "assets\idolclick.ico"; DestDir: "{app}"; Flags: ignoreversion

; XML documentation (optional)
Source: "..\publish\win-x64\IdolClick.xml"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Plugins folder
Source: "..\publish\win-x64\Plugins\*"; DestDir: "{app}\Plugins"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; Sample config (only if doesn't exist - preserve user config on upgrade)
Source: "assets\sample-config.json"; DestDir: "{app}"; DestName: "config.json"; Flags: onlyifdoesntexist skipifsourcedoesntexist

[Dirs]
; Create logs directory with user write permissions
Name: "{app}\logs"; Permissions: users-modify

[Icons]
; Start Menu shortcuts
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\idolclick.ico"; Comment: "{#MyAppDescription}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

; Desktop shortcut (optional)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\idolclick.ico"; Tasks: desktopicon; Comment: "{#MyAppDescription}"

[Registry]
; Start with Windows - uses Run registry key (more reliable than Startup folder)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: startupicon

; App registration for default programs
Root: HKCU; Subkey: "Software\{#MyAppName}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\{#MyAppName}"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey

[Run]
; Launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up logs on uninstall (optional - user may want to keep)
; Type: filesandordirs; Name: "{app}\logs"

[Code]
// ============================================================================
// CUSTOM PASCAL CODE
// ============================================================================

// Set hotkey on shortcut using Windows Script Host
procedure SetShortcutHotkey(ShortcutPath: String; Hotkey: String);
var
  WshShell: Variant;
  Shortcut: Variant;
begin
  try
    WshShell := CreateOleObject('WScript.Shell');
    Shortcut := WshShell.CreateShortcut(ShortcutPath);
    Shortcut.Hotkey := Hotkey;
    Shortcut.Save();
    Log('Set hotkey ' + Hotkey + ' on shortcut: ' + ShortcutPath);
  except
    Log('Failed to set hotkey on shortcut: ' + GetExceptionMessage());
  end;
end;

// Post-install: Set hotkey on shortcuts if task selected
procedure CurStepChanged(CurStep: TSetupStep);
var
  DesktopShortcut, StartMenuShortcut: String;
begin
  if CurStep = ssPostInstall then
  begin
    if IsTaskSelected('createhotkey') then
    begin
      DesktopShortcut := ExpandConstant('{autodesktop}\{#MyAppName}.lnk');
      StartMenuShortcut := ExpandConstant('{group}\{#MyAppName}.lnk');
      
      // Prefer desktop shortcut for hotkey, fall back to start menu
      if FileExists(DesktopShortcut) then
        SetShortcutHotkey(DesktopShortcut, 'Ctrl+Alt+T')
      else if FileExists(StartMenuShortcut) then
        SetShortcutHotkey(StartMenuShortcut, 'Ctrl+Alt+T');
    end;
  end;
end;

// Pre-uninstall: Close running instance
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Try to close IdolClick gracefully
    Exec('taskkill.exe', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Initialization
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
end;
