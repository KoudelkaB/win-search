; File Search Manager installer.
; Build with tools\Build-Installer.ps1. It refreshes both publish directories and checks
; that the app and service versions match before invoking ISCC. Calling ISCC directly can
; accidentally package stale binaries left by an older build.
; Without /DMyAppVersion the version is read from the published File Search Manager.exe
; (which MinVer stamped from the git tag - the single source of truth).

#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString("..\publish\app\File Search Manager.exe")
#endif
#define MyAppName "File Search Manager"
#define MyPublisher "Bohdan Koudelka"
#define MyServiceName "WinSearchService"
#define MyServiceExe "{app}\service\search.service.exe"
; Where Inno records this install - "{AppId}_is1". Used to detect an existing installation
; and its version, which is what unlocks the "Modify" mode.
#define MyUninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{D9AE5E34-602D-49AF-9263-89E7B851B8D4}_is1"

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
; Keep a copy of this exact installer in {app} (see [Files]) so Windows can launch
; the existing maintenance wizard from its "Modify" button.
AppModifyPath="{app}\FileSearchManager-Setup.exe"
; Admin is required once for the Program Files install (and to register the optional
; service); the app itself then runs unelevated
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=..\search\app.ico
OutputDir=Output
OutputBaseFilename=FileSearchManager-Setup-{#MyAppVersion}
; "force", not "yes": with plain "yes", an app that does not answer RestartManager makes Setup
; ask Abort/Retry/Ignore - and under winget's /SILENT /SUPPRESSMSGBOXES that prompt defaults to
; Abort, so the install dies with exit code 5. "force" still asks politely first and only
; terminates what stays unresponsive.
CloseApplications=force
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
english.InstallService=Install the background service for prompt-free NTFS indexing (recommended; without it File Search Manager asks for administrator rights at every start)
czech.InstallService=Nainstalovat službu na pozadí pro indexování NTFS bez dalších výzev (doporučeno; jinak si File Search Manager při každém spuštění vyžádá práva správce)
german.InstallService=Hintergrunddienst für NTFS-Indizierung ohne Rückfragen installieren (empfohlen; andernfalls fragt File Search Manager bei jedem Start nach Administratorrechten)
french.InstallService=Installer le service d’arrière-plan pour indexer NTFS sans demande (recommandé ; sinon File Search Manager demande les droits administrateur à chaque démarrage)
spanish.InstallService=Instalar el servicio en segundo plano para indexar NTFS sin avisos (recomendado; de lo contrario, File Search Manager pedirá permisos de administrador en cada inicio)
polish.InstallService=Zainstaluj usługę indeksowania NTFS w tle bez monitów (zalecane; w przeciwnym razie File Search Manager przy każdym uruchomieniu poprosi o uprawnienia administratora)
italian.InstallService=Installa il servizio in background per l’indicizzazione NTFS senza richieste (consigliato; in caso contrario File Search Manager chiede i diritti di amministratore a ogni avvio)
portuguesebrazil.InstallService=Instalar o serviço em segundo plano para indexação NTFS sem solicitações (recomendado; caso contrário, o File Search Manager pedirá direitos de administrador a cada inicialização)
japanese.InstallService=確認なしで NTFS をインデックス化するバックグラウンドサービスをインストールします（推奨。インストールしない場合、File Search Manager は起動のたびに管理者権限を求めます）
korean.InstallService=확인 없이 NTFS를 색인화하는 백그라운드 서비스를 설치합니다(권장. 설치하지 않으면 File Search Manager가 시작할 때마다 관리자 권한을 요청합니다)
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
english.ServiceRemoveFailed=The File Search Manager service could not be removed (code %1).%nIt is still installed and running.
czech.ServiceRemoveFailed=Službu File Search Manager se nepodařilo odebrat (kód %1).%nZůstává nainstalovaná a spuštěná.
german.ServiceRemoveFailed=Der File Search Manager-Dienst konnte nicht entfernt werden (Code %1).%nEr bleibt installiert und aktiv.
french.ServiceRemoveFailed=Le service File Search Manager n’a pas pu être supprimé (code %1).%nIl reste installé et actif.
spanish.ServiceRemoveFailed=No se pudo quitar el servicio File Search Manager (código %1).%nSigue instalado y en ejecución.
polish.ServiceRemoveFailed=Nie udało się usunąć usługi File Search Manager (kod %1).%nPozostaje zainstalowana i uruchomiona.
italian.ServiceRemoveFailed=Impossibile rimuovere il servizio File Search Manager (codice %1).%nRimane installato e in esecuzione.
portuguesebrazil.ServiceRemoveFailed=Não foi possível remover o serviço do File Search Manager (código %1).%nEle continua instalado e em execução.
japanese.ServiceRemoveFailed=File Search Manager サービスを削除できませんでした（コード %1）。%nサービスはインストールされたまま実行されています。
korean.ServiceRemoveFailed=File Search Manager 서비스를 제거할 수 없습니다(코드 %1).%n서비스가 계속 설치되어 실행 중입니다.
english.MaintCaption=Existing installation
czech.MaintCaption=Existující instalace
german.MaintCaption=Vorhandene Installation
french.MaintCaption=Installation existante
spanish.MaintCaption=Instalación existente
polish.MaintCaption=Istniejąca instalacja
italian.MaintCaption=Installazione esistente
portuguesebrazil.MaintCaption=Instalação existente
japanese.MaintCaption=既存のインストール
korean.MaintCaption=기존 설치
english.MaintDescription=This version of File Search Manager is already installed on this computer.
czech.MaintDescription=Tato verze aplikace File Search Manager je již v tomto počítači nainstalována.
german.MaintDescription=Diese Version von File Search Manager ist auf diesem Computer bereits installiert.
french.MaintDescription=Cette version de File Search Manager est déjà installée sur cet ordinateur.
spanish.MaintDescription=Esta versión de File Search Manager ya está instalada en este equipo.
polish.MaintDescription=Ta wersja programu File Search Manager jest już zainstalowana na tym komputerze.
italian.MaintDescription=Questa versione di File Search Manager è già installata in questo computer.
portuguesebrazil.MaintDescription=Esta versão do File Search Manager já está instalada neste computador.
japanese.MaintDescription=このバージョンの File Search Manager は、このコンピューターに既にインストールされています。
korean.MaintDescription=이 버전의 File Search Manager가 이 컴퓨터에 이미 설치되어 있습니다.
english.MaintPrompt=Select what Setup should do, then click Next.
czech.MaintPrompt=Vyberte, co má instalátor udělat, a klikněte na Další.
german.MaintPrompt=Wählen Sie aus, was Setup tun soll, und klicken Sie auf Weiter.
french.MaintPrompt=Choisissez ce que doit faire le programme d’installation, puis cliquez sur Suivant.
spanish.MaintPrompt=Seleccione lo que debe hacer el instalador y haga clic en Siguiente.
polish.MaintPrompt=Wybierz, co ma zrobić instalator, a następnie kliknij Dalej.
italian.MaintPrompt=Seleziona che cosa deve fare il programma di installazione, quindi fai clic su Avanti.
portuguesebrazil.MaintPrompt=Selecione o que o instalador deve fazer e clique em Avançar.
japanese.MaintPrompt=セットアップの動作を選択して、[次へ] をクリックしてください。
korean.MaintPrompt=설치 프로그램이 수행할 작업을 선택한 다음 [다음]을 클릭하세요.
english.MaintModify=Modify settings - keep the installed files and only change the options on the next page (for example whether the background service is installed)
czech.MaintModify=Upravit nastavení - ponechat nainstalované soubory a změnit pouze volby na další stránce (například zda má být nainstalována služba na pozadí)
german.MaintModify=Einstellungen ändern - die installierten Dateien beibehalten und nur die Optionen auf der nächsten Seite ändern (zum Beispiel, ob der Hintergrunddienst installiert wird)
french.MaintModify=Modifier les paramètres - conserver les fichiers installés et ne changer que les options de la page suivante (par exemple si le service d’arrière-plan est installé)
spanish.MaintModify=Modificar la configuración: conservar los archivos instalados y cambiar solo las opciones de la página siguiente (por ejemplo, si se instala el servicio en segundo plano)
polish.MaintModify=Zmień ustawienia - zachowaj zainstalowane pliki i zmień tylko opcje na następnej stronie (na przykład to, czy usługa w tle ma być zainstalowana)
italian.MaintModify=Modifica le impostazioni - mantieni i file installati e cambia solo le opzioni della pagina successiva (ad esempio se installare il servizio in background)
portuguesebrazil.MaintModify=Modificar as configurações - manter os arquivos instalados e alterar apenas as opções da próxima página (por exemplo, se o serviço em segundo plano será instalado)
japanese.MaintModify=設定を変更する - インストール済みのファイルはそのままにして、次のページのオプションのみを変更します（バックグラウンドサービスをインストールするかどうかなど）
korean.MaintModify=설정 변경 - 설치된 파일은 그대로 두고 다음 페이지의 옵션만 변경합니다(예: 백그라운드 서비스 설치 여부)
english.MaintReinstall=Reinstall - replace all program files and apply the options on the next page
czech.MaintReinstall=Přeinstalovat - nahradit všechny programové soubory a použít volby na další stránce
german.MaintReinstall=Neu installieren - alle Programmdateien ersetzen und die Optionen auf der nächsten Seite anwenden
french.MaintReinstall=Réinstaller - remplacer tous les fichiers du programme et appliquer les options de la page suivante
spanish.MaintReinstall=Reinstalar: reemplazar todos los archivos del programa y aplicar las opciones de la página siguiente
polish.MaintReinstall=Zainstaluj ponownie - zastąp wszystkie pliki programu i zastosuj opcje z następnej strony
italian.MaintReinstall=Reinstalla - sostituisci tutti i file del programma e applica le opzioni della pagina successiva
portuguesebrazil.MaintReinstall=Reinstalar - substituir todos os arquivos do programa e aplicar as opções da próxima página
japanese.MaintReinstall=再インストールする - すべてのプログラムファイルを置き換えて、次のページのオプションを適用します
korean.MaintReinstall=다시 설치 - 모든 프로그램 파일을 교체하고 다음 페이지의 옵션을 적용합니다

[Files]
; Windows' Apps & features page can only offer Modify when the registered command points
; to an installer that remains available after the original download is gone. Do not try
; to copy the cached installer onto itself when it is the one running maintenance.
Source: "{srcexe}"; DestDir: "{app}"; DestName: "FileSearchManager-Setup.exe"; Flags: external ignoreversion; Check: InstallerNeedsCaching
; NotModifying keeps every file out of a settings-only "Modify" run: the files on disk are
; already this exact version (Modify is only offered then), so re-copying them would only cost
; time and force the app shut. Shortcuts below are still refreshed - that repairs a deleted one.
; The two self-contained publishes MUST stay in separate directories -
; they contain same-named runtime DLLs with different content
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Check: NotModifying
Source: "..\publish\service\*"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion; Check: NotModifying
Source: "..\LICENSE"; DestDir: "{app}"; Check: NotModifying
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Check: NotModifying
Source: "..\README.md"; DestDir: "{app}"; Check: NotModifying
Source: "..\docs\HELP*.md"; DestDir: "{app}\Docs"; Check: NotModifying
Source: "..\docs\WINGET.md"; DestDir: "{app}\Docs"; Check: NotModifying

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
; On by default: the service is what makes instant NTFS indexing work without an admin prompt
; at every start, so it belongs in the default experience rather than behind an opt-in. Without
; it the app still works - accept the startup admin prompt, or it falls back to a slower folder
; walk. Two things make the default safe to flip:
;   - Unchecking it here still works, so nobody is forced into a service.
;   - On an existing install the box is preset from whether the service actually exists (see
;     InitializeWizard), so an upgrade keeps what the machine already has. Existing installs
;     that declined the service do not silently acquire one, which matters because winget
;     upgrades run silently with no chance to intervene.
; This box is now the single switch for the service in both directions: checked installs it,
; unchecked removes an existing one. That is what makes the Modify mode work.
Name: "installservice"; Description: "{cm:InstallService}"

[Icons]
Name: "{autoprograms}\File Search Manager"; Filename: "{app}\File Search Manager.exe"
Name: "{autoprograms}\{cm:HelpShortcut}"; Filename: "{app}\File Search Manager.exe"; Parameters: "--help"

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; RunOnceId: "SvcStop"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; RunOnceId: "SvcDelete"

[Code]
var
  { Nil unless this run offers the maintenance choice - see InitializeWizard }
  MaintPage: TInputOptionWizardPage;

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

{ Setup is 32-bit while the install itself is 64-bit, so read the 64-bit view first and
  only fall back to the redirected one. }
function ReadUninstallValue(const ValueName: String; var Value: String): Boolean;
begin
  Result := False;
  if IsWin64 then
    Result := RegQueryStringValue(HKLM64, '{#MyUninstallKey}', ValueName, Value);
  if not Result then
    Result := RegQueryStringValue(HKLM, '{#MyUninstallKey}', ValueName, Value);
end;

function IsInstalled(): Boolean;
var
  Ignored: String;
begin
  Result := ReadUninstallValue('UninstallString', Ignored);
end;

{ Modify is offered only when the installed version is the one inside this setup. Then the
  files on disk are exactly what would be copied, so a settings-only run leaves a consistent
  installation and the version shown in Programs and Features stays true. When the versions
  differ this is an upgrade - and its Tasks page changes the service just as well. }
function SameVersionInstalled(): Boolean;
var
  Installed: String;
begin
  Result := ReadUninstallValue('DisplayVersion', Installed) and
            (CompareText(Trim(Installed), '{#MyAppVersion}') = 0);
end;

function IsModifying(): Boolean;
begin
  Result := Assigned(MaintPage) and (MaintPage.SelectedValueIndex = 0);
end;

{ The cached setup is itself the maintenance entry point, so a maintenance or reinstall run
  started by Windows must leave the currently executing file alone. }
function InstallerNeedsCaching(): Boolean;
begin
  Result := CompareText(ExpandConstant('{srcexe}'),
    ExpandConstant('{app}\FileSearchManager-Setup.exe')) <> 0;
end;

{ Check function for [Files] }
function NotModifying(): Boolean;
begin
  Result := not IsModifying();
end;

procedure InitializeWizard();
begin
  { Never created for a silent run (that is how winget upgrades come in): there is nobody to
    answer the page, and IsModifying must stay False so a silent run always copies the files. }
  if (not WizardSilent) and SameVersionInstalled() then
  begin
    MaintPage := CreateInputOptionPage(wpWelcome, CustomMessage('MaintCaption'),
      CustomMessage('MaintDescription'), CustomMessage('MaintPrompt'), True, False);
    MaintPage.Add(CustomMessage('MaintModify'));
    MaintPage.Add(CustomMessage('MaintReinstall'));
    MaintPage.SelectedValueIndex := 0;
  end;
  { Preset the service task from the machine rather than from UsePreviousTasks. The box now
    removes the service when unchecked, so it must start out matching reality - otherwise a
    stale "previous task" could delete a service the user still has, or offer to re-create one
    that is already there. }
  if ServiceExists() then
    WizardSelectTasks('installservice')
  else if IsInstalled() then
    WizardSelectTasks('!installservice');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { A settings-only run has nothing to ask about the license or the target folder }
  Result := IsModifying() and ((PageID = wpLicense) or (PageID = wpSelectDir));
end;

{ Stop the service before file copy so the exe is not locked on upgrade, and before deleting it
  so the delete takes effect at once instead of being deferred. A Modify run that keeps the
  service does neither, so it leaves the running service alone - no needless re-index. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Tries: Integer;
begin
  Result := '';
  if IsModifying() and WizardIsTaskSelected('installservice') then
    Exit;
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

procedure InstallService();
var
  Code: Integer;
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
    if Code = 1056 then { ERROR_SERVICE_ALREADY_RUNNING - nothing was stopped, so nothing to do }
      Code := 0;
  end;
  if Code <> 0 then
  begin
    { Never abort the install - the app still works via the admin prompt or the folder scan.
      Log it as well as showing it: now that the task is on by default, most installs are
      silent ones from winget, where /SUPPRESSMSGBOXES eats this message box and nobody would
      ever learn the service was not created. winget passes /LOG, so this line lands in the
      diagnostic log it prints on failure. }
    Log('Service setup failed with code ' + IntToStr(Code) + ' - continuing without the service.');
    MsgBox(FmtMessage(CustomMessage('ServiceFailed'), [IntToStr(Code)]), mbInformation, MB_OK);
  end;
end;

{ The task was unchecked while the service is on the machine - the user is taking it away.
  PrepareToInstall already stopped it. The service binaries stay in the app's service subfolder
  so a later run can just switch the service back on. }
procedure RemoveService();
var
  Code: Integer;
begin
  Code := Exec2(ExpandConstant('{sys}\sc.exe'), 'delete {#MyServiceName}');
  if Code = 1072 then { ERROR_SERVICE_MARKED_FOR_DELETE - already on its way out }
    Code := 0;
  if Code = 0 then
    Log('Service removed at the user''s request - File Search Manager will use the elevation prompt or the folder walk.')
  else
  begin
    Log('Service removal failed with code ' + IntToStr(Code) + ' - it is still installed.');
    MsgBox(FmtMessage(CustomMessage('ServiceRemoveFailed'), [IntToStr(Code)]), mbInformation, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  if WizardIsTaskSelected('installservice') then
    InstallService()
  else if ServiceExists() then
    RemoveService();
end;
