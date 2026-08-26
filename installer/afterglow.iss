; Afterglow installer (Inno Setup 6)
; Build: ISCC.exe /DAppVersion=v1.0.0 installer\afterglow.iss
; Expects tools/publish.ps1 to have produced artifacts/portable first.

#ifndef AppVersion
  #define AppVersion "v0.0.0-dev"
#endif
#define CleanVersion Copy(AppVersion, 2, 99)

[Setup]
AppId={{7E6F2C1D-9A44-4C43-B58E-AFTERGLOW01}
AppName=Afterglow
AppVersion={#CleanVersion}
AppPublisher=Afterglow contributors
AppPublisherURL=https://github.com/minewefu/afterglow
DefaultDirName={autopf}\Afterglow
DefaultGroupName=Afterglow
OutputBaseFilename=Afterglow-{#CleanVersion}-setup
OutputDir=..\artifacts
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
SetupIconFile=..\src\Afterglow.App\Assets\afterglow.ico
UninstallDisplayIcon={app}\Afterglow.exe
WizardStyle=modern
LicenseFile=..\LICENSE

[Files]
Source: "..\artifacts\portable\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Afterglow"; Filename: "{app}\Afterglow.exe"
Name: "{autodesktop}\Afterglow"; Filename: "{app}\Afterglow.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start Afterglow with Windows (elevated via Task Scheduler, no UAC prompt at logon)"; GroupDescription: "Startup:"; Flags: unchecked

[Registry]
; Pre-1.0.2 installs used a Run-key autostart (unelevated, prompted at every
; boot). Delete it unconditionally so upgrades never double-start.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Afterglow"; Flags: deletevalue

[Run]
Filename: "{app}\Afterglow.exe"; Parameters: "--register-startup"; Tasks: startup; Flags: runhidden waituntilterminated
Filename: "{app}\Afterglow.exe"; Description: "Launch Afterglow"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Afterglow"" /F"; RunOnceId: "RemoveStartupTask"; Flags: runhidden
