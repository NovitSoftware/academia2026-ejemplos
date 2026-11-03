using MesaYa.Rag;
using MesaYa.Rag.Components;
using MesaYa.Rag.Busqueda;
using MesaYa.Rag.Espacio;
using MesaYa.Rag.Evaluacion;
using MesaYa.Rag.Generacion;
using MesaYa.Rag.Ingesta;
using MesaYa.Rag.Pipeline;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OpcionesRag>(builder.Configuration.GetSection("Rag"));
builder.Services.AddSingleton(_ =>
{
    var origen = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("Rag"));
    origen.UseVector();
    return origen.Build();
});
builder.Services.AddHttpClient<ClienteEmbeddings>((sp, http) =>
{
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpcionesRag>>().Value.OllamaUrl);
    http.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddSingleton<Ingestor>();
builder.Services.AddSingleton<Buscador>();
builder.Services.AddHttpClient<Generador>((sp, http) =>
{
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpcionesRag>>().Value.OllamaUrl);
    http.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<Reescritor>((sp, http) =>
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpcionesRag>>().Value.OllamaUrl));
builder.Services.AddHttpClient<Reranker>((sp, http) =>
{
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OpcionesRag>>().Value.RerankerUrl);
    http.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddTransient<PipelineRag>();
builder.Services.AddSingleton<Ejemplos>();
builder.Services.AddTransient<Evaluador>();
builder.Services.AddScoped<EstadoEvaluacion>();
builder.Services.AddSingleton<Proyector>();
builder.Services.AddSingleton<Guion>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Comandos de consola: ingestar | chunks <document_id> [estrategia] | buscar "pregunta" [opciones] | evaluar [--sin-rerank]
switch (args.FirstOrDefault())
{
    case "ingestar":
        return await Comandos.Ingestar(app.Services.GetRequiredService<Ingestor>());
    case "chunks":
        return Comandos.MostrarChunks(app.Services.GetRequiredService<Ingestor>(), args.Skip(1).ToArray());
    case "evaluar":
        return await Comandos.Evaluar(app.Services.GetRequiredService<Evaluador>(), args.Skip(1).ToArray());
    case "buscar":
        return await Comandos.Buscar(app.Services.GetRequiredService<Buscador>(), app.Services.GetRequiredService<Reranker>(), args.Skip(1).ToArray());
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
