---
document_id: tablet-codigos-error
titulo: Tablet de recepción · Códigos de error
area: sistema-de-reservas
version: 6
fecha: 2026-07-22
estado: vigente
audiencia: [personal, gerencia]
tenant_id: mesaya-ar
---

# Tablet de recepción · Códigos de error

Referencia de los códigos que muestra la aplicación MesaYa Recepción. Si el código no figura acá, seguir la guía general de problemas de la tablet.

## E-4012 · Mesa con doble reserva

Dos reservas quedaron asignadas a la misma mesa y al mismo horario, casi siempre porque una se cargó por teléfono mientras otra entraba por la aplicación. No reiniciar la tablet: se pierde el bloqueo temporal. Abrir *Plano › Conflictos*, mover la reserva más reciente a una mesa libre de igual capacidad y tocar *Resolver*. Si no hay mesa libre, avisar al encargado de turno, que decide a quién se le ofrece la barra o un horario alternativo.

## E-4013 · Seña rechazada

El procesador de pagos rechazó la seña. La reserva queda en estado *pendiente* durante 30 minutos. Ofrecer al cliente reintentar el pago desde el enlace que recibe por correo. No confirmar la mesa a mano.

## E-4021 · Mesa bloqueada por mantenimiento

La mesa está marcada como fuera de servicio en el plano. Solo el encargado puede desbloquearla desde *Plano › Mesas › Habilitar*.

## E-5001 · Sin conexión con el servidor

La tablet perdió la conexión. Las reservas nuevas se guardan localmente y se sincronizan al volver la red. Verificar el wifi de la recepción y, si persiste más de 10 minutos, llamar a soporte al interno 400.

## E-5003 · Error de sincronización

Hay reservas guardadas localmente que el servidor rechazó. Abrir *Ajustes › Sincronización* y reenviar una por una. Nunca borrar la cola de sincronización.
