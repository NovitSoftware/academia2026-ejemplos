using MesaYa.Comun;
using static MesaYa.Durabilidad.Esquema;
using static MesaYa.Durabilidad.Traza;

namespace MesaYa.Durabilidad;

/// <summary>El proceso se cae a mitad de la tarea (falla inyectada). Lo que estaba en memoria se pierde.</summary>
public sealed class Caida(int paso) : Exception($"Caída después del paso {paso}")
{
    public int PasoCaido { get; } = paso;
}

/// <summary>
/// El agente de reservas: una tarea de cinco pasos (extraer datos → bloquear mesa → cobrar seña → crear reserva → confirmar)
/// con estado persistido, checkpoints, correlation ID, reintentos con backoff, recuperación escalonada y handoff.
/// </summary>
public sealed class Agente(Almacen almacen, Sistema sistema)
{
    /// <summary>Lo que dura un turno: la traza, las respuestas al cliente y las fallas que quedan por inyectar.</summary>
    sealed class Turno(Dict config, Dict fallas)
    {
        public Traza T { get; } = new(config);
        public Dict Config { get; } = config;
        public List<Dict> Respuestas { get; } = [];
        public Dict? Handoff { get; set; }
        public string Resultado { get; set; } = "";
        /// <summary>El último error de la API: decide qué se le dice al cliente si hay que degradar.</summary>
        public string UltimoError { get; set; } = "";
        public bool Checkpoints => Config.B("checkpoints");
        public bool Idempotencia => Config.B("idempotencia");
        public bool Retry => Config.B("retry");
        public int CaidaTras { get; } = fallas.I("caida_tras");
        public bool JsonRoto { get; set; } = fallas.B("json_roto");
        /// <summary>Fallas pendientes por operación: se consumen en orden, una por intento.</summary>
        public Dictionary<string, Queue<string>> Pendientes { get; } = Armar(fallas);

        static Dictionary<string, Queue<string>> Armar(Dict f)
        {
            var d = new Dictionary<string, Queue<string>>();
            if (f.S("timeout") is { Length: > 0 } op) d[op] = new Queue<string>(["timeout"]);
            var codigos = f.Str("errores") switch
            {
                "503x2" => new[] { "503", "503" },
                "503x4" => ["503", "503", "503", "503"],
                "400" => ["400"],
                _ => [],
            };
            if (codigos.Length > 0) d["crear_reserva"] = new Queue<string>(codigos);
            return d;
        }

        public string? Falla(string op) => Pendientes.TryGetValue(op, out var q) && q.Count > 0 ? q.Dequeue() : null;
        public void Decir(string de, string texto) => Respuestas.Add(new Dict { ["de"] = de, ["texto"] = texto });
    }

    // =====================================================================
    // Entradas: un mensaje del cliente o "reanudar" después de una caída
    // =====================================================================
    public async Task<Dict> Mensaje(string texto, Dict config, Dict fallas)
    {
        var u = new Turno(config, fallas);
        var conv = almacen.Conversacion();
        conv.Sumar("mensajes", new Dict { ["de"] = "cliente", ["texto"] = texto });
        var activa = almacen.CargarTarea(conv.S("tarea_activa"));
        Dict tarea;

        if (activa != null && activa.Str("dueño") != "agente_reservas")
        {
            // Después del handoff, el agente de reservas deja de actuar: el mensaje va al dueño de la tarea.
            u.T.Agregar(Paso("gate", $"Dueño de la tarea: {Nombre(activa.Str("dueño"))}",
                "el agente de reservas no responde: la tarea ya no es suya", new Dict { ["correlation_id"] = activa.G("correlation_id"), ["dueño"] = activa.G("dueño") }, "aviso"));
            almacen.Log(activa.Str("correlation_id"), "router", $"mensaje para {activa.Str("dueño")} (dueño de la tarea)");
            await AlDueño(u, activa, texto);
            tarea = activa;
        }
        else
        {
            if (activa != null && activa.Str("estado") == "esperando_usuario")
            {
                tarea = activa;
                ((List<object?>)tarea["entrada"]!).Add(texto);
                tarea["estado"] = "en_curso";
                u.T.Agregar(Paso("codigo", "Retoma la tarea en espera", "el estado persistido trae los mensajes y los datos de antes",
                    new Dict { ["correlation_id"] = tarea.G("correlation_id"), ["datos"] = tarea.G("datos") }, "ok"));
                almacen.Log(tarea.Str("correlation_id"), "router", "retoma la tarea que esperaba al cliente");
            }
            else
            {
                tarea = Nueva([texto]);
                u.T.Agregar(Paso("codigo", $"Nueva tarea · {tarea.Str("correlation_id")}", "se genera el correlation ID que acompaña al pedido",
                    new Dict { ["correlation_id"] = tarea.G("correlation_id") }, "ok"));
                almacen.Log(tarea.Str("correlation_id"), "router", "pedido recibido: tarea de reserva");
            }
            Encolar(tarea);
            conv["tarea_activa"] = tarea.Str("correlation_id");
            await Correr(u, tarea);
        }
        foreach (var r in u.Respuestas) conv.Sumar("mensajes", r);
        if (tarea.Str("estado") == "completada") conv["tarea_activa"] = null;
        almacen.GuardarConversacion(conv);
        return Resultado(u, tarea, texto, "mensaje");
    }

