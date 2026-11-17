using MesaYa.Comun;

namespace MesaYa.Proveedores;

/// <summary>Un trabajo largo (ráfaga o pipeline). La interfaz lo consulta mientras corre para mostrar cada paso.</summary>
public sealed class Trabajo(string tipo)
{
    public string Tipo { get; } = tipo;
    public string Estado { get; set; } = "corriendo";
    public string? Actual { get; set; }
    public Dict? Resultado { get; set; }
    public string? Error { get; set; }
    readonly List<Dict> eventos = [];
    readonly DateTime inicio = DateTime.UtcNow;

    public long Ms => (long)(DateTime.UtcNow - inicio).TotalMilliseconds;

    public Dict Evento(Dict e)
    {
        lock (eventos) { e["t_ms"] = Ms; eventos.Add(e); }
        return e;
    }

    public List<Dict> Eventos() { lock (eventos) return eventos.ToList(); }

    public Dict Foto() => new()
    {
        ["tipo"] = Tipo, ["estado"] = Estado, ["actual"] = Actual, ["eventos"] = Eventos(), ["resultado"] = Resultado, ["error"] = Error, ["ms"] = Ms,
    };
}

/// <summary>Los tres tipos de corrida de la demo: un request suelto, una ráfaga contra el rate limit y un pipeline con un modelo por propósito.</summary>
public static class Demo
{
    static Cliente C => Cliente.Instancia;

    /// <summary>Modelo de cada familia por proveedor (appsettings, sección Modelos).</summary>
    public static Dictionary<string, Dictionary<string, string>> Modelos { get; set; } = [];
    public static Dict LimiteBase { get; set; } = new();

    public static string Modelo(string proveedor, string familia) =>
        Modelos.TryGetValue(proveedor, out var f) && f.TryGetValue(familia, out var m) ? m
        : Modelos["ollama"][familia];

    const string Sistema = "Sos el asistente de MesaYa, una cadena de restaurantes. Respondé en español, en una o dos oraciones.";

    // ---------- A y D · un request ----------
    public static async Task<Dict> Request(Dict p)
    {
        C.Limites.Clear();  // el límite simulado es solo para las ráfagas
        var prov = C.Proveedor(p.Str("proveedor", "ollama"));
        var modelo = p.S("modelo") is { Length: > 0 } m ? m : Modelo(prov.Id, p.Str("familia", "generalista"));
        var original = p.Str("mensaje");
        var (enviado, hallados) = p.B("filtro") ? Datos.Filtrar(original) : (original, new List<Dict>());
        if (!p.B("filtro")) hallados = Datos.Filtrar(original).hallados.Select(h => h.Con(new Dict { ["viaja"] = true })).ToList();
        var item = new Dict
        {
            ["tipo"] = "request", ["mensaje"] = original, ["enviado"] = enviado, ["filtro"] = p.B("filtro"), ["datos_personales"] = hallados,
            ["proveedor"] = prov.Id, ["modelo"] = modelo,
        };
        try
        {
            item["resultado"] = await C.Completar(new Pedido
            {
                Proveedor = prov, Modelo = modelo, Mensajes = Cliente.M(Sistema, enviado), MaxTokens = 160, Stream = true,
            });
            item["ok"] = true;
        }
        catch (ErrorProveedor e)
        {
            item["resultado"] = e.Resultado;
            item["ok"] = false;
        }
        return item;
    }

    // ---------- B · ráfaga contra el rate limit ----------
    static readonly string[] Preguntas =
    [
        "¿Puedo ir con mi perro a Belgrano?", "¿Hasta qué hora abren el sábado?", "¿Tienen carta sin TACC?",
        "¿Dónde estaciono en Palermo?", "¿Cuánto es la seña para 10 personas?", "Si cancelo 5 horas antes, ¿qué pasa con la seña?",
        "¿Puedo llevar torta para un cumpleaños?", "¿Aceptan tarjeta de crédito?",
    ];

