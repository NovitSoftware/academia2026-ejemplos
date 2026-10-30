using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesaYa.Tooling.Services;

/// <summary>Consulta los modelos instalados en el servidor local de Ollama.</summary>
/// <param name="http">Cliente HTTP con la dirección del servidor de Ollama.</param>
public sealed class CatalogoOllama(HttpClient http)
{
    readonly HttpClient http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Devuelve los nombres de modelos instalados, ordenados y sin duplicados.</summary>
    /// <param name="cancellationToken">Permite cancelar la consulta al cerrar la página.</param>
    /// <returns>Los nombres exactos que admite la API de chat de Ollama.</returns>
    public async Task<IReadOnlyList<string>> Listar(CancellationToken cancellationToken = default)
    {
        var respuesta = await http.GetFromJsonAsync<RespuestaModelos>("/api/tags", cancellationToken);
        if (respuesta?.Models is null)
            throw new JsonException("Ollama devolvió un catálogo de modelos inválido.");
        return respuesta.Models
            .Select(m => m?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    sealed record RespuestaModelos([property: JsonPropertyName("models")] ModeloInstalado?[]? Models);
    sealed record ModeloInstalado([property: JsonPropertyName("name")] string? Name);
}
