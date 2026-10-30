using System.Text.Json.Nodes;

namespace MesaYa.Agent;

/// <summary>Consumo medido por Ollama; los tiempos originales se expresan en nanosegundos.</summary>
/// <param name="TokensEntrada">Tokens del prompt, incluidas instrucciones, tools e historial.</param>
/// <param name="TokensSalida">Tokens generados por el modelo.</param>
/// <param name="TokensEntradaCache">Parte de la entrada reutilizada desde caché, si el servidor la informa.</param>
/// <param name="DuracionEntradaNs">Tiempo de evaluación de la entrada no cacheada.</param>
/// <param name="DuracionSalidaNs">Tiempo de generación de la salida.</param>
/// <param name="DuracionCargaNs">Tiempo de carga del modelo.</param>
/// <param name="DuracionTotalNs">Tiempo total informado por Ollama.</param>
public sealed record UsoModelo(
    long TokensEntrada,
    long TokensSalida,
    long? TokensEntradaCache,
    long? DuracionEntradaNs,
    long? DuracionSalidaNs,
    long? DuracionCargaNs,
    long? DuracionTotalNs)
{
    /// <summary>Suma de tokens de entrada y de salida; la caché ya está incluida en la entrada.</summary>
    public long TokensTotales => TokensEntrada + TokensSalida;

    /// <summary>Tokens de entrada que no se reutilizaron desde caché, cuando se conoce el desglose.</summary>
    public long? TokensEntradaSinCache => TokensEntradaCache is { } cache ? TokensEntrada - cache : null;

    /// <summary>Velocidad de generación, excluyendo la carga del modelo y la evaluación de la entrada.</summary>
    public double? TokensSalidaPorSegundo => DuracionSalidaNs is > 0
        ? TokensSalida / (DuracionSalidaNs.Value / 1_000_000_000d)
        : null;

    /// <summary>Extrae las métricas originales sin estimar datos ausentes.</summary>
    /// <param name="respuesta">Respuesta JSON de la API de chat de Ollama.</param>
    /// <returns>El uso medido, o null si no hay contadores válidos de entrada y salida.</returns>
    public static UsoModelo? DesdeRespuesta(JsonObject respuesta)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        if (Entero(respuesta, "prompt_eval_count") is not { } entrada ||
            Entero(respuesta, "eval_count") is not { } salida) return null;
        var cache = Entero(respuesta, "prompt_eval_cached_count");
        if (cache > entrada) cache = null;
        return new UsoModelo(entrada, salida, cache,
            Entero(respuesta, "prompt_eval_duration"), Entero(respuesta, "eval_duration"),
            Entero(respuesta, "load_duration"), Entero(respuesta, "total_duration"));
    }

    /// <summary>Acumula consumo y tiempos; conserva como desconocido todo dato parcial.</summary>
    /// <param name="otro">Métricas de otra respuesta del mismo modelo y conversación.</param>
    /// <returns>Las métricas sumadas. Su velocidad es el promedio ponderado por tiempo de generación.</returns>
    public UsoModelo Sumar(UsoModelo otro)
    {
        ArgumentNullException.ThrowIfNull(otro);
        return new UsoModelo(TokensEntrada + otro.TokensEntrada, TokensSalida + otro.TokensSalida,
            SumarOpcionales(TokensEntradaCache, otro.TokensEntradaCache),
            SumarOpcionales(DuracionEntradaNs, otro.DuracionEntradaNs),
            SumarOpcionales(TiempoGeneracionMedido, otro.TiempoGeneracionMedido),
            SumarOpcionales(DuracionCargaNs, otro.DuracionCargaNs),
            SumarOpcionales(DuracionTotalNs, otro.DuracionTotalNs));
    }

    static long? Entero(JsonObject respuesta, string nombre) =>
        respuesta[nombre] is JsonValue valor && valor.TryGetValue<long>(out var numero) && numero >= 0
            ? numero : null;

    long? TiempoGeneracionMedido => DuracionSalidaNs == 0 && TokensSalida > 0 ? null : DuracionSalidaNs;

    static long? SumarOpcionales(long? a, long? b) => a is { } x && b is { } y ? x + y : null;
}
