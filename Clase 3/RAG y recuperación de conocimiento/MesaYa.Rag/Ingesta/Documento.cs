using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MesaYa.Rag.Ingesta;

/// <summary>Una sección del documento, ubicada por posiciones dentro del cuerpo.</summary>
/// <param name="Titulo">Título del encabezado («Plazos para cancelar sin cargo»). Vacío para el texto previo al primer encabezado.</param>
/// <param name="InicioBloque">Posición del encabezado, o del texto si no tiene encabezado.</param>
/// <param name="InicioTexto">Posición del texto, después de la línea del encabezado.</param>
/// <param name="Fin">Posición donde empieza la sección siguiente.</param>
public sealed record Seccion(string Titulo, int InicioBloque, int InicioTexto, int Fin);

/// <summary>Documento del corpus con su metadata (front-matter) y su cuerpo en Markdown.</summary>
public sealed record Documento(
    string DocumentId,
    string Titulo,
    string Area,
    int Version,
    DateOnly Fecha,
    bool Vigente,
    string[] Audiencia,
    string TenantId,
    string Fuente,
    string Cuerpo,
    IReadOnlyList<Seccion> Secciones)
{
    /// <summary>Ruta legible de una sección: «Título del documento › Sección».</summary>
    public string Ruta(string seccion) => seccion.Length == 0 ? Titulo : $"{Titulo} › {seccion}";
}

/// <summary>Lee los archivos Markdown del corpus y los separa en secciones.</summary>
public static partial class LectorCorpus
{
    static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    sealed class FrontMatter
    {
        public string DocumentId { get; set; } = "";
        public string Titulo { get; set; } = "";
        public string Area { get; set; } = "";
        public int Version { get; set; }
        public DateTime Fecha { get; set; }
        public string Estado { get; set; } = "";
        public List<string> Audiencia { get; set; } = [];
        public string TenantId { get; set; } = "";
    }

    public static IReadOnlyList<Documento> Leer(string directorio) =>
        Directory.EnumerateFiles(directorio, "*.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(ruta => Parsear(File.ReadAllText(ruta).ReplaceLineEndings("\n"), Path.GetRelativePath(directorio, ruta)))
            .ToList();

    public static Documento Parsear(string texto, string fuente)
    {
        var m = FrontMatterRegex().Match(texto);
        if (!m.Success) throw new FormatException($"{fuente}: falta el front-matter.");
        var fm = Yaml.Deserialize<FrontMatter>(m.Groups[1].Value);
        if (fm.Estado is not ("vigente" or "deprecated"))
            throw new FormatException($"{fuente}: estado '{fm.Estado}' inválido.");

        var cuerpo = m.Groups[2].Value.Trim('\n') + "\n";
        return new Documento(fm.DocumentId, fm.Titulo, fm.Area, fm.Version, DateOnly.FromDateTime(fm.Fecha),
            fm.Estado == "vigente", [.. fm.Audiencia], fm.TenantId, fuente, cuerpo, Seccionar(cuerpo));
    }

    /// <summary>Corta el cuerpo en secciones por encabezados ## y ###. El # del título no abre sección.</summary>
    static List<Seccion> Seccionar(string cuerpo)
    {
        var secciones = new List<Seccion>();
        string? actual = null, h2 = null;
        int inicioBloque = 0, inicioTexto = 0, pos = 0;

        void Cerrar(int fin)
        {
            if (cuerpo[inicioTexto..fin].Trim().Length > 0)
                secciones.Add(new Seccion(actual ?? "", inicioBloque, inicioTexto, fin));
        }

        foreach (var linea in cuerpo.Split('\n'))
        {
            var siguiente = pos + linea.Length + 1;
            if (linea.StartsWith("# "))
            {
                inicioBloque = inicioTexto = Math.Min(siguiente, cuerpo.Length);
            }
            else if (linea.StartsWith("## ") || linea.StartsWith("### "))
            {
                Cerrar(pos);
                var titulo = linea.TrimStart('#').Trim();
                if (linea.StartsWith("## ")) { h2 = titulo; actual = titulo; }
                else actual = h2 is null ? titulo : $"{h2} › {titulo}";
                inicioBloque = pos;
                inicioTexto = Math.Min(siguiente, cuerpo.Length);
            }
            pos = siguiente;
        }
        Cerrar(cuerpo.Length);
        return secciones;
    }

    [GeneratedRegex(@"^---\n(.*?)\n---\n(.*)$", RegexOptions.Singleline)]
    private static partial Regex FrontMatterRegex();
}
