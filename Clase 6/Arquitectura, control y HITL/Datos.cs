using System.Globalization;
using MesaYa.Comun;

namespace MesaYa.Hitl;

/// <summary>Datos fijos de la demo. La fecha es fija para que los prompts (y las grabaciones) no dependan del día.</summary>
public static class Datos
{
    public const string Hoy = "jueves 12 de noviembre de 2026";
    public static readonly string[] Sucursales = ["Centro", "Carrasco", "Pocitos"];
    public static readonly string[] Dias = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];
    public const string Cliente = "Laura G.";
    public const string Encargada = "Paula R. (encargada)";

    public static string Plata(long x) => "$" + x.ToString("N0", CultureInfo.GetCultureInfo("es-AR"));

    // ---------- costo de referencia ----------
    // Ollama corre local y no cobra: el costo se estima con el precio de un modelo grande en la nube
    // (USD 3 por millón de tokens de entrada y USD 15 por millón de salida) para que el freno de costo tenga sentido.
    public const double PrecioEntrada = 3.0, PrecioSalida = 15.0;
    public static double Costo(double entrada, double salida) => (entrada * PrecioEntrada + salida * PrecioSalida) / 1_000_000;

    // ---------- reservas iniciales ----------
    public static List<Dict> ReservasIniciales() =>
    [
        new() { ["codigo"] = "R-1043", ["cliente"] = "Laura G.", ["sucursal"] = "Centro", ["dia"] = "sábado", ["hora"] = "21:00", ["personas"] = 4L,
                ["sena"] = 15000L, ["aviso_horas"] = 72L, ["estado"] = "confirmada" },
        new() { ["codigo"] = "R-1051", ["cliente"] = "Laura G.", ["sucursal"] = "Pocitos", ["dia"] = "domingo", ["hora"] = "13:00", ["personas"] = 2L,
                ["sena"] = 4000L, ["aviso_horas"] = 20L, ["estado"] = "confirmada" },
        new() { ["codigo"] = "R-1047", ["cliente"] = "Martín P.", ["sucursal"] = "Carrasco", ["dia"] = "viernes", ["hora"] = "20:30", ["personas"] = 6L,
                ["sena"] = 6000L, ["aviso_horas"] = 0L, ["estado"] = "confirmada" },
    ];

    /// <summary>Política de cancelación v3: con 48 h o más de aviso se devuelve toda la seña; con menos, la mitad.</summary>
    public static long Corresponde(Dict reserva) => reserva.I("aviso_horas") >= 48 ? reserva.I("sena") : reserva.I("sena") / 2;
    public const string Politica = "Política v3: con 48 h o más de aviso, reembolso total de la seña; con menos, el 50 %.";

    /// <summary>Disponibilidad determinista: Pocitos el sábado está lleno; el resto tiene lugar en horario de servicio.</summary>
    public static Dict Disponibilidad(string? sucursal, string? dia, string? hora, int? personas)
    {
        if (sucursal == null || dia == null || hora == null || personas == null)
            return new Dict { ["error"] = "faltan datos: sucursal, dia, hora y personas son obligatorios" };
        if (!TimeSpan.TryParse(hora, CultureInfo.InvariantCulture, out var h))
            return new Dict { ["error"] = "hora inválida: usá HH:MM" };
        var servicio = h >= TimeSpan.FromHours(12) && h <= TimeSpan.FromHours(15) || h >= TimeSpan.FromHours(19) && h <= TimeSpan.FromHours(23.5);
        if (!servicio) return new Dict { ["hay_lugar"] = false, ["motivo"] = "fuera del horario de servicio (12 a 15 y 19 a 23:30)" };
        if (personas > 12) return new Dict { ["hay_lugar"] = false, ["motivo"] = "más de 12 personas es un evento privado" };
        if (sucursal == "Pocitos" && dia == "sábado") return new Dict { ["hay_lugar"] = false, ["motivo"] = "completo" };
        return new Dict { ["hay_lugar"] = true, ["mesas_libres"] = 3L };
    }

    // ---------- arquitectura y autonomía (slides 4.1, 4.2, 5.2 y 5.4) ----------
    public static readonly List<Dict> Herramientas =
    [
        new() { ["nombre"] = "buscar_reservas", ["firma"] = "buscar_reservas(nombre)", ["riesgo"] = "lectura", ["nivel"] = "solo",
                ["errores"] = "más de un resultado: no devuelve el detalle" },
        new() { ["nombre"] = "consultar_disponibilidad", ["firma"] = "consultar_disponibilidad(sucursal, dia, hora, personas)", ["riesgo"] = "lectura", ["nivel"] = "solo",
                ["errores"] = "faltan datos · hora inválida" },
        new() { ["nombre"] = "crear_reserva", ["firma"] = "crear_reserva(sucursal, dia, hora, personas)", ["riesgo"] = "escritura reversible", ["nivel"] = "solo",
                ["errores"] = "sin lugar" },
        new() { ["nombre"] = "cargar_factura", ["firma"] = "cargar_factura(proveedor, items, total)", ["riesgo"] = "escritura reversible", ["nivel"] = "solo",
                ["errores"] = "el total no cierra → revisión humana" },
        new() { ["nombre"] = "cancelar_reserva", ["firma"] = "cancelar_reserva(codigo)", ["riesgo"] = "irreversible", ["nivel"] = "confirmacion",
                ["errores"] = "código inexistente · requiere confirmación del cliente" },
        new() { ["nombre"] = "reembolsar_sena", ["firma"] = "reembolsar_sena(reserva, monto, aprobacion_id=None)", ["riesgo"] = "dinero", ["nivel"] = "aprobacion",
                ["errores"] = "supera el límite → pendiente de aprobación" },
    ];

    public static readonly List<Dict> Niveles =
    [
        new() { ["id"] = "solo", ["nombre"] = "Solo", ["detalle"] = "leer y consultar · crear reservas · cargar facturas que cierran" },
        new() { ["id"] = "confirmacion", ["nombre"] = "Con confirmación", ["detalle"] = "cancelar: el mismo cliente dice que sí" },
        new() { ["id"] = "aprobacion", ["nombre"] = "Con aprobación", ["detalle"] = "dinero sobre el límite · facturas que no cierran" },
        new() { ["id"] = "nunca", ["nombre"] = "Nunca", ["detalle"] = "borrar registros · datos de gerencia: no hay tool que lo haga" },
    ];
}
