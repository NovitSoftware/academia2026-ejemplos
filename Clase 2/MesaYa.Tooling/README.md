# MesaYa.Tooling

## Naturaleza y responsabilidad

Aplicación web **Blazor Interactive Server sobre .NET 8** para mostrar cómo un modelo elige herramientas y cómo el host las valida y ejecuta. Es la interfaz del motor de [`MesaYa.Agent`](../MesaYa.Agent/README.md): cada sesión del navegador tiene una conversación y su propia confirmación humana.

El flujo es `Navegador → Blazor → AgenteReservas → MesaYa.Api`; en modo real, el agente también consulta Ollama. Las herramientas están definidas en la biblioteca y llaman a la API por HTTP. Esta aplicación funciona sin iniciar `MesaYa.Mcp`.

## Requisitos y dependencias

- SDK .NET 8 o posterior para compilar, con runtime ASP.NET Core 8 para ejecutar. Instalando el SDK .NET 8 se incluye ese runtime.
- `MesaYa.Api` en `http://localhost:5080` para consultar disponibilidad y crear reservas, incluso en los modos simulados.
- Ollama en `http://localhost:11434` y un modelo instalado con soporte de tools para **Ollama local**. Los tres modos de demo funcionan sin Ollama.
- Navegador con JavaScript habilitado. La conversación se mantiene en el servidor mediante el circuito interactivo de Blazor.

Podés iniciar la API, esta aplicación y MCP con `./iniciar-mesaya.sh` desde la raíz. Para ejecutar solo API y Tooling, seguí los comandos de abajo en terminales separadas.

## Ejecutar desde la raíz de la solución

Desde la raíz de la solución, iniciá la API y la web en terminales separadas:

```bash
# Terminal 1
dotnet run --project MesaYa.Api
```

```bash
# Terminal 2
dotnet run --project MesaYa.Tooling
```

Abrí **http://localhost:5090**. La API usa `http://localhost:5080`. Para conversar con el modelo real, Ollama debe estar corriendo y tener al menos un modelo instalado.

La interfaz usa Bootstrap y conserva el diseño original en tonos verdes, con fuentes más grandes: mensajes de 18 px, controles de 16 px y texto secundario desde 14 px.

La pantalla incluye:

- **Chat** con el agente y un ejemplo de pedido para empezar.
- **Confirmación de reserva** con sucursal, fecha, hora, personas y celular. Los botones **Confirmar reserva** y **Rechazar** resuelven la aprobación pendiente del agente.
- **Actividad** con tools, validaciones, resultados JSON, llamadas HTTP y reintentos.
- **Contrato de tools** para consultar los esquemas que recibe el modelo.
- **Selector de modo**: Ollama local, demo guiada, demo con error 503 y demo con timeout. Las demos avanzan con **Iniciar demo** y **Continuar demo**, sin necesitar Ollama.
- **Modelo local**: elegí directamente en la pantalla entre los modelos instalados en Ollama. El botón **↻** actualiza la lista si instalaste o eliminaste un modelo.
- **Estado de la API**, en la barra izquierda: verde cuando responde y rojo cuando no está disponible. Se consulta `/health` al abrir la página y cada 10 segundos, con un timeout de 3 segundos, usando `MESAYA_URL` o `MesaYa:Url`.
- **Consumo de tokens**, debajo del modelo en la barra lateral derecha: entrada, salida, total acumulado de la conversación, cantidad de respuestas medidas y velocidad de generación de la última respuesta y promedio.
- **Nueva conversación** para reiniciar el historial y el contador de reservas. Las reservas ya creadas en la API se conservan.

### Elegir el modelo desde Blazor

