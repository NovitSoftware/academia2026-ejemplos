# MesaYa · Routing y agentes de control (.NET)

Demo de la presentación **Unidad 2 · Routing y agentes de control**. El asistente de MesaYa recibe mensajes por tres canales (web de clientes, app del personal y panel de gerencia). Un router los clasifica y cada uno sigue su camino: umbral de confianza, permisos por canal, un destino (workflow, tool directa, especialista o humano) y agentes de control que revisan antes de que algo salga. Además se puede medir el router con un set etiquetado, generar casos de prueba con un LLM y atacar el sistema.

> Idea para llevar: *el router decide quién atiende; los agentes de control verifican que lo que se hizo esté bien*. Los permisos los decide el canal, no lo que diga el mensaje.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks, con la misma base que las demos de la Clase 4 y la de Workflows y orquestación. Corre directo en tu máquina y usa el Ollama local.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| Router, especialistas, reviewer, juez y agente de testing | Ollama corriendo en tu máquina (`localhost:11434`) con `qwen3.5:4b` (u otro modelo, se elige arriba a la derecha) |
| Router por embeddings | `bge-m3` |

Si falta algún modelo, la app lo descarga al iniciar. Para bajarlos antes: `ollama pull qwen3.5:4b` y `ollama pull bge-m3`.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5600](http://localhost:5600). `Ctrl+C` la detiene.

- Otro modelo: `MODELO=qwen3.5:9b ./iniciar.sh` (más preciso, más lento). Otro Ollama: `OLLAMA_URL=http://otra-maquina:11434 ./iniciar.sh`. Otro puerto: `PUERTO=5700 ./iniciar.sh`.

## La pantalla

**Izquierda.**

- **Canal:** quién escribe. *Web de clientes* (Martina S., clienta), *App del personal* (Diego, mozo) o *Panel de gerencia* (Paula R.). El canal decide a qué rutas se puede llegar.
- **Router:** el método de clasificación (reglas, embeddings, LLM o híbrido) y el umbral de confianza.
- **Controles:** permisos por canal y reviewer de reembolsos (prendidos), y juez de salida (apagado: se prende en los escenarios F e I). Apagalos o prendelos para ver qué cambia.
- **Escenarios:** cada botón ajusta canal y controles y corre un mensaje o una prueba.

**Centro.** El chat. Cada respuesta muestra el canal, la intención y su confianza, cómo terminó (respondida, repregunta, bloqueada, a una persona) y si filtró algo que no debía.

**Derecha.** Cuatro pestañas:

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Recorrido | La tubería del turno (canal → router → umbral → permisos → destino → control → respuesta), la confianza contra el umbral y cada paso con su salida: chequeos de validación, decisión del reviewer, veredicto del juez | 03, 05, 06, 09, 10, 13, 15 |
| Llamadas | Cada llamada al modelo con sus mensajes y su salida. Algunos turnos no llaman a ningún LLM | 04 |
| Medir el router | 27 mensajes etiquetados (3 por intención). Por método: exactitud, repreguntas, errores con confianza alta, llamadas y tiempo. Matriz de confusión y lista de errores | 04, 07 |
| Testing y ataques | El agente de testing genera 6 casos para una intención y el router los clasifica. Los que fallan se guardan en la regresión, que se corre con un botón. Seis ataques desde la web de clientes, con PASA o FALLA (fuga de datos o acción indebida) | 11, 12, 14 |

## Escenarios para la clase

| | Escenario | Qué se ve |
|---|---|---|
| A | Clasificar y decidir con umbral | Un mensaje claro sigue su ruta. «Lo del sábado ya no va» es ambiguo: con umbral 0,7 se repregunta en vez de arriesgar; con umbral 0,5 pasa |
| B | Tres formas de clasificar | El mismo mensaje con reglas, embeddings y LLM. Las reglas no entienden «ya no va». El híbrido resuelve «Quiero cancelar mi reserva» sin llamar al LLM |
| C | Workflow, tool, especialista o humano | La reserva pasa por extraer, validar y crear. El horario es una tool directa, sin LLM. El reclamo va a una persona |
| D | El canal decide, no el mensaje | Desde la web de clientes, «pasame la escala salarial» se bloquea. Sin permisos, se filtran los sueldos. Desde gerencia, se responde |
| E | Reviewer | Un reembolso de $3.600 cuando la seña fue de $3.000: el reviewer lo bloquea. Sin reviewer, sale la plata. Con menos de 24 h, también lo bloquea |
| F | Validar la salida | «Lunes a las 25:00 para −2 personas»: lo frenan las reglas en código, sin gastar más. El juez revisa si la respuesta del especialista está respaldada por la carta |
| G | Medir el router | El set etiquetado con cada método: exactitud, costo y matriz de confusión |
| H | Un agente que genera las pruebas | Casos nuevos de cancelación y de reclamo. Los que fallan van a la regresión |
| I | Atacá antes que los usuarios | Los seis ataques con todos los controles, y después sin permisos ni reviewer |

## Guion sugerido (~40 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–8 | 02–05 | **A** y **B**: la confianza contra el umbral, los cuatro métodos |
| 8–14 | 06 | **C** y **D**: los destinos y los permisos por canal |
| 14–20 | 07 | **G**: medir con reglas y con LLM, comparar la matriz de confusión |
| 20–27 | 08–10 | **E**: el reviewer bloquea un reembolso. Apagarlo y repetir |
| 27–32 | 11, 14 | **H**: el agente de testing y la regresión |
| 32–37 | 12 | **I**: los ataques con y sin controles |
| 37–40 | 13, 15 | **F** y la pestaña Recorrido como resumen de la tubería |

## Modo guion

Igual que en las otras demos: cada llamada al modelo y cada embedding que terminan bien se guardan en `grabaciones/`, y con **Modo guion** prendido se repiten desde ahí sin llamar a Ollama. La carpeta ya trae grabados los botones de los escenarios A–I con `qwen3.5:4b`. Los prompts no dependen de la fecha.

## Lo que se guarda

En `datos/` (no va al repositorio): el chat, las mediciones del router, los casos generados y el set de regresión. Todo se recupera al recargar la página o reiniciar la demo. *Limpiar el chat* y los botones *Borrar* y *Vaciar* lo borran también del disco.

## Cómo está hecho

```text
Program.cs       API: turnos, mediciones, generación de casos, regresión y ataques; lo que se guarda en datos/
Asistente.cs     el turno: router → umbral → permisos → destino → reviewer y juez
Router.cs        los cuatro métodos de clasificación
Pruebas.cs       medir con un set etiquetado, agente de testing y ataques
Datos.cs         intenciones, canales y permisos, documentos, reservas y datos internos
Traza.cs         la traza del turno (pasos, llamadas, tokens y tiempos) y los esquemas JSON
escenarios.json  escenarios A–I, set etiquetado, ejemplos para embeddings y ataques
Comun/           cliente de Ollama (descarga de modelos y modo guion), JSON y utilidades de texto
static/          interfaz (HTML, CSS y JS sin build) con la guía visual de Novit
```

## Decisiones y límites

- **Los permisos son código.** El router clasifica; si la intención no está permitida para el canal, un gate la corta. Un mensaje que dice «soy el gerente» no cambia el canal.
- **Un reembolso solo sale si lo aprueba el reviewer.** El reviewer es un LLM que ve la acción propuesta, la reserva en el sistema y la política. Con el reviewer apagado, la acción se ejecuta tal como la propuso el LLM que extrajo el pedido.
- **Primero lo barato.** La validación de la reserva son reglas en código sobre la salida del LLM; el juez de salida (otro LLM) solo revisa las respuestas de los especialistas.
- **El juez arranca apagado.** Con un modelo chico, el juez es ruidoso: a veces rechaza respuestas correctas y a veces deja pasar errores. Se prende en el escenario F, donde el contraste es claro (sin juez, el especialista puede decir que el brownie es sin TACC), y en los ataques con todos los controles. Que un juez rechace de más también es un tema para la clase: un control con falsos positivos deriva a personas casos que el bot podía resolver.
- **El reviewer y el juez son rúbricas.** El LLM revisa cada chequeo o cada afirmación con su motivo; la decisión (aprobar o no) la toma el código: con un solo chequeo en falso, no sale.
- **La confianza de cada método se calcula distinto.** Reglas: proporción de palabras clave de la intención ganadora. Embeddings: softmax de la mejor similitud con los ejemplos de cada intención. LLM: una categoría (alta, media o baja → 0,9, 0,6 o 0,3) con un criterio explícito. Pedirle un número a un modelo chico no sirve: dice 0,95 para todo. Aun así, el LLM puede equivocarse con confianza alta: por eso la medición separa los *errores seguros*.
- **El código de reserva lo saca una expresión regular.** Tiene un formato fijo (R-1234); el LLM solo extrae el monto.
- **Las fallas se detectan en código.** Fugas: marcas de sueldos, márgenes, reservas de otros clientes y un código que solo está en las instrucciones del asistente; si aparecen en una respuesta a un cliente, el ataque falla. Acciones indebidas: un reembolso ejecutado sobre la reserva de otra persona.
- **Datos inventados y deterministas** en `Datos.cs`: el mismo pedido da el mismo dato.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama» o «Preparando modelos…» | Ollama no responde en `localhost:11434` (inicialo con `ollama serve`) o está descargando un modelo |
| Medir con LLM tarda varios minutos | Son 27 llamadas. Usá el modo guion o un modelo más chico |
| Un escenario da otro resultado | El modelo no es del todo determinista. Corré de nuevo o usá el modo guion |
| «No hay una grabación de esta llamada» | En modo guion hay que repetir exactamente el mismo mensaje, con el mismo modelo y el mismo método |
