namespace MesaYa.Rag.Ingesta;

/// <summary>Fragmento listo para embeber e indexar.</summary>
/// <param name="Inicio">Posición del fragmento dentro de <see cref="Documento.Cuerpo"/>.</param>
/// <param name="Content">Lo que ve el modelo.</param>
/// <param name="TextoEmbed">Lo que se embebe. Solo difiere de Content en la estrategia contextual.</param>
/// <param name="Secciones">Títulos de las secciones que toca el fragmento, en orden.</param>
/// <param name="Padre">En parent–child, el chunk estructural que se entrega al modelo.</param>
public sealed record Chunk(
    Documento Doc,
    string Estrategia,
    int Posicion,
    int Inicio,
    int Largo,
    string Content,
    string TextoEmbed,
    IReadOnlyList<string> Secciones,
    Chunk? Padre = null)
{
    public int Tokens => TokensEstimados.De(Content);
    public string Seccion => Doc.Ruta(Secciones.Count > 0 ? Secciones[0] : "");
}

/// <summary>
/// Ollama no expone el tokenizador por API: los tamaños se estiman con 4 caracteres por token,
/// una aproximación razonable para español con tokenizadores modernos.
/// </summary>
public static class TokensEstimados
{
    public const int CaracteresPorToken = 4;
    public static int De(string texto) => (texto.Length + CaracteresPorToken - 1) / CaracteresPorToken;
}

/// <summary>Las estrategias del slide 06, cada una con su nombre en la columna <c>estrategia</c>.</summary>
public static class Estrategias
{
    public const string Fijo = "fijo-150";
    public const string Recursivo = "recursivo-512";
    public const string Estructural = "estructural";
    public const string Contextual = "contextual";
    public const string Hijo = "hijo";

    public static readonly string[] Todas = [Fijo, Recursivo, Estructural, Contextual, Hijo];

    /// <summary>Corta un documento con todas las estrategias. Los hijos apuntan a su chunk estructural.</summary>
    public static IEnumerable<Chunk> Cortar(Documento doc)
    {
        var fijos = Numerar(doc, Fijo, PorTamano(doc.Cuerpo, 0, doc.Cuerpo.Length, 150, 0));
        var recursivos = Numerar(doc, Recursivo, Recursivo512(doc));
        var estructurales = Numerar(doc, Estructural, PorEstructura(doc));
        var contextuales = estructurales.Select(c => c with
        {
            Estrategia = Contextual,
            TextoEmbed = $"Documento: {doc.Titulo}\nSección: {c.Seccion}\n\n{c.Content}"
        }).ToList();
        var hijos = Numerar(doc, Hijo, PorEstructura(doc, maxTokens: 64))
            .Select(h => h with { Padre = estructurales.Last(e => e.Inicio <= h.Inicio) })
            .ToList();
        return [.. fijos, .. recursivos, .. estructurales, .. contextuales, .. hijos];
    }

    /// <summary>Tramo del cuerpo [Inicio, Fin).</summary>
    readonly record struct Tramo(int Inicio, int Fin)
    {
        public int Largo => Fin - Inicio;
    }

    /// <summary>
    /// Ventana fija de tamaño constante, sin mirar la estructura. Solo retrocede hasta el último
    /// espacio para no partir palabras.
    /// </summary>
    static List<Tramo> PorTamano(string texto, int inicio, int fin, int maxTokens, int solapamientoTokens)
    {
        var max = maxTokens * TokensEstimados.CaracteresPorToken;
        var solapamiento = solapamientoTokens * TokensEstimados.CaracteresPorToken;
        var tramos = new List<Tramo>();
        var pos = inicio;
        while (pos < fin)
        {
            var corte = Math.Min(fin, pos + max);
            if (corte < fin)
            {
                var espacio = texto.LastIndexOfAny([' ', '\n'], corte - 1, corte - pos);
                if (espacio > pos + max / 2) corte = espacio + 1;
            }
            tramos.Add(new Tramo(pos, corte));
            if (corte >= fin) break;
            var siguiente = corte - solapamiento;
            if (solapamiento > 0)
            {
                var espacio = texto.IndexOfAny([' ', '\n'], siguiente);
                if (espacio >= 0 && espacio < corte) siguiente = espacio + 1;
            }
            pos = Math.Max(siguiente, pos + 1);
        }
        return tramos;
    }

