// MesaYa · Arquitectura, control y human-in-the-loop (Unidades 4 y 5).
using MesaYa.Comun;
using MesaYa.Hitl;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 12 * 1024 * 1024);  // imágenes en base64
var app = builder.Build();

var estaticos = Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });

var ollama = Ollama.Instancia;
var catalogo = J.LeerObjeto(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "escenarios.json")));
var configBase = new Dict { ["max_pasos"] = 8L, ["costo_max"] = 0.5, ["detector"] = true, ["regla"] = "codigo", ["limite"] = 10000L, ["validar"] = true };

_ = Task.Run(ollama.Preparar);

var almacen = new Almacen(Environment.GetEnvironmentVariable("DATOS") ?? "datos");
var sistema = new Sistema(almacen);
var asistente = new Asistente(almacen, sistema);
var cerrojo = new SemaphoreSlim(1, 1);  // un turno a la vez

IResult? NoListo() => ollama.Listo() || ollama.ModoGuion ? null
    : Http.Error(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.");

async Task<IResult> Intentar(Func<Task<Dict>> f, bool usaModelo = true)
{
    if (usaModelo && NoListo() is { } e) return e;
    await cerrojo.WaitAsync();
    try
    {
        var r = await f();
        var turnos = almacen.Lista("turnos.json");
        r["n"] = (long)turnos.Count + 1;
        r["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        turnos.Add(r);
        almacen.Escribir("turnos.json", turnos);
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
    ["config"] = configBase, ["escenarios"] = catalogo.G("escenarios"), ["herramientas"] = Datos.Herramientas, ["niveles"] = Datos.Niveles,
    ["imagenes"] = catalogo.G("imagenes"), ["cliente"] = Datos.Cliente, ["encargada"] = Datos.Encargada, ["timeout_ms"] = Agente.TimeoutMs,
    ["precio"] = new Dict { ["entrada"] = Datos.PrecioEntrada, ["salida"] = Datos.PrecioSalida },
}));

// ---------- el chat ----------
app.MapPost("/api/turno", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    var mensaje = p.Str("mensaje").Trim();
    string? imagen = p.S("imagen"), nombre = p.S("imagen_nombre");
    // Las imágenes de ejemplo se leen del disco: los bytes son siempre los mismos y las grabaciones sirven.
    if (p.S("imagen_ejemplo") is { Length: > 0 } ejemplo)
    {
        var f = Path.Combine(estaticos, "imagenes", Path.GetFileName(ejemplo));
        if (!File.Exists(f)) return Http.Error(404, "No existe esa imagen de ejemplo.");
        imagen = Convert.ToBase64String(await File.ReadAllBytesAsync(f));
        nombre = Path.GetFileName(ejemplo);
    }
    if (mensaje.Length == 0 && imagen == null) return Http.Error(400, "El mensaje está vacío.");
    if (mensaje.Length == 0) mensaje = "Te mando esta imagen.";
    return await Intentar(() => asistente.Mensaje(mensaje, imagen, nombre, configBase.Con(p.O("config"))));
});

// ---------- la bandeja de aprobaciones ----------
app.MapPost("/api/aprobar", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    return await Intentar(() => asistente.Resolver(p.Str("id"), p.B("aprobar"), Datos.Encargada, configBase.Con(p.O("config"))), usaModelo: false);
});

// ---------- lo que está en disco ----------
app.MapGet("/api/datos", () => Http.Json(new Dict
{
    ["turnos"] = almacen.Lista("turnos.json"), ["sistema"] = sistema.Foto(), ["aprobaciones"] = almacen.Lista("aprobaciones.json"),
    ["conversacion"] = almacen.Objeto("conversacion.json"),
}));

app.MapDelete("/api/datos", async () =>
{
    await cerrojo.WaitAsync();
    try { almacen.Reiniciar(); }
    finally { cerrojo.Release(); }
    return Http.Json(new Dict());
});

app.Run();
