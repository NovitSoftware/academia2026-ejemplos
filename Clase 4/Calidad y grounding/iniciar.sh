#!/usr/bin/env bash
# Arranca la demo con el Ollama de esta máquina. Ctrl+C la detiene.
#   ./iniciar.sh                       modelo por defecto (qwen3.5:9b)
#   MODELO=gemma4:e2b ./iniciar.sh     otro modelo
#   OLLAMA_URL=http://otra:11434 ./iniciar.sh
set -euo pipefail
cd "$(dirname "$0")"

PUERTO="${PUERTO:-5300}"
export OLLAMA_URL="${OLLAMA_URL:-http://localhost:11434}"
export MODELO="${MODELO:-qwen3.5:9b}"
export EMBEDDINGS="${EMBEDDINGS:-bge-m3}"

command -v dotnet >/dev/null || { echo "✘ Falta el SDK de .NET 10: https://dotnet.microsoft.com/download"; exit 1; }

if ! modelos="$(curl -sf "$OLLAMA_URL/api/tags")"; then
  echo "✘ Ollama no responde en $OLLAMA_URL. Inicialo con: ollama serve"
  exit 1
fi
echo "▸ Ollama en $OLLAMA_URL"
for m in "$MODELO" "$EMBEDDINGS"; do
  [[ "$modelos" == *"\"$m"* ]] || echo "  ⚠ Falta $m: la app lo descarga al iniciar (o: ollama pull $m)"
done

echo "▸ Demo en http://localhost:$PUERTO  (Ctrl+C para detener)"
exec dotnet run -c Release --urls "http://localhost:$PUERTO"
