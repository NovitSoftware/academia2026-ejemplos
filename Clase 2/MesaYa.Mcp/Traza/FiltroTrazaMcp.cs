using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MesaYa.Mcp.Traza;

/// <summary>
/// Filtros de mensajes del servidor MCP: ven pasar CADA mensaje JSON-RPC real, en ambos sentidos.
/// Entrante = cliente → servidor (requests, notificaciones, respuestas a una elicitation).
/// Saliente = servidor → cliente (respuestas, progreso, logs, pedidos de elicitation).
/// </summary>
public static class FiltroTrazaMcp
{
    public static McpMessageFilter Entrante => next => async (contexto, ct) =>
    {
        Registrar(contexto, entrante: true);
        await next(contexto, ct);
    };

    public static McpMessageFilter Saliente => next => async (contexto, ct) =>
    {
        Registrar(contexto, entrante: false);
        await next(contexto, ct);
    };

    static void Registrar(MessageContext contexto, bool entrante)
    {
        var bus = contexto.Services?.GetService<BusTraza>();
        if (bus is null) return;
        var mensaje = contexto.JsonRpcMessage;
        var sesion = contexto.Server.SessionId;
        var desde = entrante ? Actor.ClienteMcp : Actor.ServidorMcp;
        var hacia = entrante ? Actor.ServidorMcp : Actor.ClienteMcp;
        string detalle;
        try { detalle = JsonSerializer.Serialize(mensaje, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage))); }
        catch (Exception e) { detalle = $"(no se pudo serializar: {e.Message})"; }
        detalle = BusTraza.Indentar(detalle);

        // Las requests del cliente y las del servidor tienen numeraciones independientes.
        string Clave(RequestId id, bool delCliente) => $"rpc:{sesion}:{(delCliente ? "c" : "s")}:{id}";

        var evento = mensaje switch
        {
            JsonRpcRequest r => new EventoTraza
            {
                Tipo = TipoTraza.Request,
                Etiqueta = EtiquetaRequest(r),
                Correlacion = Clave(r.Id, delCliente: entrante)
            },
            JsonRpcResponse r => new EventoTraza
            {
                Tipo = TipoTraza.Response,
                Etiqueta = "",
                Correlacion = Clave(r.Id, delCliente: !entrante),
                Error = r.Result is { } res && res["isError"]?.GetValue<bool>() == true
            },
            JsonRpcError r => new EventoTraza
            {
                Tipo = TipoTraza.Response,
                Etiqueta = $"✖ error {r.Error.Code}: {r.Error.Message}",
                Correlacion = Clave(r.Id, delCliente: !entrante),
                Error = true
            },
            JsonRpcNotification n => new EventoTraza
            {
                Tipo = TipoTraza.Notificacion,
                Etiqueta = EtiquetaNotificacion(n)
            },
            _ => new EventoTraza { Tipo = TipoTraza.Nota, Etiqueta = mensaje.GetType().Name }
        };

        // Los clientes externos (VS Code, Inspector, Claude…) se nombran con el clientInfo del initialize.
        if (mensaje is JsonRpcRequest { Method: RequestMethods.Initialize } init && sesion is not null)
        {
            var info = init.Params?["clientInfo"];
            bus.Etiquetar($"mcp:{sesion}", $"{info?["title"] ?? info?["name"] ?? "Cliente MCP"} · sesión {sesion[..Math.Min(6, sesion.Length)]}");
        }

        bus.Publicar(evento with { SesionMcp = sesion, Desde = desde, Hacia = hacia, Detalle = detalle, EsMcp = true });
    }

    static string EtiquetaRequest(JsonRpcRequest r)
    {
        var p = r.Params;
        return r.Method switch
        {
            RequestMethods.ToolsCall => $"tools/call · {p?["name"]}",
            RequestMethods.ResourcesRead => $"resources/read · {p?["uri"]}",
            RequestMethods.PromptsGet => $"prompts/get · {p?["name"]}",
            RequestMethods.Initialize => $"initialize · {p?["clientInfo"]?["name"]} ({p?["protocolVersion"]})",
            _ => r.Method
        };
    }

    static string EtiquetaNotificacion(JsonRpcNotification n)
    {
        var p = n.Params;
        return n.Method switch
        {
            NotificationMethods.ProgressNotification => $"progress · {p?["message"] ?? p?["progress"]}",
            NotificationMethods.LoggingMessageNotification => $"log · {p?["data"]}",
            _ => n.Method
        };
    }
}
