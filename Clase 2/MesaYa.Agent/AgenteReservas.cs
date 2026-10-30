using System.Text.Json;
using System.Text.Json.Nodes;

namespace MesaYa.Agent;

/// <summary>Evento de conversación o diagnóstico que cada interfaz puede mostrar.</summary>
/// <param name="Tipo">Usuario, Modelo, Tool, Valida, Host, Http o Resultado.</param>
/// <param name="Texto">Texto legible o JSON indentado.</param>
/// <param name="Error">Indica un resultado fallido.</param>
public sealed record EventoAgente(string Tipo, string Texto, bool Error = false);

/// <summary>Motor compartido: modelo, tools, validaciones y confirmación humana.</summary>
public sealed class AgenteReservas
{
    readonly IModelo modelo;
    readonly MesaYaClient mesaYa;
    readonly Action<EventoAgente> registrar;
    readonly Func<JsonObject, Task<bool>> confirmar;
    readonly JsonArray mensajes = new();
    readonly JsonArray tools = Herramientas.Definiciones();
    readonly string conversacionId = Guid.NewGuid().ToString();
    int reservasHechas;
    bool ocupado;

    /// <summary>Crea una conversación independiente con sus propios límites e idempotencia.</summary>
    /// <param name="modelo">Modelo real o simulado.</param>
    /// <param name="mesaYa">Cliente de la API de reservas.</param>
    /// <param name="registrar">Receptor de mensajes y diagnósticos.</param>
    /// <param name="confirmar">Solicita confirmación antes de crear una reserva.</param>
    public AgenteReservas(IModelo modelo, MesaYaClient mesaYa, Action<EventoAgente> registrar,
        Func<JsonObject, Task<bool>> confirmar)
    {
        this.modelo = modelo ?? throw new ArgumentNullException(nameof(modelo));
        this.mesaYa = mesaYa ?? throw new ArgumentNullException(nameof(mesaYa));
        this.registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        this.confirmar = confirmar ?? throw new ArgumentNullException(nameof(confirmar));
    }

    /// <summary>Procesa un turno completo, incluidas las tools y sus resultados.</summary>
    /// <param name="entrada">Mensaje del usuario.</param>
    /// <returns>Una tarea que termina al finalizar el turno.</returns>
    public async Task Enviar(string entrada)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entrada);
        if (ocupado) throw new InvalidOperationException("Ya hay un turno en curso.");
        ocupado = true;
        try
        {
            Emitir("Usuario", entrada);
            mensajes.Add(new JsonObject { ["role"] = "user", ["content"] = entrada });
            var system = $"""
                Sos el asistente de reservas de MesaYa por WhatsApp. Hoy es {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)):dddd dd/MM/yyyy} y la zona horaria es -03:00.
                Sucursales: PAL = Palermo, BEL = Belgrano, CAB = Caballito.
                Antes de reservar necesitás sucursal, fecha, hora, cantidad de personas y celular.
                Si una tool devuelve un error, leé el mensaje y corregí o explicale al usuario qué pasó. Respondé breve.
                """;

            for (var iteracion = 1; iteracion <= 6; iteracion++)
            {
                var respuesta = await modelo.Responder(system, tools, mensajes);
                var content = respuesta["content"]!.AsArray();
                mensajes.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
                foreach (var bloque in content.Where(b => b!["type"]!.ToString() == "text"))
                    Emitir("Modelo", bloque!["text"]!.ToString());
                if (respuesta["stop_reason"]?.ToString() != "tool_use") return;

                var resultados = new JsonArray();
                foreach (var uso in content.Where(b => b!["type"]!.ToString() == "tool_use"))
                {
                    var nombre = uso!["name"]!.ToString();
                    var input = uso["input"]!.AsObject();
                    Emitir("Tool", $"{nombre}\n{input.ToJsonString(Json.Lindo)}");
                    var (cuerpo, error) = await EjecutarTool(nombre, input);
                    Emitir("Resultado", cuerpo.ToJsonString(Json.Lindo), error);
                    resultados.Add(new JsonObject
                    {
                        ["type"] = "tool_result", ["tool_use_id"] = uso["id"]!.ToString(),
                        ["content"] = cuerpo.ToJsonString(Json.Compacto), ["is_error"] = error
                    });
                }
                mensajes.Add(new JsonObject { ["role"] = "user", ["content"] = resultados });
            }
            Emitir("Host", "Se alcanzó el límite de 6 iteraciones. Iniciá una nueva conversación para continuar.", true);
        }
        finally { ocupado = false; }
    }

    async Task<(JsonNode cuerpo, bool error)> EjecutarTool(string nombre, JsonObject input)
    {
        switch (nombre)
        {
            case "buscar_disponibilidad":
            {
                var r = await mesaYa.BuscarDisponibilidad(input);
                return (r.cuerpo, !r.ok);
            }
            case "reservar_mesa":
            {
                if (Validador.Sintactica(input) is { } e1) return Rechazar("Sintáctica", e1);
                Emitir("Valida", "Sintáctica · el JSON cumple el esquema");
                if (Validador.Semantica(input) is { } e2) return Rechazar("Semántica", e2);
                Emitir("Valida", "Semántica · fecha futura y dentro del horario");
                if (Validador.Seguridad(input, reservasHechas) is { } e3) return Rechazar("Seguridad", e3);
                Emitir("Valida", "Seguridad · nota limpia y dentro del límite de uso");
                if (!await confirmar(input.DeepClone().AsObject()))
                    return (Error("USUARIO_NO_CONFIRMO", "El usuario no confirmó. Preguntale qué quiere cambiar."), true);
                var r = await mesaYa.Reservar(input, conversacionId);
                if (r.ok) reservasHechas++;
                return (r.cuerpo, !r.ok);
            }
            default:
                return (Error("TOOL_INEXISTENTE", $"No existe la tool {nombre}."), true);
        }
    }

    (JsonNode, bool) Rechazar(string capa, ErrorTool e)
    {
        Emitir("Valida", $"{capa} · {e.Mensaje}", true);
        Emitir("Host", "No se llama a la API: el error vuelve al modelo para que corrija.");
        return (JsonSerializer.SerializeToNode(new { codigo = e.Codigo, mensaje = e.Mensaje, reintentable = e.Reintentable })!, true);
    }

    static JsonNode Error(string codigo, string mensaje) =>
        JsonSerializer.SerializeToNode(new { codigo, mensaje, reintentable = false })!;

    void Emitir(string tipo, string texto, bool error = false) => registrar(new EventoAgente(tipo, texto, error));
}
