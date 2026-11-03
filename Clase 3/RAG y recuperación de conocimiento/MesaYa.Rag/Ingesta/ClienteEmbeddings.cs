using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace MesaYa.Rag.Ingesta;

/// <summary>
/// Embeddings con Ollama (<c>POST /api/embed</c>). El mismo cliente se usa al indexar y al consultar:
/// indexar y consultar con modelos distintos produce vectores incomparables (slide 04).
/// </summary>
public sealed class ClienteEmbeddings(HttpClient http, IOptions<OpcionesRag> opciones)
{
    readonly OpcionesRag o = opciones.Value;

    sealed record Respuesta([property: JsonPropertyName("embeddings")] float[][] Embeddings);

    public string Modelo => o.ModeloEmbeddings;

    public async Task<float[]> Embeber(string texto, CancellationToken ct = default) =>
        (await Embeber([texto], ct))[0];

    public async Task<float[][]> Embeber(IReadOnlyList<string> textos, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync("/api/embed",
            new { model = o.ModeloEmbeddings, input = textos, truncate = true }, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Error de Ollama ({(int)resp.StatusCode}) con el modelo {o.ModeloEmbeddings}: {await resp.Content.ReadAsStringAsync(ct)}. " +
                $"¿Está instalado? ollama pull {o.ModeloEmbeddings}");

        var vectores = (await resp.Content.ReadFromJsonAsync<Respuesta>(ct))?.Embeddings
            ?? throw new InvalidOperationException("Ollama devolvió una respuesta sin embeddings.");
        if (vectores.Length != textos.Count)
            throw new InvalidOperationException($"Se pidieron {textos.Count} embeddings y llegaron {vectores.Length}.");
        if (vectores.FirstOrDefault(v => v.Length != o.Dimensiones) is { } malo)
            throw new InvalidOperationException(
                $"{o.ModeloEmbeddings} devuelve {malo.Length} dimensiones y la columna es vector({o.Dimensiones}).");
        return vectores;
    }
}
