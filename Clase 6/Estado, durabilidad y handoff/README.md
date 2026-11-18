# MesaYa · Estado, durabilidad y handoff (.NET)

Demo de la presentación **Unidad 3 · Estado, durabilidad y handoff**. Laura G. pide una mesa por la web de MesaYa y el agente de reservas la resuelve en una tarea de cinco pasos: extraer los datos (LLM), bloquear la mesa, cobrar la seña, crear la reserva (las tres contra la API del sistema de reservas) y confirmar (LLM). El estado de la tarea vive en disco, cada pedido lleva su correlation ID y desde la columna izquierda se le inyectan fallas: una caída del proceso, un timeout, errores HTTP o un JSON roto del modelo.

> Idea para llevar: *guardá, reanudá y no repitas*. El estado vive afuera, cada acción con efecto se hace una sola vez y, cuando la tarea cambia de manos, viaja un resumen estructurado con un solo dueño.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks, con la misma base que las demos de la Clase 5. Corre directo en tu máquina y usa el Ollama local.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| Extraer datos, confirmar, agente de eventos y resumen para la persona | Ollama corriendo en tu máquina (`localhost:11434`) con `qwen3.5:4b` (u otro modelo, se elige arriba a la derecha) |

Si falta el modelo, la app lo descarga al iniciar. Para bajarlo antes: `ollama pull qwen3.5:4b`.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5700](http://localhost:5700). `Ctrl+C` la detiene.

- Otro modelo: `MODELO=qwen3.5:9b ./iniciar.sh`. Otro Ollama: `OLLAMA_URL=http://otra-maquina:11434 ./iniciar.sh`. Otro puerto: `PUERTO=5701 ./iniciar.sh`.

## La pantalla

**Izquierda.**

- **Controles:** checkpoints, correlation ID e idempotencia, retry con backoff y handoff con contexto. Arrancan prendidos: apagalos para ver qué se rompe.
- **Fallas a inyectar:** caída del proceso después de un paso, timeout en una llamada (la acción se hace pero la respuesta no llega), errores de la API al crear la reserva (503 o 400) y JSON roto del LLM. Se aplican al próximo mensaje y después vuelven a «ninguna».
- **Escenarios:** cada botón reinicia el sistema, ajusta controles y fallas, y corre uno o varios mensajes (o *Reanudar*).

**Centro.** El chat con Laura. Cada respuesta dice quién contesta (agente de reservas, agente de eventos o MesaYa), cómo terminó la tarea, su correlation ID y si su estado quedó en disco. Si el proceso se cae, aparece **Reanudar** arriba a la derecha.

**Derecha.** Cinco pestañas:

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Tarea | Los cinco pasos (hechos, salteados al reanudar, con falla o compensados) con su marca de checkpoint, el estado de ejecución que hay en disco junto al conversacional, y el recorrido del turno con llamadas a la API, esperas y checkpoints | 3.1, 3.3, 3.4 |
| Sistema de reservas | Reservas, mesas y cobros que hay de verdad en el sistema, con los **duplicados** marcados, y cada pedido a la API con su `X-Correlation-ID` y su clave de idempotencia | 3.2 |
| Handoff | El JSON que viajó, qué incluye (motivo, datos confirmados, hecho, pendiente, correlation ID), quién es el dueño de la tarea y la bandeja de atención al cliente | 3.6 |
| Disco y log | Las tareas en `datos/tareas/` (una colgada se puede reanudar desde acá), la cola de entrada y el log filtrable por correlation ID | 3.1, 3.2 |
| Llamadas | Cada llamada al modelo con sus mensajes y su salida | — |

## Escenarios para la clase

| | Escenario | Corridas | Qué se ve |
|---|---|---|---|
| A | Caída y reanudación | Caída tras bloquear la mesa · Reanudar · Sin checkpoints: caída · Reanudar sin checkpoints | Con checkpoints, *Reanudar* carga el checkpoint 2 y sigue en el paso 3: la mesa no se vuelve a bloquear. Sin checkpoints, en disco solo queda el mensaje en la cola: la tarea se repite con otro correlation ID y quedan **dos mesas bloqueadas** |
| B | Timeout: correlation ID e idempotencia | Timeout al cobrar · con ID · sin ID · Timeout al crear · sin ID | La API cobra la seña pero la respuesta no llega. Con ID, el retry recibe «ya existía» y devuelve C-501. Sin ID, cobra de nuevo (C-502). Si el timeout es al crear la reserva, quedan R-1043 y R-1044 |
| C | Reintentar con criterio | 503, 503 y después OK · HTTP 400 · JSON roto del LLM · La API no vuelve | Backoff de 1 s, 2 s y 4 s con jitter. El 400 no se reintenta. El JSON cortado se reintenta una vez pasándole el error al modelo. Con cuatro 503 se degrada, se compensa (se devuelve la seña y se libera la mesa) y se escala a una persona |
| D | Handoff con contexto | A otro agente · Sin contexto · A una persona | «En realidad somos 40, es un cumpleaños»: la tarea pasa al agente de eventos, que retoma día, hora y sucursal sin volver a pedirlos. Sin contexto, los pide de nuevo. Un reclamo va a la bandeja con un resumen de dos oraciones, y el segundo mensaje ya no lo contesta el agente de reservas |

## Guion sugerido (~25 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–8 | 3.0–3.3 | **A**: la caída, la pestaña Tarea (estado en disco contra estado en memoria) y *Reanudar*. Después sin checkpoints y la pestaña Sistema con las dos mesas |
| 8–13 | 3.2 | **B**: el timeout con y sin ID; la tabla de pedidos a la API |
| 13–19 | 3.4–3.5 | **C**: el backoff en el recorrido, el 400 y la recuperación escalonada |
| 19–25 | 3.6 | **D**: el JSON del handoff, con y sin contexto, y la bandeja de atención |

## Modo guion

Igual que en las otras demos: cada llamada al modelo que termina bien se guarda en `grabaciones/`, y con **Modo guion** prendido se repite desde ahí sin llamar a Ollama. La carpeta ya trae grabados los botones de los escenarios A–D con `qwen3.5:4b`. Los prompts no dependen de la fecha ni del correlation ID.

Para regrabar después de cambiar un prompt: borrá `grabaciones/`, iniciá con `REUSAR_GRABACIONES=1 ./iniciar.sh` y corré los escenarios en orden. Con esa variable, una llamada que ya está grabada se repite desde la grabación: así los escenarios que comparten mensajes graban cada prompt una sola vez y sus conversaciones quedan encadenadas (el modelo no es del todo determinista y, sin ella, cada corrida pisaría la anterior).

## Lo que se guarda

Todo en `datos/` (no va al repositorio), que es justamente el "afuera" del proceso:

```text
datos/tareas/req-….json  el estado de ejecución de cada tarea (los checkpoints)
datos/conversacion.json  el estado conversacional y la tarea activa
datos/cola.json          los pedidos recibidos que todavía no terminaron
datos/sistema.json       el sistema de reservas: mesas, cobros, reservas y pedidos a la API
datos/bandeja.json       los casos derivados a una persona
datos/log.json           el log, con el correlation ID en cada línea
datos/turnos.json        el historial del chat
```

Reiniciar la demo (`Ctrl+C` y `./iniciar.sh`) no borra nada: una tarea que quedó a medias aparece como *colgada* en **Disco y log** y se puede reanudar. *Reiniciar todo* borra la carpeta.

## Cómo está hecho

```text
Program.cs       API: turnos, reanudar, lo que hay en disco
Agente.cs        la tarea: cinco pasos, checkpoints, reintentos, recuperación y handoff; el agente de eventos y la bandeja
Sistema.cs       la API de reservas simulada, con idempotencia por clave y fallas inyectables
Almacen.cs       lo que vive en datos/ (escritura atómica: archivo temporal y renombre)
Datos.cs         los pasos, los agentes, la política de reintentos y los datos fijos
Traza.cs         la traza del turno (pasos, llamadas, tokens y tiempos) y los esquemas JSON
escenarios.json  escenarios A–D
Comun/           cliente de Ollama (descarga de modelos y modo guion), JSON y utilidades de texto
static/          interfaz (HTML, CSS y JS sin build) con la guía visual de Novit
```

## Decisiones y límites

- **La caída es simulada.** El proceso de la demo no se mata: la tarea en memoria se descarta y la próxima acción solo puede usar lo que quedó en `datos/`. Para probar una caída real, cortá la demo con `Ctrl+C` a mitad de un turno y volvé a iniciarla: la tarea queda colgada en disco.
- **Sin checkpoints, el estado se guarda solo al final** (o al pausar). Es el contraste de la slide 3.2: "después de cada paso significativo, no solo al final".
- **La clave de idempotencia es `correlation_id:operación`.** Sin ella, la API no puede reconocer un pedido repetido. Las compensaciones (liberar la mesa, devolver la seña) también son idempotentes.
- **Solo se reintentan los errores transitorios** (timeout, 429 y 503), hasta 4 intentos con backoff exponencial y jitter. Los tiempos van en un reloj simulado: la demo duerme una décima parte para que se note sin hacer esperar a la clase.
- **El JSON roto es una falla inyectada:** la primera respuesta del modelo se corta a la mitad, como cuando se acaban los tokens. El reintento es una llamada real con el error en el prompt.
- **El handoff lo arma el código**, no el modelo: los datos confirmados y lo hecho salen del estado de la tarea, así que no se inventan. El agente que recibe sí es un LLM, y confirma que tomó la tarea.
- **El límite de responsabilidad es una regla:** más de 12 personas o un evento va al agente de eventos; un reclamo, a una persona. Desde ese momento el agente de reservas no contesta más esa tarea.
- **La fecha es fija** (jueves 12 de noviembre de 2026) para que «el sábado» sea siempre el 14 y las grabaciones sirvan.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama» o «Preparando modelos…» | Ollama no responde en `localhost:11434` (inicialo con `ollama serve`) o está descargando un modelo |
| «No hay ninguna tarea interrumpida para reanudar» | No hubo caída, o ya se reanudó. Corré primero la corrida con caída |
| Un escenario da otro texto | El modelo no es del todo determinista. Corré de nuevo o usá el modo guion |
| «No hay una grabación de esta llamada» | En modo guion hay que repetir exactamente el mismo mensaje, con el mismo modelo |
