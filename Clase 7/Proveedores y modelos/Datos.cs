using System.Text.RegularExpressions;
using MesaYa.Comun;

namespace MesaYa.Proveedores;

/// <summary>Datos inventados de MesaYa: políticas para buscar con embeddings, la tool de disponibilidad y el filtro de datos personales.</summary>
public static class Datos
{
    public const string Hoy = "jueves 12/11/2026";

    /// <summary>Las políticas del restaurante. El paso de embeddings busca la más parecida al mensaje.</summary>
    public static readonly List<Dict> Politicas =
    [
        P("grupos", "Grupos grandes", "Para grupos de 8 personas o más se pide una seña de $2.500 por persona y se reserva un salón aparte si está disponible."),
        P("celiacos", "Opciones sin TACC", "Para clientes celíacos, todas las sucursales tienen carta sin TACC con cocina separada. Conviene avisarlo al reservar para preparar la mesa."),
        P("cancelacion", "Cancelación", "La seña se devuelve completa si se cancela con más de 24 horas; entre 2 y 24 horas, el 50 %; con menos de 2 horas no se devuelve."),
        P("mascotas", "Mascotas", "Se aceptan mascotas en las mesas de la vereda de Palermo y Belgrano. En el salón solo perros de asistencia."),
        P("estacionamiento", "Estacionamiento", "Palermo tiene convenio con el estacionamiento de Gorriti 4800: dos horas sin cargo con la consumición."),
        P("horarios", "Horarios", "De martes a domingo de 12 a 16 y de 20 a 0.30. Los lunes cierra Centro; Palermo y Belgrano abren solo a la noche."),
        P("cumpleanos", "Cumpleaños", "Para cumpleaños se puede traer torta propia con un cargo de descorche de $3.000 por torta."),
        P("pagos", "Medios de pago", "Se acepta efectivo, débito, crédito y transferencia. La seña se paga solo por transferencia o con link de pago."),
    ];

    static Dict P(string id, string titulo, string texto) => new() { ["id"] = id, ["titulo"] = titulo, ["texto"] = texto };

    /// <summary>La única tool del pipeline: consultar disponibilidad (datos fijos, para que el guion sea repetible).</summary>
    public static readonly List<Dict> Tools =
    [
        new Dict
        {
            ["type"] = "function",
            ["function"] = new Dict
            {
                ["name"] = "buscar_disponibilidad",
                ["description"] = "Busca mesas libres en una sucursal de MesaYa.",
                ["parameters"] = new Dict
                {
                    ["type"] = "object",
                    ["properties"] = new Dict
                    {
                        ["sucursal"] = new Dict { ["type"] = "string", ["enum"] = new List<object?> { "Palermo", "Belgrano", "Centro" } },
                        ["fecha"] = new Dict { ["type"] = "string", ["description"] = "Fecha en formato AAAA-MM-DD" },
                        ["hora"] = new Dict { ["type"] = "string", ["description"] = "Hora en formato HH:MM" },
                        ["personas"] = new Dict { ["type"] = "integer" },
                    },
                    ["required"] = new List<object?> { "sucursal", "fecha", "hora", "personas" },
                },
            },
        },
    ];

    public static Dict BuscarDisponibilidad(Dict args) => new()
    {
        ["sucursal"] = args.Str("sucursal", "Palermo"), ["fecha"] = args.Str("fecha"), ["personas"] = args.G("personas"),
        ["disponible"] = true, ["horarios"] = new List<object?> { "20:30", "21:00", "22:15" }, ["mesa"] = "salón principal, mesa 12",
    };

    // ---------- filtro de datos personales ----------
    static readonly (string tipo, Regex patron)[] Patrones =
    [
        ("correo", new Regex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.Compiled)),
        ("teléfono", new Regex(@"\+?\d[\d\s-]{7,}\d", RegexOptions.Compiled)),
        ("DNI", new Regex(@"\b\d{1,2}\.\d{3}\.\d{3}\b", RegexOptions.Compiled)),
        ("nombre", new Regex(@"(?<=\b(?:[Ss]oy|[Mm]e llamo|a nombre de)\s)\p{Lu}\p{Ll}+(?:\s\p{Lu}\p{Ll}+)?", RegexOptions.Compiled)),
    ];

    /// <summary>Reemplaza correos, teléfonos, DNI y nombres por marcadores. Devuelve el texto y lo que encontró.</summary>
    public static (string texto, List<Dict> hallados) Filtrar(string texto)
    {
        var hallados = new List<Dict>();
        foreach (var (tipo, patron) in Patrones)
            texto = patron.Replace(texto, m =>
            {
                hallados.Add(new Dict { ["tipo"] = tipo, ["valor"] = m.Value });
                return $"[{tipo.ToUpperInvariant()}]";
            });
        return (texto, hallados);
    }
}
