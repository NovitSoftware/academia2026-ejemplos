namespace MesaYa.Mcp.Servidor;

/// <summary>Escenarios de caos que el servidor MCP aplica antes de llamar a la API.</summary>
public enum Escenario
{
    /// <summary>Sin demoras ni fallas agregadas.</summary>
    Normal,
    /// <summary>Cada llamada a la API espera entre 0,6 y 2,5 s (red lenta).</summary>
    Latencia,
    /// <summary>El primer intento de cada reserva devuelve 503 y el servidor reintenta con backoff.</summary>
    Falla503,
    /// <summary>Latencia alta (2 a 4 s) y además 503 en las reservas.</summary>
    Saturado
}

/// <summary>Configuración global de caos. La cambia la interfaz y la leen las tools del servidor MCP.</summary>
public sealed class Escenarios
{
    /// <summary>Escenario activo para todas las sesiones MCP.</summary>
    public Escenario Actual { get; private set; } = Escenario.Normal;

    /// <summary>Notifica a las páginas abiertas que cambió el escenario.</summary>
    public event Action? Cambio;

    public static string Describir(Escenario e) => e switch
    {
        Escenario.Normal => "Normal · sin demoras",
        Escenario.Latencia => "Red lenta · 0,6–2,5 s por llamada a la API",
        Escenario.Falla503 => "Falla transitoria · 503 en el 1er intento de reserva",
        Escenario.Saturado => "Saturado · 2–4 s por llamada + 503 en reservas",
        _ => e.ToString()
    };

    public void Cambiar(Escenario e)
    {
        if (e == Actual) return;
        Actual = e;
        Cambio?.Invoke();
    }

    /// <summary>Demora a inyectar antes de una llamada a la API.</summary>
    public TimeSpan DemoraSimulada() => Actual switch
    {
        Escenario.Latencia => TimeSpan.FromMilliseconds(Random.Shared.Next(600, 2500)),
        Escenario.Saturado => TimeSpan.FromMilliseconds(Random.Shared.Next(2000, 4000)),
        _ => TimeSpan.Zero
    };

    /// <summary>Marca que MesaYa.Api entiende para responder 503 en el primer intento de un POST.</summary>
    public bool Forzar503 => Actual is Escenario.Falla503 or Escenario.Saturado;
}
