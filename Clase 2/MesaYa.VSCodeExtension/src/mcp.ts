import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import {
  ElicitRequestSchema,
  LoggingMessageNotificationSchema,
  type ElicitResult,
  type Tool
} from "@modelcontextprotocol/sdk/types.js";
import type { PedidoElicitacion } from "./protocolo";

/** Lo que el cliente necesita del host para atender pedidos del servidor. */
export interface ManejadoresHost {
  /** El servidor pide datos o una confirmación al usuario (elicitation/create). */
  elicitar(pedido: PedidoElicitacion, signal: AbortSignal): Promise<ElicitResult>;
  /** El servidor manda un log (notifications/message). */
  log(nivel: string, texto: string): void;
}

export interface ResultadoTool {
  texto: string;
  esError: boolean;
}

/**
 * Cliente MCP genérico: no sabe nada de MesaYa. Se conecta a cualquier servidor MCP por Streamable HTTP,
 * descubre sus tools y las ejecuta. Declara la capacidad "elicitation" para que el servidor pueda
 * pedirle confirmaciones al usuario.
 */
export class ClienteMcp {
  private cliente?: Client;
  private transporte?: StreamableHTTPClientTransport;
  tools: Tool[] = [];

  constructor(private readonly url: string, private readonly host: ManejadoresHost) {}

  get conectado() { return this.cliente !== undefined; }
  get sesion() { return this.transporte?.sessionId; }
  get servidor() { return this.cliente?.getServerVersion(); }
  get instrucciones() { return this.cliente?.getInstructions(); }
  get protocolo() { return this.transporte?.protocolVersion; }

  async conectar(): Promise<void> {
    const cliente = new Client(
      { name: "mesaya-vscode", title: "VS Code · Chat MesaYa", version: "0.1.0" },
      { capabilities: { elicitation: {} } }
    );

    cliente.setRequestHandler(ElicitRequestSchema, async (req, extra) => {
      const p = req.params as { message: string; requestedSchema?: PedidoElicitacion["schema"] };
      return this.host.elicitar(
        { id: crypto.randomUUID(), mensaje: p.message, schema: p.requestedSchema },
        extra.signal
      );
    });
    cliente.setNotificationHandler(LoggingMessageNotificationSchema, n => {
      const d = n.params.data;
      this.host.log(n.params.level, typeof d === "string" ? d : JSON.stringify(d));
    });

    const transporte = new StreamableHTTPClientTransport(new URL(this.url));
    await cliente.connect(transporte);
    this.cliente = cliente;
    this.transporte = transporte;
    this.tools = (await cliente.listTools()).tools;
    cliente.onclose = () => { this.cliente = undefined; };
  }

  /** tools/call con avisos de progreso. El timeout se renueva con cada progreso (las confirmaciones pueden tardar). */
  async llamar(nombre: string, args: Record<string, unknown>, progreso: (msg: string) => void, signal: AbortSignal): Promise<ResultadoTool> {
    if (!this.cliente) throw new Error("No hay conexión con el servidor MCP.");
    const r = await this.cliente.callTool({ name: nombre, arguments: args }, undefined, {
      signal,
      timeout: 10 * 60_000,
      resetTimeoutOnProgress: true,
      onprogress: p => progreso(p.message ?? `paso ${p.progress}`)
    });
    const bloques = (r.content as { type: string; text?: string }[] | undefined) ?? [];
    const texto = bloques.filter(b => b.type === "text").map(b => b.text ?? "").join("\n")
      || (r.structuredContent ? JSON.stringify(r.structuredContent) : "");
    return { texto, esError: r.isError === true };
  }

  async cerrar(): Promise<void> {
    const c = this.cliente;
    this.cliente = undefined;
    try { await this.transporte?.terminateSession(); } catch { /* el servidor pudo haberse ido */ }
    await c?.close().catch(() => undefined);
  }
}
