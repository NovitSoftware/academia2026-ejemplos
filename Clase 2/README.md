# MesaYa · laboratorio de agentes y herramientas

Solución de demostración para observar cómo un agente convierte un pedido en lenguaje natural en llamadas a herramientas: el modelo propone, el host valida, el usuario confirma y una API ejecuta la operación. Incluye una implementación con herramientas definidas en código y otra que descubre las herramientas a través de MCP.

## Proyectos y su naturaleza

| Proyecto | Naturaleza y responsabilidad | Cómo se usa |
|---|---|---|
| [MesaYa.Api](MesaYa.Api/README.md) | API REST en ASP.NET Core .NET 8. Disponibilidad, menú, reservas, cancelaciones, permisos e idempotencia; datos en memoria | Servicio HTTP en `http://localhost:5080` |
| [MesaYa.Agent](MesaYa.Agent/README.md) | Biblioteca .NET 8. Motor del agente, herramientas fijas, validaciones, cliente HTTP y adaptador de Ollama | Se compila como dependencia; Tooling ejecuta el motor y MCP reutiliza sus validaciones |
| [MesaYa.Tooling](MesaYa.Tooling/README.md) | Aplicación Blazor Interactive Server .NET 8. Chat, herramientas directas a la API, confirmación humana, modos simulados y actividad del agente | Web en `http://localhost:5090` |
| [MesaYa.Mcp](MesaYa.Mcp/README.md) | Aplicación ASP.NET Core y Blazor .NET 10. Servidor MCP, agente cliente MCP, inspector y visualización del protocolo | Web en `http://localhost:5100`; endpoint MCP en `http://localhost:5100/mcp` |
| [MesaYa.VSCodeExtension](MesaYa.VSCodeExtension/README.md) | Extensión TypeScript para VS Code. Chat con Ollama y cliente MCP que descubre y ejecuta las herramientas publicadas por el servidor | Se instala como `.vsix` o se ejecuta en un Extension Development Host con F5 |

`MesaYa.Agent` es una biblioteca y no se inicia con `dotnet run`. La extensión tampoco es un proyecto .NET; tiene sus propios comandos npm. `MesaYaDemo.sln` contiene los cuatro proyectos .NET.

## Cómo se relacionan

```text
Tooling :5090 ── motor MesaYa.Agent ── HTTP ──────────────┐
                        │                               │
                        └── Ollama :11434               ▼
                                                    API :5080
MCP web :5100 ── agente ── cliente MCP ── /mcp ── HTTP ──▲
                  │                      ▲
                  └── Ollama :11434      │
                                        │
VS Code ── agente TypeScript ── cliente MCP
                  │
                  └── Ollama :11434
```

Tooling y MCP son dos recorridos alternativos para la clase. Tooling llama directamente a la API; MCP permite observar el descubrimiento de herramientas, los mensajes JSON-RPC, las notificaciones y la confirmación mediante elicitation. La extensión usa el mismo servidor MCP que la web.

## Qué necesitás instalar

| Para qué | Requisitos |
|---|---|
| Ejecutar toda la solución .NET | SDK .NET 8 y SDK .NET 10 instalados en paralelo. Incluyen los runtimes ASP.NET Core 8 y 10 requeridos por los proyectos |
| Ejecutar solo API y Tooling | SDK .NET 8 y su runtime ASP.NET Core 8; un SDK posterior también puede compilar `net8.0`, pero necesita el runtime 8 para ejecutar |
| Compilar o ejecutar MCP | SDK .NET 10 con su runtime ASP.NET Core 10. La primera restauración necesita acceso a NuGet para el SDK de MCP |
| Usar el script de inicio | Bash y curl en Linux, macOS o WSL, además de los requisitos .NET. Puertos 5080, 5090 y 5100 libres |
| Conversar con un modelo real | Ollama iniciado, un modelo instalado con soporte de tools y recursos locales suficientes para ese modelo |
| Instalar la extensión ya empaquetada | VS Code 1.95 o posterior y el archivo `mesaya-vscode.vsix`; API, MCP y Ollama activos para conversar |
| Desarrollar o empaquetar la extensión | Node.js 22 o posterior, npm y acceso al registro npm para instalar dependencias; VS Code para depurar con F5 |

Comprobá las instalaciones de .NET:

```bash
dotnet --list-sdks
dotnet --list-runtimes
```

Para una demo sin modelo real, no necesitás Ollama: Tooling ofrece **Demo guiada**, **Demo · error 503** y **Demo · timeout**; MCP ofrece **Guion de demo** e **Inspector MCP**. Esos recorridos siguen usando la API real. La extensión requiere Ollama para su conversación.

## Iniciar API, Tooling y MCP juntos

Desde el directorio principal:

```bash
./iniciar-mesaya.sh
```

También podés usar `bash iniciar-mesaya.sh`. El script resuelve las rutas desde su propia ubicación, por lo que puede invocarse desde otro directorio.

