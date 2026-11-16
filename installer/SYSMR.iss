; Instalador do SYSMR (Inno Setup 6). Gere com installer\build-installer.ps1
#define AppName "SYSMR"
#define AppVersion "1.0.0"
#define AppPublisher "SYSMR"
#define AppExe "SYSMR.UI.exe"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#define RuntimeDirectUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{6F3B1E52-4C1A-4B7E-9D54-5A0C7E2D9A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=SYSMR-Setup-{#AppVersion}
SetupIconFile=..\SYSMR.UI\Assets\sysmr.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"
Name: "startup"; Description: "Iniciar o SYSMR com o Windows"; GroupDescription: "Opções:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\MANUAL-USUARIO.html"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Manual do SYSMR"; Filename: "{app}\MANUAL-USUARIO.html"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName} agora"; Flags: nowait postinstall skipifsilent

[Code]
function IsDesktopRuntime8Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntime8Installed then
  begin
    if MsgBox('O SYSMR precisa do .NET 8 Desktop Runtime (x64), que não foi encontrado neste computador.' + #13#10#13#10 +
              'Deseja baixar o instalador do runtime agora? Depois de instalá-lo, execute este instalador novamente.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', '{#RuntimeDirectUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
