using System.Text.Json.Serialization;

/// <summary>Reserva almacenada en memoria, con el mismo contrato de respuesta que usa el agente.</summary>
/// <param name="ReservaId">Identificador público de la reserva.</param>
/// <param name="Estado">Estado actual: confirmada o cancelada.</param>
/// <param name="Sucursal">Nombre de la sucursal.</param>
/// <param name="FechaHora">Fecha y hora reservadas.</param>
/// <param name="Personas">Cantidad de comensales.</param>
/// <param name="Mesa">Mesa asignada.</param>
/// <param name="CancelarHasta">Fecha informada para cancelar sin cargo.</param>
/// <param name="SucursalId">Código interno usado para comprobar los permisos.</param>
/// <param name="CanceladaEn">Momento de la cancelación, si corresponde.</param>
internal sealed record Reserva(
    string ReservaId,
    string Estado,
    string Sucursal,
    DateTimeOffset FechaHora,
    int Personas,
    string Mesa,
    DateTimeOffset CancelarHasta,
    [property: JsonIgnore] string SucursalId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CanceladaEn = null);
