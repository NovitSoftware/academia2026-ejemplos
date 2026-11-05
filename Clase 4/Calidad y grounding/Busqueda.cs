using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MesaYa.Comun;

namespace MesaYa.Calidad;

/// <summary>Índice de un chunking: vectores normalizados (bge-m3) y estadísticas de BM25.</summary>
public sealed class Indice
{
    public string Estrategia { get; }
    public List<Chunk> Chunks { get; }
    public float[][]? Vectores { get; private set; }

    readonly List<Dictionary<string, int>> tf;
    readonly double[] largos;
    readonly double prom;
    readonly Dictionary<string, double> idf;

    public Indice(string estrategia)
    {
        Estrategia = estrategia;
        Chunks = Corpus.Chunks[estrategia];
        var toks = Chunks.Select(c => Busqueda.Tokenizar(c.Texto)).ToList();
        tf = toks.Select(t => t.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count())).ToList();
        largos = toks.Select(t => (double)t.Count).ToArray();
        prom = largos.Average();
        var df = new Dictionary<string, int>();
        foreach (var w in toks.SelectMany(t => t.Distinct())) df[w] = df.GetValueOrDefault(w) + 1;
        var n = toks.Count;
        idf = df.ToDictionary(x => x.Key, x => Math.Log(1 + (n - x.Value + 0.5) / (x.Value + 0.5)));
    }

    static readonly string Cache = Environment.GetEnvironmentVariable("CACHE") ?? "datos";

    public async Task CargarVectores()
    {
        var ollama = Ollama.Instancia;
        Directory.CreateDirectory(Cache);
        var firma = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", Chunks.Select(c => c.Texto)) + ollama.Embeddings)))[..16];
        var f = Path.Combine(Cache, $"embeddings-{Estrategia}-{firma}.bin");
        if (File.Exists(f))
        {
            Vectores = LeerMatriz(f);
            return;
        }
        var vecs = new List<float[]>();
        for (var i = 0; i < Chunks.Count; i += 16)
            vecs.AddRange(await ollama.Embeber(Chunks.Skip(i).Take(16).Select(c => c.Texto).ToList()));
        var m = vecs.Select(Busqueda.Normalizar).ToArray();
        GuardarMatriz(f, m);
        Vectores = m;
    }

    static void GuardarMatriz(string f, float[][] m)
    {
        using var w = new BinaryWriter(File.Create(f));
        w.Write(m.Length);
        w.Write(m.Length > 0 ? m[0].Length : 0);
        foreach (var fila in m) foreach (var x in fila) w.Write(x);
    }

    static float[][] LeerMatriz(string f)
    {
        using var r = new BinaryReader(File.OpenRead(f));
        int filas = r.ReadInt32(), cols = r.ReadInt32();
        var m = new float[filas][];
        for (var i = 0; i < filas; i++)
        {
            m[i] = new float[cols];
            for (var j = 0; j < cols; j++) m[i][j] = r.ReadSingle();
        }
        return m;
    }

    public double[] Bm25(string consulta)
    {
        var q = Busqueda.Tokenizar(consulta);
        var s = new double[Chunks.Count];
        for (var i = 0; i < tf.Count; i++)
            foreach (var w in q)
                if (tf[i].TryGetValue(w, out var f))
                    s[i] += idf[w] * f * 2.2 / (f + 1.2 * (0.25 + 0.75 * largos[i] / prom));
        return s;
    }
}

/// <summary>
/// Recuperación en dos etapas (slide 03).
///
/// Etapa 1: semántica (bge-m3), palabras clave (BM25) o híbrida (RRF).
/// Filtros: audiencia del perfil, país, vigencia y jerarquía de fuentes.
/// Etapa 2 opcional: reranking con el LLM sobre los mejores candidatos.
/// Umbral: los fragmentos con similitud menor no entran al contexto (slide 04).
/// </summary>
public static class Busqueda
{
    static readonly HashSet<string> Stop = """
        a al algo como con de del el en es esta este hay la las lo los me mi mis o para pero por que qué se si sí sin
        su sus te tu un una uno y ya le les nos cuando cuándo donde dónde cual cuál cuanto cuánto cuántas cuántos puedo
        hago hace ser son fue era tiene tienen tengo quiero
        """.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    static readonly Dictionary<string, int> OrdenAutoridad = new() { ["oficial"] = 0, ["comunicacion"] = 1, ["historica"] = 2 };

