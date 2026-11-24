using System.Globalization;
using System.Text.RegularExpressions;
using MesaYa.Comun;

namespace MesaYa.Matriz;

/// <summary>Un trabajo largo (medir, rutear). La interfaz lo consulta mientras corre.</summary>
public sealed class Trabajo(string tipo)
{
    public string Tipo { get; } = tipo;
    public string Estado { get; set; } = "corriendo";
    public string? Actual { get; set; }
    public Dict? Resultado { get; set; }
    public string? Error { get; set; }
    public int Hechas { get; set; }
    public int Total { get; set; }
    readonly DateTime inicio = DateTime.UtcNow;
    public long Ms => (long)(DateTime.UtcNow - inicio).TotalMilliseconds;

    public Dict Foto() => new()
    {
        ["tipo"] = Tipo, ["estado"] = Estado, ["actual"] = Actual, ["resultado"] = Resultado, ["error"] = Error,
        ["hechas"] = (long)Hechas, ["total"] = (long)Total, ["ms"] = Ms,
    };
}

/// <summary>Un perfil de modelo: proveedor, modelo y precio ilustrativo por millón de tokens.</summary>
public sealed class Perfil
{
    public required string Id { get; init; }
    public required string Nombre { get; init; }
    public required string Proveedor { get; set; }
    public required string Modelo { get; set; }
    public double PrecioEntrada { get; init; }
    public double PrecioSalida { get; init; }
    /// <summary>Si el perfil es de NVIDIA y no hay clave, corre este modelo local en su lugar.</summary>
    public string? Respaldo { get; init; }
    public bool UsaRespaldo { get; set; }

    public double Costo(Dict r) => (r.D("tokens_entrada") * PrecioEntrada + r.D("tokens_salida") * PrecioSalida) / 1_000_000;

    public Dict Publico() => new()
    {
        ["id"] = Id, ["nombre"] = Nombre, ["proveedor"] = Proveedor, ["modelo"] = Modelo,
        ["precio_entrada"] = PrecioEntrada, ["precio_salida"] = PrecioSalida, ["usa_respaldo"] = UsaRespaldo,
    };
}

/// <summary>
/// Corre las tareas en cada perfil, las corrige y simula las estrategias de routing.
/// Cada resultado (perfil × tarea) se guarda: el router reutiliza lo medido y solo llama al modelo si falta algo.
/// </summary>
public static class Evaluacion
{
    static Cliente C => Cliente.Instancia;
    public const string Hoy = "jueves 12/11/2026";
    static readonly DateTime FechaHoy = new(2026, 11, 12);

    public static List<Perfil> Perfiles { get; set; } = [];
    public static List<Dict> Tareas { get; set; } = [];
    public static string Politica { get; set; } = "";
    public static Dictionary<string, Dict> Resultados { get; set; } = [];  // "perfil|tarea" → resultado
    public static List<Dict> Llamadas { get; } = [];

    public static Perfil Perfil(string id) => Perfiles.First(p => p.Id == id);
    static readonly string[] Sucursales = ["Palermo", "Belgrano", "Centro"];
    static List<object?> Lista(params object?[] x) => [.. x];

    // ---------- tools de la tarea "herramienta" ----------
    static Dict Tool(string nombre, string descripcion, Dict propiedades, params string[] requeridos) => new()
    {
        ["type"] = "function",
        ["function"] = new Dict
        {
            ["name"] = nombre, ["description"] = descripcion,
            ["parameters"] = new Dict { ["type"] = "object", ["properties"] = propiedades, ["required"] = Lista([.. requeridos]) },
        },
    };

    static Dict Texto(string? descripcion = null) => descripcion == null ? new Dict { ["type"] = "string" } : new Dict { ["type"] = "string", ["description"] = descripcion };
    static Dict Opciones(params object?[] valores) => new() { ["type"] = "string", ["enum"] = Lista(valores) };

