// Interfaz del chat. Corre dentro del webview de VS Code y habla con la extensión por postMessage.
import { Marked } from "marked";
import type { CampoElicitacion, DesdeWebview, EstadoConexion, HaciaWebview, MensajeChat, Paso, PedidoElicitacion, UsoConversacion } from "../protocolo";

declare function acquireVsCodeApi(): { postMessage(m: DesdeWebview): void };
const vscode = acquireVsCodeApi();
const enviar = (m: DesdeWebview) => vscode.postMessage(m);

// Markdown sin HTML crudo: lo que escribe el modelo nunca se inyecta como HTML.
const md = new Marked({ gfm: true, breaks: true });
md.use({ renderer: { html: token => esc(token.text) } });

const SUGERENCIAS = [
  "Quiero una mesa para 4 el viernes a las 21 en Palermo, mi cel es +5491155550123",
  "¿Qué horarios hay el sábado en Palermo, Belgrano y Caballito?",
  "Mostrame el menú de Belgrano",
  "Cancelá mi última reserva"
];

let estado: EstadoConexion | undefined;

// ---------- Estructura ----------
const app = document.getElementById("app")!;
app.innerHTML = `
  <header class="cabecera">
    <div class="conexion" id="conexion"></div>
    <div class="fila-modelo">
      <label for="modelo">Modelo</label>
      <select id="modelo"></select>
    </div>
    <details class="tools" id="tools"><summary></summary><ul></ul></details>
  </header>
  <main class="mensajes" id="mensajes">
    <div class="vacio" id="vacio">
      <div class="vacio-logo">🍽️</div>
      <p>Pedile lo que quieras al asistente de <b>MesaYa</b>. El modelo local elige qué <b>tools MCP</b> usar y el servidor las ejecuta contra la API.</p>
      <div class="sugerencias" id="sugerencias"></div>
    </div>
  </main>
  <footer class="pie">
    <textarea id="entrada" rows="2" placeholder="Escribí tu pedido… (Enter envía, Shift+Enter nueva línea)"></textarea>
    <div class="uso" id="uso" hidden></div>
    <div class="acciones-pie">
      <button class="sec" id="secuencia" title="Abrir la secuencia en vivo del servidor MCP">📈 Secuencia</button>
      <button class="sec" id="nueva" title="Nueva conversación (nueva sesión MCP)">＋ Nueva</button>
      <button id="boton">Enviar</button>
    </div>
  </footer>`;

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const mensajes = $<HTMLElement>("mensajes");
const entrada = $<HTMLTextAreaElement>("entrada");
const boton = $<HTMLButtonElement>("boton");
const selModelo = $<HTMLSelectElement>("modelo");

for (const s of SUGERENCIAS) {
  const b = document.createElement("button");
  b.className = "sugerencia";
  b.textContent = s;
  b.onclick = () => mandar(s);
  $("sugerencias").appendChild(b);
}

entrada.addEventListener("keydown", e => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); mandar(entrada.value); }
});
boton.onclick = () => estado?.ocupado ? enviar({ tipo: "detener" }) : mandar(entrada.value);
$("nueva").onclick = () => enviar({ tipo: "nuevaConversacion" });
$("secuencia").onclick = () => enviar({ tipo: "abrirSecuencia" });
selModelo.onchange = () => enviar({ tipo: "elegirModelo", nombre: selModelo.value });

function mandar(texto: string) {
  texto = texto.trim();
  if (!texto || estado?.ocupado || !estado?.conectado) return;
  entrada.value = "";
  enviar({ tipo: "enviar", texto });
}

// ---------- Mensajes desde la extensión ----------
window.addEventListener("message", (e: MessageEvent<HaciaWebview>) => {
  const m = e.data;
  switch (m.tipo) {
    case "estado": pintarEstado(m.estado); break;
    case "mensaje": pintarMensaje(m.mensaje); break;
    case "paso": pintarPaso(m.paso); break;
    case "elicitacion": pintarElicitacion(m.pedido); break;
    case "elicitacionCerrada": cerrarElicitacion(m.id, m.accion); break;
    case "uso": pintarUso(m.uso); break;
    case "limpiar":
      pintarUso({ entrada: 0, salida: 0, llamadas: 0 });
      mensajes.querySelectorAll(".item").forEach(n => n.remove());
      $("vacio").hidden = false;
      break;
  }
});
enviar({ tipo: "listo" });

