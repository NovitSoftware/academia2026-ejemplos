using System.Text.RegularExpressions;
using MesaYa.Comun;

namespace MesaYa.Orquestacion;

/// <summary>
/// Registro de una corrida: los pasos (llamadas al LLM, gates, tools y código) y un reloj virtual.
///
/// El reloj avanza con la duración de cada paso. Un grupo en paralelo avanza lo que tarda el más lento;
/// en secuencia, la suma. Así la línea de tiempo se ve igual en vivo y en modo guion.
/// </summary>
public sealed class Traza(Dict config)
{
    public Dict Config { get; } = config;
    public List<Dict> Pasos { get; } = [];
    public List<Dict> Llamadas { get; } = [];
    public string? Grabado { get; set; }
    long cursor;

    public bool Paralelo => Config.B("paralelo");
    public long Total => cursor;

    static long Ms(Dict p) => (long)(J.Num(p.G("ms")) ?? 0);

    public Dict Agregar(Dict paso, string? grupo = null)
    {
        paso["inicio"] = cursor;
        if (grupo != null) paso["grupo"] = grupo;
        cursor += Ms(paso);
        Pasos.Add(paso);
        return paso;
    }

    public void AgregarGrupo(IEnumerable<Dict> pasos, string grupo, bool paralelo)
    {
        long inicio = cursor, fin = cursor;
        foreach (var p in pasos)
        {
            p["grupo"] = grupo;
            p["paralelo"] = paralelo;
            p["inicio"] = paralelo ? inicio : cursor;
            if (!paralelo) cursor += Ms(p);
            fin = Math.Max(fin, (long)p["inicio"]! + Ms(p));
            Pasos.Add(p);
        }
        cursor = paralelo ? fin : cursor;
    }
}

public static class Patrones
{
    public static readonly List<Dict> Catalogo =
    [
        Patron("chaining", "Prompt chaining", "chain", "Pasos fijos en secuencia, con un gate en código entre medio.", "1.3"),
        Patron("routing", "Routing", "route", "Un router clasifica y deriva a un camino especializado.", "1.3"),
        Patron("sectioning", "Parallelization · sectioning", "parallel", "Dos llamadas a la vez, cada una con una parte distinta.", "1.3"),
        Patron("voting", "Parallelization · voting", "vote", "La misma pregunta tres veces; decide la mayoría.", "1.3"),
        Patron("orquestador", "Orchestrator-workers", "sitemap", "Un orquestador decide las subtareas y las reparte.", "1.3"),
        Patron("evaluador", "Evaluator-optimizer", "loop", "Genera, evalúa con criterios y corrige hasta aprobar.", "1.3"),
        Patron("agente", "Agente", "bot", "El modelo decide cada paso en un loop con herramientas.", "1.1"),
    ];

    static Dict Patron(string id, string nombre, string icono, string resumen, string slide) =>
        new() { ["id"] = id, ["nombre"] = nombre, ["icono"] = icono, ["resumen"] = resumen, ["slide"] = slide };

    public static Task<Dict> Correr(string patron, string mensaje, Dict config) => patron switch
    {
        "chaining" => Chaining(mensaje, config),
        "routing" => Routing(mensaje, config),
        "sectioning" => Sectioning(mensaje, config),
        "voting" => Voting(mensaje, config),
        "orquestador" => Orquestador(mensaje, config),
        "evaluador" => Evaluador(mensaje, config),
        "agente" => Agente(mensaje, config),
        _ => throw new ErrorHttp(400, $"Patrón desconocido: {patron}"),
    };

    // =====================================================================
    // Piezas comunes
    // =====================================================================
    static List<Dict> M(string sistema, string usuario) =>
        [new Dict { ["role"] = "system", ["content"] = sistema }, new Dict { ["role"] = "user", ["content"] = usuario }];

    static List<object?> Lista(params object?[] x) => [.. x];
    static Dict Tipo(params object?[] tipos) => new() { ["type"] = tipos.Length == 1 ? tipos[0] : Lista(tipos) };
    static Dict Opciones(params object?[] valores) =>
        new() { ["type"] = valores.Contains(null) ? Lista("string", "null") : "string", ["enum"] = Lista(valores) };
    static Dict Objeto(Dict propiedades) =>
        new() { ["type"] = "object", ["properties"] = propiedades, ["required"] = Lista([.. propiedades.Keys]) };

