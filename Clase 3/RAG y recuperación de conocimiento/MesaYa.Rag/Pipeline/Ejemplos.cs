using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace MesaYa.Rag.Pipeline;

public sealed record EsperadoEvaluacion(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("seccion")] string? Seccion);

public sealed record TurnoHistorial(
    [property: JsonPropertyName("rol")] string Rol,
    [property: JsonPropertyName("texto")] string Texto);

/// <summary>Una pregunta de evaluacion/preguntas.json. Se usa como ejemplo en el chat y, en el paso 7, para medir.</summary>
public sealed record PreguntaEvaluacion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("escenario")] string Escenario,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("perfil")] string Perfil,
    [property: JsonPropertyName("tenant_id")] string Tenant,
    [property: JsonPropertyName("historial")] IReadOnlyList<TurnoHistorial> Historial,
    [property: JsonPropertyName("pregunta")] string Pregunta,
    [property: JsonPropertyName("esperados")] IReadOnlyList<EsperadoEvaluacion> Esperados,
    [property: JsonPropertyName("prohibidos")] IReadOnlyList<string> Prohibidos,
    [property: JsonPropertyName("respuesta_clave")] string RespuestaClave,
    [property: JsonPropertyName("notas")] string Notas);

/// <summary>Lee las preguntas de evaluación desde el archivo configurado.</summary>
public sealed class Ejemplos(IOptions<OpcionesRag> opciones, IWebHostEnvironment entorno)
{
    sealed record Archivo([property: JsonPropertyName("preguntas")] IReadOnlyList<PreguntaEvaluacion> Preguntas);

    public IReadOnlyList<PreguntaEvaluacion> Leer()
    {
        var ruta = Path.GetFullPath(Path.Combine(entorno.ContentRootPath, opciones.Value.Evaluacion));
        return JsonSerializer.Deserialize<Archivo>(File.ReadAllText(ruta))?.Preguntas ?? [];
    }
}
