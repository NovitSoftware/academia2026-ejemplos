#!/usr/bin/env bash
# Arranca la demo. Ctrl+C la detiene.
#   ./iniciar.sh                          puerto 5910
#   PUERTO=5911 ./iniciar.sh
#   OLLAMA_URL=http://otra:11434 ./iniciar.sh
# La demo funciona sin Ollama: las trayectorias, los tiempos y los costos son simulados.
# Con Ollama se habilita la prueba real de la caché de prefijo.
set -euo pipefail
cd "$(dirname "$0")"

PUERTO="${PUERTO:-5910}"
OLLAMA="${OLLAMA_URL:-http://localhost:11434}"

command -v dotnet >/dev/null || { echo "✘ Falta el SDK de .NET 10: https://dotnet.microsoft.com/download"; exit 1; }

if curl -sf "$OLLAMA/api/tags" >/dev/null; then echo "▸ Ollama en $OLLAMA"
else echo "  ⚠ Ollama no responde en $OLLAMA: la demo corre solo con datos simulados"; fi

echo "▸ Demo en http://localhost:$PUERTO  (Ctrl+C para detener)"
export Ollama__Url="$OLLAMA"
exec dotnet run -c Release --urls "http://localhost:$PUERTO"
