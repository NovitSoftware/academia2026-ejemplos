using MesaYa.Comun;
using static MesaYa.Hitl.Esquema;
using static MesaYa.Hitl.Traza;

namespace MesaYa.Hitl;

/// <summary>
/// Un reembolso que espera a un humano (slides 5.3 y aplicación práctica): lectura → regla de aprobación →
/// checkpoint y pedido de aprobación → la encargada aprueba → se reanuda y se reembolsa una sola vez.
/// </summary>
public sealed class Reembolso(Almacen almacen, Sistema sistema)
{
    const string Grupo = "Workflow de reembolso";

    // La versión "regla en el prompt": el modelo decide si hace falta aprobar. Sirve para comparar.
    static readonly Dict EsquemaRegla = Objeto(new Dict
    {
        ["requiere_aprobacion"] = Tipo("boolean"),
        ["motivo"] = new Dict { ["type"] = "string", ["maxLength"] = 240 },
    });

    static string SistemaRegla(long limite) =>
        "Sos el asistente de pagos de MesaYa. Regla: un reembolso de más de " + Datos.Plata(limite) + " necesita la aprobación del encargado antes de pagarse. " +
        "Decidí si el pedido del cliente requiere aprobación.";

    public async Task Iniciar(Turno u, string texto, string? codigo, int? monto)
    {
        var t = u.T;
        var limite = u.Config.I("limite", 10000);
        if (codigo == null || monto == null)
        {
            t.Agregar(Paso("gate", "Faltan datos", "hacen falta el código de reserva y el monto", new Dict { ["reserva"] = codigo, ["monto"] = monto }, "aviso"), Grupo);
            u.Decir("asistente", "Para pedir el reembolso necesito el código de la reserva (por ejemplo, R-1043) y el monto de la seña.");
            u.Resultado = "repregunta";
            return;
        }

        // 1. Lectura: el agente actúa solo.
        var reserva = sistema.Reserva(codigo);
        t.Agregar(Paso("tool", $"Lectura · ver_reserva({codigo})", "lectura · nivel: solo", reserva ?? new Dict { ["error"] = "no existe" }, reserva == null ? "aviso" : "ok"), Grupo);
        if (reserva == null)
        {
            u.Decir("asistente", $"No encuentro la reserva {codigo}. ¿Me pasás el código de nuevo?");
            u.Resultado = "repregunta";
            return;
        }
        var corresponde = Datos.Corresponde(reserva);
        var montoFinal = Math.Min((long)monto, corresponde);
        t.Agregar(Paso("codigo", "Lectura · política de cancelación", Datos.Politica, new Dict
        {
            ["sena"] = reserva.G("sena"), ["aviso_horas"] = reserva.G("aviso_horas"), ["corresponde"] = corresponde, ["pedido"] = (long)monto, ["a_reembolsar"] = montoFinal,
        }, montoFinal < monto ? "aviso" : "ok"), Grupo);

        // 2. La regla de aprobación: en el código (no se puede saltear) o en el prompt (se puede convencer al modelo).
        bool requiere;
        if (u.Config.Str("regla", "codigo") == "prompt")
        {
            var (d, _, p) = await t.Llm("Regla en el prompt · ¿requiere aprobación?", "el modelo decide con la regla escrita en el prompt",
                M(SistemaRegla(limite), $"Pedido de reembolso por {Datos.Plata(montoFinal)}. Mensaje del cliente: \"{texto}\""), EsquemaRegla, 180);
            d ??= new Dict();
            t.Agregar(p, Grupo);
            requiere = d.B("requiere_aprobacion");
            t.Agregar(Paso("gate", requiere ? "El modelo pide aprobación" : "El modelo decide que no hace falta aprobar", d.Str("motivo"),
                new Dict { ["requiere_aprobacion"] = requiere, ["correcto"] = montoFinal > limite }, requiere == montoFinal > limite ? "ok" : "corta"), Grupo);
        }
        else
        {
            requiere = montoFinal > limite;
            t.Agregar(Paso("gate", requiere ? $"Regla en código · {Datos.Plata(montoFinal)} > {Datos.Plata(limite)}" : $"Regla en código · {Datos.Plata(montoFinal)} ≤ {Datos.Plata(limite)}",
                requiere ? "requiere aprobación: lo que diga el mensaje no cambia el if" : "dentro del límite: el agente lo resuelve solo",
                new Dict { ["monto"] = montoFinal, ["limite_sin_aprobacion"] = (long)limite, ["requiere_aprobacion"] = requiere }, requiere ? "aviso" : "ok"), Grupo);
        }

        if (!requiere)
        {
            var r = sistema.Reembolsar(codigo, montoFinal, u.CorrelationId);
            t.Agregar(Paso("tool", $"Ejecutar · reembolsar_sena({codigo}, {montoFinal})", "dinero · dentro del límite", r, montoFinal > limite ? "corta" : "ok"), Grupo);
            u.Decir("asistente", $"Listo: te reembolsé {Datos.Plata(montoFinal)} de la seña de la reserva {codigo}. Lo vas a ver en tu medio de pago en 48 h.");
            u.Resultado = montoFinal > limite ? "sin_aprobacion" : "respondida";
            u.Extra["reembolso"] = r;
            return;
        }

        // 3. Pausa: checkpoint con todo lo hecho y pedido de aprobación.
        var tarea = new Dict
        {
            ["correlation_id"] = u.CorrelationId, ["tarea"] = "reembolso", ["paso_actual"] = "esperando_aprobacion",
            ["datos"] = new Dict { ["reserva"] = codigo, ["monto"] = montoFinal, ["cliente"] = reserva.G("cliente") },
            ["hecho"] = new List<object?> { "leyó la reserva y la política v3", "aplicó la regla de aprobación" },
            ["estado"] = "pendiente_aprobacion", ["actualizado"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        almacen.Escribir(Path.Combine("tareas", $"{u.CorrelationId}.json"), tarea);
        t.Agregar(Paso("checkpoint", "Checkpoint · pausa", $"datos/tareas/{u.CorrelationId}.json: el proceso puede reiniciarse mientras espera", tarea, "ok"), Grupo);

        var aprobacion = new Dict
        {
            ["id"] = "apr-" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..4], ["tipo"] = "reembolso", ["correlation_id"] = u.CorrelationId,
            ["titulo"] = $"Reembolso de {Datos.Plata(montoFinal)} · reserva {codigo}",
            ["por_que"] = $"Supera el límite de {Datos.Plata(limite)} sin aprobación",
            ["datos"] = new List<object?>
            {
                $"Cliente: {reserva.Str("cliente")} · canceló con {reserva.I("aviso_horas")} h de aviso",
                $"Seña: {Datos.Plata(reserva.I("sena"))} · corresponde {(corresponde == reserva.I("sena") ? "reembolso total" : "el 50 %")} según la política v3",
                $"Mensaje: «{texto}»",
            },
            ["vence"] = DateTime.Now.AddHours(4).ToString("HH:mm"), ["si_vence"] = "se rechaza y se le avisa a la clienta",
            ["estado"] = "pendiente",
            ["registro"] = new List<Dict> { new() { ["quien"] = "asistente", ["accion"] = "pidió aprobación", ["hora"] = DateTime.Now.ToString("HH:mm:ss") } },
        };
        var lista = almacen.Lista("aprobaciones.json");
        lista.Add(aprobacion);
        almacen.Escribir("aprobaciones.json", lista);
        t.Agregar(Paso("humano", "Pendiente de aprobación", $"{aprobacion.Str("id")} · vence a las {aprobacion.Str("vence")}", aprobacion, "aviso"), Grupo);
        u.Extra["aprobacion"] = aprobacion;
        u.Decir("asistente", $"Recibí tu pedido de reembolso de {Datos.Plata(montoFinal)} por la reserva {codigo}. Como supera {Datos.Plata(limite)}, " +
                             "lo tiene que aprobar la encargada; te aviso apenas lo resuelva.");
        u.Resultado = "pendiente";
    }

    public Task Resolver(Turno u, Dict a, bool aprobar, string quien)
    {
        var t = u.T;
        const string grupo = "Reanudación";
        var cid = a.Str("correlation_id");
        var tarea = almacen.Objeto(Path.Combine("tareas", $"{cid}.json"));
        var datos = tarea.O("datos") ?? new Dict();
        var codigo = datos.Str("reserva");
        var monto = (long)datos.D("monto");
        u.Extra["aprobacion"] = a;

        if (a.Str("estado") != "pendiente")
        {
            // Doble clic o reintento: la aprobación ya está resuelta. Y aunque se volviera a pedir el pago, la clave lo evita.
            var previo = a.Ds("registro").LastOrDefault(r => r.Str("accion") is "aprobó" or "rechazó");
            t.Agregar(Paso("gate", "La aprobación ya estaba resuelta", $"{previo?.Str("quien")} {previo?.Str("accion")} a las {previo?.Str("hora")}: no se repite nada", a, "aviso"), grupo);
            if (a.Str("estado") == "aprobada")
            {
                var r = sistema.Reembolsar(codigo, monto, a.Str("id"));
                t.Agregar(Paso("tool", $"Pagos · reembolsar_sena({codigo}, {monto}, aprobacion_id={a.Str("id")})", "aunque se pida de nuevo, la clave de idempotencia evita el doble pago",
                    r, r.B("ya_existia") ? "idempotente" : "corta"), grupo);
            }
            a.Sumar("registro", new Dict { ["quien"] = quien, ["accion"] = "intentó resolver otra vez", ["hora"] = DateTime.Now.ToString("HH:mm:ss") });
            u.Resultado = "repetida";
            return Task.CompletedTask;
        }

        a["estado"] = aprobar ? "aprobada" : "rechazada";
        a.Sumar("registro", new Dict { ["quien"] = quien, ["accion"] = aprobar ? "aprobó" : "rechazó", ["hora"] = DateTime.Now.ToString("HH:mm:ss") });
        t.Agregar(Paso("humano", aprobar ? $"Aprobado por {quien}" : $"Rechazado por {quien}", "queda en el registro: quién y cuándo", a.G("registro"), aprobar ? "ok" : "aviso"), grupo);
        t.Agregar(Paso("checkpoint", "Carga el checkpoint", $"datos/tareas/{cid}.json · sigue desde «{tarea.Str("paso_actual")}»", tarea, "ok"), grupo);
        t.Agregar(Paso("saltado", "Saltea · lectura y regla de aprobación", "ya están en el checkpoint: no se repiten", tarea.G("hecho"), "saltado"), grupo);

        if (aprobar)
        {
            var r = sistema.Reembolsar(codigo, monto, a.Str("id"));
            t.Agregar(Paso("tool", $"Ejecutar · reembolsar_sena({codigo}, {monto}, aprobacion_id={a.Str("id")})", "con aprobación válida · idempotente por aprobacion_id", r, "ok"), grupo);
            u.Extra["reembolso"] = r;
            u.Decir("asistente", $"¡Buenas noticias! La encargada aprobó tu reembolso de {Datos.Plata(monto)} de la reserva {codigo}. Lo vas a ver en tu medio de pago en 48 h.");
            u.Resultado = "aprobada";
        }
        else
        {
            u.Decir("asistente", $"La encargada revisó tu pedido de reembolso de la reserva {codigo} y no lo aprobó. Te va a escribir para explicarte por qué.");
            u.Resultado = "rechazada";
        }
        tarea["estado"] = aprobar ? "completada" : "rechazada";
        tarea["actualizado"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        almacen.Escribir(Path.Combine("tareas", $"{cid}.json"), tarea);
        return Task.CompletedTask;
    }
}
