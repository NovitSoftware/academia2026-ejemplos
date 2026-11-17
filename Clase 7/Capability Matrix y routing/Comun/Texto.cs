using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace MesaYa.Comun;

public static class Texto
{
    /// <summary>Minúsculas y sin tildes ("Núñez" → "nunez").</summary>
    public static string Plano(string t)
    {
        var nfd = t.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(nfd.Length);
        foreach (var c in nfd)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Recorta como t[:n] en Python.</summary>
    public static string Corte(string t, int n) => t.Length <= n ? t : t[..n];

    /// <summary>round() de Python a entero (redondeo bancario).</summary>
    public static long Redondear(double x) => (long)Math.Round(x, MidpointRounding.ToEven);

    public static double Redondear(double x, int decimales) => Math.Round(x, decimales, MidpointRounding.ToEven);

    static readonly string[] Dias = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];

    /// <summary>Nombre del día como weekday() de Python: lunes = 0.</summary>
    public static string Dia(DateTime d) => Dias[((int)d.DayOfWeek + 6) % 7];
}

public static class Http
{
    public static IResult Error(int codigo, string detalle) =>
        Results.Json(new Dict { ["detail"] = detalle }, J.Opciones, statusCode: codigo);

    public static IResult Json(object? o) => Results.Json(o, J.Opciones);

    public static async Task<Dict> Cuerpo(HttpRequest r)
    {
        using var lector = new StreamReader(r.Body, Encoding.UTF8);
        var texto = await lector.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(texto) ? new Dict() : J.LeerObjeto(texto);
    }
}
