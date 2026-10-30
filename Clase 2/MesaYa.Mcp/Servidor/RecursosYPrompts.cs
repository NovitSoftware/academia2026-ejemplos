using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MesaYa.Mcp.Servidor;

/// <summary>Recursos MCP: datos que el cliente puede leer como contexto, sin que el modelo "ejecute" nada.</summary>
[McpServerResourceType]
public sealed class RecursosMesaYa
{
    [McpServerResource(UriTemplate = "mesaya://sucursales", Name = "sucursales", Title = "Sucursales de MesaYa", MimeType = "application/json")]
    [Description("Códigos, nombres y horarios de atención de las sucursales.")]
    public static string Sucursales() => new JsonObject
    {
        ["sucursales"] = new JsonArray(HerramientasMesaYa.Sucursales
            .Select(s => (JsonNode)new JsonObject { ["sucursal_id"] = s.Key, ["nombre"] = s.Value }).ToArray()),
        ["horarios"] = "12:00–15:30 y 20:00–23:00 (-03:00)"
    }.ToJsonString();

    [McpServerResource(UriTemplate = "mesaya://menu/{sucursal_id}", Name = "menu_sucursal", Title = "Menú de una sucursal", MimeType = "application/json")]
    [Description("Menú de la sucursal indicada, leído en vivo desde MesaYa.Api.")]
    public static async Task<string> Menu(McpServer servidor, ApiMesaYa api, string sucursal_id, CancellationToken ct)
    {
        var ctx = new ContextoTool(servidor, NullProgreso.Instancia);
        var r = await api.Get($"/v1/menu?sucursal_id={Uri.EscapeDataString(sucursal_id)}", ctx, ct);
        return r.Cuerpo.ToJsonString();
    }

    [McpServerResource(UriTemplate = "mesaya://escenario", Name = "escenario", Title = "Escenario de caos activo", MimeType = "text/plain")]
    [Description("Qué demoras o fallas está simulando el servidor en este momento.")]
    public static string Escenario(Escenarios escenarios) => Escenarios.Describir(escenarios.Actual);

    sealed class NullProgreso : IProgress<ProgressNotificationValue>
    {
        public static readonly NullProgreso Instancia = new();
        public void Report(ProgressNotificationValue value) { }
    }
}

/// <summary>Prompts MCP: plantillas que el servidor ofrece para arrancar una conversación.</summary>
[McpServerPromptType]
public sealed class PromptsMesaYa
{
    [McpServerPrompt(Name = "pedir_reserva", Title = "Pedir una reserva")]
    [Description("Arma el pedido de reserva típico de un cliente de WhatsApp.")]
    public static string PedirReserva(
        [Description("Barrio o código de sucursal")] string sucursal = "Palermo",
        [Description("Cantidad de personas")] string personas = "4",
        [Description("Día y hora, en lenguaje natural")] string cuando = "el viernes a las 21") =>
        $"Hola! Quiero una mesa para {personas} {cuando} en {sucursal}. Mi cel es +5491155550123.";

    [McpServerPrompt(Name = "comparar_sucursales", Title = "Comparar sucursales")]
    [Description("Pide disponibilidad en todas las sucursales para una fecha (provoca llamadas en paralelo).")]
    public static string CompararSucursales([Description("Fecha AAAA-MM-DD o en lenguaje natural")] string fecha = "el sábado") =>
        $"¿Qué horarios libres hay {fecha} en Palermo, Belgrano y Caballito? Consultá las tres sucursales.";
}
