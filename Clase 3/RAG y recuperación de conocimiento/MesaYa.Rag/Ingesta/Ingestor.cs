using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace MesaYa.Rag.Ingesta;

/// <summary>Cantidad de chunks y tamaño promedio por estrategia, para mostrar al terminar.</summary>
public sealed record ResumenEstrategia(string Estrategia, int Chunks, int TokensPromedio, int TokensMaximo);

public sealed record ResumenIngesta(int Documentos, IReadOnlyList<ResumenEstrategia> Estrategias, int TextosEmbebidos, TimeSpan Duracion);

/// <summary>
/// Ingesta completa (slide 02, pasos 01 y 02): lee el corpus, lo corta con todas las estrategias,
/// genera los embeddings y reemplaza el contenido de la tabla <c>chunks</c>.
/// </summary>
public sealed class Ingestor(
    NpgsqlDataSource db,
    ClienteEmbeddings embeddings,
    IOptions<OpcionesRag> opciones,
    IWebHostEnvironment entorno)
{
    readonly OpcionesRag o = opciones.Value;

    public string DirectorioCorpus => Path.GetFullPath(Path.Combine(entorno.ContentRootPath, o.Corpus));
    string DirectorioSql => Path.GetFullPath(Path.Combine(entorno.ContentRootPath, o.Sql));

    public IReadOnlyList<Documento> LeerCorpus() => LectorCorpus.Leer(DirectorioCorpus);

    public async Task<ResumenIngesta> Ingestar(Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        var reloj = Stopwatch.StartNew();

        var documentos = LeerCorpus();
        var chunks = documentos.SelectMany(Estrategias.Cortar).ToList();
        log($"{documentos.Count} documentos, {chunks.Count} chunks en {Estrategias.Todas.Length} estrategias.");

        // Varias estrategias producen el mismo texto (por ejemplo, una sección corta): se embebe una sola vez.
        var textos = chunks.Select(c => c.TextoEmbed).Distinct().ToList();
        var vectores = new Dictionary<string, Vector>();
        for (var i = 0; i < textos.Count; i += o.LoteEmbeddings)
        {
            var lote = textos.Skip(i).Take(o.LoteEmbeddings).ToList();
            var resultado = await embeddings.Embeber(lote, ct);
            for (var j = 0; j < lote.Count; j++) vectores[lote[j]] = new Vector(resultado[j]);
            log($"Embeddings con {embeddings.Modelo}: {Math.Min(i + lote.Count, textos.Count)}/{textos.Count}");
        }

        await using (var conexion = await db.OpenConnectionAsync(ct))
        await using (var tx = await conexion.BeginTransactionAsync(ct))
        {
            await Ejecutar(conexion, """
                DROP INDEX IF EXISTS chunks_embedding_hnsw;
                DROP INDEX IF EXISTS chunks_tsv_gin;
                TRUNCATE chunks RESTART IDENTITY;
                """, ct);

            // Los padres (estructurales) se insertan antes que los hijos para conocer su id.
            var ids = new Dictionary<Chunk, long>(ReferenceEqualityComparer.Instance);
            foreach (var chunk in chunks.OrderBy(c => c.Estrategia == Estrategias.Hijo))
                ids[chunk] = await Insertar(conexion, chunk, vectores[chunk.TextoEmbed],
                    chunk.Padre is null ? null : ids[chunk.Padre], ct);
            await tx.CommitAsync(ct);
        }
        log("Chunks cargados. Creando índices HNSW y GIN, IDF de términos y funciones de búsqueda…");

        // funciones.sql también corre acá para que una base ya creada reciba los cambios sin recrear el volumen.
        await using (var conexion = await db.OpenConnectionAsync(ct))
            foreach (var archivo in new[] { "indices.sql", "idf.sql", "funciones.sql" })
                await Ejecutar(conexion, await File.ReadAllTextAsync(Path.Combine(DirectorioSql, archivo), ct), ct);

        var resumen = chunks.GroupBy(c => c.Estrategia)
            .Select(g => new ResumenEstrategia(g.Key, g.Count(), (int)g.Average(c => c.Tokens), g.Max(c => c.Tokens)))
            .OrderBy(r => Array.IndexOf(Estrategias.Todas, r.Estrategia))
            .ToList();
        return new ResumenIngesta(documentos.Count, resumen, textos.Count, reloj.Elapsed);
    }

    static async Task Ejecutar(NpgsqlConnection conexion, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conexion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<long> Insertar(NpgsqlConnection conexion, Chunk c, Vector embedding, long? padre, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO chunks (tenant_id, audiencia, estrategia, parent_id, document_id, titulo, seccion, fuente,
                                posicion, version, fecha, vigente, content, texto_embed, embedding, metadata)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            RETURNING id
            """, conexion);
        var d = c.Doc;
        cmd.Parameters.AddWithValue(d.TenantId);
        cmd.Parameters.AddWithValue(d.Audiencia);
        cmd.Parameters.AddWithValue(c.Estrategia);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)padre ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(d.DocumentId);
        cmd.Parameters.AddWithValue(d.Titulo);
        cmd.Parameters.AddWithValue(c.Seccion);
        cmd.Parameters.AddWithValue(d.Fuente);
        cmd.Parameters.AddWithValue(c.Posicion);
        cmd.Parameters.AddWithValue(d.Version);
        cmd.Parameters.AddWithValue(d.Fecha);
        cmd.Parameters.AddWithValue(d.Vigente);
        cmd.Parameters.AddWithValue(c.Content);
        cmd.Parameters.AddWithValue(c.TextoEmbed);
        cmd.Parameters.AddWithValue(embedding);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(new
            {
                area = d.Area,
                secciones = c.Secciones,
                tokens = c.Tokens,
                inicio = c.Inicio,
                largo = c.Largo
            })
        });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
