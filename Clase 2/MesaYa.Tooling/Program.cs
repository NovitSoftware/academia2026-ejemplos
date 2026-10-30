using MesaYa.Tooling.Components;
using MesaYa.Tooling.Services;
using MesaYa.Agent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<SesionAgente>();
builder.Services.AddHttpClient<EstadoApi>((servicios, http) =>
{
    var configuracion = servicios.GetRequiredService<IConfiguration>();
    http.BaseAddress = new Uri(Environment.GetEnvironmentVariable("MESAYA_URL")
        ?? configuracion["MesaYa:Url"] ?? "http://localhost:5080");
    http.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddHttpClient<CatalogoOllama>((servicios, http) =>
{
    var configuracion = servicios.GetRequiredService<IConfiguration>();
    var ollama = configuracion.GetSection("Ollama").Get<ConfiguracionOllama>() ?? new ConfiguracionOllama();
    http.BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_URL") ?? ollama.Url);
    http.Timeout = TimeSpan.FromSeconds(10);
});
var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
