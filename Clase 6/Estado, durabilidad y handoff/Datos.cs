using MesaYa.Comun;

namespace MesaYa.Durabilidad;

/// <summary>Datos fijos de la demo. La fecha es fija para que los prompts (y las grabaciones) no dependan del día.</summary>
public static class Datos
{
    public const string Hoy = "jueves 12 de noviembre de 2026";
    public const string Sabado = "2026-11-14";
    public static readonly string[] Sucursales = ["Centro", "Carrasco", "Pocitos"];
    public const long SenaPorPersona = 500;
    /// <summary>Límite de responsabilidad del agente de reservas: más personas es un evento privado.</summary>
    public const int MaxPersonas = 12;
    public const string Telefono = "2900 1234";
    public const string Cliente = "Laura G.";

    /// <summary>Los cinco pasos de la tarea de reserva. Los que tienen efecto no se pueden repetir sin consecuencias.</summary>
    public static readonly List<Dict> Pasos =
    [
        new() { ["id"] = "extraer_datos", ["nombre"] = "Extraer datos", ["icono"] = "notes", ["efecto"] = false, ["tipo"] = "llm" },
        new() { ["id"] = "bloquear_mesa", ["nombre"] = "Bloquear mesa", ["icono"] = "lock", ["efecto"] = true, ["tipo"] = "api" },
        new() { ["id"] = "cobrar_sena", ["nombre"] = "Cobrar seña", ["icono"] = "tag", ["efecto"] = true, ["tipo"] = "api" },
        new() { ["id"] = "crear_reserva", ["nombre"] = "Crear reserva", ["icono"] = "calendar", ["efecto"] = true, ["tipo"] = "api" },
        new() { ["id"] = "confirmar", ["nombre"] = "Confirmar", ["icono"] = "chat", ["efecto"] = false, ["tipo"] = "llm" },
    ];

    public static readonly List<Dict> Agentes =
    [
        new() { ["id"] = "agente_reservas", ["nombre"] = "Agente de reservas", ["icono"] = "bot" },
        new() { ["id"] = "agente_eventos", ["nombre"] = "Agente de eventos", ["icono"] = "users" },
        new() { ["id"] = "humano_atencion", ["nombre"] = "Atención al cliente", ["icono"] = "person" },
        new() { ["id"] = "sistema", ["nombre"] = "MesaYa", ["icono"] = "alert" },
    ];

    /// <summary>Política de reintentos: qué errores son transitorios.</summary>
    public static bool Transitorio(string codigo) => codigo is "timeout" or "429" or "503";

    public static string Plata(long x) => "$" + x.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-AR"));

    public const int MaxIntentos = 4;
    public const long TimeoutLlamada = 10_000;  // ms, por llamada a la API
}