    public async Task<Dict> Reanudar(Dict config)
    {
        var u = new Turno(config, new Dict());
        var conv = almacen.Conversacion();
        // Tarea colgada: quedó "en_curso" en disco pero el proceso que la corría ya no existe.
        var colgada = almacen.TodasLasTareas().LastOrDefault(t => t.Str("estado") == "en_curso");
        Dict tarea;
        if (colgada != null)
        {
            tarea = colgada;
            var n = tarea.I("paso_actual");
            u.T.Agregar(Paso("checkpoint", $"Carga el checkpoint · {tarea.Str("correlation_id")}",
                $"datos/tareas/{tarea.Str("correlation_id")}.json · sigue en el paso {n} de 5", J.Leer(J.Serializar(tarea)), "ok"));
            almacen.Log(tarea.Str("correlation_id"), "agente_reservas", $"reanuda desde el checkpoint: paso {n}");
            for (var i = 1; i < n; i++)
            {
                var p = Datos.Pasos[i - 1];
                u.T.Agregar(Paso("saltado", $"Saltea · {p.Str("nombre")}",
                    p.B("efecto") ? "ya está en acciones_hechas: no se repite" : "su resultado ya está en el estado", null, "saltado"), "Reanudación");
            }
        }
        else if (almacen.Cola().LastOrDefault() is { } pendiente)
        {
            // Sin checkpoint no hay nada que cargar: solo queda el mensaje en la cola. Se empieza de cero, con otro ID.
            tarea = Nueva((pendiente.G("entrada") as List<object?> ?? []).OfType<string>().ToList());
            u.T.Agregar(Paso("gate", "No hay checkpoint · se empieza de cero",
                "en disco solo quedó el mensaje en la cola: la tarea se repite entera, con un correlation ID nuevo",
                new Dict { ["cola"] = pendiente, ["nuevo_correlation_id"] = tarea.G("correlation_id") }, "aviso"));
            almacen.Log(tarea.Str("correlation_id"), "agente_reservas", $"sin checkpoint: se repite el pedido de {pendiente.Str("correlation_id")}");
            Desencolar(pendiente.Str("correlation_id"));
            Encolar(tarea);
            conv["tarea_activa"] = tarea.Str("correlation_id");
        }
        else throw new ErrorHttp(400, "No hay ninguna tarea interrumpida para reanudar.");

        await Correr(u, tarea, reanudando: colgada != null);
        foreach (var r in u.Respuestas) conv.Sumar("mensajes", r);
        if (tarea.Str("estado") == "completada") conv["tarea_activa"] = null;
        almacen.GuardarConversacion(conv);
        return Resultado(u, tarea, null, "reanudar");
    }

    static Dict Nueva(List<string> entrada) => new()
    {
        ["correlation_id"] = "req-" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..6],
        ["tarea"] = "reserva", ["paso_actual"] = 1L,
        ["datos"] = new Dict(), ["acciones_hechas"] = new List<object?>(), ["resultados"] = new Dict(),
        ["reintentos"] = new Dict(), ["estado"] = "en_curso", ["dueño"] = "agente_reservas",
        ["entrada"] = entrada.Select(e => (object?)e).ToList(), ["checkpoints"] = new List<Dict>(),
        ["creada"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), ["actualizado"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
    };

    void Encolar(Dict tarea)
    {
        var cola = almacen.Cola();
        if (cola.Any(c => c.Str("correlation_id") == tarea.Str("correlation_id")))
            cola.RemoveAll(c => c.Str("correlation_id") == tarea.Str("correlation_id"));
        cola.Add(new Dict { ["correlation_id"] = tarea.G("correlation_id"), ["entrada"] = tarea.G("entrada"), ["recibido"] = DateTime.Now.ToString("HH:mm:ss") });
        almacen.GuardarCola(cola);
    }

    void Desencolar(string id)
    {
        var cola = almacen.Cola();
        cola.RemoveAll(c => c.Str("correlation_id") == id);
        almacen.GuardarCola(cola);
    }

