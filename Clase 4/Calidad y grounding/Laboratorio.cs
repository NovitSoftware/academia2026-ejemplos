using System.Diagnostics;
using MesaYa.Comun;

namespace MesaYa.Calidad;

/// <summary>El turno completo: ruta → recuperación → generación → verificación → diagnóstico. Y la evaluación.</summary>
public static class Laboratorio
{
    static readonly string Base = AppContext.BaseDirectory;

    public static readonly Dict Preguntas = J.LeerObjeto(File.ReadAllText(Path.Combine(Base, "evaluacion", "preguntas.json")));
    public static readonly Dictionary<string, Dict> PorId = Preguntas.Ds("preguntas").ToDictionary(q => q.Str("id"));
    public static readonly object? Escenarios = J.Leer(File.ReadAllText(Path.Combine(Base, "escenarios.json")));

    /// <summary>Estado del índice de embeddings: {listo, error}.</summary>
    public static readonly Dict EstadoIndice = new() { ["listo"] = false, ["error"] = null };

    public static readonly Dict ConfigBase = new()
    {
        ["perfil"] = "clientes", ["tenant"] = "mesaya-ar", ["chunking"] = "seccion", ["busqueda"] = "hibrida",
        ["k"] = 4, ["umbral"] = 0.0, ["solo_vigentes"] = false, ["priorizar_oficiales"] = false, ["rerank"] = false,
        ["grounding"] = "estricto", ["verificar"] = true, ["enrutar"] = false,
    };

    static Ollama ollama => Ollama.Instancia;

    public static async Task Preparar()
    {
        await ollama.Preparar();
        try
        {
            foreach (var ix in Busqueda.Indices.Values) await ix.CargarVectores();
            EstadoIndice["listo"] = true;
        }
        catch (Exception e)
        {
            EstadoIndice["error"] = $"No se pudo indexar el corpus: {e.Message}";
        }
    }

    static Dict Metricas(List<Dict> candidatos, int k, int totalRel, List<string> prohibidos)
    {
        var topk = candidatos.Where(f => f.B("en_contexto")).ToList();
        var relK = topk.Count(f => f.B("relevante"));
        var primero = candidatos.FirstOrDefault(f => f.B("relevante"))?.In("pos");
        return new Dict
        {
            ["k"] = k, ["en_contexto"] = topk.Count, ["relevantes_en_k"] = relK, ["relevantes_total"] = totalRel,
            ["precision"] = topk.Count > 0 ? Texto.Redondear((double)relK / topk.Count, 2) : null,
            ["recall"] = totalRel > 0 ? Texto.Redondear((double)relK / totalRel, 2) : null,
            ["hit"] = relK > 0, ["rr"] = primero is int p && p <= k ? Texto.Redondear(1.0 / p, 2) : 0,
            ["primer_relevante"] = primero,
            ["violaciones"] = topk.Select(f => f.Str("document_id")).Where(prohibidos.Contains).ToList(),
        };
    }

    static List<string> Textos(Dict d, string k) => (d.G(k) as IEnumerable<object?> ?? []).OfType<string>().ToList();

    static Dict Nodo(string id, string pregunta, string estado, string detalle, string? arreglo = null, bool conArreglo = false)
    {
        var n = new Dict { ["id"] = id, ["pregunta"] = pregunta, ["estado"] = estado, ["detalle"] = detalle };
        if (conArreglo || arreglo != null) n["arreglo"] = arreglo;
        return n;
    }

