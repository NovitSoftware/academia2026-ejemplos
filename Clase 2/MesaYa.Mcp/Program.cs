// MesaYa.Mcp — Servidor MCP + agente Blazor con modelos locales de Ollama.
//
//  ┌──────────── este proceso (http://localhost:5100) ─────────────┐
//  │  Blazor (UI)  →  SesionAgente  →  McpClient ──HTTP──┐          │
//  │                       │                            ▼          │
//  │                    Ollama                 /mcp  (servidor MCP) │──→ MesaYa.Api (5080)
//  └────────────────────────────────────────────────────────────────┘
//
// El servidor MCP en /mcp es un servidor MCP "de verdad": también lo pueden usar Claude,
// el MCP Inspector o cualquier otro cliente. Todo mensaje JSON-RPC queda en BusTraza.

using System.Globalization;
using MesaYa.Mcp.Agente;
using MesaYa.Mcp.Components;
using MesaYa.Mcp.Servidor;
using MesaYa.Mcp.Traza;
using ModelContextProtocol.Protocol;

// Fechas en castellano, pero números con punto decimal: las coordenadas del SVG no admiten "166,4".
var cultura = (CultureInfo)CultureInfo.GetCultureInfo("es-AR").Clone();
cultura.NumberFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = cultura;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<BusTraza>();
builder.Services.AddSingleton<Escenarios>();
builder.Services.AddScoped<SesionAgente>();
builder.Services.AddScoped<CatalogoOllama>();

builder.Services.AddHttpClient("ollama", http =>
{
    http.BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_URL") ?? config["Ollama:Url"] ?? "http://localhost:11434");
    http.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<ApiMesaYa>(http =>
{
    http.BaseAddress = new Uri(Environment.GetEnvironmentVariable("MESAYA_URL") ?? config["MesaYa:Url"] ?? "http://localhost:5080");
    http.Timeout = Timeout.InfiniteTimeSpan; // el timeout por intento lo maneja ApiMesaYa
});

// ---- Servidor MCP --------------------------------------------------------------------------
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "mesaya-mcp", Title = "MesaYa · servidor MCP de reservas", Version = "1.0.0" };
        o.ServerInstructions =
            "Usá listar_sucursales para traducir barrios a sucursal_id. Antes de reservar sin horario fijo, usá buscar_disponibilidad. " +
            "reservar_mesa pide confirmación al usuario por elicitation y es idempotente dentro de la sesión. " +
            "Los errores vuelven como JSON {codigo, mensaje, reintentable, detalle}.";
    })
    .WithHttpTransport(o => o.Stateless = false) // sesiones MCP: permiten elicitation y unir la traza por sesión
    .WithTools<HerramientasMesaYa>()
    .WithResources<RecursosMesaYa>()
    .WithPrompts<PromptsMesaYa>()
    .WithMessageFilters(f => f
        .AddIncomingFilter(FiltroTrazaMcp.Entrante)
        .AddOutgoingFilter(FiltroTrazaMcp.Saliente));

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();

app.MapMcp("/mcp");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