    public static readonly List<Dict> Tools =
    [
        Tool("buscar_disponibilidad", "Busca mesas libres en una sucursal.",
            new Dict { ["sucursal"] = Opciones("Palermo", "Belgrano", "Centro"), ["fecha"] = Texto("AAAA-MM-DD"), ["hora"] = Texto("HH:MM"), ["personas"] = new Dict { ["type"] = "integer" } },
            "sucursal", "fecha", "hora", "personas"),
        Tool("cancelar_reserva", "Cancela una reserva por su código.", new Dict { ["codigo"] = Texto("Código como R-1234") }, "codigo"),
        Tool("consultar_menu", "Consulta la carta de una sucursal.",
            new Dict { ["sucursal"] = Opciones("Palermo", "Belgrano", "Centro"), ["filtro"] = Opciones("todos", "vegano", "vegetariano", "sin_tacc") }, "sucursal", "filtro"),
        Tool("consultar_horario", "Consulta el horario de una sucursal para un día.",
            new Dict { ["sucursal"] = Opciones("Palermo", "Belgrano", "Centro"), ["dia"] = Opciones("lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo") }, "sucursal", "dia"),
        Tool("registrar_reclamo", "Registra un reclamo de un cliente.", new Dict { ["descripcion"] = Texto() }, "descripcion"),
    ];

    static Dict Objeto(Dict propiedades) => new() { ["type"] = "object", ["properties"] = propiedades, ["required"] = Lista([.. propiedades.Keys]) };

    // ---------- el pedido de cada tipo de tarea ----------
    static Pedido Armar(Perfil perfil, Dict tarea)
    {
        var prov = C.Proveedor(perfil.Proveedor);
        var tipo = tarea.Str("tipo");
        return tipo switch
        {
            "clasificar" => new Pedido
            {
                Proveedor = prov, Modelo = perfil.Modelo, MaxTokens = 60,
                Esquema = Objeto(new Dict { ["intencion"] = Opciones("reservar", "cancelar", "consulta", "reclamo") }),
                Mensajes = Cliente.M("Clasificá el mensaje de un cliente de MesaYa en una intención.", tarea.Str("mensaje")),
            },
            "extraer" => new Pedido
            {
                Proveedor = prov, Modelo = perfil.Modelo, MaxTokens = 120,
                Esquema = Objeto(new Dict { ["sucursal"] = Opciones("Palermo", "Belgrano", "Centro"), ["fecha"] = Texto(), ["hora"] = Texto(), ["personas"] = new Dict { ["type"] = "integer" } }),
                Mensajes = Cliente.M($"Hoy es {Hoy}. Extraé los datos de la reserva: sucursal, fecha (AAAA-MM-DD), hora (HH:MM, 24 h) y cantidad de personas.", tarea.Str("mensaje")),
            },
            "herramienta" => new Pedido
            {
                Proveedor = prov, Modelo = perfil.Modelo, MaxTokens = 160, Tools = Tools,
                Mensajes = Cliente.M($"Sos el asistente de MesaYa. Hoy es {Hoy}. Usá la tool que corresponda al pedido del cliente.", tarea.Str("mensaje")),
            },
            _ => new Pedido
            {
                Proveedor = prov, Modelo = perfil.Modelo, MaxTokens = 160,
                Esquema = Objeto(new Dict
                {
                    ["sena_por_persona"] = new Dict { ["type"] = "number" }, ["porcentaje"] = new Dict { ["type"] = "integer" },
                    ["devolucion"] = new Dict { ["type"] = "number" }, ["credito"] = new Dict { ["type"] = "number" },
                }),
                Mensajes = Cliente.M(Politica + "\n\nAplicá la política y devolvé la seña por persona, el porcentaje que se devuelve, el monto a devolver y el crédito.", Enunciado(tarea)),
            },
        };
    }

