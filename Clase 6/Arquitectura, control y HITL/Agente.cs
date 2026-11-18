using MesaYa.Comun;
using static MesaYa.Hitl.Esquema;
using static MesaYa.Hitl.Traza;

namespace MesaYa.Hitl;

/// <summary>
/// Agente con tools: decidir → ejecutar → observar, hasta la señal de fin. Todo loop tiene freno (slide 4.3):
/// máximo de pasos, de costo y de tiempo, señal de fin y objetivo verificable. El detector de loops (slide 4.4)
/// corta cuando repite la misma tool con los mismos argumentos y cambia de estrategia.
/// </summary>
public sealed class Agente(Sistema sistema)
{
    public const long TimeoutMs = 120_000;  // 2 minutos por conversación

    static readonly Dict EsquemaAccion = Objeto(new Dict
    {
        ["pensamiento"] = new Dict { ["type"] = "string", ["maxLength"] = 200 },
        ["accion"] = Opciones("buscar_reservas", "consultar_disponibilidad", "crear_reserva", "cancelar_reserva", "finalizar"),
        ["nombre"] = Tipo("string", "null"),
        ["codigo"] = Tipo("string", "null"),
        ["sucursal"] = Opciones("Centro", "Carrasco", "Pocitos", null),
        ["dia"] = Opciones("lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo", null),
        ["hora"] = Tipo("string", "null"),
        ["personas"] = Tipo("integer", "null"),
        ["respuesta"] = Tipo("string", "null"),
    });

    const string SistemaAgente =
        "Sos el agente de reservas de MesaYa, un restaurante con sucursales en Centro, Carrasco y Pocitos. Hoy es jueves 12 de noviembre de 2026. " +
        "La clienta que escribe es Laura G.\n" +
        "Resolvés el pedido con herramientas. En cada paso elegís UNA acción y completás solo los campos que esa acción usa. " +
        "En \"pensamiento\" escribí una oración corta con el motivo.\n" +
        "Herramientas:\n" +
        "- buscar_reservas(nombre): reservas ya hechas a nombre de una persona (para cancelar o consultar una existente). No muestra lugares libres.\n" +
        "- consultar_disponibilidad(sucursal, dia, hora, personas): si hay lugar para una reserva nueva.\n" +
        "- crear_reserva(sucursal, dia, hora, personas): solo después de confirmar que hay lugar.\n" +
        "- cancelar_reserva(codigo): necesita un único código de reserva (formato R-1234).\n" +
        "- finalizar(respuesta): cuando terminaste, en 1 a 3 oraciones para la clienta.\n" +
        "No inventes datos ni códigos: usá solo lo que devuelven las herramientas o lo que dijo la clienta. " +
        "El día va como día de la semana en minúscula y la hora como HH:MM.";

