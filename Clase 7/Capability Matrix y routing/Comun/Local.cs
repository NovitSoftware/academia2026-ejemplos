using System.Net.Http.Json;

namespace MesaYa.Comun;

/// <summary>No hay una grabación para la llamada pedida en modo guion.</summary>
public sealed class SinGrabacion(string mensaje) : Exception(mensaje);

/// <summary>Error con código HTTP. Se responde como {"detail": "..."}.</summary>
public sealed class ErrorHttp(int codigo, string detalle) : Exception(detalle)
{
    public int Codigo { get; } = codigo;
}

/// <summary>
/// El Ollama de la máquina: qué modelos hay y la descarga de los que falten.
/// Las llamadas a los modelos no pasan por acá: van por el Cliente, con la API compatible con OpenAI.
/// </summary>
public sealed class Local
{
    public static readonly Local Instancia = new();

    public bool Conectado { get; private set; }
    public List<string> Instalados { get; private set; } = [];
    public List<string> Necesarios { get; private set; } = [];
    public Dictionary<string, Dict> Descargas { get; } = [];
    public string? Error { get; private set; }

    readonly HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = Timeout.InfiniteTimeSpan };
    string url = "http://localhost:11434";

    public async Task<List<string>> Listar()
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var r = await http.GetAsync(url + "/api/tags", corte.Token);
        r.EnsureSuccessStatusCode();
        var d = J.LeerObjeto(await r.Content.ReadAsStringAsync());
        Instalados = d.Ds("models").Select(m => m.Str("name")).Order(StringComparer.Ordinal).ToList();
        Conectado = true;
        return Instalados;
    }

    /// <summary>El nombre instalado: exacto, con :latest o una variante (qwen3.5:9b → qwen3.5:9b-32k).</summary>
    public string? Resolver(string nombre)
    {
        foreach (var candidato in new[] { nombre, $"{nombre}:latest" })
            if (Instalados.Contains(candidato)) return candidato;
        return Instalados.FirstOrDefault(m => m.StartsWith(nombre, StringComparison.Ordinal));
    }

    public bool Listo() => Conectado && Necesarios.All(m => Resolver(m) != null);

    /// <summary>Espera a Ollama y descarga los modelos que falten. Corre en segundo plano al iniciar.</summary>
    public async Task Preparar(string urlV1, IEnumerable<string> modelos)
    {
        url = urlV1.EndsWith("/v1") ? urlV1[..^3] : urlV1;
        Necesarios = modelos.Distinct().ToList();
        while (true)
        {
            try { await Listar(); break; }
            catch
            {
                Conectado = false;
                Error = $"No se puede conectar a Ollama en {url}";
                await Task.Delay(2000);
            }
        }
        Error = null;
        foreach (var m in Necesarios.Where(m => Resolver(m) == null))
        {
            await Descargar(m);
            await Listar();
        }
    }

    async Task Descargar(string nombre)
    {
        for (var intento = 1; intento <= 5; intento++)  // el registro de Ollama a veces responde 503: se reintenta
        {
            var estado = Descargas[nombre] = new Dict { ["estado"] = "descargando", ["completado"] = 0L, ["total"] = 0L };
            try
            {
                using var pedido = new HttpRequestMessage(HttpMethod.Post, url + "/api/pull") { Content = JsonContent.Create(new { model = nombre }) };
                using var r = await http.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead);
                r.EnsureSuccessStatusCode();
                using var lector = new StreamReader(await r.Content.ReadAsStreamAsync());
                while (await lector.ReadLineAsync() is { } linea)
                {
                    if (linea.Length == 0) continue;
                    var d = J.LeerObjeto(linea);
                    if (d.ContainsKey("total")) { estado["total"] = d.G("total"); estado["completado"] = d.G("completed") ?? 0L; }
                    if (d.B("error")) throw new InvalidOperationException(d.Str("error"));
                }
                estado["estado"] = "listo";
                Error = null;
                return;
            }
            catch (Exception e)
            {
                estado["estado"] = $"error: {e.Message}";
                Error = $"No se pudo descargar {nombre}: {e.Message}";
            }
            await Task.Delay(5000 * intento);
        }
    }

    public Dict Estado() => new()
    {
        ["url"] = url, ["conectado"] = Conectado, ["error"] = Error, ["descargas"] = Descargas,
        ["faltan"] = Necesarios.Where(m => Resolver(m) == null).ToList(),
        ["instalados"] = Instalados.Where(m => !m.Contains("bge") && !m.Contains("embed")).ToList(),
    };
}
