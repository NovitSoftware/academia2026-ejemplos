# MesaYa.Mcp

## Naturaleza y responsabilidad

Aplicación **ASP.NET Core y Blazor Interactive Server sobre .NET 10** que reúne un servidor MCP, un agente cliente MCP y pantallas para observar el protocolo. Usa `ModelContextProtocol.AspNetCore` 2.3.0 y reutiliza las validaciones de [`MesaYa.Agent`](../MesaYa.Agent/README.md).

Tiene su propio ciclo de agente en `Agente/SesionAgente.cs`: descubre las herramientas del servidor por MCP y las entrega al modelo. El motor `AgenteReservas` de Tooling usa definiciones fijas; aquí las herramientas se descubren mediante el protocolo.

## Requisitos y dependencias

- SDK .NET 10 y runtime ASP.NET Core 10, incluido en ese SDK.
- Acceso a NuGet en la primera restauración de `ModelContextProtocol.AspNetCore` y sus dependencias.
- `MesaYa.Api` en `http://localhost:5080` para las operaciones de reservas, menú y disponibilidad.
- Ollama y un modelo local con soporte de tools para conversar con el modelo real. **Guion de demo** e **Inspector MCP** funcionan sin LLM.
- Navegador para las pantallas Blazor, o un cliente MCP externo para conectarse a `/mcp`.

`MesaYa.Tooling` se puede iniciar por separado; MCP no depende de esa aplicación. Para levantar toda la demo usá `./iniciar-mesaya.sh` desde la raíz.

## Ejecutar desde la raíz de la solución

`MesaYa.Mcp` es un proyecto Blazor que **es un servidor MCP** (en `/mcp`, Streamable HTTP) y a la vez un **agente** que lo usa como cliente MCP con un modelo local de Ollama:

```text
Navegador ─ Blazor ─ Agente (host) ─ McpClient ──JSON-RPC/HTTP──▶ /mcp (servidor MCP) ─▶ MesaYa.Api :5080
                        │
                        └── Ollama :11434 (tool calling nativo)
```

```bash
# Terminal 1
dotnet run --project MesaYa.Api
```

```bash
# Terminal 2
dotnet run --project MesaYa.Mcp
```

Abrí **http://localhost:5100**. Tiene tres pestañas:

| Pestaña | Qué muestra |
|---|---|
| **🤖 Agente** | Chat, selector de modelo local (lee `GET /api/tags` y marca los que no soportan tools), selector de escenario, y el panel **Tool calling**: cada iteración del loop (pedido a Ollama, `tool_calls`, `tools/call` por MCP, progreso y resultado). Cuando el servidor pide confirmación aparece el formulario de **elicitation**. La opción *🎬 Guion de demo* funciona sin Ollama. |
| **📈 Secuencia en vivo** | Abrila en otra pestaña (botón *Abrir secuencia ↗*). Dibuja en tiempo real el orden de los mensajes entre Usuario, Agente, LLM, Cliente MCP, Servidor MCP y la API. Tiene tres vistas: **diagrama de secuencia** (las esperas aparecen como franjas ⏳ proporcionales al tiempo), **línea de tiempo** (se ve el paralelismo de las tools) y **mensajes JSON-RPC** (los mensajes reales del protocolo). Hacé clic en cualquier flecha para ver el JSON. |
| **🔍 Inspector MCP** | Un cliente MCP sin LLM: `serverInfo`, capacidades, tools con sus anotaciones y esquemas, resources, prompts, ejecución manual de `tools/call` y cómo conectar clientes externos. |

### Barra lateral de consumo y ejecución

La pestaña **Agente** incluye una barra lateral derecha con tokens totales de la conversación, separados en entrada y salida, cantidad de respuestas medidas, velocidad promedio de generación y velocidad de la última respuesta. En pantallas angostas el panel se ubica debajo del contenido.

Los contadores se toman de `prompt_eval_count` y `eval_count` de Ollama, incluidas las respuestas que piden herramientas. La velocidad se calcula como tokens de salida divididos por `eval_duration` en segundos. El promedio usa la suma de tokens y tiempos de generación; excluye carga del modelo, evaluación del prompt y ejecución de herramientas. Los datos faltantes se muestran con un guion y se indica cuando el consumo acumulado es parcial.

