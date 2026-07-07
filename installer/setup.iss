; Win Search installer.
; Build inputs: publish the two projects first (paths relative to this script):
;   dotnet publish ..\search\search.csproj                 -c Release -r win-x64 --self-contained true -o ..\publish\app
;   dotnet publish ..\search.service\search.service.csproj -c Release -r win-x64 --self-contained true -o ..\publish\service
; Then: ISCC.exe [/DMyAppVersion=1.2.3] setup.iss
; Without /DMyAppVersion the version is read from the published search.exe
; (which MinVer stamped from the git tag - the single source of truth).

#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString("..\publish\app\search.exe")
#endif
#define MyAppName "Win Search"
#define MyPublisher "Bohdan Koudelka"
#define MyServiceName "WinSearchService"
#define MyServiceExe "{app}\service\search.service.exe"

[Setup]
; Never change the AppId - upgrades are matched by it
AppId={{D9AE5E34-602D-49AF-9263-89E7B851B8D4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
AppPublisherURL=https://github.com/KoudelkaB/win-search
AppSupportURL=https://github.com/KoudelkaB/win-search/issues
AppUpdatesURL=https://github.com/KoudelkaB/win-search/releases
DefaultDirName={autopf}\Win Search
; Admin is required once for the Program Files install (and to register the optional
; service); the app itself then runs unelevated
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=..\search\app.ico
OutputDir=Output
OutputBaseFilename=WinSearch-Setup-{#MyAppVersion}
CloseApplications=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\search.exe

[Files]
; The two self-contained publishes MUST stay in separate directories -
; they contain same-named runtime DLLs with different content
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\publish\service\*"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"
Source: "..\README.md"; DestDir: "{app}"
Source: "..\docs\HELP.md"; DestDir: "{app}\Docs"
Source: "..\docs\WINGET.md"; DestDir: "{app}\Docs"

[Tasks]
; Off by default - most users never need it. The service lets Win Search index NTFS
; drives instantly without any admin prompt; without it the app still works (accept the
; startup admin prompt for instant indexing, or it falls back to a slower folder walk).
Name: "installservice"; Description: "Install the background service for prompt-free NTFS indexing (advanced; most users can skip this)"; Flags: unchecked

[Icons]
Name: "{autoprograms}\Win Search"; Filename: "{app}\search.exe"
Name: "{autoprograms}\Win Search Help"; Filename: "{win}\notepad.exe"; Parameters: """{app}\Docs\HELP.md"""

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; RunOnceId: "SvcStop"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; RunOnceId: "SvcDelete"

[Code]
function Exec2(const FileName, Params: string): Integer;
var
  ResultCode: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
end;

function ServiceExists(): Boolean;
begin
  Result := Exec2(ExpandConstant('{sys}\sc.exe'), 'query {#MyServiceName}') = 0;
end;

{ Stop the service before file copy so the exe is not locked on upgrade }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Tries: Integer;
begin
  Result := '';
  if ServiceExists() then
  begin
    Exec2(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}'); { 1062 = not running - fine }
    for Tries := 1 to 30 do
    begin
      { "sc query" prints STOPPED only when it is; poll via interrogate exit codes instead:
        1062 = ERROR_SERVICE_NOT_ACTIVE => fully stopped }
      if Exec2(ExpandConstant('{sys}\sc.exe'), 'interrogate {#MyServiceName}') = 1062 then
        Break;
      Sleep(1000);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('installservice') then
  begin
    { sc.exe REQUIRES the space after each option= }
    Code := Exec2(ExpandConstant('{sys}\sc.exe'),
      'create {#MyServiceName} binPath= "' + ExpandConstant('{#MyServiceExe}') +
      '" start= auto obj= LocalSystem DisplayName= "Win Search MFT Service"');
    if Code = 1073 then { ERROR_SERVICE_EXISTS - upgrade: repoint the binary }
      Code := Exec2(ExpandConstant('{sys}\sc.exe'),
        'config {#MyServiceName} binPath= "' + ExpandConstant('{#MyServiceExe}') + '" start= auto');
    if Code = 0 then
    begin
      Exec2(ExpandConstant('{sys}\sc.exe'),
        'description {#MyServiceName} "Provides read-only NTFS MFT data to Win Search so it can index drives without elevation."');
      Code := Exec2(ExpandConstant('{sys}\sc.exe'), 'start {#MyServiceName}');
    end;
    if Code <> 0 then
      { Never abort the install - the app still works via the admin prompt or the folder scan }
      MsgBox('The Win Search service could not be installed or started (code ' + IntToStr(Code) + ').' + #13#10 +
             'Win Search will still work: accept the admin prompt at startup for instant indexing, ' +
             'or it falls back to a slower folder scan.', mbInformation, MB_OK);
  end;
end;
