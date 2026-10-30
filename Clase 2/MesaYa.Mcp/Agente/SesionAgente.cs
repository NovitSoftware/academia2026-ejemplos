using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesaYa.Agent;
using MesaYa.Mcp.Traza;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MesaYa.Mcp.Agente;

public enum TipoPaso { Conexion, Llm, Tool, Elicitacion }
public enum EstadoPaso { EnCurso, Ok, Error }

/// <summary>Una tarjeta del panel "Tool calling": una decisión del modelo o una ejecución de tool.</summary>
public sealed class PasoAgente
{
    public required int Iteracion { get; init; }
    public required TipoPaso Tipo { get; init; }
    public required string Titulo { get; set; }
    public string? Subtitulo { get; set; }
    public string? Entrada { get; set; }
    public string? Salida { get; set; }
    public EstadoPaso Estado { get; set; } = EstadoPaso.EnCurso;
    public DateTimeOffset Inicio { get; } = DateTimeOffset.Now;
    public double? DuracionMs { get; set; }
    public string? Progreso { get; set; }
    public List<string> Avisos { get; } = [];
    public void Terminar(bool ok) { Estado = ok ? EstadoPaso.Ok : EstadoPaso.Error; DuracionMs = (DateTimeOffset.Now - Inicio).TotalMilliseconds; }
}

public sealed record MensajeChat(string Rol, string Texto);

