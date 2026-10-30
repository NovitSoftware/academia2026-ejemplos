using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MesaYa.Mcp.Traza;

/// <summary>Participantes del diagrama de secuencia, en el orden en que se dibujan.</summary>
public enum Actor { Usuario, Agente, Llm, ClienteMcp, ServidorMcp, Api }

/// <summary>Request abre un tramo, Response lo cierra; Notificacion y Nota son puntuales.</summary>
public enum TipoTraza { Request, Response, Notificacion, Nota }

/// <summary>Un mensaje entre dos actores. Request y Response se emparejan por <see cref="Correlacion"/>.</summary>
public sealed record EventoTraza
{
    public long Id { get; init; }
    public DateTimeOffset Momento { get; init; }
    /// <summary>Conversación del agente que originó el evento (lado cliente).</summary>
    public string? Conversacion { get; init; }
    /// <summary>Sesión MCP en la que ocurrió el evento (lado servidor).</summary>
    public string? SesionMcp { get; init; }
    public Actor Desde { get; init; }
    public Actor Hacia { get; init; }
    public TipoTraza Tipo { get; init; }
    public string Etiqueta { get; init; } = "";
    public string? Detalle { get; init; }
    public string? Correlacion { get; init; }
    public bool Error { get; init; }
    /// <summary>En un Response, el tiempo desde su Request.</summary>
    public double? DuracionMs { get; init; }
    /// <summary>True si el evento viajó por el protocolo MCP (JSON-RPC real).</summary>
    public bool EsMcp { get; init; }
}

/// <summary>
/// Bus en memoria compartido por toda la aplicación: el servidor MCP, los agentes y el inspector
/// publican acá, y la página de secuencia se suscribe para dibujar en tiempo real.
/// </summary>
public sealed class BusTraza
{
    const int Maximo = 4000;
    readonly object gate = new();
    readonly List<EventoTraza> eventos = [];
    readonly Dictionary<string, (DateTimeOffset inicio, string etiqueta)> abiertos = [];
    readonly ConcurrentDictionary<string, string> conversacionPorSesion = new();
    readonly ConcurrentDictionary<string, string> etiquetas = new();
    long secuencia;

    public static readonly JsonSerializerOptions Lindo = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Se dispara (en el hilo del publicador) por cada evento nuevo.</summary>
    public event Action<EventoTraza>? Publicado;
    /// <summary>Se dispara al limpiar la traza.</summary>
    public event Action? Limpiado;

    /// <summary>Agrega un evento, le asigna id y hora, y calcula la duración si cierra un Request.</summary>
    public EventoTraza Publicar(EventoTraza evento)
    {
        EventoTraza final;
        lock (gate)
        {
            var ahora = DateTimeOffset.Now;
            var duracion = evento.DuracionMs;
            var etiqueta = evento.Etiqueta;
            if (evento.Correlacion is { } c)
            {
                if (evento.Tipo == TipoTraza.Request)
                    abiertos[c] = (ahora, evento.Etiqueta);
                else if (evento.Tipo == TipoTraza.Response && abiertos.Remove(c, out var inicio))
                {
                    duracion ??= (ahora - inicio.inicio).TotalMilliseconds;
                    if (string.IsNullOrEmpty(etiqueta)) etiqueta = "↩ " + inicio.etiqueta;
                }
            }
            final = evento with { Id = ++secuencia, Momento = ahora, DuracionMs = duracion, Etiqueta = etiqueta };
            eventos.Add(final);
            if (eventos.Count > Maximo) eventos.RemoveRange(0, eventos.Count - Maximo);
        }
        try { Publicado?.Invoke(final); } catch { /* un suscriptor caído no frena al resto */ }
        return final;
    }

    /// <summary>Copia de los eventos actuales para dibujar sin bloquear a los publicadores.</summary>
    public EventoTraza[] Instantanea()
    {
        lock (gate) return [.. eventos];
    }

    /// <summary>Etiqueta del Request todavía abierto con esa correlación, si existe.</summary>
    public bool EstaAbierto(string correlacion)
    {
        lock (gate) return abiertos.ContainsKey(correlacion);
    }

    /// <summary>Asocia una sesión MCP con la conversación que la abrió, para unir ambos lados en el gráfico.</summary>
    public void VincularSesion(string? sesionMcp, string conversacion)
    {
        if (!string.IsNullOrEmpty(sesionMcp)) conversacionPorSesion[sesionMcp] = conversacion;
    }

    /// <summary>Nombre legible de una conversación (por ejemplo "Agente #3 · qwen3.5:9b").</summary>
    public void Etiquetar(string conversacion, string etiqueta) => etiquetas[conversacion] = etiqueta;

    /// <summary>Conversación a la que pertenece un evento, resolviendo la sesión MCP si hace falta.</summary>
    public string ConversacionDe(EventoTraza e)
    {
        if (e.Conversacion is { } c) return c;
        if (e.SesionMcp is { } s)
            return conversacionPorSesion.TryGetValue(s, out var conv) ? conv : $"mcp:{s}";
        return "—";
    }

    /// <summary>Nombre para mostrar de una conversación o de una sesión MCP externa.</summary>
    public string NombreDe(string conversacion)
    {
        if (etiquetas.TryGetValue(conversacion, out var e)) return e;
        if (conversacion.StartsWith("mcp:")) return $"Cliente MCP externo · {conversacion[4..Math.Min(conversacion.Length, 12)]}";
        return conversacion;
    }

    /// <summary>Borra la traza (las sesiones y etiquetas se conservan).</summary>
    public void Limpiar()
    {
        lock (gate) { eventos.Clear(); abiertos.Clear(); }
        Limpiado?.Invoke();
    }

    /// <summary>Indenta un JSON para el panel de detalle; si no es JSON lo devuelve igual.</summary>
    public static string Indentar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return texto ?? "";
        try { return JsonNode.Parse(texto)?.ToJsonString(Lindo) ?? texto; }
        catch (JsonException) { return texto; }
    }
}
