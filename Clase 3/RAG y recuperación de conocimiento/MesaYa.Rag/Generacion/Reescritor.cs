using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MesaYa.Rag.Generacion;

/// <summary>
/// Query rewriting (slide 15): un modelo chico convierte la pregunta en una consulta de búsqueda
/// que se entiende sin el historial. «¿Y para los contratados?» → «vacaciones del personal contratado».
/// </summary>
public sealed class Reescritor(HttpClient http, IOptions<OpcionesRag> opciones)
{
    readonly OpcionesRag o = opciones.Value;

    public string Modelo => o.ModeloReescritura;

    const string Instrucciones = """
        Reescribís preguntas para un buscador de documentos internos de MesaYa, una cadena de restaurantes.
        Convertí la última pregunta del usuario en una consulta de búsqueda autónoma: tiene que entenderse
        sin leer la conversación. Reemplazá pronombres y elipsis por los temas concretos de la conversación.
        Si la última pregunta cambia de sujeto, la consulta es sobre el sujeto nuevo: no mezcles los dos.
        Conservá códigos, números y nombres propios tal como aparecen.
        Respondé solo con la consulta, en una línea, sin comillas ni explicaciones.

        Ejemplo
        Usuario: ¿Hasta qué hora abre la sucursal de Palermo?
        Asistente: Hasta las 0.30.
        Última pregunta: ¿Y la de Belgrano?
        Consulta: horario de apertura de la sucursal Belgrano
        """;

    public sealed record Reescritura(string Consulta, string Prompt);

    public async Task<Reescritura> Reescribir(string pregunta, IReadOnlyList<MensajeChat> historial, CancellationToken ct = default)
    {
        var conversacion = new StringBuilder();
        foreach (var m in historial.TakeLast(6))
            conversacion.AppendLine($"{(m.Rol == MensajeChat.Usuario ? "Usuario" : "Asistente")}: {m.Texto}");
        var pedido = $"""
            <conversacion>
            {conversacion.ToString().TrimEnd()}
            </conversacion>
            <ultima_pregunta>{pregunta}</ultima_pregunta>
            """;

        var body = new JsonObject
        {
            ["model"] = o.ModeloReescritura,
            ["stream"] = false,
            ["think"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_predict"] = 60 },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instrucciones },
                new JsonObject { ["role"] = "user", ["content"] = pedido })
        };
        using var resp = await http.PostAsJsonAsync("/api/chat", body, ct);
        var texto = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Error de Ollama ({(int)resp.StatusCode}) con {o.ModeloReescritura}: {texto}");

        var consulta = Limpiar(JsonNode.Parse(texto)?["message"]?["content"]?.GetValue<string>() ?? "");
        // Si el modelo devuelve algo vacío, se busca con la pregunta original.
        return new Reescritura(consulta.Length == 0 ? pregunta : consulta, $"{Instrucciones}\n{pedido}");
    }

    /// <summary>Primera línea con texto, sin comillas ni prefijos como «Consulta:».</summary>
    static string Limpiar(string respuesta)
    {
        var linea = respuesta.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (linea.StartsWith("Consulta:", StringComparison.OrdinalIgnoreCase)) linea = linea[9..].Trim();
        return linea.Trim('"', '«', '»', '\'', '“', '”', ' ');
    }
}
