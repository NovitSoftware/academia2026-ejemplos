using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MesaYa.Contexto.Api;

/// <summary>Respuesta de la API: código HTTP y cuerpo JSON (también en los errores).</summary>
public sealed record RespuestaApi(int Status, JsonNode? Cuerpo)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Cliente HTTP de MesaYa.Api (Clase 3). No sabe nada del modelo ni del contexto.</summary>
public sealed class ApiMesaYa
{
    readonly HttpClient http;
    readonly string token;

    public ApiMesaYa(HttpClient http, IConfiguration config)
    {
        this.http = http;
        token = config["MesaYa:Token"] ?? "demo-agente-token";
    }

    public Task<RespuestaApi> Menu(string sucursalId) => Enviar(HttpMethod.Get, $"/v1/menu?sucursal_id={Uri.EscapeDataString(sucursalId)}");
    public Task<RespuestaApi> Disponibilidad(string sucursalId, string fecha) =>
        Enviar(HttpMethod.Get, $"/v1/disponibilidad?sucursal_id={Uri.EscapeDataString(sucursalId)}&fecha={Uri.EscapeDataString(fecha)}");
    public Task<RespuestaApi> Politicas() => Enviar(HttpMethod.Get, "/v1/politicas");
    public Task<RespuestaApi> Resenas(string? sucursalId) =>
        Enviar(HttpMethod.Get, sucursalId is null ? "/v1/resenas" : $"/v1/resenas?sucursal_id={Uri.EscapeDataString(sucursalId)}");
    public Task<RespuestaApi> Reservas(string? telefono) =>
        Enviar(HttpMethod.Get, telefono is null ? "/v1/reservas" : $"/v1/reservas?telefono={Uri.EscapeDataString(telefono)}", autenticar: true);
    public Task<RespuestaApi> Cancelar(string reservaId) =>
        Enviar(HttpMethod.Delete, $"/v1/reservas/{Uri.EscapeDataString(reservaId)}", autenticar: true);

    public Task<RespuestaApi> Reservar(JsonObject cuerpo, string claveIdempotencia) =>
        Enviar(HttpMethod.Post, "/v1/reservas", autenticar: true, cuerpo, claveIdempotencia);

    /// <summary>Vuelve las reservas al estado inicial antes de cada escenario.</summary>
    public Task<RespuestaApi> Reiniciar() => Enviar(HttpMethod.Post, "/v1/demo/reset");

    public async Task<bool> Disponible()
    {
        try { return (await http.GetAsync("/health")).IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
    }

    async Task<RespuestaApi> Enviar(HttpMethod metodo, string ruta, bool autenticar = false, JsonObject? cuerpo = null, string? clave = null)
    {
        using var pedido = new HttpRequestMessage(metodo, ruta);
        if (autenticar) pedido.Headers.Authorization = new("Bearer", token);
        if (clave is not null) pedido.Headers.Add("Idempotency-Key", clave);
        if (cuerpo is not null) pedido.Content = JsonContent.Create(cuerpo);
        using var resp = await http.SendAsync(pedido);
        var texto = await resp.Content.ReadAsStringAsync();
        return new RespuestaApi((int)resp.StatusCode, string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto));
    }
}
