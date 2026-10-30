using System.Text.Json.Nodes;

namespace MesaYa.Agent;

/// <summary>
/// Definición de las tools tal como las ve el modelo: nombre, descripción y JSON Schema.
/// Es el CONTRATO. El modelo no ve el código: solo esto.
/// </summary>
public static class Herramientas
{
    public static readonly string[] Sucursales = ["PAL", "BEL", "CAB"];

    public static JsonArray Definiciones() => new()
    {
        new JsonObject
        {
            ["name"] = "buscar_disponibilidad",
            ["description"] = "Devuelve los horarios libres de una sucursal para una fecha. " +
                              "Usala antes de reservar si el usuario no tiene un horario fijo. No reserva nada.",
            ["input_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "sucursal_id": { "type": "string", "enum": ["PAL", "BEL", "CAB"],
                                 "description": "PAL = Palermo, BEL = Belgrano, CAB = Caballito" },
                "fecha":       { "type": "string", "format": "date", "description": "Formato AAAA-MM-DD" }
              },
              "required": ["sucursal_id", "fecha"],
              "additionalProperties": false
            }
            """)
        },
        new JsonObject
        {
            ["name"] = "reservar_mesa",
            ["description"] = "Reserva una mesa en una sucursal de MesaYa. Crea una reserva real: " +
                              "antes de llamarla, confirmá con el usuario fecha, hora, personas y sucursal. " +
                              "No sirve para modificar ni cancelar reservas. " +
                              "Si devuelve SIN_DISPONIBILIDAD, ofrecé las alternativas que vienen en el error.",
            ["input_schema"] = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "sucursal_id": { "type": "string", "enum": ["PAL", "BEL", "CAB"] },
                "fecha_hora":  { "type": "string", "format": "date-time",
                                 "description": "ISO 8601 con zona, ej. 2026-11-06T21:00:00-03:00" },
                "personas":    { "type": "integer", "minimum": 1, "maximum": 12 },
                "telefono":    { "type": "string", "pattern": "^\\+549[0-9]{10}$",
                                 "description": "Celular argentino, ej. +5491155550123" },
                "nota":        { "type": "string", "maxLength": 200 }
              },
              "required": ["sucursal_id", "fecha_hora", "personas", "telefono"],
              "additionalProperties": false
            }
            """)
        }
    };
}
