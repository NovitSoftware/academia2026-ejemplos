using MesaYa.Comun;
using static MesaYa.Control.Esquema;

namespace MesaYa.Control;

/// <summary>
/// Los agentes de control que prueban el sistema (slides 07, 11, 12 y 14):
/// medir el router con un set etiquetado, generar casos con un LLM, correr la regresión y atacar.
/// </summary>
public static class Pruebas
{
    /// <summary>Clasifica una lista de casos {mensaje, esperado} y arma la matriz de confusión.</summary>
    public static async Task<Dict> Medir(List<Dict> casos, string metodo, double umbral, string titulo)
    {
        var resultados = new List<Dict>();
        long llamadas = 0, tokens = 0, ms = 0;
        foreach (var c in casos)
        {
            var t = new Traza(new Dict { ["metodo"] = metodo });
            var r = await Router.Clasificar(t, c.Str("mensaje"), metodo);
            var s = t.Resumen();
            llamadas += s.I("llamadas"); tokens += (long)s.D("tokens"); ms += (long)s.D("ms_total");
            var conf = r.D("confianza");
            resultados.Add(new Dict
            {
                ["mensaje"] = c.Str("mensaje"), ["esperado"] = c.Str("esperado"), ["obtuvo"] = r.Str("intencion"), ["confianza"] = conf,
                ["ok"] = r.Str("intencion") == c.Str("esperado"), ["bajo_umbral"] = r.Str("intencion") != "otro" && conf < umbral,
                ["origen"] = c.G("origen"), ["llm"] = s.I("llamadas") > 0,
            });
        }
        var ids = Datos.Intenciones.Select(i => i.Str("id")).ToList();
        var matriz = ids.Select(e => ids.Select(o => (long)resultados.Count(r => r.Str("esperado") == e && r.Str("obtuvo") == o)).Cast<object?>().ToList()).Cast<object?>().ToList();
        var aciertos = resultados.Count(r => r.B("ok"));
        return new Dict
        {
            ["titulo"] = titulo, ["metodo"] = metodo, ["modelo"] = Ollama.Instancia.Modelo, ["umbral"] = umbral,
            ["fecha"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["casos"] = resultados, ["intenciones"] = ids, ["matriz"] = matriz,
            ["resumen"] = new Dict
            {
                ["total"] = (long)resultados.Count, ["aciertos"] = (long)aciertos,
                ["exactitud"] = resultados.Count == 0 ? 0 : Math.Round((double)aciertos / resultados.Count, 3),
                ["bajo_umbral"] = (long)resultados.Count(r => r.B("bajo_umbral")),
                ["errores_con_confianza"] = (long)resultados.Count(r => !r.B("ok") && !r.B("bajo_umbral") && r.Str("obtuvo") != "otro"),
                ["llamadas"] = llamadas, ["tokens"] = tokens, ["ms"] = ms,
            },
        };
    }

    // ---------- agente de testing (slide 11) ----------
    static readonly Dict EsquemaCasos = Objeto(new Dict
    {
        ["mensajes"] = new Dict { ["type"] = "array", ["minItems"] = 6, ["maxItems"] = 6, ["items"] = Cadena(160) },
    });

    public static async Task<Dict> Generar(string intencion, string metodo, double umbral)
    {
        var info = Datos.Intencion(intencion) ?? throw new ErrorHttp(400, $"Intención desconocida: {intencion}");
        var t = new Traza(new Dict());
        var (d, _, paso) = await t.Llm("Agente de testing · generar casos", $"6 formas de pedir: {info.Str("nombre")}",
            Traza.M("Sos un agente de testing. Generás mensajes de prueba realistas para el asistente de un restaurante de Montevideo. " +
                    "Escribí como escriben los clientes uruguayos por chat.",
                    $"Generá 6 mensajes distintos de un cliente que {info.Str("descripcion")}. Incluí: uno muy corto, uno con errores de tipeo, " +
                    "dos que NO usen las palabras más obvias del tema, uno con mucho contexto y uno que mezcle dos pedidos."),
            EsquemaCasos, maxTokens: 500, temperatura: 0.7);
        var casos = (d.G("mensajes") as List<object?> ?? []).OfType<string>().Where(m => m.Trim().Length > 0)
            .Select(m => new Dict { ["mensaje"] = m.Trim(), ["esperado"] = intencion, ["origen"] = "agente de testing" }).ToList();
        var medicion = await Medir(casos, metodo, umbral, $"Casos generados · {info.Str("nombre")}");
        medicion["generacion"] = new Dict { ["tokens"] = paso.G("tokens"), ["ms"] = paso.G("ms"), ["llamada"] = t.Llamadas.FirstOrDefault() };
        return medicion;
    }

    // ---------- ataques (slide 12) ----------
    public static async Task<Dict> Atacar(Dict ataque, Dict cfg)
    {
        var r = await Asistente.Turno(ataque.Str("mensaje"), ataque.Str("canal", "cliente"), cfg);
        var fugas = r.G("fugas") as List<string> ?? [];
        return new Dict
        {
            ["id"] = ataque.Str("id"), ["nombre"] = ataque.Str("nombre"), ["mensaje"] = ataque.Str("mensaje"), ["espera"] = ataque.Str("espera"),
            ["pasa"] = fugas.Count == 0, ["fugas"] = fugas, ["respuesta"] = r.Str("respuesta"), ["decision"] = r.Str("decision"),
            ["intencion"] = r.O("clasificacion")?.Str("intencion"), ["turno"] = r,
        };
    }
}
