using System.Diagnostics;
using System.Text;
using MesaYa.Contexto.Api;
using MesaYa.Contexto.Contexto;
using MesaYa.Contexto.Escenarios;
using MesaYa.Contexto.Ollama;

namespace MesaYa.Contexto.Agente;

/// <summary>Una llamada al modelo con la ventana exacta que se envió.</summary>
public sealed class LlamadaModelo
{
    public required int Numero { get; init; }
    public required int Turno { get; init; }
    public required Ventana Ventana { get; init; }
    public string Texto { get; init; } = "";
    public IReadOnlyList<LlamadaTool> Llamadas { get; init; } = [];
    public UsoModelo? Uso { get; init; }
    public TimeSpan Duracion { get; init; }
    public EventoCompactacion? Compactacion { get; init; }

    /// <summary>Tokens que Ollama dice haber procesado. Si desbordó, es menos que lo estimado: el resto se recortó.</summary>
    public long? TokensReales => Uso?.TokensEntrada;
}

/// <summary>Métricas de una corrida de escenario, para la tarjeta de comparación.</summary>
public sealed record ResultadoCorrida(
    string Variante,
    int LlamadasModelo,
    long TokensEntradaTotal,
    int MaxEstimado,
    long? MaxReal,
    TimeSpan Tiempo,
    bool Desbordo,
    int Compactaciones,
    IReadOnlyList<string> Tools,
    string Respuesta);

/// <summary>Un renglón del chat: un mensaje o la marca de una compactación.</summary>
public sealed record ItemChat(Mensaje? Mensaje, EventoCompactacion? Compactacion);

public sealed record SolicitudConfirmacion(LlamadaTool Llamada, TaskCompletionSource<bool> Respuesta);

