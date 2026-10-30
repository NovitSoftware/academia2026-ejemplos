using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesaYa.Mcp.Traza;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MesaYa.Mcp.Servidor;

/// <summary>Resultado de una llamada a MesaYa.Api ya interpretado para la tool.</summary>
public sealed record ResultadoApi(bool Ok, int Status, JsonNode Cuerpo);

/// <summary>
/// Contexto de una invocación de tool: sabe a qué sesión MCP pertenece y cómo avisarle
/// al cliente (notifications/progress y notifications/message) mientras la tool trabaja.
/// </summary>
public sealed class ContextoTool(McpServer servidor, IProgress<ProgressNotificationValue> progreso)
{
    int paso;
    public string? Sesion => servidor.SessionId;
    public McpServer Servidor => servidor;

    /// <summary>Envía un aviso de progreso al cliente MCP (si pidió progressToken).</summary>
    public void Progreso(string mensaje) =>
        progreso.Report(new ProgressNotificationValue { Progress = ++paso, Message = mensaje });

    /// <summary>Envía un log MCP al cliente (notifications/message).</summary>
    public async Task Log(string texto, LoggingLevel nivel = LoggingLevel.Info)
    {
        try
        {
            await servidor.SendNotificationAsync(NotificationMethods.LoggingMessageNotification,
                new LoggingMessageNotificationParams
                {
                    Level = nivel,
                    Logger = "mesaya-mcp",
                    Data = JsonSerializer.SerializeToElement(texto)
                });
        }
        catch (Exception) { /* el cliente pudo haberse desconectado; el log no es crítico */ }
    }
}

