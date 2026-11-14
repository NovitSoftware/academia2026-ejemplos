using System.Diagnostics;
using MesaYa.Comun;
using static MesaYa.Control.Esquema;

namespace MesaYa.Control;

/// <summary>
/// El router: convierte texto libre en una intención de un set cerrado, con su confianza (slides 03–05).
/// Cuatro métodos: reglas, embeddings, LLM e híbrido (reglas para lo obvio, LLM para el resto).
/// </summary>
public static class Router
{
    public static readonly List<Dict> Metodos =
    [
        new() { ["id"] = "reglas", ["nombre"] = "Reglas", ["resumen"] = "palabras clave: gratis y predecible" },
        new() { ["id"] = "embeddings", ["nombre"] = "Embeddings", ["resumen"] = "parecido con ejemplos de cada intención" },
        new() { ["id"] = "llm", ["nombre"] = "LLM", ["resumen"] = "un prompt con la lista y salida JSON" },
        new() { ["id"] = "hibrido", ["nombre"] = "Híbrido", ["resumen"] = "reglas si son claras; si no, LLM" },
    ];

    /// <summary>Ejemplos por intención para el método de embeddings (vienen de escenarios.json).</summary>
    public static Dictionary<string, List<string>> Ejemplos { get; set; } = [];

    public static async Task<Dict> Clasificar(Traza t, string mensaje, string metodo)
    {
        switch (metodo)
        {
            case "reglas":
                return PorReglas(t, mensaje, true);
            case "embeddings":
                return await PorEmbeddings(t, mensaje);
            case "hibrido":
                var r = PorReglas(t, mensaje, false);
                if (r.D("confianza") >= 0.9)
                {
                    t.Pasos.Last()["detalle"] = "las reglas son claras: no hace falta llamar al LLM";
                    return r.Con(new Dict { ["metodo"] = "hibrido" });
                }
                t.Pasos.Last()["detalle"] = "las reglas no alcanzan: se le pregunta al LLM";
                t.Pasos.Last()["marca"] = "aviso";
                return (await PorLlm(t, mensaje)).Con(new Dict { ["metodo"] = "hibrido" });
            default:
                return await PorLlm(t, mensaje);
        }
    }

    // ---------- 1. reglas ----------
    static readonly Dictionary<string, string[]> Palabras = new()
    {
        ["reserva"] = ["reservar", "mesa para", "mesa en", "lugar para", "una reserva para"],
        ["cancelacion"] = ["cancelar", "cancelo", "cancele", "cancelacion", "no vamos a poder", "no podemos ir", "sena"],
        ["reembolso"] = ["devolver", "devuelvan", "devolveme", "devolvieron", "devolucion", "reembolso", "reintegr", "mi plata", "mi dinero"],
        ["alergenos"] = ["tacc", "celiac", "gluten", "alergi", "vegan", "vegetarian", "lactosa", "ingrediente"],
        ["sucursales"] = ["horario", "abren", "abre", "cierra", "direccion", "donde queda", "terraza", "jardin"],
        ["mis_reservas"] = ["mi reserva", "reservo", "reserve", "tengo reserva", "tengo una reserva"],
        ["gerencia"] = ["sueldo", "salario", "cuanto gana", "pagan", "margen", "proveedor", "costo"],
        ["reclamo"] = ["queja", "reclamo", "horrible", "fria", "frio", "tardaron", "intoxic", "pesimo", "vomit", "mal atendid"],
    };

    static Dict PorReglas(Traza t, string mensaje, bool sola)
    {
        var plano = Texto.Plano(mensaje);
        var puntos = Palabras.ToDictionary(p => p.Key, p => p.Value.Count(w => plano.Contains(w)));
        var orden = puntos.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ToList();
        var total = orden.Sum(p => p.Value);
        string intencion = "otro", alternativa = "otro";
        double confianza = 0;
        if (orden.Count > 0)
        {
            intencion = orden[0].Key;
            alternativa = orden.Count > 1 ? orden[1].Key : "otro";
            confianza = Math.Round(0.95 * orden[0].Value / total, 2);
        }
        var encontradas = Palabras.SelectMany(p => p.Value.Where(w => plano.Contains(w)).Select(w => $"{w} → {p.Key}")).ToList();
        t.Agregar(Traza.Paso("codigo", "Router · reglas", sola ? "palabras clave por intención, sin llamar a ningún modelo" : "primero las reglas",
            new Dict { ["coincidencias"] = encontradas, ["intencion"] = intencion, ["confianza"] = confianza }));
        return new Dict
        {
            ["metodo"] = "reglas", ["intencion"] = intencion, ["alternativa"] = alternativa, ["confianza"] = confianza,
            ["puntajes"] = puntos.Where(p => p.Value > 0).Select(p => new Dict { ["intencion"] = p.Key, ["puntaje"] = p.Value }).ToList(),
        };
    }