El script comprueba herramientas, runtimes y puertos, compila la solución en Debug, inicia la API y espera su endpoint `/health`. Luego inicia Tooling y MCP y verifica que respondan sus páginas. Las aplicaciones se ejecutan en Development, con sus archivos de configuración y recursos locales.

| Servicio | Dirección |
|---|---|
| Salud de la API | [http://localhost:5080/health](http://localhost:5080/health) |
| Tooling | [http://localhost:5090](http://localhost:5090) |
| Agente MCP | [http://localhost:5100](http://localhost:5100) |
| Inspector MCP | [http://localhost:5100/inspector](http://localhost:5100/inspector) |
| Secuencia MCP | [http://localhost:5100/secuencia](http://localhost:5100/secuencia) |
| Endpoint para clientes MCP | `http://localhost:5100/mcp` |

La terminal permanece abierta. **Ctrl+C detiene los tres procesos que inició el script**. Si uno falla, el script muestra el final de su log y detiene los restantes. Si un puerto ya está ocupado, informa el conflicto antes de iniciar servicios.

Los logs se guardan por proyecto en `.logs/mesaya/`, se reemplazan en cada ejecución y están excluidos del control de versiones. Para seguirlos desde otra terminal situada en la raíz:

```bash
tail -f .logs/mesaya/MesaYa.Api.log .logs/mesaya/MesaYa.Tooling.log .logs/mesaya/MesaYa.Mcp.log
```

El script no inicia Ollama ni la extensión. Respeta `OLLAMA_URL` y `OLLAMA_MODEL`; fija `MESAYA_URL=http://localhost:5080` y `MCP_URL=http://localhost:5100` para conectar los servicios que levanta. Consultá `./iniciar-mesaya.sh --help` para ver su ayuda.

## Ejecutar servicios por separado

Todos los comandos se ejecutan desde la raíz. Usá una terminal por proceso y mantené la API activa antes de operar desde las interfaces.

```bash
# Terminal 1: servicio de negocio
dotnet run --project MesaYa.Api --launch-profile http
```

```bash
# Terminal 2: agente con herramientas directas
dotnet run --project MesaYa.Tooling --launch-profile http
```

```bash
# Terminal 3: servidor MCP, agente e inspector
dotnet run --project MesaYa.Mcp --launch-profile http
```

Elegí qué iniciar según el recorrido:

| Recorrido | Procesos necesarios | Qué abrir |
|---|---|---|
| Probar la API por HTTP | API | `MesaYa.Api/api.http` o curl |
| Herramientas directas sin LLM | API + Tooling | `:5090`, modo **Demo guiada** |
| Herramientas directas con modelo | API + Tooling + Ollama | `:5090`, modo **Ollama local** |
| MCP sin LLM | API + MCP | `:5100`, **Guion de demo**, o `/inspector` |
| MCP con modelo en el navegador | API + MCP + Ollama | `:5100`, seleccionar un modelo local |
| Chat desde VS Code | API + MCP + Ollama + extensión | Panel MesaYa de VS Code; Tooling es opcional |

## Preparar Ollama y la extensión

Para usar el modelo configurado inicialmente, descargalo:

```bash
ollama pull qwen3.5:9b
```

Si Ollama no está iniciado, ejecutá `ollama serve` en otra terminal. La dirección inicial es `http://localhost:11434`. Podés seleccionar otro modelo instalado con soporte de herramientas desde las interfaces. La configuración de Tooling usa `Ollama:Model`; la de MCP usa `Ollama:ModeloInicial`.

Con API, MCP y Ollama activos, instalá la extensión desde la raíz:

```bash
code --install-extension MesaYa.VSCodeExtension/mesaya-vscode.vsix
```

Si el paquete todavía no existe, generalo:

```bash
cd MesaYa.VSCodeExtension
npm ci
npm run typecheck
npm run package
```

Para desarrollarla, ejecutá `npm ci` y `npm run build` en esa carpeta, abrila como carpeta de trabajo en VS Code y presioná F5. Más detalles en [el README de la extensión](MesaYa.VSCodeExtension/README.md).

## Configuración y verificaciones

API, Tooling y MCP tienen su propio `appsettings.json` y perfil `http` en `Properties/launchSettings.json`. Los puertos documentados corresponden a esos perfiles. Los README de cada proyecto detallan sus opciones y variables de entorno. Si cambiás puertos al ejecutar manualmente, actualizá también las URL de sus consumidores.

Para compilar todos los proyectos .NET desde la raíz:

```bash
dotnet build MesaYaDemo.sln
```

La API almacena reservas e idempotencia en memoria y usa el token de demo `demo-agente-token`, con permiso para Palermo y Belgrano. Reiniciarla borra ese estado. Los escenarios de errores, reintentos y confirmación están explicados en [API](MesaYa.Api/README.md), [Agent](MesaYa.Agent/README.md), [Tooling](MesaYa.Tooling/README.md) y [MCP](MesaYa.Mcp/README.md).