    static string SistemaPoliticas() =>
        Sistema + "\nUsá solo estas políticas:\n" + string.Join("\n", Datos.Politicas.Select(x => $"- {x.Str("titulo")}: {x.Str("texto")}"));

    public static async Task Rafaga(Trabajo t, Dict p)
    {
        var prov = C.Proveedor(p.Str("proveedor", "ollama"));
        var modelo = p.S("modelo") is { Length: > 0 } m ? m : Modelo(prov.Id, "generalista");
        var respaldo = (prov: C.Proveedor("ollama"), modelo: Modelo("ollama", "economico"));
        var estrategia = p.Str("estrategia", "sin");
        var cantidad = Math.Clamp(p.I("cantidad", Preguntas.Length), 1, Preguntas.Length);

        // Un límite nuevo en cada ráfaga, para que el guion sea repetible.
        var lim = new Limitador(LimiteBase.I("Requests", 6), LimiteBase.I("Tokens", 2400), LimiteBase.I("VentanaS", 15));
        C.Limites[Cliente.Llave(prov.Id, modelo)] = lim;
        var simulado = C.LimiteSimulado;
        t.Resultado = new Dict
        {
            ["proveedor"] = prov.Id, ["modelo"] = modelo, ["estrategia"] = estrategia, ["cantidad"] = (long)cantidad, ["simulado"] = simulado,
            ["respaldo"] = $"{respaldo.prov.Nombre} · {respaldo.modelo}", ["limite"] = lim.Estado(),
        };

        // Una ráfaga real: los pedidos salen juntos, cada uno con su propia política de reintentos.
        long ok = 0, con429 = 0, fallidos = 0, porRespaldo = 0, esperaTotal = 0, terminados = 0;
        async Task Pedir(int n)
        {
            await Task.Delay(150 * (n - 1));  // escalonados apenas, para que se lea el orden
            var random = new Random(n);
            var actual = (prov, modelo);
            var intento = 0;
            var tuvo429 = false;
            while (true)
            {
                intento++;
                var inicio = t.Ms;
                try
                {
                    var r = await C.Completar(new Pedido
                    {
                        Proveedor = actual.prov, Modelo = actual.modelo, Mensajes = Cliente.M(SistemaPoliticas(), Preguntas[n - 1]), MaxTokens = 80,
                    });
                    t.Evento(new Dict
                    {
                        ["n"] = (long)n, ["intento"] = (long)intento, ["tipo"] = "ok", ["estado"] = 200L, ["inicio_ms"] = inicio,
                        ["proveedor"] = actual.prov.Id, ["modelo"] = actual.modelo, ["pregunta"] = Preguntas[n - 1], ["respuesta"] = r.Str("texto"),
                        ["tokens"] = (long)(r.I("tokens_entrada") + r.I("tokens_salida")), ["ms"] = r.G("ms"), ["uso"] = lim.Estado(), ["resultado"] = r,
                    });
                    Interlocked.Increment(ref ok);
                    if (actual.prov != prov || actual.modelo != modelo) Interlocked.Increment(ref porRespaldo);
                    break;
                }
                catch (ErrorProveedor e) when (e.Estado is 429 or 408 or >= 500)
                {
                    tuvo429 |= e.Estado == 429;
                    t.Evento(new Dict
                    {
                        ["n"] = (long)n, ["intento"] = (long)intento, ["tipo"] = "error", ["estado"] = (long)e.Estado, ["inicio_ms"] = inicio,
                        ["proveedor"] = actual.prov.Id, ["modelo"] = actual.modelo, ["pregunta"] = Preguntas[n - 1], ["error"] = e.Message,
                        ["retry_after"] = e.ReintentarEn, ["uso"] = lim.Estado(), ["resultado"] = e.Resultado,
                    });
                    var maximo = estrategia == "fallback" ? 1 : 3;
                    if (estrategia == "sin" || intento > maximo)
                    {
                        if (estrategia == "fallback" && actual != respaldo)
                        {
                            t.Evento(new Dict { ["n"] = (long)n, ["tipo"] = "respaldo", ["nota"] = $"Pasa al respaldo: {respaldo.prov.Nombre} · {respaldo.modelo}" });
                            actual = respaldo;
                            intento = 0;
                            continue;
                        }
                        t.Evento(new Dict { ["n"] = (long)n, ["tipo"] = "fallo", ["nota"] = estrategia == "sin" ? "Sin reintentos: el pedido falla" : "Se agotaron los reintentos" });
                        Interlocked.Increment(ref fallidos);
                        break;
                    }
                    // Backoff exponencial con jitter, o lo que diga el proveedor en Retry-After.
                    var espera = estrategia == "retry_after" && e.ReintentarEn is { } ra
                        ? (long)(ra * 1000)
                        : (long)(1000 * Math.Pow(2, intento - 1)) + random.Next(0, 300);
                    t.Evento(new Dict
                    {
                        ["n"] = (long)n, ["tipo"] = "espera", ["ms"] = espera,
                        ["nota"] = estrategia == "retry_after" && e.ReintentarEn != null ? $"Espera lo que pide Retry-After: {espera / 1000.0:0.#} s" : $"Backoff: espera {espera / 1000.0:0.#} s",
                    });
                    Interlocked.Add(ref esperaTotal, espera);
                    await Task.Delay((int)espera);
                }
            }
            if (tuvo429) Interlocked.Increment(ref con429);
            t.Actual = $"{cantidad} pedidos en paralelo · {Interlocked.Increment(ref terminados)} terminados";
        }
        t.Actual = $"{cantidad} pedidos en paralelo";
        await Task.WhenAll(Enumerable.Range(1, cantidad).Select(Pedir));
        C.Limites.Remove(Cliente.Llave(prov.Id, modelo));  // el límite es solo para esta ráfaga
        t.Resultado = t.Resultado.Con(new Dict
        {
            ["ok"] = ok, ["con_429"] = con429, ["fallidos"] = fallidos, ["por_respaldo"] = porRespaldo, ["espera_ms"] = esperaTotal, ["ms_total"] = t.Ms,
        });
    }

