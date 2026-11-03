# Propuesta · Demo RAG y recuperación de conocimiento con MesaYa

Demo para acompañar la presentación **Módulo 2.2 · RAG y recuperación de conocimiento**. Se usa el dominio de MesaYa de las clases anteriores, pero esta vez el agente no consulta una API: **responde a partir de la base de conocimiento de la cadena**, que incluye políticas para clientes, manuales del personal y documentación del sistema de reservas.

> Idea para llevar (slide 17): *recuperar mucho, seleccionar poco*. La demo tiene que mostrar con números que la búsqueda híbrida mejora el recall, el reranking mejora la precisión y la metadata controla la vigencia y los permisos.

## Decisiones tomadas

- **Proyecto independiente** dentro de `Clase 3/RAG y recuperación de conocimiento/`, sin referencias a la Clase 2 ni a la demo de Context Engineering.
- **No usa la API de MesaYa.** El foco es la recuperación, no las tools. Así no hay un proceso más que levantar.
- **PostgreSQL + pgvector** en Docker, con el esquema del slide 09 casi literal.
- **Todo local:** embeddings y generación con Ollama, y reranker en un contenedor aparte.
- **Puertos propios** para poder levantarla junto con las otras demos.

## Estructura

```text
RAG y recuperación de conocimiento/
  corpus/                    documentos Markdown de MesaYa (ver sección 1)
  evaluacion/preguntas.json  10–15 preguntas con el documento y la sección esperados
  db/schema.sql              esquema e índices (slide 09)
  MesaYa.Rag/                Blazor Server .NET 10: ingesta, búsqueda y "Rayos X de la recuperación"  (:5290)
  docker-compose.yml         postgres+pgvector (:5433) y reranker (:7997)
  iniciar.sh                 levanta contenedores, espera salud, ingesta si hace falta y arranca la app
  README.md
```

## 1 · Corpus de MesaYa

Unos 25 documentos Markdown con encabezados reales (`#`, `##`, `###`), front-matter con metadata y redactados para que **cada escenario tenga su trampa**.

| Área | Audiencia | Documentos de ejemplo | Para qué escenario |
|---|---|---|---|
| Atención al cliente | `clientes` | Política de cancelación **v1 (2021, no vigente)** y **v3 (2026, vigente)**, reembolso de señas, alérgenos y celíacos, menores y mascotas, eventos privados, accesibilidad, estacionamiento por sucursal | Embeddings, obsolescencia, chunking |
| Sistema de reservas | `personal` | Manual de la tablet de recepción con **códigos de error** (`E-4012` doble reserva, `E-4013` seña rechazada, `E-5001` sin conexión), cierre de caja, procedimiento de no-show | Palabras clave contra semántica |
| Recursos humanos | `personal` | Licencias y **vacaciones del personal de planta** y **del personal contratado** (en documentos o secciones distintos), francos y feriados, uniforme | Query rewriting |
| Gerencia | `gerencia` | Escala salarial, márgenes por proveedor, sanciones | Permisos |
| Ruido | `clientes` | Historia de la marca, horario de la cafetería de oficina central, newsletter | Mostrar qué queda lejos en el espacio de embeddings |

Front-matter de ejemplo:

```yaml
---
document_id: pol-cancel-v3
titulo: Política de cancelación
version: 3
fecha: 2026-08-01
estado: vigente        # vigente | deprecated
audiencia: [clientes, personal]
tenant_id: mesaya-ar   # segundo tenant 'mesaya-uy' con su propia política, para el filtro obligatorio
---
```

La trampa de chunking va en la política de cancelación: la regla «sin cargo hasta 2 h antes» está en un párrafo y la excepción «**salvo** eventos privados y grupos de más de 8, que requieren 72 h» está en el siguiente. Con chunks chicos y sin solapamiento, la excepción queda en otro chunk.

## 2 · Esquema (slide 09)

Igual al de la slide, con la metadata del slide 07 en columnas para filtrar sin `jsonb`:

