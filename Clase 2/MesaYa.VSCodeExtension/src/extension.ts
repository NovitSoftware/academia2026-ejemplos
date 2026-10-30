import * as os from "os";
import * as vscode from "vscode";
import type { ElicitResult } from "@modelcontextprotocol/sdk/types.js";
import { Agente } from "./agente";
import { ClienteMcp } from "./mcp";
import { Ollama, type ModeloLocal } from "./ollama";
import type { DesdeWebview, EstadoConexion, HaciaWebview, MensajeChat, Paso, PedidoElicitacion, UsoConversacion } from "./protocolo";

export function activate(context: vscode.ExtensionContext) {
  const log = vscode.window.createOutputChannel("MesaYa MCP", { log: true });
  const chat = new ChatMesaYa(context, log);
  context.subscriptions.push(
    log,
    vscode.window.registerWebviewViewProvider("mesaya.chat", chat, { webviewOptions: { retainContextWhenHidden: true } }),
    vscode.commands.registerCommand("mesaya.nuevaConversacion", () => chat.nuevaConversacion()),
    vscode.commands.registerCommand("mesaya.reconectar", () => chat.conectar()),
    vscode.commands.registerCommand("mesaya.abrirSecuencia", () => chat.abrirSecuencia()),
    vscode.commands.registerCommand("mesaya.verLog", () => log.show()),
    vscode.workspace.onDidChangeConfiguration(e => {
      if (!e.affectsConfiguration("mesaya")) return;
      // Mostrar u ocultar las tools no necesita reconectar.
      const reconectar = ["mcpUrl", "ollamaUrl", "modelo"].some(k => e.affectsConfiguration(`mesaya.${k}`));
      if (reconectar) void chat.conectar(); else chat.refrescar();
    }),
    { dispose: () => void chat.cerrar() }
  );
}

export function deactivate() { /* los recursos se liberan con context.subscriptions */ }

/** El panel lateral: un host MCP con chat. */
class ChatMesaYa implements vscode.WebviewViewProvider {
  private vista?: vscode.WebviewView;
  private mcp?: ClienteMcp;
  private agente?: Agente;
  private ollama!: Ollama;
  private modelos: ModeloLocal[] = [];
  private errorConexion?: string;
  private errorOllama?: string;
  private conectando = false;
  private readonly elicitaciones = new Map<string, (r: ElicitResult) => void>();
  private readonly historialVista: HaciaWebview[] = [];
  private ultimoUso?: UsoConversacion;

  constructor(private readonly context: vscode.ExtensionContext, private readonly log: vscode.LogOutputChannel) {}

  private get config() { return vscode.workspace.getConfiguration("mesaya"); }

  resolveWebviewView(view: vscode.WebviewView): void {
    this.vista = view;
    const media = vscode.Uri.joinPath(this.context.extensionUri, "media");
    const dist = vscode.Uri.joinPath(this.context.extensionUri, "dist");
    view.webview.options = { enableScripts: true, localResourceRoots: [media, dist] };
    view.webview.html = this.html(view.webview);
    view.webview.onDidReceiveMessage((m: DesdeWebview) => this.recibir(m));
  }