function pintarEstado(e: EstadoConexion) {
  estado = e;
  const c = $("conexion");
  c.className = `conexion ${e.conectado ? "on" : e.conectando ? "pend" : "off"}`;
  c.innerHTML = e.conectado
    ? `<span class="punto"></span><span class="mcp">MCP</span><b>${esc(e.servidor ?? "")}</b>
       <span class="chip" title="Mcp-Session-Id">sesión ${esc((e.sesion ?? "").slice(0, 8))}</span>
       <span class="chip">${esc(e.protocolo ?? "")}</span>`
    : e.conectando
      ? `<span class="punto"></span>Conectando a ${esc(e.mcpUrl)}…`
      : `<span class="punto"></span><span class="err">${esc(e.error ?? "Sin conexión MCP")}</span> <button class="link" id="reconectar">Reintentar</button>`;
  document.getElementById("reconectar")?.addEventListener("click", () => enviar({ tipo: "reconectar" }));

  selModelo.innerHTML = e.modelos.length
    ? e.modelos.map(m => `<option value="${esc(m.nombre)}" ${m.nombre === e.modelo ? "selected" : ""}>${m.tools ? "🛠" : "⛔"} ${esc(m.nombre)}${m.tamano ? ` · ${esc(m.tamano)}` : ""}${m.tools ? "" : " · sin tools"}</option>`).join("")
    : `<option>${esc(e.modelo)}</option>`;
  selModelo.disabled = e.ocupado;
  selModelo.title = e.errorOllama ?? "";

  const tools = $("tools");
  tools.querySelector("summary")!.textContent = `${e.tools.length} tools descubiertas con tools/list`;
  tools.querySelector("ul")!.innerHTML = e.tools.map(t => `<li><code>${esc(t.nombre)}</code> ${esc(t.descripcion ?? "")}</li>`).join("");
  tools.hidden = !e.conectado;

  boton.textContent = e.ocupado ? "■ Detener" : "Enviar";
  boton.classList.toggle("detener", e.ocupado);
  boton.disabled = !e.conectado && !e.ocupado;
  entrada.disabled = !e.conectado;
  mensajes.classList.toggle("ocupado", e.ocupado);
  document.body.classList.toggle("sin-tools", !e.mostrarTools);
  pensando(e.ocupado);
}

function pintarUso(u: UsoConversacion) {
  const el = $("uso");
  el.hidden = u.llamadas === 0;
  if (el.hidden) return;
  const tps = (v?: number) => v === undefined ? "—" : v.toFixed(1);
  el.innerHTML = `<span title="Tokens de toda la conversación: ${u.entrada.toLocaleString("es-AR")} de entrada (prompt) + ${u.salida.toLocaleString("es-AR")} de salida, en ${u.llamadas} llamadas al modelo">
      🔢 <b>${miles(u.entrada + u.salida)}</b> tokens <span class="det">(${miles(u.entrada)} ↑ · ${miles(u.salida)} ↓)</span></span>
    <span title="Velocidad de generación: tokens de salida por segundo. La primera es la de la última respuesta; entre paréntesis, el promedio de la conversación.">
      ⚡ <b>${tps(u.ultimoTps)}</b> tok/s <span class="det">(prom. ${tps(u.promedioTps)})</span></span>`;
}

function miles(n: number) { return n >= 10_000 ? `${(n / 1000).toFixed(1)}k` : n.toLocaleString("es-AR"); }

function agregar(el: HTMLElement) {
  $("vacio").hidden = true;
  const indicador = document.getElementById("pensando");
  mensajes.insertBefore(el, indicador ?? null);
  mensajes.scrollTop = mensajes.scrollHeight;
}

function pensando(activo: boolean) {
  const actual = document.getElementById("pensando");
  if (activo && !actual) {
    const p = document.createElement("div");
    p.id = "pensando";
    p.className = "item pensando";
    p.innerHTML = '<span class="punto"></span><span class="punto"></span><span class="punto"></span><span class="txt"></span>';
    mensajes.appendChild(p);
    mensajes.scrollTop = mensajes.scrollHeight;
  } else if (!activo) { actual?.remove(); progresos.clear(); }
  actualizarProgreso();
}