    static Dict Paso(string tipo, string titulo, string detalle, object? salida = null, string? marca = null) => new()
    {
        ["tipo"] = tipo, ["titulo"] = titulo, ["detalle"] = detalle, ["salida"] = salida, ["marca"] = marca, ["ms"] = 0L, ["tokens"] = 0L,
    };

    /// <summary>Una llamada al LLM. Devuelve el paso listo para agregar a la traza (no lo agrega: lo decide el patrón).</summary>
    static async Task<(Dict datos, string texto, Dict paso)> Llm(Traza t, string titulo, string detalle, List<Dict> mensajes,
        Dict? esquema = null, int maxTokens = 300, double temperatura = 0.0)
    {
        var r = await Ollama.Instancia.Chat(mensajes, esquema, temperatura, maxTokens: maxTokens);
        var texto = r.Str("texto").Trim();
        var datos = new Dict();
        if (esquema != null)
        {
            try { datos = J.Leer(texto) as Dict ?? new Dict(); }
            catch (System.Text.Json.JsonException) { }
        }
        var tokens = (long)(r.D("tokens_prompt") + r.D("tokens_respuesta"));
        int indice;
        lock (t.Llamadas)
        {
            indice = t.Llamadas.Count;
            t.Llamadas.Add(new Dict
            {
                ["titulo"] = titulo, ["mensajes"] = mensajes, ["esquema"] = esquema != null, ["salida"] = texto,
                ["tokens_prompt"] = r.G("tokens_prompt"), ["tokens_respuesta"] = r.G("tokens_respuesta"), ["ms"] = r.G("ms"),
            });
        }
        if (r.S("grabado") is { } g) t.Grabado = g;
        var paso = Paso("llm", titulo, detalle, esquema != null ? datos : texto);
        paso["ms"] = (long)r.D("ms");
        paso["tokens"] = tokens;
        paso["llamada"] = indice;
        return (datos, texto, paso);
    }

    /// <summary>Corre las tareas en paralelo o una detrás de otra, según la configuración.</summary>
    static async Task<List<T>> Varias<T>(Traza t, IEnumerable<Func<Task<T>>> tareas)
    {
        if (t.Paralelo) return [.. await Task.WhenAll(tareas.Select(f => f()))];
        var salida = new List<T>();
        foreach (var f in tareas) salida.Add(await f());
        return salida;
    }

    static Dict Resultado(string patron, string mensaje, Traza t, string respuesta, Dict? extra = null)
    {
        var llm = t.Pasos.Where(p => p.Str("tipo") == "llm").ToList();
        return new Dict
        {
            ["patron"] = patron, ["mensaje"] = mensaje, ["respuesta"] = respuesta, ["pasos"] = t.Pasos, ["llamadas"] = t.Llamadas,
            ["config"] = t.Config, ["grabado"] = t.Grabado, ["extra"] = extra ?? new Dict(),
            ["resumen"] = new Dict
            {
                ["llamadas"] = (long)t.Llamadas.Count,
                ["tokens"] = llm.Sum(p => (long)p.D("tokens")),
                ["ms_total"] = t.Total,
                ["ms_llm"] = llm.Sum(p => (long)p.D("ms")),
                ["pasos"] = (long)t.Pasos.Count,
            },
        };
    }

    // =====================================================================
    // 1. Prompt chaining: extraer → gate → consultar → (crear) → redactar
    // =====================================================================
    static readonly Dict EsquemaReserva = Objeto(new Dict
    {
        ["sucursal"] = Opciones("Centro", "Pocitos", "Carrasco", "no dice"),
        ["dia"] = Opciones("lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo", "no dice"),
        ["hora"] = Tipo("string"),
        ["personas"] = Tipo("integer"),
    });

    static string? Dato(string? x) => x is null or "no dice" or "ninguno" or "ninguna" or "" ? null : x;

    static async Task<Dict> Chaining(string mensaje, Dict config)
    {
        var t = new Traza(config);
        var respuesta = await PasosReserva(t, mensaje, null);
        return Resultado("chaining", mensaje, t, respuesta);
    }