    // =====================================================================
    // La tarea: cinco pasos con checkpoint después de cada uno
    // =====================================================================
    async Task Correr(Turno u, Dict tarea, bool reanudando = false)
    {
        var cid = tarea.Str("correlation_id");
        try
        {
            for (var n = tarea.I("paso_actual"); n <= Datos.Pasos.Count; n++)
            {
                var paso = Datos.Pasos[n - 1];
                var grupo = $"Paso {n} · {paso.Str("nombre")}";
                var seguir = paso.Str("id") switch
                {
                    "extraer_datos" => await Extraer(u, tarea, grupo),
                    "bloquear_mesa" => await Efecto(u, tarea, grupo, "bloquear_mesa", tarea.O("datos")!, "mesa", "id"),
                    "cobrar_sena" => await Efecto(u, tarea, grupo, "cobrar_sena",
                        tarea.O("datos")!.Con(new Dict { ["monto"] = Sena(tarea) }), "cobro", "id"),
                    "crear_reserva" => await Efecto(u, tarea, grupo, "crear_reserva",
                        tarea.O("datos")!.Con(new Dict { ["mesa"] = tarea.O("resultados")!.G("mesa"), ["cobro"] = tarea.O("resultados")!.G("cobro") }), "reserva", "codigo"),
                    _ => await Confirmar(u, tarea, grupo),
                };
                if (!seguir) return;  // espera al cliente, handoff o falla: el estado ya quedó guardado

                tarea["paso_actual"] = (long)n + 1;
                if (paso.B("efecto")) ((List<object?>)tarea["acciones_hechas"]!).Add(paso.Str("id"));
                if (n == Datos.Pasos.Count) tarea["estado"] = "completada";
                if (u.Checkpoints)
                {
                    tarea.Sumar("checkpoints", new Dict { ["n"] = (long)n, ["paso"] = paso.Str("id"), ["hora"] = DateTime.Now.ToString("HH:mm:ss") });
                    almacen.GuardarTarea(tarea);
                    u.T.Agregar(Paso("checkpoint", $"Checkpoint {n}", $"estado guardado en datos/tareas/{cid}.json",
                        new Dict { ["paso_actual"] = tarea.G("paso_actual"), ["acciones_hechas"] = tarea.G("acciones_hechas"), ["estado"] = tarea.G("estado") }, "ok", 15), grupo);
                }
                if (u.CaidaTras == n && n < Datos.Pasos.Count)
                    throw new Caida(n);
            }
            Terminar(tarea, "completada");
            almacen.GuardarTarea(tarea);  // sin checkpoints, el estado se guarda solo al final
            u.Resultado = "completada";
            almacen.Log(cid, "agente_reservas", $"tarea completada: {tarea.O("resultados")!.Str("reserva")}");
        }
        catch (Caida c)
        {
            u.T.Agregar(Paso("caida", "✗ El proceso se cae", u.Checkpoints
                ? $"se pierde lo que estaba en memoria; en disco queda el checkpoint {c.PasoCaido}"
                : "se pierde lo que estaba en memoria y no hay checkpoint: en disco solo queda el mensaje en la cola", null, "corta"));
            almacen.Log(cid, "agente_reservas", $"✗ caída después del paso {c.PasoCaido}");
            tarea["estado"] = "interrumpida";
            u.Resultado = "interrumpida";
            u.Decir("sistema", "⚠ El servidor se reinició a mitad de tu pedido. (En la demo: apretá «Reanudar».)");
        }
    }

    void Terminar(Dict tarea, string estado)
    {
        tarea["estado"] = estado;
        Desencolar(tarea.Str("correlation_id"));
    }

