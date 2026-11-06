; ReyDesk Windows installer (Inno Setup 6.5+). Build with scripts/build-installer.ps1, which passes
;   /DAppVersion=<VERSION file> /DSourceDir=<dotnet publish folder>
#ifndef AppVersion
  #error Pass /DAppVersion=... (scripts/build-installer.ps1 reads the repo VERSION file)
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=... (the dotnet publish output folder)
#endif
#define AppName "ReyDesk"
#define AppExe "ReyDesk.exe"

[Setup]
AppId={{6F0B5C6E-3D2A-4C58-9B1E-7A4D2E8F10C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Reyden
VersionInfoVersion={#AppVersion}.0
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Admin install (Program Files) so the Windows Firewall rules below are created by setup and the
; "allow access" prompt never appears on first launch. App data stays per-user (%LOCALAPPDATA%\ReyDesk).
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=out
OutputBaseFilename=ReyDesk-Setup-{#AppVersion}
SetupIconFile=..\Windows\ReyDesk.Windows\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
; Dark ink wizard with the brand art (BGR colour: ink 080B11).
WizardStyle=windows11 dark
WizardBackColor=$110B08
WizardBackColorDynamicDark=$110B08
WizardImageFile=art\wizard.bmp,art\wizard@150.bmp,art\wizard@200.bmp
WizardImageFileDynamicDark=art\wizard.bmp,art\wizard@150.bmp,art\wizard@200.bmp
WizardSmallImageFile=art\small.bmp,art\small@150.bmp,art\small@200.bmp
WizardSmallImageFileDynamicDark=art\small.bmp,art\small@150.bmp,art\small@200.bmp

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fa"; MessagesFile: "Persian.isl"

[CustomMessages]
en.CreateDesktopIcon=Create a desktop shortcut
en.StartWithWindows=Start ReyDesk with Windows
en.LaunchProgram=Launch ReyDesk
en.RuntimeMissing=ReyDesk needs the Windows App Runtime 2.4 and it was not found on this PC. Install it after setup or ReyDesk will not open.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "startwithwindows"; Description: "{cm:StartWithWindows}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; {userprograms}/{userdesktop}: the app's own Start shortcut (toast identity, Platform/PhoneHubWindowsAlerts) lives in the user's Start menu and upgrades this one.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon

[Registry]
; Same mechanism as the app's Settings toggle (Settings/StartupService.cs): per-user Run key, value "ReyDesk".
; Admin setup runs as the elevated installing user, so HKCU is that user's hive.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ReyDesk"; ValueData: """{app}\{#AppExe}"""; Tasks: startwithwindows; Flags: uninsdeletevalue

[Run]
; Inbound allow rules for the installed exe (all profiles, any port, TCP and UDP): LAN pairing listener (TCP 47821),
; mDNS discovery and WebRTC/ICE (dynamic UDP ports). Program-based, so there is no port list to keep in sync.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ReyDesk TCP"" program=""{app}\{#AppExe}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ReyDesk UDP"" program=""{app}\{#AppExe}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ReyDesk TCP"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any protocol=TCP"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ReyDesk UDP"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any protocol=UDP"; Flags: runhidden
; runasoriginaluser: launch the app as the normal user, not elevated.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ReyDesk TCP"" program=""{app}\{#AppExe}"""; Flags: runhidden; RunOnceId: "FwTcp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ReyDesk UDP"" program=""{app}\{#AppExe}"""; Flags: runhidden; RunOnceId: "FwUdp"

[Code]
function RuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('microsoft.winappruntime.ddlm.2.4', Lowercase(Names[I])) = 1 then
      begin
        Result := True;
        Exit;
      end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardSilent and not RuntimeInstalled then
    MsgBox(ExpandConstant('{cm:RuntimeMissing}'), mbInformation, MB_OK);
end;

// The app's own "Start with Windows" toggle can write the Run value after install, so remove it on uninstall too.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ReyDesk');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ReyScreen');
  end;
end;