    /// <summary>La cadena de reserva. Se reusa dentro del routing (un patrón dentro de otro).</summary>
    static async Task<string> PasosReserva(Traza t, string mensaje, string? grupo)
    {
        var (d, _, extraer) = await Llm(t, "1 · Extraer datos", "fecha, hora, personas y sucursal del mensaje",
            M("Extraé los datos de un pedido de reserva de un restaurante. Si el mensaje no menciona un dato, usá \"no dice\" " +
              "(o 0 para personas): no lo adivines. " +
              "La hora va en formato HH:MM de 24 horas (\"a las 9 de la noche\" = 21:00). El día va como día de la semana en minúscula.",
              mensaje), EsquemaReserva, maxTokens: 80);
        t.Agregar(extraer, grupo);

        // "no dice" y 0 son la forma de decir "falta": los modelos chicos abusan de null si el esquema lo permite.
        var sucursal = Dato(d.S("sucursal"));
        var dia = Dato(d.S("dia"));
        var personas = J.Num(d.G("personas")) is double p and > 0 ? (long?)p : null;
        var hora = d.S("hora") is { } h && Regex.IsMatch(h, @"^\d{1,2}:\d{2}$") ? h : null;
        var faltan = new List<string>();
        if (sucursal == null) faltan.Add("la sucursal");
        if (dia == null) faltan.Add("el día");
        if (hora == null) faltan.Add("la hora");
        if (personas == null) faltan.Add("la cantidad de personas");
        if (faltan.Count > 0)
        {
            var pregunta = $"¡Con gusto! Para hacer la reserva me falta {Unir(faltan)}. ¿Me lo pasás?";
            t.Agregar(Paso("gate", "Gate · ¿están los 4 datos?", $"falta {Unir(faltan)}: la cadena se corta y se repregunta", pregunta, "corta"), grupo);
            return pregunta;
        }
        t.Agregar(Paso("gate", "Gate · ¿están los 4 datos?", "sí: sigue la cadena", null, "ok"), grupo);

        var disp = Datos.Disponibilidad(sucursal, dia, hora, personas);
        t.Agregar(Paso("tool", "2 · consultar_disponibilidad()", $"{sucursal}, {dia} {hora}, {personas} personas", disp,
            disp.B("disponible") ? "ok" : "corta"), grupo);
        var resultado = disp;
        if (disp.B("disponible"))
        {
            resultado = Datos.CrearReserva(sucursal, dia, hora, personas);
            t.Agregar(Paso("tool", "3 · crear_reserva()", "el código decide crearla: hay lugar", resultado, "ok"), grupo);
        }

        var (_, texto, redactar) = await Llm(t, "4 · Redactar respuesta", "convierte el resultado del sistema en un mensaje",
            M("Sos el asistente de reservas de MesaYa. Respondé al cliente en 2 o 3 oraciones, en español rioplatense y con tono cálido. " +
              "Usá solo los datos del sistema: no inventes horarios, códigos ni promociones, y no prometas mails, comprobantes ni nada que el sistema no haya hecho.",
              $"Mensaje del cliente: {mensaje}\nResultado del sistema: {J.Py(resultado)}"), maxTokens: 160);
        t.Agregar(redactar, grupo);
        return texto;
    }

    static string Unir(List<string> x) => x.Count == 1 ? x[0] : string.Join(", ", x[..^1]) + " y " + x[^1];

    // =====================================================================
    // 2. Routing: clasificar → un camino especializado
    // =====================================================================
    static readonly List<Dict> Rutas =
    [
        new() { ["id"] = "reserva", ["nombre"] = "Reserva", ["como"] = "cadena de reserva (chaining)", ["icono"] = "chain" },
        new() { ["id"] = "alergenos", ["nombre"] = "Alérgenos", ["como"] = "LLM + carta con alérgenos", ["icono"] = "shield" },
        new() { ["id"] = "cancelacion", ["nombre"] = "Cancelación", ["como"] = "LLM + política v3", ["icono"] = "filecheck" },
        new() { ["id"] = "sucursales", ["nombre"] = "Sucursales", ["como"] = "LLM corto + ficha de sucursales", ["icono"] = "globe" },
        new() { ["id"] = "otro", ["nombre"] = "Otro", ["como"] = "respuesta estándar, sin LLM", ["icono"] = "ban" },
    ];

    static readonly Dict EsquemaRouter = Objeto(new Dict
    {
        ["intencion"] = Opciones("reserva", "alergenos", "cancelacion", "sucursales", "otro"),
        ["confianza"] = Tipo("number"),
    });

