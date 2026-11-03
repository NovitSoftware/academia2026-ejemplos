using System.Diagnostics;
using System.Text.Json;
using MesaYa.Rag.Ingesta;
using Npgsql;
using Pgvector;

namespace MesaYa.Rag.Busqueda;

public enum ModoBusqueda { Semantica, PalabrasClave, Hibrida }

/// <summary>
/// Dónde se aplican tenant y audiencia (slide 07).
/// Pre: en el WHERE, antes del top-k. Post: después del top-k, en el host. Apagado: no se aplican.
/// </summary>
public enum FiltroPermisos { Pre, Post, Apagado }

/// <summary>Una búsqueda con todos los controles de la demo.</summary>
/// <param name="K">Resultados finales.</param>
/// <param name="Candidatos">Resultados de cada lista (semántica y palabras clave) antes de fusionar.</param>
public sealed record ConsultaBusqueda(
    string Texto,
    string Tenant,
    string Perfil,
    string Estrategia = Estrategias.Estructural,
    ModoBusqueda Modo = ModoBusqueda.Hibrida,
    FiltroPermisos Permisos = FiltroPermisos.Pre,
    bool SoloVigentes = true,
    int K = 20,
    int Candidatos = 50,
    int EfSearch = 40,
    int RrfK = 60);

/// <summary>Un chunk tal como quedó en la tabla.</summary>
public sealed record ChunkRecuperado(
    long Id,
    string DocumentId,
    string Titulo,
    string Seccion,
    IReadOnlyList<string> Secciones,
    string Fuente,
    string Estrategia,
    string TenantId,
    string[] Audiencia,
    int Version,
    DateOnly Fecha,
    bool Vigente,
    string Content,
    string TextoEmbed,
    long? ParentId,
    string? ContentPadre,
    string? SeccionPadre,
    int Posicion = 0)
{
    /// <summary>Lo que se entrega al modelo: en parent–child, la sección completa.</summary>
    public string ParaElModelo => ContentPadre ?? Content;
    public string SeccionParaElModelo => SeccionPadre ?? Seccion;
}

/// <summary>Un resultado con su posición en cada lista, para la pestaña Ranking.</summary>
public sealed record Resultado(
    int Posicion,
    long? PosSemantica,
    double? Distancia,
    long? PosPalabrasClave,
    double? PuntajePalabrasClave,
    double Rrf,
    ChunkRecuperado Chunk);

/// <param name="Descartados">Con post-filtering, los resultados del top-k que el host sacó por permisos.</param>
/// <param name="Agrupados">En parent–child, los hijos que se omitieron porque su padre ya estaba más arriba.</param>
public sealed record ResultadoBusqueda(
    ConsultaBusqueda Consulta,
    IReadOnlyList<Resultado> Resultados,
    IReadOnlyList<Resultado> Descartados,
    IReadOnlyList<Resultado> Agrupados,
    string TsQuery,
    TimeSpan TiempoEmbedding,
    TimeSpan TiempoSql,
    string Sql,
    float[]? VectorConsulta = null);

