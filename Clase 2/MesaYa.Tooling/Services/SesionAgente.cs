using System.Text.Json;
using System.Text.Json.Nodes;
using MesaYa.Agent;

namespace MesaYa.Tooling.Services;

/// <summary>Conversación y confirmación pendientes, aisladas por circuito de Blazor.</summary>
/// <param name="configuracion">Opciones del modelo y de la API de reservas.</param>
/// <param name="logger">Registro de fallos del servidor.</param>
public sealed class SesionAgente(IConfiguration configuracion, ILogger<SesionAgente> logger) : IDisposable
{
    readonly IConfiguration configuracion = configuracion ?? throw new ArgumentNullException(nameof(configuracion));
    readonly ILogger<SesionAgente> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    AgenteReservas? agente;
    HttpClient? ollamaHttp;
    MesaYaClient? mesaYa;
    TaskCompletionSource<bool>? confirmacion;
    readonly List<EventoAgente> eventos = [];
    int pasoDemo;
    bool disposed;
    string? modeloElegido;

    /// <summary>Notifica cambios para actualizar la interfaz.</summary>
    public event Action? Cambio;
    /// <summary>Historial visible de mensajes y actividad.</summary>
    public IReadOnlyList<EventoAgente> Eventos => eventos;
    /// <summary>Indica si hay un turno en curso.</summary>
    public bool Ocupado { get; private set; }
    /// <summary>Datos de la reserva que requiere aprobación.</summary>
    public JsonObject? ReservaPendiente { get; private set; }
    /// <summary>Modo seleccionado al comenzar la conversación.</summary>
    public string Modo { get; private set; } = "Ollama";
    /// <summary>Nombre del modelo elegido en la interfaz o del modelo inicial configurado.</summary>
    public string Modelo { get; private set; } = "qwen3.5:9b";
    /// <summary>Uso de la última respuesta medida, incluidas las respuestas que solicitan tools.</summary>
    public UsoModelo? UltimoUso { get; private set; }
    /// <summary>Suma del consumo de todas las respuestas medidas en esta conversación.</summary>
    public UsoModelo? UsoAcumulado { get; private set; }
    /// <summary>Cantidad de respuestas de Ollama con contadores de tokens disponibles.</summary>
    public int RespuestasMedidas { get; private set; }
    /// <summary>Indica si terminó el guion simulado.</summary>
    public bool DemoTerminada => pasoDemo >= ModeloSimulado.MensajesDelUsuario.Length;
    /// <summary>Próximo mensaje del guion simulado.</summary>
    public string MensajeDemo => DemoTerminada ? "" : ModeloSimulado.MensajesDelUsuario[pasoDemo];

    /// <summary>Inicia una conversación sin modificar reservas ya creadas.</summary>
    /// <param name="modo">Ollama, Simulado, Falla503 o Lento.</param>
    public void NuevaConversacion(string modo)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Ocupado) return;
        if (modo is not ("Ollama" or "Simulado" or "Falla503" or "Lento"))
            throw new ArgumentException("Modo desconocido.", nameof(modo));
        ollamaHttp?.Dispose();
        mesaYa?.Dispose();
        eventos.Clear();
        pasoDemo = 0;
        Modo = modo;
        ReservaPendiente = null;
        UltimoUso = null;
        UsoAcumulado = null;
        RespuestasMedidas = 0;
        var ollama = configuracion.GetSection("Ollama").Get<ConfiguracionOllama>() ?? new ConfiguracionOllama();
        Modelo = modeloElegido ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? ollama.Model;
        mesaYa = new MesaYaClient(
            Environment.GetEnvironmentVariable("MESAYA_URL") ?? configuracion["MesaYa:Url"] ?? "http://localhost:5080",
            configuracion["MesaYa:Token"] ?? "demo-agente-token",
            texto => Agregar(new("Http", texto)), texto => Agregar(new("Host", texto)));
        IModelo modelo;
        if (modo == "Ollama")
        {
            ollamaHttp = new HttpClient
            {
                BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_URL") ?? ollama.Url),
                Timeout = TimeSpan.FromMinutes(5)
            };
            modelo = new ModeloOllama(ollamaHttp, Modelo, RegistrarUso);
        }
        else modelo = new ModeloSimulado(modo == "Falla503" ? "#falla503" : modo == "Lento" ? "#lento" : null);
        agente = new AgenteReservas(modelo, mesaYa, Agregar, SolicitarConfirmacion);
        Cambio?.Invoke();
    }

    /// <summary>Selecciona el modelo para esta sesión e inicia una conversación nueva.</summary>
    /// <param name="nombre">Nombre del modelo instalado seleccionado en la interfaz.</param>
    public void CambiarModelo(string nombre)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nombre);
        if (Ocupado || Modelo == nombre) return;
        modeloElegido = nombre;
        NuevaConversacion(Modo);
    }

    /// <summary>Envía un mensaje o ejecuta el próximo turno simulado.</summary>
    /// <param name="texto">Mensaje para el modelo local; el simulador usa su propio guion.</param>
    /// <returns>Finalización del turno y de las confirmaciones solicitadas.</returns>
    public async Task Enviar(string texto)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Ocupado || (Modo != "Ollama" && DemoTerminada)) return;
        if (agente is null) NuevaConversacion(Modo);
        var entrada = Modo == "Ollama" ? texto.Trim() : MensajeDemo;
        if (string.IsNullOrWhiteSpace(entrada)) return;
        Ocupado = true;
        Cambio?.Invoke();
        try
        {
            await agente!.Enviar(entrada);
            if (Modo != "Ollama") pasoDemo++;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(e, "No se pudo completar el turno del agente en modo {Modo}", Modo);
            Agregar(new("Host", $"No se pudo completar el turno. {e.Message} Revisá la conexión a Ollama y a MesaYa.Api.", true));
        }
        finally
        {
            Ocupado = false;
            Cambio?.Invoke();
        }
    }

    Task<bool> SolicitarConfirmacion(JsonObject input)
    {
        ReservaPendiente = input;
        confirmacion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cambio?.Invoke();
        return confirmacion.Task;
    }

    /// <summary>Resuelve la confirmación humana pendiente.</summary>
    /// <param name="aceptar">True para crear la reserva; false para devolver el rechazo al modelo.</param>
    public void Confirmar(bool aceptar)
    {
        var pendiente = confirmacion;
        if (pendiente is null) return;
        confirmacion = null;
        ReservaPendiente = null;
        Agregar(new("Host", aceptar ? "Reserva autorizada por el usuario." : "El usuario rechazó la reserva."));
        pendiente.TrySetResult(aceptar);
    }

    void Agregar(EventoAgente evento)
    {
        if (disposed) return;
        eventos.Add(evento);
        Cambio?.Invoke();
    }

    void RegistrarUso(UsoModelo uso)
    {
        if (disposed) return;
        UltimoUso = uso;
        UsoAcumulado = UsoAcumulado is null ? uso : UsoAcumulado.Sumar(uso);
        RespuestasMedidas++;
        Cambio?.Invoke();
    }

    /// <summary>Cancela confirmaciones y libera las conexiones al cerrar el circuito.</summary>
    public void Dispose()
    {
        disposed = true;
        confirmacion?.TrySetCanceled();
        ollamaHttp?.Dispose();
        mesaYa?.Dispose();
        Cambio = null;
    }
}
