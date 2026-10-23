#define AppName "WG-RDP"
#define AppVersion "1.0.0"
#define AppPublisher "WG-RDP"
#define AppExeName "WgRdp.exe"

[Setup]
AppId={{7E3C8B4A-2F91-4D6B-9C1E-5A8F0D3B7E21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=WG-RDP-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupLogging=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
CloseApplications=yes
RestartApplications=no
DisableDirPage=no
LicenseFile=
SetupIconFile=
; El runtime de .NET va dentro de la carpeta publicada (autocontenido).
; WireGuard se instala después, con el MSI oficial.

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un icono en el escritorio"; GroupDescription: "Iconos:"; Flags: checkedonce

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\redist\wireguard-amd64.msi"; DestDir: "{app}\Setup\wireguard"; DestName: "wireguard-amd64.msi"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "msiexec.exe"; \
    Parameters: "/i ""{app}\Setup\wireguard\wireguard-amd64.msi"" /qn /norestart"; \
    StatusMsg: "Instalando WireGuard…"; \
    Flags: waituntilterminated; \
    Check: NeedWireGuard
Filename: "{app}\{#AppExeName}"; \
    Description: "Iniciar {#AppName}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; No borrar %ProgramData%\WgRdp (perfiles, túnel y logs del usuario).
; No desinstalar WireGuard: puede usarlo otro software.

[Code]
function NeedWireGuard: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonpf64}\WireGuard\wg.exe'))
        and not FileExists(ExpandConstant('{pf}\WireGuard\wg.exe'))
        and not FileExists('C:\Program Files\WireGuard\wg.exe');
end;