    public static List<string> Tokenizar(string t) =>
        Regex.Matches(Texto.Plano(t), "[a-z0-9]+(?:-[a-z0-9]+)*")
            .Select(m => m.Value).Where(w => !Stop.Contains(w) && w.Length > 1).ToList();

    public static float[] Normalizar(float[] v)
    {
        var norma = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        return v.Select(x => x / norma).ToArray();
    }

    static float Punto(float[] a, float[] b)
    {
        float s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public static readonly Dictionary<string, Indice> Indices =
        Corpus.Chunks.Keys.ToDictionary(e => e, e => new Indice(e));

    /// <summary>Posición de cada puntaje (1 = el mayor), con empates en orden de aparición.</summary>
    static int[] Rangos(double[] puntajes)
    {
        var orden = Enumerable.Range(0, puntajes.Length).OrderByDescending(i => puntajes[i]).ToArray();
        var r = new int[puntajes.Length];
        for (var p = 0; p < orden.Length; p++) r[orden[p]] = p + 1;
        return r;
    }

    public static string? MotivoExclusion(Chunk c, Dict cfg)
    {
        var d = c.Doc;
        if (!d.Audiencia.Contains(cfg.Str("perfil"))) return $"audiencia: solo {string.Join(", ", d.Audiencia)}";
        if (d.Tenant != cfg.Str("tenant")) return "otro país";
        if (cfg.B("solo_vigentes") && d.Estado != "vigente") return $"no vigente (v{d.Version}, {Texto.Corte(d.Fecha, 4)})";
        return null;
    }

    static List<Dict> PorAutoridad(List<Dict> filas) =>
        filas.OrderBy(f => OrdenAutoridad[f.Str("autoridad")]).ToList();  // estable: respeta el orden previo

    public static async Task<Dict> Buscar(string consulta, Dict cfg)
    {
        var ix = Indices[cfg.Str("chunking")];
        var qv = Normalizar((await Ollama.Instancia.Embeber([consulta]))[0]);
        var sem = ix.Vectores!.Select(v => (double)Punto(v, qv)).ToArray();
        var bm = ix.Bm25(consulta);
        int[] rSem = Rangos(sem), rBm = Rangos(bm);
        var fusion = cfg.Str("busqueda") switch
        {
            "semantica" => sem.ToArray(),
            "palabras" => bm.ToArray(),
            // RRF con k=60: no importa la escala de cada puntaje, solo la posición
            _ => sem.Select((_, i) => 1.0 / (60 + rSem[i]) + 1.0 / (60 + (bm[i] > 0 ? rBm[i] : 10_000))).ToArray(),
        };
        var orden = Enumerable.Range(0, fusion.Length).OrderByDescending(i => fusion[i]).ToList();

        var permitidos = new List<Dict>();
        var excluidos = new List<Dict>();
        foreach (var i in orden)
        {
            var c = ix.Chunks[i];
            var fila = c.Ficha().Con(new Dict
            {
                ["sim"] = Texto.Redondear(sem[i], 3), ["bm25"] = Texto.Redondear(bm[i], 2),
                ["rank_sem"] = rSem[i], ["rank_bm25"] = bm[i] > 0 ? rBm[i] : null,
                ["fusion"] = Texto.Redondear(fusion[i], 4),
            });
            var motivo = MotivoExclusion(c, cfg);
            if (motivo != null)
            {
                if (excluidos.Count < 6 && permitidos.Count < 12)
                    excluidos.Add(fila.Con(new Dict { ["motivo"] = motivo }));
                continue;
            }
            permitidos.Add(fila);
        }

        var candidatos = permitidos.Take(20).ToList();
        if (cfg.B("priorizar_oficiales")) candidatos = PorAutoridad(candidatos);
        for (var p = 0; p < candidatos.Count; p++) candidatos[p]["pos_etapa1"] = p + 1;

        Dict? rerankInfo = null;
        if (cfg.B("rerank") && candidatos.Count > 0)
        {
            var cabezaPrevia = candidatos.Take(12).ToList();
            rerankInfo = await Reranquear(consulta, cabezaPrevia);
            var puntos = (Dictionary<string, int>)rerankInfo["puntajes"]!;
            var cabeza = cabezaPrevia
                .OrderBy(f => -puntos.GetValueOrDefault(f.Str("id")))
                .ThenBy(f => f.I("pos_etapa1")).ToList();
            foreach (var f in cabeza) f["rerank"] = puntos.GetValueOrDefault(f.Str("id"));
            candidatos = [.. cabeza, .. candidatos.Skip(12)];
            if (cfg.B("priorizar_oficiales")) candidatos = PorAutoridad(candidatos);
        }

        var k = cfg.I("k");
        var umbral = cfg.D("umbral");
        var contexto = new List<Dict>();
        for (var p = 0; p < candidatos.Count; p++)
        {
            var f = candidatos[p];
            f["pos"] = p + 1;
            f["en_k"] = p + 1 <= k;
            f["bajo_umbral"] = umbral > 0 && f.D("sim") < umbral;
            f["en_contexto"] = f.B("en_k") && !f.B("bajo_umbral");
            if (f.B("en_contexto")) contexto.Add(f);
        }
        return new Dict
        {
            ["candidatos"] = candidatos, ["excluidos"] = excluidos, ["contexto"] = contexto,
            ["rerank"] = rerankInfo, ["total_chunks"] = ix.Chunks.Count,
        };
    }

    static readonly Dict EsquemaRerank = new()
    {
        ["type"] = "object",
        ["properties"] = new Dict
        {
            ["puntajes"] = new Dict
            {
                ["type"] = "array",
                ["items"] = new Dict
                {
                    ["type"] = "object",
                    ["properties"] = new Dict
                    {
                        ["n"] = new Dict { ["type"] = "integer" },
                        ["relevancia"] = new Dict { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 3 },
                    },
                    ["required"] = new List<object?> { "n", "relevancia" },
                },
            },
        },
        ["required"] = new List<object?> { "puntajes" },
    };

    static async Task<Dict> Reranquear(string consulta, List<Dict> filas)
    {
        var lista = string.Join("\n\n", filas.Select((f, i) => $"[{i + 1}] {Texto.Corte(f.Str("texto"), 700)}"));
        var mensajes = new List<Dict>
        {
            new() { ["role"] = "system", ["content"] = "Sos un evaluador de relevancia. Para cada fragmento, puntuá de 0 a 3 cuánto ayuda a " +
                "responder la consulta: 3 = la responde directamente, 2 = aporta parte, 1 = mismo tema " +
                "pero no responde, 0 = no tiene relación. Evaluá todos los fragmentos." },
            new() { ["role"] = "user", ["content"] = $"Consulta: {consulta}\n\nFragmentos:\n{lista}" },
        };
        var (datos, r) = await Ollama.Instancia.ChatJson(mensajes, EsquemaRerank);
        var puntajes = new Dictionary<string, int>();
        foreach (var p in datos.Ds("puntajes"))
        {
            var n = p.I("n");
            if (n >= 1 && n <= filas.Count) puntajes[filas[n - 1].Str("id")] = p.I("relevancia");
        }
        return new Dict { ["puntajes"] = puntajes, ["ms"] = r.G("ms"), ["grabado"] = r.G("grabado") };
    }

    public static bool EsRelevante(Dict fila, List<Dict> esperados)
    {
        foreach (var e in esperados)
        {
            var seccion = e.S("seccion");
            if (fila.Str("document_id") == e.Str("document_id")
                && (seccion == null || ((List<string>)fila["secciones"]!).Contains(seccion)))
                return true;
        }
        return false;
    }

    public static int TotalRelevantes(Dict cfg, List<Dict> esperados) =>
        Indices[cfg.Str("chunking")].Chunks.Count(c => EsRelevante(c.Ficha(), esperados) && MotivoExclusion(c, cfg) == null);
}
