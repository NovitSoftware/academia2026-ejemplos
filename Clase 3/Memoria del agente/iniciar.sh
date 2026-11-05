#!/usr/bin/env bash
# Levanta la demo con Docker. Uso:
#   ./iniciar.sh                 usa el Ollama de la máquina si está corriendo; si no, el del contenedor
#   ./iniciar.sh --ollama-docker fuerza el Ollama del contenedor
#   ./iniciar.sh --detener       apaga los contenedores
set -euo pipefail
cd "$(dirname "$0")"
PUERTO=5400
export UID GID="$(id -g)"

if [[ "${1:-}" == "--detener" ]]; then
  docker compose -f docker-compose.yml -f docker-compose.ollama-local.yml down --remove-orphans
  exit 0
fi

if [[ "${1:-}" != "--ollama-docker" ]] && curl -sf http://localhost:11434/api/tags >/dev/null; then
  echo "▸ Uso el Ollama de esta máquina (localhost:11434)."
  docker compose -f docker-compose.yml -f docker-compose.ollama-local.yml up -d --build app
else
  echo "▸ Levanto Ollama en Docker. La primera vez descarga los modelos: puede tardar."
  docker compose up -d --build
fi

echo -n "▸ Esperando la app"
for _ in $(seq 1 90); do
  if curl -sf "http://localhost:$PUERTO/api/estado" >/dev/null; then
    echo; echo "✔ Listo: http://localhost:$PUERTO"
    echo "  El indicador de arriba a la derecha muestra la descarga de modelos."
    exit 0
  fi
  echo -n "."; sleep 2
done
echo; echo "✘ La app no respondió. Revisá: docker compose logs app"; exit 1
