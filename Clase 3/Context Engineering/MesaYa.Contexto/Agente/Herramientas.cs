using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesaYa.Contexto.Api;
using MesaYa.Contexto.Contexto;

namespace MesaYa.Contexto.Agente;

/// <summary>
/// Definiciones de las tools y su ejecución contra MesaYa.Api.
/// Acá se aplican tres decisiones de contexto que el modelo no ve:
/// densidad (qué campos vuelven), metadata de documentos y permisos (qué datos entran).
/// </summary>
public sealed class Herramientas(ApiMesaYa api)
{
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions JsonIndentado = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>Tools que pueden cambiar datos: piden confirmación humana fuera del modelo.</summary>
    public static bool EsAccion(string nombre) => nombre is "reservar_mesa" or "cancelar_reserva";

    /// <summary>Definiciones que se envían a Ollama. Con pre-carga, las de lectura no hacen falta.</summary>
    public static JsonArray Definiciones(OpcionesContexto o, bool clienteIdentificado)
    {
        var tools = new JsonArray
        {
            Tool("buscar_disponibilidad", "Devuelve los horarios libres de una sucursal para una fecha. No reserva nada.",
                """{"type":"object","properties":{"sucursal_id":{"type":"string","enum":["PAL","BEL","CAB"],"description":"PAL = Palermo, BEL = Belgrano, CAB = Caballito"},"fecha":{"type":"string","description":"Formato AAAA-MM-DD"}},"required":["sucursal_id","fecha"]}"""),
        };
        if (o.Estrategia == Estrategia.BajoDemanda)
        {
            tools.Add(Tool("consultar_menu", "Devuelve el menú de una sucursal con precios en pesos.",
                """{"type":"object","properties":{"sucursal_id":{"type":"string","enum":["PAL","BEL","CAB"]}},"required":["sucursal_id"]}"""));
            tools.Add(Tool("consultar_politicas", "Devuelve las políticas del restaurante, por ejemplo la de cancelación.",
                """{"type":"object","properties":{}}"""));
            tools.Add(Tool("ver_resenas", "Devuelve reseñas publicadas por clientes de una sucursal.",
                """{"type":"object","properties":{"sucursal_id":{"type":"string","enum":["PAL","BEL","CAB"]}},"required":["sucursal_id"]}"""));
        }
        if (clienteIdentificado)
            tools.Add(Tool("consultar_reservas", "Lista reservas registradas en MesaYa.",
                """{"type":"object","properties":{}}"""));
        tools.Add(Tool("reservar_mesa",
            "Crea una reserva real. Antes de llamarla, confirmá con el cliente sucursal, fecha, hora y personas.",
            """{"type":"object","properties":{"sucursal_id":{"type":"string","enum":["PAL","BEL","CAB"]},"fecha_hora":{"type":"string","description":"ISO 8601 con zona, ej. 2026-11-06T20:30:00-03:00"},"personas":{"type":"integer","minimum":1,"maximum":12}},"required":["sucursal_id","fecha_hora","personas"]}"""));
        tools.Add(Tool("cancelar_reserva", "Cancela una reserva existente a partir de su número (por ejemplo RSV-20481).",
            """{"type":"object","properties":{"reserva_id":{"type":"string","description":"Número de reserva, formato RSV-NNNNN"}},"required":["reserva_id"]}"""));
        return tools;
    }

