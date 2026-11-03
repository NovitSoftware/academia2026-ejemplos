using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MesaYa.Contexto.Contexto;
using MesaYa.Contexto.Ollama;

namespace MesaYa.Contexto.Agente;

/// <summary>Qué pasó en una compactación. Se muestra en la línea de tiempo.</summary>
public sealed record EventoCompactacion(
    int TokensAntes,
    int TokensDespues,
    int MensajesResumidos,
    int LlamadasResumen,
    string Resumen,
    IReadOnlyList<string> IdsRescatados,
    IReadOnlyList<string> IdsPerdidos,
    Resumidor Resumidor,
    TimeSpan Duracion);

/// <summary>
/// Resumen + buffer (slide 11): los turnos viejos pasan a un resumen y los últimos quedan literales.
/// El resumen se hace por tramos para que cada pedido entre en la misma ventana (sin recargar el modelo).
/// El resumidor fiel verifica después que no se haya perdido ningún ID: un resumen que pierde un ID envenena lo que sigue.
/// </summary>
public sealed partial class Compactador(ClienteOllama ollama)
{
    [GeneratedRegex(@"RSV-\d{5}|\+549\d{10}")]
    private static partial Regex Identificadores();

    public async Task<EventoCompactacion?> Compactar(
        EstadoConversacion conv, OpcionesContexto o, ConstructorContexto constructor, int tokensAntes,
        Func<OpcionesContexto, int> medir, CancellationToken ct)
    {
        var limite = conv.TurnoActual - o.TurnosLiterales;
        var viejos = conv.Mensajes.Where(m => m.Turno <= limite).ToList();
        if (viejos.Count == 0) return null;

        var reloj = Stopwatch.StartNew();
        var lineas = viejos.Select(Linea).ToList();

        // Tramos que entran cómodos en la ventana junto con el resumen previo y las instrucciones.
        var maxTokensTramo = (int)(o.Ventana * 0.45);
        var tramos = new List<List<string>> { new() };
        var tokensTramo = 0;
        foreach (var linea in lineas)
        {
            var t = constructor.Estimar(linea);
            if (tokensTramo + t > maxTokensTramo && tramos[^1].Count > 0) { tramos.Add([]); tokensTramo = 0; }
            tramos[^1].Add(linea);
            tokensTramo += t;
        }

        var resumen = conv.Resumen;
        foreach (var tramo in tramos)
            resumen = await Resumir(resumen, string.Join("\n", tramo), o, ct);
        resumen ??= "";

        // Verificación posterior: ¿sobrevivieron los identificadores?
        var esperados = viejos.SelectMany(m => Identificadores().Matches(m.Texto).Select(x => x.Value))
            .Concat(Identificadores().Matches(conv.Resumen ?? "").Select(x => x.Value))
            .Distinct().ToList();
        var faltantes = esperados.Where(id => !resumen.Contains(id) && !conv.DatosClave.Contains(id)).ToList();

        var rescatados = new List<string>();
        var perdidos = new List<string>();
        if (o.Resumidor == Resumidor.Fiel)
        {
            conv.DatosClave.AddRange(faltantes);
            rescatados.AddRange(faltantes);
        }
        else
            perdidos.AddRange(faltantes);

        foreach (var m in viejos) conv.Mensajes.Remove(m);
        conv.Resumen = resumen;
        conv.MensajesResumidos += viejos.Count;

        return new EventoCompactacion(tokensAntes, medir(o), viejos.Count, tramos.Count, resumen, rescatados, perdidos, o.Resumidor, reloj.Elapsed);
    }

    static string Linea(Mensaje m) => m.Rol switch
    {
        Rol.Usuario => $"Cliente: {m.Texto}",
        Rol.Asistente when m.Llamadas.Count > 0 => $"Asistente (consulta {string.Join(", ", m.Llamadas.Select(l => l.ToString()))}) {m.Texto}".TrimEnd(),
        Rol.Asistente => $"Asistente: {m.Texto}",
        // Los resultados ya fueron procesados: alcanza con el principio para el resumen.
        _ => $"Resultado de {m.Herramienta}: {(m.Texto.Length > 400 ? m.Texto[..400] + "…" : m.Texto)}",
    };

    async Task<string> Resumir(string? previo, string tramo, OpcionesContexto o, CancellationToken ct)
    {
        var pedido = new StringBuilder();
        if (o.Resumidor == Resumidor.Fiel)
        {
            pedido.AppendLine("Resumí esta conversación entre un cliente y el asistente de reservas de MesaYa para que otro agente pueda continuarla.");
            pedido.AppendLine("Reglas:");
            pedido.AppendLine("- Copiá textualmente todos los identificadores: números de reserva (RSV-...), teléfonos, sucursales, fechas, horarios y cantidad de personas.");
            pedido.AppendLine("- Anotá las decisiones del cliente y lo que quedó pendiente.");
            pedido.AppendLine("- No agregues nada que no esté en el texto.");
            pedido.AppendLine("- Formato: viñetas cortas, como máximo 8.");
        }
        else
        {
            // Resumidor defectuoso a propósito: generaliza y descarta números y códigos.
            pedido.AppendLine("Contá en una o dos oraciones generales de qué trató esta conversación. No incluyas números, códigos, fechas ni horarios.");
        }
        if (!string.IsNullOrWhiteSpace(previo))
            pedido.AppendLine().AppendLine("Resumen previo (integralo en el nuevo):").AppendLine(previo);
        pedido.AppendLine().AppendLine("<conversacion>").AppendLine(tramo).AppendLine("</conversacion>");

        var cuerpo = new JsonObject
        {
            ["model"] = o.Modelo,
            ["stream"] = false,
            ["think"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "Resumís conversaciones de atención al cliente. Respondé solo con el resumen." },
                new JsonObject { ["role"] = "user", ["content"] = pedido.ToString() }),
            ["options"] = new JsonObject { ["num_ctx"] = o.Ventana, ["num_predict"] = 400, ["temperature"] = 0, ["seed"] = 42 }
        };
        var r = await ollama.Chat(cuerpo, ct);
        return r.Texto;
    }
}
