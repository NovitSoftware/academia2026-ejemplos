using System.Globalization;
using MesaYa.Comun;
using Microsoft.Data.Sqlite;

namespace MesaYa.Memoria;

/// <summary>
/// Memoria de largo plazo del agente (slides 03 a 06 y 12 a 14).
///
/// Cada recuerdo se guarda con sus metadatos: tipo, origen, fuente, confianza,
/// alcance, fechas y TTL. Recuperar combina similitud, importancia y recencia.
/// Olvidar es parte del diseño: vence el TTL, se reemplaza o se borra a pedido.
/// </summary>
public static class Memoria
{
    static readonly string Datos = Environment.GetEnvironmentVariable("CACHE") ?? "datos";
    static readonly Lock Candado = new();
    public static readonly SqliteConnection Db = Abrir();

    static SqliteConnection Abrir()
    {
        Directory.CreateDirectory(Datos);
        var db = new SqliteConnection($"Data Source={Path.Combine(Datos, "memoria.db")}");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS memorias (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              usuario TEXT, texto TEXT,
              tipo TEXT,          -- semantica | episodica | procedimental
              categoria TEXT,     -- preferencia | restriccion | dato | hecho | instruccion
              origen TEXT,        -- explicita | inferida
              fuente TEXT,        -- usuario | contenido_externo
              confianza REAL, importancia REAL, alcance TEXT,
              creada TEXT, actualizada TEXT, ultimo_uso TEXT, usos INTEGER DEFAULT 0,
              ttl_dias INTEGER,   -- NULL = sin vencimiento
              estado TEXT DEFAULT 'activa',   -- activa | reemplazada | olvidada | pendiente
              motivo_estado TEXT, reemplaza_id INTEGER,
              embedding TEXT
            );
            """;
        c.ExecuteNonQuery();
        return db;
    }

    // Reloj simulado: "Avanzar 45 días" sirve para mostrar TTL y decaimiento en clase.
    public static int RelojDias;
    const double SemividaDias = 60;

    public static DateTime Ahora()
    {
        var n = DateTime.Now;
        return new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second).AddDays(RelojDias);
    }

    public static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    static DateTime? Fecha(object? s) => s is string t && t.Length > 0 ? DateTime.Parse(t, CultureInfo.InvariantCulture) : null;
    static int Dias(TimeSpan t) => (int)Math.Floor(t.TotalDays);  // como timedelta.days

    // ---------- acceso a la base ----------
    static List<Dict> Consultar(string sql, params object?[] valores)
    {
        lock (Candado)
        {
            using var c = Comando(sql, valores);
            using var r = c.ExecuteReader();
            var filas = new List<Dict>();
            while (r.Read())
            {
                var d = new Dict();
                for (var i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                filas.Add(d);
            }
            return filas;
        }
    }

    static long Ejecutar(string sql, params object?[] valores)
    {
        lock (Candado)
        {
            using var c = Comando(sql, valores);
            c.ExecuteNonQuery();
            using var id = Db.CreateCommand();
            id.CommandText = "SELECT last_insert_rowid()";
            return (long)id.ExecuteScalar()!;
        }
    }

    static SqliteCommand Comando(string sql, object?[] valores)
    {
        var c = Db.CreateCommand();
        // Parámetros posicionales como en sqlite3 de Python: cada ? pasa a ser @p0, @p1...
        var partes = sql.Split('?');
        c.CommandText = string.Concat(partes.Select((p, i) => i < partes.Length - 1 ? $"{p}@p{i}" : p));
        for (var i = 0; i < valores.Length; i++) c.Parameters.AddWithValue($"@p{i}", valores[i] ?? DBNull.Value);
        return c;
    }

    public static Dict Ficha(Dict r)
    {
        var d = new Dict(r);
        d.Remove("embedding");
        var creada = Fecha(r.G("creada"));
        if (r.B("ttl_dias") && creada is DateTime c0)
        {
            var vence = c0.AddDays(r.I("ttl_dias"));
            d["vence"] = vence.ToString("yyyy-MM-dd");
            d["dias_para_vencer"] = Dias(vence - Ahora());
        }
        var uso = Fecha(r.G("ultimo_uso")) ?? creada;
        var sinUso = uso is DateTime u ? Math.Max(0, Dias(Ahora() - u)) : 0;
        d["dias_sin_uso"] = sinUso;
        d["recencia"] = Texto.Redondear(Math.Exp(-sinUso * Math.Log(2) / SemividaDias), 2);
        return d;
    }

    public static Dict Listar(string usuario)
    {
        Vencer(usuario);
        var todas = Consultar("SELECT * FROM memorias WHERE usuario=? ORDER BY id", usuario).Select(Ficha).ToList();
        return new Dict
        {
            ["activas"] = todas.Where(m => m.Str("estado") == "activa").ToList(),
            ["pendientes"] = todas.Where(m => m.Str("estado") == "pendiente").ToList(),
            ["historial"] = todas.Where(m => m.Str("estado") is "reemplazada" or "olvidada").ToList(),
        };
    }

    /// <summary>Olvido por TTL: lo que venció deja de existir para el agente.</summary>
    public static List<Dict> Vencer(string usuario)
    {
        var vencidas = new List<Dict>();
        foreach (var r in Consultar("SELECT * FROM memorias WHERE usuario=? AND estado='activa' AND ttl_dias IS NOT NULL", usuario))
        {
            if (Fecha(r.G("creada"))!.Value.AddDays(r.I("ttl_dias")) <= Ahora())
            {
                Ejecutar("UPDATE memorias SET estado='olvidada', motivo_estado=? WHERE id=?",
                    $"Venció el TTL de {r.G("ttl_dias")} días", r.G("id"));
                vencidas.Add(Ficha(r));
            }
        }
        return vencidas;
    }

    public static async Task<float[]> Vector(string texto) =>
        Normalizar((await Ollama.Instancia.Embeber([texto]))[0]);

    static float[] Normalizar(float[] v)
    {
        var norma = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        return v.Select(x => x / norma).ToArray();
    }

    public static float[] Embedding(Dict r) =>
        ((List<object?>)J.Leer(r.Str("embedding"))!).Select(x => (float)J.Num(x)!.Value).ToArray();

    public static double Similitud(float[] a, float[] b)
    {
        float s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Puntaje = 0,6 similitud + 0,25 importancia + 0,15 recencia. Entran las k mejores con similitud ≥ 0,35.</summary>
    public static async Task<List<Dict>> Recuperar(string usuario, string consulta, int k)
    {
        Vencer(usuario);
        var filas = Consultar("SELECT * FROM memorias WHERE usuario=? AND estado='activa'", usuario);
        if (filas.Count == 0) return [];
        var q = await Vector(consulta);
        var salida = new List<Dict>();
        foreach (var r in filas)
        {
            var f = Ficha(r);
            var sim = Similitud(q, Embedding(r));
            f["sim"] = Texto.Redondear(sim, 3);
            var importancia = r.Dn("importancia") is double x && x != 0 ? x : 0.5;
            f["puntaje"] = Texto.Redondear(0.6 * sim + 0.25 * importancia + 0.15 * f.D("recencia"), 3);
            salida.Add(f);
        }
        salida = salida.OrderByDescending(f => f.D("puntaje")).ToList();
        for (var i = 0; i < salida.Count; i++) salida[i]["entra"] = i < k && salida[i].D("sim") >= 0.35;
        var usadas = salida.Where(f => f.B("entra")).Select(f => f.G("id")).ToList();
        if (usadas.Count > 0)
            Ejecutar($"UPDATE memorias SET ultimo_uso=?, usos=usos+1 WHERE id IN ({string.Join(",", usadas.Select(_ => "?"))})",
                [Iso(Ahora()), .. usadas]);
        return salida;
    }

    public static async Task<long> Guardar(string usuario, Dict c, string estado = "activa")
    {
        var emb = await Vector(c.Str("texto"));
        var t = Iso(Ahora());
        return Ejecutar(
            """
            INSERT INTO memorias (usuario, texto, tipo, categoria, origen, fuente, confianza, importancia, alcance,
               creada, actualizada, ultimo_uso, usos, ttl_dias, estado, reemplaza_id, embedding)
               VALUES (?,?,?,?,?,?,?,?,?,?,?,?,0,?,?,?,?)
            """,
            usuario, c.G("texto"), c.G("tipo"), c.G("categoria"), c.G("origen"), c.G("fuente"), c.G("confianza"), c.G("importancia"),
            c.S("alcance") ?? "global", t, t, null, c.G("ttl_dias"), estado, c.G("actualiza_id"), J.Serializar(emb));
    }

    public static void Activar(long mid) => Ejecutar("UPDATE memorias SET estado='activa' WHERE id=?", mid);

    public static void Reemplazar(long viejo, long nuevo) =>
        Ejecutar("UPDATE memorias SET estado='reemplazada', motivo_estado=? WHERE id=?", $"Actualizada por #{nuevo}", viejo);

    public static void Reforzar(long mid, double confianza) =>
        Ejecutar("UPDATE memorias SET usos=usos+1, confianza=MAX(confianza, ?), actualizada=? WHERE id=?", confianza, Iso(Ahora()), mid);

    public static void Olvidar(long mid, string motivo) =>
        Ejecutar("UPDATE memorias SET estado='olvidada', motivo_estado=? WHERE id=?", motivo, mid);

    public static void BorrarUsuario(string usuario) => Ejecutar("DELETE FROM memorias WHERE usuario=?", usuario);

    public static Dict? Obtener(long mid) => Consultar("SELECT * FROM memorias WHERE id=?", mid).Select(Ficha).FirstOrDefault();

    public static Dict? FilaCruda(long mid) => Consultar("SELECT * FROM memorias WHERE id=?", mid).FirstOrDefault();

    public static List<Dict> ActivasParaExtractor(string usuario) =>
        Consultar("SELECT id, texto FROM memorias WHERE usuario=? AND estado='activa' ORDER BY id", usuario);
}
