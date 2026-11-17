using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace MesaYa.Comun;

/// <summary>Un proveedor con API compatible con OpenAI: Ollama local o la nube de NVIDIA.</summary>
public sealed class Proveedor
{
    public required string Id { get; init; }
    public required string Nombre { get; init; }
    /// <summary>URL base, con el /v1 incluido.</summary>
    public required string Url { get; init; }
    public bool Local { get; init; }
    public string? Clave { get; init; }
    public string OrigenClave { get; init; } = "";
    public bool TieneClave => Local || !string.IsNullOrEmpty(Clave);

    /// <summary>Lo que se muestra en la interfaz: nunca el valor de la clave.</summary>
    public Dict Publico() => new()
    {
        ["id"] = Id, ["nombre"] = Nombre, ["url"] = Url, ["local"] = Local,
        ["tiene_clave"] = TieneClave, ["origen_clave"] = OrigenClave,
    };
}

/// <summary>El proveedor respondió con error (o lo simuló el limitador). Trae el pedido para mostrarlo igual.</summary>
public sealed class ErrorProveedor(int estado, string detalle, Dict resultado, double? reintentarEn = null) : Exception(detalle)
{
    public int Estado { get; } = estado;
    public Dict Resultado { get; } = resultado;
    /// <summary>Segundos del encabezado Retry-After, si vino.</summary>
    public double? ReintentarEn { get; } = reintentarEn;
}

/// <summary>Un pedido de chat, igual para cualquier proveedor.</summary>
public sealed class Pedido
{
    public required Proveedor Proveedor { get; init; }
    public required string Modelo { get; init; }
    public required List<Dict> Mensajes { get; init; }
    public int MaxTokens { get; init; } = 400;
    public double Temperatura { get; init; } = 0;
    /// <summary>JSON Schema de la respuesta (structured output).</summary>
    public Dict? Esquema { get; init; }
    public List<Dict>? Tools { get; init; }
    /// <summary>Streaming para medir el time-to-first-token. Con tools se pide la respuesta completa.</summary>
    public bool Stream { get; init; }
    public int TimeoutS { get; init; } = 120;
}

/// <summary>
/// Límite simulado de requests y tokens por ventana. En clase no se puede provocar un 429 real a pedido:
/// esto responde 429 con Retry-After como lo haría el proveedor, sin llegar a llamarlo.
/// </summary>
public sealed class Limitador(int requests, int tokens, int ventanaS)
{
    public int Requests { get; set; } = requests;
    public int Tokens { get; set; } = tokens;
    public int VentanaS { get; set; } = ventanaS;
    readonly List<(DateTime t, long tokens)> uso = [];

    void Limpiar() => uso.RemoveAll(u => (DateTime.UtcNow - u.t).TotalSeconds >= VentanaS);

    /// <summary>
    /// Null si pasa, y en ese caso reserva los tokens estimados (como los proveedores, que cuentan max_tokens al recibir el pedido).
    /// Si no pasa, qué límite se superó y en cuántos segundos se libera.
    /// </summary>
    public (string limite, double espera)? Reservar(long estimado)
    {
        lock (uso)
        {
            Limpiar();
            double Libera(Func<int, bool> alcanza)
            {
                // Espera hasta que salgan de la ventana suficientes registros viejos.
                for (var i = 0; i < uso.Count; i++)
                    if (alcanza(i + 1)) return Math.Max(0.5, VentanaS - (DateTime.UtcNow - uso[i].t).TotalSeconds);
                return VentanaS;
            }
            if (uso.Count >= Requests)
                return ("requests", Libera(n => uso.Count - n < Requests));
            var usados = uso.Sum(u => u.tokens);
            if (usados + estimado > Tokens)
                return ("tokens", Libera(n => usados - uso.Take(n).Sum(u => u.tokens) + estimado <= Tokens));
            uso.Add((DateTime.UtcNow, estimado));
            return null;
        }
    }

    public void Vaciar() { lock (uso) uso.Clear(); }

    public Dict Estado()
    {
        lock (uso)
        {
            Limpiar();
            return new Dict
            {
                ["requests"] = (long)uso.Count, ["tokens"] = uso.Sum(u => u.tokens),
                ["limite_requests"] = (long)Requests, ["limite_tokens"] = (long)Tokens, ["ventana_s"] = (long)VentanaS,
            };
        }
    }
}

