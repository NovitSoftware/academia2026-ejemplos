using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MesaYa.Rag.Busqueda;
using MesaYa.Rag.Generacion;
using Microsoft.Extensions.Options;

namespace MesaYa.Rag.Pipeline;

public sealed record EtapaGrabada(string Nombre, EstadoEtapa Estado, string Entrada, string Salida, TimeSpan? Duracion);

/// <summary>Todo lo que hace falta para reproducir un turno sin Ollama, sin reranker y sin base.</summary>
public sealed record Grabacion(
    DateTime Fecha,
    string Pregunta,
    IReadOnlyList<MensajeChat> Historial,
    OpcionesTurno Opciones,
    string Consulta,
    string? PromptReescritura,
    ResultadoBusqueda? Busqueda,
    RerankHecho? Rerank,
    IReadOnlyList<long> Seleccionados,
    PromptGenerado? Prompt,
    string Respuesta,
    UsoGeneracion? Uso,
    IReadOnlyList<EtapaGrabada> Etapas);

/// <summary>
/// Plan B para la clase: cada turno que termina bien se guarda en guion/ como JSON. En modo guion,
/// la misma pregunta con el mismo historial y los mismos controles se reproduce desde ese archivo.
/// </summary>
public sealed partial class Guion(IOptions<OpcionesRag> opciones, IWebHostEnvironment entorno, ILogger<Guion> log)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directorio => Path.GetFullPath(Path.Combine(entorno.ContentRootPath, opciones.Value.Guion));

    public int Cantidad => Directory.Exists(Directorio) ? Directory.EnumerateFiles(Directorio, "*.json").Count() : 0;

    /// <summary>Identifica un turno por pregunta, historial y controles.</summary>
    public static string Clave(string pregunta, IReadOnlyList<MensajeChat> historial, OpcionesTurno o)
    {
        var texto = JsonSerializer.Serialize(new { pregunta = pregunta.Trim(), historial, opciones = o });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..16];
    }

    public Grabacion? Buscar(string clave)
    {
        if (!Directory.Exists(Directorio)) return null;
        var archivo = Directory.EnumerateFiles(Directorio, $"*-{clave}.json").FirstOrDefault();
        return archivo is null ? null : JsonSerializer.Deserialize<Grabacion>(File.ReadAllText(archivo));
    }

    public async Task Guardar(TurnoRag t, IReadOnlyList<MensajeChat> historial)
    {
        var g = new Grabacion(DateTime.Now, t.Pregunta, historial, t.Opciones, t.Consulta, t.PromptReescritura,
            t.Busqueda, t.Rerank, t.Seleccionados.Select(s => s.Chunk.Id).ToList(), t.Prompt,
            t.Respuesta.ToString(), t.Uso,
            t.Etapas.Select(e => new EtapaGrabada(e.Nombre, e.Estado, e.Entrada, e.Salida, e.Duracion)).ToList());
        try
        {
            Directory.CreateDirectory(Directorio);
            var clave = Clave(t.Pregunta, historial, t.Opciones);
            foreach (var viejo in Directory.EnumerateFiles(Directorio, $"*-{clave}.json")) File.Delete(viejo);
            var nombre = $"{Slug(t.Pregunta)}-{clave}.json";
            await File.WriteAllTextAsync(Path.Combine(Directorio, nombre), JsonSerializer.Serialize(g, Json));
        }
        catch (IOException ex)
        {
            // Si no se puede grabar, la demo en vivo sigue igual.
            log.LogWarning(ex, "No se pudo grabar el turno en {Directorio}", Directorio);
        }
    }

    static string Slug(string texto)
    {
        var sinTildes = new string(texto.Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        var slug = NoAlfanumerico().Replace(sinTildes, "-").Trim('-');
        return slug.Length > 50 ? slug[..50].TrimEnd('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NoAlfanumerico();
}
