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
AppId={{{#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Offload
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
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Приложение создаёт этот мьютекс — установщик попросит закрыть Offload
AppMutex=Offload.Running
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать Offload при входе в Windows"; GroupDescription: "Дополнительно:"
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Отключение MCP-сервера из всех IDE и остановка llama-server
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "OffloadCleanup"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1';

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
    MsgBox('Не найден деинсталлятор установленной версии:' + #13#10 + Uninstaller + #13#10#13#10 +
           'Удалите Offload через «Параметры → Приложения» и запустите установку снова.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  { ShellExec — чтобы деинсталлятор установки «для всех пользователей» мог запросить права администратора. }
  if not ShellExec('', Uninstaller, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Не удалось запустить деинсталлятор: ' + SysErrorMessage(ResultCode), mbError, MB_OK);
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
    Result := #13#10#13#10 + 'Внимание: установлена более новая версия (' + ExistingVersion + '), чем в этом установщике ({#AppVersion}).';
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
      («для себя») поставил бы вторую копию — поэтому только удалить старую и поставить заново. }
    SetArrayLength(Labels, 1);
    Labels[0] := 'Удалить её и установить Offload {#AppVersion}';
    Choice := TaskDialogMsgBox('Offload уже установлен для всех пользователей',
      'Установлена версия ' + ExistingVersion + ' в папке ' + ExistingDir + '.' + #13#10#13#10 +
      'Эта версия ставится только для текущего пользователя, поэтому обновить установку «для всех» нельзя — ' +
      'получилось бы две копии Offload. Удалите установленную версию (потребуются права администратора); ' +
      'скачанные модели и настройки можно сохранить.' + OlderNote(),
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
      MsgBox('Установленная версия не удалена — установка отменена, чтобы не было двух копий Offload.', mbInformation, MB_OK);
    Exit;
  end;

  Cmp := CompareVersions(ExistingVersion, '{#AppVersion}');
  SetArrayLength(Labels, 2);
  if Cmp < 0 then
  begin
    Instruction := 'Обновить Offload до версии {#AppVersion}?';
    Text := 'Установлена версия ' + ExistingVersion + ' в папке ' + ExistingDir + '.' + #13#10#13#10 +
            'Новая версия будет установлена в ту же папку. Настройки, скачанные модели и подключения к IDE сохранятся.';
    Labels[0] := 'Обновить до {#AppVersion}';
  end
  else if Cmp = 0 then
  begin
    Instruction := 'Offload {#AppVersion} уже установлен';
    Text := 'Установлен в папке ' + ExistingDir + '.' + #13#10#13#10 +
            'Можно переустановить (восстановить файлы программы и изменить параметры: автозапуск, значок на рабочем столе) ' +
            'или удалить Offload.';
    Labels[0] := 'Переустановить или изменить параметры';
  end
  else
  begin
    Instruction := 'Установлена более новая версия Offload';
    Text := 'Установлена версия ' + ExistingVersion + ' в папке ' + ExistingDir +
            ', а этот установщик — более старой версии {#AppVersion}.' + #13#10#13#10 +
            'Старая версия может не прочитать настройки новой.';
    Labels[0] := 'Установить версию {#AppVersion} поверх';
  end;
  Labels[1] := 'Удалить Offload';

  Choice := TaskDialogMsgBox(Instruction, Text, mbConfirmation, MB_YESNOCANCEL, Labels, 0);
  case Choice of
    IDYES:
      Result := True;
    IDNO:
      begin
        if RemoveExisting() then
          MsgBox('Offload удалён.', mbInformation, MB_OK)
        else
          MsgBox('Offload не удалён.', mbInformation, MB_OK);
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
    if DirExists(DataDir) then
    begin
      if MsgBox('Удалить также скачанные модели, llama.cpp, OpenCode и настройки Offload?' + #13#10 +
                '(' + DataDir + ')' + #13#10#13#10 +
                'Модели занимают много места. Выберите «Нет», если хотите сохранить их для повторной установки.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
