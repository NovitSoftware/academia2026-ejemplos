# MesaYa · Proveedores y modelos por propósito (.NET)

Demo de la presentación **Módulo 4 · Unidad 1 · Proveedores y modelos por propósito**. El asistente de MesaYa manda pedidos a dos proveedores con el mismo código (Ollama local y la nube de NVIDIA), choca contra un rate limit, usa una familia de modelo distinta para cada paso y filtra datos personales antes de que salgan hacia un endpoint de prueba.

> Idea para llevar: *un modelo por tarea, un proveedor por requisito*. Cambiar de proveedor es cambiar URL base, clave y modelo; lo que no es portable vive en una sola clase.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks, con la misma base que las demos de las Clases 4 y 5.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| Modelos locales | Ollama en `localhost:11434` con `qwen3.5:2b` (económico), `qwen3.5:4b` (generalista y visión) y `bge-m3` (embeddings) |
| Escenarios con NVIDIA | Una API key de [build.nvidia.com](https://build.nvidia.com) (opcional) |

Si falta algún modelo local, la app lo descarga al iniciar.

## La clave de NVIDIA

Se lee en este orden:

1. La variable de entorno `NVIDIA_API_KEY`, si está definida.
2. Si no, `Proveedores:nvidia:ApiKey` de appsettings. `appsettings.Local.json` pisa a `appsettings.json`.

Para no exponer la clave, copiá `appsettings.Local.ejemplo.json` a `appsettings.Local.json` y poné ahí la clave real. Ese archivo está en el `.gitignore`. La interfaz muestra de dónde salió la clave, nunca su valor. Tampoco se guarda en `grabaciones/` ni en `datos/`.

Sin clave, la demo funciona igual: los pedidos a NVIDIA muestran el request que saldría y responden 401.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5700](http://localhost:5700). `Ctrl+C` la detiene. Otro puerto: `PUERTO=5701 ./iniciar.sh`. Otro Ollama: `OLLAMA_URL=http://otra-maquina:11434 ./iniciar.sh`.

## La pantalla

**Izquierda.** El proveedor (con la URL base y de dónde sale la clave), la familia de modelo (económico o generalista), el filtro de datos personales y los escenarios.

**Centro.** El registro: cada pedido con su proveedor, su modelo, sus tokens, el tiempo hasta el primer token y el tiempo total. Las ráfagas y los pipelines aparecen como tarjetas.

**Derecha.** Cuatro pestañas:

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Request | El request tal como sale: URL base, ruta, encabezados (la clave tapada) y cuerpo. Si hay un pedido a cada proveedor, una tabla con lo que cambia y lo que queda igual | 08, 09 |
| Límites | Requests y tokens usados en la ventana, cada pedido de la ráfaga en el tiempo (respondido, 429, espera, respaldo) y el registro de eventos | 11, 12 |
| Pipeline | Cada paso con su familia de modelo, sus tokens, su tiempo y su salida. Desde ahí se ve el request de cada paso | 13, 14, 15 |
| Configuración | Proveedores, modelos por familia, el orden de lectura de la clave y el límite simulado | 07, 10 |

## Escenarios para la clase

| | Escenario | Qué se ve |
|---|---|---|
| A | Cambiar de proveedor | El mismo pedido a Ollama y a NVIDIA. Cambian la URL base, la clave y el modelo; el resto del cuerpo es idéntico |
| B | Llegó un 429 | Ocho pedidos en paralelo contra el límite simulado. Sin reintentos fallan 3. El backoff ciego (1, 2 y 4 s) tampoco alcanza. Respetando `Retry-After` responden los 8, pero tardan. Con fallback responden los 8 y 3 van a un modelo chico local |
| C | Un modelo por propósito | Un mensaje de WhatsApp: el económico clasifica, `bge-m3` trae la política, el generalista llama a la tool y responde. Una factura: el modelo con visión la lee y el código detecta que el total no cierra ($45.300 contra $48.300) |
| D | Datos personales y endpoint de prueba | Sin filtro, el nombre, el teléfono y el correo viajan a NVIDIA (marcados en rojo en el cuerpo). Con filtro, salen reemplazados |

## Guion sugerido (~25 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–5 | 02–05 | Pestaña **Configuración**: los dos proveedores, sus URL y de dónde sale la clave |
| 5–10 | 08, 09 | **A**: el mismo pedido a los dos proveedores y la tabla de diferencias |
| 10–16 | 11, 12 | **B**: las cuatro estrategias, una tras otra, en la pestaña **Límites** |
| 16–22 | 13–15 | **C**: el mensaje de WhatsApp y la factura |
| 22–25 | 06, 07 | **D**: el mismo mensaje sin filtro y con filtro |

## Modo guion

Cada llamada que termina bien se guarda en `grabaciones/llm/`. Con **Modo guion** prendido se repite desde ahí sin llamar a los modelos. La carpeta ya trae grabados todos los escenarios, con Ollama y con NVIDIA (`nemotron-3.5-bolt-e4b` y `nemotron-3.5-lightning-30b-a3b`). La ráfaga está grabada contra los dos: va contra NVIDIA si hay clave y contra Ollama si no. Si cambiás un modelo, hay que correr ese escenario una vez en vivo.

En modo guion las respuestas llegan al instante, así que la ráfaga se ve más corta, pero los 429 y las esperas de `Retry-After` son reales.

## El límite simulado

En clase no se puede provocar un 429 real a pedido. El limitador responde 429 con `Retry-After` como lo haría el proveedor, sin llamarlo: 6 requests y 2.000 tokens cada 20 s, por proveedor y modelo. Cuenta los tokens estimados al enviar (mensajes + `max_tokens`), como los proveedores. Se ajusta en `appsettings.json` (sección `LimiteSimulado`). Con **Límite simulado** apagado, la ráfaga va contra el límite real del proveedor.

## Cómo está hecho

```text
Program.cs              API: requests, ráfagas, pipelines y lo que se guarda en datos/
Demo.cs                 los tres tipos de corrida: request, ráfaga con reintentos y fallback, pipeline por familias
Datos.cs                políticas de MesaYa, la tool de disponibilidad y el filtro de datos personales
Comun/Proveedores.cs    cliente único compatible con OpenAI: proveedores, clave, streaming, límite simulado y grabaciones
Comun/Local.cs          el Ollama local: modelos instalados y descargas
escenarios.json         escenarios A–D
appsettings.json        proveedores, modelos por familia y límite simulado
static/                 interfaz (HTML, CSS y JS sin build) con la guía visual de Novit; la factura en static/img/
```

## Decisiones y límites

- **Un solo cliente para todos.** Ollama y NVIDIA exponen `/v1/chat/completions` y hoy aceptan `response_format` (JSON Schema) y `reasoning_effort` igual que OpenAI. No todos los proveedores lo hacen: si uno rechaza el esquema, `Comun/Proveedores.cs` pide el JSON en el prompt y la respuesta lo indica. Antes NVIDIA usaba su propia extensión (`nvext.guided_json`), que hoy rechaza.
- **Sin audio.** El paso de audio del pipeline está marcado como omitido: la demo parte del texto ya transcripto.
- **La factura se valida en código.** El modelo con visión extrae los datos; la suma la controla el código, no otro modelo.
- **El filtro es por expresiones regulares.** Detecta correos, teléfonos, DNI y nombres después de "soy", "me llamo" o "a nombre de". Es un ejemplo, no un filtro de producción.
- **Modelos de NVIDIA.** `nvidia/nemotron-3.5-bolt-e4b` (económico) y `nvidia/nemotron-3.5-lightning-30b-a3b` (generalista). El catálogo cambia seguido: `../probar-modelos-nvidia.sh` prueba qué modelos responden y cuáles hacen tool calling, y se cambian en `appsettings.json`.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama» o «Preparando…» | Ollama no responde en `localhost:11434` (`ollama serve`) o está descargando un modelo |
| NVIDIA responde 401 | Falta la clave: definí `NVIDIA_API_KEY` o creá `appsettings.Local.json` |
| NVIDIA responde 404 o 410 | El modelo no está en el catálogo o fue dado de baja: corré `../probar-modelos-nvidia.sh` y cambialo en `appsettings.json` |
| La ráfaga no llega al límite | Con el límite simulado apagado, depende del límite real del proveedor |
| «No hay una grabación de esta llamada» | En modo guion hay que repetir exactamente el mismo escenario, con los mismos modelos |
