#ifndef Payload
  #define Payload "..\..\.tool-cache\exus-build\payload"
#endif
#ifndef OutputFolder
  #define OutputFolder "..\.."
#endif
[Setup]
AppId={{741F735C-96E2-4C0F-94BC-FD2A26923589}
AppName=HEXODUS BioSync
AppVersion=2.2.0
AppPublisher=HEXODUS
DefaultDirName={autopf}\Motor Local Exus
DefaultGroupName=HEXODUS BioSync
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#OutputFolder}
OutputBaseFilename=HEXODUS-BioSync-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=Antes-de-instalar.txt
UninstallDisplayIcon={app}\Desktop\MotorLocalExus.exe
CloseApplications=yes
CloseApplicationsFilter=MotorLocalExus.exe;MotorLocalExus.Reader.exe;MotorLocalExus.Service.exe
RestartApplications=no
SetupLogging=yes
VersionInfoVersion=2.2.0.0
SetupIconFile=HEXODUS_BioSync.ico
VersionInfoDescription=Instalador HEXODUS BioSync

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"

[Files]
Source: "{#Payload}\Desktop\MotorLocalExus.exe"; DestDir: "{app}\Desktop"; Flags: ignoreversion
Source: "{#Payload}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Payload}\Reader\*"; DestDir: "{app}\Reader"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Antes-de-instalar.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\HEXODUS BioSync"; Filename: "{app}\Desktop\MotorLocalExus.exe"
Name: "{group}\Desinstalar HEXODUS BioSync"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HEXODUS BioSync"; Filename: "{app}\Desktop\MotorLocalExus.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MotorLocalExusStatus"; ValueData: """{app}\Desktop\MotorLocalExus.exe"" --tray"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MotorLocalExusReader"; ValueData: """{app}\Reader\MotorLocalExus.Reader.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Desktop\MotorLocalExus.exe"; Description: "Abrir la ventana de estado de HEXODUS BioSync"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
var
  ReaderPage: TWizardPage;
  ReaderYes, ReaderNo: TNewRadioButton;
  SearchButton: TNewButton;
  CompletedSetup, StoppedForUpdate, RemovalCompleted: Boolean;

procedure ReaderChoice(Sender: TObject);
begin
  SearchButton.Enabled := ReaderYes.Checked;
end;

procedure FindReader(Sender: TObject);
var Code: Integer; ExistingDesktop: String;
begin
  SearchButton.Enabled := False;
  try
    ExistingDesktop := ExpandConstant('{app}\Desktop\MotorLocalExus.exe');
    if FileExists(ExistingDesktop) then begin
      if not Exec(ExistingDesktop, '--probe', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
        MsgBox('No se pudo abrir la comprobación del lector. Puedes continuar sin realizarla.', mbInformation, MB_OK);
    end else
      MsgBox('La comprobación del lector se realizará al finalizar la instalación. Puedes continuar sin bloquear la instalación.', mbInformation, MB_OK);
  finally
    SearchButton.Enabled := True;
  end;
end;

procedure InitializeWizard;
var Hint: TNewStaticText;
begin
  ReaderPage := CreateCustomPage(wpInfoBefore, 'Comprobación opcional del lector', 'Puedes instalar el motor aunque no tengas el lector o su controlador.');
  Hint := TNewStaticText.Create(ReaderPage);
  Hint.Parent := ReaderPage.Surface;
  Hint.Caption := '¿Tienes conectado el lector de huellas?' + #13#10 + #13#10 + 'Esta comprobación es informativa. El controlador se instala por separado.';
  Hint.AutoSize := False; Hint.Width := ReaderPage.SurfaceWidth; Hint.Height := ScaleY(70); Hint.WordWrap := True;
  ReaderNo := TNewRadioButton.Create(ReaderPage);
  ReaderNo.Parent := ReaderPage.Surface; ReaderNo.Top := ScaleY(80); ReaderNo.Width := ReaderPage.SurfaceWidth;
  ReaderNo.Caption := 'No / Omitir por ahora y continuar'; ReaderNo.Checked := True; ReaderNo.OnClick := @ReaderChoice;
  ReaderYes := TNewRadioButton.Create(ReaderPage);
  ReaderYes.Parent := ReaderPage.Surface; ReaderYes.Top := ScaleY(115); ReaderYes.Width := ReaderPage.SurfaceWidth;
  ReaderYes.Caption := 'Sí, quiero comprobar si se detecta'; ReaderYes.OnClick := @ReaderChoice;
  SearchButton := TNewButton.Create(ReaderPage);
  SearchButton.Parent := ReaderPage.Surface; SearchButton.Top := ScaleY(160); SearchButton.Width := ScaleX(180);
  SearchButton.Caption := 'Buscar lector'; SearchButton.Enabled := False; SearchButton.OnClick := @FindReader;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  // No se ejecuta ningun binario del motor durante la preparacion.
  // Solo se detiene el servicio existente con la herramienta de Windows.
  if Exec(ExpandConstant('{sys}\sc.exe'), 'stop MotorLocalExus', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    StoppedForUpdate := True
  else
    StoppedForUpdate := False;
end;
procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer; ProfilePath: String;
begin
  if CurStep = ssPostInstall then begin
    ProfilePath := ExtractFilePath(ExpandConstant('{srcexe}')) + 'MotorLocalExus.exusconfig';
    if not Exec(ExpandConstant('{app}\Desktop\MotorLocalExus.exe'), '--setup --profile "' + ProfilePath + '"', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
      RaiseException('No se pudo abrir la configuración del motor.');
    if Code <> 0 then
      RaiseException('La configuración inicial no se completó. Abre HEXODUS BioSync y pulsa Configuración para terminar.');
    CompletedSetup := True;
  end;
end;

procedure DeinitializeSetup;
var Code: Integer;
begin
  if StoppedForUpdate and not CompletedSetup then begin
    // If an upgrade is cancelled, restore the previous service instead of leaving it stopped.
    if FileExists(ExpandConstant('{app}\Desktop\MotorLocalExus.exe')) then
      Exec(ExpandConstant('{app}\Desktop\MotorLocalExus.exe'), '--start', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

function InitializeUninstall: Boolean;
var Code: Integer;
begin
  Result := False;
  if MsgBox('Se eliminará HEXODUS BioSync junto con su configuración, logs y datos locales. No se eliminarán el controlador del lector ni los datos guardados en HEXODUS. ¿Continuar?', mbConfirmation, MB_YESNO) <> IDYES then Exit;
  if not Exec(ExpandConstant('{app}\Desktop\MotorLocalExus.exe'), '--stop', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then begin
    MsgBox('No se pudo detener el motor. No se eliminarán sus datos.', mbError, MB_OK); Exit;
  end;
  if not Exec(ExpandConstant('{app}\Desktop\MotorLocalExus.exe'), '--pending', '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := 10;
  if Code <> 0 then begin
    if MsgBox('Hay eventos pendientes o no se pudo verificar la cola. Si continúas se perderán esos eventos locales. ¿Eliminar de todos modos?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then begin
      Exec(ExpandConstant('{app}\Desktop\MotorLocalExus.exe'), '--start', '', SW_HIDE, ewWaitUntilTerminated, Code); Exit;
    end;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Code: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    if not Exec(ExpandConstant('{app}\Desktop\MotorLocalExus.exe'), '--remove', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('No se pudo eliminar el servicio o su carpeta de datos. La desinstalación se interrumpió para conservar los archivos.');
    RemovalCompleted := True;
  end;
end;

procedure DeinitializeUninstall;
var Code: Integer;
begin
  if not RemovalCompleted and FileExists(ExpandConstant('{app}\Desktop\MotorLocalExus.exe')) then
    Exec(ExpandConstant('{app}\Desktop\MotorLocalExus.exe'), '--start', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
