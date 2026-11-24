// MesaYa · Capability Matrix y Model Routing (Módulo 4 · Unidad 2).
using MesaYa.Comun;
using MesaYa.Matriz;
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
cliente.LimiteSimulado = false;

Evaluacion.Perfiles = app.Configuration.GetSection("Perfiles").GetChildren().Select(s => new Perfil
{
    Id = s["Id"]!, Nombre = s["Nombre"]!, Proveedor = s["Proveedor"]!, Modelo = s["Modelo"]!,
    PrecioEntrada = s.GetValue<double>("PrecioEntrada"), PrecioSalida = s.GetValue<double>("PrecioSalida"), Respaldo = s["Respaldo"],
}).ToList();
// Sin clave de NVIDIA, el perfil grande corre su respaldo local: la demo funciona igual, con menos diferencia entre perfiles.
foreach (var p in Evaluacion.Perfiles.Where(p => !cliente.Proveedor(p.Proveedor).TieneClave && p.Respaldo != null))
{
    p.Proveedor = "ollama";
    p.Modelo = p.Respaldo!;
    p.UsaRespaldo = true;
}

var catalogo = J.LeerObjeto(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "escenarios.json")));
Evaluacion.Tareas = catalogo.Ds("tareas");
Evaluacion.Politica = catalogo.Str("politica");
var mensajesRouter = catalogo.Ds("mensajes_router");

var local = Local.Instancia;
_ = Task.Run(() => local.Preparar(cliente.Proveedor("ollama").Url, Evaluacion.Perfiles.Where(p => p.Proveedor == "ollama").Select(p => p.Modelo)));

// ---------- lo que se guarda en datos/ (no va al repositorio) ----------
var carpeta = Environment.GetEnvironmentVariable("DATOS") ?? "datos";
object? Cargar(string nombre)
{
    var f = Path.Combine(carpeta, nombre);
    return File.Exists(f) ? J.Leer(File.ReadAllText(f)) : null;
}
void Guardar(string nombre, object o)
{
    Directory.CreateDirectory(carpeta);
    var f = Path.Combine(carpeta, nombre);
    File.WriteAllText(f + ".tmp", J.Serializar(o));
    File.Move(f + ".tmp", f, overwrite: true);
}
Evaluacion.Resultados = (Cargar("resultados.json") as Dict ?? new Dict()).ToDictionary(kv => kv.Key, kv => (Dict)kv.Value!);
var ultimos = Cargar("ultimos.json") as Dict ?? new Dict();  // la última corrida de cada tipo

IResult? NoListo() => local.Listo() || cliente.ModoGuion ? null
    : Http.Error(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.");

Trabajo? trabajo = null;
IResult Lanzar(string tipo, Func<Trabajo, Task> f)
{
    if (NoListo() is { } e) return e;
    if (trabajo is { Estado: "corriendo" }) return Http.Error(409, "Ya hay una corrida en curso.");
    var t = trabajo = new Trabajo(tipo);
    _ = Task.Run(async () =>
    {
        try { await f(t); t.Estado = "listo"; }
        catch (SinGrabacion e2) { t.Error = e2.Message; t.Estado = "error"; }
        catch (Exception e2) { t.Error = $"{e2.GetType().Name}: {e2.Message}"; t.Estado = "error"; }
        t.Actual = null;
        lock (Evaluacion.Resultados) Guardar("resultados.json", Evaluacion.Resultados);
        if (t.Resultado != null)
        {
            ultimos[tipo] = t.Resultado.Con(new Dict { ["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
            Guardar("ultimos.json", ultimos);
        }
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
        ["proveedores"] = cliente.Proveedores.Values.Select(p => p.Publico()).ToList(),
    });
});

app.MapPost("/api/config", async (HttpRequest req) =>
{
    var c = await Http.Cuerpo(req);
    if (c.G("modo_guion") is bool guion) cliente.ModoGuion = guion;
    return Http.Json(new Dict { ["modo_guion"] = cliente.ModoGuion });
});

app.MapGet("/api/catalogo", () => Http.Json(new Dict
{
    ["escenarios"] = catalogo.G("escenarios"), ["perfiles"] = Evaluacion.Perfiles.Select(p => p.Publico()).ToList(),
    ["tareas"] = Evaluacion.Tareas.Select(t => t.Con(new Dict { ["enunciado"] = Evaluacion.Enunciado(t) })).ToList(),
    ["estrategias"] = Evaluacion.Estrategias, ["politica"] = Evaluacion.Politica, ["hoy"] = Evaluacion.Hoy,
}));

app.MapGet("/api/ultimos", () =>
{
    var m = Evaluacion.Matriz();
    return Http.Json(ultimos.Con(new Dict { ["matriz"] = m.Ds("filas").Count > 0 ? m : null }));
});

app.MapDelete("/api/resultados", () =>
{
    lock (Evaluacion.Resultados) Evaluacion.Resultados.Clear();
    lock (Evaluacion.Llamadas) Evaluacion.Llamadas.Clear();
    ultimos.Clear();
    Guardar("resultados.json", new Dict());
    Guardar("ultimos.json", ultimos);
    return Http.Json(new Dict());
});

app.MapGet("/api/llamada", (string perfil, string tarea) =>
{
    lock (Evaluacion.Llamadas)
    {
        var l = Evaluacion.Llamadas.LastOrDefault(x => x.Str("perfil") == perfil && x.Str("tarea") == tarea);
        return l != null ? Http.Json(l) : Http.Error(404, "Esta llamada no se hizo en esta sesión: corré la medición para ver el request.");
    }
});

app.MapPost("/api/matriz", () => Lanzar("matriz", Evaluacion.Medir));
app.MapPost("/api/router", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    return Lanzar("router", t => Evaluacion.Router(t, p.D("derivacion", 0.5)));
});
app.MapPost("/api/clasificar", () => Lanzar("clasificacion", t => Evaluacion.Clasificar(t, mensajesRouter)));
app.MapGet("/api/trabajo", () =>
{
    var f = trabajo?.Foto();
    if (f != null && trabajo!.Tipo == "matriz" && trabajo.Estado == "corriendo")
        f["parcial"] = Evaluacion.Matriz();  // para pintar el tablero a medida que avanza
    return Http.Json(f);
});

app.Run();