    /// <summary>Árbol de diagnóstico de la slide 05: ¿dónde se rompió?</summary>
    static List<Dict> Diagnosticar(Dict? q, Dict? rec, Dict? verif, bool abstuvo, string modo)
    {
        var nodos = new List<Dict>();
        double? fidelidad = verif?.Dn("fidelidad");
        if (q == null)
        {
            nodos.Add(Nodo("corpus", "¿La respuesta está en el corpus?", "desconocido",
                "Sin pregunta de evaluación no hay forma de saber qué fragmento era el correcto. Por eso hace falta un set de evaluación."));
            if (fidelidad is double f0)
                nodos.Add(Nodo("fiel", "¿La respuesta es fiel a la evidencia?", f0 >= 0.99 ? "ok" : "falla",
                    $"Fidelidad {Texto.Redondear(f0 * 100)} %."));
            return nodos;
        }
        var esperados = q.Ds("esperados");
        var tipo = q.S("tipo");
        if (tipo is "sin_acceso" or "fuera_corpus")
        {
            nodos.Add(Nodo("corpus", "¿La respuesta está en el corpus?", "ok", "No, para este perfil. Lo correcto es decir «no sé»."));
            var fuga = rec != null ? Textos(rec.O("metricas")!, "violaciones") : [];
            if (fuga.Count > 0)
                nodos.Add(Nodo("permisos", "¿Entró algo que no debía?", "falla", $"Entró {string.Join(", ", fuga)}.",
                    "Filtrar por audiencia o vigencia antes de buscar."));
            nodos.Add(Nodo("abstencion", "¿El asistente dijo que no sabe?", abstuvo ? "ok" : "falla",
                abstuvo ? "Se abstuvo." : "Respondió igual: inventó o usó algo que no correspondía.",
                abstuvo ? null : "Grounding estricto con «no sé» explícito y un umbral de relevancia.", conArreglo: true));
            return nodos;
        }
        if (tipo == "ruta") return nodos;
        nodos.Add(Nodo("corpus", "¿La respuesta está en el corpus?", "ok",
            "Sí: " + string.Join(", ", esperados.Select(e => e.Str("document_id") + (e.B("seccion") ? $" › {e.Str("seccion")}" : "")))));
        if (modo == "sin")
            nodos.Add(Nodo("contexto", "¿Le dimos la evidencia al modelo?", "falla", "No: respondió sin documentos.",
                "Usar RAG: recuperar y pasarle los fragmentos."));
        else
        {
            var m = rec!.O("metricas")!;
            var p = m.In("primer_relevante");
            nodos.Add(Nodo("recuperado", "¿Se recuperó entre los candidatos?", p != null ? "ok" : "falla",
                p != null ? $"Primer fragmento correcto en la posición {p}." : "No aparece entre los 20 candidatos.",
                p != null ? null : "Problema de recuperación: probar otro modo de búsqueda o chunking por sección.", conArreglo: true));
            if (p != null)
                nodos.Add(Nodo("contexto", "¿Entró al contexto?", m.B("hit") ? "ok" : "falla",
                    m.B("hit") ? $"Entró: {m.G("relevantes_en_k")} de {m.G("relevantes_total")} fragmentos relevantes."
                               : $"Quedó en la posición {p}, fuera de k = {m.G("k")} o bajo el umbral.",
                    m.B("hit") ? null : "Subir k, activar reranking o bajar el umbral.", conArreglo: true));
            var viol = Textos(m, "violaciones");
            if (viol.Count > 0)
                nodos.Add(Nodo("permisos", "¿Entró algo que no debía?", "falla", $"Entró {string.Join(", ", viol)}.",
                    "Filtrar por vigencia o país antes de buscar."));
        }
        if (fidelidad is double f)
        {
            var ok = f >= 0.99;
            nodos.Add(Nodo("fiel", "¿La respuesta es fiel a la evidencia?", ok ? "ok" : "falla", $"Fidelidad {Texto.Redondear(f * 100)} %.",
                ok ? null : "Problema de generación: grounding estricto y citas por oración.", conArreglo: true));
        }
        else if (abstuvo)
        {
            var tenia = modo != "sin" && rec!.O("metricas")!.B("hit");
            nodos.Add(Nodo("fiel", "¿Usó la evidencia que tenía?", "falla",
                tenia ? "Dijo «no sé» con el fragmento correcto en el contexto: falló la generación, no la búsqueda."
                      : "Dijo «no sé» porque el fragmento correcto no le llegó.",
                tenia ? "Revisar el prompt (demasiado estricto) o probar un modelo más capaz."
                      : "Problema de recuperación: arreglarlo antes de tocar el prompt."));
        }
        if (verif?.S("correcta") is "si" or "parcial" or "no")
        {
            var c = verif.Str("correcta");
            var motivo = verif.Str("motivo_correcta");
            nodos.Add(Nodo("correcta", "¿Responde lo que se preguntó?", c == "si" ? "ok" : c == "parcial" ? "parcial" : "falla",
                motivo.Length > 0 ? motivo : $"Clave: {q.G("respuesta_clave") ?? "None"}"));
        }
        return nodos;
    }

    static long Ms(Stopwatch s) => Texto.Redondear(s.Elapsed.TotalMilliseconds);

