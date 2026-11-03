# MesaYa · Rayos X de la recuperación

Demo de la presentación **Módulo 2.2 · RAG y recuperación de conocimiento**. Un asistente de MesaYa responde a partir de la base de conocimiento de la cadena: políticas para clientes, manuales del personal y documentos de gerencia. Al lado del chat, la app muestra cada etapa del pipeline: qué consulta se buscó, en qué posición quedó cada fragmento, qué decidió el reranker y qué texto recibió el modelo.

> Idea para llevar (slide 17): *recuperar mucho, seleccionar poco*.

Todo corre en la máquina: PostgreSQL con pgvector y el reranker en Docker, y los modelos en Ollama.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Compilar y ejecutar la app | SDK .NET 10 |
| Base de datos y reranker | Docker con Compose. El reranker descarga unos 4 GB la primera vez |
| Embeddings, respuestas y reescritura | Ollama con `bge-m3`, `qwen3.5:9b` y `qwen3.5:2b` |
| Script de inicio | Bash y curl (Linux, macOS o WSL). Puertos 5290, 5433 y 7997 libres |

```bash
ollama pull bge-m3
```

```bash
ollama pull qwen3.5:9b
```

```bash
ollama pull qwen3.5:2b
```

La app usa los tres modelos en cada turno. Para que Ollama no los descargue de memoria entre consultas, inicialo con `OLLAMA_MAX_LOADED_MODELS=3`. Si no, el primer embedding después de responder puede tardar varios segundos.

## Iniciar

```bash
./iniciar.sh
```