    static async Task<Dict> Routing(string mensaje, Dict config)
    {
        var t = new Traza(config);
        var (d, _, router) = await Llm(t, "Router", "clasifica la intención del mensaje",
            M("Clasificá el mensaje de un cliente de un restaurante en UNA intención:\n" +
              "- reserva: quiere reservar una mesa.\n- alergenos: pregunta por ingredientes, alergias, celiaquía, opciones veganas o vegetarianas.\n" +
              "- cancelacion: quiere cancelar una reserva o pregunta por la seña o la política de cancelación.\n" +
              "- sucursales: pregunta por direcciones, horarios, sectores o capacidad.\n- otro: cualquier otra cosa.\n" +
              "La confianza va de 0 a 1.", mensaje), EsquemaRouter, maxTokens: 40);
        t.Agregar(router);

        var ruta = d.S("intencion") is { } i && Rutas.Any(r => r.Str("id") == i) ? i : "otro";
        var grupo = "Ruta · " + Rutas.First(r => r.Str("id") == ruta).Str("nombre");
        string respuesta;
        switch (ruta)
        {
            case "reserva":
                respuesta = await PasosReserva(t, mensaje, grupo);
                break;
            case "otro":
                respuesta = "Puedo ayudarte con reservas, la carta y los alérgenos, cancelaciones y datos de las sucursales. ¿En qué te ayudo?";
                t.Agregar(Paso("codigo", "Respuesta estándar", "fuera de dominio: no se gasta una llamada al LLM", respuesta, "ok"), grupo);
                break;
            default:
                var (doc, sistema, max) = ruta switch
                {
                    "alergenos" => (Datos.Alergenos, "Sos el especialista en carta y alérgenos de MesaYa. Respondé en 2 a 4 oraciones usando solo la carta.", 220),
                    "cancelacion" => (Datos.Politica, "Sos el especialista en cancelaciones de MesaYa. Respondé en 2 a 4 oraciones aplicando la política vigente.", 220),
                    _ => (Datos.Ficha, "Sos el asistente de MesaYa. Respondé en 1 o 2 oraciones, breve.", 110),
                };
                var (_, texto, especialista) = await Llm(t, "Especialista · " + Rutas.First(r => r.Str("id") == ruta).Str("nombre"),
                    "prompt corto y solo el documento de su tema", M($"{sistema}\n\n{doc}", mensaje), maxTokens: max);
                t.Agregar(especialista, grupo);
                respuesta = texto;
                break;
        }
        return Resultado("routing", mensaje, t, respuesta, new Dict { ["rutas"] = Rutas, ["ruta"] = ruta, ["confianza"] = d.G("confianza") });
    }

    // =====================================================================
    // 3. Parallelization · sectioning: responder ‖ guardia → combinar
    // =====================================================================
    static readonly Dict EsquemaGuardia = Objeto(new Dict
    {
        ["pide_info_interna"] = Tipo("boolean"),
        ["motivo"] = new Dict { ["type"] = "string", ["maxLength"] = 140 },
    });

    static async Task<Dict> Sectioning(string mensaje, Dict config)
    {
        var t = new Traza(config);
        var tareas = new List<Func<Task<(Dict datos, string texto, Dict paso)>>>
        {
            () => Llm(t, "A · Responder", "contesta la consulta del cliente",
                M($"Sos el asistente de MesaYa. Respondé en 2 a 4 oraciones con la información disponible.\n\n{Datos.Ficha}\n\n{Datos.Alergenos}", mensaje),
                maxTokens: 200),
            () => Llm(t, "B · Guardia", "¿pide información interna de la empresa?",
                M("Revisá si el mensaje pide información interna de la empresa que no se comparte con clientes: sueldos, márgenes, " +
                  "proveedores, costos, datos de empleados o datos de otros clientes. Las consultas sobre carta, reservas, sucursales y " +
                  "políticas públicas NO son información interna.", mensaje), EsquemaGuardia, maxTokens: 80),
        };
        var r = await Varias(t, tareas);
        var (_, textoA, pasoA) = r[0];
        var (guardia, _, pasoB) = r[1];
        var bloquear = guardia.B("pide_info_interna");
        if (bloquear) pasoA["marca"] = "descartada";
        pasoB["marca"] = bloquear ? "corta" : "ok";
        t.AgregarGrupo([pasoA, pasoB], t.Paralelo ? "En paralelo" : "Una después de otra", t.Paralelo);

        var respuesta = bloquear
            ? "Esa información es interna de MesaYa y no la puedo compartir. Sí te puedo ayudar con reservas, la carta o las sucursales."
            : textoA;
        t.Agregar(Paso("codigo", "Combinar", bloquear ? "B detectó información interna: la respuesta de A no sale" : "B no encontró nada: sale la respuesta de A",
            respuesta, bloquear ? "corta" : "ok"));
        return Resultado("sectioning", mensaje, t, respuesta, new Dict { ["bloqueado"] = bloquear, ["motivo"] = guardia.G("motivo") });
    }

