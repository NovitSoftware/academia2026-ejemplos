using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MesaYa.Agent;

/// <summary>Error que vuelve al modelo como tool_result con is_error = true.</summary>
public record ErrorTool(string Codigo, string Mensaje, bool Reintentable = false, object? Detalle = null);

/// <summary>
/// Las tres capas de validación que corre el HOST antes de ejecutar la tool.
/// El modelo puede equivocarse: el código no confía en lo que recibe.
/// </summary>
public static class Validador
{
    static readonly string[] Campos = ["sucursal_id", "fecha_hora", "personas", "telefono", "nota"];

    // ---------- Capa 1 · Sintáctica: ¿el JSON cumple el esquema? ----------
    public static ErrorTool? Sintactica(JsonObject a)
    {
        foreach (var extra in a.Select(p => p.Key).Except(Campos))
            return Invalid($"El campo '{extra}' no existe en el esquema (additionalProperties: false).");
        foreach (var req in new[] { "sucursal_id", "fecha_hora", "personas", "telefono" })
            if (a[req] is null) return Invalid($"Falta el campo obligatorio '{req}'.");

        if (a["sucursal_id"] is not JsonValue s || !s.TryGetValue(out string? suc) || !Herramientas.Sucursales.Contains(suc))
            return Invalid("sucursal_id debe ser PAL, BEL o CAB.");
        if (a["fecha_hora"] is not JsonValue f || !f.TryGetValue(out string? fh) ||
            !DateTimeOffset.TryParse(fh, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || !fh.Contains('T'))
            return Invalid("fecha_hora debe ser ISO 8601 con zona, ej. 2026-11-06T21:00:00-03:00.");
        if (a["personas"] is not JsonValue p || !p.TryGetValue(out int personas))
            return Invalid("personas debe ser un número entero, no texto.");
        if (personas is < 1 or > 12)
            return Invalid("personas va de 1 a 12. Para grupos más grandes, derivá a eventos@mesaya.com.");
        if (a["telefono"] is not JsonValue t || !t.TryGetValue(out string? tel) || !Regex.IsMatch(tel, @"^\+549[0-9]{10}$"))
            return Invalid("telefono debe tener el formato +549 y 10 dígitos, ej. +5491155550123.");
        if (a["nota"] is JsonValue n && n.TryGetValue(out string? nota) && nota.Length > 200)
            return Invalid("nota admite como máximo 200 caracteres.");
        return null;
    }

    // ---------- Capa 2 · Semántica: ¿tiene sentido en el mundo real? ----------
    public static ErrorTool? Semantica(JsonObject a)
    {
        var fecha = DateTimeOffset.Parse(a["fecha_hora"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        if (fecha <= DateTimeOffset.Now)
            return new("FECHA_PASADA", "La fecha ya pasó. Pedile al usuario otra fecha.");
        if (fecha > DateTimeOffset.Now.AddDays(60))
            return new("FECHA_LEJANA", "Solo se reserva con hasta 60 días de anticipación.");
        var h = fecha.ToOffset(TimeSpan.FromHours(-3)).TimeOfDay;
        bool abierto = (h >= TimeSpan.FromHours(12) && h <= TimeSpan.FromHours(15.5)) ||
                       (h >= TimeSpan.FromHours(20) && h <= TimeSpan.FromHours(23));
        if (!abierto)
            return new("SUCURSAL_CERRADA", "MesaYa toma reservas de 12:00 a 15:30 y de 20:00 a 23:00.");
        return null;
    }

    // ---------- Capa 3 · Seguridad: ¿algo de esto puede dañar? ----------
    static readonly Regex Sospechoso = new(@"<\s*script|ignor(a|á|e)\s+(las\s+)?instrucciones|;\s*drop\s+table|\p{Cc}",
                                           RegexOptions.IgnoreCase);
    public static ErrorTool? Seguridad(JsonObject a, int reservasEnConversacion)
    {
        if (a["nota"]?.GetValue<string>() is { } nota && Sospechoso.IsMatch(nota))
            return new("NOTA_RECHAZADA", "La nota tiene contenido no permitido. Reservá sin nota o con una nota simple.");
        if (reservasEnConversacion >= 3)
            return new("LIMITE_DE_USO", "Máximo 3 reservas por conversación. Derivá a un humano.");
        return null;
    }

    static ErrorTool Invalid(string msg) => new("INVALID_ARGS", msg);
}
