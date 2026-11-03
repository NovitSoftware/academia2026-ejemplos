using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MesaYa.Contexto.Agente;

namespace MesaYa.Contexto.Ollama;

/// <summary>Modelo instalado en Ollama.</summary>
/// <param name="Nombre">Nombre con etiqueta, por ejemplo qwen3.5:9b.</param>
/// <param name="Parametros">Tamaño informado por Ollama, por ejemplo 9.7B.</param>
/// <param name="SoportaTools">Si el modelo declara la capacidad tools.</param>
public sealed record ModeloLocal(string Nombre, string Parametros, bool SoportaTools);

/// <summary>Respuesta de /api/chat adaptada al agente.</summary>
/// <param name="Texto">Texto de la respuesta, vacío si solo pidió herramientas.</param>
/// <param name="Llamadas">Herramientas que el modelo pidió ejecutar.</param>
/// <param name="Uso">Tokens y tiempos medidos por Ollama.</param>
/// <param name="Duracion">Tiempo de la llamada HTTP completa.</param>
public sealed record RespuestaOllama(string Texto, IReadOnlyList<LlamadaTool> Llamadas, UsoModelo? Uso, TimeSpan Duracion);

/// <summary>Cliente mínimo de la API de Ollama. El cuerpo del pedido lo arma <c>ConstructorContexto</c>.</summary>
public sealed class ClienteOllama(IHttpClientFactory fabrica)
{
    HttpClient Http => fabrica.CreateClient("ollama");

    /// <summary>Lista los modelos instalados, con los que soportan tools primero.</summary>
    public async Task<IReadOnlyList<ModeloLocal>> ListarModelos(CancellationToken ct = default)
    {
        var json = await Http.GetFromJsonAsync<JsonObject>("/api/tags", ct);
        var lista = new List<ModeloLocal>();
        foreach (var m in json?["models"]?.AsArray() ?? [])
        {
            var capacidades = m!["capabilities"]?.AsArray().Select(c => c!.GetValue<string>()) ?? [];
            lista.Add(new ModeloLocal(
                m["name"]!.GetValue<string>(),
                m["details"]?["parameter_size"]?.GetValue<string>() ?? "?",
                capacidades.Contains("tools")));
        }
        return lista.OrderByDescending(m => m.SoportaTools).ThenBy(m => m.Nombre).ToList();
    }

    /// <summary>Envía el cuerpo completo a /api/chat sin modificarlo.</summary>
    public async Task<RespuestaOllama> Chat(JsonObject cuerpo, CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        using var resp = await Http.PostAsJsonAsync("/api/chat", cuerpo, ct);
        var texto = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Error de Ollama ({(int)resp.StatusCode}): {texto}");

        var json = JsonNode.Parse(texto)?.AsObject()
            ?? throw new InvalidOperationException("Ollama devolvió una respuesta vacía.");
        var mensaje = json["message"]?.AsObject()
            ?? throw new InvalidOperationException("Ollama devolvió una respuesta sin message.");

        var llamadas = new List<LlamadaTool>();
        foreach (var llamada in mensaje["tool_calls"]?.AsArray() ?? [])
        {
            var funcion = llamada!["function"]!;
            var args = funcion["arguments"] as JsonObject ?? new JsonObject();
            llamadas.Add(new LlamadaTool(funcion["name"]!.GetValue<string>(), (JsonObject)args.DeepClone()));
        }

        return new RespuestaOllama(
            mensaje["content"]?.GetValue<string>()?.Trim() ?? "",
            llamadas,
            UsoModelo.DesdeRespuesta(json),
            reloj.Elapsed);
    }
}
