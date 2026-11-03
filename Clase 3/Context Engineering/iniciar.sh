#!/usr/bin/env bash
# Inicia la demo de la Clase 3 y conserva el control de los dos procesos hasta Ctrl+C.
set -eo pipefail

raiz="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
logs="$raiz/.logs"
pids=()
proyectos=()

error() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

if [[ "${1:-}" == '--help' || "${1:-}" == '-h' ]]; then
    cat <<'EOF'
Uso: ./iniciar.sh

Compila e inicia MesaYa.Api (:5180) y MesaYa.Contexto (:5190).
Requiere Bash, curl y el SDK .NET 10. Ollama se inicia por separado.
Se respetan OLLAMA_URL y OLLAMA_MODEL (modelo inicial, por defecto qwen3.5:9b).

Logs: .logs/ (un archivo por proyecto; se reemplazan en cada ejecución).
Ctrl+C detiene los dos servicios. Si uno falla, se detiene el otro.
EOF
    exit 0
fi
[[ $# -eq 0 ]] || error 'Argumento desconocido. Consultá --help.'

for comando in dotnet curl; do
    command -v "$comando" >/dev/null 2>&1 || error "Falta $comando en PATH."
done
[[ "$(dotnet --list-sdks)" =~ (^|$'\n')10\. ]] || error 'Instalá el SDK .NET 10.'

for puerto in 5180 5190; do
    if (exec 3<>"/dev/tcp/localhost/$puerto") 2>/dev/null; then
        error "El puerto $puerto está ocupado. Detené el servicio que lo usa antes de iniciar la demo."
    fi
done

detener() {
    trap '' INT TERM
    if [[ ${#pids[@]} -gt 0 ]]; then
        printf '\nDeteniendo los servicios...\n'
        for pid in "${pids[@]}"; do kill -TERM "$pid" 2>/dev/null || true; done
        for ((intento = 0; intento < 50; intento++)); do
            activos=0
            for pid in "${pids[@]}"; do
                if kill -0 "$pid" 2>/dev/null; then activos=1; fi
            done
            [[ $activos -eq 1 ]] || break
            sleep 0.1
        done
        for pid in "${pids[@]}"; do
            if kill -0 "$pid" 2>/dev/null; then kill -KILL "$pid" 2>/dev/null || true; fi
            wait "$pid" 2>/dev/null || true
        done
    fi
}
trap detener EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

printf 'Compilando la solución...\n'
dotnet build "$raiz/MesaYaContexto.sln" --configuration Debug --nologo
mkdir -p "$logs"

iniciar() {
    local proyecto="$1" puerto="$2"
    (
        cd -- "$raiz/$proyecto"
        export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
        export MESAYA_URL=http://localhost:5180
        exec dotnet "bin/Debug/net10.0/$proyecto.dll" --urls "http://localhost:$puerto"
    ) >"$logs/$proyecto.log" 2>&1 &
    pids+=("$!")
    proyectos+=("$proyecto")
    printf 'Iniciando %s (PID %s)...\n' "$proyecto" "$!"
}

verificar_procesos() {
    local indice estado
    for indice in "${!pids[@]}"; do
        if ! kill -0 "${pids[$indice]}" 2>/dev/null; then
            estado=0
            wait "${pids[$indice]}" || estado=$?
            printf 'Se detuvo %s (código %s). Últimas líneas del log:\n' "${proyectos[$indice]}" "$estado" >&2
            tail -n 20 "$logs/${proyectos[$indice]}.log" >&2
            exit 1
        fi
    done
}

esperar_http() {
    local url="$1" intento
    for ((intento = 0; intento < 60; intento++)); do
        verificar_procesos
        if curl --noproxy '*' --fail --silent --output /dev/null --max-time 2 "$url"; then return; fi
        sleep 0.5
    done
    error "No respondió $url durante el arranque. Revisá los logs en $logs."
}

iniciar MesaYa.Api 5180
esperar_http http://localhost:5180/health
iniciar MesaYa.Contexto 5190
esperar_http http://localhost:5190/

ollama_url="${OLLAMA_URL:-http://localhost:11434}"
if curl --noproxy '*' --fail --silent --output /dev/null --max-time 2 "$ollama_url/api/tags"; then
    estado_ollama="disponible en $ollama_url"
else
    estado_ollama="NO responde en $ollama_url (los escenarios necesitan Ollama)"
fi

cat <<EOF

MesaYa · Context Lab listo:
  Demo:    http://localhost:5190
  API:     http://localhost:5180/health
  Ollama:  $estado_ollama

Ensayo sin UI:  curl http://localhost:5190/ensayo/olvido/sin
Logs: $logs
Ctrl+C detiene los dos servicios.
EOF

while true; do
    verificar_procesos
    sleep 1
done
