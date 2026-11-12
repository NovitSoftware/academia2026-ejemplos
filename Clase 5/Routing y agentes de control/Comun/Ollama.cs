using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace MesaYa.Comun;

/// <summary>No hay una grabación para la llamada pedida en modo guion.</summary>
public sealed class SinGrabacion(string mensaje) : Exception(mensaje);

/// <summary>Error con código HTTP, como HTTPException de FastAPI. Se responde como {"detail": "..."}.</summary>
public sealed class ErrorHttp(int codigo, string detalle) : Exception(detalle)
{
    public int Codigo { get; } = codigo;
}

/// <summary>
/// Cliente de Ollama con descarga de modelos y modo guion.
///
/// Cada llamada que termina bien se guarda en grabaciones/llm/. Con el modo guion
/// prendido, la misma llamada se responde desde esa grabación sin tocar Ollama:
/// sirve de plan B si Ollama falla en plena clase.
/// </summary>
public sealed class Ollama
{
    public static readonly Ollama Instancia = new();

    public string Url { get; }
    public string Modelo { get; set; }
    public string? Embeddings { get; set; }  // EMBEDDINGS="" la apaga
    public bool ModoGuion { get; set; }
    public bool Conectado { get; set; }
    public List<string> Instalados { get; private set; } = [];
    public Dictionary<string, Dict> Descargas { get; } = [];  // modelo -> {estado, completado, total}
    public string? Error { get; private set; }

    readonly HttpClient cliente;
    readonly string grabaciones;

    Ollama()
    {
        // El Ollama de la máquina. OLLAMA_URL permite apuntar a otro.
        Url = (Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434").TrimEnd('/');
        Modelo = Environment.GetEnvironmentVariable("MODELO") ?? "qwen3.5:4b";
        Embeddings = Environment.GetEnvironmentVariable("EMBEDDINGS") is { } e ? (e.Length > 0 ? e : null) : "bge-m3";
        grabaciones = Path.Combine(Environment.GetEnvironmentVariable("GRABACIONES") ?? "grabaciones", "llm");
        Directory.CreateDirectory(grabaciones);
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) };
        // Sin timeout global: cada llamada pone el suyo (la descarga de modelos puede tardar mucho).
        cliente = new HttpClient(handler) { BaseAddress = new Uri(Url + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    async Task<bool> Conectar()
    {
        try { await Listar(); return true; }
        catch { return false; }
    }

    static CancellationToken Limite(int segundos = 300) => new CancellationTokenSource(TimeSpan.FromSeconds(segundos)).Token;

    // ---------- modelos ----------
    public async Task<List<string>> Listar()
    {
        var r = await cliente.GetAsync("api/tags", Limite(10));
        r.EnsureSuccessStatusCode();
        var d = J.LeerObjeto(await r.Content.ReadAsStringAsync());
        Instalados = d.Ds("models").Select(m => m.Str("name")).Order(StringComparer.Ordinal).ToList();
        Conectado = true;
        return Instalados;
    }

    /// <summary>Busca el modelo instalado: exacto, con :latest o una variante (qwen3.5:9b → qwen3.5:9b-32k).</summary>
    string? Resolver(string nombre)
    {
        foreach (var candidato in new[] { nombre, $"{nombre}:latest" })
            if (Instalados.Contains(candidato)) return candidato;
        return Instalados.FirstOrDefault(m => m.StartsWith(nombre, StringComparison.Ordinal));
    }

    /// <summary>Espera a Ollama y descarga los modelos que falten. Corre en segundo plano al iniciar.</summary>
    public async Task Preparar()
    {
        while (!await Conectar())  // Ollama todavía no levantó
        {
            Conectado = false;
            Error = $"No se puede conectar a Ollama en {Url}";
            await Task.Delay(2000);
        }
        Error = null;
        foreach (var embeddings in new[] { true, false })
        {
            if (embeddings && Embeddings == null) continue;
            var nombre = embeddings ? Embeddings! : Modelo;
            var encontrado = Resolver(nombre);
            if (encontrado == null)
            {
                await Descargar(nombre);
                await Listar();
                encontrado = Resolver(nombre) ?? nombre;
            }
            if (embeddings) Embeddings = encontrado; else Modelo = encontrado;
        }
    }

    async Task Descargar(string nombre)
    {
        for (var intento = 1; intento <= 5; intento++)  // el registro de Ollama a veces responde 503: se reintenta
        {
            await DescargarUnaVez(nombre);
            if (Descargas[nombre].Str("estado") == "listo")
            {
                Error = null;
                return;
            }
            await Task.Delay(5000 * intento);
        }
    }

    async Task DescargarUnaVez(string nombre)
    {
        var estado = Descargas[nombre] = new Dict { ["estado"] = "descargando", ["completado"] = 0L, ["total"] = 0L };
        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Post, "api/pull") { Content = JsonContent.Create(new { model = nombre }) };
            using var r = await cliente.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            using var lector = new StreamReader(await r.Content.ReadAsStreamAsync());
            while (await lector.ReadLineAsync() is { } linea)
            {
                if (linea.Length == 0) continue;
                var d = J.LeerObjeto(linea);
                if (d.ContainsKey("total"))
                {
                    estado["total"] = d.G("total");
                    estado["completado"] = d.G("completed") ?? 0L;
                }
                if (d.B("error")) throw new InvalidOperationException(d.Str("error"));
            }
            estado["estado"] = "listo";
        }
        catch (Exception e)
        {
            estado["estado"] = $"error: {e.Message}";
            Error = $"No se pudo descargar {nombre}: {e.Message}";
        }
    }