    static readonly string[] Separadores = ["\n## ", "\n### ", "\n\n", "\n", ". ", " "];

    /// <summary>Recursivo por separadores: párrafos, líneas, oraciones y palabras, con 15 % de solapamiento.</summary>
    static List<Tramo> Recursivo512(Documento doc) =>
        Unir(Partir(doc.Cuerpo, new Tramo(0, doc.Cuerpo.Length), 0, 512), 512, 512 * 15 / 100);

    /// <summary>Divide un tramo con el primer separador que sirva y baja de nivel solo en las partes que siguen grandes.</summary>
    static List<Tramo> Partir(string texto, Tramo tramo, int nivel, int maxTokens)
    {
        var max = maxTokens * TokensEstimados.CaracteresPorToken;
        if (tramo.Largo <= max) return [tramo];
        if (nivel >= Separadores.Length) return PorTamano(texto, tramo.Inicio, tramo.Fin, maxTokens, 0);

        var sep = Separadores[nivel];
        // Los encabezados abren la parte siguiente; el resto de los separadores cierra la parte actual.
        var antes = sep.StartsWith("\n#");
        var partes = new List<Tramo>();
        var inicio = tramo.Inicio;
        var i = texto.IndexOf(sep, tramo.Inicio, tramo.Largo, StringComparison.Ordinal);
        while (i >= 0)
        {
            var corte = antes ? i + 1 : i + sep.Length;
            if (corte > inicio && corte < tramo.Fin) { partes.Add(new Tramo(inicio, corte)); inicio = corte; }
            var desde = i + sep.Length;
            i = desde < tramo.Fin ? texto.IndexOf(sep, desde, tramo.Fin - desde, StringComparison.Ordinal) : -1;
        }
        partes.Add(new Tramo(inicio, tramo.Fin));

        return partes.Count == 1
            ? Partir(texto, tramo, nivel + 1, maxTokens)
            : partes.SelectMany(p => Partir(texto, p, nivel + 1, maxTokens)).ToList();
    }

    /// <summary>Junta piezas contiguas hasta el tamaño máximo; cada chunk nuevo repite las últimas piezas del anterior.</summary>
    static List<Tramo> Unir(List<Tramo> piezas, int maxTokens, int solapamientoTokens)
    {
        var max = maxTokens * TokensEstimados.CaracteresPorToken;
        var solapamiento = solapamientoTokens * TokensEstimados.CaracteresPorToken;
        var resultado = new List<Tramo>();
        var actual = new List<Tramo>();

        foreach (var pieza in piezas)
        {
            if (actual.Count > 0 && pieza.Fin - actual[0].Inicio > max)
            {
                resultado.Add(new Tramo(actual[0].Inicio, actual[^1].Fin));
                // Solapamiento: conservar las piezas finales que entran en el presupuesto.
                while (actual.Count > 0 && (actual[^1].Fin - actual[0].Inicio > solapamiento
                                            || pieza.Fin - actual[0].Inicio > max))
                    actual.RemoveAt(0);
            }
            actual.Add(pieza);
        }
        if (actual.Count > 0) resultado.Add(new Tramo(actual[0].Inicio, actual[^1].Fin));
        return resultado;
    }

    /// <summary>
    /// Estructural: un chunk por sección, con su encabezado. El encabezado importa: en el manual de
    /// la tablet el código «E-4012» solo aparece en el título de la sección.
    /// Si una sección supera el máximo se parte con el recursivo dentro de la sección.
    /// </summary>
    static List<Tramo> PorEstructura(Documento doc, int maxTokens = 512) =>
        doc.Secciones
            .SelectMany(s => Unir(Partir(doc.Cuerpo, new Tramo(s.InicioBloque, s.Fin), 2, maxTokens), maxTokens, 0))
            .ToList();

    static List<Chunk> Numerar(Documento doc, string estrategia, List<Tramo> tramos) =>
        tramos
            .Select(t => (t, texto: doc.Cuerpo[t.Inicio..t.Fin].Trim()))
            .Where(x => x.texto.Length > 0)
            .Select((x, i) => new Chunk(doc, estrategia, i, x.t.Inicio, x.t.Largo, x.texto, x.texto,
                doc.Secciones.Where(s => s.InicioBloque < x.t.Fin && s.Fin > x.t.Inicio).Select(s => s.Titulo).ToList()))
            .ToList();
}
