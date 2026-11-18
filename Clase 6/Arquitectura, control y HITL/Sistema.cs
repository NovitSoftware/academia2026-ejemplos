using MesaYa.Comun;

namespace MesaYa.Hitl;

/// <summary>
/// Lo que vive en datos/ (no va al repositorio). Cada archivo se escribe entero y se renombra.
///   sistema.json        reservas, reembolsos y facturas cargadas (el "sistema de destino")
///   aprobaciones.json   los pedidos de aprobación con su registro
///   tareas/req-….json   el checkpoint de un reembolso que espera a un humano
///   conversacion.json   el historial que ve el agente y la confirmación pendiente
///   turnos.json         el historial del chat de la demo
/// </summary>
public sealed class Almacen(string carpeta)
{
    public string Carpeta { get; } = carpeta;

    public object? Leer(string nombre)
    {
        var f = Path.Combine(Carpeta, nombre);
        return File.Exists(f) ? J.Leer(File.ReadAllText(f)) : null;
    }

    public void Escribir(string nombre, object o)
    {
        var f = Path.Combine(Carpeta, nombre);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f + ".tmp", J.Serializar(o));
        File.Move(f + ".tmp", f, overwrite: true);
    }

    public List<Dict> Lista(string nombre) => (Leer(nombre) as List<object?> ?? []).OfType<Dict>().ToList();
    public Dict Objeto(string nombre) => Leer(nombre) as Dict ?? new Dict();

    public void Reiniciar()
    {
        if (Directory.Exists(Carpeta)) Directory.Delete(Carpeta, recursive: true);
        Directory.CreateDirectory(Carpeta);
    }
}

/// <summary>
/// El sistema de destino: reservas, pagos y compras. Valida por su cuenta (slide 4.2: "validar también en el
/// sistema de destino") y los reembolsos son idempotentes por clave: aprobar dos veces no paga dos veces.
/// </summary>
public sealed class Sistema(Almacen almacen)
{
    Dict Estado()
    {
        var e = almacen.Objeto("sistema.json");
        if (!e.ContainsKey("reservas"))
            e = new Dict { ["reservas"] = Datos.ReservasIniciales(), ["reembolsos"] = new List<Dict>(), ["facturas"] = new List<Dict>() };
        return e;
    }

    public Dict Foto() => Estado();

    public Dict? Reserva(string? codigo) => Estado().Ds("reservas").FirstOrDefault(r => r.Str("codigo") == codigo);

    public List<Dict> Buscar(string nombre) =>
        Estado().Ds("reservas").Where(r => Texto.Plano(r.Str("cliente")).Contains(Texto.Plano(nombre.Trim())) && r.Str("estado") == "confirmada").ToList();

    public Dict Crear(string sucursal, string dia, string hora, int personas)
    {
        var e = Estado();
        var reservas = e.Ds("reservas");
        var r = new Dict
        {
            ["codigo"] = $"R-{1060 + reservas.Count}", ["cliente"] = Datos.Cliente, ["sucursal"] = sucursal, ["dia"] = dia, ["hora"] = hora,
            ["personas"] = (long)personas, ["sena"] = 0L, ["aviso_horas"] = 0L, ["estado"] = "confirmada",
        };
        reservas.Add(r);
        e["reservas"] = reservas;
        almacen.Escribir("sistema.json", e);
        return r;
    }

    public Dict Cancelar(string codigo)
    {
        var e = Estado();
        var r = e.Ds("reservas").FirstOrDefault(x => x.Str("codigo") == codigo);
        if (r == null) return new Dict { ["error"] = $"no existe la reserva {codigo}" };
        r["estado"] = "cancelada";
        almacen.Escribir("sistema.json", e);
        return new Dict { ["codigo"] = codigo, ["estado"] = "cancelada" };
    }

    /// <summary>Paga un reembolso. La clave de idempotencia es el ID de la aprobación (o el correlation ID si no hizo falta aprobar).</summary>
    public Dict Reembolsar(string reserva, long monto, string clave)
    {
        var e = Estado();
        var lista = e.Ds("reembolsos");
        if (lista.FirstOrDefault(x => x.Str("clave") == clave) is { } previo)
            return previo.Con(new Dict { ["ya_existia"] = true });
        var r = new Dict
        {
            ["id"] = $"P-{801 + lista.Count}", ["reserva"] = reserva, ["monto"] = monto, ["clave"] = clave,
            ["hora"] = DateTime.Now.ToString("HH:mm:ss"), ["ya_existia"] = false,
        };
        lista.Add(r);
        e["reembolsos"] = lista;
        almacen.Escribir("sistema.json", e);
        return r;
    }

    public Dict CargarFactura(Dict factura, bool validada)
    {
        var e = Estado();
        var lista = e.Ds("facturas");
        var f = factura.Con(new Dict { ["id"] = $"F-{301 + lista.Count}", ["validada"] = validada, ["hora"] = DateTime.Now.ToString("HH:mm:ss") });
        lista.Add(f);
        e["facturas"] = lista;
        almacen.Escribir("sistema.json", e);
        return f;
    }
}