/// <summary>
/// Búsqueda semántica, por palabras clave e híbrida con RRF sobre pgvector (slides 09 a 11),
/// con filtros de permisos y vigencia que aplica el sistema, nunca el modelo.
/// </summary>
public sealed class Buscador(NpgsqlDataSource db, ClienteEmbeddings embeddings)
{
    public async Task<ResultadoBusqueda> Buscar(ConsultaBusqueda c, CancellationToken ct = default)
    {
        var reloj = Stopwatch.StartNew();
        var usaSemantica = c.Modo != ModoBusqueda.PalabrasClave;
        var usaPalabrasClave = c.Modo != ModoBusqueda.Semantica;
        var vectorConsulta = usaSemantica ? await embeddings.Embeber(c.Texto, ct) : null;
        Vector? vector = vectorConsulta is null ? null : new Vector(vectorConsulta);
        var tiempoEmbedding = reloj.Elapsed;

        var sql = ArmarSql(c, usaSemantica, usaPalabrasClave);
        reloj.Restart();
        var filas = new List<Resultado>();
        string tsquery;
        await using (var conexion = await db.OpenConnectionAsync(ct))
        await using (var tx = await conexion.BeginTransactionAsync(ct))
        {
            // SET LOCAL vale solo dentro de esta transacción.
            await using (var set = new NpgsqlCommand(
                $"SET LOCAL hnsw.ef_search = {Math.Clamp(c.EfSearch, 1, 1000)}; SET LOCAL hnsw.iterative_scan = relaxed_order;", conexion, tx))
                await set.ExecuteNonQueryAsync(ct);

            await using (var q = new NpgsqlCommand("SELECT consulta_or($1)::text", conexion, tx))
            {
                q.Parameters.AddWithValue(c.Texto);
                tsquery = (string)(await q.ExecuteScalarAsync(ct))!;
            }

            await using var cmd = new NpgsqlCommand(sql, conexion, tx);
            cmd.Parameters.AddWithValue("embedding", (object?)vector ?? DBNull.Value);
            cmd.Parameters.AddWithValue("texto", c.Texto);
            cmd.Parameters.AddWithValue("estrategia", c.Estrategia);
            cmd.Parameters.AddWithValue("tenant", c.Tenant);
            cmd.Parameters.AddWithValue("perfil", c.Perfil);
            cmd.Parameters.AddWithValue("candidatos", c.Candidatos);
            cmd.Parameters.AddWithValue("rrf_k", c.RrfK);
            // Con parent–child se piden más filas porque después se agrupan los hijos del mismo padre.
            cmd.Parameters.AddWithValue("k", c.Estrategia == Estrategias.Hijo ? c.Candidatos : c.K);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                filas.Add(Leer(r, filas.Count + 1));
        }
        var tiempoSql = reloj.Elapsed;

        var agrupados = new List<Resultado>();
        if (c.Estrategia == Estrategias.Hijo)
        {
            var padres = new HashSet<long>();
            var unicos = new List<Resultado>();
            foreach (var f in filas)
                if (padres.Add(f.Chunk.ParentId ?? f.Chunk.Id)) unicos.Add(f);
                else agrupados.Add(f);
            filas = unicos.Take(c.K).ToList();
        }

        var descartados = new List<Resultado>();
        if (c.Permisos == FiltroPermisos.Post)
        {
            descartados = filas.Where(f => !Permitido(f.Chunk, c)).ToList();
            filas = filas.Except(descartados).ToList();
        }

        // Las posiciones finales se numeran después de agrupar y filtrar.
        var resultados = filas.Select((f, i) => f with { Posicion = i + 1 }).ToList();
        return new ResultadoBusqueda(c, resultados, descartados, agrupados, tsquery, tiempoEmbedding, tiempoSql, sql, vectorConsulta);
    }

    static bool Permitido(ChunkRecuperado chunk, ConsultaBusqueda c) =>
        chunk.TenantId == c.Tenant && chunk.Audiencia.Contains(c.Perfil);

