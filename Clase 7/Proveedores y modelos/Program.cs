// MesaYa · Proveedores y modelos por propósito (Módulo 4 · Unidad 1).
using MesaYa.Comun;
using MesaYa.Proveedores;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
// La clave real va en appsettings.Local.json (no se versiona). La variable de entorno NVIDIA_API_KEY tiene prioridad.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

var estaticos = Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });

var cliente = Cliente.Instancia;
cliente.Configurar(app.Configuration);
Demo.Modelos = app.Configuration.GetSection("Modelos").GetChildren()
    .ToDictionary(p => p.Key, p => p.GetChildren().ToDictionary(f => f.Key, f => f.Value ?? ""));
Demo.LimiteBase = new Dict
{
    ["Requests"] = (long)app.Configuration.GetValue("LimiteSimulado:Requests", 6),
    ["Tokens"] = (long)app.Configuration.GetValue("LimiteSimulado:Tokens", 2400),
    ["VentanaS"] = (long)app.Configuration.GetValue("LimiteSimulado:VentanaS", 15),
};
var local = Local.Instancia;
_ = Task.Run(() => local.Preparar(cliente.Proveedor("ollama").Url, Demo.Modelos["ollama"].Values));

var catalogo = J.LeerObjeto(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "escenarios.json")));

// ---------- lo que se guarda en datos/ (no va al repositorio) ----------
var carpeta = Environment.GetEnvironmentVariable("DATOS") ?? "datos";
var archivo = Path.Combine(carpeta, "registro.json");
var registro = File.Exists(archivo) ? (J.Leer(File.ReadAllText(archivo)) as List<object?> ?? []).OfType<Dict>().ToList() : [];
Dict Registrar(Dict item)
{
    lock (registro)
    {
        item["n"] = (registro.Count == 0 ? 0 : registro.Max(c => c.I("n"))) + 1;
        item["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        registro.Add(item);
        Directory.CreateDirectory(carpeta);
        File.WriteAllText(archivo + ".tmp", J.Serializar(registro));
        File.Move(archivo + ".tmp", archivo, overwrite: true);
    }
    return item;
}

IResult? NoListo() => local.Listo() || cliente.ModoGuion ? null
    : Http.Error(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.");

// ---------- trabajos largos: uno a la vez ----------
Trabajo? trabajo = null;
IResult Lanzar(string tipo, Dict p, Func<Trabajo, Dict, Task> f)
{
    if (NoListo() is { } e) return e;
    if (trabajo is { Estado: "corriendo" }) return Http.Error(409, "Ya hay una corrida en curso.");
    var t = trabajo = new Trabajo(tipo);
    _ = Task.Run(async () =>
    {
        try { await f(t, p); t.Estado = "listo"; }
        catch (SinGrabacion e2) { t.Error = e2.Message; t.Estado = "error"; }
        catch (ErrorProveedor e2) { t.Error = e2.Message; t.Estado = "error"; }
        catch (Exception e2) { t.Error = $"{e2.GetType().Name}: {e2.Message}"; t.Estado = "error"; }
        t.Actual = null;
        Registrar(new Dict { ["tipo"] = tipo, ["config"] = p, ["trabajo"] = t.Foto() });
    });
    return Http.Json(t.Foto());
}

app.MapGet("/", () => Results.File(Path.Combine(estaticos, "index.html"), "text/html; charset=utf-8"));

app.MapGet("/api/estado", async () =>
{
    if (local.Conectado)
    {
        try { await local.Listar(); }
        catch { /* el indicador lo muestra */ }
    }
    return Http.Json(new Dict
    {
        ["ollama"] = local.Estado(), ["listo"] = local.Listo() || cliente.ModoGuion, ["modo_guion"] = cliente.ModoGuion,
        ["limite_simulado"] = cliente.LimiteSimulado, ["proveedores"] = cliente.Proveedores.Values.Select(p => p.Publico()).ToList(),
    });
});

app.MapPost("/api/config", async (HttpRequest req) =>
{
    var c = await Http.Cuerpo(req);
    if (c.G("modo_guion") is bool guion) cliente.ModoGuion = guion;
    if (c.G("limite_simulado") is bool lim) cliente.LimiteSimulado = lim;
    return Http.Json(new Dict { ["modo_guion"] = cliente.ModoGuion, ["limite_simulado"] = cliente.LimiteSimulado });
});

app.MapGet("/api/catalogo", () => Http.Json(new Dict
{
    ["escenarios"] = catalogo.G("escenarios"), ["modelos"] = Demo.Modelos, ["limite"] = Demo.LimiteBase,
    ["politicas"] = Datos.Politicas, ["hoy"] = Datos.Hoy,
}));

app.MapGet("/api/registro", () => { lock (registro) return Http.Json(registro.ToList()); });
app.MapDelete("/api/registro", () =>
{
    lock (registro) { registro.Clear(); if (File.Exists(archivo)) File.Delete(archivo); }
    return Http.Json(new Dict());
});

app.MapPost("/api/request", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    if (p.Str("mensaje").Trim().Length == 0) return Http.Error(400, "El mensaje está vacío.");
    if (p.Str("proveedor") == "ollama" && NoListo() is { } e) return e;
    try { return Http.Json(Registrar(await Demo.Request(p))); }
    catch (SinGrabacion e2) { return Http.Error(404, e2.Message); }
    catch (ErrorHttp e2) { return Http.Error(e2.Codigo, e2.Message); }
});

app.MapPost("/api/rafaga", async (HttpRequest req) => Lanzar("rafaga", await Http.Cuerpo(req), Demo.Rafaga));
app.MapPost("/api/pipeline", async (HttpRequest req) => Lanzar("pipeline", await Http.Cuerpo(req), Demo.Pipeline));
app.MapGet("/api/trabajo", () => Http.Json(trabajo?.Foto()));

app.Run();
