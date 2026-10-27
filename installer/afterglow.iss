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
; The task is for the account signed in to Windows, even when setup itself runs
; as another administrator whose password was typed into the UAC prompt (R-41).
; A standard account's task can only start Afterglow with that account's own
; rights, so Afterglow then asks for elevation as on any other launch.
Name: "startup"; Description: "Start Afterglow when you sign in to Windows (elevated via Task Scheduler, no UAC prompt on an administrator account)"; GroupDescription: "Startup:"; Flags: unchecked

[Registry]
; Pre-1.0.2 installs used a Run-key autostart (unelevated, prompted at every
; boot). Delete it unconditionally so upgrades never double-start.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Afterglow"; Flags: deletevalue

[Run]
; --register-startup is NOT a plain [Run] entry: Inno ignores a [Run] exit code,
; so a failed Task Scheduler registration (group policy, a security product, a
; refused location) would be reported to the user as a successful install while
; the ticked "Start with Windows" box silently did nothing (F-67). It now runs
; from [Code] below, which inspects the exit code and tells the user on failure.
Filename: "{app}\Afterglow.exe"; Description: "Launch Afterglow"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; R-46: every Windows user who turned "Start with Windows" on has their own
; "Afterglow-<SID>" task, so no fixed /TN can remove them all. [UninstallRun]
; runs as the uninstaller's first step, while Afterglow.exe still exists, and
; the exe enumerates every user's task (elevated, schtasks lists them all) and
; deletes them along with the legacy machine-global "Afterglow" task. The plain
; schtasks entry stays as the fallback for that legacy task if the exe can't run.
Filename: "{app}\Afterglow.exe"; Parameters: "--unregister-startup-all-users"; RunOnceId: "RemoveStartupTasks"; Flags: runhidden skipifdoesntexist
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Afterglow"" /F"; RunOnceId: "RemoveStartupTask"; Flags: runhidden

[UninstallDelete]
; The per-GPU crash-recovery records describe tuning on THIS install; after an
; uninstall they are stale, and a reinstall must not greet the user with a crash
; banner about them (F-66). Everything else (settings with fan curves and rules,
; profiles, logs, flight recordings) is the user's — see CurUninstallStepChanged.
Type: files; Name: "{commonappdata}\Afterglow\applied-state*.json"

[Code]
// F-67: register the elevated logon task from code so the exit code is checked.
// Setup already runs elevated (PrivilegesRequired=admin), so the child inherits
// that token. R-41: that token may be an administrator whose password a standard
// user typed into the UAC prompt, so the child registers the task for the user
// signed in to this Windows session, not for its own account, and refuses when
// nobody is signed in (a deployment tool running setup as SYSTEM). Nothing is
// passed from here: ExecAsOriginalUser can only return an exit code, and after
// "Run as administrator" Setup has no original-user credentials to return to.
procedure RegisterStartupTask;
var
  ResultCode: Integer;
begin
  if not WizardIsTaskSelected('startup') then
    exit;

  if Exec(ExpandConstant('{app}\Afterglow.exe'), '--register-startup', '',
          SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode <> 0 then
    begin
      Log(Format('register-startup failed with exit code %d', [ResultCode]));
      if not WizardSilent then
        MsgBox('Afterglow was installed, but turning on "Start Afterglow with Windows"'
          + ' did not work: the Task Scheduler task could not be created. The reason is in '
          + ExpandConstant('{commonappdata}\Afterglow\logs\afterglow.log')
          + '. Group policy or security software blocking scheduled-task creation is a'
          + ' common one.'
          + #13#10#13#10 + 'You can try again later from Settings; the rest of the install'
          + ' completed normally.', mbError, MB_OK);
    end;
  end
  else
  begin
    Log(Format('register-startup could not be launched (code %d)', [ResultCode]));
    if not WizardSilent then
      MsgBox('Afterglow was installed, but the startup-task helper could not be launched,'
        + ' so "Start Afterglow with Windows" was not enabled. You can turn it on later'
        + ' from Settings.', mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterStartupTask;
end;

// F-66: settings (including the startup profile that auto-applies at logon),
// profiles, logs and crash recordings outlive the program unless the user asks
// otherwise. Interactive uninstalls ask, defaulting to keep; silent ones keep.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    if MsgBox('Also remove your Afterglow settings, profiles, logs and crash recordings?'
      + #13#10#13#10 + 'Choose No to keep them for a later reinstall. Kept settings include any'
      + ' startup profile, which applies again at logon if you re-enable "Start with Windows".',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\Afterglow'), True, True, True);
  end;
end;
