using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MesaYa.Comun;

/// <summary>
/// Objeto JSON mutable. Las demos arman respuestas con forma libre (como los dict de Python)
/// y la interfaz web depende de esas claves exactas, así que se usa un diccionario en lugar de clases.
/// </summary>
public sealed class Dict : Dictionary<string, object?>
{
    public Dict() { }
    public Dict(IDictionary<string, object?> otro) : base(otro) { }

    /// <summary>Copia superficial con las claves de <paramref name="extra"/> encima ({**a, **b}).</summary>
    public Dict Con(Dict? extra)
    {
        var d = new Dict(this);
        if (extra != null)
            foreach (var (k, v) in extra) d[k] = v;
        return d;
    }
}

public static class DictExt
{
    public static object? G(this Dict d, string k) => d.TryGetValue(k, out var v) ? v : null;
    public static string? S(this Dict d, string k) => d.G(k) as string;
    public static string Str(this Dict d, string k, string def = "") => d.G(k) is string s ? s : def;
    public static double D(this Dict d, string k, double def = 0) => J.Num(d.G(k)) ?? def;
    public static double? Dn(this Dict d, string k) => J.Num(d.G(k));
    public static int I(this Dict d, string k, int def = 0) => J.Num(d.G(k)) is double x ? (int)x : def;
    public static int? In(this Dict d, string k) => J.Num(d.G(k)) is double x ? (int)x : null;
    public static bool B(this Dict d, string k) => J.Verdadero(d.G(k));
    public static Dict? O(this Dict d, string k) => d.G(k) as Dict;

    /// <summary>Lista de objetos de la clave. Si la lista ya es List&lt;Dict&gt; devuelve la misma (mutable).</summary>
    public static List<Dict> Ds(this Dict d, string k) => d.G(k) switch
    {
        List<Dict> l => l,
        IEnumerable e and not string => e.OfType<Dict>().ToList(),
        _ => [],
    };
}

public static class J
{
    /// <summary>Opciones para responder al navegador: sin escapar tildes.</summary>
    public static readonly JsonSerializerOptions Opciones = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serializar(object? o) => JsonSerializer.Serialize(o, Opciones);

    public static double? Num(object? v) => v switch
    {
        null => null,
        bool => null,
        int i => i,
        long l => l,
        double x => x,
        float f => f,
        decimal m => (double)m,
        _ => null,
    };

    /// <summary>Verdad "a la Python": null, false, 0, "" y colecciones vacías son falsos.</summary>
    public static bool Verdadero(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        ICollection c => c.Count > 0,
        _ when Num(v) is double x => x != 0,
        _ => true,
    };

    /// <summary>Igualdad de valores JSON: 2 == 2.0 y "a" == "a".</summary>
    public static bool Igual(object? a, object? b)
    {
        if (Num(a) is double x && Num(b) is double y) return x == y;
        return Equals(a, b);
    }

    // ---------- lectura ----------
    public static object? Leer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Nativo(doc.RootElement);
    }

    public static Dict LeerObjeto(string json) => Leer(json) as Dict ?? new Dict();

    /// <summary>Convierte un JsonElement en Dict / List&lt;object?&gt; / string / long / double / bool / null.</summary>
    public static object? Nativo(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ObjetoNativo(e),
        JsonValueKind.Array => e.EnumerateArray().Select(Nativo).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    static Dict ObjetoNativo(JsonElement e)
    {
        var d = new Dict();
        foreach (var p in e.EnumerateObject()) d[p.Name] = Nativo(p.Value);
        return d;
    }

    // ---------- JSON con el formato de Python ----------
    /// <summary>
    /// Igual que json.dumps(o, ensure_ascii=False) de Python: separadores ", " y ": ", floats como 0.0.
    /// Se usa en los prompts (para que el texto sea el mismo que en la versión Python) y para la clave
    /// de las grabaciones (sort_keys=True), así las grabaciones de una versión sirven en la otra.
    /// </summary>
    public static string Py(object? o, bool ordenar = false)
    {
        var sb = new StringBuilder();
        EscribirPy(sb, o, ordenar);
        return sb.ToString();
    }

    static void EscribirPy(StringBuilder sb, object? o, bool ordenar)
    {
        switch (o)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: CadenaPy(sb, s); break;
            case int or long: sb.Append(Convert.ToInt64(o).ToString(CultureInfo.InvariantCulture)); break;
            case double or float: sb.Append(FloatPy(Convert.ToDouble(o))); break;
            case IDictionary<string, object?> d:
            {
                sb.Append('{');
                var claves = ordenar ? d.Keys.OrderBy(k => k, StringComparer.Ordinal) : d.Keys.AsEnumerable();
                var primero = true;
                foreach (var k in claves)
                {
                    if (!primero) sb.Append(", ");
                    primero = false;
                    CadenaPy(sb, k);
                    sb.Append(": ");
                    EscribirPy(sb, d[k], ordenar);
                }
                sb.Append('}');
                break;
            }
            case IEnumerable e:
            {
                sb.Append('[');
                var primero = true;
                foreach (var x in e)
                {
                    if (!primero) sb.Append(", ");
                    primero = false;
                    EscribirPy(sb, x, ordenar);
                }
                sb.Append(']');
                break;
            }
            default: CadenaPy(sb, o.ToString() ?? ""); break;
        }
    }

    static void CadenaPy(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>repr() de un float de Python.</summary>
    public static string FloatPy(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsInfinity(x)) return x > 0 ? "Infinity" : "-Infinity";
        var r = x.ToString("R", CultureInfo.InvariantCulture);
        if (r.Contains('E'))
        {
            // 1E-05 → 1e-05, 1E+16 → 1e+16
            var partes = r.Split('E');
            var exp = int.Parse(partes[1], CultureInfo.InvariantCulture);
            return $"{partes[0]}e{(exp < 0 ? "-" : "+")}{Math.Abs(exp):00}";
        }
        if (Math.Abs(x) >= 1e16) // Python pasa a notación científica desde 1e16
        {
            var e = x.ToString("0.################E+00", CultureInfo.InvariantCulture).Replace("E", "e");
            return e;
        }
        return r.Contains('.') ? r : r + ".0";
    }
}
