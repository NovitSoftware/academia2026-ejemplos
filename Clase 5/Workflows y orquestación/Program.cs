// MesaYa · Workflows y orquestación (Unidad 1).
using MesaYa.Comun;
using MesaYa.Orquestacion;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

var estaticos = Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });

var ollama = Ollama.Instancia;
var catalogo = J.LeerObjeto(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "escenarios.json")));
var configBase = new Dict { ["paralelo"] = true, ["max_vueltas"] = 3L, ["max_pasos"] = 8L };

// Historial de corridas: sobrevive a recargar la página y a reiniciar la demo. "Limpiar" lo borra.
var archivoCorridas = Path.Combine(Environment.GetEnvironmentVariable("DATOS") ?? "datos", "corridas.json");
var corridas = File.Exists(archivoCorridas)
    ? (J.Leer(File.ReadAllText(archivoCorridas)) as List<object?> ?? []).OfType<Dict>().ToList()
    : new List<Dict>();

void GuardarCorridas()
{
    Directory.CreateDirectory(Path.GetDirectoryName(archivoCorridas)!);
    var temporal = archivoCorridas + ".tmp";
    File.WriteAllText(temporal, J.Serializar(corridas));
    File.Move(temporal, archivoCorridas, overwrite: true);
}

_ = Task.Run(ollama.Preparar);

app.MapGet("/", () => Results.File(Path.Combine(estaticos, "index.html"), "text/html; charset=utf-8"));

app.MapGet("/api/estado", async () =>
{
    if (ollama.Conectado)
    {
        try { await ollama.Listar(); }
        catch { ollama.Conectado = false; }
    }
    return Http.Json(ollama.Estado().Con(new Dict { ["listo"] = ollama.Listo() || ollama.ModoGuion }));
});

app.MapPost("/api/config", async (HttpRequest req) =>
{
    var c = await Http.Cuerpo(req);
    if (c.B("modelo")) ollama.Modelo = c.Str("modelo");
    if (c.G("modo_guion") is bool guion) ollama.ModoGuion = guion;
    return Http.Json(ollama.Estado());
});

app.MapGet("/api/catalogo", () => Http.Json(new Dict
{
    ["patrones"] = Patrones.Catalogo, ["escenarios"] = catalogo.G("escenarios"), ["config"] = configBase,
}));

// Corre un pedido con el patrón elegido y devuelve la traza completa: pasos, llamadas, tokens y tiempos.
app.MapPost("/api/correr", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    if (!(ollama.Listo() || ollama.ModoGuion))
        return Http.Error(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.");
    var mensaje = p.Str("mensaje").Trim();
    if (mensaje.Length == 0) return Http.Error(400, "El pedido está vacío.");
    Dict resultado;
    try
    {
        resultado = await Patrones.Correr(p.Str("patron"), mensaje, configBase.Con(p.O("config")));
    }
    catch (SinGrabacion e) { return Http.Error(404, e.Message); }
    catch (ErrorHttp e) { return Http.Error(e.Codigo, e.Message); }
    catch (Exception e) { return Http.Error(502, $"{e.GetType().Name}: {e.Message}"); }
    lock (corridas)
    {
        resultado["n"] = (corridas.Count == 0 ? 0 : corridas.Max(c => c.I("n"))) + 1;
        resultado["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        corridas.Add(resultado);
        GuardarCorridas();
    }
    return Http.Json(resultado);
});

app.MapGet("/api/corridas", () =>
{
    lock (corridas) return Http.Json(corridas.ToList());
});

app.MapDelete("/api/corridas", () =>
{
    lock (corridas)
    {
        corridas.Clear();
        GuardarCorridas();
    }
    return Http.Json(new Dict { ["ok"] = true });
});

app.Run();
