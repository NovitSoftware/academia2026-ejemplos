# MesaYa · Laboratorio de calidad

Demo de la presentación **Unidad 3 · Calidad, grounding y criterios de uso**. Es el asistente de MesaYa de la demo de RAG, pero esta vez se mide: qué fragmentos entraron y cuántos servían, si cada oración de la respuesta está respaldada por la evidencia, dónde se rompió cuando falla y cuándo la pregunta no era para RAG.

> Idea para llevar: *medí antes de confiar*. Una respuesta fiel a un documento viejo sigue estando mal.

Está hecha en Python (FastAPI) con una interfaz web sin frameworks. Todo corre en Docker; los modelos, en Ollama.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | Docker con Compose |
| Modelos | Ollama: el del contenedor (por defecto) o el de tu máquina si ya lo tenés |
| Respuestas, reranking y verificación | `qwen3.5:9b` (u otro modelo de Ollama, se elige arriba a la derecha) |
| Embeddings | `bge-m3` |

La primera vez la app descarga los modelos que falten, unos 8 GB. El indicador de arriba a la derecha muestra el avance. El volumen de modelos (`academia-ollama`) se comparte con la demo de Memoria del agente: se descargan una sola vez.

## Iniciar

```bash
./iniciar.sh
```

Abrí [http://localhost:5300](http://localhost:5300).

- Si encuentra Ollama corriendo en tu máquina (`localhost:11434`), lo usa. Conviene si ya tenés los modelos o una GPU que Ollama aprovecha.
- Si no, levanta Ollama en un contenedor. En CPU funciona, pero cada respuesta tarda bastante más.
- `./iniciar.sh --ollama-docker` fuerza el Ollama del contenedor.
- `./iniciar.sh --detener` apaga todo.

Sin el script, el equivalente "todo en Docker" es `docker compose up -d --build`. Otro modelo: `MODELO=gemma4:e2b ./iniciar.sh`.

## La pantalla

**Izquierda.** Los escenarios de la clase y los controles:

- Recuperación: perfil, país, chunking (fijo de 150 tokens o por sección), búsqueda (semántica, palabras clave o híbrida), k, umbral de relevancia, solo vigentes, priorizar fuentes oficiales y reranking.
- Generación: grounding (sin evidencia, evidencia sin reglas o estricto) y verificación de cada oración.
- Criterio de uso: decidir la ruta antes de buscar (RAG, SQL, API o prompt).

**Centro.** El chat. Cada respuesta muestra chips con lo importante: fidelidad, si es correcta, si dijo «no sé», si entró una fuente prohibida, el tiempo y si salió de una grabación. Hacé clic en una respuesta anterior para volver a ver su recorrido.

**Derecha.** Cinco pestañas:

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Recuperación | Precision@k, recall@k, hit@k y fuentes prohibidas. La tira de 20 candidatos con los que entraron y los relevantes. Cada fragmento con su versión, vigencia, autoridad, puntajes y cuánto lo movió el reranking. Los excluidos por filtros. Si hubo router, adónde fue y la consulta SQL o el pedido a la API | 02, 03, 08, 11, 12, 16–18 |
| Grounding | Fidelidad de la respuesta y, oración por oración, si está respaldada, con la frase textual de la evidencia que la sostiene | 09, 10, 11 |
| Diagnóstico | El árbol de la slide 05 recorrido con datos: ¿estaba en el corpus?, ¿se recuperó?, ¿entró al contexto?, ¿es fiel?, ¿es correcta? Marca el primer nodo que falla y qué probar | 05, 06 |
| Contexto | Lo que recibió el modelo, tiempos por etapa y tokens | — |
| Evaluación | Todas las configuraciones contra el set de evaluación (sin LLM, segundos) y la evaluación de respuestas con la configuración actual (con el LLM como juez, minutos) | 15 |

## Escenarios para la clase

Cada escenario corre la misma pregunta con el control apagado y prendido. Las variantes con ✓ aplican la técnica de la slide.

| | Escenario | Pregunta | Qué se ve |
|---|---|---|---|
| A | El modelo inventa | ¿Qué es la seña y cuánto se paga? | Sin evidencia inventa montos («depende del establecimiento»). Con grounding estricto: 20 % del consumo, con cita |
| B | Precision y recall según k | ¿Cómo hago si quiero que me devuelvan la seña? | k = 1: precision 100 %, recall 25 % y el modelo dice «no sé». k = 10: recall alto y precision 30 % |
| C | Chunk crudo vs. buen chunk | Somos 12, ¿hasta cuándo puedo cancelar sin cargo? | El corte fijo separa la regla de su excepción. Por sección: 72 horas, con cita |
| D | Vigencia y jerarquía de fuentes | ¿Hasta cuántas horas antes puedo cancelar sin cargo? | Sin filtros entra la política 2021 y el newsletter (1 hora): la respuesta es **fiel 100 % e incorrecta**. Con vigentes y jerarquía: 2 horas |
| E | Evidencia insuficiente | ¿Cuánto sale el bife de chorizo? | Sin evidencia inventa. Estricto dice «no sé». Con umbral ni siquiera llama al modelo |
| F | ¿Dónde se rompió? | ¿Se puede llegar en silla de ruedas a la terraza de Palermo? | Palabras clave: falla la recuperación. Semántica: el fragmento entra y el modelo igual dice «no sé»: falla la generación |
| G | Dos etapas: reranking | ¿Y para los contratados? (con historial) | El documento correcto queda 5.º; el reranking lo sube al 2.º |
| H | ¿RAG o no? | Reservas de la semana, mesa hoy a las 21, teléfono | Sin router, RAG no puede contestar. Con router: SQL con el número exacto, API con los turnos libres, prompt con la ficha |

## Guion sugerido (~45 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–5 | 02 | **B**: las tres variantes de k. Mirar la tira de candidatos |
| 5–12 | 03, 08 | **G** y **C** |
| 12–22 | 09–13 | **A**, **E** y **D**. En D, quedarse en la pestaña Grounding: fiel no es correcto |
| 22–30 | 05–06 | **F** en la pestaña Diagnóstico |
| 30–37 | 16–18 | **H** con sus tres preguntas |
| 37–45 | 15 | Pestaña Evaluación: correr recuperación con k = 2 y k = 4 y comparar |

## Modo guion

Plan B por si Ollama falla en vivo. Cada llamada al modelo que termina bien se guarda en `grabaciones/`. Con **Modo guion** prendido, la misma consulta con los mismos controles se responde desde la grabación, sin llamar a Ollama, y la respuesta muestra la fecha de grabación.

Para preparar la clase, corré todos los escenarios una vez con el modo apagado **el mismo día de la clase**: el router y el SQL usan la fecha de hoy, así que las grabaciones de esas preguntas sirven solo ese día. Las demás no dependen de la fecha. Si querés que viajen con el repositorio, commiteá `grabaciones/`.

## Cómo está hecho

```text
app/
  main.py         API, turno completo, árbol de diagnóstico y evaluación
  corpus.py       lectura del corpus y los dos chunkers (por sección y fijo)
  busqueda.py     embeddings, BM25, RRF, filtros, jerarquía de fuentes, umbral y reranking con el LLM
  generacion.py   prompts de grounding, verificador con cita textual y comparación con la respuesta de referencia
  ruta.py         router, base SQLite de reservas, consulta SQL y API de disponibilidad
  escenarios.py   escenarios A–H
  ollama.py       cliente de Ollama: descarga de modelos y modo guion
  static/         interfaz (HTML, CSS y JS sin build) con la guía visual de Novit
corpus/           los 26 documentos de la demo de RAG, más una sección del newsletter que contradice la política vigente
evaluacion/       preguntas.json: las 15 de la demo de RAG y 7 nuevas (fuera del corpus y de ruteo)
```

## Decisiones y límites

- **Verificador con cita textual.** Un LLM como juez es demasiado literal o demasiado permisivo según el día. Por eso el verificador tiene que copiar la frase de la evidencia que respalda cada oración, y el código comprueba que exista. Si no aparece, la oración queda sin respaldo.
- **Fiel y correcta son dos preguntas.** La fidelidad se mide contra la evidencia; la corrección, contra la respuesta de referencia, en otra llamada.
- **Precision y recall por chunk.** Un fragmento es relevante si pertenece al documento (y sección) esperados. Con chunking fijo, una sección queda en varios fragmentos y el recall lo refleja.
- **Reranking con el LLM.** No usa un cross-encoder para no sumar otro modelo de varios GB: el LLM puntúa de 0 a 3 los 12 primeros. Tarda unos segundos.
- **Datos de reservas sintéticos.** Se generan al iniciar, siempre iguales, alrededor de la fecha de hoy. Hoy a las 21 Belgrano está lleno a propósito.
- **Corpus chico.** Con 26 documentos las diferencias entre configuraciones son chicas. La lección es la misma que en la demo de RAG: medir en el corpus propio antes de elegir.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama» arriba a la derecha | El contenedor de Ollama no levantó o el Ollama local no responde. `docker compose logs ollama` |
| Se queda en «Descargando…» | La primera descarga es grande. Si falla, la app reintenta sola; si no, revisá la conexión |
| Cada respuesta tarda más de 30 s | Ollama corre en CPU. Usá el Ollama de tu máquina con GPU o un modelo más chico |
| «No hay una grabación de esta llamada» | En modo guion, la pregunta y los controles tienen que coincidir con una corrida grabada |
| La app no ve el Ollama local | Ollama tiene que escuchar en `localhost:11434`. El script usa la red del host para llegar ahí (Linux) |