    /// <summary>
    /// La consulta del slide 11, generalizada: cada lista se arma solo si el modo la usa y los
    /// filtros entran al WHERE según el control de permisos.
    /// </summary>
    static string ArmarSql(ConsultaBusqueda c, bool semantica, bool palabrasClave)
    {
        var filtros = new List<string> { "c.estrategia = @estrategia" };
        if (c.Permisos == FiltroPermisos.Pre)
        {
            filtros.Add("c.tenant_id = @tenant");
            filtros.Add("@perfil = ANY (c.audiencia)");
        }
        if (c.SoloVigentes) filtros.Add("c.vigente");
        var where = string.Join("\n      AND ", filtros);

        var sem = semantica
            ? $"""
              SELECT id, ROW_NUMBER() OVER (ORDER BY embedding <=> @embedding) AS r,
                     embedding <=> @embedding AS distancia
                FROM chunks c
               WHERE {where}
               ORDER BY embedding <=> @embedding
               LIMIT @candidatos
              """
            : "SELECT NULL::bigint AS id, NULL::bigint AS r, NULL::float8 AS distancia WHERE false";

        // BM25 simplificado: suma del IDF de los términos de la consulta que tiene el chunk (db/idf.sql).
        var kw = palabrasClave
            ? $"""
              SELECT id, ROW_NUMBER() OVER (ORDER BY puntaje DESC, id) AS r, puntaje
                FROM (
                  SELECT c.id, sum(i.idf) AS puntaje
                    FROM chunks c
                    CROSS JOIN consulta_or(@texto) AS q
                    JOIN (SELECT DISTINCT unnest(tsvector_to_array(to_tsvector('spanish', @texto))) AS lexema) t
                      ON t.lexema = ANY (tsvector_to_array(c.tsv))
                    JOIN lexemas_idf i ON i.estrategia = c.estrategia AND i.lexema = t.lexema
                   WHERE {where.Replace("\n      AND ", "\n                     AND ")}
                     AND c.tsv @@ q
                   GROUP BY c.id
                ) puntajes
               ORDER BY puntaje DESC, id
               LIMIT @candidatos
              """
            : "SELECT NULL::bigint AS id, NULL::bigint AS r, NULL::float8 AS puntaje WHERE false";

        return $"""
            WITH sem AS (
              {Indentar(sem)}
            ), kw AS (
              {Indentar(kw)}
            ), fusion AS (
              SELECT COALESCE(sem.id, kw.id) AS id,
                     sem.r AS pos_sem, sem.distancia,
                     kw.r  AS pos_kw,  kw.puntaje,
                     COALESCE(1.0 / (@rrf_k + sem.r), 0)
                   + COALESCE(1.0 / (@rrf_k + kw.r), 0) AS rrf
                FROM sem FULL JOIN kw ON sem.id = kw.id
            )
            SELECT f.pos_sem, f.distancia, f.pos_kw, f.puntaje, f.rrf::float8,
                   c.id, c.document_id, c.titulo, c.seccion, c.metadata -> 'secciones', c.fuente, c.estrategia,
                   c.tenant_id, c.audiencia, c.version, c.fecha, c.vigente, c.content, c.texto_embed,
                   c.parent_id, p.content, p.seccion, c.posicion
              FROM fusion f
              JOIN chunks c ON c.id = f.id
              LEFT JOIN chunks p ON p.id = c.parent_id
             ORDER BY f.rrf DESC, LEAST(f.pos_sem, f.pos_kw), c.id
             LIMIT @k
            """;
    }

    static string Indentar(string sql) => sql.ReplaceLineEndings("\n  ");

    static Resultado Leer(NpgsqlDataReader r, int posicion)
    {
        long? Long(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        double? Double(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        string? Texto(int i) => r.IsDBNull(i) ? null : r.GetString(i);

        var chunk = new ChunkRecuperado(
            Id: r.GetInt64(5),
            DocumentId: r.GetString(6),
            Titulo: r.GetString(7),
            Seccion: r.GetString(8),
            Secciones: JsonSerializer.Deserialize<string[]>(r.GetString(9)) ?? [],
            Fuente: r.GetString(10),
            Estrategia: r.GetString(11),
            TenantId: r.GetString(12),
            Audiencia: r.GetFieldValue<string[]>(13),
            Version: r.GetInt32(14),
            Fecha: r.GetFieldValue<DateOnly>(15),
            Vigente: r.GetBoolean(16),
            Content: r.GetString(17),
            TextoEmbed: r.GetString(18),
            ParentId: Long(19),
            ContentPadre: Texto(20),
            SeccionPadre: Texto(21),
            Posicion: r.GetInt32(22));
        return new Resultado(posicion, Long(0), Double(1), Long(2), Double(3), r.GetDouble(4), chunk);
    }
}