/// <summary>
/// Cliente único para todos los proveedores: el mismo endpoint /chat/completions, cambian la URL, la clave y el modelo.
/// Lo que no es portable entre proveedores (structured output, razonamiento) se resuelve acá, en un solo lugar.
///
/// Cada llamada que termina bien se guarda en grabaciones/llm/. Con el modo guion prendido,
/// la misma llamada se responde desde esa grabación sin salir a la red.
/// </summary>
public sealed class Cliente
{
    public static readonly Cliente Instancia = new();

    public Dictionary<string, Proveedor> Proveedores { get; } = [];
    /// <summary>Límites simulados por proveedor y modelo (los proveedores limitan por modelo y por clave).</summary>
    public Dictionary<string, Limitador> Limites { get; } = [];
    public static string Llave(string proveedor, string modelo) => $"{proveedor}|{modelo}";
    public bool LimiteSimulado { get; set; } = true;
    public bool ModoGuion { get; set; }

    readonly HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    readonly string grabaciones = Path.Combine(Environment.GetEnvironmentVariable("GRABACIONES") ?? "grabaciones", "llm");

    /// <summary>
    /// Lee los proveedores de la configuración. La clave de NVIDIA sale primero de la variable de entorno
    /// NVIDIA_API_KEY y, si no está, de Proveedores:nvidia:ApiKey (appsettings.json o appsettings.Local.json).
    /// </summary>
    public void Configurar(IConfiguration config)
    {
        var ollama = Environment.GetEnvironmentVariable("OLLAMA_URL") is { Length: > 0 } u
            ? u.TrimEnd('/') + "/v1" : config["Proveedores:ollama:Url"] ?? "http://localhost:11434/v1";
        Proveedores["ollama"] = new Proveedor { Id = "ollama", Nombre = "Ollama local", Url = ollama.TrimEnd('/'), Local = true, OrigenClave = "no hace falta" };

        var entorno = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
        var archivo = config["Proveedores:nvidia:ApiKey"];
        if (archivo == "nvapi-...") archivo = null;  // el valor de appsettings.Local.ejemplo.json no es una clave
        var (clave, origen) = !string.IsNullOrWhiteSpace(entorno) ? (entorno.Trim(), "variable de entorno NVIDIA_API_KEY")
            : !string.IsNullOrWhiteSpace(archivo) ? (archivo.Trim(), "appsettings (Proveedores:nvidia:ApiKey)")
            : (null, "sin clave");
        Proveedores["nvidia"] = new Proveedor
        {
            Id = "nvidia", Nombre = "NVIDIA", Url = (config["Proveedores:nvidia:Url"] ?? "https://integrate.api.nvidia.com/v1").TrimEnd('/'),
            Clave = clave, OrigenClave = origen,
        };
        Directory.CreateDirectory(grabaciones);
    }

    public Proveedor Proveedor(string id) =>
        Proveedores.TryGetValue(id, out var p) ? p : throw new ErrorHttp(400, $"Proveedor desconocido: {id}");

    // ---------- cuerpo del request ----------
    static Dict Cuerpo(Pedido p, bool conEstructura)
    {
        var cuerpo = new Dict
        {
            ["model"] = p.Modelo, ["messages"] = p.Mensajes, ["max_tokens"] = p.MaxTokens, ["temperature"] = p.Temperatura,
        };
        var stream = p.Stream && p.Tools == null;
        if (stream)
        {
            cuerpo["stream"] = true;
            cuerpo["stream_options"] = new Dict { ["include_usage"] = true };
        }
        cuerpo["reasoning_effort"] = "none";  // qwen3.5 y Nemotron razonan por defecto: acá no hace falta
        if (p.Tools != null) cuerpo["tools"] = p.Tools;
        if (p.Esquema != null && conEstructura)
            // El formato de OpenAI: hoy lo aceptan Ollama y NVIDIA. Si un proveedor lo rechaza, Completar pide el JSON en el prompt.
            cuerpo["response_format"] = new Dict
            {
                ["type"] = "json_schema",
                ["json_schema"] = new Dict { ["name"] = "respuesta", ["strict"] = true, ["schema"] = p.Esquema },
            };
        return cuerpo;
    }