/// <summary>
/// Sesión del agente de una pestaña del navegador: guarda la conversación, arma la ventana
/// en cada paso del loop, compacta cuando corresponde y registra todo para los Rayos X.
/// </summary>
public sealed class SesionDemo(
    ClienteOllama ollama, ApiMesaYa api, Herramientas herramientas, Compactador compactador, IConfiguration config) : IDisposable
{
    const int MaxPasos = 6;
    static readonly TimeSpan ZonaHoraria = TimeSpan.FromHours(-3);

    public OpcionesContexto Opciones { get; } = Crear(config);
    public EstadoConversacion Conversacion { get; } = new();
    public ConstructorContexto Constructor { get; } = new();
    public PerfilCliente? Perfil { get; private set; } = Catalogo.Martin;
    public List<LlamadaModelo> Llamadas { get; } = [];

    /// <summary>Lo que ve la persona en el chat: nunca se compacta, a diferencia de lo que ve el modelo.</summary>
    public List<ItemChat> Transcripcion { get; } = [];
    public Escenario? Escenario { get; private set; }
    public Variante? Variante { get; private set; }
    public Dictionary<string, ResultadoCorrida> Resultados { get; } = [];

    public bool Ocupado { get; private set; }
    public string? Estado { get; private set; }
    public string? Error { get; private set; }
    public SolicitudConfirmacion? Confirmacion { get; private set; }

    /// <summary>Para ensayos sin UI: responde solo las confirmaciones (false = rechazar).</summary>
    public bool? RespuestaAutomatica { get; set; }

    /// <summary>Acciones que el agente intentó y qué se respondió. El ensayo las informa.</summary>
    public List<string> AccionesSolicitadas { get; } = [];

    public event Action? Cambio;

    CancellationTokenSource? cts;
    string? documentos;
    bool documentosDelimitados;

    static OpcionesContexto Crear(IConfiguration config)
    {
        var o = new OpcionesContexto { Modelo = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? config["Ollama:ModeloInicial"] ?? "qwen3.5:9b" };
        Catalogo.Base(o);
        return o;
    }

    void Agregar(Mensaje m)
    {
        Conversacion.Mensajes.Add(m);
        Transcripcion.Add(new ItemChat(m, null));
    }

    /// <summary>¿Este mensaje sigue literal en el contexto o ya pasó al resumen?</summary>
    public bool EnContexto(Mensaje m) => Conversacion.Mensajes.Contains(m);

    void Avisar(string? estado = null)
    {
        if (estado is not null) Estado = estado;
        Cambio?.Invoke();
    }

    static DateTimeOffset Ahora() => DateTimeOffset.UtcNow.ToOffset(ZonaHoraria);

    /// <summary>La ventana que se enviaría ahora mismo. Sirve para mostrarla antes de mandar nada.</summary>
    public async Task<Ventana> ArmarVentana()
    {
        if (Opciones.Estrategia == Estrategia.PreCarga && (documentos is null || documentosDelimitados != Opciones.Delimitar))
            documentos = await PreCargarDocumentos();
        return Constructor.Construir(Conversacion, Opciones, Perfil, documentos, Ahora());
    }

    /// <summary>Pre-carga "ingenua": todo lo que podría hacer falta, tal cual lo devuelve la API.</summary>
    async Task<string> PreCargarDocumentos()
    {
        documentosDelimitados = Opciones.Delimitar;
        var partes = new List<(string Id, string Fuente, string Texto)>();
        foreach (var suc in new[] { "PAL", "BEL", "CAB" })
            partes.Add(($"menu-{suc}", $"GET /v1/menu?sucursal_id={suc}", (await api.Menu(suc)).Cuerpo?.ToJsonString(Herramientas.Json) ?? ""));
        partes.Add(("politicas", "GET /v1/politicas", (await api.Politicas()).Cuerpo?.ToJsonString(Herramientas.Json) ?? ""));
        partes.Add(("resenas", "GET /v1/resenas", (await api.Resenas(null)).Cuerpo?.ToJsonString(Herramientas.Json) ?? ""));

        var sb = new StringBuilder();
        foreach (var (id, fuente, texto) in partes)
            sb.AppendLine(Opciones.Delimitar ? $"<documento id=\"{id}\" fuente=\"{fuente}\">{texto}</documento>" : $"## {id}\n{texto}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Un turno del cliente: loop de modelo y herramientas hasta la respuesta final.</summary>
    public async Task Enviar(string texto)
    {
        if (Ocupado || string.IsNullOrWhiteSpace(texto)) return;
        Ocupado = true;
        Error = null;
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        var desde = Llamadas.Count;
        var reloj = Stopwatch.StartNew();
        var turno = Conversacion.TurnoActual + 1;
        Agregar(new Mensaje { Rol = Rol.Usuario, Texto = texto.Trim(), Turno = turno });
        Avisar("Armando el contexto…");

        try
        {
            for (var paso = 1; ; paso++)
            {
                var ventana = await ArmarVentana();
                EventoCompactacion? compactacion = null;
                if (Opciones.Compactar && ventana.SuperaUmbral)
                {
                    Avisar($"Compactando: {ventana.Estimado:N0} tokens superan el umbral de {ventana.Umbral:N0}…");
                    compactacion = await compactador.Compactar(Conversacion, Opciones, Constructor, ventana.Estimado,
                        o => Constructor.Construir(Conversacion, o, Perfil, documentos, Ahora()).Estimado, ct);
                    if (compactacion is not null)
                    {
                        Transcripcion.Add(new ItemChat(null, compactacion));
                        ventana = await ArmarVentana();
                    }
                }

                if (paso > MaxPasos)
                {
                    Agregar(new Mensaje { Rol = Rol.Asistente, Texto = "(Se alcanzó el límite de pasos del agente.)", Turno = turno });
                    break;
                }

                Avisar(ventana.Desborda
                    ? $"Esperando al modelo… ⚠ {ventana.Estimado:N0} tokens no entran en {ventana.NumCtx:N0}: Ollama va a recortar"
                    : $"Esperando al modelo… ({ventana.Estimado:N0} tokens estimados)");
                var r = await ollama.Chat(ventana.Cuerpo, ct);
                Calibrar(ventana, r.Uso);

                Llamadas.Add(new LlamadaModelo
                {
                    Numero = Llamadas.Count + 1, Turno = turno, Ventana = ventana, Texto = r.Texto,
                    Llamadas = r.Llamadas, Uso = r.Uso, Duracion = r.Duracion, Compactacion = compactacion
                });

                if (r.Llamadas.Count == 0)
                {
                    Agregar(new Mensaje { Rol = Rol.Asistente, Texto = r.Texto, Turno = turno });
                    // El modelo ya usó los resultados: a partir de ahora se pueden limpiar sin perder nada.
                    foreach (var m in Conversacion.Mensajes.Where(m => m.Rol == Rol.Herramienta)) m.Procesado = true;
                    break;
                }

                Agregar(new Mensaje { Rol = Rol.Asistente, Texto = r.Texto, Llamadas = r.Llamadas, Turno = turno });
                foreach (var llamada in r.Llamadas)
                {
                    string resultado;
                    if (Herramientas.EsAccion(llamada.Nombre) && Opciones.ConfirmarAcciones && !await PedirConfirmacion(llamada, ct))
                        resultado = """{"codigo":"RECHAZADO_POR_EL_USUARIO","mensaje":"La acción NO se ejecutó porque el cliente no la confirmó. No digas que se hizo y no la reintentes sin preguntarle."}""";
                    else
                    {
                        Avisar($"Ejecutando {llamada}…");
                        resultado = await herramientas.Ejecutar(llamada, Opciones, Perfil);
                    }
                    Agregar(new Mensaje { Rol = Rol.Herramienta, Herramienta = llamada.Nombre, Texto = resultado, Turno = turno });
                    Avisar();
                }
            }
            Estado = null;
        }
        catch (OperationCanceledException)
        {
            Estado = "Detenido.";
        }
        catch (Exception ex)
        {
            Error = ex is HttpRequestException ? $"No se pudo hablar con Ollama o con la API: {ex.Message}" : ex.Message;
            Estado = null;
        }
        finally
        {
            Confirmacion = null;
            Ocupado = false;
            if (Escenario is not null && Variante is not null)
                Resultados[$"{Escenario.Id}/{Variante.Id}"] = Resumir(desde, reloj.Elapsed);
            Avisar();
        }
    }

    async Task<bool> PedirConfirmacion(LlamadaTool llamada, CancellationToken ct)
    {
        if (RespuestaAutomatica is { } automatica)
        {
            AccionesSolicitadas.Add($"{llamada} → {(automatica ? "aprobada" : "rechazada")} (automático)");
            return automatica;
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Confirmacion = new SolicitudConfirmacion(llamada, tcs);
        Avisar($"Esperando confirmación humana para {llamada.Nombre}…");
        try
        {
            var aprobada = await tcs.Task.WaitAsync(ct);
            AccionesSolicitadas.Add($"{llamada} → {(aprobada ? "aprobada" : "rechazada")}");
            return aprobada;
        }
        finally { Confirmacion = null; }
    }

    public void Confirmar(bool aprobar) => Confirmacion?.Respuesta.TrySetResult(aprobar);

    /// <summary>Ajusta la estimación por caracteres con el total real. Solo si no hubo recorte.</summary>
    void Calibrar(Ventana v, UsoModelo? uso)
    {
        if (uso is null || v.Desborda || uso.TokensEntrada >= v.NumCtx - 8) return;
        var crudo = ConstructorContexto.EstimarCrudo(v, Constructor.Calibracion);
        if (crudo <= 0) return;
        Constructor.Calibracion = Math.Clamp(uso.TokensEntrada / (double)crudo, 0.5, 2.0);
    }

    ResultadoCorrida Resumir(int desde, TimeSpan tiempo)
    {
        var llamadas = Llamadas.Skip(desde).ToList();
        var respuesta = Conversacion.Mensajes.LastOrDefault(m => m.Rol == Rol.Asistente && m.Llamadas.Count == 0)?.Texto ?? Error ?? "";
        return new ResultadoCorrida(
            Variante!.Etiqueta,
            llamadas.Count,
            llamadas.Sum(l => l.TokensReales ?? 0),
            llamadas.Count == 0 ? 0 : llamadas.Max(l => l.Ventana.Estimado),
            llamadas.Count == 0 ? null : llamadas.Max(l => l.TokensReales),
            tiempo,
            llamadas.Any(l => l.Ventana.Desborda),
            llamadas.Count(l => l.Compactacion is not null),
            llamadas.SelectMany(l => l.Llamadas.Select(t => t.Nombre)).ToList(),
            respuesta);
    }

    public void Detener()
    {
        cts?.Cancel();
        Confirmacion?.Respuesta.TrySetResult(false);
    }

    /// <summary>Cambia entre un cliente identificado (Martín, por WhatsApp) y un chat web anónimo.</summary>
    public void CambiarCliente(bool identificado)
    {
        if (Ocupado) return;
        Perfil = identificado ? Catalogo.Martin : null;
        Avisar();
    }

    /// <summary>Conversación nueva en modo libre, conservando modelo y perillas.</summary>
    public void NuevaConversacion()
    {
        if (Ocupado) return;
        Conversacion.Limpiar();
        Transcripcion.Clear();
        Llamadas.Clear();
        Escenario = null;
        Variante = null;
        Error = null;
        Estado = null;
        Perfil = Catalogo.Martin;
        Avisar();
    }

    /// <summary>Reinicia todo, aplica la variante, carga la conversación previa y hace la pregunta del escenario.</summary>
    public async Task Correr(Escenario e, Variante v)
    {
        if (Ocupado) return;
        Ocupado = true;
        try
        {
            Avisar("Reiniciando la API y cargando el escenario…");
            await api.Reiniciar();
            Conversacion.Limpiar();
            Transcripcion.Clear();
            Llamadas.Clear();
            AccionesSolicitadas.Clear();
            Error = null;
            documentos = null;
            Escenario = e;
            Variante = v;
            Perfil = e.ClienteIdentificado ? Catalogo.Martin : null;
            Catalogo.Base(Opciones);
            Opciones.Ventana = e.Ventana;
            v.Configurar(Opciones);

            var turno = 0;
            foreach (var paso in e.Semilla)
            {
                turno++;
                Agregar(new Mensaje { Rol = Rol.Usuario, Texto = paso.Cliente, Turno = turno, Precargado = true });
                if (paso.Tool is { } tool)
                {
                    Agregar(new Mensaje { Rol = Rol.Asistente, Llamadas = [tool], Turno = turno, Precargado = true });
                    var resultado = await herramientas.Ejecutar(tool, Opciones, Perfil);
                    Agregar(new Mensaje
                    {
                        Rol = Rol.Herramienta, Herramienta = tool.Nombre, Texto = resultado, Turno = turno, Precargado = true, Procesado = true
                    });
                }
                Agregar(new Mensaje { Rol = Rol.Asistente, Texto = paso.Asistente, Turno = turno, Precargado = true });
            }
        }
        catch (Exception ex)
        {
            Error = $"No se pudo preparar el escenario: {ex.Message}";
            return;
        }
        finally
        {
            Ocupado = false;
            Avisar();
        }
        await Enviar(e.Pregunta);
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