    /// <summary>Corre el loop. Devuelve una confirmación pendiente si el agente quiso cancelar una reserva.</summary>
    public async Task<Dict?> Correr(Turno u, List<Dict> historial, string texto)
    {
        var t = u.T;
        var maxPasos = Math.Clamp(u.Config.I("max_pasos", 8), 2, 15);
        var costoMax = u.Config.D("costo_max", 0.5);
        var detector = u.Config.B("detector");
        // El agente ve los últimos mensajes de la conversación: así entiende "la del sábado".
        var conversacion = new List<Dict> { new() { ["role"] = "system", ["content"] = SistemaAgente } };
        conversacion.AddRange(historial.TakeLast(6).Select(m => new Dict { ["role"] = m.Str("role"), ["content"] = m.Str("content") }));
        conversacion.Add(new Dict { ["role"] = "user", ["content"] = texto });

        var control = new Dict
        {
            ["max_pasos"] = (long)maxPasos, ["costo_max"] = costoMax, ["timeout_ms"] = TimeoutMs, ["detector"] = detector,
            ["pasos"] = 0L, ["costo"] = 0.0, ["ms"] = 0L, ["fin"] = null, ["objetivo"] = null, ["repeticiones"] = 0L,
        };
        u.Extra["control"] = control;
        string? anterior = null;
        var repetidas = 0;
        string? creada = null;
        List<Dict>? candidatas = null;
        Dict? confirmacion = null;

        for (var n = 1; ; n++)
        {
            var costo = t.Pasos.Sum(p => p.D("costo"));
            control["costo"] = costo; control["ms"] = t.Total; control["pasos"] = (long)n - 1;
            // Los frenos se revisan antes de cada vuelta.
            if (n > maxPasos) { Cortar(u, "max_pasos", $"llegó al máximo de {maxPasos} pasos"); break; }
            if (costo >= costoMax) { Cortar(u, "costo", $"gastó USD {costo:0.000} y el máximo es USD {costoMax:0.000}"); break; }
            if (t.Total >= TimeoutMs) { Cortar(u, "timeout", $"pasaron {t.Total / 1000} s y el máximo es {TimeoutMs / 1000} s"); break; }

            var grupo = $"Vuelta {n}";
            var (d, salida, decidir) = await t.Llm("Decidir", "el modelo elige la próxima acción", conversacion, EsquemaAccion, 260);
            d ??= new Dict();
            var accion = d.Str("accion", "finalizar");
            decidir["titulo"] = $"Decidir → {accion}";
            decidir["detalle"] = d.Str("pensamiento");
            t.Agregar(decidir, grupo);

            if (accion == "finalizar")
            {
                control["pasos"] = (long)n; control["fin"] = "finalizar";
                t.Agregar(Paso("fin", "Señal de fin · finalizar()", "el agente indica que terminó", d.S("respuesta"), "ok"), grupo);
                if (creada != null)
                {
                    // Objetivo verificable: no alcanza con que el agente diga que terminó.
                    var r = sistema.Reserva(creada);
                    var ok = r?.Str("estado") == "confirmada";
                    control["objetivo"] = ok;
                    t.Agregar(Paso("gate", ok ? "Objetivo verificado" : "El objetivo no se cumple",
                        $"la reserva {creada} {(ok ? "existe y está confirmada" : "no está en el sistema")}", r, ok ? "ok" : "corta"), grupo);
                }
                u.Decir("asistente", d.S("respuesta") is { Length: > 0 } resp ? resp : "Listo.");
                u.Resultado = "respondida";
                break;
            }

            var args = Args(accion, d);
            var firma = $"{accion}({args})";
            repetidas = firma == anterior ? repetidas + 1 : 1;
            anterior = firma;
            control["repeticiones"] = (long)Math.Max(control.I("repeticiones"), repetidas);
            if (detector && repetidas >= 3)
            {
                control["pasos"] = (long)n; control["fin"] = "loop";
                t.Agregar(Paso("gate", "⚠ Loop detectado · se corta", $"{firma} por tercera vez seguida: no se ejecuta", null, "corta"), grupo);
                CambiarEstrategia(u, accion, candidatas);
                break;
            }

            var resultado = Ejecutar(accion, d, ref creada, ref candidatas, ref confirmacion);
            t.Agregar(Paso("tool", $"Ejecutar · {firma}", Nivel(accion), resultado,
                resultado.ContainsKey("error") ? "aviso" : repetidas > 1 ? "repetida" : "ok"), grupo);
            if (confirmacion != null)
            {
                control["pasos"] = (long)n; control["fin"] = "confirmacion";
                t.Agregar(Paso("gate", "Pide confirmación", "cancelar es irreversible: el código frena y le pregunta al cliente", confirmacion, "aviso"), grupo);
                u.Decir("asistente", confirmacion.Str("pregunta"));
                u.Resultado = "confirmacion";
                return confirmacion;
            }
            conversacion = [.. conversacion,
                new Dict { ["role"] = "assistant", ["content"] = salida },
                new Dict { ["role"] = "user", ["content"] = $"Resultado de {accion}: {J.Py(resultado)}" }];
        }
        control["costo"] = t.Pasos.Sum(p => p.D("costo")); control["ms"] = t.Total;
        return null;
    }

    static string Args(string accion, Dict d) => accion switch
    {
        "buscar_reservas" => $"nombre=\"{d.Str("nombre")}\"",
        "cancelar_reserva" => $"codigo=\"{d.Str("codigo")}\"",
        _ => string.Join(", ", new[] { d.S("sucursal"), d.S("dia"), d.S("hora"), d.In("personas")?.ToString() }.Where(x => x != null)),
    };

    static string Nivel(string accion) =>
        Datos.Herramientas.FirstOrDefault(h => h.Str("nombre") == accion) is { } h ? $"{h.Str("riesgo")} · nivel: {h.Str("nivel")}" : "";

