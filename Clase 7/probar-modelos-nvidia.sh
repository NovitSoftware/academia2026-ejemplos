#!/usr/bin/env bash
# Prueba qué modelos del catálogo de NVIDIA responden con tu clave y si hacen tool calling.
# Uso: ./probar-modelos-nvidia.sh
# La clave se busca igual que en las demos: primero la variable de entorno NVIDIA_API_KEY y, si no está,
# Proveedores:nvidia:ApiKey en appsettings.Local.json o appsettings.json de alguna de las dos demos.
# No muestra la clave: solo el código HTTP y si el modelo llamó a la tool.
set -uo pipefail
export LC_NUMERIC=C  # printf con punto decimal, sin importar el idioma del sistema
cd "$(dirname "$0")"

if [[ -z "${NVIDIA_API_KEY:-}" ]]; then
  for f in "Proveedores y modelos/appsettings.Local.json" "Capability Matrix y routing/appsettings.Local.json" \
           "Proveedores y modelos/appsettings.json" "Capability Matrix y routing/appsettings.json"; do
    [[ -f "$f" ]] || continue
    NVIDIA_API_KEY=$(python3 -I -c "import json,sys; print(json.load(open(sys.argv[1])).get('Proveedores',{}).get('nvidia',{}).get('ApiKey',''))" "$f")
    [[ "$NVIDIA_API_KEY" == "nvapi-..." ]] && NVIDIA_API_KEY=""  # el valor de ejemplo no cuenta
    [[ -n "$NVIDIA_API_KEY" ]] && { echo "▸ Clave de NVIDIA: $f"; break; }
  done
else
  echo "▸ Clave de NVIDIA: variable de entorno"
fi
if [[ -z "${NVIDIA_API_KEY:-}" ]]; then
  echo "✘ No encontré la clave: exportá NVIDIA_API_KEY o creá appsettings.Local.json en una de las demos."
  exit 1
fi

URL="https://integrate.api.nvidia.com/v1/chat/completions"
MODELOS=(
  nvidia/nemotron-3.5-bolt-e4b
  nvidia/nemotron-3.5-lightning-30b-a3b
  nvidia/nemotron-3-nano-omni-30b-a3b-reasoning
  openai/gpt-oss-20b
  google/gemma-4-31b-it
  deepseek-ai/deepseek-v4.1-flash
  nvidia/nemotron-3-super-120b-a12b
  mistralai/mistral-large-4-1t-a52b
  nvidia/nemotron-3-ultra-550b-a55b
  google/gemma-3-4b-it
  mistralai/mistral-7b-instruct-v0.3
)
TOOL='{"type":"function","function":{"name":"cancelar_reserva","description":"Cancela una reserva por su código.","parameters":{"type":"object","properties":{"codigo":{"type":"string"}},"required":["codigo"]}}}'

printf "%-42s %-6s %-6s %s\n" "MODELO" "CHAT" "TOOLS" "SEGUNDOS"
for m in "${MODELOS[@]}"; do
  inicio=$(date +%s.%N)
  chat=$(curl -s -m 120 -o /dev/null -w "%{http_code}" "$URL" -H "Authorization: Bearer $NVIDIA_API_KEY" -H "content-type: application/json" \
    -d "{\"model\":\"$m\",\"messages\":[{\"role\":\"user\",\"content\":\"Decí hola en tres palabras.\"}],\"max_tokens\":20}")
  tools="-"
  if [[ "$chat" == "200" ]]; then
    r=$(curl -s -m 120 "$URL" -H "Authorization: Bearer $NVIDIA_API_KEY" -H "content-type: application/json" \
      -d "{\"model\":\"$m\",\"messages\":[{\"role\":\"user\",\"content\":\"Cancelá la reserva R-2291\"}],\"tools\":[$TOOL],\"max_tokens\":200}")
    if grep -q '"tool_calls"' <<<"$r" && grep -q 'cancelar_reserva' <<<"$r"; then tools="sí"; else tools="no"; fi
  fi
  printf "%-42s %-6s %-6s %.1f\n" "$m" "$chat" "$tools" "$(echo "$(date +%s.%N) - $inicio" | bc)"
done
