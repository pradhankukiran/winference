; Inno Setup script for Winference. Build it with scripts\Build-Windows.ps1 -Installer,
; which publishes the app and passes AppVersion, SourceDir and OutputDir.

#ifndef AppVersion
  #error AppVersion is not defined. Run scripts\Build-Windows.ps1 -Installer.
#endif
#ifndef SourceDir
  #error SourceDir is not defined. Run scripts\Build-Windows.ps1 -Installer.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{F53C05EE-EE0A-4F75-ABFB-5340C862F4AF}
AppName=Winference
AppVersion={#AppVersion}
AppVerName=Winference {#AppVersion}
AppPublisher=Winference contributors
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Winference
VersionInfoDescription=Winference Setup
; Per-user install: no admin prompt. {autopf} becomes %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Winference
; Fixed folder: [InstallDelete] clears it on upgrade, so it must never be a shared folder.
DisableDirPage=yes
DefaultGroupName=Winference
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11 22H2, matching SupportedOSPlatformVersion in the app project.
MinVersion=10.0.22621
; The app holds this mutex while open, so Setup asks the user to close it first.
AppMutex=Winference.Shell
CloseApplications=yes
SetupIconFile=..\src\Winference.App\Assets\Winference.ico
UninstallDisplayIcon={app}\Winference.exe
UninstallDisplayName=Winference
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=Winference-Setup-{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Remove files from the previous version so stale libraries are never loaded.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Winference"; Filename: "{app}\Winference.exe"
Name: "{autodesktop}\Winference"; Filename: "{app}\Winference.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Winference.exe"; Description: "{cm:LaunchProgram,Winference}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DataFolderName = 'Winference';

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\') + DataFolderName;
  if not DirExists(DataDir) then Exit;
  { Silent uninstalls keep the data; only a person can choose to delete it. }
  if UninstallSilent then Exit;
  if MsgBox('Also delete your Winference chats, settings and downloaded models?' + #13#10 + #13#10 +
            'This removes ' + DataDir + '.' + #13#10 +
            'Choose No to keep them for a later reinstall.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    if not DelTree(DataDir, True, True, True) then
      MsgBox('Some files in ' + DataDir + ' could not be deleted. You can remove that folder yourself.',
             mbError, MB_OK);
  end;
end;
