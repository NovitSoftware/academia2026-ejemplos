using System.Diagnostics;
using System.Text;
using MesaYa.Rag.Busqueda;
using MesaYa.Rag.Generacion;
using MesaYa.Rag.Ingesta;

namespace MesaYa.Rag.Pipeline;

/// <summary>Controles de la barra superior que afectan a un turno.</summary>
public sealed record OpcionesTurno(
    string Perfil = "clientes",
    string Tenant = "mesaya-ar",
    string Estrategia = Estrategias.Estructural,
    ModoBusqueda Modo = ModoBusqueda.Hibrida,
    FiltroPermisos Permisos = FiltroPermisos.Pre,
    bool SoloVigentes = true,
    int Candidatos = 50,
    int TopFinal = 5,
    bool Reescribir = false,
    bool Rerankear = false)
{
    /// <summary>Filas que se piden a la búsqueda: más que las que entran al contexto, para verlas en Ranking.</summary>
    public const int FilasRanking = 20;
}

public enum EstadoEtapa { Pendiente, EnCurso, Lista, Omitida, Error }

/// <summary>Una etapa del pipeline tal como se muestra en la pestaña Pipeline.</summary>
public sealed class Etapa(string nombre, string descripcion)
{
    public string Nombre { get; } = nombre;
    public string Descripcion { get; } = descripcion;
    public EstadoEtapa Estado { get; set; } = EstadoEtapa.Pendiente;
    public string Entrada { get; set; } = "";
    public string Salida { get; set; } = "";
    public TimeSpan? Duracion { get; set; }
}

/// <summary>Un turno de la conversación con todo lo que pasó en el pipeline.</summary>
public sealed class TurnoRag(string pregunta, OpcionesTurno opciones)
{
    public string Pregunta { get; } = pregunta;
    public OpcionesTurno Opciones { get; } = opciones;
    public DateTime Inicio { get; } = DateTime.Now;

    public Etapa Reescritura { get; } = new("Reescribir", "Aclara la consulta con el historial.");
    public Etapa Recuperacion { get; } = new("Recuperar", "Busca candidatos en pgvector.");
    public Etapa Reranking { get; } = new("Rerankear", "Ordena los candidatos con un cross-encoder y se queda con los mejores.");
    public Etapa Generacion { get; } = new("Generar", "Responde con los fragmentos seleccionados y cita.");
    public IReadOnlyList<Etapa> Etapas => [Reescritura, Recuperacion, Reranking, Generacion];

    /// <summary>La consulta que se usó para buscar (igual a la pregunta si no hubo reescritura).</summary>
    public string Consulta { get; set; } = pregunta;
    public string? PromptReescritura { get; set; }
    public ResultadoBusqueda? Busqueda { get; set; }
    public RerankHecho? Rerank { get; set; }
    public IReadOnlyList<Resultado> Seleccionados { get; set; } = [];
    public PromptGenerado? Prompt { get; set; }
    public StringBuilder Respuesta { get; } = new();
    public UsoGeneracion? Uso { get; set; }
    public string? Error { get; set; }
    public bool Terminado { get; set; }
    /// <summary>En modo guion, cuándo se grabó el turno que se está reproduciendo.</summary>
    public DateTime? Grabado { get; set; }
}

/// <summary>
/// El pipeline completo de la slide 02 para un turno: reescribir, recuperar, rerankear y generar.
/// Cada etapa deja registro de lo que recibió y produjo.
/// </summary>
public sealed class PipelineRag(Buscador buscador, Generador generador, Reescritor reescritor, Reranker reranker, Guion guion)
{
    /// <param name="notificar">Se llama cada vez que cambia algo visible del turno.</param>
    /// <param name="modoGuion">Reproduce una grabación en lugar de llamar a Ollama, al reranker y a la base.</param>
    public async Task Ejecutar(TurnoRag turno, IReadOnlyList<MensajeChat> historial, Func<Task> notificar,
        bool modoGuion = false, CancellationToken ct = default)
    {
        if (modoGuion)
        {
            await Reproducir(turno, historial, notificar, ct);
            return;
        }
        await EjecutarEnVivo(turno, historial, notificar, ct);
        if (turno.Error is null && turno.Generacion.Estado == EstadoEtapa.Lista)
            await guion.Guardar(turno, historial);
    }