    /// <summary>"2026-11-14" → "sábado 14 de noviembre": el modelo chico se equivoca al calcular el día de la semana.</summary>
    static string Dia(string iso) => DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, out var f)
        ? $"{Texto.Dia(f)} {f.Day} de {f.ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("es-AR"))}" : iso;

    static long Sena(Dict tarea) => Datos.SenaPorPersona * tarea.O("datos")!.I("personas");

    // ---------- paso 1: extraer los datos (LLM) ----------
    static readonly Dict EsquemaDatos = Objeto(new Dict
    {
        ["intencion"] = Opciones("reserva", "evento", "reclamo", "otra"),
        ["sucursal"] = Opciones("Centro", "Carrasco", "Pocitos", null),
        ["fecha"] = Tipo("string", "null"),
        ["hora"] = Tipo("string", "null"),
        ["personas"] = Tipo("integer", "null"),
    });

    const string SistemaExtraer =
        "Sos el agente de reservas de MesaYa, un restaurante con sucursales en Centro, Carrasco y Pocitos. Hoy es jueves 12 de noviembre de 2026.\n" +
        "Leé los mensajes del cliente (pueden ser varios: los últimos corrigen a los anteriores) y extraé el pedido.\n" +
        "- intencion: reserva (pedir una mesa), evento (cumpleaños, fiesta o grupo grande), reclamo (queja por la comida o la atención, " +
        "pedido de devolución) u otra.\n" +
        "- fecha en formato AAAA-MM-DD (\"el sábado\" es 2026-11-14) y hora en HH:MM.\n" +
        "- Si un dato no aparece en los mensajes, va null: no lo inventes.";

    static readonly System.Text.RegularExpressions.Regex Fecha = new(@"^\d{4}-\d{2}-\d{2}$"), Hora = new(@"^\d{2}:\d{2}$");

    async Task<bool> Extraer(Turno u, Dict tarea, string grupo)
    {
        var cid = tarea.Str("correlation_id");
        var entrada = (tarea.G("entrada") as List<object?> ?? []).OfType<string>().ToList();
        var mensajes = M(SistemaExtraer, "Mensajes del cliente:\n" + string.Join("\n", entrada.Select((m, i) => $"{i + 1}. {m}")));
        Func<string, string>? cortar = null;
        if (u.JsonRoto)
        {
            u.JsonRoto = false;
            cortar = s => s[..(s.Length * 55 / 100)];  // la respuesta llega cortada, como cuando se acaban los tokens
        }
        var (datos, texto, p) = await u.T.Llm("Extraer datos del pedido", "salida estructurada: intención, sucursal, fecha, hora y personas",
            mensajes, EsquemaDatos, 200, cortar);
        if (cortar != null) p["detalle"] = "falla inyectada: la respuesta llega cortada";
        u.T.Agregar(p, grupo);

        if (datos == null)
        {
            var error = ErrorJson(texto);
            u.T.Agregar(Paso("gate", "JSON inválido · se reintenta una vez", $"se le pasa el error al modelo: {error}", texto, "aviso"), grupo);
            Contar(tarea, "extraer_datos");
            almacen.Log(cid, "agente_reservas", $"JSON inválido del LLM: {error}. 1 retry");
            mensajes = [.. mensajes,
                new Dict { ["role"] = "assistant", ["content"] = texto },
                new Dict { ["role"] = "user", ["content"] = $"Tu respuesta no es un JSON válido ({error}). Respondé de nuevo, solo con el JSON completo." }];
            (datos, texto, p) = await u.T.Llm("Extraer datos · reintento con el error", "el mismo pedido más el error de validación", mensajes, EsquemaDatos, 200);
            u.T.Agregar(p, grupo);
            if (datos == null)
            {
                await Recuperar(u, tarea, "el modelo devolvió dos veces un JSON inválido");
                return false;
            }
        }

        // Validación en código: un formato inválido se descarta en lugar de usarse.
        var previos = tarea.O("datos") ?? new Dict();
        var nuevos = new Dict
        {
            ["sucursal"] = datos.S("sucursal") is { } s && Datos.Sucursales.Contains(s) ? s : previos.G("sucursal"),
            ["fecha"] = datos.S("fecha") is { } f && Fecha.IsMatch(f) ? f : previos.G("fecha"),
            ["hora"] = datos.S("hora") is { } h && Hora.IsMatch(h) ? h : previos.G("hora"),
            ["personas"] = datos.In("personas") is int n && n > 0 ? (long)n : previos.G("personas"),
            ["nombre"] = Datos.Cliente,
        };
        tarea["datos"] = nuevos;
        var intencion = datos.Str("intencion", "reserva");
        almacen.Log(cid, "agente_reservas", $"intención={intencion} · {J.Py(nuevos)}");

        if (intencion == "reclamo")
        {
            await Derivar(u, tarea, "humano_atencion", "reclamo del cliente: hace falta criterio y autoridad para decidir",
                "decidir qué se le ofrece al cliente y responderle", grupo);
            return false;
        }
        if (intencion == "evento" || nuevos.I("personas") > Datos.MaxPersonas)
        {
            await Derivar(u, tarea, "agente_eventos",
                $"{nuevos.I("personas")} personas: supera el máximo de {Datos.MaxPersonas} del agente de reservas (es un evento privado)",
                "armar la propuesta del evento: menú, espacio y seña de grupo", grupo);
            return false;
        }

        var faltan = new[] { ("sucursal", "la sucursal"), ("fecha", "el día"), ("hora", "la hora"), ("personas", "cuántas personas son") }
            .Where(x => nuevos.G(x.Item1) == null).Select(x => x.Item2).ToList();
        if (faltan.Count > 0)
        {
            tarea["estado"] = "esperando_usuario";
            almacen.GuardarTarea(tarea);
            u.T.Agregar(Paso("gate", "Faltan datos · se le pregunta al cliente", "la tarea queda en espera, con su estado guardado",
                new Dict { ["faltan"] = faltan, ["estado"] = "esperando_usuario" }, "aviso"), grupo);
            almacen.Log(cid, "agente_reservas", $"esperando al cliente: falta {string.Join(", ", faltan)}");
            u.Decir("agente_reservas", $"¡Dale! Para reservar me falta {Unir(faltan)}. ¿Me lo pasás?");
            u.Resultado = "esperando";
            return false;
        }
        return true;
    }

    static string ErrorJson(string texto)
    {
        try { J.Leer(texto); return "ok"; }
        catch (System.Text.Json.JsonException e) { return Texto.Corte(e.Message.Split(". ")[0], 90); }
    }

    static string Unir(List<string> l) => l.Count == 1 ? l[0] : string.Join(", ", l[..^1]) + " y " + l[^1];

    static void Contar(Dict tarea, string op)
    {
        var r = tarea.O("reintentos")!;
        r[op] = (long)r.I(op) + 1;
    }

    // ---------- pasos 2 a 4: acciones con efecto (API) ----------
    async Task<bool> Efecto(Turno u, Dict tarea, string grupo, string op, Dict datos, string guardarEn, string campo)
    {
        var r = await Api(u, tarea, grupo, op, datos);
        if (r == null)
        {
            await Recuperar(u, tarea, $"{op} falló sin poder recuperarse");
            return false;
        }
        tarea.O("resultados")![guardarEn] = r.Str(campo);
        if (op == "cobrar_sena") tarea.O("resultados")!["sena"] = r.G("monto");
        return true;
    }

    /// <summary>Una llamada a la API con la política de reintentos: solo errores transitorios, con backoff y jitter.</summary>
    async Task<Dict?> Api(Turno u, Dict tarea, string grupo, string op, Dict datos, bool sinFallas = false)
    {
        var cid = tarea.Str("correlation_id");
        var clave = u.Idempotencia ? $"{cid}:{op}" : null;
        var maximo = u.Retry ? Datos.MaxIntentos : 1;
        for (var intento = 1; intento <= maximo; intento++)
        {
            var falla = sinFallas ? null : u.Falla(op);
            var r = sistema.Llamar(op, datos, cid, clave, falla);
            var codigo = r.Str("codigo");
            var ms = codigo == "timeout" ? Datos.TimeoutLlamada : 120;
            await Dormir(ms);
            var cabecera = clave != null ? $"X-Correlation-ID: {cid} · Idempotency-Key: {clave}" : $"X-Correlation-ID: {cid} · sin clave de idempotencia";
            var titulo = $"API · {op}" + (intento > 1 ? $" · intento {intento}" : "");
            if (codigo is "200" or "201")
            {
                var detalle = r.B("ya_existia") ? $"200 · ya existía: la API devuelve el resultado original" : $"201 · {cabecera}";
                var paso = Paso("api", titulo, detalle, r.G("cuerpo"), r.B("ya_existia") ? "idempotente" : "ok", ms);
                u.T.Agregar(paso, grupo);
                almacen.Log(cid, "api", $"{op} → {codigo}{(r.B("ya_existia") ? " (ya existía)" : "")}");
                return r.O("cuerpo");
            }
            var texto = codigo == "timeout" ? $"timeout a los {Datos.TimeoutLlamada / 1000} s: no llegó la respuesta (la acción quizá sí se hizo)"
                : $"HTTP {codigo} · {r.O("cuerpo")?.Str("error")}";
            u.T.Agregar(Paso("api", titulo, texto, r.G("hecho_igual") is Dict h ? new Dict { ["en_el_sistema"] = h } : r.G("cuerpo"), "error", ms), grupo);
            almacen.Log(cid, "api", $"{op} → {(codigo == "timeout" ? "timeout" : codigo)}");
            Contar(tarea, op);
            u.UltimoError = codigo;

            if (!Datos.Transitorio(codigo))
            {
                u.T.Agregar(Paso("gate", $"HTTP {codigo} · no se reintenta", "no es transitorio: repetir el mismo pedido da el mismo error", null, "corta"), grupo);
                return null;
            }
            if (!u.Retry)
            {
                u.T.Agregar(Paso("gate", "Sin retry · falla", "la política de reintentos está apagada", null, "corta"), grupo);
                return null;
            }
            if (intento == maximo)
            {
                u.T.Agregar(Paso("gate", $"Falla definitiva tras {maximo} intentos", "no se reintenta para siempre: se degrada y se escala", null, "corta"), grupo);
                return null;
            }
            // Backoff exponencial con jitter (un azar chico para que no reintenten todos a la vez).
            var espera = 1000L * (1 << (intento - 1)) + new Random(intento * 7919).Next(0, 300);
            await Dormir(espera);
            u.T.Agregar(Paso("espera", $"Backoff · espera {espera / 1000.0:0.0} s".Replace('.', ','), $"{1 << (intento - 1)} s + jitter antes del intento {intento + 1}",
                null, "espera", espera), grupo);
        }
        return null;
    }

    // ---------- paso 5: confirmar (LLM) ----------
    const string SistemaConfirmar =
        "Sos el agente de reservas de MesaYa. Escribí la confirmación para el cliente en 2 o 3 oraciones, en español rioplatense y con tono cálido. " +
        "Mencioná el código de reserva, la sucursal, el día, la hora, cuántas personas son y la seña cobrada, tal cual vienen en los datos. " +
        "No agregues nada que no esté en los datos.";

    async Task<bool> Confirmar(Turno u, Dict tarea, string grupo)
    {
        var d = tarea.O("datos")!;
        var r = tarea.O("resultados")!;
        var datos = new Dict
        {
            ["cliente"] = Datos.Cliente, ["reserva"] = r.G("reserva"), ["sucursal"] = d.G("sucursal"), ["dia"] = Dia(d.Str("fecha")),
            ["hora"] = d.G("hora"), ["personas"] = d.G("personas"), ["sena_cobrada"] = Datos.Plata(r.I("sena")),
        };
        var (_, texto, p) = await u.T.Llm("Redactar la confirmación", "el modelo escribe el mensaje con los datos de la reserva", M(SistemaConfirmar, J.Py(datos)), maxTokens: 160);
        u.T.Agregar(p, grupo);
        u.Decir("agente_reservas", texto);
        return true;
    }

    // =====================================================================
    // Recuperación escalonada: degradar, compensar, escalar
    // =====================================================================
    async Task Recuperar(Turno u, Dict tarea, string motivo)
    {
        const string grupo = "Recuperación";
        var cid = tarea.Str("correlation_id");
        var res = tarea.O("resultados")!;
        var hechas = (tarea.G("acciones_hechas") as List<object?> ?? []).OfType<string>().ToList();
        u.T.Agregar(Paso("gate", "1 · Degradar", "se le avisa al cliente y se le ofrece otro camino", motivo, "aviso"), grupo);

        var compensadas = new List<string>();
        if (hechas.Any(h => h is "cobrar_sena" or "bloquear_mesa"))
            u.T.Agregar(Paso("gate", "2 · Compensar", "se deshace lo que quedó a medias, en orden inverso",
                Enumerable.Reverse(hechas).Where(h => h != "crear_reserva").ToList(), "aviso"), grupo);
        foreach (var accion in Enumerable.Reverse(hechas))
        {
            var (op, datos, texto) = accion switch
            {
                "cobrar_sena" => ("reembolsar_sena", new Dict { ["cobro"] = res.G("cobro") }, $"te devolví la seña de {Datos.Plata(res.I("sena"))}"),
                "bloquear_mesa" => ("liberar_mesa", new Dict { ["mesa"] = res.G("mesa") }, "liberé la mesa"),
                _ => ("", new Dict(), ""),
            };
            if (op == "") continue;
            var r = await Api(u, tarea, grupo, op, datos, sinFallas: true);
            if (r != null) compensadas.Add(texto);
        }
        tarea["compensadas"] = compensadas.Select(c => (object?)c).ToList();

        var causa = u.UltimoError switch
        {
            "" => "no pude entender bien tu pedido",
            "400" or "401" or "403" => "el sistema de reservas rechazó el pedido",
            _ => "el sistema de reservas no está respondiendo",
        };
        var aviso = $"No pude confirmar tu reserva porque {causa}." +
                    (compensadas.Count > 0 ? $" Ya {Unir(compensadas)}." : "") +
                    $" Podés reservar por teléfono al {Datos.Telefono}. Le pasé tu caso al equipo con la referencia {cid}.";
        u.Decir("agente_reservas", aviso);
        await Derivar(u, tarea, "humano_atencion", motivo, "contactar al cliente y cerrar la reserva a mano", grupo, escalado: true);
        u.Resultado = "fallida";
        almacen.Log(cid, "agente_reservas", $"falla definitiva: {motivo}. Compensado: {string.Join(", ", compensadas)}");
    }

    // =====================================================================
    // Handoff: a otro agente o a una persona, con contexto estructurado
    // =====================================================================
    async Task Derivar(Turno u, Dict tarea, string para, string motivo, string pendiente, string grupo, bool escalado = false)
    {
        var cid = tarea.Str("correlation_id");
        var entrada = (tarea.G("entrada") as List<object?> ?? []).OfType<string>().ToList();
        Dict handoff;
        if (u.Config.B("handoff_contexto") || escalado)
        {
            var d = tarea.O("datos")!;
            var confirmados = new Dict();
            foreach (var k in new[] { "sucursal", "fecha", "hora", "personas" })
                if (d.G(k) != null) confirmados[k] = d.G(k);
            handoff = new Dict
            {
                ["de"] = "agente_reservas", ["para"] = para, ["motivo"] = motivo, ["cliente"] = Datos.Cliente,
                ["datos_confirmados"] = confirmados, ["hecho"] = Hecho(tarea), ["pendiente"] = pendiente, ["correlation_id"] = cid,
            };
        }
        else
            handoff = new Dict { ["de"] = "agente_reservas", ["para"] = para, ["mensaje"] = entrada.LastOrDefault() };

        tarea["dueño"] = para;
        tarea["estado"] = "derivada";
        tarea["handoff"] = handoff;
        Desencolar(cid);
        almacen.GuardarTarea(tarea);
        u.Handoff = handoff;
        u.T.Agregar(Paso("handoff", escalado ? $"3 · Escalar → {Nombre(para)}" : $"Handoff → {Nombre(para)}",
            u.Config.B("handoff_contexto") || escalado ? "resumen estructurado: motivo, datos confirmados, hecho y pendiente" : "sin contexto: solo el último mensaje",
            handoff, "handoff", 20), grupo);
        u.T.Agregar(Paso("gate", "El agente de reservas deja de actuar", $"la tarea ahora es de {Nombre(para)}: un solo dueño", null, "ok"), grupo);
        almacen.Log(cid, "agente_reservas", $"handoff → {para}: {motivo}");
        if (!escalado) u.Resultado = "derivada";

        if (para == "agente_eventos") await Eventos(u, tarea, null);
        else await AHumano(u, tarea, escalado);
    }

    static List<object?> Hecho(Dict tarea)
    {
        var r = tarea.O("resultados")!;
        var h = new List<object?> { "extrajo y validó los datos del pedido" };
        foreach (var a in (tarea.G("acciones_hechas") as List<object?> ?? []).OfType<string>())
            h.Add(a switch
            {
                "bloquear_mesa" => $"bloqueó la mesa {r.Str("mesa")}",
                "cobrar_sena" => $"cobró la seña de {Datos.Plata(r.I("sena"))}",
                "crear_reserva" => $"creó la reserva {r.Str("reserva")}",
                _ => a,
            });
        foreach (var c in (tarea.G("compensadas") as List<object?> ?? []).OfType<string>())
            h.Add(c.Replace("te devolví", "devolvió").Replace("liberé", "liberó"));
        return h;
    }

    /// <summary>El correlation ID viaja en el handoff pero no en el prompt: es aleatorio y no le sirve al modelo (y así las grabaciones sirven).</summary>
    static Dict ParaElModelo(Dict handoff)
    {
        var d = new Dict(handoff);
        d.Remove("correlation_id");
        return d;
    }

    static string Nombre(string agente) => Datos.Agentes.FirstOrDefault(a => a.Str("id") == agente)?.Str("nombre") ?? agente;

    // ---------- el agente de eventos ----------
    static readonly Dict EsquemaEventos = Objeto(new Dict
    {
        ["recibido"] = Tipo("boolean"),
        ["datos_que_faltan"] = new Dict { ["type"] = "array", ["items"] = Tipo("string"), ["maxItems"] = 5 },
        ["respuesta"] = Tipo("string"),
    });

    const string SistemaEventos =
        "Sos el agente de eventos privados de MesaYa: grupos de más de 12 personas, cumpleaños y fiestas, en las sucursales Centro, Carrasco y Pocitos. " +
        "Otro agente te pasó la tarea. Confirmá que la recibiste (recibido: true) y respondé al cliente en 2 a 4 oraciones, en español rioplatense: " +
        "presentate como el equipo de eventos, retomá los datos que ya están confirmados sin volver a pedirlos y pedí solo lo que falta para armar la " +
        "propuesta. En datos_que_faltan listá lo que todavía necesitás saber del cliente, incluidos los datos básicos del evento que no tengas.";

    async Task Eventos(Turno u, Dict tarea, string? nuevo)
    {
        var handoff = tarea.O("handoff")!;
        var contenido = $"Handoff recibido:\n{J.Py(ParaElModelo(handoff))}" + (nuevo != null ? $"\n\nNuevo mensaje del cliente: \"{nuevo}\"" : "");
        var (d, texto, p) = await u.T.Llm("Agente de eventos · recibe la tarea", "lee el handoff y le responde al cliente", M(SistemaEventos, contenido), EsquemaEventos, 320);
        d ??= new Dict();
        const string grupo = "Agente de eventos";
        u.T.Agregar(p, grupo);
        u.T.Agregar(Paso("codigo", d.B("recibido") ? "Acuse de recibo · recibido" : "Sin acuse de recibo",
            "el agente nuevo confirma que tomó la tarea", new Dict { ["recibido"] = d.G("recibido"), ["datos_que_faltan"] = d.G("datos_que_faltan") },
            d.B("recibido") ? "ok" : "aviso"), grupo);
        almacen.Log(tarea.Str("correlation_id"), "agente_eventos", $"recibido={J.Py(d.G("recibido"))} · le faltan: {J.Py(d.G("datos_que_faltan"))}");
        u.Decir("agente_eventos", d.Str("respuesta", texto));
    }

    // ---------- una persona ----------
    const string SistemaResumen =
        "Resumí el caso para la persona de atención al cliente que lo va a tomar: 2 oraciones que se lean en 10 segundos, " +
        "con qué pasó y qué pide el cliente. Sin saludos.";

    async Task AHumano(Turno u, Dict tarea, bool escalado)
    {
        const string grupo = "Atención al cliente";
        var cid = tarea.Str("correlation_id");
        var handoff = tarea.O("handoff")!;
        string resumen;
        if (escalado)
            resumen = $"El sistema de reservas falló y la reserva no se pudo crear. Hay que contactar a {Datos.Cliente} y cerrarla a mano.";
        else
        {
            var entrada = (tarea.G("entrada") as List<object?> ?? []).OfType<string>().ToList();
            var (_, texto, p) = await u.T.Llm("Resumen para la persona", "se lee en 10 segundos",
                M(SistemaResumen, $"Mensajes del cliente:\n{string.Join("\n", entrada.Select(e => $"- {e}"))}\n\nHandoff:\n{J.Py(ParaElModelo(handoff))}"), maxTokens: 120);
            u.T.Agregar(p, grupo);
            resumen = texto;
        }
        var bandeja = almacen.Bandeja();
        bandeja.Add(new Dict
        {
            ["correlation_id"] = cid, ["cliente"] = Datos.Cliente, ["resumen"] = resumen, ["handoff"] = handoff,
            ["mensajes"] = tarea.G("entrada"), ["estado"] = "pendiente", ["recibido"] = DateTime.Now.ToString("HH:mm"),
        });
        almacen.GuardarBandeja(bandeja);
        u.T.Agregar(Paso("codigo", "Caso en la bandeja de atención", "resumen legible más el handoff estructurado", new Dict { ["resumen"] = resumen }, "ok"), grupo);
        almacen.Log(cid, "humano_atencion", "caso recibido en la bandeja");
        if (!escalado)
            u.Decir("sistema", $"Te va a atender una persona del equipo de MesaYa. Ya tiene tu caso (referencia {cid}), así que no hace falta que repitas nada.");
    }

    /// <summary>Un mensaje que llega cuando la tarea ya es de otro: responde el dueño, no el agente de reservas.</summary>
    async Task AlDueño(Turno u, Dict tarea, string texto)
    {
        var entrada = tarea.G("entrada") as List<object?> ?? [];
        entrada.Add(texto);
        if (tarea.Str("dueño") == "agente_eventos")
        {
            await Eventos(u, tarea, texto);
            u.Resultado = "derivada";
        }
        else
        {
            var bandeja = almacen.Bandeja();
            var caso = bandeja.LastOrDefault(b => b.Str("correlation_id") == tarea.Str("correlation_id"));
            if (caso != null) caso["mensajes"] = entrada;
            almacen.GuardarBandeja(bandeja);
            u.T.Agregar(Paso("codigo", "Se suma al caso de la bandeja", "la persona ve el mensaje nuevo junto al resto", null, "ok"));
            u.Decir("sistema", $"Tu mensaje se sumó al caso {tarea.Str("correlation_id")}. Te responde una persona del equipo en cuanto lo tome.");
            u.Resultado = "derivada";
        }
        almacen.GuardarTarea(tarea);
    }

    // =====================================================================
    Dict Resultado(Turno u, Dict tarea, string? mensaje, string accion)
    {
        var enDisco = almacen.CargarTarea(tarea.Str("correlation_id"));
        if (u.Resultado == "") u.Resultado = tarea.Str("estado");
        return new Dict
        {
            ["accion"] = accion, ["mensaje"] = mensaje, ["respuestas"] = u.Respuestas, ["resultado"] = u.Resultado,
            ["tarea"] = J.Leer(J.Serializar(tarea)), ["en_disco"] = enDisco, ["handoff"] = u.Handoff,
            ["pasos"] = u.T.Pasos, ["llamadas"] = u.T.Llamadas, ["resumen"] = u.T.Resumen(), ["grabado"] = u.T.Grabado,
            ["config"] = u.Config, ["sistema"] = J.Leer(J.Serializar(sistema.Foto())),
        };
    }
}