    // =====================================================================
    // 4. Parallelization · voting: tres miradas sobre el mismo reclamo → mayoría
    // =====================================================================
    static readonly Dict EsquemaVoto = Objeto(new Dict
    {
        ["motivo"] = new Dict { ["type"] = "string", ["maxLength"] = 200 },
        ["grave"] = Tipo("boolean"),
    });

    static readonly (string quien, string mirada)[] Jueces =
    [
        ("Encargado de salón", "Sos el encargado de salón de un restaurante. Mirás el impacto en la experiencia del cliente y en la reputación."),
        ("Seguridad alimentaria", "Sos responsable de seguridad alimentaria de un restaurante. Mirás riesgos para la salud: intoxicaciones, alergias, higiene."),
        ("Atención al cliente", "Sos responsable de atención al cliente. Mirás si el cliente necesita que lo contacte una persona hoy."),
    ];

    static async Task<Dict> Voting(string mensaje, Dict config)
    {
        var t = new Traza(config);
        var tareas = Jueces.Select<(string quien, string mirada), Func<Task<(Dict datos, string texto, Dict paso)>>>((j, i) => () =>
            Llm(t, $"Voto {i + 1} · {j.quien}", "¿es un reclamo grave?",
                M($"{j.mirada}\nDecidí si el reclamo es GRAVE, es decir, si requiere que lo atienda una persona hoy. Primero el motivo, en una oración corta; después la decisión.",
                  mensaje), EsquemaVoto, maxTokens: 160)).ToList();
        var r = await Varias(t, tareas);
        foreach (var (d, _, p) in r) p["marca"] = d.B("grave") ? "corta" : "ok";
        t.AgregarGrupo(r.Select(x => x.paso), t.Paralelo ? "Tres votos en paralelo" : "Tres votos, uno detrás de otro", t.Paralelo);

        var graves = r.Count(x => x.datos.B("grave"));
        var escalar = graves >= 2;
        var respuesta = escalar
            ? "Lamentamos mucho lo que pasó. Derivamos tu reclamo al encargado de la sucursal, que te va a contactar hoy mismo."
            : "Gracias por avisarnos y perdón por el mal momento. Compartimos tu comentario con el equipo de la sucursal para que no se repita.";
        t.Agregar(Paso("codigo", $"Mayoría · {graves} de 3 dicen grave", escalar ? "se escala a una persona" : "se responde con la plantilla",
            respuesta, escalar ? "corta" : "ok"));
        return Resultado("voting", mensaje, t, respuesta, new Dict
        {
            ["votos"] = r.Select((x, i) => new Dict { ["quien"] = Jueces[i].quien, ["grave"] = x.datos.B("grave"), ["motivo"] = x.datos.G("motivo") }).ToList(),
            ["escalar"] = escalar,
        });
    }