Seleccioná **Ollama local** en el modo de conversación y elegí un nombre en **Modelo local**. La lista se consulta automáticamente a través de [`GET /api/tags` de Ollama](https://docs.ollama.com/api/tags). No necesitás editar `appsettings.json` ni reiniciar la web para cambiar el modelo.

Cambiar de modelo inicia una conversación nueva. La elección tiene prioridad sobre `OLLAMA_MODEL` y el modelo de configuración, y se conserva al usar **Nueva conversación** o al alternar con los modos de demo. Es independiente para cada sesión y se pierde al recargar la página. Durante un turno o una confirmación pendiente, el selector queda bloqueado.

Al abrir la página, se usa el modelo inicial configurado si está instalado; si no está en la lista, se selecciona el primero disponible. Si la lista está vacía o no se puede consultar Ollama, se muestra un mensaje con la posibilidad de actualizarla. Los modos simulados funcionan sin cargar modelos.

### Métricas de Ollama

El panel **Consumo de tokens** usa los [contadores originales de Ollama](https://docs.ollama.com/api/usage), sin estimarlos a partir del texto. Se actualiza al finalizar cada respuesta, incluidas las iteraciones en las que el modelo solicita tools. El acumulado suma las respuestas de la conversación que tienen contadores válidos y muestra cuántas fueron medidas; el historial reenviado puede contarse en varias entradas.

La velocidad de salida se calcula como `eval_count / (eval_duration / 1_000_000_000)`. Excluye la carga del modelo, la evaluación del prompt y la espera de confirmación humana. El promedio se obtiene dividiendo los tokens de salida acumulados por el tiempo de generación acumulado, en lugar de promediar las velocidades individuales.

En **Desglose y tiempos** se muestran los tokens de la última respuesta y los tiempos de carga, evaluación de entrada, generación y total del servidor. Si la versión de Ollama informa `prompt_eval_cached_count`, se separa la entrada desde caché y sin caché. La caché es un subconjunto de la entrada y no se suma nuevamente al total. Los datos ausentes se muestran como no disponibles; no se inventa un desglose entre texto, argumentos de tools o razonamiento que Ollama no informa.

Las métricas se reinician con **Nueva conversación**, al cambiar de modelo o al alternar modos. Las demos simuladas no consumen tokens de Ollama. Al usar respuestas sin streaming, las cifras y velocidades corresponden a respuestas terminadas y no a una medición instantánea durante la generación.

### Configuración de conexiones

[`MesaYa.Tooling/appsettings.json`](appsettings.json) configura las conexiones y, opcionalmente, el modelo inicial:

```json
{
  "Ollama": {
    "Model": "qwen3.5:9b",
    "Url": "http://localhost:11434"
  },
  "MesaYa": {
    "Url": "http://localhost:5080",
    "Token": "demo-agente-token"
  }
}
```

Este ejemplo muestra las secciones del agente; el archivo también incluye las opciones de logs. Las variables `OLLAMA_URL` y `MESAYA_URL` tienen prioridad sobre las direcciones del archivo. `OLLAMA_MODEL` define el modelo inicial por encima de `Ollama.Model`; el selector de la interfaz tiene prioridad sobre ambos. Reiniciá la web después de modificar las conexiones.

La web usa el motor de `MesaYa.Agent`: límite de seis iteraciones por turno, máximo de tres reservas por conversación, validaciones y ejecución de la API. Las conexiones a Ollama y a MesaYa.Api se hacen desde el servidor Blazor. Cada circuito tiene su historial y confirmación; recargar la página inicia una conversación nueva. `MesaYa.Agent` es una biblioteca; se ejecuta a través de `MesaYa.Tooling`.

## Preparar Ollama local

Para usar el modelo local en `MesaYa.Tooling`, necesitás [.NET 8 SDK](https://dotnet.microsoft.com/download), [Ollama](https://ollama.com/) y el modelo elegido instalado:

```bash
ollama pull qwen3.5:9b
```

Ollama debe estar corriendo en `http://localhost:11434`. Si no está iniciado, ejecutá `ollama serve` en otra terminal. Mantené también `MesaYa.Api` corriendo y abrí la web de `MesaYa.Tooling` en `http://localhost:5090`.

Seleccioná **Ollama local** y escribí, por ejemplo:

```text
Hola, quiero una mesa para 4 el viernes a las 21 en Palermo. Mi celular es +5491155550123.
```

El modelo decide qué tools llamar; la secuencia puede variar respecto del guion simulado. No necesita API key.

La configuración inicial está en [`MesaYa.Tooling/appsettings.json`](appsettings.json). También podés definir `OLLAMA_MODEL` y `OLLAMA_URL` al iniciar la web:

```bash
OLLAMA_MODEL=qwen3.5:9b OLLAMA_URL=http://localhost:11434 dotnet run --project MesaYa.Tooling
```

El adaptador usa [la API de chat y tools de Ollama](https://docs.ollama.com/capabilities/tool-calling), conserva el historial de llamadas y devuelve sus resultados al modelo. El host sigue validando y pidiendo confirmación antes de reservar. Desactiva el modo de razonamiento con `think: false` y permite hasta cinco minutos por respuesta para la ejecución local.

Para provocar errores, probá pedidos como "mesa para 15", "para ayer", "a las 18" o "en Caballito" (este último devuelve 403 porque el token del agente no tiene permiso sobre esa sucursal). Los modos **Demo guiada**, **Demo · error 503** y **Demo · timeout** funcionan sin Ollama.

### Si no conecta

- **Ollama no responde:** verificá `Ollama.Url` e iniciá el servidor con `ollama serve` si no está corriendo.
- **El modelo no está instalado:** ejecutá `ollama pull` con el nombre configurado en `Ollama.Model`.
- **Aparece otro modelo seleccionado:** revisá `OLLAMA_MODEL`, la configuración inicial y la selección de la interfaz.
- **La API de reservas no responde:** iniciá `MesaYa.Api`. Si usa otra dirección, configurá `MESAYA_URL` en el entorno de la web; por defecto es `http://localhost:5080`.

## Organización del código

| Archivo o carpeta | Responsabilidad |
|---|---|
| `Program.cs` | Registra Blazor, sesiones y clientes HTTP; publica la web |
| `Components/Pages/Home.razor` | Chat, modos de demo, confirmación, actividad y consumo de tokens |
| `Services/SesionAgente.cs` | Crea el motor, administra el estado y resuelve la confirmación humana |
| `Services/CatalogoOllama.cs` | Consulta los modelos locales disponibles |
| `Services/EstadoApi.cs` | Consulta la salud de la API |
| `wwwroot/` | Bootstrap, estilos y scripts locales |

## Dónde ocurre el reintento

En el modo **Demo · error 503**, el guion agrega la nota `#falla503` a la reserva de las **22:15**. Después de la confirmación humana, la API devuelve 503 en el primer intento de esa reserva.

El método `Enviar` de [`MesaYaClient.cs`](../MesaYa.Agent/MesaYaClient.cs) detecta el error transitorio, espera **500 ms** y vuelve a enviar el pedido con la misma `Idempotency-Key`. En el panel **Actividad** aparecen `Error transitorio (NO_DISPONIBLE)` y `(reintento 1)`.

El cliente permite hasta dos reintentos adicionales al pedido inicial, con esperas de 500 ms y 1 segundo. También reintenta ante HTTP 429 y timeout; un 409 por falta de disponibilidad vuelve al modelo para que ofrezca alternativas.

## Qué muestra el modo simulado

En el modo simulado las decisiones del modelo siguen un guion fijo, para que la demo siempre muestre todos los casos. La validación, la API, los reintentos y la idempotencia son reales.

| Paso | Qué pasa | Qué mirar |
|---|---|---|
| 1 | El modelo manda `"personas": "cuatro"` | Falla la capa **sintáctica**: la API **no** se llama y el error vuelve al modelo |
| 2 | Corrige a `"personas": 4` | Pasan las 3 capas, confirmación humana, `POST /v1/reservas` → **409 SIN_DISPONIBILIDAD** con alternativas |
| 3 | El modelo ofrece 20:30 o 22:15 | El modelo lee el error y se lo explica al usuario |
| 4 | El usuario elige 22:15 | `201 Created` con `reserva_id` |
| 5 | Confirmación final | El modelo responde con los datos que devolvió la API |

## Guion sugerido (≈ 5 minutos)

1. **Contrato.** Abrí `MesaYa.Agent/Herramientas.cs`: el modelo solo ve nombre, descripción y esquema. Iniciá `MesaYa.Tooling` y abrí **Contrato de tools**.
2. **Validación.** En la salida, el paso 1 muestra que el código no confía en el modelo. Abrí `MesaYa.Agent/Validador.cs`.
3. **Ejecución.** Seguí los eventos **HTTP** del panel **Actividad**: token, Idempotency-Key, 409 con alternativas y el modelo que se recupera.
4. **Controles.** Seleccioná **Demo · error 503** (reintento con la misma clave) y **Demo · timeout** (timeout y `Idempotent-Replayed`: el primer intento sí había llegado, y la idempotencia evitó la reserva doble).
5. **La API sola.** En `api.http`, ejecutá dos veces la request 2 para ver la idempotencia, y la 4 para ver el 403.

## Notas de diseño

- La `Idempotency-Key` la arma el **host** a partir de la conversación y los argumentos, no el modelo. Si el modelo o el runtime repiten la misma llamada, la API devuelve la misma reserva.
- La API vuelve a validar sus reglas aunque el agente ya lo haya hecho: defensa en profundidad.
- Los errores de ejecución vuelven como `tool_result` con `is_error: true` y un JSON `{codigo, mensaje, reintentable, detalle}`.
- Todo está en memoria: al reiniciar la API se pierden las reservas.

[Volver al README principal](../README.md).
