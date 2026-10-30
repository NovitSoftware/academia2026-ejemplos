using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MesaYa.Agent;

public static class Json
{
    // Sin escapar tildes ni '+', para que el JSON se lea tal cual en la demo.
    public static readonly JsonSerializerOptions Compacto = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Lindo = new(Compacto) { WriteIndented = true };
}

/// <summary>
/// Lo que haría el servidor MCP: traduce una tool a una llamada HTTP a la API real,
/// con token, timeout, reintentos e Idempotency-Key.
/// </summary>
/// <param name="baseUrl">Dirección base de la API de reservas.</param>
/// <param name="token">Token de servicio usado para autenticar las escrituras.</param>
/// <param name="registrarHttp">Receptor de peticiones, respuestas y timeouts HTTP.</param>
/// <param name="registrarHost">Receptor de diagnósticos del host y reintentos.</param>
public class MesaYaClient(string baseUrl, string token, Action<string> registrarHttp, Action<string> registrarHost) : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    const int MaxReintentos = 2;
    readonly HttpClient http = new() { BaseAddress = new Uri(baseUrl), Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public Task<(bool ok, JsonNode cuerpo)> BuscarDisponibilidad(JsonObject a) =>
        Enviar(() => new HttpRequestMessage(HttpMethod.Get,
            $"/v1/disponibilidad?sucursal_id={a["sucursal_id"]}&fecha={a["fecha"]}"));

    public Task<(bool ok, JsonNode cuerpo)> Reservar(JsonObject a, string conversacionId)
    {
        // La clave se deriva de la conversación + los argumentos: si el modelo o el runtime
        // repiten la misma llamada, la API devuelve la misma reserva en vez de crear otra.
        var clave = ClaveIdempotencia(conversacionId, a);
        return Enviar(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/reservas")
            {
                Content = new StringContent(a.ToJsonString(Json.Compacto), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Idempotency-Key", clave);
            return req;
        });
    }

    async Task<(bool ok, JsonNode cuerpo)> Enviar(Func<HttpRequestMessage> crear)
    {
        for (int intento = 0; ; intento++)
        {
            using var req = crear();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            registrarHttp($"→ {req.Method} {req.RequestUri}" +
                         (req.Headers.TryGetValues("Idempotency-Key", out var k) ? $"  Idempotency-Key: {k.First()[..8]}…" : "") +
                         (intento > 0 ? $"  (reintento {intento})" : ""));
            if (req.Content is not null) registrarHttp("  " + await req.Content.ReadAsStringAsync());

            bool transitorio; string motivo;
            try
            {
                using var cts = new CancellationTokenSource(Timeout);
                using var resp = await http.SendAsync(req, cts.Token);
                var texto = await resp.Content.ReadAsStringAsync();
                var replay = resp.Headers.Contains("Idempotent-Replayed") ? "  (Idempotent-Replayed)" : "";
                registrarHttp($"← {(int)resp.StatusCode} {resp.StatusCode}{replay}");
                registrarHttp("  " + texto);
                var cuerpo = JsonNode.Parse(texto)!;

                if (resp.IsSuccessStatusCode) return (true, cuerpo);
                transitorio = resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
                if (!transitorio) return (false, ErrorDeApi(cuerpo));
                motivo = cuerpo["codigo"]?.ToString() ?? "503";
            }
            catch (OperationCanceledException)
            {
                transitorio = true; motivo = "TIMEOUT";
                registrarHttp($"✖ sin respuesta en {Timeout.TotalSeconds} s");
            }
            catch (HttpRequestException e)
            {
                return (false, Error("API_INACCESIBLE", $"No se pudo conectar con MesaYa ({e.Message}). ¿Está corriendo MesaYa.Api?", false));
            }

            if (intento >= MaxReintentos)
                return (false, Error(motivo, "MesaYa no respondió después de los reintentos. Avisale al usuario que pruebe más tarde.", true));
            var espera = TimeSpan.FromMilliseconds(500 * Math.Pow(2, intento)); // backoff exponencial
            registrarHost($"Error transitorio ({motivo}). Reintento en {espera.TotalMilliseconds} ms con la MISMA Idempotency-Key.");
            await Task.Delay(espera);
        }
    }

    static JsonNode ErrorDeApi(JsonNode c) => new JsonObject
    {
        ["codigo"] = c["codigo"]?.DeepClone(),
        ["mensaje"] = c["mensaje"]?.DeepClone(),
        ["reintentable"] = false,
        ["detalle"] = c["detalle"]?.DeepClone()
    };

    static JsonNode Error(string codigo, string mensaje, bool reintentable) =>
        JsonSerializer.SerializeToNode(new { codigo, mensaje, reintentable })!;

    /// <summary>Libera la conexión HTTP del cliente.</summary>
    public void Dispose() => http.Dispose();

    static string ClaveIdempotencia(string conversacionId, JsonObject a)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(conversacionId + a.ToJsonString(Json.Compacto)));
        return new Guid(hash[..16]).ToString();
    }
}