```sql
CREATE TABLE chunks (
  id           bigserial PRIMARY KEY,
  tenant_id    text  NOT NULL,
  estrategia   text  NOT NULL,       -- 'fijo-150' | 'recursivo-512' | 'estructural' | 'contextual'
  document_id  text  NOT NULL,
  seccion      text,                 -- ruta: 'Política de cancelación > Excepciones'
  parent_id    bigint,               -- para parent–child
  vigente      boolean NOT NULL,
  audiencia    text[] NOT NULL,
  content      text  NOT NULL,
  embedding    vector(1024) NOT NULL,
  tsv tsvector GENERATED ALWAYS AS (to_tsvector('spanish', content)) STORED
);
CREATE INDEX ON chunks USING hnsw (embedding vector_cosine_ops);
CREATE INDEX ON chunks USING gin (tsv);
```

Cada estrategia de chunking se indexa **en paralelo en la misma tabla** (columna `estrategia`) para poder compararlas en vivo sin reingestar. El índice HNSW se crea después de la carga, como dice la slide.

## 3 · MesaYa.Rag (Blazor Server .NET 10)

### Componentes

| Componente | Responsabilidad |
|---|---|
| `Ingestor` | Lee el corpus, parsea front-matter y encabezados, genera chunks con cada estrategia y guarda embeddings |
| `Chunkers` | `FijoChunker` (150 tokens, sin solapamiento), `RecursivoChunker` (512, 15 % de solapamiento), `EstructuralChunker` (por sección), `ContextualChunker` (antepone «Documento › Sección» antes del embedding), y parent–child |
| `Embeddings` | `bge-m3` vía Ollama `/api/embed` (1024 dimensiones, coincide con `vector(1024)`) |
| `Buscador` | Consulta semántica, por palabras clave e híbrida con RRF (SQL del slide 11), con `tenant_id` y `audiencia` como filtro del sistema (pre, post o apagado, para el escenario F). Palabras clave usa un BM25 simplificado (suma del IDF de `db/idf.sql`), porque `ts_rank_cd` no pondera la rareza de los términos y «tablet» le ganaba a «E-4012» |
| `Reescritor` | Modelo chico de Ollama que reescribe la consulta con el historial; opcional multi-query o HyDE |
| `Reranker` | Cliente HTTP del contenedor con `bge-reranker-v2-m3` (cross-encoder real) |
| `Generador` | Responde con los top 5 delimitados en `<documento id seccion fecha>` y exige citas |
| `Evaluador` | Corre las preguntas de `evaluacion/preguntas.json` y calcula hit rate@5 y MRR por configuración |

### Pantalla: dos columnas

**Izquierda · Chat** del asistente de MesaYa, con un selector de **perfil** (cliente / personal / gerencia) y de **tenant** (AR / UY).

**Derecha · Rayos X de la recuperación**, con pestañas:

| Pestaña | Qué muestra | Slide |
|---|---|---|
| **Pipeline** | Las etapas en fila (reescribir → semántica → palabras clave → RRF → reranker → generar) con lo que entra y sale de cada una y su latencia | 02, 13 |
| **Espacio** | Proyección 2D (PCA) de los embeddings reales del corpus con la consulta marcada: lo cercano en significado queda cerca | 04 |
| **Chunks** | El mismo documento cortado con cada estrategia, lado a lado, con los chunks recuperados resaltados | 05–06 |
| **Ranking** | Tabla con posición semántica, posición por palabras clave, cálculo `1/(60+r)`, puntaje RRF y posición después del reranker, con flechas de subida y bajada | 10–11, 14 |
| **Contexto** | El prompt exacto que recibe el modelo, con los documentos delimitados y su metadata | 07 |
| **Evaluación** | Tabla de hit rate@5 y MRR para cada configuración y lista de preguntas falladas | 16 |

### Controles

- **Estrategia de chunking**: fijo-150 / recursivo-512 / estructural / contextual / parent–child.
- **Modo de búsqueda**: semántica / palabras clave / híbrida RRF.
- **Reescritura de consulta**: sí / no (y multi-query o HyDE como variante).
- **Reranker**: sí / no, con candidatos (20–100) y top final (3–10).
- **Filtro de vigencia**: sí / no.
- **Filtro de permisos**: pre-filtering / post-filtering / apagado.
- **`ef_search`** de HNSW (slide 09), para mostrar recall contra latencia.
- **Modo guion**: reproduce respuestas grabadas sin llamar a Ollama ni al reranker, como plan B.