    // ---------- C · un modelo por propósito ----------
    static Dict Paso(string familia, string titulo, string proveedor, string modelo) => new()
    {
        ["familia"] = familia, ["titulo"] = titulo, ["proveedor"] = proveedor, ["modelo"] = modelo,
    };

    static Dict Medidas(Dict r) => new()
    {
        ["tokens_entrada"] = r.G("tokens_entrada"), ["tokens_salida"] = r.G("tokens_salida"), ["ms"] = r.G("ms"),
        ["request"] = r.G("request"), ["grabado"] = r.G("grabado"), ["estructura"] = r.G("estructura"),
    };

    public static async Task Pipeline(Trabajo t, Dict p)
    {
        C.Limites.Clear();
        var prov = C.Proveedor(p.Str("proveedor", "ollama"));
        var local = C.Proveedor("ollama");
        if (p.Str("caso") == "factura") await Factura(t, prov, local);
        else await Reserva(t, prov, local, p.Str("mensaje", "Hola, quiero reservar para 6 el viernes a las 21 en Palermo. Uno de nosotros es celíaco."));
        var pasos = t.Eventos();
        t.Resultado = new Dict
        {
            ["caso"] = p.Str("caso", "reserva"), ["llamadas"] = (long)pasos.Count(x => x.G("ms") != null && x.Str("familia") != "código"),
            ["modelos"] = pasos.Select(x => x.Str("modelo")).Where(x => x.Length > 0 && x != "—").Distinct().ToList(),
            ["tokens"] = pasos.Sum(x => (long)(x.D("tokens_entrada") + x.D("tokens_salida"))), ["ms_total"] = t.Ms,
        };
    }

