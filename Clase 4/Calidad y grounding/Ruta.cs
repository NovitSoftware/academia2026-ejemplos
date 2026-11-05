using System.Text.RegularExpressions;
using MesaYa.Comun;
using Microsoft.Data.Sqlite;

namespace MesaYa.Calidad;

/// <summary>
/// ¿RAG o no? (slides 16 a 18).
///
/// Antes de buscar, el router decide adónde va la pregunta:
/// - documentos → RAG sobre el corpus.
/// - datos      → SQL sobre la base de reservas (números exactos, agregados).
/// - en_vivo    → API de disponibilidad (cambia minuto a minuto).
/// - directo    → ficha corta que entra entera en el prompt.
/// </summary>
public static class Ruta
{
    public static readonly Dictionary<string, int> Sucursales = new() { ["Palermo"] = 60, ["Belgrano"] = 40, ["Núñez"] = 45 };  // cubiertos por turno de 30 minutos
    static readonly string[] Horarios = ["20:00", "20:30", "21:00", "21:30", "22:00", "22:30"];

    public const string Ficha =
        "MesaYa es una cadena de restaurantes con tres sucursales en Buenos Aires: Palermo, Belgrano y Núñez.\n" +
        "Teléfono central: (011) 4800-1234. Web y app: mesaya.example. Correo de ayuda: ayuda@mesaya.example.\n" +
        "Reservas desde la app, la web o por teléfono.";

    const string Esquema =
        "reservas(id TEXT, sucursal TEXT, fecha TEXT 'AAAA-MM-DD', hora TEXT 'HH:MM', personas INTEGER,\n" +
        "         estado TEXT -- 'confirmada' | 'cumplida' | 'cancelada' | 'no_show',\n" +
        "         canal TEXT -- 'app' | 'web' | 'telefono')\n" +
        "ventas_diarias(sucursal TEXT, fecha TEXT 'AAAA-MM-DD', cubiertos INTEGER, facturacion INTEGER -- en pesos)\n" +
        "Valores de sucursal: 'Palermo', 'Belgrano', 'Núñez'.";

    public static DateTime Hoy() => DateTime.Today;
    static string Iso(DateTime d) => d.ToString("yyyy-MM-dd");
    static string HoyTxt() => $"{Iso(Hoy())} ({Texto.Dia(Hoy())})";

    static readonly SqliteConnection Db = CrearBase();
    static readonly Lock Candado = new();

    static T Elegir<T>(Random rnd, IList<T> opciones) => opciones[rnd.Next(opciones.Count)];

    static T ElegirPesado<T>(Random rnd, IList<T> opciones, IList<int> pesos)
    {
        var r = rnd.NextDouble() * pesos.Sum();
        for (var i = 0; i < opciones.Count; i++)
        {
            r -= pesos[i];
            if (r < 0) return opciones[i];
        }
        return opciones[^1];
    }

