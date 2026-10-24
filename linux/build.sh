#!/bin/bash
# Compila la edición Linux. Si la ruta del proyecto contiene «:» (recurso
# SMB de gvfs), MSBuild y el generador de Avalonia no pueden usarla: se
# compila una copia en el disco local.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

PROJECT_DIR="$ROOT"
if [[ "$ROOT" == *:* ]]; then
  PROJECT_DIR="${WGRDP_BUILD_DIR:-$HOME/.cache/wgrdp-linux-build}"
  mkdir -p "$PROJECT_DIR"
  rm -rf "$PROJECT_DIR/src" "$PROJECT_DIR/WgRdp.Linux.sln" "$PROJECT_DIR/Directory.Build.props"
  cp -a "$ROOT/src" "$ROOT/WgRdp.Linux.sln" "$ROOT/Directory.Build.props" "$PROJECT_DIR/"
fi

CONFIG=Release
if [[ "${1:-}" == "run" ]]; then
  shift
  exec dotnet run --project "$PROJECT_DIR/src/WgRdp.Linux/WgRdp.Linux.csproj" -c "$CONFIG" -- "$@"
fi

dotnet build "$PROJECT_DIR/WgRdp.Linux.sln" -c "$CONFIG" "$@"
echo "Binario: /tmp/wgrdp-linux/bin/${CONFIG}/net8.0/wgrdp.dll"