    // ---------- 2. embeddings ----------
    static readonly Dictionary<string, List<float[]>> Vectores = [];
    static readonly SemaphoreSlim Candado = new(1, 1);

    static async Task<Dictionary<string, List<float[]>>> VectoresEjemplos()
    {
        await Candado.WaitAsync();
        try
        {
            foreach (var (intencion, ejemplos) in Ejemplos)
            {
                if (Vectores.ContainsKey(intencion)) continue;
                var lista = new List<float[]>();
                // De a 4: así cada lote queda grabado y sirve en modo guion.
                foreach (var lote in ejemplos.Chunk(4)) lista.AddRange(await Ollama.Instancia.Embeber([.. lote]));
                Vectores[intencion] = lista;
            }
            return Vectores;
        }
        finally { Candado.Release(); }
    }

    static double Coseno(float[] a, float[] b)
    {
        double p = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { p += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return p / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-9);
    }

    static async Task<Dict> PorEmbeddings(Traza t, string mensaje)
    {
        var reloj = Stopwatch.StartNew();
        var ejemplos = await VectoresEjemplos();
        var v = (await Ollama.Instancia.Embeber([mensaje]))[0];
        var sim = ejemplos.ToDictionary(e => e.Key, e => e.Value.Max(x => Coseno(v, x)));
        // Softmax con temperatura baja: convierte similitudes cercanas (0,6 vs 0,7) en una confianza legible.
        var exp = sim.ToDictionary(s => s.Key, s => Math.Exp(s.Value / 0.04));
        var suma = exp.Values.Sum();
        var orden = sim.OrderByDescending(s => s.Value).ToList();
        var intencion = orden[0].Key;
        var confianza = Math.Round(exp[intencion] / suma, 2);
        var paso = Traza.Paso("embed", "Router · embeddings", "similitud del mensaje con los ejemplos de cada intención",
            orden.Take(4).Select(s => new Dict { ["intencion"] = s.Key, ["similitud"] = Math.Round(s.Value, 3) }).ToList());
        paso["ms"] = reloj.ElapsedMilliseconds;
        t.Agregar(paso);
        return new Dict
        {
            ["metodo"] = "embeddings", ["intencion"] = intencion, ["alternativa"] = orden[1].Key, ["confianza"] = confianza,
            ["puntajes"] = orden.Select(s => new Dict { ["intencion"] = s.Key, ["puntaje"] = Math.Round(s.Value, 3) }).ToList(),
        };
    }

    // ---------- 3. LLM ----------
    static Dict EsquemaRouter() => Objeto(new Dict
    {
        ["intencion"] = Opciones([.. Datos.Intenciones.Select(i => i.Str("id"))]),
        ["alternativa"] = Opciones([.. Datos.Intenciones.Select(i => i.Str("id"))]),
        ["confianza"] = Opciones("alta", "media", "baja"),
    });

    // Un modelo chico dice 0,95 para todo si se le pide un número. Con categorías y un criterio, discrimina mejor.
    static readonly Dictionary<string, double> Niveles = new() { ["alta"] = 0.9, ["media"] = 0.6, ["baja"] = 0.3 };

    static string SistemaRouter() =>
        "Clasificá el mensaje que llega al asistente de un restaurante en UNA de estas intenciones:\n" +
        string.Join("\n", Datos.Intenciones.Select(i => $"- {i.Str("id")}: {i.Str("descripcion")}")) +
        "\nDevolvé la intención principal, la segunda más probable (alternativa) y tu confianza: " +
        "alta si el mensaje dice explícitamente lo que quiere; media si hay que interpretarlo; baja si podría ser otra intención.";

    static async Task<Dict> PorLlm(Traza t, string mensaje)
    {
        var (d, _, paso) = await t.Llm("Router · LLM", "clasifica con la lista de intenciones y devuelve JSON",
            Traza.M(SistemaRouter(), mensaje), EsquemaRouter(), maxTokens: 60);
        t.Agregar(paso);
        var ids = Datos.Intenciones.Select(i => i.Str("id")).ToList();
        var intencion = ids.Contains(d.Str("intencion")) ? d.Str("intencion") : "otro";
        return new Dict
        {
            ["metodo"] = "llm", ["intencion"] = intencion, ["alternativa"] = d.Str("alternativa", "otro"),
            ["confianza"] = Niveles.GetValueOrDefault(d.Str("confianza"), 0.3), ["nivel"] = d.Str("confianza", "baja"),
            ["puntajes"] = new List<object?>(),
        };
    }
}
