# MesaYa · Workflows y orquestación (.NET)

Demo de la presentación **Unidad 1 · Workflows, agentes y orquestación**. El mismo asistente de MesaYa resuelve pedidos con siete patrones distintos: prompt chaining, routing, sectioning, voting, orchestrator-workers, evaluator-optimizer y un agente con herramientas. Al lado del chat se ve cada paso, cada llamada al modelo, la línea de tiempo y lo que costó.

> Idea para llevar: *si podés dibujar el diagrama, es un workflow*. La autonomía se agrega de a un escalón, midiendo lo que cuesta.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks, con la misma base que las demos de la Clase 4. Corre directo en tu máquina y usa el Ollama local.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| Modelo | Ollama corriendo en tu máquina (`localhost:11434`) con `qwen3.5:9b` (u otro modelo, se elige arriba a la derecha) |

Si falta el modelo, la app lo descarga al iniciar. Para bajarlo antes: `ollama pull qwen3.5:9b`. Esta demo no usa embeddings.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5500](http://localhost:5500). `Ctrl+C` la detiene.

- El script verifica que Ollama responda y avisa si falta el modelo.
- Otro modelo: `MODELO=qwen3.5:4b ./iniciar.sh`. En una máquina sin GPU grande, el de 4b responde bastante más rápido.
- Otro Ollama: `OLLAMA_URL=http://otra-maquina:11434 ./iniciar.sh`. Otro puerto: `PUERTO=5600 ./iniciar.sh`.

## La pantalla

**Izquierda.**

- **Patrón:** los siete patrones, de menos a más autonomía. El pedido que escribas se resuelve con el patrón elegido.
- **Escenarios:** cada botón elige el patrón, ajusta los controles y corre un pedido preparado.
- **Controles:** llamadas en paralelo (sí o no), máximo de vueltas del evaluador y máximo de pasos del agente. *Limpiar* borra el chat y la comparación, también del disco.

**Centro.** El chat. Cada respuesta muestra el patrón, cuántas llamadas al modelo hizo, los tokens, el tiempo y si algo cortó la corrida (un gate, la guardia, el máximo de vueltas o de pasos). Tocá una respuesta para verla a la derecha.

**Derecha.** Tres pestañas:

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Recorrido | Llamadas, tokens, tiempo y quién decide el camino (el código o el modelo). Una tarjeta propia de cada patrón: las rutas del router, los tres votos, el plan del orquestador o las vueltas del evaluador con su rúbrica. Debajo, la línea de tiempo con cada paso: en paralelo, las barras se superponen | 02–13 |
| Llamadas al LLM | Cada llamada con los mensajes que recibió (system, user, assistant), lo que devolvió y sus tokens | 03 |
| Comparar | Todas las corridas de la sesión, con barras de llamadas, tokens y tiempo | 14–15 |

## Escenarios para la clase

| | Escenario | Corridas | Qué se ve |
|---|---|---|---|
| A | Workflow vs agente | La misma reserva con chaining y con el agente | El workflow resuelve con 2 llamadas. El agente necesita varias vueltas y bastantes más tokens para lo mismo. Pestaña Comparar |
| B | Prompt chaining con gate | Datos completos · falta un dato · sin lugar | Sin la cantidad de personas, el gate corta y se repregunta sin gastar más llamadas. Pocitos el sábado a las 21 está lleno: la respuesta ofrece las alternativas del sistema |
| C | Routing | Alérgenos · cancelación · sucursales · reserva · fuera de dominio | Cada ruta tiene su prompt y su documento. La reserva entra a la cadena de B: un patrón dentro de otro. Fuera de dominio no gasta una segunda llamada |
| D | Sectioning | Consulta normal · pide datos internos · sin paralelo | La guardia corre al mismo tiempo que la respuesta y, si detecta un pedido de sueldos, la respuesta no sale. Sin paralelo, el tiempo es la suma |
| E | Voting | Leve · grave · dudoso | Tres miradas (salón, seguridad alimentaria, atención al cliente). Si dos de tres dicen grave, se escala a una persona |
| F | Orchestrator-workers | Las tres sucursales · una sucursal · sin paralelo | El orquestador decide cuántas subtareas hacen falta: pedidos distintos, planes distintos |
| G | Evaluator-optimizer | Hasta 3 vueltas · una sola vuelta | El generador no conoce los criterios. El evaluador devuelve qué falló y el generador corrige. Un criterio (largo máximo) lo cuenta el código |
| H | Un agente investiga | Investigar · máximo 3 pasos | El agente cruza el cierre de caja, los tickets y las transferencias hasta encontrar la causa. Con 3 pasos se corta antes |

## Guion sugerido (~40 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–8 | 02–05 | **A**: la misma reserva con workflow y con agente, en la pestaña Comparar. Después **H**: un caso donde el agente sí tiene sentido |
| 8–14 | 07 | **B** en sus tres corridas, pestaña Recorrido |
| 14–20 | 08 | **C**: alérgenos, fuera de dominio y reserva (un patrón dentro de otro) |
| 20–27 | 09 | **D** y **E**. Mostrar la línea de tiempo con y sin paralelo |
| 27–32 | 10 | **F**: dos pedidos, dos planes |
| 32–38 | 11 | **G**: abrir las vueltas y la rúbrica. Después la variante de una sola vuelta |
| 38–40 | 14–15 | Pestaña Comparar con todo lo corrido: la autonomía se paga |

## Modo guion

Igual que en las demos de la Clase 4: cada llamada al modelo que termina bien se guarda en `grabaciones/`, y con **Modo guion** prendido se repite desde ahí sin llamar a Ollama. Sirve de plan B si el modelo tarda o falla en plena clase.

La carpeta ya trae grabados los 20 botones de los escenarios A–H con `qwen3.5:9b`. Si vas a usar otro modelo, corré los escenarios una vez antes de la clase con ese modelo. Los prompts no dependen de la fecha, así que las grabaciones sirven cualquier día.

En vivo, con `qwen3.5:9b` en una GPU de 4 GB, cada llamada tarda entre 10 y 60 segundos: una cadena o un router, menos de un minuto; el orquestador con cuatro workers y el evaluador, unos 4 minutos; el agente que investiga la caja, otros 4. Para esos escenarios, el modo guion es la opción más cómoda en clase.

## Cómo está hecho

```text
Program.cs       API: catálogo, estado de Ollama, /api/correr y el historial de corridas
Patrones.cs      los siete patrones y la traza (pasos, llamadas, tokens y reloj)
Datos.cs         el mundo de MesaYa: sucursales, carta, política, disponibilidad, ventas y caja
escenarios.json  escenarios A–H
Comun/           cliente de Ollama (descarga de modelos y modo guion), JSON y utilidades de texto
static/          interfaz (HTML, CSS y JS sin build) con la guía visual de Novit
```

## Decisiones y límites

- **Las corridas se guardan.** Cada corrida queda en `datos/corridas.json` con su traza completa: el chat y la pestaña Comparar se recuperan al recargar la página o reiniciar la demo. La carpeta `datos/` no va al repositorio.
- **Los datos son inventados y deterministas.** Disponibilidad, ventas y caja salen de `Datos.cs`. Así, el mismo pedido da el mismo dato y las corridas se pueden comparar.
- **Los gates y las herramientas son código.** El LLM extrae, clasifica, redacta o decide; el código chequea, consulta los sistemas y crea la reserva.
- **La línea de tiempo usa un reloj virtual.** Cada paso dura lo que tardó su llamada; un grupo en paralelo avanza lo que el más lento, uno en secuencia, la suma. Así se ve igual en vivo y en modo guion. Si Ollama atiende las llamadas de a una, las que van "en paralelo" esperan en la cola y su barra se alarga: eso también se ve. Para paralelo real, Ollama necesita memoria para atender varias a la vez (`OLLAMA_NUM_PARALLEL=2` o más al iniciar `ollama serve`).
- **El evaluador mezcla LLM y código.** Cuatro criterios los juzga el LLM; el largo máximo lo cuenta el código. La aprobación la decide el código: todos los criterios tienen que cumplirse.
- **El agente tiene freno.** Máximo de pasos configurable. Si repite una herramienta con los mismos argumentos, se le avisa una vez para que cambie de estrategia; si repite de nuevo, se corta.
- **El agente decide con JSON, no con tool calling nativo.** En cada paso devuelve `{pensamiento, accion, argumentos}` con un esquema; así funciona igual con cualquier modelo de Ollama.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama» o «Preparando el modelo…» | Ollama no responde en `localhost:11434` (inicialo con `ollama serve`) o está descargando el modelo |
| La primera corrida tarda mucho | Ollama está cargando el modelo en memoria. Las siguientes son más rápidas |
| El agente tarda varios minutos | Cada vuelta del loop es una llamada con todo el historial. Con poca GPU, usá `qwen3.5:4b` o el modo guion |
| Una corrida da otro resultado | El modelo no es del todo determinista. Corré de nuevo, o grabala antes de clase y usá el modo guion |
| «No hay una grabación de esta llamada» | En modo guion hay que repetir exactamente el mismo pedido, con el mismo modelo y los mismos controles |
