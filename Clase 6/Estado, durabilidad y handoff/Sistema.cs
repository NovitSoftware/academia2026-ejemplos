using MesaYa.Comun;

namespace MesaYa.Durabilidad;

/// <summary>
/// El sistema de reservas de MesaYa, simulado. Tiene estado propio (mesas bloqueadas, cobros y reservas),
/// guardado en datos/sistema.json, y registra cada pedido con su X-Correlation-ID.
///
/// Con clave de idempotencia, un pedido repetido devuelve el resultado original en lugar de repetir la acción.
/// Las fallas se inyectan desde la demo: un timeout (la acción se hace pero la respuesta no llega) o un código HTTP.
/// </summary>
public sealed class Sistema
{
    public List<Dict> Mesas { get; private set; } = [];
    public List<Dict> Cobros { get; private set; } = [];
    public List<Dict> Reservas { get; private set; } = [];
    public List<Dict> Pedidos { get; private set; } = [];
    readonly string archivo;

    public Sistema(string carpeta)
    {
        archivo = Path.Combine(carpeta, "sistema.json");
        if (File.Exists(archivo))
        {
            var d = J.LeerObjeto(File.ReadAllText(archivo));
            Mesas = d.Ds("mesas"); Cobros = d.Ds("cobros"); Reservas = d.Ds("reservas"); Pedidos = d.Ds("pedidos");
        }
    }

    public void Reiniciar()
    {
        Mesas = []; Cobros = []; Reservas = []; Pedidos = [];
        Guardar();
    }

    void Guardar()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivo)!);
        File.WriteAllText(archivo + ".tmp", J.Serializar(Foto()));
        File.Move(archivo + ".tmp", archivo, overwrite: true);
    }

    public Dict Foto() => new() { ["mesas"] = Mesas, ["cobros"] = Cobros, ["reservas"] = Reservas, ["pedidos"] = Pedidos };

    /// <summary>
    /// Un pedido a la API. <paramref name="falla"/> es "timeout", "503", "429", "400" o null.
    /// Devuelve {codigo, cuerpo, ya_existia}. Con "timeout" la acción SÍ se hace: lo que se pierde es la respuesta.
    /// </summary>
    public Dict Llamar(string operacion, Dict datos, string correlationId, string? clave, string? falla)
    {
        lock (this)
        {
            var pedido = new Dict
            {
                ["n"] = (long)Pedidos.Count + 1, ["operacion"] = operacion, ["correlation_id"] = correlationId,
                ["clave"] = clave, ["hora"] = DateTime.Now.ToString("HH:mm:ss"),
            };
            Pedidos.Add(pedido);
            Dict r;
            if (falla is "503" or "429" or "400")
            {
                var texto = falla switch { "503" => "servicio no disponible", "429" => "demasiados pedidos", _ => "pedido mal armado" };
                r = new Dict { ["codigo"] = falla, ["cuerpo"] = new Dict { ["error"] = texto }, ["ya_existia"] = false };
            }
            else
            {
                var previo = clave == null ? null : Buscar(operacion, clave);
                var cuerpo = previo ?? Ejecutar(operacion, datos, correlationId, clave);
                r = new Dict { ["codigo"] = previo != null ? "200" : "201", ["cuerpo"] = cuerpo, ["ya_existia"] = previo != null };
                if (falla == "timeout")
                    r = new Dict { ["codigo"] = "timeout", ["cuerpo"] = null, ["ya_existia"] = false, ["hecho_igual"] = cuerpo };
            }
            pedido["resultado"] = r.Str("codigo") switch
            {
                "201" => $"201 · {Resumen(r.O("cuerpo"))}",
                "200" => $"200 · ya existía: {Resumen(r.O("cuerpo"))}",
                "timeout" => $"se hizo ({Resumen(r.O("hecho_igual"))}) pero la respuesta no llegó",
                var c => $"{c} · {r.O("cuerpo")?.Str("error")}",
            };
            pedido["ok"] = r.Str("codigo") is "200" or "201";
            Guardar();
            return r;
        }
    }

    static string Resumen(Dict? d) => d == null ? "" : d.Str("codigo", d.Str("id"));

    Dict? Buscar(string operacion, string clave)
    {
        var lista = operacion switch { "bloquear_mesa" => Mesas, "cobrar_sena" => Cobros, "crear_reserva" => Reservas, _ => null };
        // Las compensaciones también son idempotentes: liberar dos veces la misma mesa no hace nada.
        if (operacion is "liberar_mesa" or "reembolsar_sena")
            return Pedidos.Any(p => p.Str("clave") == clave && p.B("ok")) ? new Dict { ["id"] = "ya hecho" } : null;
        return lista?.FirstOrDefault(x => x.Str("clave") == clave);
    }

    Dict Ejecutar(string operacion, Dict datos, string correlationId, string? clave)
    {
        var comun = new Dict
        {
            ["cliente"] = datos.Str("nombre", Datos.Cliente), ["sucursal"] = datos.G("sucursal"), ["fecha"] = datos.G("fecha"),
            ["hora"] = datos.G("hora"), ["correlation_id"] = correlationId, ["clave"] = clave,
        };
        switch (operacion)
        {
            case "bloquear_mesa":
            {
                var m = comun.Con(new Dict { ["id"] = $"M-{7 + Mesas.Count:00}", ["personas"] = datos.G("personas"), ["estado"] = "bloqueada" });
                Mesas.Add(m);
                return m;
            }
            case "cobrar_sena":
            {
                var c = comun.Con(new Dict { ["id"] = $"C-{501 + Cobros.Count}", ["monto"] = datos.G("monto"), ["estado"] = "cobrado" });
                Cobros.Add(c);
                return c;
            }
            case "crear_reserva":
            {
                var r = comun.Con(new Dict
                {
                    ["codigo"] = $"R-{1043 + Reservas.Count}", ["personas"] = datos.G("personas"), ["mesa"] = datos.G("mesa"),
                    ["cobro"] = datos.G("cobro"), ["estado"] = "confirmada",
                });
                Reservas.Add(r);
                return r;
            }
            case "liberar_mesa":
            {
                var m = Mesas.FirstOrDefault(x => x.Str("id") == datos.Str("mesa"));
                if (m != null) m["estado"] = "liberada";
                return new Dict { ["id"] = datos.Str("mesa"), ["estado"] = "liberada" };
            }
            case "reembolsar_sena":
            {
                var c = Cobros.FirstOrDefault(x => x.Str("id") == datos.Str("cobro"));
                if (c != null) c["estado"] = "reembolsado";
                return new Dict { ["id"] = datos.Str("cobro"), ["estado"] = "reembolsado" };
            }
            default:
                throw new ErrorHttp(400, $"Operación desconocida: {operacion}");
        }
    }
}
