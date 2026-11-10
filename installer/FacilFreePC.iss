; Facil FreePC - installeur Inno Setup.
; Compile par build.ps1 :  ISCC /DAppVersion=x.y.z /DSourceExe=<exe publie> installer\FacilFreePC.iss
; Installation PAR UTILISATEUR (aucun droit administrateur) : %LOCALAPPDATA%\Programs\Facil FreePC.
; Silencieux : /SILENT ou /VERYSILENT /SUPPRESSMSGBOXES /NORESTART ; /RELAUNCH=1 relance FreePC apres l'installation
; (mise a jour depuis l'application). Desinstallation silencieuse : unins000.exe /VERYSILENT (donnees conservees).

#define AppName "Facil FreePC"
#define AppExe "FacilFreePC.exe"
#define AppDataId "FacilFreePC"
#define AppPublisher "Emac SAH"
#define AppCopyright "Copyright (c) 2026 EMAC SAH AI Solutions Architect"
; Compilateur fige : meme version en local et en CI (tools\Install-InnoSetup.ps1). Plus ancien = refus, pas divergence.
#if VER < EncodeVer(7,1,1)
  #error Inno Setup 7.1.1 minimum requis : lancer tools\Install-InnoSetup.ps1
#endif
#ifndef AppVersion
  #error AppVersion non defini (utiliser build.ps1)
#endif
#ifndef SourceExe
  #error SourceExe non defini (utiliser build.ps1)
#endif
#ifndef ExtensionXpi
  #error ExtensionXpi non defini (utiliser build.ps1)
#endif

[Setup]
; AppId : NE JAMAIS CHANGER (identifie le produit pour les mises a jour et la desinstallation).
AppId={{930648CE-F8E4-4C30-8D5E-B717E90CFCEA}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright={#AppCopyright}
AppComments=Liberation et controle des ressources Windows - une solution de la marque Facil
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoCopyright={#AppCopyright}
VersionInfoDescription=Installation de {#AppName}
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=FacilFreePC-Setup-{#AppVersion}
SetupIconFile=..\assets\app.ico
WizardSmallImageFile=..\assets\wizard-small-55.png,..\assets\wizard-small-110.png
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ShowLanguageDialog=no
SetupMutex=FacilFreePCSetup

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
fr.StartupTask=Démarrer {#AppName} à l'ouverture de session (recommandé : la surveillance tourne en arrière-plan)
en.StartupTask=Start {#AppName} when I sign in (recommended: monitoring runs in the background)
fr.DeleteData=Supprimer aussi les données de {#AppName} (historique, journal, réglages) ?
en.DeleteData=Also delete {#AppName} data (history, log, settings)?
fr.Downgrade=Une version plus récente de {#AppName} (%1) est déjà installée. Installez une version égale ou plus récente que %1.
en.Downgrade=A newer version of {#AppName} (%1) is already installed. Install version %1 or later.
fr.CloseFailed={#AppName} est encore en cours d'exécution et n'a pas pu être fermé. Fermez-le depuis la zone de notification puis relancez l'installation.
en.CloseFailed={#AppName} is still running and could not be closed. Quit it from the notification area and run setup again.

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion

; Extension compagnon Firefox (proposee a l'installation dans Firefox par FreePC seulement si signee par Mozilla).
Source: "{#ExtensionXpi}"; DestDir: "{app}\extensions"; DestName: "facil-freepc-firefox.xpi"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Demarrage a l'ouverture de session, en arriere-plan (icone seulement). Retire a la desinstallation.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppDataId}"; ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Mise a jour lancee par FreePC (/RELAUNCH=1, silencieuse) : relance en arriere-plan (icone de notification).
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: RelaunchRequested

[Code]
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// Compare deux versions x.y.z : -1, 0 ou 1.
function CompareVersions(A, B: String): Integer;
var
  I, PA, PB, NA, NB: Integer;
begin
  Result := 0;
  for I := 1 to 3 do
  begin
    PA := Pos('.', A);
    PB := Pos('.', B);
    if PA = 0 then begin NA := StrToIntDef(A, 0); A := ''; end else begin NA := StrToIntDef(Copy(A, 1, PA - 1), 0); A := Copy(A, PA + 1, Length(A)); end;
    if PB = 0 then begin NB := StrToIntDef(B, 0); B := ''; end else begin NB := StrToIntDef(Copy(B, 1, PB - 1), 0); B := Copy(B, PB + 1, Length(B)); end;
    if NA < NB then begin Result := -1; Exit; end;
    if NA > NB then begin Result := 1; Exit; end;
  end;
end;

// Retour en arriere refuse : la base de donnees d'une version plus recente serait refusee par l'ancienne.
function InitializeSetup(): Boolean;
var
  Installed: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{930648CE-F8E4-4C30-8D5E-B717E90CFCEA}_is1', 'DisplayVersion', Installed)
     and (CompareVersions(Installed, '{#AppVersion}') > 0) then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('Downgrade'), [Installed]), mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

// Ferme proprement une instance en cours (FacilFreePC.exe --quit attend la fin reelle, 15 s max).
function QuitRunningInstance(const Exe: String): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if FileExists(Exe) then
    Result := Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

// Mise a jour : fermer l'ancienne version avant de remplacer l'exe.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not QuitRunningInstance(ExpandConstant('{app}\{#AppExe}')) then
    Result := CustomMessage('CloseFailed');
end;

// Desinstallation : fermer l'application avant de supprimer ses fichiers.
function InitializeUninstall(): Boolean;
begin
  Result := QuitRunningInstance(ExpandConstant('{app}\{#AppExe}'));
  if not Result then
    MsgBox(CustomMessage('CloseFailed'), mbError, MB_OK);
end;

// Donnees utilisateur : conservees par defaut ; supprimees seulement sur confirmation explicite
// (jamais en desinstallation silencieuse).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#AppDataId}');
    if DirExists(DataDir) and (not UninstallSilent()) then
      if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
