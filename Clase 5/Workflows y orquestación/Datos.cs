using MesaYa.Comun;

namespace MesaYa.Orquestacion;

/// <summary>
/// El mundo de MesaYa: sucursales, menú, política de cancelación y los "sistemas" que consultan
/// los patrones (disponibilidad, reservas, ventas y caja). Todo es inventado y determinista:
/// el mismo pedido da siempre el mismo dato, así las corridas se pueden comparar y grabar.
/// </summary>
public static class Datos
{
    public static readonly string[] Sucursales = ["Centro", "Pocitos", "Carrasco"];
    public static readonly string[] Dias = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];

    public const string Ficha =
        "MesaYa es una cadena de restaurantes con tres sucursales en Montevideo:\n" +
        "- Centro: salón interior de 60 cubiertos.\n" +
        "- Pocitos: salón y terraza, 80 cubiertos. Es la más pedida los fines de semana.\n" +
        "- Carrasco: salón, jardín y salón privado para eventos, 70 cubiertos.\n" +
        "Abre de martes a domingo para la cena, de 20 a 00 h. Sábados y domingos también al mediodía, de 12 a 16 h. Los lunes está cerrado.\n" +
        "Para reservar hacen falta sucursal, día, hora y cantidad de personas. Grupos de más de 8 personas se gestionan como evento.";

    public const string Alergenos =
        "Carta y alérgenos de MesaYa:\n" +
        "- Ensalada de quinoa, palta y tomates asados: sin TACC, vegana.\n" +
        "- Risotto de hongos: sin TACC, vegetariano (lleva manteca y queso).\n" +
        "- Bife de chorizo con papas rústicas: sin TACC.\n" +
        "- Ñoquis de papa con salsa pomodoro: contiene gluten. Hay versión sin TACC por pedido, cocinada aparte.\n" +
        "- Salmón grillado con vegetales: sin TACC. Contiene pescado.\n" +
        "- Milanesa napolitana: contiene gluten, huevo y lácteos.\n" +
        "- Flan casero con dulce de leche: sin TACC, contiene huevo y lácteos.\n" +
        "- Brownie con nueces: contiene gluten, huevo, lácteos y frutos secos.\n" +
        "La cocina tiene un sector separado para preparaciones sin TACC. Ante alergias graves, avisar al mozo al llegar.";

    public const string Politica =
        "Política de cancelación de reservas · versión 3 (vigente desde marzo de 2026):\n" +
        "- Para reservas de 5 personas o más se cobra una seña de $500 por persona.\n" +
        "- Reservas de hasta 8 personas: si se cancela con 24 horas o más de anticipación, se devuelve la seña completa.\n" +
        "- Reservas de hasta 8 personas: si se cancela con menos de 24 horas, la seña no se devuelve en dinero: queda como crédito para usar en cualquier sucursal dentro de los 30 días.\n" +
        "- Solo para grupos de 9 personas o más, el plazo para cancelar sin cargo es de 72 horas en lugar de 24.\n" +
        "- Si el cliente no se presenta, se pierde la seña.";

    // ---------- reservas ----------
    /// <summary>Disponibilidad de mesa. Pocitos el sábado a las 21 está lleno, para mostrar alternativas.</summary>
    public static Dict Disponibilidad(string? sucursal, string? dia, string? hora, long? personas)
    {
        var faltan = new List<string>();
        if (!Sucursales.Contains(sucursal)) faltan.Add("sucursal");
        if (dia == null || !Dias.Contains(dia)) faltan.Add("día");
        if (hora == null) faltan.Add("hora");
        if (personas is null or < 1) faltan.Add("personas");
        if (faltan.Count > 0) return new Dict { ["disponible"] = false, ["motivo"] = "faltan datos: " + string.Join(", ", faltan) };
        if (dia == "lunes") return new Dict { ["disponible"] = false, ["motivo"] = "MesaYa está cerrado los lunes" };
        if (personas > 8) return new Dict { ["disponible"] = false, ["motivo"] = "más de 8 personas se reserva como evento" };
        var h = Hora(hora!);
        var cena = h >= 20 * 60 && h <= 23 * 60;
        var mediodia = (dia is "sábado" or "domingo") && h >= 12 * 60 && h <= 15 * 60;
        if (!cena && !mediodia) return new Dict { ["disponible"] = false, ["motivo"] = $"a las {hora} no hay servicio" };
        if (sucursal == "Pocitos" && dia == "sábado" && h is >= 20 * 60 + 30 and <= 21 * 60 + 30)
            return new Dict { ["disponible"] = false, ["motivo"] = "Pocitos está completo el sábado de 20:30 a 21:30", ["alternativas"] = new List<object?> { "20:00", "22:30" } };
        return new Dict { ["disponible"] = true, ["sucursal"] = sucursal, ["dia"] = dia, ["hora"] = hora, ["personas"] = personas };
    }

    public static Dict CrearReserva(string? sucursal, string? dia, string? hora, long? personas)
    {
        var d = Disponibilidad(sucursal, dia, hora, personas);
        if (!d.B("disponible")) return d.Con(new Dict { ["creada"] = false });
        var codigo = "R-" + (1000 + Math.Abs($"{sucursal}{dia}{hora}{personas}".Aggregate(17, (a, c) => a * 31 + c)) % 9000);
        return new Dict { ["creada"] = true, ["codigo"] = codigo, ["sucursal"] = sucursal, ["dia"] = dia, ["hora"] = hora, ["personas"] = personas };
    }

    static int Hora(string hhmm)
    {
        var p = hhmm.Split(':');
        return int.TryParse(p[0], out var h) ? h * 60 + (p.Length > 1 && int.TryParse(p[1], out var m) ? m : 0) : -1;
    }

    // ---------- ventas (para el informe del orquestador) ----------
    public static readonly Dictionary<string, Dictionary<string, Dict>> Ventas = new()
    {
        ["septiembre"] = new()
        {
            ["Centro"] = Mes(1480, 1_036_000, 3, "milanesa napolitana", 0.71),
            ["Pocitos"] = Mes(2310, 1_917_300, 5, "risotto de hongos", 0.88),
            ["Carrasco"] = Mes(1690, 1_571_700, 2, "bife de chorizo", 0.74),
        },
        ["octubre"] = new()
        {
            ["Centro"] = Mes(1395, 962_550, 4, "milanesa napolitana", 0.66),
            ["Pocitos"] = Mes(2540, 2_133_600, 9, "risotto de hongos", 0.93),
            ["Carrasco"] = Mes(1820, 1_765_400, 1, "salmón grillado", 0.79),
        },
    };

    static Dict Mes(long cubiertos, long facturacion, long reclamos, string plato, double ocupacion) => new()
    {
        ["cubiertos"] = cubiertos, ["facturacion_pesos"] = facturacion, ["ticket_promedio"] = facturacion / cubiertos,
        ["reclamos"] = reclamos, ["plato_mas_pedido"] = plato, ["ocupacion_fines_de_semana"] = ocupacion,
    };

    // ---------- caja (para el agente) ----------
    /// <summary>
    /// El caso del agente: la caja de Pocitos del martes no cierra por $3.200 en efectivo.
    /// La causa aparece recién al cruzar tres fuentes: dos pagos por transferencia cargados como efectivo.
    /// </summary>
    public static Dict CierreCaja(string? sucursal, string? dia)
    {
        if (sucursal == "Pocitos" && dia == "martes")
            return new Dict
            {
                ["sucursal"] = "Pocitos", ["dia"] = "martes", ["ventas_sistema"] = new Dict { ["efectivo"] = 21_500L, ["tarjeta"] = 26_800L, ["total"] = 48_300L },
                ["arqueo"] = new Dict { ["efectivo_contado"] = 18_300L, ["tarjeta_comprobantes"] = 26_800L, ["total"] = 45_100L },
                ["diferencia"] = -3_200L, ["medio_con_diferencia"] = "efectivo", ["cerro"] = "encargada Paula R. a las 00:20",
            };
        return new Dict { ["sucursal"] = sucursal, ["dia"] = dia, ["diferencia"] = 0L, ["nota"] = "la caja cerró sin diferencias" };
    }

    public static Dict Tickets(string? sucursal, string? dia, string? medio)
    {
        if (sucursal != "Pocitos" || dia != "martes")
            return new Dict { ["tickets"] = new List<object?>(), ["nota"] = "sin tickets observados para ese día" };
        if (medio == "tarjeta")
            return new Dict { ["medio"] = "tarjeta", ["cantidad"] = 31L, ["total"] = 26_800L, ["nota"] = "coincide con los comprobantes del POS" };
        return new Dict
        {
            ["medio"] = "efectivo", ["cantidad"] = 24L, ["total"] = 21_500L,
            ["ultimos"] = new List<object?>
            {
                new Dict { ["ticket"] = "#4469", ["hora"] = "23:05", ["mesa"] = 4, ["importe"] = 2_100L, ["mozo"] = "mozo2", ["nota"] = "" },
                new Dict { ["ticket"] = "#4471", ["hora"] = "23:40", ["mesa"] = 12, ["importe"] = 1_600L, ["mozo"] = "mozo3", ["nota"] = "pagó con transferencia desde el celular" },
                new Dict { ["ticket"] = "#4472", ["hora"] = "23:41", ["mesa"] = 12, ["importe"] = 1_600L, ["mozo"] = "mozo3", ["nota"] = "dividieron la cuenta; pagó con transferencia" },
                new Dict { ["ticket"] = "#4473", ["hora"] = "23:52", ["mesa"] = 7, ["importe"] = 950L, ["mozo"] = "mozo1", ["nota"] = "" },
            },
        };
    }

    public static Dict Transferencias(string? sucursal, string? dia)
    {
        if (sucursal != "Pocitos" || dia != "martes")
            return new Dict { ["transferencias"] = new List<object?>(), ["nota"] = "no hubo transferencias ese día" };
        return new Dict
        {
            ["cuenta"] = "BROU de MesaYa Pocitos",
            ["transferencias"] = new List<object?>
            {
                new Dict { ["hora"] = "23:40", ["importe"] = 1_600L, ["concepto"] = "mesa 12" },
                new Dict { ["hora"] = "23:42", ["importe"] = 1_600L, ["concepto"] = "MesaYa cena" },
            },
            ["nota"] = "las transferencias no pasan por la caja ni por el POS",
        };
    }
}