    /// <summary>Si el proveedor no acepta el esquema, se pide el JSON en el prompt (sin garantía).</summary>
    static Pedido SinEstructura(Pedido p)
    {
        var mensajes = p.Mensajes.Select(m => new Dict(m)).ToList();
        var pista = "Respondé solo con un objeto JSON que cumpla este esquema, sin texto alrededor: " + J.Serializar(p.Esquema);
        if (mensajes.Count > 0 && mensajes[0].Str("role") == "system") mensajes[0]["content"] = mensajes[0].Str("content") + "\n\n" + pista;
        else mensajes.Insert(0, new Dict { ["role"] = "system", ["content"] = pista });
        return new Pedido
        {
            Proveedor = p.Proveedor, Modelo = p.Modelo, Mensajes = mensajes, MaxTokens = p.MaxTokens, Temperatura = p.Temperatura,
            Esquema = p.Esquema, Tools = p.Tools, Stream = p.Stream, TimeoutS = p.TimeoutS,
        };
    }

    /// <summary>El request tal como sale, para mostrarlo: la clave tapada y las imágenes resumidas.</summary>
    static Dict Mostrable(Proveedor prov, Dict cuerpo)
    {
        var encabezados = new Dict { ["Content-Type"] = "application/json" };
        if (!prov.Local) encabezados["Authorization"] = prov.TieneClave ? "Bearer ••••••••" : "(falta la clave)";
        return new Dict
        {
            ["metodo"] = "POST", ["url"] = prov.Url + "/chat/completions", ["base"] = prov.Url, ["ruta"] = "/chat/completions",
            ["encabezados"] = encabezados, ["origen_clave"] = prov.OrigenClave,
            ["cuerpo"] = J.Leer(Regex.Replace(J.Serializar(cuerpo), "data:image/(\\w+);base64,[A-Za-z0-9+/=]+",
                m => $"data:image/{m.Groups[1].Value};base64,… ({m.Length * 3 / 4 / 1024} KB)")),
        };
    }

    static long Estimar(Pedido p) => J.Serializar(p.Mensajes).Length / 4 + p.MaxTokens;

