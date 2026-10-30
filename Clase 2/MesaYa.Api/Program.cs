// MesaYa.Api — API REST inventada de reservas de restaurantes.
// Es "el sistema real" que la tool reservar_mesa termina llamando.
// No sabe nada de LLMs: valida, aplica permisos y responde HTTP como cualquier API.

using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
var app = builder.Build();

// ---- Datos de juguete ------------------------------------------------------------
var sucursales = new Dictionary<string, string> { ["PAL"] = "Palermo", ["BEL"] = "Belgrano", ["CAB"] = "Caballito" };

// Tokens de servicio: a qué sucursales puede reservar cada consumidor (scope reservas:write).
var tokens = new Dictionary<string, string[]>
{
    ["demo-agente-token"] = ["PAL", "BEL"], // el agente de WhatsApp NO tiene permiso sobre CAB
};

// Turnos que ya están llenos (para provocar SIN_DISPONIBILIDAD).
var turnosLlenos = new HashSet<string> { "PAL@21:00" };
var turnos = new[] { "12:30", "13:30", "20:30", "21:00", "22:15", "23:00" };

var reservas = new ConcurrentDictionary<string, Reserva>(); // reserva_id -> estado actual
var idempotencia = new ConcurrentDictionary<string, string>(); // Idempotency-Key -> reserva_id
var escrituraReservas = new object();
var fallasSimuladas = new ConcurrentDictionary<string, int>();  // para #falla503
var secuencia = 20480;

// Menú ficticio compartido por las sucursales de la demo. Importes en pesos argentinos.
var menu = new[]
{
    new { categoria = "Entradas", platos = new[]
    {
        new { plato_id = "ENT-01", nombre = "Provoleta", descripcion = "Provolone a la plancha con tomate y orégano.", precio = 8500m, vegetariano = true },
        new { plato_id = "ENT-02", nombre = "Empanadas de carne", descripcion = "Dos empanadas de carne cortada a cuchillo.", precio = 6000m, vegetariano = false }
    } },
    new { categoria = "Principales", platos = new[]
    {
        new { plato_id = "PRI-01", nombre = "Milanesa con papas", descripcion = "Milanesa de ternera con papas fritas.", precio = 16000m, vegetariano = false },
        new { plato_id = "PRI-02", nombre = "Ravioles de verdura", descripcion = "Pasta rellena de verdura con salsa de tomate.", precio = 14000m, vegetariano = true }
    } },
    new { categoria = "Postres", platos = new[]
    {
        new { plato_id = "POS-01", nombre = "Flan casero", descripcion = "Flan con dulce de leche.", precio = 5500m, vegetariano = true },
        new { plato_id = "POS-02", nombre = "Helado artesanal", descripcion = "Dos bochas de helado a elección.", precio = 5000m, vegetariano = true }
    } }
};

// ---- Endpoints -----------------------------------------------------------------------

// Comprobación liviana de disponibilidad, sin modificar reservas.
app.MapGet("/health", () => Results.Ok(new { estado = "activa" }));

// GET /v1/menu   o   GET /v1/menu?sucursal_id=PAL (lectura pública)
app.MapGet("/v1/menu", (string? sucursal_id) =>
{
    if (sucursal_id is not null && !sucursales.ContainsKey(sucursal_id))
        return Error(404, "SUCURSAL_INEXISTENTE", $"No existe la sucursal {sucursal_id}.");
    return Results.Ok(new
    {
        sucursal_id,
        sucursal = sucursal_id is not null ? sucursales[sucursal_id] : null,
        moneda = "ARS",
        categorias = menu
    });
});

// GET /v1/disponibilidad?sucursal_id=PAL&fecha=2026-11-06
app.MapGet("/v1/disponibilidad", (string sucursal_id, DateOnly fecha) =>
{
    if (!sucursales.ContainsKey(sucursal_id))
        return Error(404, "SUCURSAL_INEXISTENTE", $"No existe la sucursal {sucursal_id}.");
    var libres = turnos.Where(t => !turnosLlenos.Contains($"{sucursal_id}@{t}")).ToArray();
    return Results.Ok(new { sucursal_id, fecha, horarios_libres = libres });
});