    Dict Ejecutar(string accion, Dict d, ref string? creada, ref List<Dict>? candidatas, ref Dict? confirmacion)
    {
        switch (accion)
        {
            case "buscar_reservas":
            {
                var lista = sistema.Buscar(d.Str("nombre"));
                if (lista.Count > 1)
                {
                    candidatas = lista;
                    return new Dict { ["resultados"] = (long)lista.Count, ["detalle"] = "más de una coincidencia: la búsqueda solo muestra el detalle cuando hay un único resultado" };
                }
                return lista.Count == 1 ? new Dict { ["resultados"] = 1L, ["reserva"] = Publica(lista[0]) } : new Dict { ["resultados"] = 0L };
            }
            case "consultar_disponibilidad":
                return Datos.Disponibilidad(d.S("sucursal"), d.S("dia"), d.S("hora"), d.In("personas"));
            case "crear_reserva":
            {
                // El sistema de destino valida por su cuenta: no confía en que el agente haya consultado.
                var disp = Datos.Disponibilidad(d.S("sucursal"), d.S("dia"), d.S("hora"), d.In("personas"));
                if (!disp.B("hay_lugar")) return new Dict { ["error"] = disp.Str("motivo", disp.Str("error")) };
                var r = sistema.Crear(d.Str("sucursal"), d.Str("dia"), d.Str("hora"), d.In("personas") ?? 2);
                creada = r.Str("codigo");
                return Publica(r);
            }
            case "cancelar_reserva":
            {
                var r = sistema.Reserva(d.S("codigo"));
                if (r == null || r.Str("estado") != "confirmada") return new Dict { ["error"] = $"no hay una reserva confirmada con el código {d.Str("codigo")}" };
                confirmacion = new Dict
                {
                    ["codigo"] = r.Str("codigo"),
                    ["pregunta"] = $"Voy a cancelar tu reserva {r.Str("codigo")} del {r.Str("dia")} a las {r.Str("hora")} en {r.Str("sucursal")}. ¿Confirmás?",
                };
                return new Dict { ["estado"] = "requiere_confirmacion", ["codigo"] = r.Str("codigo") };
            }
            default:
                return new Dict { ["error"] = "herramienta desconocida" };
        }
    }

    static Dict Publica(Dict r) => new()
    {
        ["codigo"] = r.G("codigo"), ["sucursal"] = r.G("sucursal"), ["dia"] = r.G("dia"), ["hora"] = r.G("hora"), ["personas"] = r.G("personas"), ["estado"] = r.G("estado"),
    };

    /// <summary>Un freno se activó: se corta y se escala con el estado, en lugar de seguir gastando.</summary>
    static void Cortar(Turno u, string fin, string motivo)
    {
        ((Dict)u.Extra["control"]!)["fin"] = fin;
        var titulo = fin switch { "costo" => "Freno de costo", "timeout" => "Timeout", _ => "Máximo de pasos" };
        u.T.Agregar(Paso("gate", $"{titulo} · se corta", motivo, null, "corta"));
        u.T.Agregar(Paso("codigo", "Escalar con el estado", "una persona recibe el pedido y lo que el agente ya revisó",
            u.T.Pasos.Where(p => p.Str("tipo") == "tool").Select(p => (object?)p.Str("titulo").Replace("Ejecutar · ", "")).ToList(), "aviso"));
        u.Decir("asistente", "No pude resolverlo por mi cuenta, así que le pasé tu pedido a una persona del equipo con todo lo que ya revisé. Te escriben en breve.");
        u.Resultado = "escalada";
    }

    /// <summary>No reintentar igual: cambiar de estrategia. Si la búsqueda era ambigua, preguntarle al cliente cuál.</summary>
    static void CambiarEstrategia(Turno u, string accion, List<Dict>? candidatas)
    {
        if (accion == "buscar_reservas" && candidatas is { Count: > 1 })
        {
            var opciones = candidatas.Select(c => $"{c.Str("codigo")} ({c.Str("dia")} {c.Str("hora")}, {c.Str("sucursal")})").ToList();
            var texto = opciones.Count == 2 ? $"{opciones[0]} y {opciones[1]}" : string.Join(", ", opciones);
            u.T.Agregar(Paso("codigo", "Cambiar de estrategia · preguntarle al cliente", "el código arma la repregunta con los resultados que la búsqueda no mostraba", opciones, "ok"));
            u.Decir("asistente", $"Encontré {candidatas.Count} reservas a tu nombre: {texto}. ¿Cuál de las {candidatas.Count}?");
            u.Resultado = "repregunta";
            return;
        }
        u.T.Agregar(Paso("codigo", "Cambiar de estrategia · escalar", "repetir no acerca al objetivo", null, "aviso"));
        u.Decir("asistente", "Me trabé con tu pedido, así que se lo pasé a una persona del equipo. Te escriben en breve.");
        u.Resultado = "escalada";
    }
}
