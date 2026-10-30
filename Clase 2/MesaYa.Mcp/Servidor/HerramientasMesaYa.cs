using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Validador = MesaYa.Agent.Validador;

namespace MesaYa.Mcp.Servidor;

/// <summary>
/// Las tools que expone el servidor MCP. Cualquier cliente MCP (este agente, Claude, el MCP Inspector…)
/// las descubre con tools/list y las ejecuta con tools/call. Cada una termina llamando a MesaYa.Api.
/// </summary>
[McpServerToolType]
public sealed class HerramientasMesaYa
{
    public static readonly IReadOnlyDictionary<string, string> Sucursales =
        new Dictionary<string, string> { ["PAL"] = "Palermo", ["BEL"] = "Belgrano", ["CAB"] = "Caballito" };

    static readonly ConcurrentDictionary<string, int> reservasPorSesion = new();
    static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-AR");

    [McpServerTool(Name = "listar_sucursales", Title = "Listar sucursales", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Devuelve las sucursales de MesaYa con su código sucursal_id (PAL, BEL, CAB). " +
                 "Usala si el usuario nombra un barrio y necesitás el código.")]
    public static CallToolResult ListarSucursales() =>
        Ok(new JsonObject
        {
            ["sucursales"] = new JsonArray(Sucursales.Select(s => (JsonNode)new JsonObject
            {
                ["sucursal_id"] = s.Key, ["nombre"] = s.Value
            }).ToArray()),
            ["horarios"] = "Almuerzo 12:00–15:30 · Cena 20:00–23:00 · zona horaria -03:00"
        });

    [McpServerTool(Name = "consultar_menu", Title = "Consultar menú", ReadOnly = true, Idempotent = true)]
    [Description("Devuelve el menú (categorías, platos, precios en ARS y si son vegetarianos). " +
                 "Opcionalmente filtrado por sucursal.")]
    public static async Task<CallToolResult> ConsultarMenu(
        McpServer servidor, ApiMesaYa api, IProgress<ProgressNotificationValue> progreso,
        [Description("Código de sucursal: PAL, BEL o CAB. Omitilo para el menú general.")] string? sucursal_id = null,
        CancellationToken ct = default)
    {
        var ctx = new ContextoTool(servidor, progreso);
        ctx.Progreso("Consultando el menú en MesaYa.Api…");
        var ruta = string.IsNullOrWhiteSpace(sucursal_id) ? "/v1/menu" : $"/v1/menu?sucursal_id={Uri.EscapeDataString(sucursal_id)}";
        var r = await api.Get(ruta, ctx, ct);
        return r.Ok ? Ok(r.Cuerpo) : ErrorDeApi(r);
    }

    [McpServerTool(Name = "buscar_disponibilidad", Title = "Buscar disponibilidad", ReadOnly = true, Idempotent = true)]
    [Description("Devuelve los horarios libres de una sucursal para una fecha. No reserva nada. " +
                 "Usala antes de reservar si el usuario no tiene un horario fijo.")]
    public static async Task<CallToolResult> BuscarDisponibilidad(
        McpServer servidor, ApiMesaYa api, IProgress<ProgressNotificationValue> progreso,
        [Description("Código de sucursal: PAL = Palermo, BEL = Belgrano, CAB = Caballito")] string sucursal_id,
        [Description("Fecha en formato AAAA-MM-DD")] string fecha,
        CancellationToken ct = default)
    {
        var ctx = new ContextoTool(servidor, progreso);
        if (!Sucursales.ContainsKey(sucursal_id))
            return Error("INVALID_ARGS", "sucursal_id debe ser PAL, BEL o CAB.");
        if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", out _))
            return Error("INVALID_ARGS", "fecha debe tener el formato AAAA-MM-DD, por ejemplo 2026-11-06.");
        ctx.Progreso($"Consultando horarios de {Sucursales[sucursal_id]} para el {fecha}…");
        var r = await api.Get($"/v1/disponibilidad?sucursal_id={sucursal_id}&fecha={fecha}", ctx, ct);
        return r.Ok ? Ok(r.Cuerpo) : ErrorDeApi(r);
    }

