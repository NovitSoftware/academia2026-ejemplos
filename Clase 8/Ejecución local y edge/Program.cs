// MesaYa · Ejecución local y edge (Módulo 4 · Unidad 3).
// El banco, la tablet y la cuenta se simulan en el navegador (static/sim.js).
// Este servidor solo agrega lo que necesita un Ollama real: medir un modelo instalado,
// el modo real de la tablet y el perfil que toma la demo de Costos.
using System.Diagnostics;
using System.Text.Json;
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
var modeloMozo = app.Configuration["Ollama:ModeloMozo"] ?? "qwen3.5:2b";
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };

async Task<JsonNode?> Get(string ruta) => JsonNode.Parse(await http.GetStringAsync(ollama + ruta));
async Task<JsonNode?> Post(string ruta, JsonObject cuerpo)
{
    var r = await http.PostAsync(ollama + ruta, new StringContent(cuerpo.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
    var texto = await r.Content.ReadAsStringAsync();
    if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama respondió {(int)r.StatusCode}: {texto}");
    return JsonNode.Parse(texto);
}
static double Seg(JsonNode? ns) => (ns?.GetValue<long>() ?? 0) / 1e9;
static IResult Error(string detalle, int estado = 502) => Results.Json(new { detail = detalle }, statusCode: estado);

// ---------- estado: Ollama, modelos instalados y cargados ----------
app.MapGet("/api/estado", async () =>
{
    try
    {
        var tags = await Get("/api/tags");
        var ps = await Get("/api/ps");
        var instalados = (tags?["models"]?.AsArray() ?? []).Select(m => new
        {
            nombre = m?["name"]?.ToString(),
            bytes = m?["size"]?.GetValue<long>() ?? 0,
            parametros = m?["details"]?["parameter_size"]?.ToString(),
            cuantizacion = m?["details"]?["quantization_level"]?.ToString(),
            familia = m?["details"]?["family"]?.ToString(),
        }).Where(m => m.familia is not ("bert" or "nomic-bert")).OrderBy(m => m.bytes).ToList();
        var cargados = (ps?["models"]?.AsArray() ?? []).Select(m => new
        {
            nombre = m?["name"]?.ToString(),
            bytes = m?["size"]?.GetValue<long>() ?? 0,
            vram = m?["size_vram"]?.GetValue<long>() ?? 0,
            contexto = m?["context_length"]?.GetValue<long>() ?? 0,
        }).ToList();
        return Results.Json(new { ollama = new { conectado = true, url = ollama, instalados, cargados }, modelo_mozo = modeloMozo });
    }
    catch (Exception e)
    {
        return Results.Json(new { ollama = new { conectado = false, url = ollama, error = e.Message }, modelo_mozo = modeloMozo });
    }
});

// ---------- medir un modelo instalado: lo mismo que `ollama run --verbose` ----------
const string PromptMedicion =
    "Sos el asistente de MesaYa, una app de reservas de restaurantes. Explicá en un párrafo, para un mozo que recién empieza, " +
    "cómo funciona la política de señas: los grupos de hasta 6 personas no dejan seña; de 7 a 12 dejan el 20 %; más de 12, el 30 %. " +
    "La seña se devuelve completa si cancelan con 48 horas o más, la mitad entre 24 y 48 horas, y nada con menos de 24 horas. " +
    "Los sábados a la noche la política es la misma, pero las reservas de más de 8 personas necesitan confirmar por WhatsApp el día anterior. " +
    "Si el cliente es habitual (más de 5 visitas en el año) se le puede perdonar la seña una vez. Terminá con un ejemplo con números.";

app.MapPost("/api/medir", async (JsonObject p) =>
{
    var modelo = p["modelo"]?.ToString();
    if (string.IsNullOrWhiteSpace(modelo)) return Error("Falta el modelo.", 400);
    try
    {
        var reloj = Stopwatch.StartNew();
        var r = await Post("/api/generate", new JsonObject
        {
            ["model"] = modelo, ["prompt"] = PromptMedicion, ["stream"] = false, ["think"] = false, ["keep_alive"] = "5m",
            ["options"] = new JsonObject { ["num_predict"] = 220, ["temperature"] = 0.2 },
        });
        var total = reloj.Elapsed.TotalSeconds;
        var ps = await Get("/api/ps");
        var cargado = (ps?["models"]?.AsArray() ?? []).FirstOrDefault(m => m?["name"]?.ToString() == modelo);
        double pe = Seg(r?["prompt_eval_duration"]), ev = Seg(r?["eval_duration"]);
        long pc = r?["prompt_eval_count"]?.GetValue<long>() ?? 0, ec = r?["eval_count"]?.GetValue<long>() ?? 0;
        return Results.Json(new
        {
            modelo,
            total_s = total,
            carga_s = Seg(r?["load_duration"]),
            prompt_eval_count = pc, prompt_eval_s = pe, prompt_eval_rate = pe > 0 ? pc / pe : 0,
            eval_count = ec, eval_s = ev, eval_rate = ev > 0 ? ec / ev : 0,
            bytes = cargado?["size"]?.GetValue<long>() ?? 0,
            vram = cargado?["size_vram"]?.GetValue<long>() ?? 0,
            contexto = cargado?["context_length"]?.GetValue<long>() ?? 0,
            texto = r?["response"]?.ToString(),
        });
    }
    catch (Exception e) { return Error(e.Message); }
});

// ---------- la tablet del mozo en modo real: un modelo chico con la carta en el contexto ----------
app.MapPost("/api/mozo", async (JsonObject p) =>
{
    var mensaje = p["mensaje"]?.ToString() ?? "";
    var carta = p["carta"]?.ToString() ?? "";
    try
    {
        var reloj = Stopwatch.StartNew();
        var r = await Post("/api/chat", new JsonObject
        {
            ["model"] = modeloMozo, ["stream"] = false, ["think"] = false, ["keep_alive"] = "10m",
            ["options"] = new JsonObject { ["num_predict"] = 160, ["temperature"] = 0.1 },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] =
                    "Sos el asistente de la tablet de un mozo de MesaYa. Respondé en español rioplatense, en una o dos oraciones, " +
                    "usando solo la carta de abajo. Si algo no está en la carta, decí que no figura y que consulte en cocina.\n\nCARTA\n" + carta },
                new JsonObject { ["role"] = "user", ["content"] = mensaje },
            },
        });
        double ev = Seg(r?["eval_duration"]);
        long ec = r?["eval_count"]?.GetValue<long>() ?? 0;
        return Results.Json(new
        {
            modelo = modeloMozo,
            texto = r?["message"]?["content"]?.ToString()?.Trim(),
            ms = reloj.ElapsedMilliseconds,
            tokens_entrada = r?["prompt_eval_count"]?.GetValue<long>() ?? 0,
            tokens_salida = ec,
            tokens_s = ev > 0 ? ec / ev : 0,
        });
    }
    catch (Exception e) { return Error(e.Message); }
});

// ---------- el perfil local que usa la demo de Costos ----------
var carpeta = Environment.GetEnvironmentVariable("DATOS") ?? "datos";
var archivoPerfil = Path.Combine(carpeta, "perfil-local.json");
app.MapGet("/api/perfil-local", () => File.Exists(archivoPerfil)
    ? Results.Text(File.ReadAllText(archivoPerfil), "application/json") : Results.Json(new { }));
app.MapPost("/api/perfil-local", (JsonObject p) =>
{
    p["guardado"] = DateTime.Now.ToString("s");
    Directory.CreateDirectory(carpeta);
    File.WriteAllText(archivoPerfil, p.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return Results.Json(p);
});

app.Run();
