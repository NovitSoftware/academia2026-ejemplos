#!/usr/bin/env bash
# Arranca la demo. Ctrl+C la detiene.
#   ./iniciar.sh                          puerto 5900
#   PUERTO=5901 ./iniciar.sh
#   OLLAMA_URL=http://otra:11434 ./iniciar.sh
# La demo funciona sin Ollama: el banco, la tablet y la cuenta son simulados.
# Con Ollama se habilita "Medir en este equipo" y el modo real de la tablet.
set -euo pipefail
cd "$(dirname "$0")"

PUERTO="${PUERTO:-5900}"
OLLAMA="${OLLAMA_URL:-http://localhost:11434}"

command -v dotnet >/dev/null || { echo "✘ Falta el SDK de .NET 10: https://dotnet.microsoft.com/download"; exit 1; }

if curl -sf "$OLLAMA/api/tags" >/dev/null; then echo "▸ Ollama en $OLLAMA"
else echo "  ⚠ Ollama no responde en $OLLAMA: la demo corre solo con datos simulados"; fi

echo "▸ Demo en http://localhost:$PUERTO  (Ctrl+C para detener)"
export Ollama__Url="$OLLAMA"
exec dotnet run -c Release --urls "http://localhost:$PUERTO"