  private async recibir(m: DesdeWebview) {
    switch (m.tipo) {
      case "listo":
        // El webview se (re)creó: le reenviamos lo que ya pasó y nos conectamos si hace falta.
        for (const msg of this.historialVista) void this.vista?.webview.postMessage(msg);
        if (this.ultimoUso) void this.vista?.webview.postMessage({ tipo: "uso", uso: this.ultimoUso } satisfies HaciaWebview);
        if (!this.mcp?.conectado && !this.conectando) await this.conectar(); else this.publicarEstado();
        break;
      case "enviar":
        if (!this.mcp?.conectado) await this.conectar();
        if (!this.agente) return;
        this.agente.paralelo = this.config.get<boolean>("toolsEnParalelo", true);
        const turno = this.agente.enviar(m.texto);
        this.publicarEstado();
        await turno;
        this.publicarEstado();
        break;
      case "detener":
        this.agente?.detener();
        for (const [, resolver] of this.elicitaciones) resolver({ action: "cancel" });
        break;
      case "nuevaConversacion":
        await this.nuevaConversacion();
        break;
      case "elegirModelo":
        await this.config.update("modelo", m.nombre, vscode.ConfigurationTarget.Global);
        break;
      case "responderElicitacion": {
        const resolver = this.elicitaciones.get(m.id);
        resolver?.({ action: m.accion, content: m.accion === "accept" ? m.contenido as ElicitResult["content"] : undefined });
        break;
      }
      case "abrirEnEditor": {
        const doc = await vscode.workspace.openTextDocument({ content: m.contenido, language: m.lenguaje });
        await vscode.window.showTextDocument(doc, { preview: false });
        break;
      }
      case "guardar": {
        const base = vscode.workspace.workspaceFolders?.[0]?.uri ?? vscode.Uri.file(os.homedir());
        const destino = await vscode.window.showSaveDialog({ defaultUri: vscode.Uri.joinPath(base, m.nombre) });
        if (!destino) return;
        await vscode.workspace.fs.writeFile(destino, Buffer.from(m.contenido, "utf8"));
        const abrir = await vscode.window.showInformationMessage(`Guardado en ${destino.fsPath}`, "Abrir");
        if (abrir) await vscode.window.showTextDocument(destino);
        break;
      }
      case "abrirSecuencia":
        this.abrirSecuencia();
        break;
      case "reconectar":
        await this.conectar();
        break;
    }
  }

  /** Conecta (o reconecta) al servidor MCP y a Ollama según la configuración. */
  async conectar() {
    if (this.conectando) return;
    this.conectando = true;
    this.agente?.detener();
    await this.mcp?.cerrar();
    this.mcp = undefined;
    this.agente = undefined;
    this.errorConexion = undefined;
    this.publicarEstado();

    const mcpUrl = this.config.get<string>("mcpUrl", "http://localhost:5100/mcp");
    this.ollama = new Ollama(this.config.get<string>("ollamaUrl", "http://localhost:11434").replace(/\/$/, ""));
    try {
      this.modelos = await this.ollama.modelos();
      this.errorOllama = undefined;
    } catch (e) {
      this.modelos = [];
      this.errorOllama = `No se pudo consultar Ollama: ${(e as Error).message}`;
    }

    const mcp = new ClienteMcp(mcpUrl, {
      elicitar: (pedido, signal) => this.elicitar(pedido, signal),
      log: (nivel, texto) => this.log.info(`[servidor ${nivel}] ${texto}`)
    });
    try {
      await mcp.conectar();
      this.mcp = mcp;
      this.log.info(`Conectado a ${mcpUrl} · sesión ${mcp.sesion} · tools: ${mcp.tools.map(t => t.name).join(", ")}`);
      this.agente = new Agente(mcp, this.ollama, this.vistaAgente(), this.modeloElegido());
    } catch (e) {
      this.errorConexion = `No se pudo conectar al servidor MCP en ${mcpUrl}: ${(e as Error).message}`;
      this.log.error(this.errorConexion);
    } finally {
      this.conectando = false;
      this.publicarEstado();
    }
  }

  async nuevaConversacion() {
    this.historialVista.length = 0;
    this.ultimoUso = undefined;
    this.enviarVista({ tipo: "limpiar" }, false);
    // Una sesión MCP nueva por conversación: la traza del servidor queda separada.
    await this.conectar();
  }

  refrescar() { this.publicarEstado(); }

  abrirSecuencia() {
    const base = this.config.get<string>("mcpUrl", "http://localhost:5100/mcp").replace(/\/mcp\/?$/, "");
    void vscode.env.openExternal(vscode.Uri.parse(`${base}/secuencia`));
  }

  async cerrar() {
    this.agente?.detener();
    await this.mcp?.cerrar();
  }

  private modeloElegido(): ModeloLocal {
    const nombre = this.config.get<string>("modelo", "qwen3.5:9b");
    return this.modelos.find(m => m.nombre === nombre)
      ?? this.modelos.find(m => m.tools)
      ?? { nombre, tools: true, thinking: false };
  }