El **tiempo de ejecución** se actualiza durante el turno y suma modelo, herramientas y espera de confirmación humana. También muestra el tiempo del turno actual o del último turno terminado. No cuenta las pausas entre mensajes ni la conexión inicial al servidor MCP. Si se detiene un turno o falla, conserva el tiempo transcurrido y las métricas ya recibidas.

**Nueva conversación** y cambiar de modelo reinician los contadores y tiempos. El **Guion de demo** mide tiempo de ejecución y muestra cero tokens porque no llama a Ollama. Las métricas pertenecen a la conversación del agente web, no a otros clientes MCP conectados al servidor.

**Qué expone el servidor MCP**

- **Tools:** `listar_sucursales`, `consultar_menu`, `buscar_disponibilidad`, `reservar_mesa` (valida con las tres capas de `MesaYa.Agent`, pide confirmación con **elicitation** y usa `Idempotency-Key`) y `cancelar_reserva`.
- **Resources:** `mesaya://sucursales`, `mesaya://menu/{sucursal_id}`, `mesaya://escenario`.
- **Prompts:** `pedir_reserva`, `comparar_sucursales`.
- **Notificaciones:** `notifications/progress` durante las esperas y reintentos, `notifications/message` con los logs del servidor.

**Escenarios** (selector en la pestaña Agente, aplican a todo el servidor): *Normal*, *Red lenta* (0,6–2,5 s por llamada a la API), *Falla transitoria* (503 en el primer intento de cada reserva y reintento con backoff) y *Saturado* (2–4 s por llamada + 503). Probá *Saturado* con "¿Qué horarios hay el sábado en Palermo, Belgrano y Caballito?": se ven tres `tools/call` en paralelo, cada una con su demora.

**Conectar otro cliente MCP** al mismo servidor (sus llamadas también aparecen en la secuencia):

```bash
claude mcp add --transport http mesaya http://localhost:5100/mcp
```

```bash
npx @modelcontextprotocol/inspector
```

Configuración en [`MesaYa.Mcp/appsettings.json`](appsettings.json): `Ollama:Url`, `Ollama:ModeloInicial`, `MesaYa:Url`, `MesaYa:Token` y `Mcp:UrlPublica`. También se pueden usar `OLLAMA_URL`, `OLLAMA_MODEL`, `MESAYA_URL` y `MCP_URL`. Usa .NET 10 y el SDK oficial `ModelContextProtocol.AspNetCore` 2.3; el agente fija la revisión del protocolo `2025-11-25`.

## Configuración

| Opción de `appsettings.json` | Variable de entorno | Valor inicial |
|---|---|---|
| `MesaYa:Url` | `MESAYA_URL` | `http://localhost:5080` |
| `MesaYa:Token` | `MesaYa__Token` | `demo-agente-token` |
| `Ollama:Url` | `OLLAMA_URL` | `http://localhost:11434` |
| `Ollama:ModeloInicial` | `OLLAMA_MODEL` | `qwen3.5:9b` |
| `Mcp:UrlPublica` | `MCP_URL` | `http://localhost:5100` |

`MCP_URL` es la URL base, **sin `/mcp`**: el cliente interno agrega esa ruta. Si cambiás el puerto al ejecutar manualmente, actualizá también esa URL. Las variables específicas de la tabla tienen prioridad sobre los valores del archivo. El script principal fija las conexiones locales entre sus tres servicios.

## Organización del código

| Carpeta o archivo | Responsabilidad |
|---|---|
| `Program.cs` | Configura Blazor, SDK MCP, transporte HTTP y endpoint `/mcp` |
| `Servidor/HerramientasMesaYa.cs` | Tools, validación, confirmación por elicitation y ejecución |
| `Servidor/ApiMesaYa.cs` | Cliente HTTP de la API con reintentos y escenarios de demora |
| `Servidor/RecursosYPrompts.cs` | Resources y prompts del servidor |
| `Servidor/Escenarios.cs` | Estado de las fallas y demoras simuladas |
| `Agente/` | Sesión del agente, cliente MCP y modelos real o guionado |
| `Traza/` | Eventos y filtros para capturar los mensajes del protocolo |
| `Components/Pages/` | Agente (`/`), secuencia (`/secuencia`) e inspector (`/inspector`) |

Las trazas y escenarios pertenecen al proceso; el selector de escenario afecta a las llamadas del servidor. Las reservas viven en la API y se pierden al reiniciarla.

[Volver al README principal](../README.md).
