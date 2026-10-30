# MesaYa.Api

## Naturaleza y responsabilidad

API REST de demostración construida con **ASP.NET Core Minimal APIs sobre .NET 8**. Representa el sistema de reservas de un restaurante: ofrece disponibilidad y menú, crea reservas y permite cancelarlas.

Es el servicio de negocio que consumen Tooling y el servidor MCP. Usa datos ficticios y almacenamiento en memoria; no necesita base de datos, Ollama ni una interfaz web para funcionar. Al reiniciar el proceso se pierden reservas, claves de idempotencia y estado de fallas simuladas.

## Requisitos y ejecución

Necesitás SDK .NET 8 o posterior para compilar y runtime ASP.NET Core 8 para ejecutar. El SDK .NET 8 incluye ese runtime. No agrega paquetes NuGet de terceros.

Desde la raíz de la solución:

```bash
dotnet run --project MesaYa.Api --launch-profile http
```

Escucha en `http://localhost:5080`, en entorno Development. El perfil está en `Properties/launchSettings.json`; `appsettings.json` configura logs y hosts permitidos.

Comprobá que esté disponible:

```bash
curl http://localhost:5080/health
```

Responde `{"estado":"activa"}`. Para iniciar también Tooling y MCP, usá `./iniciar-mesaya.sh` desde la raíz.

## Endpoints

| Método y ruta | Función | Autenticación |
|---|---|---|
| `GET /health` | Comprueba que la API responde | Pública |
| `GET /v1/disponibilidad?sucursal_id=PAL&fecha=AAAA-MM-DD` | Lista horarios disponibles | Pública |
| `GET /v1/menu` | Devuelve el menú ficticio y precios en ARS | Pública |
| `GET /v1/menu?sucursal_id=PAL` | Incluye los datos de la sucursal | Pública |
| `POST /v1/reservas` | Crea una reserva | Bearer token e `Idempotency-Key` |
| `DELETE /v1/reservas/{reserva_id}` | Cancela una reserva | Bearer token |

El archivo [`api.http`](api.http) contiene solicitudes listas para probar con un cliente HTTP compatible. Para fechas de reservas, usá una fecha futura.

## Datos, permisos y errores

Las sucursales son `PAL` (Palermo), `BEL` (Belgrano) y `CAB` (Caballito). El token de demostración `demo-agente-token`, definido en `Program.cs`, permite escribir reservas de Palermo y Belgrano; Caballito devuelve 403. Las lecturas de menú y disponibilidad son públicas.

Para crear una reserva se envían `sucursal_id`, `fecha_hora`, `personas`, `telefono` y, opcionalmente, `nota`. La API valida entre una y doce personas y una fecha futura. Palermo a las 21:00 está lleno por definición para mostrar el error `409 SIN_DISPONIBILIDAD` con alternativas.

Los errores usan JSON con `codigo`, `mensaje` y `detalle`. La API vuelve a validar sus reglas aunque el consumidor ya haya validado los argumentos.

## Idempotencia y fallas simuladas

Repetir un POST con la misma `Idempotency-Key` devuelve la reserva asociada sin crear otra y agrega `Idempotent-Replayed: true`. La cancelación puede repetirse: conserva el estado y la fecha de cancelación, e indica la repetición con el mismo encabezado. Un POST repetido después de cancelar devuelve la reserva en su estado actual.

Las notas especiales permiten observar cómo reaccionan los clientes:

| Nota | Comportamiento |
|---|---|
| `#falla503` | El primer intento para esa clave devuelve 503; el reintento puede continuar |
| `#lento` | Demora doce segundos para provocar el timeout del cliente de Tooling |

Estas fallas se activan después de autenticar y comprobar la idempotencia. La API no hace los reintentos: los implementan sus consumidores.

## Organización del código

- `Program.cs`: endpoints, datos ficticios, permisos, idempotencia y fallas simuladas.
- `Reserva.cs`: representación de una reserva y su estado.
- `api.http`: ejemplos de consultas, reservas, errores y cancelación.
- `Properties/launchSettings.json`: puerto del perfil local.

[Volver al README principal](../README.md).
