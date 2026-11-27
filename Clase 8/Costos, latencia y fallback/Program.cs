// MesaYa · Costos, latencia, eficiencia y fallback (Módulo 4 · Unidad 4).
// Las trayectorias, los tiempos y los costos se simulan en el navegador (static/sim.js).
// Este servidor agrega el perfil Local que viene de la demo de Ejecución local y una prueba real
// de la caché de prefijo contra el Ollama de esta máquina.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

// Desde la carpeta del proyecto si existe (así un cambio en static/ se ve sin recompilar); si no, la copia del build.
var estaticos = Directory.Exists("static") ? Path.GetFullPath("static") : Path.Combine(AppContext.BaseDirectory, "static");
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(estaticos), RequestPath = "/static" });
app.MapGet("/", () => Results.File(Path.Combine(estaticos, "index.html"), "text/html; charset=utf-8"));

var ollama = (app.Configuration["Ollama:Url"] ?? "http://localhost:11434").TrimEnd('/');
var modeloCache = app.Configuration["Ollama:ModeloCache"] ?? "ministral-3:3b";
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };

// ---------- el perfil Local: lo guarda la demo de Ejecución local y edge ----------
app.MapGet("/api/perfil-local", () =>
{
    var archivo = app.Configuration["PerfilLocal:Archivo"] ?? "";
    if (File.Exists(archivo))
    {
        try
        {
            var p = JsonNode.Parse(File.ReadAllText(archivo))!.AsObject();
            p["fuente"] = "demo de Ejecución local y edge";
            return Results.Text(p.ToJsonString(), "application/json");
        }
        catch { /* archivo a medio escribir: se usa el de ejemplo */ }
    }
    var defecto = new JsonObject();
    foreach (var c in app.Configuration.GetSection("PerfilLocal:PorDefecto").GetChildren())
        defecto[c.Key] = double.TryParse(c.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : c.Value;
    defecto["fuente"] = "appsettings.json";
    return Results.Text(defecto.ToJsonString(), "application/json");
});

// ---------- estado de Ollama ----------
app.MapGet("/api/estado", async () =>
{
    try
    {
        var tags = JsonNode.Parse(await http.GetStringAsync(ollama + "/api/tags"));
        var instalado = (tags?["models"]?.AsArray() ?? []).Any(m => m?["name"]?.ToString() == modeloCache);
        return Results.Json(new { ollama = true, modelo = modeloCache, instalado });
    }
    catch { return Results.Json(new { ollama = false, modelo = modeloCache, instalado = false }); }
});

// ---------- prueba real de la caché de prefijo ----------
// Dos pedidos seguidos con la fecha arriba y dos con la fecha abajo. Ollama (llama.cpp) reutiliza el prefijo
// que ya procesó: se ve en el tiempo de lectura del prompt, no en prompt_eval_count.
var sistema = "Sos el asistente de reservas de MesaYa. " + string.Join(" ", Enumerable.Range(1, 90).Select(i =>
    $"Regla {i}: para grupos de {i % 12 + 2} personas en la sucursal {(i % 3 == 0 ? "Palermo" : i % 3 == 1 ? "Belgrano" : "Centro")} " +
    $"se pide una seña del {i % 4 * 10} % y se puede cancelar sin cargo hasta {24 + i % 3 * 12} horas antes."));

app.MapPost("/api/cache-ollama", async () =>
{
    async Task<object> Pedido(string orden, int n)
    {
        var fecha = $"Fecha: sábado 14/11 21:03:{DateTime.Now.Second:00}.{DateTime.Now.Millisecond:000}";
        var sis = orden == "arriba" ? fecha + "\n" + sistema : sistema;
        var usuario = orden == "arriba" ? "Mesa para 4 a las 22 en Palermo." : "Mesa para 4 a las 22 en Palermo.\n" + fecha;
        var cuerpo = new JsonObject
        {
            ["model"] = modeloCache, ["stream"] = false, ["think"] = false, ["keep_alive"] = "5m",
            ["options"] = new JsonObject { ["num_predict"] = 1, ["num_ctx"] = 8192 },
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = sis }, new JsonObject { ["role"] = "user", ["content"] = usuario } },
        };
        var reloj = Stopwatch.StartNew();
        var r = await http.PostAsync(ollama + "/api/chat", new StringContent(cuerpo.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        var d = JsonNode.Parse(await r.Content.ReadAsStringAsync());
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(d?["error"]?.ToString() ?? r.StatusCode.ToString());
        return new
        {
            orden, n,
            tokens = d?["prompt_eval_count"]?.GetValue<long>() ?? 0,
            lectura_s = (d?["prompt_eval_duration"]?.GetValue<long>() ?? 0) / 1e9,
            total_s = reloj.Elapsed.TotalSeconds,
        };
    }
    try
    {
        var resultados = new List<object>();
        // Un pedido de calentamiento carga el modelo para que la primera medición no incluya la carga desde el disco.
        await Pedido("arriba", 0);
        foreach (var orden in new[] { "arriba", "abajo" })
            for (var n = 1; n <= 2; n++) resultados.Add(await Pedido(orden, n));
        return Results.Json(new { modelo = modeloCache, resultados });
    }
    catch (Exception e) { return Results.Json(new { detail = e.Message }, statusCode: 502); }
});

app.Run();