    async Task EjecutarEnVivo(TurnoRag turno, IReadOnlyList<MensajeChat> historial, Func<Task> notificar, CancellationToken ct)
    {
        var o = turno.Opciones;
        Etapa? actual = null;
        var reloj = new Stopwatch();

        async Task Empezar(Etapa etapa, string entrada)
        {
            actual = etapa;
            etapa.Estado = EstadoEtapa.EnCurso;
            etapa.Entrada = entrada;
            reloj.Restart();
            await notificar();
        }

        static void Omitir(Etapa etapa, string entrada, string salida)
        {
            etapa.Estado = EstadoEtapa.Omitida;
            etapa.Entrada = entrada;
            etapa.Salida = salida;
        }

        async Task Terminar(string salida)
        {
            actual!.Estado = EstadoEtapa.Lista;
            actual.Salida = salida;
            actual.Duracion = reloj.Elapsed;
            await notificar();
        }

        try
        {
            // 01 · Reescribir.
            if (!o.Reescribir)
                Omitir(turno.Reescritura, turno.Pregunta, "Apagada: se busca con la pregunta literal.");
            else if (historial.Count == 0)
                Omitir(turno.Reescritura, turno.Pregunta, "Sin historial: no hay nada que aclarar, se busca con la pregunta.");
            else
            {
                await Empezar(turno.Reescritura, $"«{turno.Pregunta}» + {historial.Count} mensajes de historial · {reescritor.Modelo}");
                var r = await reescritor.Reescribir(turno.Pregunta, historial, ct);
                turno.Consulta = r.Consulta;
                turno.PromptReescritura = r.Prompt;
                await Terminar($"«{r.Consulta}»");
            }

            // 02 · Recuperar. Se traen los 20 primeros por RRF: son los que ve el Ranking y, si está
            // prendido, los que pasan por el reranker (slide 11). En CPU, 20 pares tardan unos segundos.
            // Con post-filtering el filtro se aplica sobre el top-k que va al contexto (slide 07):
            // si se pidieran más filas, los descartados se reemplazarían y no se vería el problema.
            var k = o.Permisos == FiltroPermisos.Post ? o.TopFinal : OpcionesTurno.FilasRanking;
            await Empezar(turno.Recuperacion,
                $"«{turno.Consulta}» · {o.Modo} · {o.Estrategia} · {o.Candidatos} candidatos por lista");
            turno.Busqueda = await buscador.Buscar(new ConsultaBusqueda(
                turno.Consulta, o.Tenant, o.Perfil, o.Estrategia, o.Modo, o.Permisos, o.SoloVigentes,
                K: k, Candidatos: o.Candidatos), ct);
            var b = turno.Busqueda;
            var notas = new List<string> { $"{b.Resultados.Count} resultados" };
            if (b.Descartados.Count > 0) notas.Add($"{b.Descartados.Count} descartados por permisos después del top-k");
            if (b.Agrupados.Count > 0) notas.Add($"{b.Agrupados.Count} hijos agrupados con su padre");
            notas.Add($"embedding {b.TiempoEmbedding.TotalMilliseconds:F0} ms · SQL {b.TiempoSql.TotalMilliseconds:F0} ms");
            await Terminar(string.Join(" · ", notas));

            // 03 · Rerankear. Sin reranker, pasan los primeros por RRF.
            var porOrden = $"los primeros {Math.Min(o.TopFinal, b.Resultados.Count)} por {(o.Modo == ModoBusqueda.Hibrida ? "RRF" : "posición")}";
            turno.Seleccionados = b.Resultados.Take(o.TopFinal).ToList();
            if (!o.Rerankear || b.Resultados.Count == 0)
                Omitir(turno.Reranking, $"{b.Resultados.Count} candidatos",
                    o.Rerankear ? "No hay candidatos para ordenar." : $"Apagado: pasan {porOrden}.");
            else
            {
                await Empezar(turno.Reranking, $"«{turno.Consulta}» × {b.Resultados.Count} candidatos · {reranker.Modelo}");
                try
                {
                    turno.Rerank = await reranker.Ordenar(turno.Consulta, b.Resultados, ct);
                    turno.Seleccionados = turno.Rerank.Resultados.Take(o.TopFinal).Select(r => r.Original).ToList();
                    var subieron = turno.Seleccionados.Count(s => s.Posicion > o.TopFinal);
                    await Terminar($"Top {turno.Seleccionados.Count} de {b.Resultados.Count}" +
                        (subieron > 0 ? $" · {subieron} entraron desde más abajo del top {o.TopFinal} de RRF" : " · mismos fragmentos que RRF, en otro orden"));
                }
                catch (HttpRequestException ex)
                {
                    // Sin reranker la demo sigue: se avisa en la etapa y se usa el orden de RRF.
                    turno.Reranking.Estado = EstadoEtapa.Error;
                    turno.Reranking.Salida = $"{ex.Message} Se usan {porOrden}.";
                    turno.Reranking.Duracion = reloj.Elapsed;
                    await notificar();
                }
            }

            // 04 · Generar.
            turno.Prompt = generador.Armar(turno.Pregunta, historial, turno.Seleccionados.Select(s => s.Chunk).ToList(), o.Perfil);
            await Empezar(turno.Generacion,
                $"{turno.Seleccionados.Count} fragmentos · ~{turno.Prompt.TokensAprox} tokens estimados · {turno.Prompt.Modelo}");
            var ultimo = Stopwatch.StartNew();
            await foreach (var texto in generador.Generar(turno.Prompt, uso => turno.Uso = uso, ct))
            {
                turno.Respuesta.Append(texto);
                if (ultimo.ElapsedMilliseconds > 80) { await notificar(); ultimo.Restart(); }
            }
            var u = turno.Uso;
            await Terminar(u is null ? "Respuesta completa."
                : $"{u.TokensEntrada} tokens de entrada · {u.TokensSalida} de salida · " +
                  $"evaluación {u.EvaluacionEntrada.TotalMilliseconds:F0} ms · generación {u.Generacion.TotalSeconds:F1} s" +
                  (u.Carga > TimeSpan.FromSeconds(1) ? $" · carga del modelo {u.Carga.TotalSeconds:F1} s" : ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            turno.Error = ex.Message;
            if (actual is not null)
            {
                actual.Estado = EstadoEtapa.Error;
                actual.Salida = ex.Message;
                actual.Duracion = reloj.Elapsed;
            }
        }
        finally
        {
            turno.Terminado = true;
            await notificar();
        }
    }

    /// <summary>
    /// Reproduce un turno grabado con pausas cortas, para que en clase se vea avanzar el pipeline.
    /// Los objetos se vuelven a enlazar por id de chunk para que Ranking y Contexto los reconozcan.
    /// </summary>
    async Task Reproducir(TurnoRag turno, IReadOnlyList<MensajeChat> historial, Func<Task> notificar, CancellationToken ct)
    {
        try
        {
            var g = guion.Buscar(Guion.Clave(turno.Pregunta, historial, turno.Opciones));
            if (g is null)
            {
                turno.Error = "No hay una grabación de esta pregunta con este historial y estos controles. " +
                              "Apagá el modo guion o corré la pregunta en vivo una vez para grabarla.";
                return;
            }
            turno.Grabado = g.Fecha;
            var porId = g.Busqueda?.Resultados.ToDictionary(r => r.Chunk.Id) ?? [];

            foreach (var grabada in g.Etapas)
            {
                var etapa = turno.Etapas.First(e => e.Nombre == grabada.Nombre);
                etapa.Entrada = grabada.Entrada;
                if (grabada.Estado is EstadoEtapa.Lista or EstadoEtapa.Error)
                {
                    etapa.Estado = EstadoEtapa.EnCurso;
                    await notificar();
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(grabada.Duracion?.TotalMilliseconds ?? 0, 200, 700)), ct);
                }

                if (etapa == turno.Reescritura)
                {
                    turno.Consulta = g.Consulta;
                    turno.PromptReescritura = g.PromptReescritura;
                }
                else if (etapa == turno.Recuperacion)
                    turno.Busqueda = g.Busqueda;
                else if (etapa == turno.Reranking)
                {
                    turno.Rerank = g.Rerank is null ? null : g.Rerank with
                    {
                        Resultados = g.Rerank.Resultados.Select(r => r with { Original = porId[r.Original.Chunk.Id] }).ToList()
                    };
                    turno.Seleccionados = g.Seleccionados.Select(id => porId[id]).ToList();
                }
                else if (etapa == turno.Generacion)
                {
                    turno.Prompt = g.Prompt;
                    for (var i = 0; i < g.Respuesta.Length; i += 6)
                    {
                        turno.Respuesta.Append(g.Respuesta.AsSpan(i, Math.Min(6, g.Respuesta.Length - i)));
                        if (i % 60 == 0) await notificar();
                        await Task.Delay(12, ct);
                    }
                    turno.Uso = g.Uso;
                }

                etapa.Estado = grabada.Estado;
                etapa.Salida = grabada.Salida;
                etapa.Duracion = grabada.Duracion;
                await notificar();
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            turno.Error = $"No se pudo reproducir la grabación: {ex.Message}";
        }
        finally
        {
            turno.Terminado = true;
            await notificar();
        }
    }
}
