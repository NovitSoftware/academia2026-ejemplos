using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace MesaYa.Rag.Busqueda;

/// <summary>Un candidato después del reranking, con su posición antes y después.</summary>
public sealed record ResultadoRerank(Resultado Original, int Posicion, double Puntaje);

public sealed record RerankHecho(IReadOnlyList<ResultadoRerank> Resultados, TimeSpan Duracion, string Modelo);

/// <summary>
/// Cross-encoder (slide 14): procesa consulta y fragmento juntos y devuelve un puntaje de relevancia.
/// Una inferencia por par: sirve para decenas de candidatos, no para todo el corpus.
/// Usa la API /rerank de Infinity (docker compose --profile reranker up -d).
/// </summary>
public sealed class Reranker(HttpClient http, IOptions<OpcionesRag> opciones)
{
    readonly OpcionesRag o = opciones.Value;

    sealed record Puntaje(
        [property: JsonPropertyName("index")] int Indice,
        [property: JsonPropertyName("relevance_score")] double Relevancia);

    sealed record Respuesta([property: JsonPropertyName("results")] IReadOnlyList<Puntaje> Resultados);

    public string Modelo => o.ModeloReranker;

    /// <summary>Lo que lee el cross-encoder: la ruta de la sección y el texto que recibiría el modelo.</summary>
    public static string TextoParaRerank(ChunkRecuperado c) => $"{c.SeccionParaElModelo}\n\n{c.ParaElModelo}";

    public async Task<RerankHecho> Ordenar(string consulta, IReadOnlyList<Resultado> candidatos, CancellationToken ct = default)
    {
        var reloj = Stopwatch.StartNew();
        if (candidatos.Count == 0) return new RerankHecho([], reloj.Elapsed, o.ModeloReranker);

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("/rerank", new
            {
                model = o.ModeloReranker,
                query = consulta,
                documents = candidatos.Select(c => TextoParaRerank(c.Chunk)).ToArray(),
                return_documents = false
            }, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                $"No se pudo conectar al reranker en {http.BaseAddress} ({ex.Message}). " +
                "Levantalo con: docker compose --profile reranker up -d", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Error del reranker ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
            var puntajes = (await resp.Content.ReadFromJsonAsync<Respuesta>(ct))?.Resultados
                ?? throw new InvalidOperationException("El reranker devolvió una respuesta vacía.");

            var ordenados = puntajes
                .OrderByDescending(p => p.Relevancia)
                .Select((p, i) => new ResultadoRerank(candidatos[p.Indice], i + 1, p.Relevancia))
                .ToList();
            return new RerankHecho(ordenados, reloj.Elapsed, o.ModeloReranker);
        }
    }
}
