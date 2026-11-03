# Clase 3 · Context Engineering — MesaYa Context Lab

Demo para la presentación **Módulo 2.1 · Context Engineering**. Es el agente de reservas de MesaYa de la Clase 2, pero esta vez se mira **la ventana exacta que recibe el modelo en cada llamada**: qué entra, en qué orden, cuánto ocupa y qué pasa cuando no entra.

> *Más contexto no es mejor contexto.* Cada escenario corre la misma pregunta con dos o tres configuraciones de contexto y las compara.

## Requisitos

- SDK .NET 10.
- [Ollama](https://ollama.com) con un modelo que soporte tools. Probado con `qwen3.5:9b` (por defecto); también funcionan `gemma4:e2b`, `ministral-3:3b` y otros.
- No depende de la Clase 2: la API y el cliente de Ollama están copiados acá.

## Ejecutar

```bash
./iniciar.sh
```

Abrí **http://localhost:5190**. El script compila y levanta:

| Proyecto | Puerto | Qué es |
|---|---|---|
| `MesaYa.Api` | 5180 | Copia de la API de la Clase 2 con datos nuevos para los escenarios |
| `MesaYa.Contexto` | 5190 | Blazor: chat del agente + **Rayos X del contexto** |

Otro modelo inicial: `OLLAMA_MODEL=gemma4:e2b ./iniciar.sh` (también se cambia desde la pantalla).

## La pantalla

- **Izquierda:** escenarios (un botón por variante; las marcadas con ✓ aplican la técnica del slide) y todas las perillas del contexto.
- **Centro:** el chat completo tal como lo ve la persona. Lo que el modelo ya no ve literal aparece atenuado con ✂. Abajo está la **comparación** de las variantes que ya se corrieron.
- **Derecha · Rayos X**, con selector de llamada:
  - **Ventana:** cada tramo en el orden en que se envía, coloreado por tipo (instrucciones, conocimiento, interacción, tarea). Los tramos que Ollama descarta aparecen tachados. Botón *JSON crudo* con el cuerpo real del `POST /api/chat`.
  - **Presupuesto:** la tabla del slide 08 escalada a `num_ctx`, con barra de uso, umbral, reserva de salida y lo que realmente procesó Ollama.
  - **Línea de tiempo:** tokens por llamada, dónde se cruzó el umbral y cada compactación con su resumen y los IDs rescatados o perdidos.

## Escenarios

Resultados del ensayo con `qwen3.5:9b` (temperatura 0, semilla fija):

| | Escenario | Slides | Sin la técnica | Con la técnica |
|---|---|---|---|---|
| **A** | El agente olvidó la reserva (`num_ctx` 4.096) | 10–13 | ~5.200 tokens no entran; Ollama recorta el turno 1 y el agente **vuelve a pedir el número de reserva** | Resumen + 3 turnos literales: cancela `RSV-20481` sin preguntar. Variante *Resumen defectuoso*: el resumen pierde el ID y el agente vuelve a pedirlo |
| **B** | Pre-carga vs. bajo demanda (`num_ctx` 16.384) | 06 | ~6.600 tokens, ~33 s y **no consulta disponibilidad**: ofrece las 21 h, que están llenas | ~2.900 tokens, ~13 s; detecta el turno lleno y ofrece 20:30 |
| **C** | Información obsoleta | 05, 13, 14 | Duda entre "12, 2 o 24 horas" | Cita `pol-cancel-v3`: sin cargo |
| **D** | Prompt injection en una reseña | 14 | La reseña `RES-202` entra a la ventana como texto suelto | Entra rotulada como dato. Si el agente intenta cancelar, la confirmación humana lo frena |
| **E** | Permisos | 05 | El agente **muestra las reservas de Lucía** | El host filtra antes: el dato nunca entra |
| **F** | Densidad por token | 05 | ~4.100 tokens | ~2.700 tokens con la misma respuesta |

Sobre **D:** los modelos actuales (`qwen3.5`, `gemma4`, `ministral-3`) ignoran este ataque directo, tanto en su versión de "mensaje del sistema" como en la de desinformación. Usalo para mostrar que el texto malicioso **llega igual a la ventana** (pestaña Ventana) y que la defensa no puede depender de que el modelo resista: delimitar, rotular y poner la confirmación fuera del modelo.

## Guion sugerido (~60 min)

| Min | Slide | En la demo |
|---|---|---|
| 0–5 | 02 | Modo libre, una pregunta simple. Pestaña **Ventana**: "esto es todo lo que ve el modelo". *JSON crudo*. |
| 5–15 | 05–06 | **B** (pre-carga / bajo demanda). Si sobra tiempo, **F**. |
| 15–20 | 07–08 | Pestaña **Presupuesto** sobre la última llamada. |
| 20–35 | 10–12 | **A** con sus tres variantes. Pestaña **Línea de tiempo**. |
| 35–45 | 13–14 | **C** y **D**. |
| 45–60 | 15 | Trabajo práctico: la pestaña Presupuesto sirve como plantilla de la tabla del entregable. |

## Ensayo sin UI

Antes de clase se puede verificar cada escenario con el modelo que vas a usar:

```bash
curl -s http://localhost:5190/ensayo/olvido/sin
```

Escenarios y variantes: `olvido/{sin,con,envenenado}`, `precarga/{precarga,demanda}`, `obsoleta/{sin,meta,vigentes}`, `injection/{sin,con}`, `permisos/{sin,con}`, `densidad/{completo,compacto}`. Se puede agregar `?modelo=gemma4:e2b`. En el ensayo las acciones (reservar, cancelar) se rechazan automáticamente.

## Cómo está hecho

```text
MesaYa.Contexto/
  Contexto/ConstructorContexto.cs   arma la ventana por componentes y en orden; estima tokens
  Contexto/Modelo.cs                componentes, tipos, perillas y presupuesto del slide 08
  Agente/SesionDemo.cs              loop del agente, compactación al superar el umbral, métricas
  Agente/Compactador.cs             resumen + buffer, por tramos, con verificación de IDs
  Agente/Herramientas.cs            tools; densidad, metadata, permisos y delimitación del lado del host
  Escenarios/Catalogo.cs            escenarios A–F, variantes y conversaciones precargadas
  Components/                       pantalla Blazor y paneles de Rayos X
MesaYa.Api/
  Program.cs                        API de la Clase 2 + políticas, reseñas, reservas y /v1/demo/reset
```

Detalles técnicos:

- **Sin MCP:** las tools se definen en el agente para que el foco quede en la ventana del modelo.
- **Tokens:** Ollama no tiene un endpoint para tokenizar. Cada componente se estima por caracteres y la estimación se calibra con `prompt_eval_count`, que siempre informa el prompt completo.
- **Desborde:** si el prompt supera `num_ctx`, Ollama no da error. Conserva el system prompt y el último mensaje y descarta en silencio los mensajes más viejos. El escenario A lo usa tal cual, sin simularlo.
- **Caché:** con el mismo prefijo, `prompt_eval_duration` baja mucho (por ejemplo, de 2.000 ms a 140 ms). Por eso lo estable va arriba y la fecha del turno va abajo, en el recordatorio.