    public static async Task<Dict> CorrerTurno(string pregunta, Dict config, string? preguntaId, List<Dict> historial)
    {
        if (!EstadoIndice.B("listo") && !ollama.ModoGuion)
            throw new ErrorHttp(503, "El índice todavía se está preparando. Mirá el estado arriba a la derecha.");
        var cfg = ConfigBase.Con(config);
        var q = preguntaId != null ? PorId.GetValueOrDefault(preguntaId) : null;
        if (q != null && q.Str("pregunta") != pregunta) q = null;
        var t0 = Stopwatch.StartNew();
        var etapas = new List<Dict>();
        var salida = new Dict
        {
            ["pregunta"] = pregunta, ["config"] = cfg, ["pregunta_id"] = q?.Str("id"),
            ["clave"] = q?.G("respuesta_clave"), ["etapas"] = etapas,
        };

        var destino = new Dict { ["ruta"] = "documentos", ["motivo"] = "Router apagado: todo va a RAG." };
        if (cfg.B("enrutar"))
        {
            destino = await Ruta.Decidir(pregunta);
            etapas.Add(new Dict { ["nombre"] = "Decidir ruta", ["ms"] = destino.G("ms") ?? 0 });
        }
        salida["ruta"] = destino;

        if (destino.Str("ruta") != "documentos")
            return await TurnoSinRag(pregunta, preguntaId, destino, salida, t0);

        // Con historial, la búsqueda usa también el último mensaje del usuario ("¿Y para los contratados?").
        var previo = historial.Where(h => h.Str("rol") == "usuario").Select(h => h.Str("texto")).TakeLast(1);
        var rec = await Busqueda.Buscar(string.Join(" ", previo.Append(pregunta)), cfg);
        var esperados = q?.Ds("esperados") ?? [];
        var candidatos = rec.Ds("candidatos");
        foreach (var f in candidatos) f["relevante"] = q != null ? Busqueda.EsRelevante(f, esperados) : null;
        if (q != null)
            rec["metricas"] = Metricas(candidatos, cfg.I("k"), Busqueda.TotalRelevantes(cfg, esperados), Textos(q, "prohibidos"));
        else
            rec["metricas"] = new Dict
            {
                ["k"] = cfg.G("k"), ["en_contexto"] = rec.Ds("contexto").Count, ["precision"] = null, ["recall"] = null,
                ["violaciones"] = new List<object?>(), ["primer_relevante"] = null,
            };
        salida["recuperacion"] = rec;
        etapas.Add(new Dict
        {
            ["nombre"] = "Recuperar" + (cfg.B("rerank") ? " + rerank" : ""),
            ["ms"] = Ms(t0) - etapas.Sum(e => (long)e.D("ms")),
        });

        var modo = cfg.Str("grounding");
        var contexto = modo != "sin" ? rec.Ds("contexto") : [];
        List<Dict> mensajes;
        if (modo == "estricto" && contexto.Count == 0)
        {
            // Evidencia insuficiente: ni siquiera se llama al modelo (slide 13).
            salida["respuesta"] = new Dict
            {
                ["texto"] = Generacion.NoSe, ["tokens_prompt"] = 0, ["tokens_respuesta"] = 0, ["ms"] = 0,
                ["corte"] = "Ningún fragmento superó el umbral: se responde «no sé» sin llamar al modelo.",
            };
            mensajes = [];
        }
        else
        {
            mensajes = Generacion.ArmarMensajes(pregunta, contexto, modo, historial);
            var r = await ollama.Chat(mensajes);
            salida["respuesta"] = r;
            etapas.Add(new Dict { ["nombre"] = "Generar", ["ms"] = r.G("ms") });
        }
        salida["mensajes"] = mensajes;
        var respuesta = salida.O("respuesta")!;
        var texto = respuesta.Str("texto");
        var abstuvo = Generacion.EsAbstencion(texto);
        salida["abstencion"] = abstuvo;

        Dict? verif = null;
        if (cfg.B("verificar"))
        {
            var evidencia = rec.Ds("contexto");  // en modo "sin" se verifica contra lo que el modelo NO vio
            verif = await Generacion.Verificar(pregunta, texto, evidencia, q?.S("respuesta_clave"));
            if (modo == "estricto")  // citas que no existen en el contexto
                foreach (var o in verif.Ds("oraciones"))
                    o["citas_invalidas"] = (o.G("citas") as List<int> ?? []).Where(c => c < 1 || c > evidencia.Count).ToList();
            etapas.Add(new Dict { ["nombre"] = "Verificar", ["ms"] = verif.G("ms") ?? 0 });
        }
        salida["verificacion"] = verif;
        salida["diagnostico"] = Diagnosticar(q, rec, verif, abstuvo, modo);
        salida["ms_total"] = Ms(t0);
        salida["grabado"] = respuesta.G("grabado");
        return salida;
    }