// Con las tools ocultas, el aviso de progreso del servidor (p. ej. "Esperando la confirmación…") se muestra junto al indicador.
const progresos = new Map<string, string>();
function actualizarProgreso() {
  const txt = document.querySelector("#pensando .txt");
  if (txt) txt.textContent = [...progresos.values()].pop() ?? "";
}

function pintarMensaje(m: MensajeChat) {
  const el = document.createElement("div");
  el.className = `item burbuja ${m.rol}`;
  if (m.rol === "assistant") {
    el.innerHTML = `<div class="md">${md.parse(m.texto) as string}</div>
      <div class="acciones">
        <button class="link" data-a="copiar">Copiar</button>
        <button class="link" data-a="abrir">Abrir en editor</button>
        <button class="link" data-a="guardar">Descargar .md</button>
      </div>`;
    el.querySelector<HTMLElement>('[data-a="copiar"]')!.onclick = () => void navigator.clipboard.writeText(m.texto);
    el.querySelector<HTMLElement>('[data-a="abrir"]')!.onclick = () => enviar({ tipo: "abrirEnEditor", contenido: m.texto, lenguaje: "markdown" });
    el.querySelector<HTMLElement>('[data-a="guardar"]')!.onclick = () => enviar({ tipo: "guardar", contenido: m.texto, nombre: `mesaya-${marca()}.md` });
  } else el.textContent = m.texto;
  agregar(el);
}

function pintarPaso(p: Paso) {
  if (p.estado === "encurso" && p.progreso) progresos.set(p.id, p.progreso); else progresos.delete(p.id);
  actualizarProgreso();
  let el = document.getElementById(`p-${p.id}`);
  const nuevo = !el;
  if (!el) { el = document.createElement("div"); el.id = `p-${p.id}`; }
  el.className = `item paso ${p.tipo} ${p.estado}`;
  el.dataset.inicio = String(p.inicio);

  const icono = p.tipo === "llm" ? "🧠" : "🛠";
  const resultado = p.tipo === "tool" && p.estado !== "encurso"
    ? `<span class="resultado ${p.estado}">${p.estado === "ok" ? "✓ se pudo" : `✗ ${esc(p.codigoError ?? "error")}`}</span>` : "";
  const tiempo = p.estado === "encurso" ? `<span class="tiempo vivo">…</span>` : `<span class="tiempo">${dur(p.duracionMs)}</span>`;
  const abiertos = new Set([...el.querySelectorAll("details[open]")].map(d => (d as HTMLElement).dataset.k));

  el.innerHTML = `
    <div class="paso-cab">
      <span class="ico">${icono}</span>
      ${p.tipo === "tool" ? `<span class="mcp">tools/call</span>` : ""}
      <code class="nombre">${esc(p.titulo)}</code>
      ${resultado}
      ${tiempo}
    </div>
    ${p.sub ? `<div class="paso-sub">${esc(p.sub)}</div>` : ""}
    ${p.progreso ? `<div class="progreso"><span class="mcp chico">progress</span> ${esc(p.progreso)}</div>` : ""}
    ${p.entrada ? `<details data-k="in"><summary>${p.tipo === "llm" ? "pedido a Ollama" : "arguments"}</summary><pre>${esc(p.entrada)}</pre></details>` : ""}
    ${p.salida ? `<details data-k="out"><summary>${p.tipo === "llm" ? "respuesta del modelo" : "resultado"}</summary><pre>${esc(p.salida)}</pre>
        ${p.tipo === "tool" ? `<div class="acciones"><button class="link" data-a="abrir">Abrir JSON</button><button class="link" data-a="guardar">Descargar JSON</button></div>` : ""}
      </details>` : ""}`;
  // Conserva los desplegables que el usuario abrió; los args de tools arrancan abiertos.
  el.querySelectorAll("details").forEach(d => {
    const k = (d as HTMLElement).dataset.k!;
    if (abiertos.has(k) || (nuevo && p.tipo === "tool" && k === "in")) d.setAttribute("open", "");
  });
  el.querySelector<HTMLElement>('[data-a="abrir"]')?.addEventListener("click", () =>
    enviar({ tipo: "abrirEnEditor", contenido: p.salida ?? "", lenguaje: "json" }));
  el.querySelector<HTMLElement>('[data-a="guardar"]')?.addEventListener("click", () =>
    enviar({ tipo: "guardar", contenido: p.salida ?? "", nombre: `${p.titulo}-${marca()}.json` }));
  if (nuevo) agregar(el);
}

