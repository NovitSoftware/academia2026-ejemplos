// Adaptador mínimo de la API de chat de Ollama con tool calling nativo.

export interface ModeloLocal { nombre: string; tools: boolean; thinking: boolean; tamano?: string }

export interface MensajeOllama {
  role: "system" | "user" | "assistant" | "tool";
  content: string;
  tool_calls?: { function: { name: string; arguments: Record<string, unknown> | string } }[];
  tool_name?: string;
}

export interface LlamadaTool { nombre: string; args: Record<string, unknown> }

/** Tokens y tiempos que informa Ollama en cada respuesta. */
export interface UsoTokens {
  entrada: number;
  salida: number;
  /** Tiempo generando la salida (eval_duration), en ms. */
  generacionMs: number;
}

export interface RespuestaOllama {
  texto: string;
  llamadas: LlamadaTool[];
  mensaje: MensajeOllama;
  metricas?: string;
  uso?: UsoTokens;
}

export class Ollama {
  constructor(private readonly url: string) {}

  async modelos(): Promise<ModeloLocal[]> {
    const r = await fetch(`${this.url}/api/tags`);
    if (!r.ok) throw new Error(`Ollama respondió ${r.status}`);
    const datos = await r.json() as { models: { name: string; capabilities?: string[]; details?: { parameter_size?: string } }[] };
    return datos.models
      .map(m => ({
        nombre: m.name,
        tools: m.capabilities?.includes("tools") ?? false,
        thinking: m.capabilities?.includes("thinking") ?? false,
        tamano: m.details?.parameter_size
      }))
      .sort((a, b) => Number(b.tools) - Number(a.tools) || a.nombre.localeCompare(b.nombre));
  }

  async chat(modelo: ModeloLocal, mensajes: MensajeOllama[], tools: unknown[], signal: AbortSignal): Promise<RespuestaOllama> {
    const pedido: Record<string, unknown> = {
      model: modelo.nombre, stream: false, keep_alive: "15m",
      options: { temperature: 0.2 }, messages: mensajes, tools
    };
    // Ollama rechaza "think" en modelos sin razonamiento; en los que lo tienen lo apagamos por velocidad.
    if (modelo.thinking) pedido.think = false;

    const r = await fetch(`${this.url}/api/chat`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(pedido), signal
    });
    const texto = await r.text();
    if (!r.ok) throw new Error(`Ollama respondió ${r.status}: ${texto}`);
    const raiz = JSON.parse(texto) as {
      message: MensajeOllama; prompt_eval_count?: number; eval_count?: number; eval_duration?: number; total_duration?: number;
    };
    const m = raiz.message;
    const llamadas = (m.tool_calls ?? []).map(c => ({
      nombre: c.function.name,
      args: typeof c.function.arguments === "string" ? JSON.parse(c.function.arguments || "{}") : (c.function.arguments ?? {})
    }));
    const metricas = raiz.eval_count !== undefined
      ? `${raiz.prompt_eval_count} tokens entrada · ${raiz.eval_count} salida · ${Math.round((raiz.total_duration ?? 0) / 1e6)} ms`
      : undefined;
    const mensaje: MensajeOllama = { role: "assistant", content: m.content ?? "" };
    if (m.tool_calls?.length) mensaje.tool_calls = m.tool_calls;
    const uso = raiz.eval_count !== undefined
      ? { entrada: raiz.prompt_eval_count ?? 0, salida: raiz.eval_count, generacionMs: (raiz.eval_duration ?? 0) / 1e6 }
      : undefined;
    return { texto: (m.content ?? "").trim(), llamadas, mensaje, metricas, uso };
  }
}
