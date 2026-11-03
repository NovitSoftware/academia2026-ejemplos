namespace MesaYa.Rag.Evaluacion;

/// <summary>
/// Resultados de la evaluación de una sesión de la app. Vive mientras dura el circuito de Blazor,
/// así la corrida sigue aunque se cambie de pestaña.
/// </summary>
public sealed class EstadoEvaluacion(Evaluador evaluador) : IDisposable
{
    readonly CancellationTokenSource cancelacion = new();

    public List<ResultadoConfiguracion> Resultados { get; } = [];
    public bool Corriendo { get; private set; }
    public string? EnCurso { get; private set; }
    public int Hechas { get; private set; }
    public int Total { get; private set; }

    public event Func<Task>? Cambio;

    public IReadOnlyList<Pipeline.PreguntaEvaluacion> Preguntas() => evaluador.Preguntas();

    public async Task Correr(bool conReranker)
    {
        if (Corriendo) return;
        Corriendo = true;
        Resultados.Clear();
        var configs = Evaluador.Configuraciones.Where(c => conReranker || !c.Rerank).ToList();
        Total = configs.Count * evaluador.Preguntas().Count;
        Hechas = 0;
        try
        {
            foreach (var c in configs)
            {
                EnCurso = c.Nombre;
                await Avisar();
                var antes = Hechas;
                var r = await evaluador.Evaluar(c, async (hechas, _) => { Hechas = antes + hechas; await Avisar(); }, cancelacion.Token);
                Resultados.Add(r);
                Hechas = antes + r.Preguntas.Count;
                await Avisar();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Corriendo = false;
            EnCurso = null;
            await Avisar();
        }
    }

    Task Avisar() => Cambio?.Invoke() ?? Task.CompletedTask;

    public void Dispose()
    {
        cancelacion.Cancel();
        cancelacion.Dispose();
    }
}
