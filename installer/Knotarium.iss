; Inno Setup script for Knotarium — installs the published folder build as a Windows service.
;
; Built by publish.ps1 -Installer, which passes these defines:
;   AppVersion  release version (e.g. 1.0.0-rc.1)
;   SourceDir   the published app folder (publish/app)
;   OutputDir   where to drop the setup .exe
;   OutputBase  setup filename without extension (e.g. Knotarium-1.0.0-rc.1-setup)
;
; It can also be opened directly in the Inno Setup IDE for editing; the #ifndef fallbacks below
; let it compile standalone against a local ..\publish\app.
;
; Silent install (used by winget):  Knotarium-<ver>-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; Silent uninstall:                  "%ProgramFiles%\Knotarium\unins000.exe" /VERYSILENT

#define AppName "Knotarium"
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\publish"
#endif
#ifndef OutputBase
  #define OutputBase "Knotarium-setup"
#endif
#ifndef Port
  #define Port "43120"
#endif

#define ServiceName "Knotarium"
#define AppExe "Knotarium.Api.exe"
#define AppUrl "http://localhost:" + Port
#define AppPublisher "Andre Kaufmann"

[Setup]
; AppId must stay STABLE across versions so upgrades replace rather than install side-by-side.
AppId={{8F3A2C1E-5B4D-4E9A-9C7F-1D2E3F4A5B6C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
Compression=lzma2
SolidCompression=yes
; The app is a self-contained win-x64 build: only allow install on x64 Windows (blocks 32-bit-only
; Windows, which can't run it), and always install in 64-bit mode so it lands in the 64-bit
; Program Files — never the x86 folder.
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Installing a service and writing to Program Files needs elevation.
PrivilegesRequired=admin
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
; Internet shortcut that opens the running service in the default browser.
Name: "{group}\Open {#AppName}"; Filename: "{#AppUrl}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
; Offer to open the app after a non-silent install (the service is already started by [Code]).
Filename: "{#AppUrl}"; Description: "Open {#AppName} in your browser"; Flags: postinstall shellexec nowait skipifsilent

[Code]
const
  SC = 'sc.exe';

function RunSc(const Params: string): Integer;
begin
  Result := 0;
  Exec(ExpandConstant('{sys}\' + SC), Params, '', SW_HIDE, ewWaitUntilTerminated, Result);
end;

function ServiceExists(): Boolean;
var
  Rc: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\' + SC), 'query {#ServiceName}', '',
    SW_HIDE, ewWaitUntilTerminated, Rc) and (Rc = 0);
end;

procedure StopAndDeleteService();
begin
  if ServiceExists() then
  begin
    RunSc('stop {#ServiceName}');
    Sleep(2000);
    RunSc('delete {#ServiceName}');
    Sleep(1000);
  end;
end;

// Before copying files (e.g. on upgrade) the existing service must be stopped and removed,
// otherwise the running exe is locked and cannot be overwritten.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopAndDeleteService();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExePath, BinPath, Params: string;
begin
  if CurStep = ssPostInstall then
  begin
    ExePath := ExpandConstant('{app}\{#AppExe}');
    // sc requires the whole binPath (exe + args) as ONE quoted argument, with the inner quotes
    // around the exe backslash-escaped so a spaced install path (Program Files) survives.
    BinPath := '\"' + ExePath + '\" --urls {#AppUrl}';
    Params := 'create {#ServiceName} binPath= "' + BinPath + '" start= auto DisplayName= "{#AppName}"';
    RunSc(Params);
    RunSc('description {#ServiceName} "Knotarium workflow automation server."');
    RunSc('start {#ServiceName}');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Stop + remove the service before files are deleted. The %ProgramData%\Knotarium data
  // directory (DB + credential key) is intentionally left in place so a reinstall keeps state.
  if CurUninstallStep = usUninstall then
    StopAndDeleteService();
end;
