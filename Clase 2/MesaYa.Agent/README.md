# MesaYa.Agent

## Naturaleza y responsabilidad

Biblioteca de clases **.NET 8** que implementa el agente de reservas utilizado por [`MesaYa.Tooling`](../MesaYa.Tooling/README.md). Contiene el ciclo de conversación, el contrato de herramientas, la integración con Ollama y el cliente HTTP de la API.

El modelo propone llamadas a herramientas; el motor valida sus argumentos, solicita confirmación humana antes de reservar, ejecuta la llamada HTTP y devuelve el resultado al modelo. La interfaz decide cómo mostrar los eventos y cómo pedir esa confirmación.

[`MesaYa.Mcp`](../MesaYa.Mcp/README.md) referencia esta biblioteca para reutilizar sus validaciones. Tiene su propio agente que descubre y ejecuta herramientas por MCP. La extensión de VS Code implementa su agente en TypeScript.

## Requisitos y ejecución

Para compilar se necesita SDK .NET 8 o posterior con soporte para `net8.0`. No agrega paquetes NuGet de terceros.

Desde la raíz:

```bash
dotnet build MesaYa.Agent/MesaYa.Agent.csproj
```

Es una biblioteca: no tiene `Program.cs`, puerto, pantalla ni un comando `dotnet run` propio. Para usar el agente, iniciá la API y Tooling en dos terminales:

```bash
dotnet run --project MesaYa.Api --launch-profile http
```

```bash
dotnet run --project MesaYa.Tooling --launch-profile http
```

Abrí `http://localhost:5090`. También podés levantar los tres servicios con `./iniciar-mesaya.sh` desde la raíz.

En ejecución, las herramientas necesitan `MesaYa.Api`. `ModeloOllama` necesita Ollama con un modelo instalado; `ModeloSimulado` sigue un guion sin LLM, pero usa validaciones y llamadas a la API reales.

## Componentes

| Archivo | Responsabilidad |
|---|---|
| `AgenteReservas.cs` | Historial, ciclo de modelo y herramientas, eventos, límites y confirmación humana |
| `Herramientas.cs` | Definiciones y JSON Schema de `buscar_disponibilidad` y `reservar_mesa` |
| `Validador.cs` | Validación sintáctica, semántica y de seguridad |
| `MesaYaClient.cs` | HTTP, token, timeout, reintentos y clave de idempotencia |
| `Modelos.cs` | Contrato `IModelo` y guion `ModeloSimulado` |
| `ModeloOllama.cs` | Adaptación de la conversación y las herramientas a Ollama |
| `ConfiguracionAgente.cs` | Opciones de conexión y nombre del modelo |
| `UsoModelo.cs` | Contadores de tokens y tiempos informados por el modelo |

## Flujo y límites

1. `Enviar` incorpora el mensaje al historial y consulta el modelo con el contrato de herramientas.
2. Si el modelo pide una herramienta, el motor procesa sus argumentos.
3. Para reservar, aplica las tres capas de validación y espera la confirmación que le proporciona el host.
4. Llama a la API y entrega al modelo el resultado o un error descriptivo.
5. Continúa hasta la respuesta final o el límite de seis iteraciones por turno.

Cada conversación admite hasta tres reservas exitosas. La clave de idempotencia se deriva de la conversación y los argumentos. El cliente espera ocho segundos por intento y permite dos reintentos adicionales ante 429, 503 o timeout, con esperas de 500 ms y un segundo.

## Configuración

La biblioteca recibe sus dependencias y callbacks al crear el agente; no carga un `appsettings.json` propio. Tooling configura la API, el token, Ollama y la confirmación desde su aplicación. Consultá [la configuración de Tooling](../MesaYa.Tooling/README.md#configuración-de-conexiones).

[Volver al README principal](../README.md).
