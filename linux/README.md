# WG-RDP para Linux

Edición independiente de la aplicación de Windows. No comparte binario ni interfaz:
en Linux el túnel lo levanta `wg-quick` y el escritorio lo abre FreeRDP.

La aplicación de Windows (`src/WgRdp`) no cambia.

## Qué hace

1. Comprueba `wg`, `wg-quick` y FreeRDP (`xfreerdp` o `wlfreerdp`). Si falta alguno,
   lo instala con el gestor de paquetes (en Debian: `wireguard`, `wireguard-tools`,
   `freerdp3-x11`) y pide autorización con polkit.
2. Copia el `.conf` a `/etc/wireguard/<túnel>.conf` (modo 0600) y ejecuta `wg-quick up`.
3. Espera handshake, ping a la IP VPN o que el puerto RDP responda.
4. Abre el escritorio con Remmina (el FreeRDP que ya usa) o, si existe, con `xfreerdp`.
   La contraseña no va en la línea de comandos: Remmina la cifra en un perfil temporal
   y `xfreerdp` la lee por la entrada estándar.
5. Al cerrar esa ventana, ejecuta `wg-quick down`. Si el perfil lo pide, borra el `.conf`.

## Requisitos

- Linux x64 con escritorio (el diálogo de polkit y FreeRDP lo necesitan).
- SDK de .NET 8 para compilar.
- Paquetes que la propia app puede instalar: `wireguard-tools`, `freerdp3-x11`,
  `libsecret-tools`.

## Compilar y abrir

```bash
cd linux
bash build.sh
bash build.sh run
```

Comprobar herramientas sin abrir la ventana:

```bash
bash build.sh run -- --doctor
```

`build.sh` copia el proyecto a un directorio local antes de compilar: la ruta de
este recurso compartido contiene `:` y ni MSBuild ni el generador de Avalonia
pueden usarla. La salida queda en `/tmp/wgrdp-linux/bin/Release/net8.0/wgrdp.dll`.

## Datos de esta máquina

| Qué | Dónde |
| --- | --- |
| Perfiles (sin contraseña) | `~/.config/wgrdp/profiles.json` |
| Configuración del túnel | `~/.config/wgrdp/default.conf` (modo 0600) |
| Contraseña | Anillo de claves, servicio `wgrdp`. Si no hay `secret-tool`, archivo 0600 en `~/.local/share/wgrdp/secrets/` |
| Registro | `~/.local/state/wgrdp/logs/` |

Cerrar la ventana la deja en la bandeja. **Salir** en el menú de la bandeja cierra el túnel y termina el proceso.

## Diferencias con Windows

- No hay control RDP embebido: FreeRDP abre su ventana.
- La autorización es polkit (`pkexec`), no UAC.
- El túnel es una interfaz `wg-quick`, no el servicio `WireGuardTunnel$`.
- Hace falta un núcleo con el módulo WireGuard (el de Debian ya lo trae).
