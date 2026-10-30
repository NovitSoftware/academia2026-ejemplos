# MesaYa.VSCodeExtension · Chat MCP para VS Code

## Naturaleza y responsabilidad

Extensión de **Visual Studio Code escrita en TypeScript**, con un proceso de extensión en Node.js y un chat en un webview. Implementa su propio ciclo de agente y un cliente MCP con el SDK de TypeScript. Se compila con esbuild y se distribuye como `mesaya-vscode.vsix`; no forma parte de la solución .NET.

Un chat en el panel lateral de VS Code que funciona como el agente de `MesaYa.Mcp`: le escribís lo que querés y un **modelo local de Ollama** decide qué tools usar del **servidor MCP de MesaYa**.

La extensión es un **host MCP genérico**: no tiene ninguna tool escrita en el código. Al conectarse pide `tools/list`, le pasa esas tools al modelo, ejecuta con `tools/call` las que el modelo elija y le devuelve los resultados. Si el servidor agrega una tool nueva, el chat la puede usar sin cambiar la extensión.

```text
VS Code (chat) ─▶ Ollama (decide)            
      │                                      
      └── cliente MCP ──JSON-RPC/HTTP──▶ MesaYa.Mcp /mcp ──▶ MesaYa.Api
```

## Qué podés hacer

- **Reservar:** "Quiero una mesa para 4 el viernes a las 21 en Palermo, mi cel es +5491155550123". El servidor pide la confirmación con **elicitation** y aparece un formulario en el chat, con un campo para la nota.
- **Saber si se pudo y por qué no:** el modelo explica el motivo con el código que devolvió el servidor (`SIN_DISPONIBILIDAD`, `FORBIDDEN`, `FECHA_PASADA`…) y ofrece alternativas.
- **Consultar y bajar el menú:** "Mostrame el menú de Belgrano". La respuesta tiene **Descargar .md** y **Abrir en editor** (con `mesaya.mostrarTools`, el resultado crudo de la tool también se puede bajar como JSON).
- **Consumo de tokens:** debajo del cuadro de texto se ve el total de tokens de la conversación (entrada ↑ y salida ↓) y la velocidad de generación en tokens por segundo, de la última respuesta y el promedio.
- **Cancelar:** "Cancelá esa reserva". El modelo usa el `reserva_id` de la conversación.
- **Ver el protocolo** (con `mesaya.mostrarTools` activado): cada vuelta del loop muestra el pedido a Ollama, los `tools/call`, los avisos de progreso y el resultado. El botón **📈 Secuencia** abre la Secuencia en vivo de `MesaYa.Mcp`, donde la extensión aparece como "VS Code · Chat MesaYa".

## Cómo usarla

Para instalar el paquete necesitás **VS Code 1.95 o posterior**. Para conversar, necesitás Ollama con un modelo instalado que soporte tools, y `MesaYa.Api` y `MesaYa.Mcp` corriendo. Tooling no es necesario para la extensión.

Desde la raíz podés iniciar los servicios con `./iniciar-mesaya.sh` (también inicia Tooling), o ejecutar los siguientes comandos en terminales separadas. Los requisitos de .NET de los servicios están en `README.md` del directorio principal de la solución.

```bash
dotnet run --project MesaYa.Api
```

```bash
dotnet run --project MesaYa.Mcp
```

**Instalarla:**

```bash
code --install-extension MesaYa.VSCodeExtension/mesaya-vscode.vsix
```

Después hacé clic en el ícono de MesaYa en la barra lateral.

**Desarrollarla:** necesitás **Node.js 22 o posterior y npm**; el empaquetador `@vscode/vsce` requiere esa versión mínima. Instalá las dependencias y compilá desde la raíz:

```bash
cd MesaYa.VSCodeExtension
npm ci
npm run typecheck
npm run build
```

Abrí la carpeta `MesaYa.VSCodeExtension` como carpeta de trabajo en VS Code y apretá **F5** (*Run Extension*). Compila con esbuild y abre una ventana nueva con la extensión cargada. La ventana principal conserva el código y el depurador.

Para generar o actualizar el paquete instalable, desde la carpeta de la extensión:

```bash
npm run package
```

Produce `mesaya-vscode.vsix` en esa misma carpeta. La primera instalación con `npm ci` necesita acceso al registro npm. Node.js y npm se usan para desarrollar y empaquetar; VS Code proporciona el entorno de ejecución de la extensión instalada.

## Configuración

Las opciones se editan en la configuración de VS Code (`mesaya.*`); la extensión no carga los `appsettings.json` de los proyectos .NET. Configurá API y permisos en MCP, porque es el servidor quien llama a la API.

| Opción | Por defecto | Para qué |
|---|---|---|
| `mesaya.mcpUrl` | `http://localhost:5100/mcp` | Servidor MCP (Streamable HTTP) |
| `mesaya.ollamaUrl` | `http://localhost:11434` | Servidor de Ollama |
| `mesaya.modelo` | `qwen3.5:9b` | Modelo local; también se elige desde el chat |
| `mesaya.toolsEnParalelo` | `true` | Ejecutar en paralelo varias tool_calls de una misma respuesta |
| `mesaya.mostrarTools` | `false` | Mostrar en el chat las llamadas al modelo y los `tools/call` (para demos). Apagado, solo se ven los mensajes y las confirmaciones |

Comandos: **MesaYa: Nueva conversación** (abre una sesión MCP nueva), **Reconectar al servidor MCP**, **Abrir la secuencia en vivo** y **Ver log MCP** (canal de salida con conexión, `tools/call`, elicitations y logs del servidor).

## Probar sin VS Code

`npm run probar` corre el mismo loop desde la terminal contra el servidor real y acepta las confirmaciones solo:

```bash
npm run probar -- "Mostrame el menú de Belgrano" "Reservá para 2 en Belgrano mañana a las 13:30, mi cel es +5491155550123" "Cancelá esa reserva"
```

`scripts/vista-previa.html` muestra el chat en un navegador con una API de VS Code simulada, para revisar la interfaz.

## Código

| Archivo | Qué hace |
|---|---|
| `src/mcp.ts` | Cliente MCP con el SDK oficial: conexión, `tools/list`, `tools/call` con progreso, elicitation y logs |
| `src/ollama.ts` | `/api/tags` y `/api/chat` con tool calling |
| `src/agente.ts` | Loop agéntico: modelo → tools por MCP → modelo |
| `src/extension.ts` | Panel de chat, comandos, confirmaciones y descargas |
| `src/webview/main.ts`, `media/chat.css` | Interfaz del chat con los colores del tema de VS Code |

Con modelos chicos como `qwen3.5:9b`, a veces el modelo consulta la disponibilidad antes de reservar o te pide confirmar él mismo. Con modelos más grandes sigue mejor las instrucciones.

La guía general de la solución está en `README.md` del directorio principal.
