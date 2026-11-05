// MesaYa · Memoria del agente (Unidad 4).
using System.Collections.Concurrent;
using MesaYa.Comun;
using MesaYa.Memoria;
using Microsoft.Extensions.FileProviders;
using Mem = MesaYa.Memoria.Memoria;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

var estaticos = Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });

var ollama = Ollama.Instancia;
var catalogo = J.LeerObjeto(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "escenarios.json")));
var trazas = new ConcurrentDictionary<string, List<Dict>>();

var configBase = new Dict
{
    ["memoria"] = true, ["estrategia"] = "resumen", ["k"] = 4, ["escritura"] = "segundo_plano",
    ["filtrar_sensibles"] = true, ["validar_origen"] = true,
};

_ = Task.Run(ollama.Preparar);

Dict Vista(string usuario)
{
    var s = Agente.Sesion(usuario);
    List<Dict> lista;
    var propias = trazas.GetValueOrDefault(usuario) ?? [];
    lock (propias) lista = propias.ToList();
    return new Dict
    {
        ["usuario"] = usuario, ["sesion"] = s.G("n"), ["turnos"] = s.G("turnos"), ["estado"] = s.G("estado"),
        ["archivados"] = s.G("archivados"), ["resumen"] = s.G("resumen"), ["memoria"] = Mem.Listar(usuario),
        ["pendientes"] = Agente.Pendientes.GetValueOrDefault(usuario),
        ["reloj"] = new Dict { ["dias"] = Mem.RelojDias, ["hoy"] = Mem.Ahora().ToString("yyyy-MM-dd") },
        ["trazas"] = lista.Select((t, i) => new Dict { ["i"] = i, ["sesion"] = t.G("sesion"), ["escritura"] = t.G("escritura") }).ToList(),
    };
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
    ["escenarios"] = catalogo.G("escenarios"), ["usuarios"] = Agente.Usuarios, ["config"] = configBase,
}));

app.MapGet("/api/sesion/{usuario}", (string usuario) => Http.Json(Vista(usuario)));

app.MapPost("/api/turno", async (HttpRequest req) =>
{
    var t = await Http.Cuerpo(req);
    if (!(ollama.Listo() || ollama.ModoGuion))
        return Http.Error(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.");
    var usuario = t.Str("usuario");
    Dict traza;
    Func<Task>? fondo;
    try
    {
        (traza, fondo) = await Agente.Turno(usuario, t.Str("mensaje"), configBase.Con(t.O("config")));
    }
    catch (SinGrabacion e) { return Http.Error(404, e.Message); }
    catch (Exception e) { return Http.Error(502, $"{e.GetType().Name}: {e.Message}"); }
    var propias = trazas.GetOrAdd(usuario, _ => []);
    int indice;
    lock (propias)
    {
        propias.Add(traza);
        indice = propias.Count - 1;
    }
    // Se serializa antes de arrancar la escritura en segundo plano: la respuesta llega antes que el recuerdo.
    var cuerpo = J.Serializar(new Dict { ["traza"] = traza, ["indice"] = indice, ["vista"] = Vista(usuario) });
    if (fondo != null) _ = Task.Run(fondo);
    return Results.Content(cuerpo, "application/json; charset=utf-8");
});

app.MapPost("/api/sesion/nueva", async (HttpRequest req) =>
{
    var u = await Http.Cuerpo(req);
    Agente.NuevaSesion(u.Str("usuario"));
    return Http.Json(Vista(u.Str("usuario")));
});

// Borra memoria, sesiones y trazas de todos los usuarios y vuelve el reloj a hoy.
app.MapPost("/api/reiniciar", async (HttpRequest req) =>
{
    var u = await Http.Cuerpo(req);
    foreach (var usuario in Agente.Usuarios.Keys)
    {
        Agente.Reiniciar(usuario);
        trazas.TryRemove(usuario, out _);
    }
    Mem.RelojDias = 0;
    return Http.Json(Vista(u.Str("usuario")));
});

app.MapPost("/api/reloj", async (HttpRequest req) =>
{
    var r = await Http.Cuerpo(req);
    var dias = r.I("dias");  // 0 = volver a hoy; otro valor = avanzar
    Mem.RelojDias = dias == 0 ? 0 : Mem.RelojDias + dias;
    var vencidas = Mem.Vencer(r.Str("usuario"));
    return Http.Json(Vista(r.Str("usuario")).Con(new Dict { ["vencidas"] = vencidas }));
});

app.MapDelete("/api/memoria/{usuario}/{mid:long}", (string usuario, long mid) =>
{
    Mem.Olvidar(mid, "Borrada a mano desde el panel");
    return Http.Json(Vista(usuario));
});

app.MapPost("/api/precargar", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    var s = Agente.Sesion(p.Str("usuario"));
    var conversacion = (catalogo.O("conversaciones")?.G(p.Str("conversacion")) as List<object?> ?? []).OfType<List<object?>>();
    s["turnos"] = conversacion.Select(x => new Dict { ["rol"] = x[0], ["texto"] = x[1] }).ToList();
    s["resumen"] = null;
    s["resumen_hasta"] = 0;
    return Http.Json(Vista(p.Str("usuario")));
});

app.Run();