    /// <summary>Datos sintéticos y deterministas: 30 días hacia atrás y 14 hacia adelante.</summary>
    static SqliteConnection CrearBase()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using (var c = db.CreateCommand())
        {
            c.CommandText = """
                CREATE TABLE reservas(id TEXT, sucursal TEXT, fecha TEXT, hora TEXT, personas INTEGER, estado TEXT, canal TEXT);
                CREATE TABLE ventas_diarias(sucursal TEXT, fecha TEXT, cubiertos INTEGER, facturacion INTEGER);
                """;
            c.ExecuteNonQuery();
        }
        using var tx = db.BeginTransaction();
        void Insertar(string sql, params object[] valores)
        {
            using var c = db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            for (var i = 0; i < valores.Length; i++) c.Parameters.AddWithValue($"@p{i}", valores[i]);
            c.ExecuteNonQuery();
        }
        var rnd = new Random(2026);
        var n = 0;
        int[] grupos = [2, 2, 2, 3, 4, 4, 5, 6, 8];
        string[] canales = ["app", "app", "web", "telefono"];
        for (var delta = -30; delta < 15; delta++)
        {
            var dia = Hoy().AddDays(delta);
            var finde = dia.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday;
            foreach (var suc in Sucursales.Keys)
            {
                var cubiertos = 0;
                var cantidad = rnd.Next(14, 23) + (finde ? 12 : 0);
                for (var j = 0; j < cantidad; j++)
                {
                    n++;
                    var personas = Elegir(rnd, grupos);
                    var estado = delta < 0
                        ? ElegirPesado(rnd, ["cumplida", "cancelada", "no_show"], [82, 12, 6])
                        : ElegirPesado(rnd, ["confirmada", "cancelada"], [92, 8]);
                    if (estado == "cumplida") cubiertos += personas;
                    Insertar("INSERT INTO reservas VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6)",
                        $"RSV-{20000 + n}", suc, Iso(dia), Elegir(rnd, Horarios), personas, estado, Elegir(rnd, canales));
                }
                if (delta < 0)
                    Insertar("INSERT INTO ventas_diarias VALUES (@p0,@p1,@p2,@p3)",
                        suc, Iso(dia), cubiertos, cubiertos * rnd.Next(27, 35) * 1000);
            }
        }
        // Hoy a las 21 Belgrano está lleno: la respuesta correcta es ofrecer otro horario.
        for (var i = 0; i < 8; i++)
            Insertar("INSERT INTO reservas VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6)",
                $"RSV-{30000 + i}", "Belgrano", Iso(Hoy()), "21:00", 5, "confirmada", "app");
        tx.Commit();
        return db;
    }

    /// <summary>La "API en vivo". Responde lo que hay en este momento.</summary>
    public static Dict Disponibilidad(string sucursal, string fecha, int personas)
    {
        if (!Sucursales.TryGetValue(sucursal, out var cap)) return new Dict { ["error"] = $"Sucursal desconocida: {sucursal}" };
        var turnos = new List<Dict>();
        lock (Candado)
        {
            foreach (var h in Horarios)
            {
                using var c = Db.CreateCommand();
                c.CommandText = "SELECT COALESCE(SUM(personas),0) FROM reservas WHERE sucursal=@s AND fecha=@f AND hora=@h AND estado='confirmada'";
                c.Parameters.AddWithValue("@s", sucursal);
                c.Parameters.AddWithValue("@f", fecha);
                c.Parameters.AddWithValue("@h", h);
                var ocupado = Convert.ToInt32(c.ExecuteScalar());
                var libre = Math.Max(0, cap - ocupado);
                turnos.Add(new Dict { ["hora"] = h, ["lugares_libres"] = libre, ["disponible"] = libre >= personas });
            }
        }
        return new Dict
        {
            ["sucursal"] = sucursal, ["fecha"] = fecha, ["personas"] = personas, ["turnos"] = turnos,
            ["consultado"] = DateTime.Now.ToString("HH:mm:ss"),
        };
    }

    static readonly Dict EsquemaRuta = new()
    {
        ["type"] = "object",
        ["properties"] = new Dict
        {
            ["ruta"] = new Dict { ["type"] = "string", ["enum"] = new List<object?> { "documentos", "datos", "en_vivo", "directo" } },
            ["motivo"] = new Dict { ["type"] = "string" },
            ["sucursal"] = new Dict { ["type"] = "string" }, ["fecha"] = new Dict { ["type"] = "string" },
            ["personas"] = new Dict { ["type"] = "integer" },
        },
        ["required"] = new List<object?> { "ruta", "motivo" },
    };

    static Dict Msj(string rol, string contenido) => new() { ["role"] = rol, ["content"] = contenido };

    public static async Task<Dict> Decidir(string pregunta)
    {
        var mensajes = new List<Dict>
        {
            Msj("system",
                "Clasificá la pregunta de un usuario de MesaYa (cadena de restaurantes) según de dónde sale la respuesta:\n" +
                "- documentos: políticas, reglas, procedimientos, menú, accesibilidad, manuales del personal. Texto que cambia poco.\n" +
                "- datos: números exactos o agregados sobre reservas o ventas pasadas (cuántas, total, promedio, ranking). Va a SQL.\n" +
                "- en_vivo: disponibilidad de mesas o lugares libres para una fecha y hora. Cambia minuto a minuto. Va a la API.\n" +
                "- directo: saludo, teléfono, web, mail de contacto o qué es MesaYa. Está en una ficha corta.\n" +
                $"Si es en_vivo, completá sucursal (Palermo, Belgrano o Núñez), fecha (AAAA-MM-DD) y personas. Hoy es {HoyTxt()}."),
            Msj("user", pregunta),
        };
        var (datos, r) = await Ollama.Instancia.ChatJson(mensajes, EsquemaRuta);
        datos.TryAdd("ruta", "documentos");
        datos["ms"] = r.G("ms");
        return datos;
    }

    public static async Task<(string sql, Dict respuesta)> GenerarSql(string pregunta)
    {
        var mensajes = new List<Dict>
        {
            Msj("system", $"Escribí una sola consulta SQLite (solo SELECT) que responda la pregunta. Esquema:\n{Esquema}\n" +
                          $"Hoy es {HoyTxt()}. Usá date('now', ...) o fechas literales. Las reservas pasadas que se concretaron tienen estado 'cumplida'."),
            Msj("user", pregunta),
        };
        var esquema = new Dict
        {
            ["type"] = "object", ["properties"] = new Dict { ["sql"] = new Dict { ["type"] = "string" } },
            ["required"] = new List<object?> { "sql" },
        };
        var (datos, r) = await Ollama.Instancia.ChatJson(mensajes, esquema);
        return ((datos.S("sql") ?? "").Trim().TrimEnd(';'), r);
    }

    public static Dict EjecutarSql(string sql)
    {
        if (!Regex.IsMatch(sql, @"^\s*(select|with)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(sql, @"\b(insert|update|delete|drop|alter|attach|pragma)\b", RegexOptions.IgnoreCase))
            return new Dict { ["error"] = "Solo se permiten consultas de lectura (SELECT)." };
        try
        {
            lock (Candado)
            {
                using var c = Db.CreateCommand();
                c.CommandText = sql;
                using var r = c.ExecuteReader();
                var columnas = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
                var filas = new List<List<object?>>();
                while (filas.Count < 30 && r.Read())
                    filas.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToList());
                return new Dict { ["columnas"] = columnas, ["filas"] = filas };
            }
        }
        catch (Exception e)
        {
            return new Dict { ["error"] = e.Message };
        }
    }
}
