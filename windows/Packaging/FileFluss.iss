; FileFluss for Windows — installer.
;
; Built by windows/Packaging/build-release.ps1, which passes:
;   /DAppVersion=1.2.3      the version (from the win-vX.Y.Z tag; may be 1.2.3-beta.1)
;   /DNumericVersion=1.2.3  the same without a pre-release suffix (file-version fields are numbers only)
;   /DArch=x64|arm64        the architecture of the published files
;   /DSource=<folder>       the self-contained publish output
;
; Per-user and unprivileged, like dropping FileFluss.app into ~/Applications: it installs to
; %LOCALAPPDATA%\Programs\FileFluss and never asks for administrator rights. No file
; associations are registered. Settings, accounts and the search index live in
; %LOCALAPPDATA%\FileFluss, which neither an update nor the uninstaller touches.
; The same setup, run with /VERYSILENT /LAUNCH, is what the in-app updater uses.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Source
  #define Source "..\artifacts\publish\" + Arch
#endif

[Setup]
; Matches UninstallKey in windows/src/FileFluss.App/Services/UpdateService.cs. Never change it:
; a new AppId installs a second copy beside the first instead of updating it.
AppId={{ED875F07-FD68-4091-AD98-B37CDC7B8490}
AppName=FileFluss
AppVersion={#AppVersion}
AppVerName=FileFluss {#AppVersion}
AppPublisher=Rana GmbH
AppPublisherURL=https://github.com/rana-gmbh/FileFluss
AppSupportURL=https://github.com/rana-gmbh/FileFluss/issues
AppUpdatesURL=https://github.com/rana-gmbh/FileFluss/releases
DefaultDirName={localappdata}\Programs\FileFluss
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\release
OutputBaseFilename=FileFluss-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\FileFluss.App\Assets\FileFluss.ico
UninstallDisplayIcon={app}\FileFluss.exe
UninstallDisplayName=FileFluss
LicenseFile=..\..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
VersionInfoVersion={#NumericVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCompany=Rana GmbH
VersionInfoProductName=FileFluss
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.LaunchNow=Launch FileFluss
german.LaunchNow=FileFluss starten

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FileFluss"; Filename: "{app}\FileFluss.exe"

[Run]
Filename: "{app}\FileFluss.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent
; An in-app update runs silently and asks to be relaunched.
Filename: "{app}\FileFluss.exe"; Flags: nowait; Check: LaunchAfterSilentUpdate

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
function LaunchAfterSilentUpdate: Boolean;
begin
  Result := WizardSilent and (Pos('/LAUNCH', UpperCase(GetCmdTail)) > 0);
end;

{ Asks a running FileFluss to close (taskkill without /F posts WM_CLOSE, so it shuts down
  the way the close button does) and only then forces the issue. The in-app updater quits
  FileFluss itself before the setup gets here; this covers a setup started by hand. }
procedure StopFileFluss;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM FileFluss.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    Sleep(2500);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM FileFluss.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopFileFluss;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopFileFluss;
  Result := True;
end;
