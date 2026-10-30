import type { ClienteMcp } from "./mcp";
import type { MensajeOllama, ModeloLocal, Ollama } from "./ollama";
import type { MensajeChat, Paso, UsoConversacion } from "./protocolo";

const MAX_ITERACIONES = 8;

/** Lo que el agente necesita de la interfaz: mostrar mensajes y actualizar tarjetas. */
export interface Vista {
  mensaje(m: MensajeChat): void;
  paso(p: Paso): void;
  uso(u: UsoConversacion): void;
}

/**
 * Host MCP: el loop agéntico. No conoce ninguna tool de antemano. Toma las que publica el servidor
 * (tools/list), se las da al modelo, ejecuta por MCP las que el modelo pida y le devuelve los resultados.
 */
export class Agente {
  private historial: MensajeOllama[] = [];
  private iteracion = 0;
  private uso: UsoConversacion = { entrada: 0, salida: 0, llamadas: 0 };
  private generacionMs = 0;
  private abortador?: AbortController;

  constructor(
    private readonly mcp: ClienteMcp,
    private readonly ollama: Ollama,
    private readonly vista: Vista,
    public modelo: ModeloLocal,
    public paralelo = true
  ) {}

  get ocupado() { return this.abortador !== undefined; }

  reiniciar() {
    this.historial = []; this.iteracion = 0;
    this.uso = { entrada: 0, salida: 0, llamadas: 0 }; this.generacionMs = 0;
    this.vista.uso({ ...this.uso });
  }

  detener() { this.abortador?.abort(); }

  async enviar(texto: string): Promise<void> {
    if (this.ocupado) return;
    this.abortador = new AbortController();
    const signal = this.abortador.signal;
    this.vista.mensaje({ id: id(), rol: "user", texto });
    this.historial.push({ role: "user", content: texto });

    // Las tools salen del servidor MCP: si mañana agrega una, el modelo la ve sin cambiar la extensión.
    const tools = this.mcp.tools.map(t => ({
      type: "function",
      function: { name: t.name, description: t.description ?? t.title ?? "", parameters: t.inputSchema }
    }));

    try {
      for (let i = 0; i < MAX_ITERACIONES; i++) {
        this.iteracion++;
        const mensajes: MensajeOllama[] = [{ role: "system", content: this.sistema() }, ...this.historial];
        const pasoLlm = this.nuevoPaso("llm", this.modelo.nombre,
          `${mensajes.length} mensajes · ${tools.length} tools del servidor MCP`, JSON.stringify({ model: this.modelo.nombre, messages: mensajes, tools }, null, 2));

        let r;
        try { r = await this.ollama.chat(this.modelo, mensajes, tools, signal); }
        catch (e) { this.terminar(pasoLlm, false, String((e as Error).message)); throw e; }

        pasoLlm.sub = (r.llamadas.length ? `tool_calls: ${r.llamadas.map(l => l.nombre).join(", ")}` : "respuesta final")
          + (r.metricas ? ` · ${r.metricas}` : "");
        this.terminar(pasoLlm, true, JSON.stringify(r.mensaje, null, 2));
        if (r.uso) this.sumarUso(r.uso.entrada, r.uso.salida, r.uso.generacionMs);

        this.historial.push(r.mensaje);
        if (r.texto) this.vista.mensaje({ id: id(), rol: "assistant", texto: r.texto });
        if (r.llamadas.length === 0) return;

        const ejecutar = (l: { nombre: string; args: Record<string, unknown> }) => this.ejecutarTool(l.nombre, l.args, signal);
        const resultados = this.paralelo && r.llamadas.length > 1
          ? await Promise.all(r.llamadas.map(ejecutar))
          : await r.llamadas.reduce<Promise<string[]>>(async (acc, l) => [...await acc, await ejecutar(l)], Promise.resolve([]));
        r.llamadas.forEach((l, k) => this.historial.push({ role: "tool", tool_name: l.nombre, content: resultados[k] }));
      }
      this.vista.mensaje({ id: id(), rol: "error", texto: `Se alcanzó el límite de ${MAX_ITERACIONES} iteraciones del loop agéntico.` });
    } catch (e) {
      const texto = signal.aborted ? "Turno detenido." : `No se pudo completar el turno: ${(e as Error).message}`;
      this.vista.mensaje({ id: id(), rol: "error", texto });
    } finally {
      this.abortador = undefined;
    }
  }