/// <summary>Pedido de datos que el SERVIDOR MCP le hace al usuario a través del cliente (elicitation/create).</summary>
public sealed class ElicitacionPendiente(string mensaje, ElicitRequestParams.RequestSchema? schema)
{
    public string Mensaje { get; } = mensaje;
    public ElicitRequestParams.RequestSchema? Schema { get; } = schema;
    public Dictionary<string, string?> Valores { get; } = [];
    internal TaskCompletionSource<ElicitResult> Respuesta { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// El agente de una pestaña del navegador. Es un HOST MCP: tiene un cliente MCP conectado al servidor
/// (este mismo proceso, por HTTP), descubre las tools con tools/list, se las pasa al modelo local y
/// ejecuta con tools/call lo que el modelo decida. También atiende los pedidos del servidor (elicitation).
/// </summary>
public sealed class SesionAgente(
    BusTraza bus, IHttpClientFactory fabrica, IConfiguration configuracion, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    const int MaxIteraciones = 8;
    static int contador;
    readonly object gate = new();
    readonly List<PasoAgente> pasos = [];
    readonly List<MensajeChat> chat = [];
    readonly List<string> logs = [];
    readonly List<ElicitacionPendiente> elicitaciones = [];
    readonly Stopwatch relojTurno = new();
    TimeSpan ejecucionAcumulada;
    JsonArray historial = [];
    JsonArray toolsOllama = [];
    CancellationTokenSource? turno;
    int iteracion;
    bool disposed;

    public event Action? Cambio;
    public string ConversacionId { get; private set; } = "";
    public string Etiqueta { get; private set; } = "";
    public McpClient? Cliente { get; private set; }
    public IList<McpClientTool> Tools { get; private set; } = [];
    public string? ErrorConexion { get; private set; }
    public bool Conectando { get; private set; }
    public bool Ocupado { get; private set; }
    public bool ToolsEnParalelo { get; set; } = true;
    /// <summary>Modelo de Ollama elegido, o null para usar el guion de demo.</summary>
    public ModeloLocal? Modelo { get; private set; }
    public string NombreModelo => Modelo?.Nombre ?? ModeloGuion.NombreGuion;
    /// <summary>Uso de la última respuesta de Ollama; null si no informó contadores válidos.</summary>
    public UsoModelo? UltimoUso { get; private set; }
    /// <summary>Tokens y tiempos acumulados de las respuestas medidas de la conversación.</summary>
    public UsoModelo? UsoAcumulado { get; private set; }
    /// <summary>Cantidad de respuestas con contadores válidos de entrada y salida.</summary>
    public int RespuestasMedidas { get; private set; }
    /// <summary>Respuestas reales del modelo que no informaron contadores válidos.</summary>
    public int RespuestasSinMetricas { get; private set; }
    /// <summary>Tiempo transcurrido del turno actual, o duración del último turno terminado.</summary>
    public TimeSpan DuracionUltimoTurno => relojTurno.Elapsed;
    /// <summary>Suma de los tiempos de ejecución, incluido el turno activo y las confirmaciones humanas.</summary>
    public TimeSpan DuracionEjecucion => ejecucionAcumulada + (Ocupado ? relojTurno.Elapsed : TimeSpan.Zero);
    public string UrlMcp => (Environment.GetEnvironmentVariable("MCP_URL") ?? configuracion["Mcp:UrlPublica"] ?? "http://localhost:5100").TrimEnd('/') + "/mcp";

    public PasoAgente[] Pasos { get { lock (gate) return [.. pasos]; } }
    public MensajeChat[] Chat { get { lock (gate) return [.. chat]; } }
    public string[] LogsServidor { get { lock (gate) return [.. logs]; } }
    public ElicitacionPendiente? Elicitacion { get { lock (gate) return elicitaciones.FirstOrDefault(); } }
    public int TurnosDeGuion => historial.Count(m => m!["role"]?.ToString() == "user");

    /// <summary>Elige el modelo (o el guion) e inicia una conversación con una sesión MCP nueva.</summary>
    public async Task UsarModelo(ModeloLocal? modelo)
    {
        if (Ocupado) return;
        Modelo = modelo;
        await NuevaConversacion();
    }

    /// <summary>Cierra la sesión MCP actual, abre otra y limpia el historial.</summary>
    public async Task NuevaConversacion()
    {
        if (Ocupado || disposed) return;
        if (Cliente is not null) { await Cliente.DisposeAsync(); Cliente = null; }
        lock (gate) { pasos.Clear(); chat.Clear(); logs.Clear(); elicitaciones.Clear(); }
        historial = [];
        iteracion = 0;
        UltimoUso = null;
        UsoAcumulado = null;
        RespuestasMedidas = 0;
        RespuestasSinMetricas = 0;
        relojTurno.Reset();
        ejecucionAcumulada = TimeSpan.Zero;
        ConversacionId = Guid.NewGuid().ToString("N")[..8];
        Etiqueta = $"Agente #{Interlocked.Increment(ref contador)} · {NombreModelo}";
        bus.Etiquetar(ConversacionId, Etiqueta);
        await Conectar();
    }

    async Task Conectar()
    {
        Conectando = true; ErrorConexion = null; Avisar();
        var paso = AgregarPaso(new PasoAgente { Iteracion = 0, Tipo = TipoPaso.Conexion, Titulo = "Conexión MCP", Subtitulo = UrlMcp });
        var corr = $"conn:{Guid.NewGuid():N}";
        Traza(Actor.Agente, Actor.ClienteMcp, TipoTraza.Request, "McpClient.CreateAsync() + ListToolsAsync()", corr);
        try
        {
            var transporte = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(UrlMcp),
                Name = "mesaya-mcp",
                TransportMode = HttpTransportMode.StreamableHttp
            }, loggerFactory);
            var opciones = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "mesaya-agente-blazor", Title = Etiqueta, Version = "1.0.0" },
                // Fijamos la revisión 2025-11-25 (sesiones HTTP + elicitation clásica) para que el ida y vuelta se vea completo.
                ProtocolVersion = "2025-11-25",
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = ManejarElicitacion,
                    NotificationHandlers =
                    [
                        new(NotificationMethods.LoggingMessageNotification, (n, _) =>
                        {
                            var p = n.Params;
                            Log($"[{p?["level"]}] {p?["data"]}");
                            return ValueTask.CompletedTask;
                        })
                    ]
                }
            };
            Cliente = await McpClient.CreateAsync(transporte, opciones, loggerFactory);
            bus.VincularSesion(Cliente.SessionId, ConversacionId);
            Tools = await Cliente.ListToolsAsync();
            toolsOllama = new JsonArray(Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.JsonSchema.GetRawText())
                }
            }).ToArray());
            paso.Salida = $"Servidor: {Cliente.ServerInfo?.Title ?? Cliente.ServerInfo?.Name} {Cliente.ServerInfo?.Version}\n" +
                          $"Protocolo: {Cliente.NegotiatedProtocolVersion}\nSesión: {Cliente.SessionId}\n" +
                          $"Tools descubiertas: {string.Join(", ", Tools.Select(t => t.Name))}";
            paso.Terminar(true);
            Traza(Actor.ClienteMcp, Actor.Agente, TipoTraza.Response, $"{Tools.Count} tools descubiertas", corr, detalle: paso.Salida);
        }
        catch (Exception e) when (e is HttpRequestException or McpException or TaskCanceledException or InvalidOperationException)
        {
            ErrorConexion = $"No se pudo conectar al servidor MCP en {UrlMcp}: {e.Message}";
            paso.Salida = ErrorConexion;
            paso.Terminar(false);
            Traza(Actor.ClienteMcp, Actor.Agente, TipoTraza.Response, "✖ no se pudo conectar", corr, error: true, detalle: e.ToString());
        }
        finally { Conectando = false; Avisar(); }
    }

    /// <summary>Procesa un mensaje del usuario: loop modelo → tools/call → modelo, hasta la respuesta final.</summary>
    public async Task Enviar(string texto)
    {
        texto = texto.Trim();
        if (Ocupado || disposed || texto.Length == 0) return;
        if (Cliente is null) await NuevaConversacion();
        if (Cliente is null) return;

        Ocupado = true;
        relojTurno.Restart();
        turno = new CancellationTokenSource();
        var ct = turno.Token;
        lock (gate) chat.Add(new MensajeChat("user", texto));
        historial.Add(new JsonObject { ["role"] = "user", ["content"] = texto });
        var corrTurno = $"turno:{Guid.NewGuid():N}";
        Traza(Actor.Usuario, Actor.Agente, TipoTraza.Request, $"💬 {Recortar(texto, 60)}", corrTurno, detalle: texto);
        Avisar();

        IModeloChat modelo = Modelo is null
            ? new ModeloGuion()
            : new ChatOllama(fabrica.CreateClient("ollama"), Modelo);
        string? respuestaFinal = null;
        var error = false;
        try
        {
            for (var i = 1; i <= MaxIteraciones; i++)
            {
                iteracion++;
                var mensajes = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = Sistema() });
                foreach (var m in historial) mensajes.Add(m!.DeepClone());
                var pedido = modelo.ArmarPedido(mensajes, toolsOllama);

                var pasoLlm = AgregarPaso(new PasoAgente
                {
                    Iteracion = iteracion, Tipo = TipoPaso.Llm, Titulo = modelo.Nombre,
                    Subtitulo = $"{mensajes.Count} mensajes · {toolsOllama.Count} tools disponibles",
                    Entrada = pedido.ToJsonString(BusTraza.Lindo)
                });
                var corrLlm = $"llm:{Guid.NewGuid():N}";
                Traza(Actor.Agente, Actor.Llm, TipoTraza.Request, $"POST /api/chat · iteración {iteracion}", corrLlm, detalle: pasoLlm.Entrada);

                RespuestaModelo r;
                try { r = await modelo.Responder(pedido, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    pasoLlm.Salida = e.Message; pasoLlm.Terminar(false);
                    Traza(Actor.Llm, Actor.Agente, TipoTraza.Response, "✖ error del modelo", corrLlm, error: true, detalle: e.Message);
                    throw;
                }
                UltimoUso = r.Uso;
                if (r.Uso is { } uso)
                {
                    UsoAcumulado = UsoAcumulado?.Sumar(uso) ?? uso;
                    RespuestasMedidas++;
                }
                else if (Modelo is not null) RespuestasSinMetricas++;
                var decision = r.Llamadas.Count > 0
                    ? $"tool_calls: {string.Join(", ", r.Llamadas.Select(l => l.Nombre))}"
                    : "respuesta final (sin tools)";
                pasoLlm.Subtitulo = decision + (r.Metricas is { } met && met != "guion" ? $" · {met}" : "");
                pasoLlm.Salida = r.MensajeAsistente.ToJsonString(BusTraza.Lindo);
                pasoLlm.Terminar(true);
                Traza(Actor.Llm, Actor.Agente, TipoTraza.Response, decision, corrLlm, detalle: pasoLlm.Salida);

                historial.Add(r.MensajeAsistente);
                if (r.Texto.Length > 0)
                {
                    lock (gate) chat.Add(new MensajeChat("assistant", r.Texto));
                    respuestaFinal = r.Texto;
                }
                Avisar();
                if (r.Llamadas.Count == 0) return;

                // El modelo pidió tools: el host las ejecuta por MCP (en paralelo si son varias y está activado).
                string[] resultados;
                if (ToolsEnParalelo && r.Llamadas.Count > 1)
                    resultados = await Task.WhenAll(r.Llamadas.Select(l => EjecutarTool(l, ct)));
                else
                {
                    resultados = new string[r.Llamadas.Count];
                    for (var k = 0; k < r.Llamadas.Count; k++) resultados[k] = await EjecutarTool(r.Llamadas[k], ct);
                }
                for (var k = 0; k < r.Llamadas.Count; k++)
                    historial.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = r.Llamadas[k].Nombre, ["content"] = resultados[k] });
            }
            error = true;
            respuestaFinal = $"Se alcanzó el límite de {MaxIteraciones} iteraciones del loop agéntico.";
            lock (gate) chat.Add(new MensajeChat("error", respuestaFinal));
        }
        catch (OperationCanceledException)
        {
            error = true; respuestaFinal = "Turno detenido por el usuario.";
            lock (gate) chat.Add(new MensajeChat("error", respuestaFinal));
        }
        catch (Exception e)
        {
            error = true; respuestaFinal = $"No se pudo completar el turno: {e.Message}";
            lock (gate) chat.Add(new MensajeChat("error", respuestaFinal));
        }
        finally
        {
            relojTurno.Stop();
            ejecucionAcumulada += relojTurno.Elapsed;
            Traza(Actor.Agente, Actor.Usuario, TipoTraza.Response, error ? "✖ turno sin respuesta" : $"🤖 {Recortar(respuestaFinal ?? "", 60)}",
                  corrTurno, error: error, detalle: respuestaFinal);
            Ocupado = false;
            turno?.Dispose(); turno = null;
            Avisar();
        }
    }

    async Task<string> EjecutarTool(LlamadaTool llamada, CancellationToken ct)
    {
        var paso = AgregarPaso(new PasoAgente
        {
            Iteracion = iteracion, Tipo = TipoPaso.Tool, Titulo = llamada.Nombre,
            Subtitulo = "tools/call por MCP", Entrada = llamada.Argumentos.ToJsonString(BusTraza.Lindo)
        });
        var corr = $"tool:{Guid.NewGuid():N}";
        Traza(Actor.Agente, Actor.ClienteMcp, TipoTraza.Request, $"CallToolAsync(\"{llamada.Nombre}\")", corr, detalle: paso.Entrada);

        var progreso = new ProgresoInmediato(v =>
        {
            paso.Progreso = v.Message ?? $"paso {v.Progress}";
            lock (gate) paso.Avisos.Add(paso.Progreso);
            Avisar();
        });
        string texto;
        bool esError;
        try
        {
            var args = llamada.Argumentos.ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.SerializeToElement(kv.Value));
            var r = await Cliente!.CallToolAsync(llamada.Nombre, args, progreso, cancellationToken: ct);
            texto = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text));
            esError = r.IsError == true;
        }
        catch (McpException e)
        {
            texto = new JsonObject { ["codigo"] = "MCP_ERROR", ["mensaje"] = e.Message }.ToJsonString();
            esError = true;
        }
        paso.Salida = BusTraza.Indentar(texto);
        paso.Subtitulo = esError ? "CallToolResult · isError: true" : "CallToolResult · ok";
        paso.Progreso = null;
        paso.Terminar(!esError);
        Traza(Actor.ClienteMcp, Actor.Agente, TipoTraza.Response, esError ? $"⚠ {llamada.Nombre}: isError" : $"✓ {llamada.Nombre}",
              corr, error: esError, detalle: paso.Salida);
        Avisar();
        return texto;
    }

    async ValueTask<ElicitResult> ManejarElicitacion(ElicitRequestParams? pedido, CancellationToken ct)
    {
        var pendiente = new ElicitacionPendiente(pedido?.Message ?? "El servidor pide confirmación.", pedido?.RequestedSchema);
        var paso = AgregarPaso(new PasoAgente
        {
            Iteracion = iteracion, Tipo = TipoPaso.Elicitacion, Titulo = "elicitation/create",
            Subtitulo = "El servidor MCP le pregunta al usuario", Entrada = pendiente.Mensaje
        });
        var corr = $"elicit:{Guid.NewGuid():N}";
        Traza(Actor.ClienteMcp, Actor.Usuario, TipoTraza.Request, $"🙋 {Recortar(pendiente.Mensaje, 55)}", corr, detalle: pendiente.Mensaje);
        lock (gate) elicitaciones.Add(pendiente);
        Avisar();

        using var registro = ct.Register(() => pendiente.Respuesta.TrySetResult(new ElicitResult { Action = "cancel" }));
        var resultado = await pendiente.Respuesta.Task;
        lock (gate) elicitaciones.Remove(pendiente);

        paso.Salida = resultado.Action + (resultado.Content is { Count: > 0 } c
            ? "\n" + JsonSerializer.Serialize(c, BusTraza.Lindo) : "");
        paso.Terminar(resultado.IsAccepted);
        Traza(Actor.Usuario, Actor.ClienteMcp, TipoTraza.Response, resultado.IsAccepted ? "✔ accept" : $"✖ {resultado.Action}",
              corr, error: !resultado.IsAccepted, detalle: paso.Salida);
        Avisar();
        return resultado;
    }

    /// <summary>Responde la elicitation visible: accept (con los valores del formulario), decline o cancel.</summary>
    public void ResolverElicitacion(string accion)
    {
        var e = Elicitacion;
        if (e is null) return;
        Dictionary<string, JsonElement>? contenido = null;
        if (accion == "accept" && e.Schema?.Properties is { } props)
        {
            contenido = [];
            foreach (var (nombre, def) in props)
            {
                var valor = e.Valores.GetValueOrDefault(nombre);
                switch (def)
                {
                    case ElicitRequestParams.BooleanSchema:
                        contenido[nombre] = JsonSerializer.SerializeToElement(valor == "true");
                        break;
                    case ElicitRequestParams.NumberSchema when double.TryParse(valor, CultureInfo.InvariantCulture, out var num):
                        contenido[nombre] = JsonSerializer.SerializeToElement(num);
                        break;
                    default:
                        if (!string.IsNullOrWhiteSpace(valor)) contenido[nombre] = JsonSerializer.SerializeToElement(valor);
                        break;
                }
            }
        }
        e.Respuesta.TrySetResult(new ElicitResult { Action = accion, Content = contenido });
    }

    /// <summary>Cancela el turno en curso (el SDK envía notifications/cancelled al servidor).</summary>
    public void Detener()
    {
        turno?.Cancel();
        lock (gate) foreach (var e in elicitaciones) e.Respuesta.TrySetResult(new ElicitResult { Action = "cancel" });
    }

    string Sistema()
    {
        var hoy = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        var es = CultureInfo.GetCultureInfo("es-AR");
        // Los modelos chicos se equivocan con "el sábado" o "mañana": les damos el calendario resuelto.
        var calendario = string.Join(", ", Enumerable.Range(0, 8).Select(d => hoy.AddDays(d))
            .Select((d, i) => $"{d.ToString("dddd", es)}{(i == 0 ? " (hoy)" : i == 1 ? " (mañana)" : "")} = {d:yyyy-MM-dd}"));
        return $"""
            Sos el asistente de reservas de MesaYa (restaurantes en Buenos Aires).
            Hoy es {hoy.ToString("dddd dd/MM/yyyy", es)} y la zona horaria es -03:00.
            Próximos días: {calendario}.
            Interpretá las fechas relativas ("el sábado", "mañana") con ese calendario, sin preguntarle al usuario.
            Tenés tools de un servidor MCP para listar sucursales, consultar el menú, buscar disponibilidad, reservar y cancelar.
            No inventes datos: si necesitás información, llamá una tool. Si varias consultas son independientes, pedilas juntas en la misma respuesta.
            Para reservar necesitás sucursal, fecha, hora, personas y celular (+549 y 10 dígitos). El servidor MCP le pide confirmación al usuario: no la pidas vos.
            Si una tool devuelve un error, leé el código y el mensaje y corregí los argumentos o explicale al usuario qué pasó.
            Respondé breve, en español rioplatense.

            Instrucciones del servidor MCP:
            {Cliente?.ServerInstructions}
            """;
    }

    PasoAgente AgregarPaso(PasoAgente paso)
    {
        lock (gate) pasos.Add(paso);
        Avisar();
        return paso;
    }

    void Log(string texto)
    {
        lock (gate)
        {
            logs.Add($"{DateTime.Now:HH:mm:ss.fff}  {texto}");
            if (logs.Count > 200) logs.RemoveAt(0);
        }
        Avisar();
    }

    void Traza(Actor desde, Actor hacia, TipoTraza tipo, string etiqueta, string? correlacion = null, bool error = false, string? detalle = null) =>
        bus.Publicar(new EventoTraza
        {
            Conversacion = ConversacionId, Desde = desde, Hacia = hacia, Tipo = tipo, Etiqueta = etiqueta,
            Correlacion = correlacion, Error = error, Detalle = detalle
        });

    void Avisar() { if (!disposed) Cambio?.Invoke(); }

    static string Recortar(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        Detener();
        Cambio = null;
        if (Cliente is not null) await Cliente.DisposeAsync();
    }

    /// <summary>IProgress que invoca al instante (Progress&lt;T&gt; publicaría en el contexto de sincronización).</summary>
    sealed class ProgresoInmediato(Action<ProgressNotificationValue> accion) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => accion(value);
    }
}