    static async Task Reserva(Trabajo t, Proveedor prov, Proveedor local, string mensaje)
    {
        t.Evento(Paso("audio", "Audio → texto", "—", "—").Con(new Dict
        {
            ["omitido"] = true, ["salida"] = mensaje,
            ["nota"] = "En producción, un modelo de audio transcribe la nota de voz. La demo parte del texto ya transcripto.",
        }));

        // 1 · Económico: clasificar
        var economico = Modelo(prov.Id, "economico");
        t.Actual = $"Clasificando con {economico}…";
        var esquema = new Dict
        {
            ["type"] = "object",
            ["properties"] = new Dict { ["intencion"] = new Dict { ["type"] = "string", ["enum"] = new List<object?> { "reservar", "cancelar", "consulta", "reclamo" } } },
            ["required"] = new List<object?> { "intencion" },
        };
        var r1 = await C.Completar(new Pedido
        {
            Proveedor = prov, Modelo = economico, Esquema = esquema, MaxTokens = 40,
            Mensajes = Cliente.M("Clasificá el mensaje de un cliente de MesaYa.", mensaje),
        });
        var intencion = Leer(r1.Str("texto")).Str("intencion", "?");
        t.Evento(Paso("económico", "Clasificar la intención", prov.Id, economico).Con(Medidas(r1)).Con(new Dict { ["salida"] = new Dict { ["intencion"] = intencion } }));

        // 2 · Embeddings: la política más parecida
        var embeddings = Modelo("ollama", "embeddings");
        t.Actual = $"Buscando la política con {embeddings}…";
        var (corpus, _) = await C.Embeber(local, embeddings, Datos.Politicas.Select(x => x.Str("texto")).ToList());
        var (consulta, r2) = await C.Embeber(local, embeddings, [mensaje]);
        var ranking = Datos.Politicas.Select((x, i) => new Dict { ["id"] = x.Str("id"), ["titulo"] = x.Str("titulo"), ["texto"] = x.Str("texto"), ["similitud"] = Math.Round(Cliente.Coseno(consulta[0], corpus[i]), 3) })
            .OrderByDescending(x => x.D("similitud")).ToList();
        var relevantes = ranking.Take(2).ToList();
        t.Evento(Paso("embeddings", "Buscar las políticas relevantes", local.Id, embeddings).Con(new Dict
        {
            ["ms"] = r2.G("ms"), ["tokens_entrada"] = r2.G("tokens_entrada"), ["request"] = r2.G("request"), ["grabado"] = r2.G("grabado"),
            ["salida"] = ranking.Take(3).Select(x => new Dict { ["titulo"] = x.Str("titulo"), ["similitud"] = x.G("similitud") }).ToList(),
        }));

        // 3 · Generalista: tool calling
        var generalista = Modelo(prov.Id, "generalista");
        t.Actual = $"Eligiendo la tool con {generalista}…";
        var sistema = $"Sos el asistente de reservas de MesaYa. Hoy es {Datos.Hoy}. Si el cliente quiere reservar, consultá la disponibilidad con la tool. " +
                      "Políticas relevantes:\n" + string.Join("\n", relevantes.Select(x => $"- {x.Str("titulo")}: {x.Str("texto")}"));
        var mensajes = Cliente.M(sistema, mensaje);
        var r3 = await C.Completar(new Pedido { Proveedor = prov, Modelo = generalista, Mensajes = mensajes, Tools = Datos.Tools, MaxTokens = 200 });
        var llamada = r3.Ds("tool_calls").FirstOrDefault();
        t.Evento(Paso("generalista", "Elegir la tool (tool calling)", prov.Id, generalista).Con(Medidas(r3)).Con(new Dict
        {
            ["salida"] = llamada != null ? llamada : r3.Str("texto"),
        }));
        if (llamada == null) return;

        // 4 · La tool corre en código y el modelo redacta la respuesta
        var args = llamada.O("argumentos") ?? new Dict();
        var disponibilidad = Datos.BuscarDisponibilidad(args);
        t.Evento(Paso("código", "Ejecutar buscar_disponibilidad", "—", "—").Con(new Dict { ["salida"] = disponibilidad, ["ms"] = 40L }));
        var cruda = r3.Ds("tool_calls_crudas").First();
        mensajes.Add(new Dict { ["role"] = "assistant", ["content"] = r3.Str("texto"), ["tool_calls"] = new List<object?> { cruda } });
        mensajes.Add(new Dict { ["role"] = "tool", ["tool_call_id"] = cruda.Str("id"), ["content"] = J.Serializar(disponibilidad) });
        t.Actual = $"Redactando la respuesta con {generalista}…";
        var r4 = await C.Completar(new Pedido { Proveedor = prov, Modelo = generalista, Mensajes = mensajes, Tools = Datos.Tools, MaxTokens = 220 });
        t.Evento(Paso("generalista", "Responder al cliente", prov.Id, generalista).Con(Medidas(r4)).Con(new Dict { ["salida"] = r4.Str("texto") }));
    }

