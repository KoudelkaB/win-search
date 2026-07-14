; File Search Manager installer.
; Build inputs: publish the two projects first (paths relative to this script):
;   dotnet publish ..\search\search.csproj                 -c Release -r win-x64 --self-contained true -o ..\publish\app
;   dotnet publish ..\search.service\search.service.csproj -c Release -r win-x64 --self-contained true -o ..\publish\service
; Then: ISCC.exe [/DMyAppVersion=1.2.3] setup.iss
; Without /DMyAppVersion the version is read from the published File Search Manager.exe
; (which MinVer stamped from the git tag - the single source of truth).

#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString("..\publish\app\File Search Manager.exe")
#endif
#define MyAppName "File Search Manager"
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
DefaultDirName={autopf}\File Search Manager
; Admin is required once for the Program Files install (and to register the optional
; service); the app itself then runs unelevated
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=..\search\app.ico
OutputDir=Output
OutputBaseFilename=FileSearchManager-Setup-{#MyAppVersion}
CloseApplications=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\File Search Manager.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "portuguesebrazil"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
; Simplified Chinese has no translation bundled with Inno Setup, so the installer wizard
; falls back to English for it. The app UI and help are still fully localized to zh-Hans.

[CustomMessages]
english.InstallService=Install the background service for prompt-free NTFS indexing (advanced; most users can skip this)
czech.InstallService=Nainstalovat službu na pozadí pro indexování NTFS bez dalších výzev (pokročilé; většina uživatelů ji nepotřebuje)
german.InstallService=Hintergrunddienst für NTFS-Indizierung ohne Rückfragen installieren (erweitert; meist nicht erforderlich)
french.InstallService=Installer le service d’arrière-plan pour indexer NTFS sans demande (avancé ; généralement inutile)
spanish.InstallService=Instalar el servicio en segundo plano para indexar NTFS sin avisos (avanzado; normalmente no es necesario)
polish.InstallService=Zainstaluj usługę indeksowania NTFS w tle bez monitów (zaawansowane; zwykle niepotrzebne)
italian.InstallService=Installa il servizio in background per l’indicizzazione NTFS senza richieste (avanzato; la maggior parte degli utenti può saltarlo)
portuguesebrazil.InstallService=Instalar o serviço em segundo plano para indexação NTFS sem solicitações (avançado; a maioria dos usuários pode ignorar)
japanese.InstallService=確認なしで NTFS をインデックス化するバックグラウンドサービスをインストールします（上級者向け。ほとんどのユーザーは不要です）
korean.InstallService=확인 없이 NTFS를 색인화하는 백그라운드 서비스를 설치합니다(고급, 대부분의 사용자는 건너뛰어도 됩니다)
english.HelpShortcut=File Search Manager Help
czech.HelpShortcut=Nápověda File Search Manager
german.HelpShortcut=File Search Manager Hilfe
french.HelpShortcut=Aide de File Search Manager
spanish.HelpShortcut=Ayuda de File Search Manager
polish.HelpShortcut=Pomoc File Search Manager
italian.HelpShortcut=Guida di File Search Manager
portuguesebrazil.HelpShortcut=Ajuda do File Search Manager
japanese.HelpShortcut=File Search Manager ヘルプ
korean.HelpShortcut=File Search Manager 도움말
english.ServiceFailed=The File Search Manager service could not be installed or started (code %1).%nFile Search Manager will still work: approve the startup prompt for instant indexing, or it will use a slower folder scan.
czech.ServiceFailed=Službu File Search Manager se nepodařilo nainstalovat nebo spustit (kód %1).%nAplikace bude nadále fungovat: potvrďte úvodní výzvu pro okamžité indexování, jinak použije pomalejší procházení složek.
german.ServiceFailed=Der File Search Manager-Dienst konnte nicht installiert oder gestartet werden (Code %1).%nDie Anwendung funktioniert weiterhin mit der Startabfrage oder der langsameren Ordnersuche.
french.ServiceFailed=Le service File Search Manager n’a pas pu être installé ou démarré (code %1).%nL’application fonctionnera avec la demande au démarrage ou l’analyse plus lente des dossiers.
spanish.ServiceFailed=No se pudo instalar o iniciar el servicio File Search Manager (código %1).%nLa aplicación seguirá funcionando con el aviso inicial o el análisis de carpetas más lento.
polish.ServiceFailed=Nie udało się zainstalować lub uruchomić usługi File Search Manager (kod %1).%nAplikacja nadal będzie działać z monitem startowym lub wolniejszym skanowaniem folderów.
italian.ServiceFailed=Impossibile installare o avviare il servizio File Search Manager (codice %1).%nL’applicazione funzionerà comunque: approva la richiesta all’avvio per l’indicizzazione immediata, altrimenti verrà usata una scansione delle cartelle più lenta.
portuguesebrazil.ServiceFailed=Não foi possível instalar ou iniciar o serviço File Search Manager (código %1).%nO aplicativo continuará funcionando: aprove a solicitação na inicialização para indexação instantânea ou será usada uma verificação de pastas mais lenta.
japanese.ServiceFailed=File Search Manager サービスをインストールまたは開始できませんでした（コード %1）。%nアプリは引き続き動作します。起動時の確認を承認すると即時インデックス化が行われ、承認しない場合は低速なフォルダースキャンが使用されます。
korean.ServiceFailed=File Search Manager 서비스를 설치하거나 시작할 수 없습니다(코드 %1).%n앱은 계속 작동합니다. 시작 시 표시되는 확인을 승인하면 즉시 색인화되고, 그렇지 않으면 느린 폴더 검색이 사용됩니다.

[Files]
; The two self-contained publishes MUST stay in separate directories -
; they contain same-named runtime DLLs with different content
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\publish\service\*"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"
Source: "..\README.md"; DestDir: "{app}"
Source: "..\docs\HELP*.md"; DestDir: "{app}\Docs"
Source: "..\docs\WINGET.md"; DestDir: "{app}\Docs"

[InstallDelete]
; Remove main-binary names left by releases before the File Search Manager rename.
Type: files; Name: "{app}\search.exe"
Type: files; Name: "{app}\search.dll"
Type: files; Name: "{app}\search.deps.json"
Type: files; Name: "{app}\search.runtimeconfig.json"
Type: files; Name: "{app}\search.pdb"
Type: files; Name: "{autoprograms}\Win Search.lnk"
Type: files; Name: "{autoprograms}\Win Search Help.lnk"

[Tasks]
; Off by default - most users never need it. The service lets File Search Manager index NTFS
; drives instantly without any admin prompt; without it the app still works (accept the
; startup admin prompt for instant indexing, or it falls back to a slower folder walk).
Name: "installservice"; Description: "{cm:InstallService}"; Flags: unchecked

[Icons]
Name: "{autoprograms}\File Search Manager"; Filename: "{app}\File Search Manager.exe"
Name: "{autoprograms}\{cm:HelpShortcut}"; Filename: "{app}\File Search Manager.exe"; Parameters: "--help"

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
      '" start= auto obj= LocalSystem DisplayName= "File Search Manager MFT Service"');
    if Code = 1073 then { ERROR_SERVICE_EXISTS - upgrade: repoint the binary }
      Code := Exec2(ExpandConstant('{sys}\sc.exe'),
        'config {#MyServiceName} binPath= "' + ExpandConstant('{#MyServiceExe}') + '" start= auto');
    if Code = 0 then
    begin
      Exec2(ExpandConstant('{sys}\sc.exe'),
        'description {#MyServiceName} "Provides read-only NTFS MFT data to File Search Manager so it can index drives without elevation."');
      Code := Exec2(ExpandConstant('{sys}\sc.exe'), 'start {#MyServiceName}');
    end;
    if Code <> 0 then
      { Never abort the install - the app still works via the admin prompt or the folder scan }
      MsgBox(FmtMessage(CustomMessage('ServiceFailed'), [IntToStr(Code)]), mbInformation, MB_OK);
  end;
end;
