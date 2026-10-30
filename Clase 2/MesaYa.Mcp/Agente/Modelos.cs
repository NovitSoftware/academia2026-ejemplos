using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesaYa.Agent;

namespace MesaYa.Mcp.Agente;

/// <summary>Modelo instalado en Ollama y lo que sabe hacer.</summary>
public sealed record ModeloLocal(string Nombre, string? Tamano, IReadOnlyList<string> Capacidades)
{
    public bool SoportaTools => Capacidades.Contains("tools");
    public bool SoportaThinking => Capacidades.Contains("thinking");
}

/// <summary>Consulta los modelos instalados con GET /api/tags.</summary>
public sealed class CatalogoOllama(IHttpClientFactory fabrica)
{
    public async Task<IReadOnlyList<ModeloLocal>> Listar(CancellationToken ct = default)
    {
        var http = fabrica.CreateClient("ollama");
        var raiz = await http.GetFromJsonAsync<JsonObject>("/api/tags", ct)
                   ?? throw new JsonException("Ollama devolvió un catálogo vacío.");
        return (raiz["models"] as JsonArray ?? [])
            .Select(m => new ModeloLocal(
                m!["name"]!.GetValue<string>(),
                m["details"]?["parameter_size"]?.GetValue<string>(),
                (m["capabilities"] as JsonArray)?.Select(c => c!.GetValue<string>()).ToArray() ?? []))
            .OrderByDescending(m => m.SoportaTools).ThenBy(m => m.Nombre, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>Una llamada a tool decidida por el modelo.</summary>
public sealed record LlamadaTool(string Id, string Nombre, JsonObject Argumentos);

/// <summary>Lo que respondió el modelo en una iteración.</summary>
/// <param name="Texto">Texto para el usuario (puede venir vacío si solo pidió tools).</param>
/// <param name="Llamadas">Tools pedidas en esta iteración (pueden ser varias, en paralelo).</param>
/// <param name="MensajeAsistente">Mensaje tal cual se agrega al historial de Ollama.</param>
/// <param name="Metricas">Tokens y tiempos informados por el motor.</param>
/// <param name="Uso">Contadores originales de Ollama, o null para el guion o si faltan métricas.</param>
public sealed record RespuestaModelo(string Texto, IReadOnlyList<LlamadaTool> Llamadas, JsonObject MensajeAsistente, string? Metricas, UsoModelo? Uso = null);

/// <summary>Quien decide qué tools llamar: un LLM local o un guion fijo.</summary>
public interface IModeloChat
{
    string Nombre { get; }
    /// <summary>JSON exacto que se envía (para mostrarlo en la traza).</summary>
    JsonObject ArmarPedido(JsonArray mensajes, JsonArray tools);
    Task<RespuestaModelo> Responder(JsonObject pedido, CancellationToken ct);
}

/// <summary>Adaptador del endpoint /api/chat de Ollama con tool calling nativo.</summary>
public sealed class ChatOllama(HttpClient http, ModeloLocal modelo) : IModeloChat
{
    public string Nombre => modelo.Nombre;

    public JsonObject ArmarPedido(JsonArray mensajes, JsonArray tools)
    {
        var pedido = new JsonObject
        {
            ["model"] = modelo.Nombre,
            ["stream"] = false,
            ["keep_alive"] = "15m",
            ["options"] = new JsonObject { ["temperature"] = 0.2 },
            ["messages"] = mensajes.DeepClone(),
            ["tools"] = tools.DeepClone()
        };
        // Ollama rechaza "think" en modelos sin razonamiento; en los que lo tienen lo apagamos por velocidad.
        if (modelo.SoportaThinking) pedido["think"] = false;
        return pedido;
    }

    public async Task<RespuestaModelo> Responder(JsonObject pedido, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("/api/chat", pedido, ct);
        var texto = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama respondió {(int)resp.StatusCode}: {texto}");
        var raiz = JsonNode.Parse(texto)!.AsObject();
        var mensaje = raiz["message"]?.AsObject() ?? throw new InvalidOperationException("Ollama devolvió una respuesta sin message.");

        var llamadas = new List<LlamadaTool>();
        if (mensaje["tool_calls"] is JsonArray calls)
            foreach (var call in calls)
            {
                var funcion = call!["function"]!;
                var args = funcion["arguments"] switch
                {
                    JsonObject o => (JsonObject)o.DeepClone(),
                    JsonValue v when v.TryGetValue(out string? s) && JsonNode.Parse(s) is JsonObject o2 => o2,
                    _ => new JsonObject()
                };
                llamadas.Add(new LlamadaTool(call["id"]?.ToString() ?? $"call_{Guid.NewGuid():N}"[..13], funcion["name"]!.ToString(), args));
            }

        var uso = UsoModelo.DesdeRespuesta(raiz);
        var metricas = uso is not null
            ? $"{uso.TokensEntrada} tokens entrada · {uso.TokensSalida} tokens salida" +
              (uso.DuracionTotalNs is { } total ? $" · {total / 1_000_000.0:0} ms en Ollama" : "")
            : null;
        var asistente = new JsonObject { ["role"] = "assistant", ["content"] = mensaje["content"]?.ToString() ?? "" };
        if (mensaje["tool_calls"] is JsonArray tc) asistente["tool_calls"] = tc.DeepClone();
        return new RespuestaModelo(mensaje["content"]?.ToString()?.Trim() ?? "", llamadas, asistente, metricas, uso);
    }
}

/// <summary>
/// "Modelo" con guion fijo para dar la demo sin depender de Ollama. Las decisiones están guionadas,
/// pero el protocolo MCP, el servidor, las validaciones y la API son reales.
/// </summary>
public sealed class ModeloGuion : IModeloChat
{
    public const string NombreGuion = "Guion de demo (sin LLM)";
    public string Nombre => NombreGuion;

    public static readonly string[] MensajesSugeridos =
    [
        "Hola! Quiero una mesa para 4 el viernes a las 21 en Palermo. Mi cel es 11 5555 0123",
        "Dale, 22:15 está bien"
    ];

    static string Viernes()
    {
        var d = DateTime.Today.AddDays(1);
        while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
        return d.ToString("yyyy-MM-dd");
    }

    public JsonObject ArmarPedido(JsonArray mensajes, JsonArray tools) => new()
    {
        ["model"] = NombreGuion, ["messages"] = mensajes.DeepClone(), ["tools"] = tools.DeepClone()
    };

    public async Task<RespuestaModelo> Responder(JsonObject pedido, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(400, 900), ct); // que se note el "pensar" en el gráfico
        var mensajes = pedido["messages"]!.AsArray();
        var paso = mensajes.Count(m => m!["role"]?.ToString() == "assistant");
        var v = Viernes();
        return paso switch
        {
            0 => Tools("Busco la sucursal, la disponibilidad y el menú en paralelo.",
                ("listar_sucursales", new JsonObject()),
                ("buscar_disponibilidad", new JsonObject { ["sucursal_id"] = "PAL", ["fecha"] = v }),
                ("consultar_menu", new JsonObject { ["sucursal_id"] = "PAL" })),
            1 => Tools("", ("reservar_mesa", new JsonObject
            {
                ["sucursal_id"] = "PAL", ["fecha_hora"] = $"{v}T21:00:00-03:00", ["personas"] = 4, ["telefono"] = "+5491155550123"
            })),
            2 => Texto($"No hay mesa a las 21:00 en Palermo el viernes. Tengo lugar a las {Alternativas(mensajes)}. ¿Cuál te sirve?"),
            3 => Tools("Perfecto, reservo para las 22:15.", ("reservar_mesa", new JsonObject
            {
                ["sucursal_id"] = "PAL", ["fecha_hora"] = $"{v}T22:15:00-03:00", ["personas"] = 4, ["telefono"] = "+5491155550123"
            })),
            _ => Texto(Confirmacion(mensajes))
        };
    }

    static RespuestaModelo Tools(string texto, params (string nombre, JsonObject args)[] llamadas)
    {
        var lista = llamadas.Select(l => new LlamadaTool($"call_{Guid.NewGuid():N}"[..13], l.nombre, l.args)).ToArray();
        var asistente = new JsonObject
        {
            ["role"] = "assistant", ["content"] = texto,
            ["tool_calls"] = new JsonArray(lista.Select(l => (JsonNode)new JsonObject
            {
                ["function"] = new JsonObject { ["name"] = l.Nombre, ["arguments"] = l.Argumentos.DeepClone() }
            }).ToArray())
        };
        return new RespuestaModelo(texto, lista, asistente, "guion");
    }

    static RespuestaModelo Texto(string t) =>
        new(t, [], new JsonObject { ["role"] = "assistant", ["content"] = t }, "guion");

    static JsonNode? UltimoResultado(JsonArray mensajes)
    {
        var ultimo = mensajes.LastOrDefault(m => m!["role"]?.ToString() == "tool");
        try { return ultimo is null ? null : JsonNode.Parse(ultimo["content"]!.ToString()); }
        catch (JsonException) { return null; }
    }

    static string Alternativas(JsonArray mensajes)
    {
        var alt = UltimoResultado(mensajes)?["detalle"]?["alternativas"]?.AsArray().Select(x => x!.ToString()).ToArray();
        return alt is { Length: > 0 } ? string.Join(" o ", alt) : "otros horarios";
    }

    static string Confirmacion(JsonArray mensajes)
    {
        var r = UltimoResultado(mensajes);
        return r?["reserva_id"] is null
            ? $"No pude completar la reserva ({r?["mensaje"] ?? "sin detalle"}). ¿Querés que lo intente de nuevo?"
            : $"¡Listo! Reserva {r["reserva_id"]} confirmada: {r["personas"]} personas en {r["sucursal"]}, mesa {r["mesa"]}.";
    }
}
