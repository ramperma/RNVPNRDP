# WG-RDP Launcher

Aplicación de Windows (.NET 8 / WPF) que automatiza el acceso remoto.

La edición para Linux está en [`linux/`](linux/README.md) y es un proyecto aparte
(Avalonia, `wg-quick` y FreeRDP). Esta carpeta `src/WgRdp` sigue siendo solo Windows.

1. **Instala** el cliente oficial de Windows de **WireGuard** si no está presente (MSI silencioso).
2. **Importa** la configuración de túnel por defecto (`Setup\wireguard\default.conf`).
3. **Conecta** la VPN y verifica el handshake.
4. Abre una **sesión RDP incrustada** (control MSTSCLib) al servidor de terminales con las
   credenciales guardadas (usuario, dominio y contraseña inyectados en un solo paso).
5. Al terminar la sesión, **desmonta** el túnel.

## Requisitos

- Windows 10/11 x64 en el equipo de destino.
- SDK de .NET 8 para compilar (la compilación puede hacerse en Linux con `EnableWindowsTargeting`).

## Compilar

```bash
dotnet build RNWgRdp.sln -c Release
# Publicar exe autocontenido (en Windows o con -r win-x64 desde Linux):
dotnet publish src/WgRdp/WgRdp.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Preparación de los activos (opcional)

El asistente de primera ejecución descarga el MSI oficial automáticamente, así que no hace falta
preparar nada. Si prefiere distribuirlo todo offline:

1. Descargue el MSI oficial de WireGuard (https://download.wireguard.com/windows-client/,
   p. ej. `wireguard-amd64-1.1.1.msi`) y colóquelo en `assets/wireguard/` (se copia a
   `Setup\wireguard\` en la salida).
2. Copie `assets/default.conf.example` a `assets/wireguard/default.conf` y rellene las claves
   WireGuard de su infraestructura. **No versione el `.conf` real** (contiene clave privada).

## Uso

### Primera ejecución (asistente automático)

La primera vez que se abre, la app lanza un **protocolo de instalación inicial en 3 pasos**:

1. **Instalar WireGuard** — detecta si el cliente está presente; si falta, descarga el MSI oficial
   más reciente desde `download.wireguard.com` (con barra de progreso) a
   `%ProgramData%\WgRdp\setup\` y lo instala en silencio (pide UAC).
2. **Configuración del túnel** — seleccione su archivo `.conf` o pegue el contenido; se guarda en
   `%ProgramData%\WgRdp\default.conf`.
3. **Datos del usuario RDP** — servidor, puerto, usuario, dominio, contraseña (va al almacén de
   credenciales), nombre del túnel e IP VPN opcional.

Al finalizar queda marcado en `%ProgramData%\WgRdp\setup.json`; el asistente puede volver a
lanzarse con el botón **Reiniciar asistente de configuración**.

### Uso diario

1. Rellene/ajuste el perfil y pulse **Guardar perfil**. La contraseña se guarda en el
   **Administrador de credenciales de Windows**, nunca en disco en claro.
2. Pulse **Conectar y abrir RDP**. La app pedirá elevación (UAC) solo para instalar WireGuard o
   importar el túnel; la sesión RDP corre sin privilegios.
3. Al cerrar la ventana de sesión, el túnel se detiene (y se elimina si marca esa opción).

## Arquitectura

- `Orchestration/Pipeline.cs` — máquina de estados Install → Import → Connect → RDP → Teardown.
- `WireGuard/` — `wg.exe` + servicios `WireGuardTunnel$<nombre>`.
- `Rdp/` — control ActiveX `MsRdpClient10 not safe for scripting` vía `AxHost`; interop manual de
  `IMsTscNonScriptable` para `SetClearTextCredentials`; fin de sesión por sondeo de `Connected`.
- `Profiles/` — JSON sin secretos en `%ProgramData%\WgRdp` + secretos en CredMan (`WgRdp/<perfil>`).
- `Elevation/` — re-elevación del propio exe con `--elevated <comando>` (verbo `runas`).
- Logs en `%ProgramData%\WgRdp\logs`.

## Notas y limitaciones

- La v1 detecta el fin de sesión por sondeo (1 s) en lugar de eventos `OnDisconnected`.
- Firme el binario y documente el comportamiento ante su antivirus/EDR (MSI silencioso +
  inyección de credenciales pueden generar alertas).
- Pruebe en una VM limpia antes de desplegar.