  private async ejecutarTool(nombre: string, args: Record<string, unknown>, signal: AbortSignal): Promise<string> {
    const paso = this.nuevoPaso("tool", nombre, "tools/call por MCP", JSON.stringify(args, null, 2));
    try {
      const r = await this.mcp.llamar(nombre, args, msg => { paso.progreso = msg; this.vista.paso({ ...paso }); }, signal);
      paso.progreso = undefined;
      if (r.esError) paso.codigoError = codigoDe(r.texto);
      paso.sub = r.esError ? `isError · ${paso.codigoError ?? "error"}` : "ok";
      this.terminar(paso, !r.esError, bonito(r.texto));
      return r.texto;
    } catch (e) {
      const texto = JSON.stringify({ codigo: "MCP_ERROR", mensaje: (e as Error).message });
      paso.codigoError = "MCP_ERROR";
      paso.sub = "error de protocolo";
      this.terminar(paso, false, texto);
      if (signal.aborted) throw e;
      return texto;
    }
  }

  private sumarUso(entrada: number, salida: number, generacionMs: number) {
    this.generacionMs += generacionMs;
    this.uso = {
      entrada: this.uso.entrada + entrada,
      salida: this.uso.salida + salida,
      llamadas: this.uso.llamadas + 1,
      ultimoTps: generacionMs > 0 ? salida / (generacionMs / 1000) : this.uso.ultimoTps,
      promedioTps: this.generacionMs > 0 ? (this.uso.salida + salida) / (this.generacionMs / 1000) : undefined
    };
    this.vista.uso({ ...this.uso });
  }

  private sistema(): string {
    const hoy = new Date(Date.now() - 3 * 3600_000); // -03:00
    const dia = (d: Date) => d.toLocaleDateString("es-AR", { weekday: "long", timeZone: "UTC" });
    const iso = (d: Date) => d.toISOString().slice(0, 10);
    const calendario = Array.from({ length: 8 }, (_, i) => new Date(hoy.getTime() + i * 86_400_000))
      .map((d, i) => `- ${i === 0 ? "hoy" : i === 1 ? "mañana" : "el"} ${dia(d)} → ${iso(d)}`).join("\n");
    return [
      `Sos un asistente que trabaja con las tools de un servidor MCP. La zona horaria es -03:00.`,
      `Calendario (usalo para "hoy", "mañana", "el viernes", etc., sin preguntar):\n${calendario}`,
      "No inventes datos: si necesitás información, llamá una tool. Si varias consultas son independientes, pedilas juntas.",
      "Si el usuario pide una acción (reservar, cancelar) y ya tenés todos los datos, llamá directamente a esa tool: el servidor valida, " +
        "le pide la confirmación al usuario y te devuelve el resultado. No consultes antes ni pidas confirmación vos.",
      "Cuando la acción se pudo hacer, decilo en una línea con los datos que devolvió la tool (incluí identificadores como el reserva_id).",
      "Cuando NO se pudo, decilo claramente y explicá por qué usando el código y el mensaje del error; si el error trae alternativas, ofrecelas.",
      "Si te piden un menú o un listado, mostralo completo en una tabla Markdown.",
      "Respondé breve, en español rioplatense.",
      "",
      "Instrucciones del servidor MCP:",
      this.mcp.instrucciones ?? "(sin instrucciones)"
    ].join("\n");
  }

  private nuevoPaso(tipo: Paso["tipo"], titulo: string, sub: string, entrada: string): Paso {
    const p: Paso = { id: id(), tipo, iteracion: this.iteracion, titulo, sub, entrada, estado: "encurso", inicio: Date.now() };
    this.vista.paso({ ...p });
    return p;
  }

  private terminar(p: Paso, ok: boolean, salida: string) {
    p.estado = ok ? "ok" : "error";
    p.salida = salida;
    p.duracionMs = Date.now() - p.inicio;
    this.vista.paso({ ...p });
  }
}

function id() { return crypto.randomUUID(); }

function bonito(texto: string) {
  try { return JSON.stringify(JSON.parse(texto), null, 2); } catch { return texto; }
}

function codigoDe(texto: string): string | undefined {
  try { return (JSON.parse(texto) as { codigo?: string }).codigo; } catch { return undefined; }
}