    public bool Listo() => Conectado && Resolver(Modelo) != null && (Embeddings == null || Resolver(Embeddings) != null);

    public Dict Estado() => new()
    {
        ["url"] = Url,
        ["conectado"] = Conectado,
        ["modelo"] = Modelo,
        ["embeddings"] = Embeddings,
        ["instalados"] = Instalados.Where(m => !m.Contains("bge") && !m.Contains("embed")).ToList(),
        ["descargas"] = Descargas,
        ["error"] = Error,
        ["modo_guion"] = ModoGuion,
    };

    // ---------- grabaciones ----------
    /// <summary>Misma clave que la versión Python: sha256 del pedido con claves ordenadas.</summary>
    static string Clave(Dict cuerpo) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(J.Py(cuerpo, ordenar: true))))[..24];

    Dict? Leer(string clave)
    {
        var f = Path.Combine(grabaciones, $"{clave}.json");
        return File.Exists(f) ? J.LeerObjeto(File.ReadAllText(f, Encoding.UTF8)) : null;
    }

    void Guardar(string clave, Dict cuerpo, object respuesta)
    {
        Directory.CreateDirectory(grabaciones);  // por si alguien borró la carpeta con la app andando
        var f = Path.Combine(grabaciones, $"{clave}.json");
        var g = new Dict { ["pedido"] = cuerpo, ["respuesta"] = respuesta, ["grabado"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        File.WriteAllText(f, J.Serializar(g), new UTF8Encoding(false));
    }

    // ---------- llamadas ----------
    /// <summary>Devuelve {texto, tokens_prompt, tokens_respuesta, ms, grabado}.</summary>
    public async Task<Dict> Chat(List<Dict> mensajes, Dict? formato = null,
                                 double temperatura = 0.0, int numCtx = 8192, int? maxTokens = null, int semilla = 42)
    {
        var cuerpo = new Dict
        {
            ["model"] = Modelo, ["messages"] = mensajes, ["stream"] = false, ["think"] = false,
            ["options"] = new Dict
            {
                ["temperature"] = temperatura, ["seed"] = semilla, ["num_ctx"] = numCtx,
                // Tope de salida: evita que un JSON estructurado quede en un bucle.
                ["num_predict"] = maxTokens ?? (formato != null ? 900 : 500),
            },
        };
        if (formato != null) cuerpo["format"] = formato;
        var clave = Clave(cuerpo);
        if (ModoGuion)
        {
            var g = Leer(clave) ?? throw new SinGrabacion(
                "No hay una grabación de esta llamada. Apagá el modo guion o corré la misma consulta una vez en vivo.");
            return (g.O("respuesta") ?? new Dict()).Con(new Dict { ["grabado"] = g.G("grabado") });
        }
        var reloj = Stopwatch.StartNew();
        var r = await cliente.PostAsync("api/chat", Contenido(cuerpo), Limite());
        var texto = await r.Content.ReadAsStringAsync();
        if ((int)r.StatusCode >= 400)
            throw new InvalidOperationException($"Ollama respondió {(int)r.StatusCode}: {texto[..Math.Min(200, texto.Length)]}");
        var d = J.LeerObjeto(texto);
        var salida = new Dict
        {
            ["texto"] = d.O("message")?.Str("content") ?? "",
            ["tokens_prompt"] = d.G("prompt_eval_count") ?? 0L,
            ["tokens_respuesta"] = d.G("eval_count") ?? 0L,
            ["ms"] = reloj.ElapsedMilliseconds,
        };
        Guardar(clave, cuerpo, salida);
        return salida.Con(new Dict { ["grabado"] = null });
    }

    public async Task<(Dict datos, Dict respuesta)> ChatJson(List<Dict> mensajes, Dict esquema, int? maxTokens = null,
                                                         double temperatura = 0.0, int semilla = 42)
    {
        var r = await Chat(mensajes, esquema, temperatura, maxTokens: maxTokens, semilla: semilla);
        try { return (J.Leer(r.Str("texto")) as Dict ?? new Dict(), r); }
        catch (System.Text.Json.JsonException) { return (new Dict(), r); }
    }

    public async Task<List<float[]>> Embeber(List<string> textos)
    {
        var cuerpo = new Dict { ["model"] = Embeddings, ["input"] = textos };
        var clave = "emb-" + Clave(cuerpo);
        var g = Leer(clave);
        if (g != null)  // los embeddings siempre se reutilizan: son deterministas
            return Vectores(g.G("respuesta"));
        if (ModoGuion) throw new SinGrabacion("No hay embeddings grabados para esta consulta.");
        var r = await cliente.PostAsync("api/embed", Contenido(cuerpo), Limite());
        r.EnsureSuccessStatusCode();
        var lista = J.LeerObjeto(await r.Content.ReadAsStringAsync()).G("embeddings");
        var vectores = Vectores(lista);
        if (textos.Count <= 4)  // solo se graban las consultas; el corpus va a su propio caché
            Guardar(clave, cuerpo, lista!);
        return vectores;
    }

    static List<float[]> Vectores(object? lista) =>
        (lista as List<object?> ?? []).Select(v => ((List<object?>)v!).Select(x => (float)J.Num(x)!.Value).ToArray()).ToList();

    static StringContent Contenido(Dict cuerpo) => new(J.Serializar(cuerpo), Encoding.UTF8, "application/json");
}
