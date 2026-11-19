#!/bin/bash
# Instala un acceso directo de escritorio para WG-RDP (Linux) en el menú de aplicaciones.
# En muchos entornos (GNOME, KDE) aparece en la sección Red / Internet.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DESKTOP_FILE="$HOME/.local/share/applications/wgrdp-linux.desktop"
ICON="$ROOT/src/WgRdp.Linux/Assets/app-icon.png"

mkdir -p "$(dirname "$DESKTOP_FILE")"

if [[ ! -f "$ICON" ]]; then
  echo "No se encontró el icono: $ICON" >&2
  exit 1
fi

cat >"$DESKTOP_FILE" <<EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=WG-RDP
Name[es]=WG-RDP
GenericName=Remote desktop via WireGuard
GenericName[es]=Escritorio remoto vía WireGuard
Comment=WireGuard VPN and RDP session (FreeRDP / Remmina)
Comment[es]=VPN WireGuard y sesión RDP (FreeRDP)
Exec=/bin/bash "$ROOT/build.sh" run
Path=$ROOT
Icon=$ICON
Terminal=false
Categories=Network;RemoteAccess;
StartupNotify=true
Keywords=wg-rdp;wireguard;rdp;remmina;vpn;acceso remoto;escritorio remoto;
EOF

chmod 644 "$DESKTOP_FILE"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
fi

echo "Instalado: $DESKTOP_FILE"
echo "Cierre sesión o ejecute «Actualizar bases de datos de escritorio» si no aparece al instante."
