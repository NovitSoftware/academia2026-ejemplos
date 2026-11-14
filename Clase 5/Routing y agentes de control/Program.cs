// MesaYa · Routing y agentes de control (Unidad 2).
using MesaYa.Comun;
using MesaYa.Control;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

var estaticos = Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });

var ollama = Ollama.Instancia;
var catalogo = J.LeerObjeto(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "escenarios.json")));
var configBase = new Dict { ["metodo"] = "llm", ["umbral"] = 0.7, ["permisos"] = true, ["reviewer"] = true, ["juez"] = false };
var setRouter = catalogo.Ds("set_router");
var ataques = catalogo.Ds("ataques");
Router.Ejemplos = (catalogo.O("ejemplos") ?? new Dict()).ToDictionary(
    e => e.Key, e => (e.Value as List<object?> ?? []).OfType<string>().ToList());

_ = Task.Run(ollama.Preparar);

// ---------- lo que se guarda en datos/ (no va al repositorio) ----------
var carpeta = Environment.GetEnvironmentVariable("DATOS") ?? "datos";
List<Dict> Cargar(string nombre)
{
    var f = Path.Combine(carpeta, nombre);
    return File.Exists(f) ? (J.Leer(File.ReadAllText(f)) as List<object?> ?? []).OfType<Dict>().ToList() : [];
}
void Guardar(string nombre, List<Dict> lista)
{
    Directory.CreateDirectory(carpeta);
    var f = Path.Combine(carpeta, nombre);
    File.WriteAllText(f + ".tmp", J.Serializar(lista));
    File.Move(f + ".tmp", f, overwrite: true);
}
var corridas = Cargar("corridas.json");      // el chat
var mediciones = Cargar("mediciones.json");  // las mediciones del router y los casos generados
var regresion = Cargar("regresion.json");    // el set de regresión

IResult? NoListo() => ollama.Listo() || ollama.ModoGuion ? null
    : Http.Error(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.");

async Task<IResult> Intentar(Func<Task<IResult>> f)
{
    if (NoListo() is { } e) return e;
    try { return await f(); }
    catch (SinGrabacion e2) { return Http.Error(404, e2.Message); }
    catch (ErrorHttp e2) { return Http.Error(e2.Codigo, e2.Message); }
    catch (Exception e2) { return Http.Error(502, $"{e2.GetType().Name}: {e2.Message}"); }
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
    ["intenciones"] = Datos.Intenciones, ["canales"] = Datos.Canales, ["metodos"] = Router.Metodos, ["config"] = configBase,
    ["escenarios"] = catalogo.G("escenarios"), ["ataques"] = ataques, ["set_router"] = setRouter,
}));

// ---------- chat ----------
app.MapPost("/api/turno", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    if (p.Str("mensaje").Trim().Length == 0) return Http.Error(400, "El mensaje está vacío.");
    return await Intentar(async () =>
    {
        var r = await Asistente.Turno(p.Str("mensaje").Trim(), p.Str("canal", "cliente"), configBase.Con(p.O("config")));
        lock (corridas)
        {
            r["n"] = (corridas.Count == 0 ? 0 : corridas.Max(c => c.I("n"))) + 1;
            r["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            corridas.Add(r);
            Guardar("corridas.json", corridas);
        }
        return Http.Json(r);
    });
});

app.MapGet("/api/corridas", () => { lock (corridas) return Http.Json(corridas.ToList()); });
app.MapDelete("/api/corridas", () => { lock (corridas) { corridas.Clear(); Guardar("corridas.json", corridas); } return Http.Json(new Dict()); });

// ---------- medir el router (slides 04 y 07) ----------
void GuardarMedicion(Dict m)
{
    lock (mediciones)
    {
        m["n"] = (mediciones.Count == 0 ? 0 : mediciones.Max(c => c.I("n"))) + 1;
        mediciones.Add(m);
        Guardar("mediciones.json", mediciones);
    }
}

app.MapPost("/api/medir", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    var cfg = configBase.Con(p.O("config"));
    return await Intentar(async () =>
    {
        var m = await Pruebas.Medir(setRouter, cfg.Str("metodo"), cfg.D("umbral"), "Set etiquetado");
        GuardarMedicion(m);
        return Http.Json(m);
    });
});

app.MapGet("/api/mediciones", () => { lock (mediciones) return Http.Json(mediciones.ToList()); });
app.MapDelete("/api/mediciones", () => { lock (mediciones) { mediciones.Clear(); Guardar("mediciones.json", mediciones); } return Http.Json(new Dict()); });

// ---------- agente de testing y regresión (slides 11 y 14) ----------
app.MapPost("/api/generar", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    var cfg = configBase.Con(p.O("config"));
    return await Intentar(async () =>
    {
        var m = await Pruebas.Generar(p.Str("intencion"), cfg.Str("metodo"), cfg.D("umbral"));
        GuardarMedicion(m);
        return Http.Json(m);
    });
});

app.MapGet("/api/regresion", () => { lock (regresion) return Http.Json(regresion.ToList()); });

app.MapPost("/api/regresion", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    lock (regresion)
    {
        foreach (var c in p.Ds("casos"))
            if (!regresion.Any(r => r.Str("mensaje") == c.Str("mensaje")))
                regresion.Add(new Dict { ["mensaje"] = c.Str("mensaje"), ["esperado"] = c.Str("esperado"), ["origen"] = c.Str("origen", "manual") });
        Guardar("regresion.json", regresion);
        return Http.Json(regresion.ToList());
    }
});

app.MapDelete("/api/regresion", () => { lock (regresion) { regresion.Clear(); Guardar("regresion.json", regresion); } return Http.Json(new List<Dict>()); });

app.MapPost("/api/regresion/correr", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    var cfg = configBase.Con(p.O("config"));
    List<Dict> casos;
    lock (regresion) casos = regresion.ToList();
    if (casos.Count == 0) return Http.Error(400, "El set de regresión está vacío. Generá casos y guardá los que fallen.");
    return await Intentar(async () =>
    {
        var m = await Pruebas.Medir(casos, cfg.Str("metodo"), cfg.D("umbral"), "Set de regresión");
        GuardarMedicion(m);
        return Http.Json(m);
    });
});

// ---------- ataques (slide 12) ----------
app.MapPost("/api/atacar", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    var ataque = ataques.FirstOrDefault(a => a.Str("id") == p.Str("id"));
    if (ataque == null) return Http.Error(404, "Ataque desconocido.");
    return await Intentar(async () => Http.Json(await Pruebas.Atacar(ataque, configBase.Con(p.O("config")))));
});

app.Run();
