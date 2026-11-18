using System.Text.RegularExpressions;
using MesaYa.Comun;
using static MesaYa.Hitl.Esquema;
using static MesaYa.Hitl.Traza;

namespace MesaYa.Hitl;

/// <summary>Lo que dura un turno: la traza, las respuestas, la ruta que tomó y los datos para la interfaz.</summary>
public sealed class Turno(Dict config)
{
    public Traza T { get; } = new(config);
    public Dict Config => T.Config;
    public string CorrelationId { get; } = "req-" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..6];
    public List<Dict> Respuestas { get; } = [];
    public string Ruta { get; set; } = "";
    public string Resultado { get; set; } = "";
    public Dict Extra { get; } = new();
    public void Decir(string de, string texto) => Respuestas.Add(new Dict { ["de"] = de, ["texto"] = texto });
}

/// <summary>
/// La entrada del sistema. Es un workflow híbrido (slide 4.2): el código decide el camino y solo los pasos
/// abiertos usan un agente.
///   imagen adjunta          → pipeline de documentos: visión → validar en código → persona si no cierra
///   confirmación pendiente  → el código ejecuta (o no) lo que el cliente confirmó
///   pide un reembolso       → workflow fijo: lectura → regla de aprobación en código → humano → reanudar
///   cualquier otra cosa     → agente con tools, con frenos (pasos, costo, tiempo) y detector de loops
/// </summary>
public sealed class Asistente(Almacen almacen, Sistema sistema)
{
    readonly Agente agente = new(sistema);
    readonly Reembolso reembolso = new(almacen, sistema);
    readonly Documentos documentos = new(almacen, sistema);

    static readonly Dict EsquemaRouter = Objeto(new Dict
    {
        ["intencion"] = Opciones("reembolso", "otra"),
        ["reserva"] = Tipo("string", "null"),
        ["monto"] = Tipo("integer", "null"),
    });

    const string SistemaRouter =
        "Clasificá el mensaje de un cliente de MesaYa, un restaurante.\n" +
        "- reembolso: pide que le devuelvan dinero (una seña o un pago).\n" +
        "- otra: cualquier otra cosa (reservar, cancelar, consultar).\n" +
        "Si aparecen, extraé el código de reserva (formato R-1234) y el monto en pesos como entero (\"$15.000\" → 15000). Si no aparecen, van null.";

    static readonly Regex Afirmativo = new(@"^\s*(s[ií]|dale|confirm|ok|de una|claro|por favor)", RegexOptions.IgnoreCase);

    public async Task<Dict> Mensaje(string texto, string? imagen, string? imagenNombre, Dict config)
    {
        var u = new Turno(config);
        var conv = almacen.Objeto("conversacion.json");
        var historial = conv.Ds("historial");

        if (imagen != null)
        {
            u.Ruta = "documento";
            u.T.Agregar(Paso("codigo", "Router · trae una imagen", "va al pipeline de documentos: el agente con tools no la ve", new Dict { ["imagen"] = imagenNombre }, "ok"), "Router");
            await documentos.Procesar(u, texto, imagen);
        }
        else if (conv.O("confirmacion") is { } pendiente)
        {
            u.Ruta = "confirmacion";
            conv["confirmacion"] = null;
            var si = Afirmativo.IsMatch(texto);
            u.T.Agregar(Paso("gate", $"Confirmación pendiente · cancelar {pendiente.Str("codigo")}",
                si ? "el cliente confirmó: el código ejecuta la acción" : "no es un sí: no se cancela nada", new Dict { ["mensaje"] = texto, ["confirma"] = si }, si ? "ok" : "aviso"), "Confirmación");
            if (si)
            {
                var r = sistema.Cancelar(pendiente.Str("codigo"));
                u.T.Agregar(Paso("tool", $"Ejecutar · cancelar_reserva({pendiente.Str("codigo")})", "nivel: con confirmación del mismo cliente", r, "ok"), "Confirmación");
                u.Decir("asistente", $"Listo: cancelé la reserva {pendiente.Str("codigo")}. Te llega la confirmación por mail.");
                u.Resultado = "respondida";
            }
            else
            {
                u.Decir("asistente", $"Perfecto, no cancelo nada: la reserva {pendiente.Str("codigo")} sigue en pie.");
                u.Resultado = "respondida";
            }
        }
        else
        {
            var (d, _, p) = await u.T.Llm("Router · clasificar y extraer", "¿pide un reembolso? código de reserva y monto", M(SistemaRouter, texto), EsquemaRouter, 120);
            d ??= new Dict();
            u.T.Agregar(p, "Router");
            if (d.Str("intencion") == "reembolso")
            {
                u.Ruta = "reembolso";
                var codigo = Regex.Match(texto, @"R-\d{4}") is { Success: true } m ? m.Value : d.S("reserva");
                await reembolso.Iniciar(u, texto, codigo, d.In("monto"));
            }
            else
            {
                u.Ruta = "agente";
                var confirmacion = await agente.Correr(u, historial, texto);
                if (confirmacion != null) conv["confirmacion"] = confirmacion;
            }
        }

        historial.Add(new Dict { ["role"] = "user", ["content"] = imagen != null ? $"{texto} [imagen adjunta]" : texto });
        foreach (var r in u.Respuestas) historial.Add(new Dict { ["role"] = "assistant", ["content"] = r.Str("texto") });
        conv["historial"] = historial;
        almacen.Escribir("conversacion.json", conv);
        return Resultado(u, "mensaje", texto, imagenNombre);
    }

    public async Task<Dict> Resolver(string id, bool aprobar, string quien, Dict config)
    {
        var u = new Turno(config);
        var aprobaciones = almacen.Lista("aprobaciones.json");
        var a = aprobaciones.FirstOrDefault(x => x.Str("id") == id) ?? throw new ErrorHttp(404, "No existe esa aprobación.");
        u.Ruta = "aprobacion";
        if (a.Str("tipo") == "factura") await documentos.Resolver(u, a, aprobar, quien);
        else await reembolso.Resolver(u, a, aprobar, quien);
        almacen.Escribir("aprobaciones.json", aprobaciones);
        var conv = almacen.Objeto("conversacion.json");
        var historial = conv.Ds("historial");
        foreach (var r in u.Respuestas) historial.Add(new Dict { ["role"] = "assistant", ["content"] = r.Str("texto") });
        conv["historial"] = historial;
        almacen.Escribir("conversacion.json", conv);
        return Resultado(u, aprobar ? "aprobar" : "rechazar", $"{quien}: {(aprobar ? "aprueba" : "rechaza")} · {a.Str("titulo")}", null);
    }

    Dict Resultado(Turno u, string accion, string? mensaje, string? imagen) => new()
    {
        ["accion"] = accion, ["mensaje"] = mensaje, ["imagen"] = imagen, ["respuestas"] = u.Respuestas, ["ruta"] = u.Ruta,
        ["resultado"] = u.Resultado, ["correlation_id"] = u.CorrelationId, ["extra"] = u.Extra,
        ["pasos"] = u.T.Pasos, ["llamadas"] = u.T.Llamadas, ["resumen"] = u.T.Resumen(), ["grabado"] = u.T.Grabado, ["config"] = u.Config,
    };
}
