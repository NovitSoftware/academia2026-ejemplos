using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MesaYa.Agent;

/// <summary>Conecta el contrato del host con la API de chat y tools de Ollama.</summary>
/// <param name="http">Cliente configurado con la URL y el timeout de Ollama.</param>
/// <param name="modelo">Nombre del modelo local, por ejemplo qwen3.5:9b.</param>
/// <param name="registrarUso">Receptor opcional de las métricas medidas en cada respuesta.</param>
public class ModeloOllama(HttpClient http, string modelo, Action<UsoModelo>? registrarUso = null) : IModelo
{
    /// <summary>Envía la conversación a Ollama y adapta su respuesta al loop del agente.</summary>
    /// <param name="system">Instrucciones del asistente.</param>
    /// <param name="tools">Definiciones de las herramientas y sus esquemas.</param>
    /// <param name="mensajes">Historial, incluidas llamadas y resultados de herramientas.</param>
    /// <returns>Bloques de texto o tool_use que entiende el host.</returns>
    public async Task<JsonObject> Responder(string system, JsonArray tools, JsonArray mensajes)
    {
        var herramientas = new JsonArray();
        foreach (var tool in tools)
            herramientas.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool!["name"]!.DeepClone(),
                    ["description"] = tool["description"]!.DeepClone(),
                    ["parameters"] = tool["input_schema"]!.DeepClone()
                }
            });

        var body = new JsonObject
        {
            ["model"] = modelo,
            ["stream"] = false,
            ["think"] = false,
            ["messages"] = ConvertirMensajes(system, mensajes),
            ["tools"] = herramientas
        };
        using var resp = await http.PostAsJsonAsync("/api/chat", body);
        var texto = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Error de Ollama ({(int)resp.StatusCode}): {texto}");

        var respuesta = JsonNode.Parse(texto)?.AsObject()
            ?? throw new InvalidOperationException("Ollama devolvió una respuesta vacía.");
        if (UsoModelo.DesdeRespuesta(respuesta) is { } uso) registrarUso?.Invoke(uso);
        var mensaje = respuesta["message"]?.AsObject()
            ?? throw new InvalidOperationException("Ollama devolvió una respuesta sin message.");
        var content = new JsonArray();
        if (mensaje["content"]?.GetValue<string>() is { Length: > 0 } contenido)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = contenido });

        if (mensaje["tool_calls"] is JsonArray llamadas)
            foreach (var llamada in llamadas)
            {
                var funcion = llamada!["function"]!;
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = $"toolu_{Guid.NewGuid():N}",
                    ["name"] = funcion["name"]!.DeepClone(),
                    ["input"] = funcion["arguments"]!.DeepClone()
                });
            }

        if (content.Count == 0)
            throw new InvalidOperationException("Ollama devolvió una respuesta sin texto ni llamadas a tools.");

        return new JsonObject
        {
            ["stop_reason"] = content.Any(b => b!["type"]!.GetValue<string>() == "tool_use") ? "tool_use" : "end_turn",
            ["content"] = content
        };
    }

    static JsonArray ConvertirMensajes(string system, JsonArray mensajes)
    {
        var historial = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });
        var nombresPorId = new Dictionary<string, string>();
        foreach (var mensaje in mensajes)
        {
            var rol = mensaje!["role"]!.GetValue<string>();
            if (mensaje["content"] is not JsonArray bloques)
            {
                historial.Add(mensaje.DeepClone());
                continue;
            }

            if (rol == "assistant")
            {
                var llamadas = new JsonArray();
                var textos = new List<string>();
                foreach (var bloque in bloques)
                {
                    if (bloque!["type"]!.GetValue<string>() == "text")
                        textos.Add(bloque["text"]!.GetValue<string>());
                    else if (bloque["type"]!.GetValue<string>() == "tool_use")
                    {
                        var nombre = bloque["name"]!.GetValue<string>();
                        nombresPorId.Add(bloque["id"]!.GetValue<string>(), nombre);
                        llamadas.Add(new JsonObject
                        {
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = nombre,
                                ["arguments"] = bloque["input"]!.DeepClone()
                            }
                        });
                    }
                }
                var asistente = new JsonObject { ["role"] = "assistant", ["content"] = string.Join("\n", textos) };
                if (llamadas.Count > 0) asistente["tool_calls"] = llamadas;
                historial.Add(asistente);
            }
            else
            {
                foreach (var bloque in bloques)
                    historial.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_name"] = nombresPorId[bloque!["tool_use_id"]!.GetValue<string>()],
                        ["content"] = bloque["content"]!.DeepClone()
                    });
            }
        }
        return historial;
    }
}