// POST /v1/reservas   (Authorization: Bearer ... · Idempotency-Key: ...)
app.MapPost("/v1/reservas", async (HttpRequest req, ReservaRequest body) =>
{
    // 1. Autenticación y permisos
    var auth = req.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ") || !tokens.TryGetValue(auth[7..], out var permitidas))
        return Error(401, "UNAUTHORIZED", "Falta el token o no es válido.");
    if (!permitidas.Contains(body.SucursalId))
        return Error(403, "FORBIDDEN", $"El token no tiene permiso para reservar en {body.SucursalId}.");

    // 2. Idempotencia: misma clave => misma respuesta, sin crear otra reserva
    var key = req.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key))
        return Error(400, "IDEMPOTENCY_KEY_REQUERIDA", "Crear una reserva necesita Idempotency-Key.");
    if (idempotencia.TryGetValue(key, out var reservaPrevia))
    {
        var previa = reservas[reservaPrevia];
        if (!permitidas.Contains(previa.SucursalId))
            return Error(403, "FORBIDDEN", $"El token no tiene permiso para reservar en {previa.SucursalId}.");
        req.HttpContext.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Json(previa, statusCode: 201);
    }

    // 3. Fallas simuladas para la demo (se activan con la nota)
    var nota = body.Nota ?? "";
    if (nota.Contains("#lento")) await Task.Delay(TimeSpan.FromSeconds(12));              // provoca TIMEOUT
    if (nota.Contains("#falla503") && fallasSimuladas.AddOrUpdate(key, 1, (_, n) => n + 1) == 1)
        return Error(503, "NO_DISPONIBLE", "Servicio momentáneamente no disponible.");    // falla solo el 1er intento

    // 4. Reglas de negocio (la API se defiende sola, aunque el agente ya haya validado)
    if (!sucursales.ContainsKey(body.SucursalId))
        return Error(404, "SUCURSAL_INEXISTENTE", $"No existe la sucursal {body.SucursalId}.");
    if (body.Personas is < 1 or > 12)
        return Error(422, "INVALID_ARGS", "personas debe estar entre 1 y 12.");
    if (body.FechaHora <= DateTimeOffset.Now)
        return Error(422, "FECHA_PASADA", "La fecha ya pasó.");
    var hora = body.FechaHora.ToString("HH:mm");
    if (turnosLlenos.Contains($"{body.SucursalId}@{hora}"))
    {
        var alternativas = turnos.Where(t => !turnosLlenos.Contains($"{body.SucursalId}@{t}"))
                                 .OrderBy(t => Math.Abs((TimeOnly.Parse(t).ToTimeSpan() - TimeOnly.Parse(hora).ToTimeSpan()).TotalMinutes))
                                 .Take(2).OrderBy(t => t);
        return Error(409, "SIN_DISPONIBILIDAD", $"Sin mesa a las {hora}.", new { alternativas });
    }

    // 5. Alta de la reserva
    lock (escrituraReservas)
    {
        // Un reintento puede llegar mientras el primer pedido todavía está en curso.
        if (idempotencia.TryGetValue(key, out reservaPrevia))
        {
            var previa = reservas[reservaPrevia];
            if (!permitidas.Contains(previa.SucursalId))
                return Error(403, "FORBIDDEN", $"El token no tiene permiso para reservar en {previa.SucursalId}.");
            req.HttpContext.Response.Headers["Idempotent-Replayed"] = "true";
            return Results.Json(previa, statusCode: 201);
        }
        var reserva = new Reserva(
            $"RSV-{Interlocked.Increment(ref secuencia)}", "confirmada", sucursales[body.SucursalId],
            body.FechaHora, body.Personas, $"{Random.Shared.Next(1, 30)} · salón",
            body.FechaHora.AddHours(-3), body.SucursalId);
        reservas[reserva.ReservaId] = reserva;
        idempotencia[key] = reserva.ReservaId;
        return Results.Json(reserva, statusCode: 201);
    }
});

// DELETE /v1/reservas/{reserva_id}   (Authorization: Bearer ...)
app.MapDelete("/v1/reservas/{reserva_id}", (HttpRequest req, string reserva_id) =>
{
    var auth = req.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ") || !tokens.TryGetValue(auth[7..], out var permitidas))
        return Error(401, "UNAUTHORIZED", "Falta el token o no es válido.");

    lock (escrituraReservas)
    {
        if (!reservas.TryGetValue(reserva_id, out var reserva))
            return Error(404, "RESERVA_INEXISTENTE", $"No existe la reserva {reserva_id}.");
        if (!permitidas.Contains(reserva.SucursalId))
            return Error(403, "FORBIDDEN", $"El token no tiene permiso para cancelar reservas en {reserva.SucursalId}.");
        if (reserva.Estado == "cancelada")
            req.HttpContext.Response.Headers["Idempotent-Replayed"] = "true";
        else
        {
            reserva = reserva with { Estado = "cancelada", CanceladaEn = DateTimeOffset.UtcNow };
            reservas[reserva_id] = reserva;
        }
        return Results.Ok(reserva);
    }
});

app.Run();

static IResult Error(int status, string codigo, string mensaje, object? extra = null) =>
    Results.Json(new { codigo, mensaje, detalle = extra }, statusCode: status);

record ReservaRequest(string SucursalId, DateTimeOffset FechaHora, int Personas, string Telefono, string? Nota);
