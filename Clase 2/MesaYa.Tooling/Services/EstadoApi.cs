namespace MesaYa.Tooling.Services;

/// <summary>Consulta la disponibilidad de la API de reservas sin ejecutar operaciones de negocio.</summary>
/// <param name="http">Cliente con la dirección de MesaYa.Api y un tiempo límite de conexión.</param>
public sealed class EstadoApi(HttpClient http)
{
    readonly HttpClient http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Comprueba si la API responde correctamente a su consulta de estado.</summary>
    /// <param name="cancellationToken">Cancela la consulta cuando se cierra la página.</param>
    /// <returns>True si la API responde con éxito; false ante errores HTTP, de conexión o timeout.</returns>
    public async Task<bool> EstaActiva(CancellationToken cancellationToken = default)
    {
        try
        {
            using var respuesta = await http.GetAsync("health", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return respuesta.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
