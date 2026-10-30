using System.Text.Json.Nodes;

namespace MesaYa.Agent;

/// <summary>Quien decide qué tool llamar. Puede ser Ollama o un guion fijo para la demo.</summary>
public interface IModelo
{
    Task<JsonObject> Responder(string system, JsonArray tools, JsonArray mensajes);
}

/// <summary>
/// Modelo "de mentira" que sigue un guion: sirve para dar la demo sin API key ni internet,
/// y garantiza que se vean todos los casos (error de esquema, error de negocio, éxito).
/// La validación, la API y los reintentos son reales; solo las decisiones del modelo están guionadas.
/// </summary>
public class ModeloSimulado(string? notaDeFalla = null) : IModelo
{
    int paso;
    static readonly string Viernes = ProximoViernes().ToString("yyyy-MM-dd");

    public static string[] MensajesDelUsuario =>
    [
        "Hola! Quiero una mesa para 4 el viernes a las 21 en Palermo. Mi cel es 11 5555 0123",
        "Dale, 22:15 está bien"
    ];

    public Task<JsonObject> Responder(string system, JsonArray tools, JsonArray mensajes)
    {
        var r = paso++ switch
        {
            // 1) Se equivoca de tipo: manda personas como texto -> falla la validación sintáctica
            0 => ToolUse("Voy a reservar tu mesa.", "reservar_mesa", new JsonObject
            {
                ["sucursal_id"] = "PAL", ["fecha_hora"] = $"{Viernes}T21:00:00-03:00",
                ["personas"] = "cuatro", ["telefono"] = "+5491155550123"
            }),
            // 2) Corrige el tipo -> pasa la validación, pero la API dice que no hay lugar
            1 => ToolUse("", "reservar_mesa", new JsonObject
            {
                ["sucursal_id"] = "PAL", ["fecha_hora"] = $"{Viernes}T21:00:00-03:00",
                ["personas"] = 4, ["telefono"] = "+5491155550123"
            }),
            // 3) Lee las alternativas del error y se las ofrece al usuario
            2 => Texto($"No hay mesa a las 21:00 en Palermo para el viernes. Tengo lugar a las " +
                       $"{Alternativas(mensajes)}. ¿Cuál te sirve?"),
            // 4) Reserva en el horario elegido
            //    (con --falla503 o --lento agrega una nota que hace fallar a la API de la demo)
            3 => ToolUse("Perfecto, reservo para las 22:15.", "reservar_mesa", ConNota(new JsonObject
            {
                ["sucursal_id"] = "PAL", ["fecha_hora"] = $"{Viernes}T22:15:00-03:00",
                ["personas"] = 4, ["telefono"] = "+5491155550123"
            })),
            // 5) Confirma con los datos que devolvió la API
            _ => Texto(Confirmacion(mensajes))
        };
        return Task.FromResult(r);
    }

    JsonObject ConNota(JsonObject o)
    {
        if (notaDeFalla is not null) o["nota"] = notaDeFalla;
        return o;
    }

    static JsonObject ToolUse(string texto, string nombre, JsonObject input)
    {
        var content = new JsonArray();
        if (texto != "") content.Add(new JsonObject { ["type"] = "text", ["text"] = texto });
        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = $"toolu_{Guid.NewGuid():N}"[..14], ["name"] = nombre, ["input"] = input });
        return new JsonObject { ["stop_reason"] = "tool_use", ["content"] = content };
    }

    static JsonObject Texto(string t) => new()
    {
        ["stop_reason"] = "end_turn",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = t })
    };

    static JsonNode? UltimoResultado(JsonArray mensajes)
    {
        var bloque = mensajes.Last()!["content"]!.AsArray().Last()!;
        return JsonNode.Parse(bloque["content"]!.GetValue<string>());
    }

    static string Alternativas(JsonArray mensajes)
    {
        // mensajes: ... user(tool_result del 409), asistente no agregado todavía
        var alt = UltimoResultado(mensajes)?["detalle"]?["alternativas"]?.AsArray().Select(x => x!.ToString()).ToArray();
        return alt is { Length: > 0 } ? string.Join(" o ", alt) : "otros horarios";
    }

    static string Confirmacion(JsonArray mensajes)
    {
        var r = UltimoResultado(mensajes);
        return r?["reserva_id"] is null
            ? "No pude completar la reserva. ¿Querés que lo intente de nuevo?"
            : $"¡Listo! Reserva {r["reserva_id"]} confirmada: {r["personas"]} personas en {r["sucursal"]}, " +
              $"mesa {r["mesa"]}. Podés cancelar sin cargo hasta {DateTimeOffset.Parse(r["cancelar_hasta"]!.ToString()):dd/MM HH:mm}.";
    }

    static DateTime ProximoViernes()
    {
        var d = DateTime.Today.AddDays(1);
        while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
        return d;
    }
}
