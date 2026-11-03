using System.Collections.Concurrent;
using Npgsql;
using Pgvector;

namespace MesaYa.Rag.Espacio;

/// <summary>Un chunk del corpus ubicado en el plano.</summary>
public sealed record PuntoChunk(
    long Id, string DocumentId, string Seccion, string Area, string TenantId, string[] Audiencia, bool Vigente,
    double X, double Y);

/// <summary>Proyección de una estrategia: los puntos del corpus y la base para proyectar consultas.</summary>
public sealed class Proyeccion(IReadOnlyList<PuntoChunk> puntos, double[] media, double[] eje1, double[] eje2, double varianza)
{
    public IReadOnlyList<PuntoChunk> Puntos { get; } = puntos;
    /// <summary>Fracción de la varianza total que explican los dos ejes: cuánto se pierde al dibujar en 2D.</summary>
    public double VarianzaExplicada { get; } = varianza;

    public (double X, double Y) Proyectar(float[] v)
    {
        double x = 0, y = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i] - media[i];
            x += c * eje1[i];
            y += c * eje2[i];
        }
        return (x, y);
    }
}

/// <summary>
/// Proyecta los embeddings de 1024 dimensiones a 2D con PCA (slide 04: «representación ilustrativa
/// en 2D de vectores de cientos de dimensiones»). Se calcula una vez por estrategia y se cachea;
/// hay que reiniciar la app después de reingestar.
/// </summary>
public sealed class Proyector(NpgsqlDataSource db)
{
    readonly ConcurrentDictionary<string, Task<Proyeccion>> cache = new();

    public async Task<Proyeccion> Obtener(string estrategia)
    {
        var tarea = cache.GetOrAdd(estrategia, Calcular);
        try { return await tarea; }
        catch
        {
            // Un error (por ejemplo, la base caída) no queda cacheado: el próximo intento recalcula.
            cache.TryRemove(new KeyValuePair<string, Task<Proyeccion>>(estrategia, tarea));
            throw;
        }
    }

    async Task<Proyeccion> Calcular(string estrategia)
    {
        var ct = CancellationToken.None;
        var filas = new List<(long Id, string Doc, string Seccion, string Area, string Tenant, string[] Audiencia, bool Vigente, float[] V)>();
        await using (var cmd = db.CreateCommand("""
            SELECT id, document_id, seccion, metadata ->> 'area', tenant_id, audiencia, vigente, embedding
              FROM chunks WHERE estrategia = $1 ORDER BY id
            """))
        {
            cmd.Parameters.AddWithValue(estrategia);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                filas.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                    r.GetString(4), r.GetFieldValue<string[]>(5), r.GetBoolean(6), r.GetFieldValue<Vector>(7).ToArray()));
        }
        if (filas.Count < 3)
            throw new InvalidOperationException($"No hay chunks de la estrategia {estrategia}. ¿Corriste la ingesta?");

        var n = filas.Count;
        var d = filas[0].V.Length;
        var media = new double[d];
        foreach (var f in filas) for (var j = 0; j < d; j++) media[j] += f.V[j] / n;
        var x = filas.Select(f => Enumerable.Range(0, d).Select(j => f.V[j] - media[j]).ToArray()).ToArray();

        // Con n puntos ≪ d dimensiones conviene trabajar con la matriz de Gram (n × n).
        var gram = new double[n, n];
        for (var a = 0; a < n; a++)
            for (var b = a; b < n; b++)
            {
                double s = 0;
                for (var j = 0; j < d; j++) s += x[a][j] * x[b][j];
                gram[a, b] = gram[b, a] = s;
            }
        double traza = 0;
        for (var a = 0; a < n; a++) traza += gram[a, a];

        var (u1, l1) = Potencia(gram, n, null);
        var (u2, l2) = Potencia(gram, n, u1);
        var eje1 = Eje(x, u1, l1, d);
        var eje2 = Eje(x, u2, l2, d);

        var proyeccion = new Proyeccion([], media, eje1, eje2, traza == 0 ? 0 : (l1 + l2) / traza);
        var puntos = filas.Select(f =>
        {
            var (px, py) = proyeccion.Proyectar(f.V);
            return new PuntoChunk(f.Id, f.Doc, f.Seccion, f.Area, f.Tenant, f.Audiencia, f.Vigente, px, py);
        }).ToList();
        return new Proyeccion(puntos, media, eje1, eje2, proyeccion.VarianzaExplicada);
    }

    /// <summary>Autovector dominante por iteración de potencia, ortogonal a <paramref name="sacar"/> si se indica.</summary>
    static (double[] U, double Lambda) Potencia(double[,] m, int n, double[]? sacar)
    {
        var u = Enumerable.Range(0, n).Select(i => 1.0 + i % 7 * 0.1).ToArray();
        double lambda = 0;
        for (var it = 0; it < 300; it++)
        {
            if (sacar is not null)
            {
                var p = Punto(u, sacar);
                for (var i = 0; i < n; i++) u[i] -= p * sacar[i];
            }
            var v = new double[n];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++) v[i] += m[i, j] * u[j];
            var norma = Math.Sqrt(Punto(v, v));
            if (norma == 0) break;
            for (var i = 0; i < n; i++) v[i] /= norma;
            lambda = norma;
            var cambio = Math.Abs(Math.Abs(Punto(u, v)) / Math.Sqrt(Punto(u, u)) - 1);
            u = v;
            if (cambio < 1e-10) break;
        }
        return (u, lambda);
    }

    /// <summary>Pasa un autovector de la matriz de Gram al espacio original: Xᵀu / √λ.</summary>
    static double[] Eje(double[][] x, double[] u, double lambda, int d)
    {
        var eje = new double[d];
        for (var i = 0; i < x.Length; i++)
            for (var j = 0; j < d; j++) eje[j] += x[i][j] * u[i];
        var norma = Math.Sqrt(Punto(eje, eje));
        if (norma > 0) for (var j = 0; j < d; j++) eje[j] /= norma;
        return eje;
    }

    static double Punto(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
