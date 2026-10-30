#!/usr/bin/env bash
# Inicia la demo local y conserva el control de los tres procesos hasta Ctrl+C.
set -eo pipefail

raiz="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
logs="$raiz/.logs/mesaya"
pids=()
proyectos=()

error() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

ayuda() {
    cat <<'EOF'
Uso: ./iniciar-mesaya.sh

Compila e inicia MesaYa.Api (:5080), MesaYa.Tooling (:5090) y MesaYa.Mcp (:5100).
Requiere Bash, curl, SDK .NET 10 y runtimes ASP.NET Core 8 y 10.
Los SDK .NET 8 y 10 instalados juntos cubren estos requisitos.

Logs: .logs/mesaya/ (un archivo por proyecto; se reemplazan en cada ejecución).
Ctrl+C detiene los tres servicios. Si uno falla, se detienen los restantes.
Ollama se inicia por separado; los modos de demo funcionan sin él.
Se respetan OLLAMA_URL y OLLAMA_MODEL. Las conexiones entre los tres servicios
usan localhost y los puertos indicados arriba.
EOF
}

if [[ "${1:-}" == '--help' || "${1:-}" == '-h' ]]; then
    ayuda
    exit 0
fi
[[ $# -eq 0 ]] || error 'Argumento desconocido. Consultá --help.'

for comando in dotnet curl; do
    command -v "$comando" >/dev/null 2>&1 || error "Falta $comando en PATH."
done
sdks="$(dotnet --list-sdks)"
[[ "$sdks" =~ (^|$'\n')10\. ]] || error 'Instalá el SDK .NET 10 para compilar MesaYa.Mcp.'
runtimes="$(dotnet --list-runtimes)"
for version in 8 10; do
    [[ "$runtimes" == *"Microsoft.AspNetCore.App $version."* ]] ||
        error "Falta el runtime ASP.NET Core $version. Instalá el SDK .NET $version."
done

for puerto in 5080 5090 5100; do
    if (exec 3<>"/dev/tcp/localhost/$puerto") 2>/dev/null; then
        error "El puerto $puerto está ocupado. Detené el servicio que lo usa antes de iniciar la demo."
    fi
done

detener() {
    trap '' INT TERM
    if [[ ${#pids[@]} -gt 0 ]]; then
        printf '\nDeteniendo los servicios de esta ejecución...\n'
        for pid in "${pids[@]}"; do
            kill -TERM "$pid" 2>/dev/null || true
        done
        # Dar tiempo al cierre de ASP.NET; después forzar solo nuestros procesos.
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
dotnet build "$raiz/MesaYaDemo.sln" --configuration Debug --nologo
mkdir -p "$logs"

iniciar() {
    local proyecto="$1" framework="$2" puerto="$3"
    (
        cd -- "$raiz/$proyecto"
        # Ejecutar la DLL evita procesos intermediarios de dotnet run y usa el
        # directorio del proyecto para appsettings.json y los archivos estáticos.
        export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
        export MESAYA_URL=http://localhost:5080 MCP_URL=http://localhost:5100
        exec dotnet "bin/Debug/$framework/$proyecto.dll" --urls "http://localhost:$puerto"
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
        if curl --noproxy '*' --fail --silent --output /dev/null --max-time 2 "$url"; then
            return
        fi
        sleep 0.5
    done
    error "No respondió $url durante el arranque. Revisá los logs en $logs."
}

iniciar MesaYa.Api net8.0 5080
esperar_http http://localhost:5080/health
iniciar MesaYa.Tooling net8.0 5090
iniciar MesaYa.Mcp net10.0 5100
esperar_http http://localhost:5090/
esperar_http http://localhost:5100/

cat <<EOF

MesaYa lista:
  API:      http://localhost:5080/health
  Tooling:  http://localhost:5090
  MCP web:  http://localhost:5100
  MCP:      http://localhost:5100/mcp

Logs: $logs
Ctrl+C detiene los tres servicios.
EOF

while true; do
    verificar_procesos
    sleep 1
done
