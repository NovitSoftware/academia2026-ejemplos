// MesaYa.Contexto — Clase 3 · Context Engineering.
//
//  Navegador ─ Blazor ─ SesionDemo ─ ConstructorContexto ──▶ Ollama :11434 (/api/chat, num_ctx)
//                            │
//                            └── Herramientas ──HTTP──▶ MesaYa.Api :5180
//
// Mismo agente de reservas de la Clase 2, sin MCP: las tools se definen acá para que quede
// a la vista dónde y cómo se arma la ventana que recibe el modelo en cada llamada.

using System.Globalization;
using MesaYa.Contexto.Agente;
using MesaYa.Contexto.Api;
using MesaYa.Contexto.Components;
using MesaYa.Contexto.Ollama;

var cultura = CultureInfo.GetCultureInfo("es-AR");
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = cultura;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddHttpClient("ollama", http =>
{
    http.BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_URL") ?? config["Ollama:Url"] ?? "http://localhost:11434");
    http.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<ApiMesaYa>(http =>
{
    http.BaseAddress = new Uri(Environment.GetEnvironmentVariable("MESAYA_URL") ?? config["MesaYa:Url"] ?? "http://localhost:5180");
    http.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddSingleton<ClienteOllama>();
builder.Services.AddScoped<Herramientas>();
builder.Services.AddScoped<Compactador>();
builder.Services.AddScoped<SesionDemo>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Ensayo sin UI: corre un escenario completo y devuelve las métricas. Las acciones se rechazan solas.
// Ejemplo: curl http://localhost:5190/ensayo/olvido/sin   (opcional: ?modelo=gemma4:e2b)
app.MapGet("/ensayo/{escenario}/{variante}", async (string escenario, string variante, string? modelo, IServiceProvider servicios) =>
{
    var e = MesaYa.Contexto.Escenarios.Catalogo.Todos().FirstOrDefault(x => x.Id == escenario);
    var v = e?.Variantes.FirstOrDefault(x => x.Id == variante);
    if (e is null || v is null) return Results.NotFound(new { error = "Escenario o variante inexistente." });

    using var scope = servicios.CreateScope();
    var sesion = scope.ServiceProvider.GetRequiredService<SesionDemo>();
    sesion.RespuestaAutomatica = false;
    if (!string.IsNullOrWhiteSpace(modelo)) sesion.Opciones.Modelo = modelo;
    await sesion.Correr(e, v);
    return Results.Ok(new
    {
        escenario = e.Titulo,
        variante = v.Etiqueta,
        sesion.Error,
        resultado = sesion.Resultados.Values.FirstOrDefault(),
        acciones = sesion.AccionesSolicitadas,
        llamadas = sesion.Llamadas.Select(l => new
        {
            l.Numero, estimado = l.Ventana.Estimado, real = l.TokensReales, ventana = l.Ventana.NumCtx,
            desborda = l.Ventana.Desborda, tools = l.Llamadas.Select(t => t.ToString()), texto = l.Texto,
            compactacion = l.Compactacion is null ? null : new
            {
                l.Compactacion.TokensAntes, l.Compactacion.TokensDespues, l.Compactacion.MensajesResumidos,
                l.Compactacion.IdsRescatados, l.Compactacion.IdsPerdidos, l.Compactacion.Resumen
            }
        }),
        calibracion = sesion.Constructor.Calibracion
    });
});
app.Run();