    static JsonObject Tool(string nombre, string descripcion, string esquema) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = nombre, ["description"] = descripcion, ["parameters"] = JsonNode.Parse(esquema) }
    };

    /// <summary>Ejecuta la tool y devuelve el texto que va a entrar al contexto como resultado.</summary>
    public async Task<string> Ejecutar(LlamadaTool llamada, OpcionesContexto o, PerfilCliente? perfil)
    {
        var a = llamada.Argumentos;
        string Arg(string nombre) => a[nombre]?.ToString() ?? "";
        try
        {
            return llamada.Nombre switch
            {
                "buscar_disponibilidad" => Texto(await api.Disponibilidad(Arg("sucursal_id"), Arg("fecha"))),
                "consultar_menu" => Menu(await api.Menu(Arg("sucursal_id")), o),
                "consultar_politicas" => Politicas(await api.Politicas(), o),
                "ver_resenas" => Resenas(await api.Resenas(a["sucursal_id"]?.ToString()), o),
                "consultar_reservas" => await Reservas(o, perfil),
                "reservar_mesa" => await Reservar(a, o, perfil),
                "cancelar_reserva" => ReservaUnica(await api.Cancelar(Arg("reserva_id")), o),
                _ => Error("TOOL_INEXISTENTE", $"No existe la herramienta {llamada.Nombre}."),
            };
        }
        catch (HttpRequestException ex)
        {
            return Error("API_NO_DISPONIBLE", $"No se pudo consultar MesaYa.Api: {ex.Message}");
        }
    }

    // ---- Densidad: tres campos útiles, no un JSON entero (slide 05) --------------------------

    static string Menu(RespuestaApi r, OpcionesContexto o)
    {
        if (!r.Ok || !o.ResultadosCompactos) return Texto(r);
        var compacto = new JsonObject { ["sucursal"] = r.Cuerpo!["sucursal"]?.DeepClone(), ["moneda"] = "ARS" };
        foreach (var categoria in r.Cuerpo["categorias"]!.AsArray())
        {
            var platos = new JsonArray();
            foreach (var p in categoria!["platos"]!.AsArray())
                platos.Add(new JsonObject
                {
                    ["nombre"] = p!["nombre"]!.DeepClone(),
                    ["precio"] = p["precio"]!.DeepClone(),
                    ["vegetariano"] = p["vegetariano"]!.DeepClone()
                });
            compacto[categoria["categoria"]!.GetValue<string>()] = platos;
        }
        return compacto.ToJsonString(Json);
    }

    // ---- Autoridad y frescura: metadata visible o solo la versión vigente (slides 05 y 14) ----

    static string Politicas(RespuestaApi r, OpcionesContexto o)
    {
        if (!r.Ok) return Texto(r);
        var politicas = r.Cuerpo!["politicas"]!.AsArray().Select(p => p!).ToList();
        if (o.Documentos == ModoDocumentos.SinMetadata)
            // Lo que pasa cuando el RAG indexa solo el texto: no hay forma de saber cuál vale.
            return string.Join("\n\n", politicas.Select(p => $"{p["titulo"]}: {p["texto"]}"));

        if (o.Documentos == ModoDocumentos.SoloVigentes)
            politicas = politicas.Where(p => p["vigente"]!.GetValue<bool>()).ToList();

        if (o.Delimitar)
            return string.Join("\n", politicas.Select(p =>
                $"<documento id=\"{p["politica_id"]}\" version=\"{p["version"]}\" fecha=\"{p["fecha"]}\" vigente=\"{p["vigente"]!.ToJsonString()}\">{p["texto"]}</documento>"));
        return new JsonArray(politicas.Select(p => p.DeepClone()).ToArray()).ToJsonString(Json);
    }

    static string Resenas(RespuestaApi r, OpcionesContexto o)
    {
        if (!r.Ok) return Texto(r);
        var resenas = r.Cuerpo!["resenas"]!.AsArray();
        if (!o.Delimitar)
            // Sin delimitar, el texto de cada reseña queda suelto, mezclado con el resto del prompt.
            return string.Join("\n", resenas.Select(x => $"{x!["autor"]} ({x["estrellas"]}★): {x["texto"]}"));
        return string.Join("\n", resenas.Select(x =>
            $"<resena id=\"{x!["resena_id"]}\" autor=\"{x["autor"]}\" estrellas=\"{x["estrellas"]}\" fecha=\"{x["fecha"]}\">{x["texto"]}</resena>"));
    }

    // ---- Permisos: lo que el cliente no puede ver no entra al contexto (slide 05) --------------

    async Task<string> Reservas(OpcionesContexto o, PerfilCliente? perfil)
    {
        if (perfil is null) return Error("CLIENTE_NO_IDENTIFICADO", "El chat es anónimo: pedile al cliente el número de reserva.");
        // Sin filtro, el host usa el token de servicio tal cual: la API devuelve las reservas de todos.
        var r = await api.Reservas(o.FiltrarPermisos ? perfil.Telefono : null);
        if (!r.Ok) return Texto(r);
        if (!o.ResultadosCompactos) return Texto(r);
        var lista = new JsonArray();
        foreach (var x in r.Cuerpo!["reservas"]!.AsArray())
            lista.Add(new JsonObject
            {
                ["reserva_id"] = x!["reserva_id"]!.DeepClone(),
                ["cliente"] = x["cliente"]!.DeepClone(),
                ["sucursal"] = x["sucursal"]!.DeepClone(),
                ["fecha_hora"] = x["fecha_hora"]!.DeepClone(),
                ["personas"] = x["personas"]!.DeepClone(),
                ["estado"] = x["estado"]!.DeepClone(),
            });
        return new JsonObject { ["reservas"] = lista }.ToJsonString(Json);
    }

    async Task<string> Reservar(JsonObject a, OpcionesContexto o, PerfilCliente? perfil)
    {
        if (perfil is null) return Error("CLIENTE_NO_IDENTIFICADO", "Para reservar hace falta un cliente identificado.");
        var cuerpo = new JsonObject
        {
            ["sucursal_id"] = a["sucursal_id"]?.DeepClone(),
            ["fecha_hora"] = a["fecha_hora"]?.DeepClone(),
            ["personas"] = a["personas"]?.DeepClone(),
            ["telefono"] = perfil.Telefono,
            ["cliente"] = perfil.Nombre,
        };
        var clave = $"ctx-{perfil.Telefono}-{a["sucursal_id"]}-{a["fecha_hora"]}-{a["personas"]}";
        return ReservaUnica(await api.Reservar(cuerpo, clave), o);
    }

    static string ReservaUnica(RespuestaApi r, OpcionesContexto o)
    {
        if (!r.Ok || !o.ResultadosCompactos) return Texto(r);
        var x = r.Cuerpo!;
        return new JsonObject
        {
            ["reserva_id"] = x["reserva_id"]?.DeepClone(),
            ["estado"] = x["estado"]?.DeepClone(),
            ["sucursal"] = x["sucursal"]?.DeepClone(),
            ["fecha_hora"] = x["fecha_hora"]?.DeepClone(),
            ["personas"] = x["personas"]?.DeepClone(),
        }.ToJsonString(Json);
    }

    static string Texto(RespuestaApi r) =>
        r.Cuerpo?.ToJsonString(Json) ?? (r.Ok ? "{}" : Error("ERROR_HTTP", $"La API respondió {r.Status}."));

    static string Error(string codigo, string mensaje) =>
        new JsonObject { ["codigo"] = codigo, ["mensaje"] = mensaje }.ToJsonString(Json);
}
