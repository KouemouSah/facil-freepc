; Facil FreePC - installeur Inno Setup.
; Compile par build.ps1 :  ISCC /DAppVersion=x.y.z /DSourceExe=<exe publie> installer\FacilFreePC.iss
; Installation PAR UTILISATEUR (aucun droit administrateur) : %LOCALAPPDATA%\Programs\Facil FreePC.

#define AppName "Facil FreePC"
#define AppExe "FacilFreePC.exe"
#define AppDataId "FacilFreePC"
#define AppPublisher "Emac SAH"
#define AppCopyright "Copyright (c) 2026 EMAC SAH AI Solutions Architect"
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

[Code]
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