  /** elicitation/create: el servidor le pregunta algo al usuario. Lo mostramos en el chat y esperamos. */
  private elicitar(pedido: PedidoElicitacion, signal: AbortSignal): Promise<ElicitResult> {
    this.log.info(`elicitation/create · ${pedido.mensaje}`);
    return new Promise<ElicitResult>(resolver => {
      const terminar = (r: ElicitResult) => {
        if (!this.elicitaciones.delete(pedido.id)) return;
        this.enviarVista({ tipo: "elicitacionCerrada", id: pedido.id, accion: r.action });
        this.log.info(`elicitation → ${r.action}`);
        resolver(r);
      };
      this.elicitaciones.set(pedido.id, terminar);
      signal.addEventListener("abort", () => terminar({ action: "cancel" }));
      this.enviarVista({ tipo: "elicitacion", pedido });
      // Si el panel está oculto, avisamos para que el usuario no se quede esperando sin saber.
      if (!this.vista?.visible)
        void vscode.window.showInformationMessage(`MesaYa pide confirmación: ${pedido.mensaje}`, "Ver")
          .then(v => { if (v) void vscode.commands.executeCommand("mesaya.chat.focus"); });
    });
  }

  private vistaAgente() {
    return {
      mensaje: (m: MensajeChat) => this.enviarVista({ tipo: "mensaje", mensaje: m }),
      uso: (u: UsoConversacion) => {
        this.ultimoUso = u;
        void this.vista?.webview.postMessage({ tipo: "uso", uso: u } satisfies HaciaWebview);
      },
      paso: (p: Paso) => {
        if (p.tipo === "tool" && p.estado !== "encurso")
          this.log.info(`tools/call ${p.titulo} → ${p.estado === "ok" ? "ok" : `error ${p.codigoError ?? ""}`} (${p.duracionMs} ms)`);
        this.enviarVista({ tipo: "paso", paso: p });
      }
    };
  }

  private publicarEstado() {
    const estado: EstadoConexion = {
      conectado: !!this.mcp?.conectado,
      conectando: this.conectando,
      mcpUrl: this.config.get<string>("mcpUrl", "http://localhost:5100/mcp"),
      servidor: this.mcp?.servidor ? `${this.mcp.servidor.title ?? this.mcp.servidor.name} ${this.mcp.servidor.version}` : undefined,
      sesion: this.mcp?.sesion,
      protocolo: this.mcp?.protocolo,
      tools: this.mcp?.tools.map(t => ({ nombre: t.name, descripcion: t.description })) ?? [],
      error: this.errorConexion,
      modelo: this.agente?.modelo.nombre ?? this.config.get<string>("modelo", "qwen3.5:9b"),
      modelos: this.modelos.map(m => ({ nombre: m.nombre, tools: m.tools, tamano: m.tamano })),
      errorOllama: this.errorOllama,
      ocupado: !!this.agente?.ocupado,
      mostrarTools: this.config.get<boolean>("mostrarTools", false)
    };
    void this.vista?.webview.postMessage({ tipo: "estado", estado } satisfies HaciaWebview);
  }

  /** Envía al webview y guarda el mensaje para poder reconstruir el chat si el panel se recrea. */
  private enviarVista(msg: HaciaWebview, guardar = true) {
    if (guardar) {
      if (msg.tipo === "paso") {
        const i = this.historialVista.findIndex(h => h.tipo === "paso" && h.paso.id === msg.paso.id);
        if (i >= 0) this.historialVista[i] = msg; else this.historialVista.push(msg);
      } else this.historialVista.push(msg);
    }
    void this.vista?.webview.postMessage(msg);
  }

  private html(webview: vscode.Webview): string {
    const nonce = crypto.randomUUID().replace(/-/g, "");
    const css = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "media", "chat.css"));
    const js = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "dist", "webview.js"));
    return `<!doctype html>
<html lang="es">
<head>
  <meta charset="utf-8" />
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}'; img-src ${webview.cspSource} data:;" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <link rel="stylesheet" href="${css}" />
  <title>MesaYa</title>
</head>
<body>
  <div id="app"></div>
  <script nonce="${nonce}" src="${js}"></script>
</body>
</html>`;
  }
}