    // ---------- grabaciones ----------
    static string Hash(string texto) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..24];

    Dict? Leer(string clave)
    {
        var f = Path.Combine(grabaciones, $"{clave}.json");
        return File.Exists(f) ? J.LeerObjeto(File.ReadAllText(f, Encoding.UTF8)) : null;
    }

    void Guardar(string clave, Dict pedido, Dict respuesta)
    {
        Directory.CreateDirectory(grabaciones);
        var g = new Dict { ["pedido"] = pedido, ["respuesta"] = respuesta, ["grabado"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        File.WriteAllText(Path.Combine(grabaciones, $"{clave}.json"), J.Serializar(g), new UTF8Encoding(false));
    }

    // ---------- chat ----------
    /// <summary>
    /// Devuelve {proveedor, modelo, texto, tool_calls, tokens_entrada, tokens_salida, ms, ttft_ms, estructura, request, grabado}.
    /// Si el proveedor responde con error (o el limitador simula un 429) lanza ErrorProveedor con el mismo resultado.
    /// </summary>
    public async Task<Dict> Completar(Pedido p)
    {
        try { return await CompletarUnaVez(p, conEstructura: true); }
        catch (ErrorProveedor e) when (e.Estado == 400 && p.Esquema != null && !p.Proveedor.Local)
        {
            var r = await CompletarUnaVez(SinEstructura(p), conEstructura: false);
            r["estructura"] = "pedida en el prompt";
            return r;
        }
    }

    async Task<Dict> CompletarUnaVez(Pedido p, bool conEstructura)
    {
        var prov = p.Proveedor;
        var cuerpo = Cuerpo(p, conEstructura);
        var resultado = new Dict
        {
            ["proveedor"] = prov.Id, ["modelo"] = p.Modelo, ["request"] = Mostrable(prov, cuerpo),
            ["estructura"] = p.Esquema == null ? null : conEstructura ? "garantizada" : "pedida en el prompt",
        };

        if (LimiteSimulado && Limites.TryGetValue(Llave(prov.Id, p.Modelo), out var lim) && lim.Reservar(Estimar(p)) is { } corte)
        {
            var espera = Math.Ceiling(corte.espera);
            resultado["estado"] = 429L;
            resultado["error"] = $"429 Too Many Requests · límite simulado de {corte.limite} · Retry-After: {espera} s";
            resultado["simulado"] = true;
            throw new ErrorProveedor(429, resultado.Str("error"), resultado, espera);
        }

        var clave = Hash(prov.Url + J.Py(cuerpo, ordenar: true));
        if (ModoGuion)
        {
            var g = Leer(clave) ?? throw new SinGrabacion(
                "No hay una grabación de esta llamada. Apagá el modo guion o corré lo mismo una vez en vivo.");
            var r = resultado.Con(g.O("respuesta")).Con(new Dict { ["grabado"] = g.G("grabado"), ["estado"] = 200L });
            return r;
        }

        if (!prov.TieneClave)
        {
            resultado["estado"] = 401L;
            resultado["error"] = "Falta la clave de NVIDIA: definí NVIDIA_API_KEY o Proveedores:nvidia:ApiKey en appsettings.Local.json.";
            throw new ErrorProveedor(401, resultado.Str("error"), resultado);
        }

        using var pedido = new HttpRequestMessage(HttpMethod.Post, prov.Url + "/chat/completions")
        {
            Content = new StringContent(J.Serializar(cuerpo), Encoding.UTF8, "application/json"),
        };
        if (!prov.Local) pedido.Headers.Authorization = new("Bearer", prov.Clave);

        using var corte2 = new CancellationTokenSource(TimeSpan.FromSeconds(p.TimeoutS));
        var reloj = Stopwatch.StartNew();
        HttpResponseMessage r2;
        try { r2 = await http.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, corte2.Token); }
        catch (Exception e) when (e is TaskCanceledException or HttpRequestException)
        {
            resultado["estado"] = e is TaskCanceledException ? 408L : 503L;
            resultado["error"] = e is TaskCanceledException ? $"Timeout: sin respuesta en {p.TimeoutS} s" : $"No se pudo conectar: {e.Message}";
            resultado["ms"] = reloj.ElapsedMilliseconds;
            throw new ErrorProveedor(resultado.I("estado"), resultado.Str("error"), resultado);
        }
        using var _ = r2;

        if ((int)r2.StatusCode >= 400)
        {
            var texto = await r2.Content.ReadAsStringAsync();
            double? espera = r2.Headers.RetryAfter?.Delta?.TotalSeconds
                ?? (r2.Headers.RetryAfter?.Date is { } fecha ? (fecha - DateTimeOffset.UtcNow).TotalSeconds : null);
            resultado["estado"] = (long)r2.StatusCode;
            resultado["error"] = $"{(int)r2.StatusCode} {r2.ReasonPhrase}" + (espera != null ? $" · Retry-After: {Math.Ceiling(espera.Value)} s" : "")
                                 + $" · {Texto.Corte(texto, 240)}";
            resultado["ms"] = reloj.ElapsedMilliseconds;
            throw new ErrorProveedor((int)r2.StatusCode, resultado.Str("error"), resultado, espera);
        }

        var salida = cuerpo.B("stream") ? await LeerStream(r2, reloj, corte2.Token) : await LeerCompleto(r2);
        salida["ms"] = reloj.ElapsedMilliseconds;
        if (salida.G("tokens_entrada") == null)  // el proveedor no informó el uso: se estima
        {
            salida["tokens_entrada"] = (long)(J.Serializar(p.Mensajes).Length / 4);
            salida["tokens_salida"] = (long)(salida.Str("texto").Length / 4);
            salida["tokens_estimados"] = true;
        }
        Guardar(clave, cuerpo, salida);
        return resultado.Con(salida).Con(new Dict { ["estado"] = 200L, ["grabado"] = null });
    }

    static async Task<Dict> LeerCompleto(HttpResponseMessage r)
    {
        var d = J.LeerObjeto(await r.Content.ReadAsStringAsync());
        var mensaje = d.Ds("choices").FirstOrDefault()?.O("message") ?? new Dict();
        var llamadas = mensaje.Ds("tool_calls").Select(t =>
        {
            var f = t.O("function") ?? new Dict();
            object? args;
            try { args = J.Leer(f.Str("arguments", "{}")); }
            catch (System.Text.Json.JsonException) { args = f.Str("arguments"); }
            return new Dict { ["nombre"] = f.Str("name"), ["argumentos"] = args };
        }).ToList();
        // Las llamadas tal cual vinieron: hacen falta para devolverle al modelo el resultado de cada tool.
        var crudas = mensaje.Ds("tool_calls").Select((t, i) => new Dict(t) { ["id"] = t.S("id") ?? $"call_{i}" }).ToList();
        var uso = d.O("usage");
        return new Dict
        {
            ["texto"] = mensaje.Str("content"), ["tool_calls"] = llamadas, ["tool_calls_crudas"] = crudas, ["ttft_ms"] = null,
            ["tokens_entrada"] = uso?.G("prompt_tokens"), ["tokens_salida"] = uso?.G("completion_tokens"),
        };
    }

    static async Task<Dict> LeerStream(HttpResponseMessage r, Stopwatch reloj, CancellationToken ct)
    {
        var texto = new StringBuilder();
        long? ttft = null;
        Dict? uso = null;
        using var lector = new StreamReader(await r.Content.ReadAsStreamAsync(ct));
        while (await lector.ReadLineAsync(ct) is { } linea)
        {
            if (!linea.StartsWith("data:")) continue;
            var dato = linea[5..].Trim();
            if (dato == "[DONE]") break;
            var d = J.LeerObjeto(dato);
            if (d.O("usage") is { } u) uso = u;
            var delta = d.Ds("choices").FirstOrDefault()?.O("delta");
            if (delta?.S("content") is { Length: > 0 } c)
            {
                ttft ??= reloj.ElapsedMilliseconds;
                texto.Append(c);
            }
        }
        return new Dict
        {
            ["texto"] = texto.ToString(), ["tool_calls"] = new List<Dict>(), ["ttft_ms"] = ttft,
            ["tokens_entrada"] = uso?.G("prompt_tokens"), ["tokens_salida"] = uso?.G("completion_tokens"),
        };
    }

    // ---------- embeddings ----------
    /// <summary>Embeddings por el endpoint /embeddings. Siempre se guardan: son deterministas.</summary>
    public async Task<(List<float[]> vectores, Dict resultado)> Embeber(Proveedor prov, string modelo, List<string> textos)
    {
        var cuerpo = new Dict { ["model"] = modelo, ["input"] = textos };
        var clave = "emb-" + Hash(prov.Url + J.Py(cuerpo, ordenar: true));
        var resultado = new Dict
        {
            ["proveedor"] = prov.Id, ["modelo"] = modelo,
            ["request"] = new Dict
            {
                ["metodo"] = "POST", ["url"] = prov.Url + "/embeddings", ["base"] = prov.Url, ["ruta"] = "/embeddings",
                ["encabezados"] = new Dict { ["Content-Type"] = "application/json" },
                ["cuerpo"] = new Dict { ["model"] = modelo, ["input"] = textos.Count == 1 ? textos : new List<object?> { $"… {textos.Count} textos" } },
            },
        };
        if (Leer(clave) is { } g)
            return (Vectores(g.G("respuesta")), resultado.Con(new Dict { ["ms"] = 0L, ["tokens_entrada"] = (long)(textos.Sum(t => t.Length) / 4), ["grabado"] = g.G("grabado"), ["estado"] = 200L }));
        if (ModoGuion) throw new SinGrabacion("No hay embeddings grabados para esta consulta.");
        var reloj = Stopwatch.StartNew();
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var r = await http.PostAsync(prov.Url + "/embeddings", new StringContent(J.Serializar(cuerpo), Encoding.UTF8, "application/json"), corte.Token);
        var texto = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Embeddings: {(int)r.StatusCode} {Texto.Corte(texto, 200)}");
        var d = J.LeerObjeto(texto);
        var lista = d.Ds("data").OrderBy(x => x.I("index")).Select(x => x.G("embedding")).ToList<object?>();
        Guardar(clave, cuerpo, new Dict { ["vectores"] = lista });
        return (Vectores(new Dict { ["vectores"] = lista }), resultado.Con(new Dict
        {
            ["ms"] = reloj.ElapsedMilliseconds, ["tokens_entrada"] = d.O("usage")?.G("prompt_tokens"), ["grabado"] = null, ["estado"] = 200L,
        }));
    }

    static List<float[]> Vectores(object? respuesta) =>
        ((respuesta as Dict)?.G("vectores") as List<object?> ?? [])
        .Select(v => ((List<object?>)v!).Select(x => (float)J.Num(x)!.Value).ToArray()).ToList();

    public static double Coseno(float[] a, float[] b)
    {
        double p = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { p += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return p / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-9);
    }

    public static List<Dict> M(string sistema, string usuario) =>
        [new Dict { ["role"] = "system", ["content"] = sistema }, new Dict { ["role"] = "user", ["content"] = usuario }];
}
