# MesaYa · Memoria del agente (.NET)

Demo de la presentación **Unidad 4 · Memoria del agente**. Es el asistente de reservas de MesaYa con memoria de corto y largo plazo. Al lado del chat se ve qué recuerda de cada cliente, qué recuerdos entran en cada turno, qué decide guardar, actualizar o descartar, y por qué.

> Idea para llevar: *recordar es decidir*. Qué guardar, con qué metadatos, cuándo olvidar y de quién no aceptar recuerdos.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks. Es un port de la versión original en Python: mismos escenarios, mismos prompts y la misma API. Corre directo en tu máquina y usa el Ollama local.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| Modelos | Ollama corriendo en tu máquina (`localhost:11434`) |
| Agente, extractor de memoria y resúmenes | `qwen3.5:9b` (u otro modelo de Ollama, se elige arriba a la derecha) |
| Embeddings de los recuerdos | `bge-m3` |

Si falta algún modelo, la app lo descarga en tu Ollama al iniciar. Para bajarlos antes: `ollama pull qwen3.5:9b` y `ollama pull bge-m3`.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5400](http://localhost:5400). `Ctrl+C` la detiene.

- El script verifica que Ollama responda y avisa si falta algún modelo.
- Otro modelo: `MODELO=gemma4:e2b ./iniciar.sh`.
- Otro Ollama: `OLLAMA_URL=http://otra-maquina:11434 ./iniciar.sh`. Otro puerto: `PUERTO=5500 ./iniciar.sh`.

## La pantalla

**Izquierda.**

- **Cliente:** Lucía o Martín. Cada uno tiene su propia memoria: lo de uno nunca entra en el contexto del otro.
- **Sesión:** *Nueva sesión* cierra la conversación. El estado de la tarea se archiva y la memoria de largo plazo queda.
- **Reloj:** *+30 días* simula el paso del tiempo para ver vencimientos (TTL) y decaimiento.
- **Escenarios:** cada variante reinicia todo y deja los pasos listos. *Siguiente paso* los corre de a uno; *Correr todos*, de corrido.
- **Controles:** memoria de largo plazo, memoria conversacional (completa, ventana o resumen + ventana), recuerdos por turno, escritura (en caliente o en segundo plano) y la política de escritura (filtrar datos sensibles, validar origen).

**Centro.** El chat. Cada respuesta muestra cuántos recuerdos usó, qué guardó, descartó o bloqueó, y debajo los recuerdos que entraron al contexto (en rojo si vienen de una fuente externa).

**Derecha.** Cuatro pestañas:

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Memoria | Los recuerdos activos agrupados en semánticos, episódicos y procedimentales, cada uno con su ficha: origen, fuente, confianza, usos, vencimiento y recencia. El ícono `</>` muestra el JSON de metadatos. El historial muestra lo que se olvidó o se reemplazó y por qué | 04, 05, 13 |
| Turno | El recorrido del turno (recuperar → analizar → herramienta → responder → escribir), las memorias candidatas con su puntaje y la decisión sobre cada candidato: GUARDAR, ACTUALIZAR, REFORZAR, NO GUARDAR o BLOQUEAR | 06, 12, 15, 16 |
| Contexto | La ventana del turno por componentes, con tokens: instrucciones, memorias, estado, herramienta, resumen, conversación y mensaje | 07, 17 |
| Estado | La reserva en curso (corto plazo) con sus datos y su fase, y los estados archivados al cerrar cada sesión | 10, 11 |

## Escenarios para la clase

| | Escenario | Pasos | Qué se ve |
|---|---|---|---|
| A | Un LLM no se acuerda de nada | «Soy celíaca y prefiero la terraza» → nueva sesión → «Quiero reservar el sábado» | Sin memoria, empieza de cero. Con memoria, recomienda terraza y platos sin TACC |
| B | ¿Quién gana? | Prefiere terraza → nueva sesión → «Esta vez adentro, que llueve» → nueva sesión → «¿Dónde me sentás?» | El pedido de hoy cambia el estado y se descarta como recuerdo («solo esta vez»). En la sesión siguiente vuelve la terraza |
| C | Estado vs. memoria | Reservar en Núñez en tres mensajes y confirmar → nueva sesión → «¿Qué reserva tengo?» | El estado se completa paso a paso. Al confirmar, se archiva y queda un recuerdo episódico con vencimiento de 30 días |
| D | Lo que no se guarda | DNI, tarjeta y «me encanta el risotto» en un mensaje | Sin filtro, la tarjeta y el DNI quedan en memoria. Con filtro, solo el gusto por el risotto |
| E | Memory poisoning | «¿Qué dicen las reseñas de Palermo?» → nueva sesión → «¿Tengo algún beneficio?» | Una reseña esconde instrucciones. Sin validar origen, el agente le ofrece un 50 % de descuento «por ser VIP». Validando origen, el host bloquea todo lo que viene de la reseña |
| F | Olvidar también es diseño | Preferencias y una reserva → +45 días → «Olvidate de la terraza» → cambiar a Martín | Vence el episodio, se olvida la preferencia a pedido y Martín no ve nada de Lucía |
| G | Conversación larga | 14 mensajes precargados → «¿Para cuántas personas quedó?» | Completa: correcto, ~630 tokens. Ventana: inventa «cuatro». Resumen + ventana: correcto con ~450 tokens |

## Guion sugerido (~45 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–8 | 02–05 | **A** en sus dos variantes. Pestaña Memoria: los tres tipos y la ficha JSON |
| 8–15 | 06–07 | **G** en la pestaña Contexto. Mostrar escritura en segundo plano: la respuesta llega antes que el recuerdo |
| 15–25 | 08–11 | **B** y **C** en las pestañas Turno y Estado |
| 25–33 | 12–14 | **D** y **F** |
| 33–43 | 15 | **E**: primero sin validar, después validando |
| 43–45 | 16–17 | Pestaña Turno: el recorrido completo como resumen de la arquitectura |

## Modo guion

Igual que en la demo de calidad: cada llamada al modelo que termina bien se guarda en `grabaciones/`, y con **Modo guion** prendido se repite desde ahí. Para que la repetición encuentre las grabaciones, corré cada escenario desde su botón de variante (eso reinicia todo) y **el mismo día de la clase**, porque el agente usa la fecha de hoy para entender «el sábado». La clave de cada grabación se calcula igual que en Python: si el pedido es el mismo, una grabación sirve en las dos versiones.

## Cómo está hecho
```text
Program.cs       API: turnos, sesiones, reloj, olvido y precarga
Agente.cs        el turno: recuperar, analizar, herramientas, responder y escribir memoria con su política
Memoria.cs       almacén SQLite con metadatos, recuperación (similitud + importancia + recencia), TTL y olvido
escenarios.json  escenarios A–G y la conversación larga precargada
Comun/           cliente de Ollama (descarga de modelos y modo guion), JSON y utilidades de texto
static/          interfaz (HTML, CSS y JS sin build) con la guía visual de Novit
```

## Decisiones y límites

- **La procedencia la pone el host.** El extractor corre por separado sobre lo que dijo el cliente y sobre lo que leyó el agente (las reseñas). La fuente de cada candidato la asigna el código, no el modelo: por eso la validación de origen no depende de que el LLM se dé cuenta del ataque.
- **Se extrae solo del mensaje del cliente.** La respuesta del asistente puede repetir texto externo y colarlo como si lo hubiera dicho el cliente.
- **La política está en código.** El LLM propone candidatos y los marca (sensible, solo esta vez, inferido). Lo que se guarda lo decide una función con reglas explícitas, en `Agente.cs › Politica`.
- **El episodio lo escribe el host.** Al confirmar una reserva, el estado se archiva y el código guarda el recuerdo episódico con TTL, sin pasar por el LLM.
- **Recuperación.** Puntaje = 0,6 × similitud + 0,25 × importancia + 0,15 × recencia (semivida de 60 días). Entran los k mejores con similitud ≥ 0,35.
- **Olvido a pedido por similitud.** Se olvidan los recuerdos con similitud ≥ 0,55 respecto de lo que el cliente pidió olvidar. Un pedido vago puede borrar de más o de menos: buen tema para discutir.
- **Escritura en segundo plano.** La respuesta del turno se serializa antes de arrancar la escritura de memoria, como `asyncio.create_task` en Python: la respuesta llega antes que el recuerdo.
- **La misma API que en Python.** Como la interfaz es la original, las respuestas tienen exactamente las mismas claves. Por eso los datos se arman con `Dict` (un `Dictionary<string, object?>`) en lugar de clases.
- **Sesiones en memoria del proceso.** La memoria de largo plazo persiste en SQLite (carpeta `datos/`); las sesiones y el estado se pierden al reiniciar la app, como cualquier memoria de corto plazo.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama» o «Preparando modelos…» | Ollama no responde en `localhost:11434` (inicialo con `ollama serve`) o está descargando un modelo |
| El recuerdo tarda en aparecer | Con escritura en segundo plano es lo esperado: la respuesta llega primero y el recuerdo unos segundos después |
| Un escenario da otro resultado | El modelo no es del todo determinista. Corré la variante de nuevo, o grabala antes de clase y usá el modo guion |
| «No hay una grabación de esta llamada» | En modo guion hay que repetir exactamente los mismos pasos, desde el botón de la variante |
