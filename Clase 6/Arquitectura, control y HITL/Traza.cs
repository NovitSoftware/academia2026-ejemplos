using MesaYa.Comun;

namespace MesaYa.Hitl;

/// <summary>
/// Registro de un turno: los pasos (llamadas al LLM, tools, reglas y código), las llamadas al modelo,
/// un reloj que avanza con la duración de cada paso y el costo estimado de los tokens.
/// </summary>
public sealed class Traza(Dict config)
{
    public Dict Config { get; } = config;
    public List<Dict> Pasos { get; } = [];
    public List<Dict> Llamadas { get; } = [];
    public string? Grabado { get; set; }
    long cursor;

    public long Total => cursor;

    public Dict Agregar(Dict paso, string? grupo = null)
    {
        paso["inicio"] = cursor;
        if (grupo != null) paso["grupo"] = grupo;
        cursor += (long)(J.Num(paso.G("ms")) ?? 0);
        Pasos.Add(paso);
        return paso;
    }

    public static Dict Paso(string tipo, string titulo, string detalle, object? salida = null, string? marca = null, long ms = 0) => new()
    {
        ["tipo"] = tipo, ["titulo"] = titulo, ["detalle"] = detalle, ["salida"] = salida, ["marca"] = marca, ["ms"] = ms, ["tokens"] = 0L,
    };

    /// <summary>Una llamada al LLM. Devuelve los datos (si hay esquema), el texto y el paso, sin agregarlo.</summary>
    public async Task<(Dict datos, string texto, Dict paso)> Llm(string titulo, string detalle, List<Dict> mensajes,
        Dict? esquema = null, int maxTokens = 300)
    {
        var r = await Ollama.Instancia.Chat(mensajes, esquema, maxTokens: maxTokens);
        var texto = r.Str("texto").Trim();
        var datos = new Dict();
        if (esquema != null)
        {
            try { datos = J.Leer(texto) as Dict ?? new Dict(); }
            catch (System.Text.Json.JsonException) { datos = null!; }
        }
        var indice = Llamadas.Count;
        Llamadas.Add(new Dict
        {
            ["titulo"] = titulo, ["esquema"] = esquema != null, ["salida"] = texto,
            // Las imágenes no viajan a la interfaz: se marca que había una.
            ["mensajes"] = mensajes.Select(m => m.ContainsKey("images") ? m.Con(new Dict { ["images"] = null, ["imagen"] = true }) : m).ToList(),
            ["tokens_prompt"] = r.G("tokens_prompt"), ["tokens_respuesta"] = r.G("tokens_respuesta"), ["ms"] = r.G("ms"),
        });
        if (r.S("grabado") is { } g) Grabado = g;
        var paso = Paso("llm", titulo, detalle, esquema != null ? (object?)datos ?? texto : texto);
        paso["ms"] = (long)r.D("ms");
        paso["tokens"] = (long)(r.D("tokens_prompt") + r.D("tokens_respuesta"));
        paso["costo"] = Datos.Costo(r.D("tokens_prompt"), r.D("tokens_respuesta"));
        paso["llamada"] = indice;
        return (datos!, texto, paso);
    }

    public Dict Resumen() => new()
    {
        ["llamadas"] = (long)Llamadas.Count,
        ["tokens"] = Pasos.Where(p => p.Str("tipo") == "llm").Sum(p => (long)p.D("tokens")),
        ["ms_total"] = Total, ["pasos"] = (long)Pasos.Count,
        ["costo"] = Pasos.Sum(p => p.D("costo")),
    };

    public static List<Dict> M(string sistema, string usuario) =>
        [new Dict { ["role"] = "system", ["content"] = sistema }, new Dict { ["role"] = "user", ["content"] = usuario }];
}

/// <summary>Atajos para armar esquemas JSON de salida estructurada.</summary>
public static class Esquema
{
    public static List<object?> Lista(params object?[] x) => [.. x];
    public static Dict Tipo(params object?[] tipos) => new() { ["type"] = tipos.Length == 1 ? tipos[0] : Lista(tipos) };
    public static Dict Opciones(params object?[] valores) => new() { ["type"] = "string", ["enum"] = Lista(valores) };
    public static Dict Objeto(Dict propiedades) =>
        new() { ["type"] = "object", ["properties"] = propiedades, ["required"] = Lista([.. propiedades.Keys]) };
}
