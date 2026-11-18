// MesaYa · Estado, durabilidad y handoff (Unidad 3).
using MesaYa.Comun;
using MesaYa.Durabilidad;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

var estaticos = Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });

var ollama = Ollama.Instancia;
var catalogo = J.LeerObjeto(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "escenarios.json")));
var configBase = new Dict { ["checkpoints"] = true, ["idempotencia"] = true, ["retry"] = true, ["handoff_contexto"] = true };
var fallasBase = new Dict { ["caida_tras"] = 0L, ["timeout"] = "", ["errores"] = "", ["json_roto"] = false };

_ = Task.Run(ollama.Preparar);

// Todo el estado vive en datos/ (no va al repositorio): sobrevive a reiniciar la demo.
var almacen = new Almacen(Environment.GetEnvironmentVariable("DATOS") ?? "datos");
var sistema = new Sistema(almacen.Carpeta);
var agente = new Agente(almacen, sistema);
var cerrojo = new SemaphoreSlim(1, 1);  // un turno a la vez: la demo tiene un solo cliente

IResult? NoListo() => ollama.Listo() || ollama.ModoGuion ? null
    : Http.Error(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.");

async Task<IResult> Intentar(Func<Task<Dict>> f)
{
    if (NoListo() is { } e) return e;
    await cerrojo.WaitAsync();
    try
    {
        var r = await f();
        var turnos = almacen.Turnos();
        r["n"] = (long)turnos.Count + 1;
        r["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        turnos.Add(r);
        almacen.GuardarTurnos(turnos);
        return Http.Json(r);
    }
    catch (SinGrabacion e2) { return Http.Error(404, e2.Message); }
    catch (ErrorHttp e2) { return Http.Error(e2.Codigo, e2.Message); }
    catch (Exception e2) { return Http.Error(502, $"{e2.GetType().Name}: {e2.Message}"); }
    finally { cerrojo.Release(); }
}

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
    ["pasos"] = Datos.Pasos, ["agentes"] = Datos.Agentes, ["config"] = configBase, ["fallas"] = fallasBase,
    ["escenarios"] = catalogo.G("escenarios"), ["cliente"] = Datos.Cliente,
}));

// ---------- el chat ----------
app.MapPost("/api/turno", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    var mensaje = p.Str("mensaje").Trim();
    if (mensaje.Length == 0) return Http.Error(400, "El mensaje está vacío.");
    return await Intentar(() => agente.Mensaje(mensaje, configBase.Con(p.O("config")), fallasBase.Con(p.O("fallas"))));
});

app.MapPost("/api/reanudar", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    return await Intentar(() => agente.Reanudar(configBase.Con(p.O("config"))));
});

// ---------- lo que está en disco ----------
app.MapGet("/api/datos", () => Http.Json(new Dict
{
    ["turnos"] = almacen.Turnos(), ["tareas"] = almacen.TodasLasTareas(), ["conversacion"] = almacen.Conversacion(),
    ["cola"] = almacen.Cola(), ["bandeja"] = almacen.Bandeja(), ["log"] = almacen.LeerLog(), ["sistema"] = sistema.Foto(),
}));

app.MapDelete("/api/datos", async () =>
{
    await cerrojo.WaitAsync();
    try { almacen.Reiniciar(); sistema.Reiniciar(); }
    finally { cerrojo.Release(); }
    return Http.Json(new Dict());
});

app.Run();
