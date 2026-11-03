using System.Text.Json.Nodes;

namespace MesaYa.Contexto.Contexto;

/// <summary>Las cuatro partes de lo que ve el modelo (slide 02).</summary>
public enum Tipo { Instrucciones, Conocimiento, Interaccion, Tarea }

/// <summary>Componentes con presupuesto propio (slide 08).</summary>
public enum Componente { Sistema, Herramientas, Documentos, Memoria, Historial, ResultadosTools, Consulta, ReservaSalida }

public enum Estrategia { BajoDemanda, PreCarga }
public enum ModoDocumentos { SinMetadata, ConMetadata, SoloVigentes }
public enum Resumidor { Fiel, Defectuoso }

/// <summary>Perillas del contexto. Cada escenario prende o apaga una para comparar.</summary>
public sealed class OpcionesContexto
{
    public string Modelo { get; set; } = "qwen3.5:9b";

    /// <summary>Ventana efectiva en tokens: se envía a Ollama como num_ctx.</summary>
    public int Ventana { get; set; } = 8192;

    public Estrategia Estrategia { get; set; } = Estrategia.BajoDemanda;
    public bool Compactar { get; set; } = true;
    public int UmbralPct { get; set; } = 75;
    public int TurnosLiterales { get; set; } = 3;
    public Resumidor Resumidor { get; set; } = Resumidor.Fiel;
    public bool LimpiarToolsProcesadas { get; set; } = true;
    public bool ResultadosCompactos { get; set; } = true;
    public ModoDocumentos Documentos { get; set; } = ModoDocumentos.ConMetadata;
    public bool Delimitar { get; set; } = true;
    public bool FiltrarPermisos { get; set; } = true;
    public bool ConfirmarAcciones { get; set; } = true;

    public int Umbral => Ventana * UmbralPct / 100;

    public OpcionesContexto Clonar() => (OpcionesContexto)MemberwiseClone();
}

/// <summary>Asignación de tokens por componente. Es la tabla del slide 08, escalada a la ventana elegida.</summary>
public static class Presupuesto
{
    static readonly Dictionary<Componente, int> Base32k = new()
    {
        [Componente.Sistema] = 2_000,
        [Componente.Herramientas] = 3_000,
        [Componente.Memoria] = 1_000,
        [Componente.Documentos] = 10_000,
        [Componente.Historial] = 8_000,
        [Componente.ResultadosTools] = 4_000,
        [Componente.Consulta] = 500,
        [Componente.ReservaSalida] = 3_500,
    };

    public static IEnumerable<Componente> Todos => Base32k.Keys;

    public static int Tope(Componente c, int ventana) => (int)Math.Round(Base32k[c] * ventana / 32_000.0);

    public static Tipo TipoDe(Componente c) => c switch
    {
        Componente.Sistema or Componente.Herramientas => Tipo.Instrucciones,
        Componente.Documentos or Componente.Memoria => Tipo.Conocimiento,
        Componente.Historial or Componente.ResultadosTools => Tipo.Interaccion,
        _ => Tipo.Tarea,
    };

    public static string Nombre(Componente c) => c switch
    {
        Componente.Sistema => "Sistema y reglas",
        Componente.Herramientas => "Herramientas",
        Componente.Documentos => "Documentos",
        Componente.Memoria => "Memoria",
        Componente.Historial => "Historial",
        Componente.ResultadosTools => "Resultados de tools",
        Componente.Consulta => "Consulta",
        _ => "Reserva de salida",
    };

    public static string Nombre(Tipo t) => t switch
    {
        Tipo.Instrucciones => "Instrucciones",
        Tipo.Conocimiento => "Conocimiento",
        Tipo.Interaccion => "Interacción",
        _ => "Tarea",
    };

    /// <summary>Qué pasa cuando el componente se pasa de su tope (slide 08: nunca se recortan las instrucciones).</summary>
    public static string Politica(Componente c) => c switch
    {
        Componente.Sistema or Componente.Herramientas => "Nunca se recorta",
        Componente.Documentos => "Pasar a carga bajo demanda",
        Componente.Memoria => "Quedarse con lo más reciente",
        Componente.Historial => "Resumen + últimos turnos literales",
        Componente.ResultadosTools => "Limpiar los ya procesados",
        Componente.Consulta => "Pedir una consulta más corta",
        _ => "Reservada para la respuesta",
    };
}

/// <summary>Un tramo de la ventana, en el orden exacto en que se envía.</summary>
public sealed class Bloque
{
    public required Componente Componente { get; init; }
    public required string Titulo { get; init; }
    public required string Texto { get; init; }
    public required int Tokens { get; init; }

    /// <summary>Rol del mensaje en el pedido a Ollama (system, user, assistant, tool o tools).</summary>
    public required string Rol { get; init; }

    /// <summary>Aclaración visible: "omitido, ya procesado", "resumen de 12 mensajes", etc.</summary>
    public string? Nota { get; init; }

    /// <summary>Estimación de que Ollama lo descarta porque no entra en num_ctx.</summary>
    public bool FueraDeVentana { get; set; }

    public Tipo Tipo => Presupuesto.TipoDe(Componente);
}

/// <summary>La ventana completa de una llamada al modelo.</summary>
public sealed class Ventana
{
    public required List<Bloque> Bloques { get; init; }
    public required JsonObject Cuerpo { get; init; }
    public required int NumCtx { get; init; }
    public required int Umbral { get; init; }

    public int Estimado => Bloques.Sum(b => b.Tokens);
    public int EstimadoDentro => Bloques.Where(b => !b.FueraDeVentana).Sum(b => b.Tokens);
    public bool Desborda => Estimado > NumCtx;
    public bool SuperaUmbral => Estimado > Umbral;

    public int TokensDe(Componente c) => Bloques.Where(b => b.Componente == c).Sum(b => b.Tokens);
}
