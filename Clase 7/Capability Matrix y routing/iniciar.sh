#!/usr/bin/env bash
# Arranca la demo con el Ollama de esta máquina. Ctrl+C la detiene.
#   ./iniciar.sh                          puerto 5800
#   NVIDIA_API_KEY=nvapi-... ./iniciar.sh la clave de NVIDIA (si no, se lee de appsettings.Local.json)
#   OLLAMA_URL=http://otra:11434 ./iniciar.sh
set -euo pipefail
cd "$(dirname "$0")"

PUERTO="${PUERTO:-5800}"
OLLAMA="${OLLAMA_URL:-http://localhost:11434}"

command -v dotnet >/dev/null || { echo "✘ Falta el SDK de .NET 10: https://dotnet.microsoft.com/download"; exit 1; }

if ! curl -sf "$OLLAMA/api/tags" >/dev/null; then
  echo "✘ Ollama no responde en $OLLAMA. Inicialo con: ollama serve"
  exit 1
fi
echo "▸ Ollama en $OLLAMA"
if [[ -n "${NVIDIA_API_KEY:-}" ]]; then echo "▸ Clave de NVIDIA: variable de entorno"
elif [[ -f appsettings.Local.json ]]; then echo "▸ Clave de NVIDIA: appsettings.Local.json"
else echo "  ⚠ Sin clave de NVIDIA: el perfil grande corre qwen3.5:9b local en lugar de NVIDIA"; fi

echo "▸ Demo en http://localhost:$PUERTO  (Ctrl+C para detener)"
exec dotnet run -c Release --urls "http://localhost:$PUERTO"
