using System.Diagnostics;
using MesaYa.Rag.Busqueda;
using MesaYa.Rag.Generacion;
using MesaYa.Rag.Ingesta;
using MesaYa.Rag.Pipeline;

namespace MesaYa.Rag.Evaluacion;

/// <summary>Una fila de la tabla de métricas.</summary>
public sealed record ConfiguracionEval(string Nombre, string Estrategia, ModoBusqueda Modo, bool Rerank = false, bool Reescribir = false);

/// <summary>Cómo le fue a una pregunta con una configuración.</summary>
/// <param name="Rango">Posición (1 a 5) del primer fragmento esperado; null si no apareció.</param>
/// <param name="Prohibidos">Documentos prohibidos que llegaron al top 5.</param>
public sealed record ResultadoPregunta(
    PreguntaEvaluacion Pregunta,
    string Consulta,
    IReadOnlyList<ChunkRecuperado> Top,
    int? Rango,
    IReadOnlyList<string> Prohibidos)
{
    public bool CuentaParaHitRate => Pregunta.Tipo == "recuperacion";
    public bool Acierto => CuentaParaHitRate ? Rango is not null && Prohibidos.Count == 0 : Prohibidos.Count == 0;
}

public sealed record ResultadoConfiguracion(
    ConfiguracionEval Config,
    IReadOnlyList<ResultadoPregunta> Preguntas,
    TimeSpan Duracion,
    string? Error = null)
{
    IEnumerable<ResultadoPregunta> DeRecuperacion => Preguntas.Where(p => p.CuentaParaHitRate);
    public int Evaluadas => DeRecuperacion.Count();
    public int Aciertos => DeRecuperacion.Count(p => p.Rango is not null);
    public double HitRate => Evaluadas == 0 ? 0 : (double)Aciertos / Evaluadas;
    public double Mrr => Evaluadas == 0 ? 0 : DeRecuperacion.Sum(p => p.Rango is { } r ? 1.0 / r : 0) / Evaluadas;
    /// <summary>Preguntas en las que un documento prohibido (otro tenant, otra audiencia, obsoleto) llegó al contexto.</summary>
    public int Violaciones => Preguntas.Count(p => p.Prohibidos.Count > 0);
}

/// <summary>
/// Trabajo práctico del slide 16: mide hit rate@5 y MRR@5 de cada configuración sobre las preguntas
/// de evaluacion/preguntas.json. Solo mide la recuperación: no llama al modelo que responde.
/// </summary>
public sealed class Evaluador(Buscador buscador, Reranker reranker, Reescritor reescritor, Ejemplos ejemplos)
{
    public const int K = 5;

    public static readonly IReadOnlyList<ConfiguracionEval> Configuraciones =
    [
        new("Semántica · fijo-150", Estrategias.Fijo, ModoBusqueda.Semantica),
        new("Semántica · estructural", Estrategias.Estructural, ModoBusqueda.Semantica),
        new("Palabras clave · estructural", Estrategias.Estructural, ModoBusqueda.PalabrasClave),
        new("Híbrida RRF · estructural", Estrategias.Estructural, ModoBusqueda.Hibrida),
        new("Híbrida RRF · contextual", Estrategias.Contextual, ModoBusqueda.Hibrida),
        new("Híbrida RRF + reranker", Estrategias.Estructural, ModoBusqueda.Hibrida, Rerank: true),
        new("Híbrida RRF + reranker + reescritura", Estrategias.Estructural, ModoBusqueda.Hibrida, Rerank: true, Reescribir: true),
    ];

    public IReadOnlyList<PreguntaEvaluacion> Preguntas() => ejemplos.Leer();

    /// <param name="progreso">Se llama después de cada pregunta con (hechas, total).</param>
    public async Task<ResultadoConfiguracion> Evaluar(ConfiguracionEval c, Func<int, int, Task>? progreso = null, CancellationToken ct = default)
    {
        var preguntas = Preguntas();
        var reloj = Stopwatch.StartNew();
        var resultados = new List<ResultadoPregunta>();
        try
        {
            foreach (var p in preguntas)
            {
                resultados.Add(await EvaluarPregunta(c, p, ct));
                if (progreso is not null) await progreso(resultados.Count, preguntas.Count);
            }
            return new ResultadoConfiguracion(c, resultados, reloj.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return new ResultadoConfiguracion(c, resultados, reloj.Elapsed, ex.Message);
        }
    }

    async Task<ResultadoPregunta> EvaluarPregunta(ConfiguracionEval c, PreguntaEvaluacion p, CancellationToken ct)
    {
        var consulta = p.Pregunta;
        if (c.Reescribir && p.Historial.Count > 0)
        {
            var historial = p.Historial
                .Select(h => new MensajeChat(h.Rol == "usuario" ? MensajeChat.Usuario : MensajeChat.Asistente, h.Texto))
                .ToList();
            consulta = (await reescritor.Reescribir(p.Pregunta, historial, ct)).Consulta;
        }

        // Mismo recorrido que la app: con reranker, los 20 primeros por RRF pasan por el cross-encoder.
        var busqueda = await buscador.Buscar(new ConsultaBusqueda(
            consulta, p.Tenant, p.Perfil, c.Estrategia, c.Modo,
            K: c.Rerank ? OpcionesTurno.FilasRanking : K), ct);
        var top = c.Rerank
            ? (await reranker.Ordenar(consulta, busqueda.Resultados, ct)).Resultados.Take(K).Select(r => r.Original.Chunk).ToList()
            : busqueda.Resultados.Take(K).Select(r => r.Chunk).ToList();

        int? rango = null;
        for (var i = 0; i < top.Count && rango is null; i++)
            if (p.Esperados.Any(e => Coincide(top[i], e))) rango = i + 1;

        var prohibidos = top.Select(t => t.DocumentId).Where(p.Prohibidos.Contains).Distinct().ToList();
        return new ResultadoPregunta(p, consulta, top, rango, prohibidos);
    }

    /// <summary>
    /// Un fragmento acierta si es del documento esperado y cubre la sección esperada. En fijo-150 un chunk
    /// puede cruzar varias secciones; en parent–child cuenta la sección del padre que recibe el modelo.
    /// </summary>
    static bool Coincide(ChunkRecuperado chunk, EsperadoEvaluacion e) =>
        chunk.DocumentId == e.DocumentId
        && (e.Seccion is null
            || chunk.Secciones.Contains(e.Seccion)
            || chunk.SeccionParaElModelo.EndsWith($" › {e.Seccion}", StringComparison.Ordinal));
}