/// <summary>
/// El "adaptador" del servidor MCP hacia la API REST real: token, timeout, reintentos con backoff,
/// Idempotency-Key y los escenarios de caos. Publica cada llamada HTTP en la traza.
/// </summary>
public sealed class ApiMesaYa(HttpClient http, BusTraza traza, Escenarios escenarios, IConfiguration configuracion)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    const int MaxReintentos = 2;
    readonly string token = configuracion["MesaYa:Token"] ?? "demo-agente-token";

    public Task<ResultadoApi> Get(string ruta, ContextoTool ctx, CancellationToken ct) =>
        Enviar(() => new HttpRequestMessage(HttpMethod.Get, ruta), ctx, ct);

    public Task<ResultadoApi> Delete(string ruta, ContextoTool ctx, CancellationToken ct) =>
        Enviar(() => new HttpRequestMessage(HttpMethod.Delete, ruta), ctx, ct);

    public Task<ResultadoApi> Post(string ruta, JsonObject cuerpo, string idempotencyKey, ContextoTool ctx, CancellationToken ct) =>
        Enviar(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, ruta)
            {
                Content = new StringContent(cuerpo.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Idempotency-Key", idempotencyKey);
            return req;
        }, ctx, ct);

    async Task<ResultadoApi> Enviar(Func<HttpRequestMessage> crear, ContextoTool ctx, CancellationToken ct)
    {
        for (var intento = 0; ; intento++)
        {
            await AplicarLatencia(ctx, ct);

            using var req = crear();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var cuerpoReq = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            var correlacion = $"http:{Guid.NewGuid():N}";
            var reintento = intento > 0 ? $"  (reintento {intento})" : "";
            traza.Publicar(new EventoTraza
            {
                SesionMcp = ctx.Sesion, Desde = Actor.ServidorMcp, Hacia = Actor.Api, Tipo = TipoTraza.Request,
                Etiqueta = $"{req.Method} {req.RequestUri}{reintento}", Correlacion = correlacion,
                Detalle = $"{req.Method} {http.BaseAddress}{req.RequestUri?.ToString().TrimStart('/')}\n" +
                          string.Join("\n", req.Headers.Select(h => $"{h.Key}: {(h.Key == "Authorization" ? "Bearer ***" : string.Join(",", h.Value))}")) +
                          (cuerpoReq is null ? "" : "\n\n" + BusTraza.Indentar(cuerpoReq))
            });

            string motivo;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Timeout);
                using var resp = await http.SendAsync(req, cts.Token);
                var texto = await resp.Content.ReadAsStringAsync(ct);
                var replay = resp.Headers.Contains("Idempotent-Replayed") ? " · Idempotent-Replayed" : "";
                traza.Publicar(new EventoTraza
                {
                    SesionMcp = ctx.Sesion, Desde = Actor.Api, Hacia = Actor.ServidorMcp, Tipo = TipoTraza.Response,
                    Etiqueta = $"{(int)resp.StatusCode} {resp.StatusCode}{replay}", Correlacion = correlacion,
                    Error = !resp.IsSuccessStatusCode, Detalle = BusTraza.Indentar(texto)
                });
                var cuerpo = Parsear(texto);
                if (!resp.IsSuccessStatusCode && cuerpo is JsonObject sinCodigo && sinCodigo["codigo"] is null)
                {
                    sinCodigo["codigo"] = $"HTTP_{(int)resp.StatusCode}";
                    sinCodigo["mensaje"] = $"MesaYa.Api respondió {(int)resp.StatusCode} {resp.ReasonPhrase} sin detalle.";
                }
                if (resp.IsSuccessStatusCode) return new ResultadoApi(true, (int)resp.StatusCode, cuerpo);
                if (resp.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
                    return new ResultadoApi(false, (int)resp.StatusCode, cuerpo);
                motivo = $"{(int)resp.StatusCode} {cuerpo["codigo"]}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                motivo = "TIMEOUT";
                traza.Publicar(new EventoTraza
                {
                    SesionMcp = ctx.Sesion, Desde = Actor.Api, Hacia = Actor.ServidorMcp, Tipo = TipoTraza.Response,
                    Etiqueta = $"✖ sin respuesta en {Timeout.TotalSeconds} s", Correlacion = correlacion, Error = true
                });
            }
            catch (HttpRequestException e)
            {
                traza.Publicar(new EventoTraza
                {
                    SesionMcp = ctx.Sesion, Desde = Actor.Api, Hacia = Actor.ServidorMcp, Tipo = TipoTraza.Response,
                    Etiqueta = "✖ API inaccesible", Correlacion = correlacion, Error = true, Detalle = e.Message
                });
                return new ResultadoApi(false, 0, new JsonObject
                {
                    ["codigo"] = "API_INACCESIBLE",
                    ["mensaje"] = $"No se pudo conectar con MesaYa.Api ({e.Message}). ¿Está corriendo en {http.BaseAddress}?"
                });
            }

            if (intento >= MaxReintentos)
                return new ResultadoApi(false, 503, new JsonObject
                {
                    ["codigo"] = "NO_DISPONIBLE",
                    ["mensaje"] = "MesaYa no respondió después de los reintentos. Avisale al usuario que pruebe más tarde.",
                    ["reintentable"] = true
                });

            var espera = TimeSpan.FromMilliseconds(500 * Math.Pow(2, intento));
            var aviso = $"Error transitorio ({motivo}). Reintento {intento + 1} en {espera.TotalMilliseconds} ms con la misma Idempotency-Key.";
            ctx.Progreso(aviso);
            await ctx.Log(aviso, LoggingLevel.Warning);
            await Espera(ctx, espera, "backoff antes de reintentar", ct);
        }
    }

    static JsonNode Parsear(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return new JsonObject();
        try { return JsonNode.Parse(texto) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject { ["texto"] = texto }; }
    }

    async Task AplicarLatencia(ContextoTool ctx, CancellationToken ct)
    {
        var demora = escenarios.DemoraSimulada();
        if (demora <= TimeSpan.Zero) return;
        ctx.Progreso($"Red lenta: esperando {demora.TotalSeconds:0.0} s…");
        await Espera(ctx, demora, $"latencia simulada ({escenarios.Actual})", ct);
    }

    /// <summary>Espera visible: abre un tramo en el servidor MCP y manda progreso cada medio segundo.</summary>
    async Task Espera(ContextoTool ctx, TimeSpan demora, string motivo, CancellationToken ct)
    {
        var correlacion = $"espera:{Guid.NewGuid():N}";
        traza.Publicar(new EventoTraza
        {
            SesionMcp = ctx.Sesion, Desde = Actor.ServidorMcp, Hacia = Actor.ServidorMcp, Tipo = TipoTraza.Request,
            Etiqueta = $"⏳ {motivo} · {demora.TotalSeconds:0.0} s", Correlacion = correlacion
        });
        // Espera en tramos de 700 ms con un aviso por tramo. Task.Delay con menos de 1 ms vuelve al instante,
        // así que el último resto se espera de una vez y sin avisar (si no, se vuelve un bucle de notificaciones).
        var fin = DateTimeOffset.Now + demora;
        var tramo = TimeSpan.FromMilliseconds(700);
        for (var resta = demora; resta > TimeSpan.Zero; resta = fin - DateTimeOffset.Now)
        {
            if (resta <= tramo) { await Task.Delay(resta, ct); break; }
            await Task.Delay(tramo, ct);
            ctx.Progreso($"⏳ {motivo}: faltan {(fin - DateTimeOffset.Now).TotalSeconds:0.0} s");
        }
        traza.Publicar(new EventoTraza
        {
            SesionMcp = ctx.Sesion, Desde = Actor.ServidorMcp, Hacia = Actor.ServidorMcp, Tipo = TipoTraza.Response,
            Etiqueta = "fin de la espera", Correlacion = correlacion
        });
    }
}