    static async Task Factura(Trabajo t, Proveedor prov, Proveedor local)
    {
        var vision = Modelo("ollama", "vision");
        t.Actual = $"Leyendo la factura con {vision}…";
        var imagen = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "static", "img", "factura.png")));
        var esquema = new Dict
        {
            ["type"] = "object",
            ["properties"] = new Dict
            {
                ["proveedor"] = new Dict { ["type"] = "string" },
                ["items"] = new Dict
                {
                    ["type"] = "array",
                    ["items"] = new Dict
                    {
                        ["type"] = "object",
                        ["properties"] = new Dict { ["descripcion"] = new Dict { ["type"] = "string" }, ["importe"] = new Dict { ["type"] = "number" } },
                        ["required"] = new List<object?> { "descripcion", "importe" },
                    },
                },
                ["total"] = new Dict { ["type"] = "number" },
            },
            ["required"] = new List<object?> { "proveedor", "items", "total" },
        };
        var mensajes = new List<Dict>
        {
            new()
            {
                ["role"] = "user",
                ["content"] = new List<object?>
                {
                    new Dict { ["type"] = "text", ["text"] = "Extraé los datos de esta factura. Importes en pesos, como números sin separadores de miles." },
                    new Dict { ["type"] = "image_url", ["image_url"] = new Dict { ["url"] = $"data:image/png;base64,{imagen}" } },
                },
            },
        };
        var r1 = await C.Completar(new Pedido { Proveedor = local, Modelo = vision, Mensajes = mensajes, Esquema = esquema, MaxTokens = 300 });
        var datos = Leer(r1.Str("texto"));
        t.Evento(Paso("visión", "Leer la factura (imagen → JSON)", local.Id, vision).Con(Medidas(r1)).Con(new Dict { ["salida"] = datos, ["imagen"] = "/static/img/factura.png" }));

        // Lo que se extrae, se valida: en código, no con otro modelo.
        var suma = datos.Ds("items").Sum(i => i.D("importe"));
        var total = datos.D("total");
        var cierra = Math.Abs(suma - total) < 1;
        t.Evento(Paso("código", "Validar que el total cierre", "—", "—").Con(new Dict
        {
            ["ms"] = 2L, ["salida"] = new Dict { ["suma_items"] = suma, ["total_leido"] = total, ["cierra"] = cierra, ["decision"] = cierra ? "cargar" : "revisión humana" },
        }));

        var economico = Modelo(prov.Id, "economico");
        t.Actual = $"Redactando el aviso con {economico}…";
        var r2 = await C.Completar(new Pedido
        {
            Proveedor = prov, Modelo = economico, MaxTokens = 120,
            Mensajes = Cliente.M("Redactá un aviso de dos oraciones para el encargado de compras de MesaYa.",
                $"Factura de {datos.Str("proveedor")}: la suma de los ítems da ${suma:N0} pero el total impreso es ${total:N0}. " + (cierra ? "Cierra." : "No cierra: hay que revisarla antes de pagar.")),
        });
        t.Evento(Paso("económico", "Avisar al encargado", prov.Id, economico).Con(Medidas(r2)).Con(new Dict { ["salida"] = r2.Str("texto") }));
    }

    static Dict Leer(string texto)
    {
        try { return J.Leer(texto.Trim().Trim('`').Replace("json\n", "")) as Dict ?? new Dict(); }
        catch (System.Text.Json.JsonException) { return new Dict(); }
    }
}
