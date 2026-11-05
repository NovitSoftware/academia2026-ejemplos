using System.Text;
using System.Text.RegularExpressions;
using MesaYa.Comun;

namespace MesaYa.Calidad;

/// <summary>
/// Lectura del corpus de MesaYa y las dos estrategias de chunking.
///
/// - seccion: un chunk por sección (##), con título del documento y de la sección
///   al principio. Es el "buen chunk" de la slide 08: se entiende solo.
/// - fijo: ventanas de ~150 tokens (600 caracteres) sin encabezados. Es el
///   "chunk crudo": corta reglas por la mitad y pierde a qué documento pertenece.
/// </summary>
public sealed record Documento(
    string Id, string Titulo, string Area, int Version, string Fecha, string Estado,
    List<string> Audiencia, string Tenant, string Ruta, List<(string Titulo, string Texto)> Secciones)
{
    /// <summary>Jerarquía de fuentes de la slide 12.</summary>
    public string Autoridad =>
        Estado != "vigente" ? "historica" : Area is "marketing" or "administracion" ? "comunicacion" : "oficial";
}

public sealed record Chunk(string Id, Documento Doc, string Texto, List<string> Secciones)
{
    public Dict Ficha() => new()
    {
        ["id"] = Id, ["document_id"] = Doc.Id, ["titulo"] = Doc.Titulo,
        ["seccion"] = string.Join(" · ", Secciones), ["secciones"] = Secciones,
        ["estado"] = Doc.Estado, ["version"] = Doc.Version, ["fecha"] = Doc.Fecha,
        ["autoridad"] = Doc.Autoridad, ["audiencia"] = Doc.Audiencia, ["tenant"] = Doc.Tenant,
        ["texto"] = Texto, ["tokens"] = Math.Max(1, Texto.Length / 4),
    };
}

public static class Corpus
{
    public static readonly string Carpeta = Environment.GetEnvironmentVariable("CORPUS")
        ?? Path.Combine(AppContext.BaseDirectory, "corpus");

    public static readonly List<Documento> Documentos = LeerCorpus();

    public static readonly Dictionary<string, List<Chunk>> Chunks = new()
    {
        ["seccion"] = ChunksPorSeccion(Documentos),
        ["fijo"] = ChunksFijos(Documentos),
    };

    static (Dictionary<string, object> meta, string cuerpo) FrontMatter(string texto)
    {
        var m = Regex.Match(texto, @"^---\n(.*?)\n---\n(.*)", RegexOptions.Singleline);
        var meta = new Dictionary<string, object>();
        foreach (var linea in m.Groups[1].Value.Split('\n'))
        {
            var i = linea.IndexOf(':');
            var (k, v) = i < 0 ? (linea, "") : (linea[..i], linea[(i + 1)..].Trim());
            meta[k.Trim()] = v.StartsWith('[')
                ? v.Trim('[', ']').Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList()
                : v;
        }
        return (meta, m.Groups[2].Value);
    }

    static List<Documento> LeerCorpus()
    {
        var docs = new List<Documento>();
        var archivos = Directory.GetFiles(Carpeta, "*.md", SearchOption.AllDirectories)
            .Select(f => (f, ruta: Path.GetRelativePath(Carpeta, f).Replace('\\', '/')))
            .OrderBy(x => x.ruta, StringComparer.Ordinal);
        foreach (var (f, ruta) in archivos)
        {
            // En Windows git puede dejar los archivos con CRLF: se normaliza como hace Python al leer.
            var (meta, cuerpo) = FrontMatter(File.ReadAllText(f, Encoding.UTF8).Replace("\r\n", "\n"));
            var secciones = new List<(string, string)>();
            var actual = "Introducción";
            var lineas = new List<string>();
            foreach (var linea in cuerpo.Split('\n'))
            {
                if (linea.StartsWith("# ")) continue;
                if (linea.StartsWith("## "))
                {
                    if (string.Concat(lineas).Trim().Length > 0) secciones.Add((actual, string.Join("\n", lineas).Trim()));
                    actual = linea[3..].Trim();
                    lineas = [];
                }
                else lineas.Add(linea);
            }
            if (string.Concat(lineas).Trim().Length > 0) secciones.Add((actual, string.Join("\n", lineas).Trim()));
            string Meta(string k, string def = "") => meta.TryGetValue(k, out var v) && v is string s ? s : def;
            docs.Add(new Documento(
                Id: Meta("document_id"), Titulo: Meta("titulo"), Area: Meta("area"),
                Version: int.Parse(Meta("version", "1")), Fecha: Meta("fecha"),
                Estado: Meta("estado", "vigente"),
                Audiencia: meta.TryGetValue("audiencia", out var a) && a is List<string> l ? l : [],
                Tenant: Meta("tenant_id", "mesaya-ar"), Ruta: ruta, Secciones: secciones));
        }
        return docs;
    }

    static List<Chunk> ChunksPorSeccion(List<Documento> docs)
    {
        var salida = new List<Chunk>();
        foreach (var d in docs)
            for (var i = 0; i < d.Secciones.Count; i++)
            {
                var (titulo, texto) = d.Secciones[i];
                salida.Add(new Chunk($"{d.Id}#{i + 1}", d, $"{d.Titulo} › {titulo}\n{texto}", [titulo]));
            }
        return salida;
    }

    static List<Chunk> ChunksFijos(List<Documento> docs, int largo = 600)
    {
        var salida = new List<Chunk>();
        foreach (var d in docs)
        {
            // Texto corrido, sin títulos: así llega un PDF exportado "tal cual".
            var corridoSb = new StringBuilder();
            var mapa = new List<(int ini, int fin, string sec)>();
            foreach (var (titulo, texto) in d.Secciones)
            {
                var plano = Regex.Replace(texto, @"\s+", " ").Trim() + " ";
                mapa.Add((corridoSb.Length, corridoSb.Length + plano.Length, titulo));
                corridoSb.Append(plano);
            }
            var corrido = corridoSb.ToString();
            int inicio = 0, n = 0;
            while (inicio < corrido.Length)
            {
                var fin = Math.Min(corrido.Length, inicio + largo);
                if (fin < corrido.Length)
                {
                    var corte = corrido.LastIndexOf(' ', fin - 1, fin - inicio);  // rfind(" ", inicio, fin)
                    fin = corte > inicio ? corte : fin;
                }
                var trozo = corrido[inicio..fin].Trim();
                if (trozo.Length > 0)
                {
                    n++;
                    var secs = mapa.Where(x => x.ini < fin && x.fin > inicio).Select(x => x.sec).ToList();
                    salida.Add(new Chunk($"{d.Id}~{n}", d, trozo, secs));
                }
                inicio = fin + 1;
            }
        }
        return salida;
    }
}
