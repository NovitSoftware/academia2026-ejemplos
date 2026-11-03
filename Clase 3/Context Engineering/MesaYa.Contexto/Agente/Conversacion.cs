using System.Text.Json.Nodes;

namespace MesaYa.Contexto.Agente;

/// <summary>Herramienta que el modelo pidió ejecutar.</summary>
public sealed record LlamadaTool(string Nombre, JsonObject Argumentos)
{
    public override string ToString() =>
        $"{Nombre}({string.Join(", ", Argumentos.Select(a => $"{a.Key}: {a.Value?.ToJsonString()}"))})";
}

public enum Rol { Usuario, Asistente, Herramienta }

/// <summary>Un mensaje de la conversación tal como lo guarda el host (no como lo ve el modelo).</summary>
public sealed class Mensaje
{
    public required Rol Rol { get; init; }
    public string Texto { get; init; } = "";

    /// <summary>Número de turno del usuario al que pertenece. Sirve para decidir qué queda literal.</summary>
    public required int Turno { get; init; }

    /// <summary>Herramientas pedidas por el asistente en este mensaje.</summary>
    public IReadOnlyList<LlamadaTool> Llamadas { get; init; } = [];

    /// <summary>Nombre de la herramienta, cuando el rol es Herramienta.</summary>
    public string? Herramienta { get; init; }

    /// <summary>El modelo ya respondió usando este resultado: se puede limpiar sin perder nada.</summary>
    public bool Procesado { get; set; }

    /// <summary>Vino precargado por el escenario, no de esta sesión en vivo.</summary>
    public bool Precargado { get; init; }
}

/// <summary>Cliente identificado por el canal. Si es null, el chat es anónimo.</summary>
public sealed record PerfilCliente(string Nombre, string Telefono, string Preferencias);

/// <summary>Todo lo que el host recuerda de la conversación. El modelo no recuerda nada entre llamadas.</summary>
public sealed class EstadoConversacion
{
    public List<Mensaje> Mensajes { get; } = [];

    /// <summary>Resumen de los turnos viejos que ya no están literales.</summary>
    public string? Resumen { get; set; }

    /// <summary>Datos que la verificación del resumen tuvo que rescatar (IDs, teléfonos).</summary>
    public List<string> DatosClave { get; } = [];

    /// <summary>Cantidad de mensajes que ya pasaron al resumen.</summary>
    public int MensajesResumidos { get; set; }

    public int TurnoActual => Mensajes.Count == 0 ? 0 : Mensajes.Max(m => m.Turno);

    public void Limpiar()
    {
        Mensajes.Clear();
        Resumen = null;
        DatosClave.Clear();
        MensajesResumidos = 0;
    }
}