## 4 · Escenarios

Como en la demo de Context Engineering, cada escenario se corre **dos veces con la misma pregunta**: con el control apagado y prendido.

### A · Significado como distancia (slide 04) ★
- **Pregunta:** «¿cómo hago si quiero que me devuelvan la seña?»
- **Pestaña Espacio:** la consulta cae junto a *Reembolso de señas* y *Política de cancelación*, lejos de *Horario de la cafetería* y *Vacaciones del personal*.
- **Variante límite:** «¿qué platos **no** tienen gluten?» frente a «¿qué platos tienen gluten?» quedan casi en el mismo punto (las negaciones se representan mal).

### B · Chunks chicos frente a estructurales (slides 05–06) ★
- **Pregunta:** «Somos 12, ¿hasta cuándo puedo cancelar sin cargo?»
- **Fijo-150:** recupera «sin cargo hasta 2 h antes» y el modelo responde mal, porque la excepción para grupos de más de 8 quedó en otro chunk.
- **Estructural o parent–child:** recupera la sección completa y responde «72 h».
- **Contextual:** se muestra cómo el prefijo «Política de cancelación › Excepciones» acerca el chunk a la consulta.

### C · Semántica, palabras clave e híbrida (slides 10–11) ★
- **Pregunta 1 (personal):** «La tablet me tira E-4012, ¿qué hago?» → la semántica trae documentos genéricos de errores; las palabras clave aciertan.
- **Pregunta 2 (cliente):** «Quiero que me reintegren la plata de la reserva» → las palabras clave no encuentran «reembolso de seña»; la semántica acierta.
- **Híbrida RRF:** acierta las dos. La pestaña **Ranking** muestra las posiciones y el cálculo.

### D · Query rewriting (slide 15) ★
- **Perfil personal.** Primer turno: «¿Cuántos días de vacaciones tiene el personal de planta?». Segundo turno: «¿Y para los contratados?».
- **Sin reescritura:** busca la frase literal y recupera cualquier cosa.
- **Con reescritura:** la consulta pasa a «política de vacaciones para personal contratado de MesaYa» y el reranker la ordena primera.

### E · Reranking (slides 13–14)
- **Pregunta:** «¿Puedo ir con mi perro a la terraza de Palermo?»
- **Sin reranker:** el top 5 por RRF mezcla mascotas, terraza y Palermo de documentos distintos.
- **Con cross-encoder sobre 50 candidatos:** sube al primer lugar la sección que responde las tres cosas juntas. La pestaña **Ranking** muestra cuánto se movió cada chunk.

### F · Permisos y pre- contra post-filtering (slide 07)
- **Perfil cliente:** «¿Cuánto gana un mozo en MesaYa?»
- **Filtro apagado:** entra la escala salarial y el modelo la cuenta.
- **Post-filtering:** se busca el top 5 y después se filtra; quedan 0 resultados aunque había documentos permitidos más abajo.
- **Pre-filtering:** el filtro está en el `WHERE`; el documento nunca entra al contexto.
- **Tenant:** el mismo cliente con tenant UY recibe la política de cancelación uruguaya.

### G · Vigencia (slide 07)
- **Pregunta:** «Si cancelo 3 horas antes, ¿me cobran?»
- **Sin filtro:** aparece la política v1 de 2021 («24 h») y el modelo responde «sí» o mezcla las dos.
- **Con `estado = vigente`:** responde «no» con la v3 («hasta 2 h antes») y la cita.
- **Con tenant UY:** la misma pregunta responde «sí», porque en Uruguay el plazo es de 4 h.

### H · Evaluación (slide 16) ★
- Las 15 preguntas de `evaluacion/preguntas.json`, que incluyen las de los escenarios anteriores.
- Una sola corrida llena la tabla (pestaña **Evaluación** o `dotnet run -- evaluar`). Resultados medidos con `bge-m3` y `bge-reranker-v2-m3` en CPU:

