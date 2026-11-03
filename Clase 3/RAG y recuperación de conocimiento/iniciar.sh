#!/usr/bin/env bash
# Inicia la demo de RAG de MesaYa y conserva el control de la app hasta Ctrl+C.
set -eo pipefail

raiz="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
proyecto="MesaYa.Rag"
puerto=5290
logs="$raiz/.logs"
ollama_url="${OLLAMA_URL:-http://localhost:11434}"
con_reranker=1
reingestar=0
pid=""

error() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

aviso() {
    printf 'Aviso: %s\n' "$*" >&2
}

ayuda() {
    cat <<'EOF'
Uso: ./iniciar.sh [--sin-reranker] [--reingestar]

Levanta PostgreSQL con pgvector (:5433) y el reranker (:7997) con Docker Compose,
compila MesaYa.Rag, carga el corpus si la base está vacía e inicia la app en :5290.

  --sin-reranker  no levanta el contenedor del reranker (la primera vez descarga ~4 GB).
                  La app funciona igual; la etapa Rerankear avisa y usa el orden de RRF.
  --reingestar    vuelve a cortar y embeber el corpus aunque la base ya tenga datos.
                  Hace falta después de cambiar corpus/ o los chunkers.

Requiere Bash, curl, Docker con Compose, SDK .NET 10 y Ollama con los modelos
bge-m3, qwen3.5:9b y qwen3.5:2b. Se respeta OLLAMA_URL.
Sin Ollama, la app arranca y el modo guion reproduce los turnos grabados en guion/.

Log de la app: .logs/mesaya-rag.log (se reemplaza en cada ejecución).
Ctrl+C detiene la app. Los contenedores siguen corriendo: docker compose --profile reranker down
EOF
}

for argumento in "$@"; do
    case "$argumento" in
        -h | --help) ayuda; exit 0 ;;
        --sin-reranker) con_reranker=0 ;;
        --reingestar) reingestar=1 ;;
        *) error "Argumento desconocido: $argumento. Consultá --help." ;;
    esac
done

for comando in dotnet curl docker; do
    command -v "$comando" >/dev/null 2>&1 || error "Falta $comando en PATH."
done
docker compose version >/dev/null 2>&1 || error 'Falta Docker Compose (docker compose).'
docker info >/dev/null 2>&1 || error 'Docker no responde. ¿Está iniciado y tu usuario tiene permisos?'
[[ "$(dotnet --list-sdks)" =~ (^|$'\n')10\. ]] || error 'Instalá el SDK .NET 10.'

if (exec 3<>"/dev/tcp/localhost/$puerto") 2>/dev/null; then
    error "El puerto $puerto está ocupado. Detené el servicio que lo usa antes de iniciar la demo."
fi

# Ollama: sin él, la app arranca igual y se puede usar el modo guion.
ollama_ok=0
if modelos="$(curl --noproxy '*' --silent --fail --max-time 3 "$ollama_url/api/tags")"; then
    ollama_ok=1
    for modelo in bge-m3 qwen3.5:9b qwen3.5:2b; do
        [[ "$modelos" == *"\"$modelo"* ]] || aviso "Falta el modelo $modelo en Ollama. Instalalo con: ollama pull $modelo"
    done
else
    aviso "Ollama no responde en $ollama_url. La app arranca, pero solo funciona el modo guion."
fi

cd -- "$raiz"
printf 'Iniciando PostgreSQL con pgvector...\n'
docker compose up -d --wait postgres

if [[ $con_reranker -eq 1 ]]; then
    printf 'Iniciando el reranker (la primera vez descarga el modelo, puede tardar varios minutos)...\n'
    docker compose --profile reranker up -d reranker
fi

printf 'Compilando %s...\n' "$proyecto"
dotnet build "$raiz/MesaYaRag.slnx" --configuration Debug --nologo -v q
dll="bin/Debug/net10.0/$proyecto.dll"

export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
export Rag__OllamaUrl="$ollama_url"

chunks="$(docker compose exec -T postgres psql -U mesaya -d mesaya_rag -tAc 'SELECT count(*) FROM chunks' 2>/dev/null || echo 0)"
if [[ $reingestar -eq 1 || "$chunks" == "0" ]]; then
    [[ $ollama_ok -eq 1 ]] || error 'Para cargar el corpus hace falta Ollama con bge-m3.'
    printf 'Cargando el corpus (cortar, embeber e indexar)...\n'
    (cd -- "$raiz/$proyecto" && dotnet "$dll" ingestar)
else
    printf 'La base ya tiene %s chunks. Usá --reingestar para recargar el corpus.\n' "$chunks"
fi

detener() {
    trap '' INT TERM
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        printf '\nDeteniendo la app...\n'
        kill -TERM "$pid" 2>/dev/null || true
        for ((intento = 0; intento < 50; intento++)); do
            kill -0 "$pid" 2>/dev/null || break
            sleep 0.1
        done
        kill -KILL "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    fi
}
trap detener EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "$logs"
(
    cd -- "$raiz/$proyecto"
    # Ejecutar la DLL desde el directorio del proyecto usa sus appsettings.json y wwwroot.
    exec dotnet "$dll" --urls "http://localhost:$puerto"
) >"$logs/mesaya-rag.log" 2>&1 &
pid="$!"
printf 'Iniciando %s (PID %s)...\n' "$proyecto" "$pid"

for ((intento = 0; intento < 60; intento++)); do
    if ! kill -0 "$pid" 2>/dev/null; then
        printf 'La app se detuvo durante el arranque. Últimas líneas del log:\n' >&2
        tail -n 20 "$logs/mesaya-rag.log" >&2
        exit 1
    fi
    curl --noproxy '*' --fail --silent --output /dev/null --max-time 2 "http://localhost:$puerto/" && break
    sleep 0.5
done
curl --noproxy '*' --fail --silent --output /dev/null --max-time 2 "http://localhost:$puerto/" ||
    error "La app no respondió en http://localhost:$puerto. Revisá $logs/mesaya-rag.log."

reranker_estado='apagado (--sin-reranker)'
if [[ $con_reranker -eq 1 ]]; then
    if curl --noproxy '*' --fail --silent --output /dev/null --max-time 2 http://localhost:7997/health; then
        reranker_estado='listo en http://localhost:7997'
    else
        reranker_estado='arrancando (docker compose logs -f reranker); mientras tanto se usa RRF'
    fi
fi

cat <<EOF

MesaYa RAG lista:
  App:       http://localhost:$puerto
  Postgres:  localhost:5433 (mesaya / mesaya, base mesaya_rag)
  Reranker:  $reranker_estado
  Ollama:    $([[ $ollama_ok -eq 1 ]] && echo "$ollama_url" || echo "sin conexión: usá el modo guion")

Consejo: la app usa tres modelos de Ollama. Para que no se descarguen entre consultas,
iniciá Ollama con OLLAMA_MAX_LOADED_MODELS=3.

Log: $logs/mesaya-rag.log
Ctrl+C detiene la app; los contenedores siguen corriendo.
EOF

while kill -0 "$pid" 2>/dev/null; do sleep 1; done
estado=0
wait "$pid" || estado=$?
printf 'La app se detuvo (código %s). Últimas líneas del log:\n' "$estado" >&2
tail -n 20 "$logs/mesaya-rag.log" >&2
exit 1
