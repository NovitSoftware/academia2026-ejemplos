using MesaYa.Comun;

namespace MesaYa.Durabilidad;

/// <summary>
/// Lo que vive fuera del proceso, en datos/ (no va al repositorio):
///   tareas/req-….json  el estado de ejecución de cada tarea (los checkpoints)
///   conversacion.json  el estado conversacional: los mensajes y la tarea activa
///   cola.json          los pedidos recibidos que todavía no terminaron
///   bandeja.json       los casos derivados a una persona
///   log.json           el log, con el correlation ID en cada línea
///   turnos.json        el historial del chat de la demo
/// Cada archivo se escribe entero y se renombra: si el proceso se cae a mitad, queda el anterior.
/// </summary>
public sealed class Almacen
{
    public string Carpeta { get; }
    string Tareas => Path.Combine(Carpeta, "tareas");

    public Almacen(string carpeta)
    {
        Carpeta = carpeta;
        Directory.CreateDirectory(Tareas);
    }

    // ---------- archivos ----------
    object? Leer(string nombre)
    {
        var f = Path.Combine(Carpeta, nombre);
        return File.Exists(f) ? J.Leer(File.ReadAllText(f)) : null;
    }

    void Escribir(string nombre, object o)
    {
        var f = Path.Combine(Carpeta, nombre);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f + ".tmp", J.Serializar(o));
        File.Move(f + ".tmp", f, overwrite: true);
    }

    List<Dict> Lista(string nombre) => (Leer(nombre) as List<object?> ?? []).OfType<Dict>().ToList();

    // ---------- estado de ejecución ----------
    public void GuardarTarea(Dict tarea)
    {
        tarea["actualizado"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        Escribir(Path.Combine("tareas", $"{tarea.Str("correlation_id")}.json"), tarea);
    }

    public Dict? CargarTarea(string? id) => id == null ? null : Leer(Path.Combine("tareas", $"{id}.json")) as Dict;

    public List<Dict> TodasLasTareas() =>
        Directory.GetFiles(Tareas, "*.json").Select(f => J.LeerObjeto(File.ReadAllText(f)))
            .OrderBy(t => t.Str("creada")).ToList();

    // ---------- estado conversacional ----------
    public Dict Conversacion() => Leer("conversacion.json") as Dict ?? new Dict { ["mensajes"] = new List<Dict>(), ["tarea_activa"] = null };
    public void GuardarConversacion(Dict c) => Escribir("conversacion.json", c);

    // ---------- cola de entrada ----------
    public List<Dict> Cola() => Lista("cola.json");
    public void GuardarCola(List<Dict> c) => Escribir("cola.json", c);

    // ---------- bandeja de atención ----------
    public List<Dict> Bandeja() => Lista("bandeja.json");
    public void GuardarBandeja(List<Dict> b) => Escribir("bandeja.json", b);

    // ---------- log ----------
    public List<Dict> LeerLog() => Lista("log.json");

    public void Log(string correlationId, string componente, string texto)
    {
        lock (this)
        {
            var l = LeerLog();
            l.Add(new Dict { ["hora"] = DateTime.Now.ToString("HH:mm:ss.fff"), ["correlation_id"] = correlationId, ["componente"] = componente, ["texto"] = texto });
            Escribir("log.json", l);
        }
    }

    // ---------- historial del chat ----------
    public List<Dict> Turnos() => Lista("turnos.json");
    public void GuardarTurnos(List<Dict> t) => Escribir("turnos.json", t);

    /// <summary>Borra todo: tareas, conversación, cola, bandeja, log y chat.</summary>
    public void Reiniciar()
    {
        foreach (var f in Directory.GetFiles(Tareas)) File.Delete(f);
        foreach (var n in new[] { "conversacion.json", "cola.json", "bandeja.json", "log.json", "turnos.json" })
        {
            var f = Path.Combine(Carpeta, n);
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
