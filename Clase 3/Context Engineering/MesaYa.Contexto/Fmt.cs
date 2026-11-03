using System.Globalization;
using MesaYa.Contexto.Contexto;

namespace MesaYa.Contexto;

/// <summary>Formatos de la UI. Los números visibles van en es-AR; las coordenadas SVG, siempre con punto.</summary>
public static class Fmt
{
    static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-AR");

    public static string N(long v) => v.ToString("#,0", Es);
    public static string N(long? v) => v is { } x ? N(x) : "—";

    /// <summary>Número para atributos SVG y CSS.</summary>
    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Seg(TimeSpan t) => t.TotalSeconds < 10 ? t.TotalSeconds.ToString("0.0 's'", Es) : t.TotalSeconds.ToString("0 's'", Es);

    public static string Clase(Tipo t) => t switch
    {
        Tipo.Instrucciones => "t-inst",
        Tipo.Conocimiento => "t-conoc",
        Tipo.Interaccion => "t-inter",
        _ => "t-tarea",
    };

    public static string Clase(Componente c) => Clase(Presupuesto.TipoDe(c));
}
