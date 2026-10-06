; Squad DNS — script d'installation Inno Setup 6
;
; Lancé par installer\build.ps1, qui fournit les définitions /DAppVersion, /DPublishDir et /DOutputDir.
; Compilation manuelle (depuis la racine du dépôt) :
;   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 `
;       /DPublishDir="$PWD\installer\artifacts\publish" /DOutputDir="$PWD\installer\artifacts\output" `
;       installer\setup.iss
;
; Le fichier doit être enregistré en UTF-8 avec BOM pour que les accents des [Messages] français
; soient lus correctement par ISCC (qui interprète autrement le fichier en ANSI).
;
; Installation dans Program Files = droits administrateur. L'application, elle, se lance en
; asInvoker et redemande l'élévation au moment d'écrire la configuration réseau.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "artifacts\publish"
#endif

#ifndef OutputDir
  #define OutputDir "artifacts\output"
#endif

#define MyAppName "Squad DNS"
#define MyAppExe "SquadDns.exe"
#define MyAppPublisher "Squad DNS"
#define MyAppWeb "https://github.com/Aw3n/squad-dns"
#define MyAppId "{{B7D4F1C9-3E6A-4F0D-9B2B-6E1D2A8C5F73}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppWeb}
AppSupportURL={#MyAppWeb}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\SquadDns
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
OutputDir={#OutputDir}
OutputBaseFilename=SquadDNS-Setup-{#AppVersion}
SetupIconFile=..\src\SquadDns\Assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
; La publication est faite avec -r win-x64 : x64 uniquement.
; x64compatible demande Inno Setup 6.3 ou plus récent.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
MinVersion=10.0
WizardStyle=modern
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; CustomMessage ne formate pas les sauts de ligne : les paragraphes sont stockés en clés
; séparées et rejoints par #13#10 dans la partie [Code].
fr.GuideName=Guide d'utilisation
en.GuideName=User guide
fr.DotNetMissingLead=Cette version est « framework-dependent » : l'application a besoin du .NET Desktop Runtime 8 (x64) pour démarrer.
en.DotNetMissingLead=This build is framework-dependent: the application needs the .NET Desktop Runtime 8 (x64) in order to start.
fr.DotNetMissingDownload=Téléchargement : https://dotnet.microsoft.com/download/dotnet/8.0 (Windows x64 Desktop Runtime)
en.DotNetMissingDownload=Download: https://dotnet.microsoft.com/download/dotnet/8.0 (Windows x64 Desktop Runtime)
fr.DotNetMissingChoice=Cliquez sur Annuler pour interrompre l'installation, ou sur OK pour continuer et ajouter le runtime plus tard.
en.DotNetMissingChoice=Click Cancel to stop the installation, or OK to continue and add the runtime later.
fr.RuntimesNone=Aucun Microsoft.WindowsDesktop.App détecté
en.RuntimesNone=No Microsoft.WindowsDesktop.App detected
fr.RuntimesNoV8=Aucune version 8.x détectée
en.RuntimesNoV8=No 8.x version detected
fr.RuntimesFolderNote=Le dossier des runtimes existe mais aucune version n'est déclarée dans le registre : vérifiez le contenu de C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App.
en.RuntimesFolderNote=The runtime folder exists but no version is declared in the registry: check the contents of C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App.
fr.KeepDataLead=Squad DNS range vos sauvegardes DNS dans %LOCALAPPDATA%\SquadDns et dans le registre HKCU\Software\SquadDns. La désinstallation ne les supprime pas.
en.KeepDataLead=Squad DNS keeps your DNS backups in %LOCALAPPDATA%\SquadDns and under HKCU\Software\SquadDns. Uninstalling does not remove them.
fr.KeepDataAsk=Souhaitez-vous quand même supprimer ces sauvegardes ?
en.KeepDataAsk=Delete those backups anyway?

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\guide-fr.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\guide-en.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\{code:GetGuideName}"; Filename: "{app}\docs"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserData: Boolean;
  Runtimes: TArrayOfString;

function GetGuideName(Param: String): String;
begin
  { CustomMessage résout déjà la clé de la langue active (fr.GuideName / en.GuideName). }
  Result := CustomMessage('GuideName');
end;

procedure AddVersion(const Value: String);
var
  J: Integer;
  Known: Boolean;
begin
  Known := False;
  for J := 0 to GetArrayLength(Runtimes) - 1 do
    if Runtimes[J] = Value then
      Known := True;

  if Known then
    Exit;

  SetArrayLength(Runtimes, GetArrayLength(Runtimes) + 1);
  Runtimes[GetArrayLength(Runtimes) - 1] := Value;
end;

procedure RememberRuntimeVersions;
var
  Names: TArrayOfString;
  Key: String;
  I: Integer;
begin
  { Clé remplie par les installateurs officiels du runtime .NET et par dotnet-install.
    Le parcours du dossier shared\ est volontairement évité : l'API FindFirst d'Inno
    n'expose pas d'attribut « dossiers seuls » fiable dans le Pascal Script. }
  Key := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  if RegGetValueNames(HKLM64, Key, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      AddVersion(Names[I]);

  if (GetArrayLength(Runtimes) = 0) and
     DirExists(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) then
    AddVersion('folder-present');
end;

function HasDotNet8: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Runtimes) - 1 do
    if Copy(Runtimes[I], 1, 2) = '8.' then
      Result := True;
end;

function HasFolderNote: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Runtimes) - 1 do
    if Runtimes[I] = 'folder-present' then
      Result := True;
end;

function RuntimeSummary: String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Runtimes) - 1 do
    if Runtimes[I] <> 'folder-present' then
      Result := Result + Runtimes[I] + '  ';

  if Result = '' then
    Result := CustomMessage('RuntimesNone');

  if HasFolderNote then
    Result := Result + #13#10 + CustomMessage('RuntimesFolderNote')
  else if not HasDotNet8 then
    Result := Result + ' - ' + CustomMessage('RuntimesNoV8');
end;

function InitializeSetup: Boolean;
var
  Msg: String;
begin
  SetArrayLength(Runtimes, 0);
  RememberRuntimeVersions;

  Result := True;
  if HasDotNet8 then
    Exit;

  Msg := CustomMessage('DotNetMissingLead') + #13#10#13#10 +
    CustomMessage('DotNetMissingDownload') + #13#10#13#10 +
    RuntimeSummary + #13#10#13#10 +
    CustomMessage('DotNetMissingChoice');

  Result := (MsgBox(Msg, mbInformation, MB_OKCANCEL) = IDOK);
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  RemoveUserData := (MsgBox(CustomMessage('KeepDataLead') + #13#10#13#10 +
    CustomMessage('KeepDataAsk'), mbConfirmation, MB_YESNO) = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Local: String;
  ErrorCode: Integer;
begin
  if (CurUninstallStep <> usPostUninstall) or not RemoveUserData then
    Exit;

  Local := GetEnv('LOCALAPPDATA');
  if (Local <> '') and DirExists(Local + '\SquadDns') then
    DelTree(Local + '\SquadDns', True, True, True);

  { Le miroir registre des sauvegardes (HKCU) est nettoyé via reg.exe :
    RegDeleteTree n'existe pas dans le Pascal Script d'Inno Setup. }
  Exec(ExpandConstant('{sys}\reg.exe'), 'delete "HKCU\Software\SquadDns" /f', '',
    SW_HIDE, ewWaitUntilTerminated, ErrorCode);
end;