    [McpServerTool(Name = "reservar_mesa", Title = "Reservar mesa", Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Crea una reserva real en MesaYa. El servidor le pide confirmación al usuario (elicitation) antes de reservar, " +
                 "así que no hace falta que la pidas vos. No sirve para modificar ni cancelar. " +
                 "Si devuelve SIN_DISPONIBILIDAD, ofrecé las alternativas que vienen en detalle.alternativas.")]
    public static async Task<CallToolResult> ReservarMesa(
        McpServer servidor, ApiMesaYa api, Escenarios escenarios, IProgress<ProgressNotificationValue> progreso,
        [Description("Código de sucursal: PAL = Palermo, BEL = Belgrano, CAB = Caballito")] string sucursal_id,
        [Description("Fecha y hora ISO 8601 con zona, ej. 2026-11-06T21:00:00-03:00")] string fecha_hora,
        [Description("Cantidad de personas, de 1 a 12")] int personas,
        [Description("Celular argentino con formato +549 y 10 dígitos, ej. +5491155550123")] string telefono,
        [Description("Nota opcional para el restaurante (máx. 200 caracteres)")] string? nota = null,
        CancellationToken ct = default)
    {
        var ctx = new ContextoTool(servidor, progreso);
        var sesion = servidor.SessionId ?? "sin-sesion";
        var input = new JsonObject
        {
            ["sucursal_id"] = sucursal_id, ["fecha_hora"] = fecha_hora, ["personas"] = personas, ["telefono"] = telefono
        };
        if (!string.IsNullOrWhiteSpace(nota)) input["nota"] = nota;

        // 1. Las tres capas de validación de MesaYa.Agent: el servidor no confía en lo que manda el modelo.
        ctx.Progreso("Validando argumentos (sintáctica, semántica y seguridad)…");
        if (Validador.Sintactica(input) is { } e1) return Rechazo("sintáctica", e1, ctx);
        if (Validador.Semantica(input) is { } e2) return Rechazo("semántica", e2, ctx);
        if (Validador.Seguridad(input, reservasPorSesion.GetValueOrDefault(sesion)) is { } e3) return Rechazo("seguridad", e3, ctx);
        await ctx.Log("Validación OK: sintáctica · semántica · seguridad");

        // 2. Confirmación humana vía MCP elicitation: el SERVIDOR le pregunta al usuario a través del cliente.
        var fh = DateTimeOffset.Parse(fecha_hora, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(-3));
        var resumen = $"{personas} personas en {Sucursales[sucursal_id]} el {fh.ToString("dddd dd/MM 'a las' HH:mm", Es)} (tel. {telefono})";
        if (servidor.ClientCapabilities?.Elicitation is not null)
        {
            ctx.Progreso("Esperando la confirmación del usuario (elicitation)…");
            var respuesta = await servidor.ElicitAsync(new ElicitRequestParams
            {
                Message = $"¿Confirmás la reserva para {resumen}?",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["nota"] = new ElicitRequestParams.StringSchema
                        {
                            Title = "Nota para el restaurante",
                            Description = "Opcional: cumpleaños, silla para bebé, alergias…",
                            MaxLength = 200
                        }
                    }
                }
            }, ct);
            if (!respuesta.IsAccepted)
                return Error("USUARIO_NO_CONFIRMO",
                    respuesta.Action == "decline"
                        ? "El usuario rechazó la reserva. Preguntale qué quiere cambiar."
                        : "El usuario cerró la confirmación sin responder. No se reservó nada.");
            if (respuesta.Content?.TryGetValue("nota", out var n) == true && n.ValueKind == JsonValueKind.String
                && n.GetString() is { Length: > 0 } notaUsuario)
            {
                input["nota"] = notaUsuario;
                if (Validador.Seguridad(input, reservasPorSesion.GetValueOrDefault(sesion)) is { } e4) return Rechazo("seguridad", e4, ctx);
            }
        }
        else
        {
            await ctx.Log("El cliente no soporta elicitation: se reserva sin confirmación del servidor.", LoggingLevel.Warning);
        }

        // 3. Llamada real con Idempotency-Key derivada de la sesión MCP + argumentos.
        var cuerpo = (JsonObject)input.DeepClone();
        if (escenarios.Forzar503)
            cuerpo["nota"] = ((cuerpo["nota"]?.ToString() ?? "") + " #falla503").Trim();
        var clave = ClaveIdempotencia(sesion, input);
        ctx.Progreso("Enviando la reserva a MesaYa.Api…");
        var r = await api.Post("/v1/reservas", cuerpo, clave, ctx, ct);
        if (!r.Ok) return ErrorDeApi(r);
        reservasPorSesion.AddOrUpdate(sesion, 1, (_, c) => c + 1);
        await ctx.Log($"Reserva {r.Cuerpo["reserva_id"]} confirmada.");
        return Ok(r.Cuerpo);
    }

    [McpServerTool(Name = "cancelar_reserva", Title = "Cancelar reserva", Destructive = true, Idempotent = true)]
    [Description("Cancela una reserva existente por su reserva_id (ej. RSV-20481). El servidor pide confirmación al usuario.")]
    public static async Task<CallToolResult> CancelarReserva(
        McpServer servidor, ApiMesaYa api, IProgress<ProgressNotificationValue> progreso,
        [Description("Identificador devuelto al reservar, ej. RSV-20481")] string reserva_id,
        CancellationToken ct = default)
    {
        var ctx = new ContextoTool(servidor, progreso);
        if (servidor.ClientCapabilities?.Elicitation is not null)
        {
            ctx.Progreso("Esperando la confirmación del usuario (elicitation)…");
            var respuesta = await servidor.ElicitAsync(new ElicitRequestParams
            {
                Message = $"¿Confirmás que querés CANCELAR la reserva {reserva_id}?",
                RequestedSchema = new ElicitRequestParams.RequestSchema()
            }, ct);
            if (!respuesta.IsAccepted)
                return Error("USUARIO_NO_CONFIRMO", "El usuario no confirmó la cancelación. La reserva sigue vigente.");
        }
        var r = await api.Delete($"/v1/reservas/{Uri.EscapeDataString(reserva_id)}", ctx, ct);
        return r.Ok ? Ok(r.Cuerpo) : ErrorDeApi(r);
    }

    // ---- Helpers de resultado ---------------------------------------------------------------

    static CallToolResult Ok(JsonNode cuerpo) => new()
    {
        Content = [new TextContentBlock { Text = cuerpo.ToJsonString(Opciones) }]
    };

    static CallToolResult Error(string codigo, string mensaje, JsonNode? detalle = null, bool reintentable = false) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = new JsonObject
        {
            ["codigo"] = codigo, ["mensaje"] = mensaje, ["reintentable"] = reintentable, ["detalle"] = detalle?.DeepClone()
        }.ToJsonString(Opciones) }]
    };

    static CallToolResult ErrorDeApi(ResultadoApi r) =>
        Error(r.Cuerpo["codigo"]?.ToString() ?? $"HTTP_{r.Status}", r.Cuerpo["mensaje"]?.ToString() ?? "Error de la API.",
              r.Cuerpo["detalle"], r.Cuerpo["reintentable"]?.GetValue<bool>() ?? false);

    static CallToolResult Rechazo(string capa, MesaYa.Agent.ErrorTool e, ContextoTool ctx)
    {
        _ = ctx.Log($"Validación {capa} rechazada: {e.Mensaje}. No se llama a la API.", LoggingLevel.Warning);
        return Error(e.Codigo, $"[validación {capa}] {e.Mensaje}", reintentable: e.Reintentable);
    }

    static string ClaveIdempotencia(string sesion, JsonObject a)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sesion + a.ToJsonString()));
        return new Guid(hash[..16]).ToString();
    }

    static readonly JsonSerializerOptions Opciones = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