    public static string Enunciado(Dict tarea)
    {
        if (tarea.Str("tipo") != "razonar") return tarea.Str("mensaje");
        var d = tarea.O("datos")!;
        return $"Una reserva de {d.I("personas")} personas cancela {d.I("horas")} horas antes. El cliente tiene {d.I("visitas")} visitas este año. ¿Cuánto se le devuelve y cuánto crédito recibe?";
    }

    // ---------- validador (no conoce la respuesta) y corrección (sí) ----------
    /// <summary>
    /// Lo que el router puede chequear sin saber la respuesta: formato, valores posibles y que las cuentas cierren.
    /// Es lo que usa la cascada para decidir si escala.
    /// </summary>
    public static (bool ok, string motivo) Validar(Dict tarea, Dict salida, List<Dict> tools)
    {
        switch (tarea.Str("tipo"))
        {
            case "clasificar":
                return salida.Str("intencion") is "reservar" or "cancelar" or "consulta" or "reclamo" ? (true, "intención válida") : (false, "intención fuera de la lista");
            case "extraer":
                if (!Sucursales.Contains(salida.Str("sucursal"))) return (false, "sucursal inválida");
                if (!DateTime.TryParseExact(salida.Str("fecha"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) || f < FechaHoy)
                    return (false, "fecha inválida o pasada");
                if (!Regex.IsMatch(salida.Str("hora"), @"^([01]\d|2[0-3]):[0-5]\d$")) return (false, "hora con otro formato");
                return salida.I("personas") is >= 1 and <= 60 ? (true, "campos válidos") : (false, "personas fuera de rango");
            case "herramienta":
                if (tools.Count == 0) return (false, "no llamó a ninguna tool");
                var t = tools[0];
                var def = Tools.Select(x => x.O("function")!).FirstOrDefault(x => x.Str("name") == t.Str("nombre"));
                if (def == null) return (false, "tool inexistente");
                var args = t.O("argumentos") ?? new Dict();
                var faltan = (def.O("parameters")!.G("required") as List<object?> ?? []).OfType<string>().Where(r => !args.ContainsKey(r)).ToList();
                if (faltan.Count > 0) return (false, "faltan argumentos: " + string.Join(", ", faltan));
                return args.S("sucursal") is { } s && !Sucursales.Contains(s) ? (false, "sucursal inválida") : (true, "tool y argumentos válidos");
            default:
                var personas = tarea.O("datos")!.I("personas");
                var sena = salida.D("sena_por_persona");
                var pct = salida.I("porcentaje");
                if (sena is not (2000 or 2500)) return (false, "seña que no existe en la política");
                if (pct is not (0 or 50 or 100)) return (false, "porcentaje que no existe en la política");
                if (Math.Abs(salida.D("devolucion") - personas * sena * pct / 100.0) > 1) return (false, "la cuenta no cierra");
                return salida.D("credito") is 0 or 1000 ? (true, "la cuenta cierra") : (false, "crédito inválido");
        }
    }

    /// <summary>La corrección real: compara con la respuesta esperada. El router no la ve.</summary>
    public static bool Corregir(Dict tarea, Dict salida, List<Dict> tools)
    {
        var e = tarea.O("esperado")!;
        static string N(object? v) => Comun.Texto.Plano(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim();
        switch (tarea.Str("tipo"))
        {
            case "herramienta":
                if (tools.Count == 0 || tools[0].Str("nombre") != e.Str("tool")) return false;
                var args = tools[0].O("argumentos") ?? new Dict();
                return (e.O("args") ?? new Dict()).All(kv => N(args.G(kv.Key)) == N(kv.Value));
            case "extraer":
                return e.All(kv => kv.Key == "hora" ? N(salida.G("hora")).PadLeft(5, '0') == N(kv.Value) : N(salida.G(kv.Key)) == N(kv.Value));
            case "razonar":
                return Math.Abs(salida.D("devolucion") - e.D("devolucion")) < 1 && Math.Abs(salida.D("credito") - e.D("credito")) < 1;
            default:
                return N(salida.G("intencion")) == N(e.G("intencion"));
        }
    }

    static Dict LeerJson(string texto)
    {
        var t = texto.Trim();
        var i = t.IndexOf('{'); var j = t.LastIndexOf('}');
        if (i < 0 || j <= i) return new Dict();
        try { return J.Leer(t[i..(j + 1)]) as Dict ?? new Dict(); }
        catch (System.Text.Json.JsonException) { return new Dict(); }
    }

    // ---------- correr una tarea ----------
    public static async Task<Dict> Correr(Perfil perfil, Dict tarea)
    {
        var pedido = Armar(perfil, tarea);
        Dict r;
        try { r = await C.Completar(pedido); }
        catch (ErrorProveedor e) { r = e.Resultado; }
        var tools = r.Ds("tool_calls");
        var salida = tarea.Str("tipo") == "herramienta" ? new Dict() : LeerJson(r.Str("texto"));
        var error = r.S("error");
        var (valida, motivo) = error != null ? (false, "error del proveedor") : Validar(tarea, salida, tools);
        var resultado = new Dict
        {
            ["perfil"] = perfil.Id, ["tarea"] = tarea.Str("id"), ["tipo"] = tarea.Str("tipo"), ["proveedor"] = perfil.Proveedor, ["modelo"] = perfil.Modelo,
            ["salida"] = tarea.Str("tipo") == "herramienta" ? (tools.Count > 0 ? tools[0] : r.Str("texto")) : salida,
            ["valida"] = valida, ["motivo"] = motivo, ["correcta"] = error == null && Corregir(tarea, salida, tools),
            ["tokens_entrada"] = r.G("tokens_entrada") ?? 0L, ["tokens_salida"] = r.G("tokens_salida") ?? 0L, ["ms"] = r.G("ms") ?? 0L,
            ["costo"] = perfil.Costo(r), ["estructura"] = r.G("estructura"), ["error"] = error, ["grabado"] = r.G("grabado"),
        };
        lock (Resultados) Resultados[$"{perfil.Id}|{tarea.Str("id")}"] = resultado;
        lock (Llamadas) Llamadas.Add(resultado.Con(new Dict { ["request"] = r.G("request"), ["texto"] = r.G("texto") }));
        return resultado;
    }

    static async Task<Dict> Obtener(Perfil perfil, Dict tarea, Trabajo t)
    {
        lock (Resultados)
            if (Resultados.TryGetValue($"{perfil.Id}|{tarea.Str("id")}", out var r) && r.Str("modelo") == perfil.Modelo) return r;
        t.Actual = $"{perfil.Nombre} · {tarea.Str("id")} · {perfil.Modelo}";
        return await Correr(perfil, tarea);
    }

    // ---------- A · medir la matriz ----------
    public static async Task Medir(Trabajo t)
    {
        t.Total = Perfiles.Count * Tareas.Count;
        foreach (var perfil in Perfiles)  // por perfil, para no cambiar de modelo en cada llamada
            foreach (var tarea in Tareas)
            {
                t.Actual = $"{perfil.Nombre} · {tarea.Str("id")} · {perfil.Modelo}";
                await Correr(perfil, tarea);
                t.Hechas++;
            }
        t.Resultado = Matriz();
    }

    static double Percentil(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var o = xs.Order().ToList();
        return o[(int)Math.Ceiling(p * o.Count) - 1];
    }

    public static Dict Matriz()
    {
        var filas = new List<Dict>();
        foreach (var perfil in Perfiles)
        {
            List<Dict> rs;
            lock (Resultados) rs = Resultados.Values.Where(r => r.Str("perfil") == perfil.Id && r.Str("modelo") == perfil.Modelo).ToList();
            if (rs.Count == 0) continue;
            var porTipo = new Dict();
            foreach (var tipo in new[] { "clasificar", "extraer", "herramienta", "razonar" })
            {
                var xs = rs.Where(r => r.Str("tipo") == tipo).ToList();
                porTipo[tipo] = new Dict { ["correctas"] = (long)xs.Count(r => r.B("correcta")), ["total"] = (long)xs.Count, ["validas"] = (long)xs.Count(r => r.B("valida")) };
            }
            var ms = rs.Select(r => r.D("ms")).ToList();
            var salida = rs.Sum(r => r.D("tokens_salida"));
            filas.Add(new Dict
            {
                ["perfil"] = perfil.Id, ["nombre"] = perfil.Nombre, ["modelo"] = perfil.Modelo, ["proveedor"] = perfil.Proveedor, ["usa_respaldo"] = perfil.UsaRespaldo,
                ["por_tipo"] = porTipo, ["correctas"] = (long)rs.Count(r => r.B("correcta")), ["total"] = (long)rs.Count,
                ["p50_ms"] = Percentil(ms, .5), ["p95_ms"] = Percentil(ms, .95), ["tokens_s"] = ms.Sum() > 0 ? salida / (ms.Sum() / 1000) : 0,
                ["costo_tarea"] = rs.Average(r => r.D("costo")), ["costo_por_1000"] = rs.Average(r => r.D("costo")) * 1000,
                ["json_garantizado"] = rs.Where(r => r.Str("tipo") != "herramienta").All(r => r.Str("estructura") == "garantizada"),
                ["tools"] = rs.Where(r => r.Str("tipo") == "herramienta").Any(r => r.G("salida") is Dict),
            });
        }
        List<Dict> resultados;
        lock (Resultados) resultados = Resultados.Values.ToList();
        return new Dict { ["filas"] = filas, ["resultados"] = resultados };
    }

    // ---------- C · estrategias de routing ----------
    static readonly string[] Orden = ["economico", "generalista", "grande"];
    static string Regla(string tipo) => tipo switch { "herramienta" => "generalista", "razonar" => "grande", _ => "economico" };

    public static readonly List<Dict> Estrategias =
    [
        new() { ["id"] = "grande", ["nombre"] = "Siempre el grande", ["resumen"] = "La línea base: todo al modelo más capaz." },
        new() { ["id"] = "economico", ["nombre"] = "Siempre el económico", ["resumen"] = "Todo al más barato, aunque falle." },
        new() { ["id"] = "reglas", ["nombre"] = "Reglas", ["resumen"] = "Por tipo de tarea: clasificar y extraer al económico, tools al generalista, razonar al grande." },
        new() { ["id"] = "cascada", ["nombre"] = "Cascada", ["resumen"] = "Empieza por el económico; si el validador rechaza, escala al siguiente." },
        new() { ["id"] = "reglas_cascada", ["nombre"] = "Reglas + cascada", ["resumen"] = "Arranca donde dice la regla y escala si el validador rechaza." },
    ];

    static IEnumerable<string> Camino(string estrategia, string tipo) => estrategia switch
    {
        "grande" => ["grande"],
        "economico" => ["economico"],
        "reglas" => [Regla(tipo)],
        "cascada" => Orden,
        _ => Orden.SkipWhile(o => o != Regla(tipo)),
    };

    public static async Task Router(Trabajo t, double derivacion)
    {
        t.Total = Estrategias.Count * Tareas.Count;
        var porEstrategia = new List<Dict>();
        foreach (var e in Estrategias)
        {
            var id = e.Str("id");
            var detalle = new List<Dict>();
            foreach (var tarea in Tareas)
            {
                var intentos = new List<Dict>();
                var cascada = id is "cascada" or "reglas_cascada";
                foreach (var p in Camino(id, tarea.Str("tipo")))
                {
                    var r = await Obtener(Perfil(p), tarea, t);
                    intentos.Add(new Dict { ["perfil"] = p, ["valida"] = r.G("valida"), ["motivo"] = r.G("motivo"), ["correcta"] = r.G("correcta"), ["costo"] = r.G("costo"), ["ms"] = r.G("ms") });
                    if (!cascada || r.B("valida")) break;
                }
                detalle.Add(new Dict
                {
                    ["tarea"] = tarea.Str("id"), ["tipo"] = tarea.Str("tipo"), ["intentos"] = intentos, ["correcta"] = intentos.Last().B("correcta"),
                    ["costo"] = intentos.Sum(i => i.D("costo")), ["ms"] = intentos.Sum(i => i.D("ms")),
                });
                t.Hechas++;
            }
            var n = detalle.Count;
            var resueltas = detalle.Count(d => d.B("correcta"));
            var costoModelo = detalle.Sum(d => d.D("costo"));
            var costoTotal = costoModelo + (n - resueltas) * derivacion;
            porEstrategia.Add(e.Con(new Dict
            {
                ["detalle"] = detalle, ["resueltas"] = (long)resueltas, ["total"] = (long)n, ["escalamientos"] = (long)detalle.Count(d => d.Ds("intentos").Count > 1),
                ["llamadas"] = (long)detalle.Sum(d => d.Ds("intentos").Count), ["costo_modelo"] = costoModelo, ["costo_modelo_1000"] = costoModelo / n * 1000,
                ["costo_total"] = costoTotal, ["costo_resolucion"] = resueltas > 0 ? costoTotal / resueltas : null, ["ms"] = detalle.Sum(d => d.D("ms")),
            }));
        }
        t.Resultado = new Dict { ["estrategias"] = porEstrategia, ["derivacion"] = derivacion };
    }

    // ---------- D · reglas o clasificación ----------
    static readonly Regex Complejo = new(@"\b(evento|presupuesto|devuelv\w*|reembols\w*|grupo)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task Clasificar(Trabajo t, List<Dict> mensajes)
    {
        var eco = Perfil("economico");
        var prov = C.Proveedor(eco.Proveedor);
        t.Total = mensajes.Count;
        var filas = new List<Dict>();
        foreach (var m in mensajes)
        {
            t.Actual = $"Clasificando con {eco.Modelo}: {m.Str("mensaje")}";
            var regla = Complejo.Match(m.Str("mensaje"));
            Dict r;
            try
            {
                r = await C.Completar(new Pedido
                {
                    Proveedor = prov, Modelo = eco.Modelo, MaxTokens = 80,
                    Esquema = Objeto(new Dict { ["complejidad"] = Opciones("simple", "complejo") }),
                    Mensajes = Cliente.M("Decidí si el pedido de un cliente de restaurante es simple (una consulta o acción directa) " +
                                         "o complejo (varios pasos, cálculos, cambios combinados o un problema a resolver).", m.Str("mensaje")),
                });
            }
            catch (ErrorProveedor e) { r = e.Resultado; }
            var clasif = LeerJson(r.Str("texto")).Str("complejidad", "?");
            var porRegla = regla.Success ? "complejo" : "simple";
            filas.Add(new Dict
            {
                ["mensaje"] = m.Str("mensaje"), ["esperado"] = m.Str("esperado"),
                ["reglas"] = porRegla, ["palabra"] = regla.Success ? regla.Value : null, ["clasificador"] = clasif,
                ["tokens"] = r.D("tokens_entrada") + r.D("tokens_salida"), ["ms"] = r.G("ms"), ["costo"] = eco.Costo(r),
            });
            t.Hechas++;
        }
        t.Resultado = new Dict
        {
            ["filas"] = filas, ["modelo"] = eco.Modelo,
            ["aciertos_reglas"] = (long)filas.Count(f => f.Str("reglas") == f.Str("esperado")),
            ["aciertos_clasificador"] = (long)filas.Count(f => f.Str("clasificador") == f.Str("esperado")),
            ["costo_clasificador"] = filas.Sum(f => f.D("costo")), ["ms_clasificador"] = filas.Sum(f => f.D("ms")),
        };
    }
}
