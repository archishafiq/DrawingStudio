; Drawing Studio for Revit - Windows installer (Inno Setup 6)
;
; Produces DrawingStudio-Setup-<version>.exe which:
;   * installs the app to C:\Program Files\Drawing Studio\App
;   * installs one bridge add-in per Revit version to ...\Bridge\R20xx
;   * registers the bridge with every selected Revit (C:\ProgramData\Autodesk\Revit\Addins\20xx)
;   * removes everything on uninstall
;
; Build:  iscc /DAppVersion=1.0.0 installer\DrawingStudio.iss
; Expects the compiled files in ..\build\App and ..\build\Bridge\R20xx (see build-installer.ps1).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Drawing Studio for Revit"
#define AddinFile "DrawingStudio.Bridge.addin"

[Setup]
AppId={{4E8B2D61-93A7-4C1F-B5E0-7A2C9D3F1B68}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Drawing Studio
DefaultDirName={autopf}\Drawing Studio
DefaultGroupName=Drawing Studio
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=DrawingStudio-Setup-{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\App\DrawingStudio.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Types]
Name: "full"; Description: "Every Revit version found on this computer"
Name: "custom"; Description: "Choose Revit versions"; Flags: iscustom

[Components]
Name: "app";   Description: "Drawing Studio app"; Types: full custom; Flags: fixed
Name: "r2022"; Description: "Bridge for Revit 2022"; Types: full; Check: RevitInstalled('2022')
Name: "r2023"; Description: "Bridge for Revit 2023"; Types: full; Check: RevitInstalled('2023')
Name: "r2024"; Description: "Bridge for Revit 2024"; Types: full; Check: RevitInstalled('2024')
Name: "r2025"; Description: "Bridge for Revit 2025"; Types: full; Check: RevitInstalled('2025')
Name: "r2026"; Description: "Bridge for Revit 2026"; Types: full; Check: RevitInstalled('2026')
Name: "r2027"; Description: "Bridge for Revit 2027"; Types: full; Check: RevitInstalled('2027')

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\build\App\*"; DestDir: "{app}\App"; Components: app; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\build\Bridge\R2022\*"; DestDir: "{app}\Bridge\R2022"; Components: r2022; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\build\Bridge\R2023\*"; DestDir: "{app}\Bridge\R2023"; Components: r2023; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\build\Bridge\R2024\*"; DestDir: "{app}\Bridge\R2024"; Components: r2024; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\build\Bridge\R2025\*"; DestDir: "{app}\Bridge\R2025"; Components: r2025; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\build\Bridge\R2026\*"; DestDir: "{app}\Bridge\R2026"; Components: r2026; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\build\Bridge\R2027\*"; DestDir: "{app}\Bridge\R2027"; Components: r2027; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Drawing Studio"; Filename: "{app}\App\DrawingStudio.exe"
Name: "{autodesktop}\Drawing Studio"; Filename: "{app}\App\DrawingStudio.exe"; Tasks: desktopicon

[UninstallDelete]
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2022\{#AddinFile}"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\{#AddinFile}"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\{#AddinFile}"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\{#AddinFile}"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\{#AddinFile}"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\{#AddinFile}"

[Run]
Filename: "{app}\App\DrawingStudio.exe"; Description: "Open Drawing Studio now"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function RevitInstalled(V: String): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\Autodesk\Revit ' + V + '\Revit.exe'));
end;

function IsRevitRunning(): Boolean;
var
  ResultCode: Integer;
begin
  { find.exe returns 0 when Revit.exe is in the task list }
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >NUL',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
var
  V: Integer;
  Any: Boolean;
begin
  Any := False;
  for V := 2022 to 2027 do
    if RevitInstalled(IntToStr(V)) then Any := True;
  if not Any then
    MsgBox('No Revit 2022-2027 installation was found in Program Files\Autodesk.' + #13#10 +
           'The app will be installed, but no Revit add-in can be registered.', mbInformation, MB_OK);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  while IsRevitRunning() do
  begin
    if MsgBox('Revit is running. Please save your work and close Revit, then click Retry.',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := 'Revit must be closed so the add-in can be installed.';
      Exit;
    end;
  end;
end;

function ManifestPath(Root, V: String): String;
begin
  Result := ExpandConstant(Root) + '\Autodesk\Revit\Addins\' + V + '\{#AddinFile}';
end;

procedure WriteManifest(V: String);
var
  Xml, Path: String;
begin
  Path := ManifestPath('{commonappdata}', V);
  if not FileExists(ExpandConstant('{app}') + '\Bridge\R' + V + '\DrawingStudio.Bridge.dll') then
  begin
    Log('Bridge for Revit ' + V + ' is not in this installer; not registered.');
    Exit;
  end;
  ForceDirectories(ExtractFileDir(Path));
  Xml :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>Drawing Studio Bridge</Name>' + #13#10 +
    '    <Assembly>' + ExpandConstant('{app}') + '\Bridge\R' + V + '\DrawingStudio.Bridge.dll</Assembly>' + #13#10 +
    '    <AddInId>6B1C4E0A-7D2F-4B8E-9C3A-2F5D8E1A4C77</AddInId>' + #13#10 +
    '    <FullClassName>DrawingStudio.Bridge.BridgeApp</FullClassName>' + #13#10 +
    '    <VendorId>DRAWINGSTUDIO</VendorId>' + #13#10 +
    '    <VendorDescription>Drawing Studio - sheets, views and annotation to ISO 19650</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>' + #13#10;
  if SaveStringToFile(Path, Xml, False) then
    Log('Registered bridge: ' + Path)
  else
    MsgBox('Could not write ' + Path, mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  V: Integer;
  S: String;
begin
  if CurStep <> ssPostInstall then Exit;
  for V := 2022 to 2027 do
  begin
    S := IntToStr(V);
    { A copy registered by the old install.ps1 would load the add-in twice. }
    DeleteFile(ManifestPath('{userappdata}', S));
    if WizardIsComponentSelected('r' + S) then
      WriteManifest(S)
    else
      DeleteFile(ManifestPath('{commonappdata}', S));
  end;
end;