function pintarElicitacion(pedido: PedidoElicitacion) {
  const el = document.createElement("form");
  el.id = `e-${pedido.id}`;
  el.className = "item elicitacion";
  const campos = Object.entries(pedido.schema?.properties ?? {});
  el.innerHTML = `
    <div class="eli-cinta"><span class="tec"><span class="mcp">MCP</span> elicitation/create · el <b>servidor</b> le pregunta al usuario</span><span class="simple">🔒 Confirmación requerida</span></div>
    <div class="eli-msj">${esc(pedido.mensaje)}</div>
    ${campos.map(([n, c]) => campo(n, c, pedido.schema?.required?.includes(n) ?? false)).join("")}
    <div class="eli-botones">
      <button type="submit" data-a="accept">✔ Aceptar</button>
      <button type="button" class="sec" data-a="decline">✖ Rechazar</button>
      <button type="button" class="sec" data-a="cancel">Cerrar</button>
    </div>`;
  const responder = (accion: "accept" | "decline" | "cancel") => {
    const contenido: Record<string, unknown> = {};
    for (const [n, c] of campos) {
      const input = el.querySelector<HTMLInputElement | HTMLSelectElement>(`[name="${CSS.escape(n)}"]`);
      if (!input) continue;
      if (c.type === "boolean") contenido[n] = (input as HTMLInputElement).checked;
      else if ((c.type === "number" || c.type === "integer") && input.value !== "") contenido[n] = Number(input.value);
      else if (input.value !== "") contenido[n] = input.value;
    }
    enviar({ tipo: "responderElicitacion", id: pedido.id, accion, contenido: accion === "accept" ? contenido : undefined });
  };
  el.onsubmit = ev => { ev.preventDefault(); responder("accept"); };
  el.querySelectorAll<HTMLButtonElement>("button[type=button]").forEach(b => b.onclick = () => responder(b.dataset.a as "decline" | "cancel"));
  agregar(el);
  el.querySelector<HTMLElement>("input, select, button")?.focus();
}

function campo(nombre: string, c: CampoElicitacion, requerido: boolean): string {
  const etiqueta = `${esc(c.title ?? nombre)}${requerido ? " *" : ""}`;
  const ayuda = c.description ? `<small>${esc(c.description)}</small>` : "";
  const req = requerido ? "required" : "";
  let control: string;
  if (c.type === "boolean") control = `<input type="checkbox" name="${esc(nombre)}" ${c.default ? "checked" : ""} />`;
  else if (c.enum || c.oneOf) {
    const ops = c.oneOf ?? c.enum!.map(v => ({ const: v, title: v }));
    control = `<select name="${esc(nombre)}" ${req}><option value=""></option>${ops.map(o => `<option value="${esc(o.const)}">${esc(o.title ?? o.const)}</option>`).join("")}</select>`;
  } else if (c.type === "number" || c.type === "integer")
    control = `<input type="number" name="${esc(nombre)}" ${c.minimum !== undefined ? `min="${c.minimum}"` : ""} ${c.maximum !== undefined ? `max="${c.maximum}"` : ""} ${req} />`;
  else control = `<input type="text" name="${esc(nombre)}" ${c.maxLength ? `maxlength="${c.maxLength}"` : ""} ${req} />`;
  return `<label class="eli-campo"><span>${etiqueta}</span>${control}${ayuda}</label>`;
}

function cerrarElicitacion(id: string, accion: string) {
  const el = document.getElementById(`e-${id}`);
  if (!el) return;
  el.classList.add("respondida", accion);
  el.querySelectorAll("input, select, button").forEach(n => (n as HTMLInputElement).disabled = true);
  const pie = el.querySelector(".eli-botones")!;
  pie.innerHTML = `<span class="eli-resp">${accion === "accept" ? "✔ Aceptado" : accion === "decline" ? "✖ Rechazado" : "Cerrado sin responder"} · respuesta enviada al servidor</span>`;
}

// Cronómetro de las tarjetas en curso.
setInterval(() => {
  document.querySelectorAll<HTMLElement>(".paso.encurso").forEach(el => {
    const t = el.querySelector(".tiempo.vivo");
    if (t) t.textContent = dur(Date.now() - Number(el.dataset.inicio));
  });
}, 200);

function dur(ms?: number) { return ms === undefined ? "" : ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`; }
function marca() { return new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-"); }
function esc(s: string) {
  return s.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}