El script levanta los contenedores, compila y carga el corpus si la base está vacía. Después inicia la app en [http://localhost:5290](http://localhost:5290). Opciones:

- `--sin-reranker`: no levanta el contenedor del reranker. La etapa Rerankear avisa y se usa el orden de RRF.
- `--reingestar`: vuelve a cortar y embeber el corpus. Hace falta después de cambiar `corpus/` o los chunkers.

**Ctrl+C** detiene la app; los contenedores siguen corriendo. Para apagarlos:

```bash
docker compose --profile reranker down
```

Con `down -v` también se borran la base y el modelo descargado.

## Estructura

```text
corpus/                    26 documentos de MesaYa en Markdown con front-matter (versión, vigencia, audiencia, tenant)
evaluacion/preguntas.json  15 preguntas con el fragmento esperado y los documentos prohibidos
db/                        esquema (slide 09), índices, IDF para palabras clave y funciones de búsqueda
guion/                     turnos grabados para el modo guion
MesaYa.Rag/                app Blazor Server .NET 10
  Ingesta/                 lector del corpus, chunkers, embeddings e ingestor
  Busqueda/                búsqueda semántica, por palabras clave e híbrida con RRF; reranker
  Generacion/              armado del prompt, respuesta con citas y reescritura de consulta
  Pipeline/                orquestación de un turno, preguntas de ejemplo y modo guion
  Evaluacion/              hit rate@5 y MRR por configuración
  Espacio/                 proyección 2D de los embeddings (PCA)
docker-compose.yml         pgvector (:5433) y reranker bge-reranker-v2-m3 con Infinity (:7997)
iniciar.sh                 inicio de toda la demo
PROPUESTA.md               diseño de la demo, escenarios y guion de clase
```

## La pantalla

**Barra superior.** Controles que cambian la recuperación del próximo turno:

- Perfil (cliente, personal, gerencia) y tenant (Argentina, Uruguay).
- Estrategia de chunking y modo de búsqueda.
- Filtro de permisos: pre-filtering, post-filtering o sin filtro.
- Candidatos por lista y cuántos fragmentos van al contexto.
- Solo vigentes, reescritura de consulta, reranker y modo guion.

**Chat.** Permite cargar cualquiera de las preguntas de evaluación con su perfil, su historial y una nota sobre qué esperar. Hacé clic en un mensaje anterior para ver su recorrido en las pestañas.

**Pestañas.**

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Pipeline | Las 4 etapas con entrada, salida y duración; la consulta reescrita, la `tsquery`, los filtros y el SQL | 02, 13 |
| Ranking | Posición en semántica y en palabras clave, cálculo de RRF, puntaje del reranker y cuánto subió o bajó cada fragmento | 10, 11, 14 |
| Espacio | Los embeddings del corpus en 2D con la consulta y los fragmentos elegidos | 04 |
| Chunks | El mismo documento cortado con cada estrategia, con los fragmentos recuperados marcados | 05, 06 |
| Contexto | El pedido exacto que recibe el modelo, con tokens estimados y reales | 07 |
| Evaluación | hit rate@5, MRR y violaciones de permisos de cada configuración, y la matriz pregunta × configuración | 16 |

## Escenarios para la clase

Cada escenario está en el selector de ejemplos. Se corre dos veces con la misma pregunta: una con el control apagado y otra prendido. El detalle está en [PROPUESTA.md](PROPUESTA.md).

| Escenario | Ejemplo | Qué cambiar |
|---|---|---|
| A · Embeddings | q01, q12 | Pestaña Espacio. q12 muestra que las negaciones se representan mal |
| B · Chunking | q02 | Chunking fijo 150 frente a estructural, en la pestaña Chunks |
| C · Semántica, palabras clave e híbrida | q03, q04 | Modo de búsqueda. q03 gana con palabras clave; q04 con semántica |
| D · Reescritura | q05 | Reescribir consulta. Sin reescritura, el documento correcto queda 6.º |
| E · Reranking | q06, q15 | Reranker. q15 es un caso en el que el cross-encoder se equivoca |
| F · Permisos | q07, q08, q10 | Filtro de permisos y perfil. Con post-filtering quedan 2 de 5 fragmentos |
| G · Vigencia | q09 | Solo vigentes. Sin el filtro entra la política de 2021 |
| H · Evaluación | — | Pestaña Evaluación, botón Correr evaluación |

## Modo guion

Plan B por si Ollama o el reranker fallan en vivo. Cada turno que termina bien se guarda en `guion/` como JSON. Queda identificado por la pregunta, el historial y los controles de la barra. Con **Modo guion** prendido, la misma pregunta con los mismos controles se reproduce etapa por etapa, sin llamar a Ollama ni al reranker. La respuesta se marca con la fecha de la grabación.

Para preparar la clase, corré el guion completo una vez en vivo con el modo apagado. Si querés que las grabaciones viajen con el repositorio, commiteá `guion/`.

## Comandos de consola

Desde `MesaYa.Rag/`, con la base levantada:

| Comando | Para qué |
|---|---|
| `dotnet run -- ingestar` | Corta, embebe e indexa el corpus |
| `dotnet run -- chunks pol-cancel-v3` | Muestra cómo corta cada estrategia un documento. No usa Ollama ni la base |
| `dotnet run -- buscar "La tablet me tira E-4012" --perfil personal --rerank` | Una búsqueda con el ranking en texto. `--help` lista las opciones |
| `dotnet run -- evaluar` | La tabla de métricas completa. `--sin-rerank` omite las configuraciones lentas |

Para mostrar el esquema y las funciones en vivo:

```bash
docker compose exec postgres psql -U mesaya -d mesaya_rag
```

## Decisiones y límites

- **Tokens estimados.** Ollama no expone el tokenizador por API. Los tamaños de chunk se estiman con 4 caracteres por token; el total real de cada respuesta sale de `prompt_eval_count`.
- **BM25 simplificado.** `ts_rank_cd` de PostgreSQL no pondera la rareza de los términos, y «tablet» le ganaba a «E-4012». La búsqueda por palabras clave suma el IDF de los términos de la consulta presentes en el chunk (`db/idf.sql`). No satura por frecuencia ni normaliza por largo.
- **Stemming.** La configuración `spanish` reduce «plata» y «platos» a la misma raíz, y descarta la «e» de «E-4012» por ser una palabra vacía. Las dos cosas se ven en la `tsquery` de la pestaña Pipeline.
- **Reranker en CPU.** `bge-reranker-v2-m3` tarda unos 4 s con los 20 candidatos que pasan por RRF (slide 11). Con 50 candidatos tardaba entre 8 y 13 s.
- **Corpus chico.** Con 26 documentos casi todas las configuraciones aciertan casi todo y las diferencias son chicas. Se dejó así a propósito: la lección es medir en el corpus propio antes de elegir. Los resultados medidos están en PROPUESTA.md.
- **Proyección 2D.** Dos ejes de PCA conservan alrededor del 15 % de la variación de 1024 dimensiones. La distancia real es la de la pestaña Ranking. La proyección se cachea por estrategia: reiniciá la app después de reingestar.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «No se pudo conectar al reranker» en la etapa Rerankear | El contenedor no está listo o no se levantó. Revisá `docker compose logs -f reranker` |
| La primera respuesta tarda más de 10 s | Ollama está cargando el modelo. Ver `OLLAMA_MAX_LOADED_MODELS` arriba |
| «No hay chunks de la estrategia…» en Espacio | La base está vacía: `./iniciar.sh --reingestar` |
| «No hay una grabación de esta pregunta…» | En modo guion, la pregunta, el historial y los controles tienen que coincidir con una corrida grabada |
| `Falta el modelo … en Ollama` | `ollama pull <modelo>` |
