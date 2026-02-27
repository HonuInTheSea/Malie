#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyPublishDir
  #define MyPublishDir "..\..\publish\win-x64"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif

#ifndef MyAppIconFile
  #define MyAppIconFile "..\..\Assets\Icons\app-sun-3d.ico"
#endif

[Setup]
AppId={{6FD34B2B-6A5F-4F64-9282-78E5A65F85C9}
AppName=Mâlie
AppVersion={#MyAppVersion}
AppPublisher=Mâlie
UninstallDisplayName=Mâlie
DefaultDirName={autopf}\Malie
DefaultGroupName=Mâlie
OutputDir={#MyOutputDir}
OutputBaseFilename=Malie-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayIcon={app}\Malie.exe
SetupIconFile={#MyAppIconFile}
DisableProgramGroupPage=yes
CloseApplications=yes
CloseApplicationsFilter=Malie.exe
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autodesktop}\Mâlie"; Filename: "{app}\Malie.exe"; IconFilename: "{app}\Malie.exe"; Tasks: desktopicon
Name: "{autoprograms}\Mâlie"; Filename: "{app}\Malie.exe"; IconFilename: "{app}\Malie.exe"

[Run]
Filename: "{app}\Malie.exe"; Description: "Launch Mâlie"; Flags: nowait postinstall skipifsilent

[Code]
procedure TryDeleteTree(const APath: string);
begin
  if (APath = '') then
    exit;

  if DirExists(APath) then
    DelTree(APath, True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  MalieRoot: string;
  LegacyRoot: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    MalieRoot := ExpandConstant('{localappdata}\Malie');
    LegacyRoot := ExpandConstant('{localappdata}\IsometricLiveWeatherDesktop');
    TryDeleteTree(MalieRoot);
    TryDeleteTree(LegacyRoot);
  end;
end;
