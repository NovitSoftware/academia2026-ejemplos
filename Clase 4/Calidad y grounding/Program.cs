// MesaYa · Laboratorio de calidad (Unidad 3: calidad, grounding y criterios de uso).
using MesaYa.Calidad;
using MesaYa.Comun;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

var estaticos = Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });

var ollama = Ollama.Instancia;

// Al iniciar: esperar a Ollama, descargar modelos que falten e indexar el corpus, en segundo plano.
_ = Task.Run(Laboratorio.Preparar);

app.MapGet("/", () => Results.File(Path.Combine(estaticos, "index.html"), "text/html; charset=utf-8"));

app.MapGet("/api/estado", async () =>
{
    if (ollama.Conectado)
    {
        try { await ollama.Listar(); }
        catch { ollama.Conectado = false; }
    }
    return Http.Json(ollama.Estado().Con(new Dict { ["indice"] = Laboratorio.EstadoIndice }));
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
    ["escenarios"] = Laboratorio.Escenarios, ["preguntas"] = Laboratorio.Preguntas.G("preguntas"),
}));

app.MapPost("/api/turno", async (HttpRequest req) =>
{
    var t = await Http.Cuerpo(req);
    try
    {
        return Http.Json(await Laboratorio.CorrerTurno(t.Str("pregunta"), t.O("config") ?? new Dict(), t.S("pregunta_id"), t.Ds("historial")));
    }
    catch (SinGrabacion e) { return Http.Error(404, e.Message); }
    catch (ErrorHttp e) { return Http.Error(e.Codigo, e.Message); }
    catch (Exception e) { return Http.Error(502, $"{e.GetType().Name}: {e.Message}"); }
});

app.MapPost("/api/evaluar/recuperacion", async (HttpRequest req) =>
{
    var p = await Http.Cuerpo(req);
    try { return Http.Json(await Laboratorio.EvaluarRecuperacion(p.I("k", 4))); }
    catch (Exception e) { return Http.Error(500, e.Message); }
});

// Una línea JSON por pregunta (NDJSON), a medida que se evalúan.
app.MapPost("/api/evaluar/respuestas", async (HttpContext ctx) =>
{
    var p = await Http.Cuerpo(ctx.Request);
    ctx.Response.ContentType = "application/x-ndjson";
    await foreach (var fila in Laboratorio.EvaluarRespuestas(p.O("config") ?? new Dict()))
    {
        await ctx.Response.WriteAsync(J.Serializar(fila) + "\n");
        await ctx.Response.Body.FlushAsync();
    }
});

app.Run();