| Configuración | hit rate@5 | MRR | Tiempo |
|---|---|---|---|
| Semántica · fijo-150 | 100 % | 0,85 | 2 s |
| Semántica · estructural | 93 % | 0,82 | 1 s |
| Palabras clave · estructural | 86 % | 0,60 | 0,1 s |
| Híbrida RRF · estructural | 93 % | 0,81 | 2 s |
| Híbrida RRF · contextual | 93 % | 0,85 | 1 s |
| Híbrida RRF + reranker | 93 % | 0,82 | 85 s |
| Híbrida RRF + reranker + reescritura | 100 % | 0,84 | 96 s |

**Lectura para la clase.** Con 26 documentos casi todas las configuraciones encuentran casi todo, y las diferencias son chicas:

- Palabras clave sola es la más débil: pierde las paráfrasis (q04).
- La reescritura es lo único que recupera el escenario D (q05).
- Fijo-150 sale primera porque, en un corpus chico, los dos pedazos de la política llegan al top 5. El hit rate no ve que la regla y la excepción quedaron separadas.
- El reranker no mejora el hit rate y cuesta unos 4 s por pregunta en CPU.

Es el mensaje del slide 05: *ajustá midiendo con preguntas reales*. Lo que conviene en un corpus no se puede suponer, hay que medirlo. Se decidió dejar el corpus así, sin forzarlo para que los números confirmen la teoría.

Es el entregable del trabajo práctico resuelto sobre otro corpus. Sirve como ejemplo sin darles la solución.

★ = escenarios recomendados para una clase de ~60 minutos.

## 5 · Guion sugerido (~60 min)

| Min | Slide | En la demo |
|---|---|---|
| 0–5 | 02 | Una pregunta simple. Pestaña **Pipeline**: qué hace cada etapa. |
| 5–10 | 04 | Escenario **A** en la pestaña **Espacio**. |
| 10–20 | 05–07 | Escenario **B**. Si sobra tiempo, **G** (vigencia) o **F** (permisos). |
| 20–25 | 09 | Mostrar `schema.sql` y la tabla real en `psql`. |
| 25–35 | 10–11 | Escenario **C** con la pestaña **Ranking**. |
| 35–45 | 13–15 | Escenarios **D** y **E**. |
| 45–50 | 16 | Escenario **H**: la tabla de métricas completa. |
| 50–60 | 16 | Arranque del trabajo práctico. |

## 6 · Orden de construcción

1. Corpus de MesaYa con sus trampas y `preguntas.json`.
2. `docker-compose.yml` con pgvector y el reranker, y `schema.sql`.
3. `Ingestor`, chunkers y embeddings con `bge-m3`.
4. `Buscador` (semántica, palabras clave y RRF) con filtros de tenant, audiencia y vigencia.
5. Pantalla con chat y pestañas **Pipeline**, **Ranking** y **Contexto**.
6. `Reranker` y `Reescritor`.
7. `Evaluador` y pestaña **Evaluación**.
8. Pestañas **Espacio** y **Chunks**, y modo guion.
9. `iniciar.sh`, `README.md` y prueba completa con el modelo de la clase.

Los pasos 1–7 alcanzan para dar la clase. El 8 es opcional.

## Decisiones tomadas durante la construcción

- **Reranker:** contenedor con `bge-reranker-v2-m3` sobre Infinity, opcional (`--profile reranker`). En CPU, 50 pares tardaban entre 8 y 13 s; por eso pasan los 20 primeros por RRF, como en el slide 11, y tarda unos 4 s.
- **Modelos:** `qwen3.5:9b` para responder, `qwen3.5:2b` para reescribir y `bge-m3` para los embeddings.
- **Modo guion:** incluido. Cada turno en vivo se graba en `guion/` y se reproduce sin Ollama ni reranker.
- **Corpus:** queda como está aunque las diferencias entre configuraciones sean chicas (ver escenario H).

## Pendiente de definir

- Duración real de la clase.