    static async Task<Dict> TurnoSinRag(string pregunta, string? preguntaId, Dict destino, Dict salida, Stopwatch t0)
    {
        var etapas = (List<Dict>)salida["etapas"]!;
        var r = destino.Str("ruta");
        string sistema, usuario;
        if (r == "datos")
        {
            var (sql, rs) = await Ruta.GenerarSql(pregunta);
            var resultado = Ruta.EjecutarSql(sql);
            salida["herramienta"] = new Dict { ["tipo"] = "SQL", ["pedido"] = sql, ["resultado"] = resultado };
            etapas.Add(new Dict { ["nombre"] = "Generar SQL", ["ms"] = rs.G("ms") });
            sistema = "Sos el asistente de MesaYa. Respondé en 1 o 2 oraciones usando solo el resultado de la consulta SQL. Si hay un error, decí que no pudiste obtener el dato.";
            usuario = $"Pregunta: {pregunta}\nSQL ejecutado: {sql}\nResultado: {J.Py(resultado)}";
        }
        else if (r == "en_vivo")
        {
            var suc = destino.B("sucursal") ? destino.Str("sucursal") : "Palermo";
            foreach (var s in Ruta.Sucursales.Keys)
                if (Texto.Plano(s) == Texto.Plano(suc)) suc = s;
            var fecha = destino.B("fecha") ? destino.Str("fecha") : Ruta.Hoy().ToString("yyyy-MM-dd");
            var personas = destino.B("personas") ? destino.I("personas") : 2;
            var resultado = Ruta.Disponibilidad(suc, fecha, personas);
            salida["herramienta"] = new Dict
            {
                ["tipo"] = "API", ["pedido"] = $"GET /disponibilidad?sucursal={suc}&fecha={fecha}&personas={personas}", ["resultado"] = resultado,
            };
            sistema = "Sos el asistente de MesaYa. Respondé en 1 o 2 oraciones usando solo la respuesta de la API de disponibilidad. Si el horario pedido no está disponible, ofrecé el más cercano que sí.";
            usuario = $"Pregunta: {pregunta}\nRespuesta de la API: {J.Py(resultado)}";
        }
        else
        {
            salida["herramienta"] = new Dict { ["tipo"] = "Prompt", ["pedido"] = "Ficha fija en el prompt", ["resultado"] = Ruta.Ficha };
            sistema = $"Sos el asistente de MesaYa. Respondé en 1 o 2 oraciones con esta ficha:\n{Ruta.Ficha}";
            usuario = pregunta;
        }
        var mensajes = new List<Dict>
        {
            new() { ["role"] = "system", ["content"] = sistema },
            new() { ["role"] = "user", ["content"] = usuario },
        };
        salida["mensajes"] = mensajes;
        var respuesta = await ollama.Chat(mensajes);
        salida["respuesta"] = respuesta;
        etapas.Add(new Dict { ["nombre"] = "Generar", ["ms"] = respuesta.G("ms") });
        salida["abstencion"] = false;
        salida["verificacion"] = null;
        var q = preguntaId != null ? PorId.GetValueOrDefault(preguntaId) : null;
        var esperada = q?.S("ruta_esperada");
        salida["diagnostico"] = new List<Dict>
        {
            new()
            {
                ["id"] = "ruta", ["pregunta"] = "¿Fue a la fuente correcta?",
                ["estado"] = q == null || esperada == null || esperada == r ? "ok" : "falla",
                ["detalle"] = $"Ruta: {r}. {destino.Str("motivo")}",
            },
        };
        salida["ms_total"] = Ms(t0);
        salida["grabado"] = respuesta.G("grabado");
        return salida;
    }

    // ---------- evaluación (slide 15) ----------
    static readonly List<Dict> ConfigsEval =
    [
        new() { ["nombre"] = "Semántica · fijo", ["chunking"] = "fijo", ["busqueda"] = "semantica" },
        new() { ["nombre"] = "Semántica · sección", ["chunking"] = "seccion", ["busqueda"] = "semantica" },
        new() { ["nombre"] = "Palabras · sección", ["chunking"] = "seccion", ["busqueda"] = "palabras" },
        new() { ["nombre"] = "Híbrida · sección", ["chunking"] = "seccion", ["busqueda"] = "hibrida" },
        new() { ["nombre"] = "Híbrida + vigentes", ["chunking"] = "seccion", ["busqueda"] = "hibrida", ["solo_vigentes"] = true },
        new() { ["nombre"] = "Híbrida + vigentes + jerarquía", ["chunking"] = "seccion", ["busqueda"] = "hibrida", ["solo_vigentes"] = true, ["priorizar_oficiales"] = true },
    ];

