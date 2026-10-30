namespace MesaYa.Agent;

/// <summary>Configuración del agente leída desde appsettings.json.</summary>
public sealed class ConfiguracionAgente
{
    /// <summary>Modelo y servidor de Ollama usados en el modo interactivo.</summary>
    public ConfiguracionOllama Ollama { get; init; } = new();
}

/// <summary>Opciones de conexión al modelo local.</summary>
public sealed class ConfiguracionOllama
{
    /// <summary>Nombre del modelo instalado en Ollama.</summary>
    public string Model { get; init; } = "qwen3.5:9b";

    /// <summary>URL base del servidor de Ollama.</summary>
    public string Url { get; init; } = "http://localhost:11434";
}
