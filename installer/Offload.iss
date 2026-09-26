; Установщик Offload (Inno Setup 6/7). Сборка: .\scripts\build.ps1 -Installer
; Ставится в профиль пользователя — права администратора не нужны.
; На компьютере может быть только одна установка: установщик находит прежнюю (в том числе старую «для всех
; пользователей») и предлагает обновить, переустановить или удалить её.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "Offload"
#define AppExe "Offload.exe"
; Совпадает с AppInfo.InstallerAppId в программе (по нему она находит установленную копию). Не менять.
; Переопределение (/DAppGuid=...) — только для проверки диалогов установщика на тестовой записи.
#ifndef AppGuid
  #define AppGuid "6C1B7E2A-4F0D-4B8E-9C35-2B1F4E7A9D10"
#endif

[Setup]
; «{{» — экранированная «{», закрывающей «}» нет: так было с первого выпуска, и ключ удаления у всех установок —
; «{<GUID>_is1» (без «}»). Не «исправлять»: с другим AppId новая версия встанет второй программой, а не обновлением.
AppId={{{#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Kelll31
AppPublisherURL=https://github.com/Kelll31/offload
AppSupportURL=https://github.com/Kelll31/offload/issues
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Только «для себя» (HKCU): режим «для всех пользователей» давал бы вторую, независимую установку.
PrivilegesRequired=lowest
; Папку выбирают только при первой установке; обновление ставится туда же (см. ShouldSkipPage).
DisableDirPage=auto
UsePreviousAppDir=yes
OutputDir=..\dist
OutputBaseFilename=Offload-Setup-{#AppVersion}
SetupIconFile=..\src\Offload.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
; Подпись установщика и деинсталлятора — только при сборке с build.ps1 -Sign (он передаёт /Soffloadsign=… и /DSignInstaller).
#ifdef SignInstaller
SignTool=offloadsign
SignedUninstaller=yes
#endif
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Приложение создаёт этот мьютекс — установщик попросит закрыть Offload
AppMutex=Offload.Running
CloseApplications=yes

[Languages]
; Первый язык — запасной для систем, чей язык интерфейса не совпал ни с одним из списка.
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.TaskGroup=Additional options:
ru.TaskGroup=Дополнительно:
en.TaskAutostart=Start Offload when I sign in to Windows
ru.TaskAutostart=Запускать Offload при входе в Windows
en.TaskDesktopIcon=Create a desktop shortcut
ru.TaskDesktopIcon=Создать значок на рабочем столе
en.UninstallIcon=Uninstall %1
ru.UninstallIcon=Удалить %1
en.RunApp=Launch %1
ru.RunApp=Запустить %1
en.UninstallerMissing=The uninstaller of the installed version was not found:%n%1%n%nRemove Offload in Settings → Apps and run the setup again.
ru.UninstallerMissing=Не найден деинсталлятор установленной версии:%n%1%n%nУдалите Offload через «Параметры → Приложения» и запустите установку снова.
en.UninstallerFailed=Could not start the uninstaller: %1
ru.UninstallerFailed=Не удалось запустить деинсталлятор: %1
en.OlderNote=%n%nNote: a newer version (%1) is installed than the one in this setup (%2).
ru.OlderNote=%n%nВнимание: установлена более новая версия (%1), чем в этом установщике (%2).
en.PerMachineTitle=Offload is installed for all users
ru.PerMachineTitle=Offload уже установлен для всех пользователей
en.PerMachineText=Version %1 is installed in %2.%n%nThis version installs for the current user only, so an "all users" installation cannot be upgraded — you would end up with two copies of Offload. Remove the installed version (administrator rights are required); downloaded models and settings can be kept.
ru.PerMachineText=Установлена версия %1 в папке %2.%n%nЭта версия ставится только для текущего пользователя, поэтому обновить установку «для всех» нельзя — получилось бы две копии Offload. Удалите установленную версию (потребуются права администратора); скачанные модели и настройки можно сохранить.
en.PerMachineButton=Remove it and install Offload %1
ru.PerMachineButton=Удалить её и установить Offload %1
en.PerMachineNotRemoved=The installed version was not removed — setup is cancelled to avoid two copies of Offload.
ru.PerMachineNotRemoved=Установленная версия не удалена — установка отменена, чтобы не было двух копий Offload.
en.PerMachineSilent=Offload is installed for all users (%1). Remove it before a silent per-user installation.
ru.PerMachineSilent=Offload установлен для всех пользователей (%1). Удалите его перед тихой установкой для текущего пользователя.
en.UpgradeTitle=Upgrade Offload to version %1?
ru.UpgradeTitle=Обновить Offload до версии %1?
en.UpgradeText=Version %1 is installed in %2.%n%nThe new version will be installed into the same folder. Settings, downloaded models and IDE connections are kept.
ru.UpgradeText=Установлена версия %1 в папке %2.%n%nНовая версия будет установлена в ту же папку. Настройки, скачанные модели и подключения к IDE сохранятся.
en.UpgradeButton=Upgrade to %1
ru.UpgradeButton=Обновить до %1
en.SameTitle=Offload %1 is already installed
ru.SameTitle=Offload %1 уже установлен
en.SameText=Installed in %1.%n%nYou can reinstall (restore program files and change options: autostart, desktop shortcut) or uninstall Offload.
ru.SameText=Установлен в папке %1.%n%nМожно переустановить (восстановить файлы программы и изменить параметры: автозапуск, значок на рабочем столе) или удалить Offload.
en.SameButton=Reinstall or change options
ru.SameButton=Переустановить или изменить параметры
en.NewerTitle=A newer version of Offload is installed
ru.NewerTitle=Установлена более новая версия Offload
en.NewerText=Version %1 is installed in %2, and this setup contains an older version %3.%n%nThe older version may not read the newer settings.
ru.NewerText=Установлена версия %1 в папке %2, а этот установщик — более старой версии %3.%n%nСтарая версия может не прочитать настройки новой.
en.NewerButton=Install version %1 over it
ru.NewerButton=Установить версию %1 поверх
en.RemoveButton=Uninstall Offload
ru.RemoveButton=Удалить Offload
en.Removed=Offload has been uninstalled.
ru.Removed=Offload удалён.
en.NotRemoved=Offload was not uninstalled.
ru.NotRemoved=Offload не удалён.
en.DeleteData=Also delete downloaded models, llama.cpp, OpenCode and Offload settings?%n(%1)%n%nModels take a lot of space. Choose "No" to keep them for a later reinstall.
ru.DeleteData=Удалить также скачанные модели, llama.cpp, OpenCode и настройки Offload?%n(%1)%n%nМодели занимают много места. Выберите «Нет», если хотите сохранить их для повторной установки.

[Tasks]
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:TaskGroup}"
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:TaskGroup}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallIcon,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunApp,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Отключение MCP-сервера из всех IDE и остановка llama-server
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "OffloadCleanup"

[Code]
const
  // Ключ, который Inno Setup создаёт для AppId из [Setup]: «{<GUID>_is1», без закрывающей скобки.
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + '{' + '{#AppGuid}_is1';

var
  ExistingFound: Boolean;
  ExistingPerMachine: Boolean;
  ExistingRoot: Integer;
  ExistingDir: String;
  ExistingVersion: String;
  ExistingUninstall: String;

{ ---------- Поиск установленной копии ---------- }

function ReadExisting(Root: Integer; PerMachine: Boolean): Boolean;
begin
  Result := RegQueryStringValue(Root, UninstallKey, 'UninstallString', ExistingUninstall);
  if Result then
  begin
    ExistingRoot := Root;
    ExistingPerMachine := PerMachine;
    if not RegQueryStringValue(Root, UninstallKey, 'Inno Setup: App Path', ExistingDir) then
      RegQueryStringValue(Root, UninstallKey, 'InstallLocation', ExistingDir);
    if not RegQueryStringValue(Root, UninstallKey, 'DisplayVersion', ExistingVersion) then
      ExistingVersion := '?';
  end;
end;

function FindExisting(): Boolean;
begin
  Result := ReadExisting(HKCU, False) or ReadExisting(HKLM64, True) or ReadExisting(HKLM32, True);
  ExistingFound := Result;
end;

function StillInstalled(): Boolean;
begin
  Result := RegKeyExists(ExistingRoot, UninstallKey);
end;

{ ---------- Версии ---------- }

function NextVersionPart(var S: String): Integer;
var
  P: Integer;
begin
  P := Pos('.', S);
  if P = 0 then
  begin
    Result := StrToIntDef(S, 0);
    S := '';
  end
  else
  begin
    Result := StrToIntDef(Copy(S, 1, P - 1), 0);
    Delete(S, 1, P);
  end;
end;

{ <0 — A старше B, 0 — равны, >0 — A новее. Суффиксы вида -beta отбрасываются. }
function CompareVersions(A, B: String): Integer;
var
  I, X, Y: Integer;
begin
  Result := 0;
  if Pos('-', A) > 0 then A := Copy(A, 1, Pos('-', A) - 1);
  if Pos('-', B) > 0 then B := Copy(B, 1, Pos('-', B) - 1);
  for I := 1 to 4 do
  begin
    X := NextVersionPart(A);
    Y := NextVersionPart(B);
    if X <> Y then
    begin
      if X < Y then Result := -1 else Result := 1;
      Exit;
    end;
  end;
end;

{ ---------- Удаление установленной копии ---------- }

function RemoveExisting(): Boolean;
var
  Uninstaller: String;
  ResultCode, I: Integer;
begin
  Uninstaller := RemoveQuotes(ExistingUninstall);
  if not FileExists(Uninstaller) then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('UninstallerMissing'), [Uninstaller]), mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  { ShellExec — чтобы деинсталлятор установки «для всех пользователей» мог запросить права администратора. }
  if not ShellExec('', Uninstaller, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('UninstallerFailed'), [SysErrorMessage(ResultCode)]), mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  { Деинсталлятор Inno Setup работает в две фазы — ждём, пока исчезнет запись об установке. }
  for I := 1 to 120 do
  begin
    if not StillInstalled() then Break;
    Sleep(500);
  end;
  Result := not StillInstalled();
end;

{ ---------- Выбор действия ---------- }

function OlderNote(): String;
begin
  Result := '';
  if CompareVersions(ExistingVersion, '{#AppVersion}') > 0 then
    Result := FmtMessage(CustomMessage('OlderNote'), [ExistingVersion, '{#AppVersion}']);
end;

function InitializeSetup(): Boolean;
var
  Cmp, Choice: Integer;
  Instruction, Text: String;
  Labels: TArrayOfString;
begin
  Result := True;
  if not FindExisting() then Exit;

  if ExistingPerMachine then
  begin
    { Старые установщики позволяли ставить «для всех пользователей». Поверх такой установки этот установщик
      («для себя») поставил бы вторую копию — поэтому только удалить старую и поставить заново.
      В тихом режиме (winget и т. п.) удалить её нельзя: нужны права администратора — установка отменяется. }
    if WizardSilent() then
    begin
      Log(FmtMessage(CustomMessage('PerMachineSilent'), [ExistingDir]));
      Result := False;
      Exit;
    end;
    SetArrayLength(Labels, 1);
    Labels[0] := FmtMessage(CustomMessage('PerMachineButton'), ['{#AppVersion}']);
    Choice := TaskDialogMsgBox(CustomMessage('PerMachineTitle'),
      FmtMessage(CustomMessage('PerMachineText'), [ExistingVersion, ExistingDir]) + OlderNote(),
      mbConfirmation, MB_OKCANCEL, Labels, 0);
    if Choice <> IDOK then
    begin
      Result := False;
      Exit;
    end;
    Result := RemoveExisting();
    if Result then
      ExistingFound := False
    else
      MsgBox(CustomMessage('PerMachineNotRemoved'), mbInformation, MB_OK);
    Exit;
  end;

  { Тихая установка поверх своей копии — это обновление (или переустановка) в ту же папку, без вопросов. }
  if WizardSilent() then Exit;

  Cmp := CompareVersions(ExistingVersion, '{#AppVersion}');
  SetArrayLength(Labels, 2);
  if Cmp < 0 then
  begin
    Instruction := FmtMessage(CustomMessage('UpgradeTitle'), ['{#AppVersion}']);
    Text := FmtMessage(CustomMessage('UpgradeText'), [ExistingVersion, ExistingDir]);
    Labels[0] := FmtMessage(CustomMessage('UpgradeButton'), ['{#AppVersion}']);
  end
  else if Cmp = 0 then
  begin
    Instruction := FmtMessage(CustomMessage('SameTitle'), ['{#AppVersion}']);
    Text := FmtMessage(CustomMessage('SameText'), [ExistingDir]);
    Labels[0] := CustomMessage('SameButton');
  end
  else
  begin
    Instruction := CustomMessage('NewerTitle');
    Text := FmtMessage(CustomMessage('NewerText'), [ExistingVersion, ExistingDir, '{#AppVersion}']);
    Labels[0] := FmtMessage(CustomMessage('NewerButton'), ['{#AppVersion}']);
  end;
  Labels[1] := CustomMessage('RemoveButton');

  Choice := TaskDialogMsgBox(Instruction, Text, mbConfirmation, MB_YESNOCANCEL, Labels, 0);
  case Choice of
    IDYES:
      Result := True;
    IDNO:
      begin
        if RemoveExisting() then
          MsgBox(CustomMessage('Removed'), mbInformation, MB_OK)
        else
          MsgBox(CustomMessage('NotRemoved'), mbInformation, MB_OK);
        Result := False;
      end;
  else
    Result := False;
  end;
end;

{ Обновление и переустановка — строго в ту же папку: иначе на диске осталась бы вторая копия. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectDir) and ExistingFound;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Файл, оставшийся после обновления из самой программы (см. InstallGuard), больше не нужен. }
  if CurStep = ssPostInstall then
    DeleteFile(ExpandConstant('{app}\{#AppExe}.old'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DeleteFile(ExpandConstant('{app}\{#AppExe}.old'));
    DataDir := ExpandConstant('{localappdata}\Offload');
    { В тихом режиме (winget и т. п.) данные сохраняются без вопроса — даже без /SUPPRESSMSGBOXES. }
    if DirExists(DataDir) and not UninstallSilent() then
    begin
      if SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]),
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