    public static async Task<Dict> EvaluarRecuperacion(int k)
    {
        var filas = new List<Dict>();
        var preguntas = Preguntas.Ds("preguntas").Where(q => q.S("tipo") is "recuperacion" or "sin_acceso").ToList();
        foreach (var c in ConfigsEval)
        {
            var detalle = new List<Dict>();
            var hits = new List<bool>();
            var rrs = new List<double>();
            var precs = new List<double>();
            var recs = new List<double>();
            var viol = 0;
            foreach (var q in preguntas)
            {
                var sinNombre = new Dict(c);
                sinNombre.Remove("nombre");
                var cfg = ConfigBase.Con(new Dict { ["k"] = k }).Con(sinNombre).Con(new Dict
                {
                    ["perfil"] = q.G("perfil"), ["tenant"] = q.S("tenant_id") ?? "mesaya-ar", ["rerank"] = false,
                });
                var historial = q.Ds("historial");
                var consulta = historial.Count == 0
                    ? q.Str("pregunta")
                    : string.Join(" ", historial.Where(h => h.Str("rol") == "usuario").Select(h => h.Str("texto"))) + " " + q.Str("pregunta");
                var rec = await Busqueda.Buscar(consulta, cfg);
                var esperados = q.Ds("esperados");
                var candidatos = rec.Ds("candidatos");
                foreach (var f in candidatos) f["relevante"] = Busqueda.EsRelevante(f, esperados);
                var m = Metricas(candidatos, k, Busqueda.TotalRelevantes(cfg, esperados), Textos(q, "prohibidos"));
                var violo = Textos(m, "violaciones").Count > 0;
                if (violo) viol++;
                var esRec = q.S("tipo") == "recuperacion";
                if (esRec)
                {
                    hits.Add(m.B("hit")); rrs.Add(m.D("rr")); precs.Add(m.D("precision")); recs.Add(m.D("recall"));
                }
                detalle.Add(new Dict
                {
                    ["id"] = q.G("id"), ["hit"] = esRec ? m.B("hit") : null, ["pos"] = m.G("primer_relevante"), ["violacion"] = violo,
                });
            }
            double n = hits.Count > 0 ? hits.Count : 1;
            filas.Add(new Dict
            {
                ["nombre"] = c.G("nombre"), ["hit"] = Texto.Redondear(hits.Count(h => h) / n, 2),
                ["mrr"] = Texto.Redondear(rrs.Sum() / n, 2), ["precision"] = Texto.Redondear(precs.Sum() / n, 2),
                ["recall"] = Texto.Redondear(recs.Sum() / n, 2), ["violaciones"] = viol, ["detalle"] = detalle,
            });
        }
        return new Dict
        {
            ["k"] = k, ["filas"] = filas,
            ["preguntas"] = preguntas.Select(q => new Dict { ["id"] = q.G("id"), ["pregunta"] = q.G("pregunta"), ["tipo"] = q.G("tipo") }).ToList(),
        };
    }

    /// <summary>Corre el set completo con la configuración actual. Devuelve una fila por pregunta.</summary>
    public static async IAsyncEnumerable<Dict> EvaluarRespuestas(Dict config)
    {
        var preguntas = Preguntas.Ds("preguntas").Where(q => q.S("tipo") != "ruta").ToList();
        for (var i = 1; i <= preguntas.Count; i++)
        {
            var q = preguntas[i - 1];
            var cfg = config.Con(new Dict
            {
                ["perfil"] = q.G("perfil"), ["tenant"] = q.S("tenant_id") ?? "mesaya-ar", ["verificar"] = true, ["enrutar"] = false,
            });
            Dict fila;
            try
            {
                var r = await CorrerTurno(q.Str("pregunta"), cfg, q.Str("id"), q.Ds("historial"));
                var v = r.O("verificacion") ?? new Dict();
                var abstuvo = r.B("abstencion");
                fila = new Dict
                {
                    ["i"] = i, ["n"] = preguntas.Count, ["id"] = q.G("id"), ["tipo"] = q.G("tipo"), ["pregunta"] = q.G("pregunta"),
                    ["respuesta"] = r.O("respuesta")!.G("texto"), ["abstencion"] = abstuvo,
                    ["fidelidad"] = v.G("fidelidad"), ["correcta"] = v.G("correcta"),
                    ["abstencion_correcta"] = q.S("tipo") is "sin_acceso" or "fuera_corpus" ? abstuvo : null,
                    ["hit"] = r.O("recuperacion")?.O("metricas")?.G("hit"),
                };
            }
            catch (Exception e)
            {
                fila = new Dict { ["i"] = i, ["n"] = preguntas.Count, ["id"] = q.G("id"), ["error"] = e.Message };
            }
            yield return fila;
        }
    }
}