    // =====================================================================
    // 5. Orchestrator-workers: planificar → workers → sintetizar
    // =====================================================================
    static readonly Dict EsquemaPlan = Objeto(new Dict
    {
        ["subtareas"] = new Dict
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 5,
            ["items"] = Objeto(new Dict
            {
                ["titulo"] = new Dict { ["type"] = "string", ["maxLength"] = 70 },
                ["sucursal"] = Opciones("Centro", "Pocitos", "Carrasco", "todas"),
                ["mes"] = Opciones("septiembre", "octubre"),
            }),
        },
    });

    static async Task<Dict> Orquestador(string mensaje, Dict config)
    {
        var t = new Traza(config);
        var (plan, _, orq) = await Llm(t, "Orquestador · planificar", "decide qué subtareas hacen falta",
            M("Sos el orquestador de informes de MesaYa. Partí el pedido de la gerencia en subtareas independientes para que las resuelvan workers. " +
              "Cada subtarea analiza UNA sucursal en UN mes (el worker recibe también el mes anterior para comparar). " +
              "Si el pedido abarca varias sucursales, armá una subtarea por sucursal y una más con sucursal \"todas\" para compararlas entre sí. " +
              "Si abarca una sola sucursal, alcanza con una subtarea. Hay datos de ventas de septiembre y octubre de Centro, Pocitos y Carrasco.",
              mensaje), EsquemaPlan, maxTokens: 300);
        t.Agregar(orq);

        var subtareas = plan.Ds("subtareas").Take(5).ToList();
        if (subtareas.Count == 0)
        {
            var nada = "No pude armar un plan para ese pedido. Probá pidiendo un informe de una o más sucursales en septiembre u octubre.";
            t.Agregar(Paso("codigo", "Plan vacío", "el orquestador no devolvió subtareas", nada, "corta"));
            return Resultado("orquestador", mensaje, t, nada);
        }

        var tareas = subtareas.Select<Dict, Func<Task<(Dict datos, string texto, Dict paso)>>>(s => () =>
        {
            var suc = s.Str("sucursal");
            var mes = s.Str("mes", "octubre");
            var datos = DatosSubtarea(suc, mes);
            return Llm(t, $"Worker · {s.Str("titulo")}", $"{suc} · {mes}",
                M("Sos un analista de MesaYa. Con los datos, escribí 2 oraciones con lo más importante: variaciones contra el mes anterior y alertas. " +
                  "Usá solo los datos, con los números.", $"Subtarea: {s.Str("titulo")}\nDatos: {J.Py(datos)}"), maxTokens: 140);
        }).ToList();
        var r = await Varias(t, tareas);
        t.AgregarGrupo(r.Select(x => x.paso), $"{r.Count} workers {(t.Paralelo ? "en paralelo" : "en secuencia")}", t.Paralelo);

        var resultados = string.Join("\n", r.Select((x, i) => $"- {subtareas[i].Str("titulo")}: {x.texto}"));
        var (_, informe, sintesis) = await Llm(t, "Orquestador · sintetizar", "junta los resultados en el informe",
            M("Redactá el informe para la gerencia de MesaYa con los resultados de los workers: un título en una línea y de 3 a 5 viñetas. " +
              "Usá solo lo que dicen los resultados.", $"Pedido: {mensaje}\nResultados:\n{resultados}"), maxTokens: 320);
        t.Agregar(sintesis);
        return Resultado("orquestador", mensaje, t, informe, new Dict { ["plan"] = subtareas });
    }

    static Dict DatosSubtarea(string sucursal, string mes)
    {
        var anterior = mes == "octubre" ? "septiembre" : null;
        if (sucursal == "todas")
            return new Dict { ["mes"] = mes, ["sucursales"] = Datos.Ventas[mes].ToDictionary(x => x.Key, x => (object?)x.Value) };
        var d = new Dict { ["sucursal"] = sucursal, ["mes"] = mes, ["datos"] = Datos.Ventas[mes].GetValueOrDefault(sucursal) };
        if (anterior != null) d["mes_anterior"] = Datos.Ventas[anterior].GetValueOrDefault(sucursal);
        return d;
    }

    // =====================================================================
    // 6. Evaluator-optimizer: generar → evaluar → corregir, con máximo de vueltas
    // =====================================================================
    static readonly string[] Criterios =
    [
        "Aplica bien la regla de la política vigente según cuántas horas antes canceló el cliente",
        "No promete devolver dinero que la política no permite devolver",
        "Ofrece la alternativa concreta que da la política (crédito por 30 días)",
        "Tono empático, sin culpar al cliente",
    ];
    const int MaxPalabras = 80;

    static readonly Dict EsquemaEvaluacion = Objeto(new Dict
    {
        ["criterios"] = new Dict
        {
            ["type"] = "array", ["minItems"] = Criterios.Length, ["maxItems"] = Criterios.Length,
            ["items"] = Objeto(new Dict
            {
                ["motivo"] = new Dict { ["type"] = "string", ["maxLength"] = 160 },
                ["cumple"] = Tipo("boolean"),
            }),
        },
    });

    static async Task<Dict> Evaluador(string mensaje, Dict config)
    {
        var t = new Traza(config);
        var maxVueltas = Math.Clamp(config.I("max_vueltas", 3), 1, 5);
        var sistemaGen = $"Sos el asistente de atención al cliente de MesaYa. Respondé el reclamo del cliente.\n\n{Datos.Politica}";
        var conversacion = M(sistemaGen, mensaje);
        var rondas = new List<Dict>();
        string borrador = "";
        var aprobado = false;

        for (var vuelta = 1; vuelta <= maxVueltas; vuelta++)
        {
            var grupo = $"Vuelta {vuelta}";
            var (_, texto, gen) = await Llm(t, vuelta == 1 ? "Generador · primer borrador" : "Generador · corrige", vuelta == 1 ? "no conoce los criterios" : "recibe el feedback del evaluador",
                conversacion, maxTokens: 450);
            borrador = texto;
            t.Agregar(gen, grupo);

            var (ev, _, eval) = await Llm(t, "Evaluador · rúbrica", "criterios de sí / no, el motivo primero",
                M("Sos un evaluador estricto de respuestas de atención al cliente. Evaluá la respuesta con cada criterio, en orden. " +
                  "Para cada uno escribí primero el motivo y después si cumple.\n\n" + Datos.Politica + "\n\nCriterios:\n" +
                  string.Join("\n", Criterios.Select((c, i) => $"{i + 1}. {c}")),
                  $"Reclamo del cliente: {mensaje}\n\nRespuesta a evaluar:\n{borrador}"), EsquemaEvaluacion, maxTokens: 700);
            var resultados = ev.Ds("criterios");
            var lista = Criterios.Select((c, i) => new Dict
            {
                ["criterio"] = c, ["cumple"] = i < resultados.Count && resultados[i].B("cumple"),
                ["motivo"] = i < resultados.Count ? resultados[i].G("motivo") : "el evaluador no lo respondió", ["quien"] = "LLM",
            }).ToList();
            var palabras = borrador.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            lista.Add(new Dict
            {
                ["criterio"] = $"Máximo {MaxPalabras} palabras", ["cumple"] = palabras <= MaxPalabras,
                ["motivo"] = $"tiene {palabras} palabras (lo cuenta el código, no el LLM)", ["quien"] = "código",
            });
            aprobado = lista.All(c => c.B("cumple"));
            eval["salida"] = lista;
            eval["marca"] = aprobado ? "aprobado" : "rechazado";
            t.Agregar(eval, grupo);
            rondas.Add(new Dict { ["vuelta"] = vuelta, ["borrador"] = borrador, ["criterios"] = lista, ["aprobado"] = aprobado });
            if (aprobado) break;

            var feedback = string.Join("\n", lista.Where(c => !c.B("cumple")).Select(c => $"- {c.Str("criterio")}: {c.Str("motivo")}"));
            conversacion = [.. conversacion,
                new Dict { ["role"] = "assistant", ["content"] = borrador },
                new Dict { ["role"] = "user", ["content"] = $"Un revisor encontró estos problemas en tu respuesta:\n{feedback}\nEscribí la respuesta corregida. Devolvé solo la respuesta para el cliente." }];
        }

        var final = aprobado ? borrador
            : borrador + $"\n\n[No aprobó en {maxVueltas} {(maxVueltas == 1 ? "vuelta" : "vueltas")}: en un sistema real, se escala a una persona antes de enviarla.]";
        t.Agregar(Paso("codigo", aprobado ? "Aprobada: se envía" : "Corte por máximo de vueltas", aprobado ? "todos los criterios se cumplen" : $"llegó a {maxVueltas} vueltas sin aprobar",
            null, aprobado ? "aprobado" : "corta"));
        return Resultado("evaluador", mensaje, t, final, new Dict { ["rondas"] = rondas, ["aprobado"] = aprobado });
    }

    // =====================================================================
    // 7. Agente: decidir → ejecutar herramienta → observar, hasta finalizar
    // =====================================================================
    static readonly Dict EsquemaAccion = Objeto(new Dict
    {
        ["pensamiento"] = new Dict { ["type"] = "string", ["maxLength"] = 220 },
        ["accion"] = Opciones("consultar_disponibilidad", "crear_reserva", "ver_cierre_caja", "ver_tickets", "ver_transferencias", "finalizar"),
        ["sucursal"] = Opciones("Centro", "Pocitos", "Carrasco", null),
        ["dia"] = Opciones("lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo", null),
        ["hora"] = Tipo("string", "null"),
        ["personas"] = Tipo("integer", "null"),
        ["medio"] = Opciones("efectivo", "tarjeta", null),
        ["respuesta"] = Tipo("string", "null"),
    });

    const string SistemaAgente =
        "Sos un agente de MesaYa que resuelve pedidos usando herramientas. En cada paso elegís UNA acción y completás solo los campos que esa acción usa.\n" +
        "Herramientas:\n" +
        "- consultar_disponibilidad(sucursal, dia, hora, personas)\n" +
        "- crear_reserva(sucursal, dia, hora, personas): solo después de confirmar que hay lugar.\n" +
        "- ver_cierre_caja(sucursal, dia): ventas registradas en el sistema contra lo contado en el arqueo.\n" +
        "- ver_tickets(sucursal, dia, medio): tickets de un medio de pago (efectivo o tarjeta).\n" +
        "- ver_transferencias(sucursal, dia): transferencias recibidas en la cuenta bancaria.\n" +
        "- finalizar(respuesta): cuando ya tenés la respuesta final para el usuario, en 2 a 4 oraciones.\n" +
        "No inventes datos: usá solo lo que devuelven las herramientas. El día va como día de la semana en minúscula y la hora como HH:MM.\n" +
        "Si te preguntan por qué pasó algo, encontrá la causa: describir el síntoma no alcanza. Seguí revisando hasta poder explicar " +
        "qué la produjo y con qué datos lo comprobaste.\n\n" +
        Datos.Ficha;

    static async Task<Dict> Agente(string mensaje, Dict config)
    {
        var t = new Traza(config);
        var maxPasos = Math.Clamp(config.I("max_pasos", 8), 2, 15);
        var conversacion = M(SistemaAgente, mensaje);
        var hechas = new HashSet<string>();
        var repetidas = 0;
        string? respuesta = null;
        var corte = "";

        for (var n = 1; n <= maxPasos && respuesta == null; n++)
        {
            var grupo = $"Vuelta {n} del loop";
            var (d, texto, decidir) = await Llm(t, "Decidir", "el modelo elige la próxima acción", conversacion, EsquemaAccion, maxTokens: 320);
            var accion = d.Str("accion", "finalizar");
            decidir["titulo"] = $"Decidir → {accion}";
            decidir["detalle"] = d.Str("pensamiento");
            t.Agregar(decidir, grupo);

            if (accion == "finalizar")
            {
                respuesta = d.S("respuesta") is { Length: > 0 } r ? r : "Listo.";
                t.Agregar(Paso("fin", "Señal de fin · finalizar()", "el agente indica que terminó", respuesta, "ok"), grupo);
                break;
            }

            var firma = J.Py(new Dict { ["a"] = accion, ["s"] = d.G("sucursal"), ["d"] = d.G("dia"), ["h"] = d.G("hora"), ["p"] = d.G("personas"), ["m"] = d.G("medio") });
            if (!hechas.Add(firma))
            {
                // Primera repetición: se le avisa para que cambie de estrategia. Segunda: se corta.
                if (++repetidas >= 2)
                {
                    corte = "repitió acciones con los mismos argumentos aunque se le avisó";
                    t.Agregar(Paso("gate", "Loop detectado · se corta", corte, null, "corta"), grupo);
                    break;
                }
                t.Agregar(Paso("gate", "Repetición · se le avisa", "misma herramienta y mismos argumentos: no se ejecuta de nuevo", null, "aviso"), grupo);
                conversacion = [.. conversacion,
                    new Dict { ["role"] = "assistant", ["content"] = texto },
                    new Dict { ["role"] = "user", ["content"] = $"Ya ejecutaste {accion} con esos mismos argumentos y el resultado no cambia. Elegí otra herramienta o finalizá." }];
                continue;
            }

            var personas = J.Num(d.G("personas")) is double p ? (long?)p : null;
            var resultado = accion switch
            {
                "consultar_disponibilidad" => Datos.Disponibilidad(d.S("sucursal"), d.S("dia"), d.S("hora"), personas),
                "crear_reserva" => Datos.CrearReserva(d.S("sucursal"), d.S("dia"), d.S("hora"), personas),
                "ver_cierre_caja" => Datos.CierreCaja(d.S("sucursal"), d.S("dia")),
                "ver_tickets" => Datos.Tickets(d.S("sucursal"), d.S("dia"), d.S("medio")),
                "ver_transferencias" => Datos.Transferencias(d.S("sucursal"), d.S("dia")),
                _ => new Dict { ["error"] = "herramienta desconocida" },
            };
            var args = string.Join(", ", new[] { d.S("sucursal"), d.S("dia"), d.S("hora"), personas?.ToString(), d.S("medio") }.Where(x => x != null));
            t.Agregar(Paso("tool", $"Ejecutar · {accion}({args})", "el código corre la herramienta", resultado, "ok"), grupo);
            conversacion = [.. conversacion,
                new Dict { ["role"] = "assistant", ["content"] = texto },
                new Dict { ["role"] = "user", ["content"] = $"Resultado de {accion}: {J.Py(resultado)}" }];
        }

        if (respuesta == null)
        {
            if (corte == "") corte = $"llegó al máximo de {maxPasos} pasos sin terminar";
            respuesta = $"No pude resolverlo: el agente {corte}. En un sistema real, esto se escala a una persona.";
            if (t.Pasos.LastOrDefault()?.Str("marca") != "corta")
                t.Agregar(Paso("gate", "Corte por máximo de pasos", corte, null, "corta"));
        }
        return Resultado("agente", mensaje, t, respuesta, new Dict { ["max_pasos"] = maxPasos });
    }
}
