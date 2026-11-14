// MesaYa · Workflows y orquestación — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
// Redibuja solo si cambió: así las animaciones de entrada no se reinician.
function poner(el, html) { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; }
const seg = ms => (ms / 1000).toFixed(1).replace(".", ",") + " s";
const mil = n => Number(n || 0).toLocaleString("es-AR");

// corridas: todas las ejecuciones de la sesión (para el chat y la comparación)
const S = { cfg: {}, base: {}, patron: "chaining", catalogo: null, chat: [], corridas: [], sel: null, tab: "recorrido",
            esc: null, hecho: {}, ocupado: false };
const patron = id => S.catalogo.patrones.find(p => p.id === id);

// ---------- controles ----------
function pintarControles() {
  $$("[data-k]").forEach(el => {
    const v = S.cfg[el.dataset.k];
    if (el.type === "checkbox") el.checked = !!v; else el.value = v;
  });
  $("#v-max_vueltas").textContent = S.cfg.max_vueltas;
  $("#v-max_pasos").textContent = S.cfg.max_pasos;
}
$$("[data-k]").forEach(el => el.addEventListener("input", () => {
  S.cfg[el.dataset.k] = el.type === "checkbox" ? el.checked : Number(el.value); pintarControles();
}));

// ---------- estado de Ollama ----------
async function estado() {
  try {
    const e = await (await fetch("/api/estado")).json();
    const dot = $("#estado .dot"), txt = $("#estado-txt"), sel = $("#modelo");
    const desc = Object.entries(e.descargas || {}).find(([, d]) => d.estado === "descargando");
    $("#guion").checked = e.modo_guion;
    if (e.modo_guion) { dot.className = "dot ok"; txt.textContent = "Modo guion"; sel.hidden = true; }
    else if (desc) { const [m, d] = desc; dot.className = "dot carga"; txt.textContent = `Descargando ${m}… ${d.total ? Math.round(100 * d.completado / d.total) : 0} %`; sel.hidden = true; }
    else if (!e.conectado) { dot.className = "dot mal"; txt.textContent = e.error || "Sin Ollama"; sel.hidden = true; }
    else if (!e.listo) { dot.className = "dot carga"; txt.textContent = "Preparando el modelo…"; sel.hidden = true; }
    else {
      dot.className = "dot ok"; txt.textContent = ""; sel.hidden = false;
      if (sel.options.length !== e.instalados.length) sel.innerHTML = e.instalados.map(m => `<option>${esc(m)}</option>`).join("");
      sel.value = e.modelo;
    }
    setTimeout(estado, desc || !e.listo ? 1500 : 6000);
  } catch { $("#estado .dot").className = "dot mal"; $("#estado-txt").textContent = "Sin servidor"; setTimeout(estado, 3000); }
}
$("#modelo").onchange = e => post("/api/config", { modelo: e.target.value });
$("#guion").onchange = e => post("/api/config", { modo_guion: e.target.checked }).then(estado);
function toast(t) { const d = document.createElement("div"); d.className = "toast"; d.textContent = t; document.body.append(d); setTimeout(() => d.remove(), 4500); }

// ---------- patrones ----------
function pintarPatrones() {
  poner($("#patrones"), S.catalogo.patrones.map(p => `<button class="patron ${p.id} ${S.patron === p.id ? "on" : ""}" data-p="${p.id}">
      <span class="icb">${ic(p.icono)}</span><span><b>${esc(p.nombre)}</b><small>${esc(p.resumen)}</small></span></button>`).join(""));
  $$(".patron").forEach(b => b.onclick = () => { S.patron = b.dataset.p; pintarPatrones(); pintarCabecera(); });
  pintarCabecera();
}
function pintarCabecera() {
  const p = patron(S.patron);
  $("#cab-icono").innerHTML = ic(p.icono);
  $("#cab-icono").className = "icb" + (p.id === "agente" ? " violeta" : "");
  $("#cab-nombre").textContent = p.nombre;
  $("#cab-resumen").textContent = p.resumen;
}

// ---------- escenarios ----------
function pintarEscenarios() {
  const html = S.catalogo.escenarios.map(e => `<div class="esc ${S.esc === e.id ? "abierto" : ""}" data-e="${e.id}">
      <button><span class="letra">${e.id}</span><span><div class="nom">${esc(e.titulo)}</div><div class="sl">Slides ${esc(e.slides)}</div></span></button>
      <div class="cuerpo"><p class="idea">${esc(e.idea)}</p>
        <div class="vars">${e.corridas.map((c, i) => {
          const k = `${e.id}${i}`;
          return `<button class="var ${c.bien ? "bien" : ""} ${S.hecho[k] === "corriendo" ? "corriendo" : S.hecho[k] ? "hecho" : ""}" data-i="${i}" title="${esc(c.mensaje)}">
            ${c.bien ? ic("check") : ""}${esc(c.nombre)}<span class="pat">· ${esc(patron(c.patron).nombre.split(" · ")[0])}</span></button>`;
        }).join("")}</div></div></div>`).join("");
  if (!poner($("#escenarios"), html)) return;
  $$(".esc > button").forEach(b => b.onclick = () => { const id = b.parentElement.dataset.e; S.esc = S.esc === id ? null : id; pintarEscenarios(); });
  $$(".var").forEach(b => b.onclick = () => correrEscenario(b.closest(".esc").dataset.e, Number(b.dataset.i)));
}

async function correrEscenario(id, i) {
  if (S.ocupado) return;
  const e = S.catalogo.escenarios.find(x => x.id === id), c = e.corridas[i], k = `${id}${i}`;
  S.patron = c.patron; pintarPatrones();
  S.cfg = { ...S.base, ...(c.config || {}) }; pintarControles();
  S.tab = e.panel; pintarTabs();
  S.hecho[k] = "corriendo"; pintarEscenarios();
  const ok = await enviar(c.mensaje);
  S.hecho[k] = ok; pintarEscenarios();
}

// ---------- chat ----------
$("#enviar").onclick = () => { const t = $("#mensaje").value.trim(); if (t) enviar(t); };
$("#mensaje").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#enviar").click(); } });
$("#limpiar").onclick = async () => {
  await fetch("/api/corridas", { method: "DELETE" });
  S.chat = []; S.corridas = []; S.sel = null; S.hecho = {}; pintarTodo();
};

// El historial vive en el servidor (datos/corridas.json): se recupera al abrir o recargar la página.
async function cargarCorridas() {
  const lista = await (await fetch("/api/corridas")).json();
  S.chat = []; S.corridas = [];
  for (const r of lista) {
    const item = { res: r, n: r.n, patron: r.patron };
    S.chat.push({ tipo: "u", texto: r.mensaje, patron: r.patron }, item);
    S.corridas.push(item);
  }
  if (lista.length) S.chat.unshift({ tipo: "sep", texto: `${lista.length} ${lista.length === 1 ? "corrida guardada" : "corridas guardadas"} · desde ${lista[0].fecha}` });
}

async function enviar(texto) {
  if (S.ocupado) return false;
  S.ocupado = true; $("#enviar").disabled = true; $("#mensaje").value = "";
  const p = patron(S.patron), cfg = { ...S.cfg };
  const item = { cargando: true, patron: p.id };
  S.chat.push({ tipo: "u", texto, patron: p.id, cfg }, item);
  S.sel = item; pintarChat(); pintarPanel();
  let ok = true;
  try {
    const r = await post("/api/correr", { patron: p.id, mensaje: texto, config: cfg });
    const d = await r.json();
    if (!r.ok) throw new Error(d.detail || r.statusText);
    Object.assign(item, { cargando: false, res: d, n: d.n });
    S.corridas.push(item);
  } catch (e) { Object.assign(item, { cargando: false, error: e.message }); toast(e.message); ok = false; }
  S.ocupado = false; $("#enviar").disabled = false;
  pintarTodo();
  return ok;
}

function chipsCorrida(r) {
  const s = r.resumen, out = [`<span class="chip grad">${ic(patron(r.patron).icono)}${esc(patron(r.patron).nombre)}</span>`,
    `<span class="chip azul">${ic("chat")}${s.llamadas} ${s.llamadas === 1 ? "llamada" : "llamadas"}</span>`,
    `<span class="chip">${mil(s.tokens)} tokens</span>`, `<span class="chip">${ic("clock")}${seg(s.ms_total)}</span>`];
  const corte = r.pasos.find(p => p.marca === "corta" && p.tipo === "gate");
  if (corte) out.push(`<span class="chip mal">${ic("stop")}${esc(corte.titulo)}</span>`);
  if (r.patron === "evaluador") out.push(`<span class="chip ${r.extra.aprobado ? "ok" : "mal"}">${r.extra.rondas.length} ${r.extra.rondas.length === 1 ? "vuelta" : "vueltas"} · ${r.extra.aprobado ? "aprobada" : "sin aprobar"}</span>`);
  if (r.patron === "voting") out.push(`<span class="chip ${r.extra.escalar ? "mal" : "ok"}">${r.extra.escalar ? "se escala" : "plantilla"}</span>`);
  if (r.patron === "sectioning" && r.extra.bloqueado) out.push(`<span class="chip mal">${ic("shield")}bloqueado</span>`);
  if (r.grabado) out.push(`<span class="chip vio">Grabado ${esc(r.grabado)}</span>`);
  return out.join("");
}

function pintarChat() {
  const m = $("#mensajes");
  if (!S.chat.length) {
    poner(m, `<div class="vacio"><img src="/static/isotipo-blanco.png" alt=""><h2>¿Quién decide el camino?</h2>
      <p>Elegí un patrón y escribí un pedido, o corré un escenario. A la derecha se ve cada paso, cada llamada al modelo y lo que cuesta.</p></div>`);
    return;
  }
  const html = S.chat.map((it, i) => {
    if (it.tipo === "sep") return `<div class="separador">${esc(it.texto)}</div>`;
    if (it.tipo === "u") return `<div class="msg-u">${esc(it.texto)}</div>`;
    const r = it.res;
    return `<div class="msg-a ${S.sel === it ? "sel" : ""} ${it.error ? "err" : ""}" data-i="${i}">
      ${r ? `<div class="meta">${chipsCorrida(r)}</div>` : ""}
      <div class="resp">${it.cargando ? `<span class="pensando"><i></i><i></i><i></i></span>` : esc(it.error || r.respuesta)}</div></div>`;
  }).join("");
  if (!poner(m, html)) return;
  $$(".msg-a").forEach(el => el.onclick = () => { const it = S.chat[Number(el.dataset.i)]; if (!it.res) return; S.sel = it; pintarChat(); pintarPanel(); });
  m.scrollTop = m.scrollHeight;
}

// ---------- pestañas ----------
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); pintarPanel(); }
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); });

function pintarPanel() {
  const p = $("#panel");
  if (S.tab === "comparar") return pintarComparar(p);
  const it = S.sel || S.corridas[S.corridas.length - 1];
  if (!it) { poner(p, `<div class="panel-vacio">${ic("steps")}<p>Todavía no corriste nada.<br>Cada corrida muestra acá sus pasos, sus llamadas al modelo y lo que tardó.</p></div>`); return; }
  if (it.cargando) { poner(p, `<div class="panel-vacio"><span class="pensando"><i></i><i></i><i></i></span><p>Corriendo ${esc(patron(it.patron).nombre)}…</p></div>`); return; }
  if (it.error) { poner(p, `<div class="aviso">${ic("alert")}<div>${esc(it.error)}</div></div>`); return; }
  (S.tab === "recorrido" ? pintarRecorrido : pintarLlamadas)(p, it.res);
}

const ICONO_PASO = { llm: "chat", tool: "bolt", gate: "funnel", codigo: "code", fin: "check" };
const MARCA = { ok: ["ok", "ok"], corta: ["mal", "corta"], aviso: ["warn", "aviso"], descartada: ["", "descartada"], aprobado: ["ok", "aprobado"], rechazado: ["mal", "no aprueba"] };

function salidaTexto(x) { return typeof x === "string" ? x : JSON.stringify(x, null, 2); }

function tarjetaPatron(r) {
  const x = r.extra;
  if (r.patron === "routing") return `<div class="card"><h4>${ic("route")}El router eligió <span class="chip azul">confianza ${Number(x.confianza ?? 0).toFixed(2)}</span></h4>
    <div class="rutas">${x.rutas.map(ru => `<div class="ruta ${ru.id === x.ruta ? "on" : ""}">${ic(ru.icono)}<b>${esc(ru.nombre)}</b>${esc(ru.como)}</div>`).join("")}</div></div>`;
  if (r.patron === "voting") return `<div class="card"><h4>${ic("vote")}Tres votos · ${x.votos.filter(v => v.grave).length} de 3 dicen grave</h4>
    <div class="votos">${x.votos.map(v => `<div class="voto ${v.grave ? "grave" : ""}"><b>${esc(v.quien)}</b><span class="v">${v.grave ? "grave" : "no grave"}</span><p>${esc(v.motivo)}</p></div>`).join("")}</div></div>`;
  if (r.patron === "orquestador" && x.plan) return `<div class="card"><h4>${ic("sitemap")}El plan que decidió el orquestador · ${x.plan.length} ${x.plan.length === 1 ? "subtarea" : "subtareas"}</h4>
    <div class="plan">${x.plan.map((s, i) => `<div><span class="n">${i + 1}</span><span>${esc(s.titulo)}</span><span class="chip" style="margin-left:auto">${esc(s.sucursal)} · ${esc(s.mes)}</span></div>`).join("")}</div></div>`;
  if (r.patron === "evaluador") return `<div class="card"><h4>${ic("loop")}${x.rondas.length} ${x.rondas.length === 1 ? "vuelta" : "vueltas"} · ${x.aprobado ? "aprobada" : `sin aprobar (máximo ${r.config.max_vueltas})`}</h4>
    ${x.rondas.map(ro => `<div class="ronda"><div>Vuelta ${ro.vuelta} <span class="chip ${ro.aprobado ? "ok" : "mal"}" style="margin-left:auto">${ro.criterios.filter(c => c.cumple).length} de ${ro.criterios.length} criterios</span></div>
      <div class="borr">${esc(ro.borrador)}</div>
      ${ro.criterios.map(c => `<div class="crit ${c.cumple ? "" : "no"}">${ic(c.cumple ? "check" : "x")}<div>${esc(c.criterio)} <span class="chip" style="font-size:10px">${esc(c.quien)}</span><small>${esc(c.motivo)}</small></div></div>`).join("")}</div>`).join("")}</div>`;
  if (r.patron === "sectioning") return `<div class="aviso ${x.bloqueado ? "" : "info"}">${ic(x.bloqueado ? "shield" : "check")}<div>${x.bloqueado
    ? `<b>La guardia bloqueó la respuesta.</b> ${esc(x.motivo)}` : `<b>La guardia no encontró nada:</b> sale la respuesta de A.`}${r.config.paralelo ? "" : " Esta corrida fue <b>sin paralelo</b>: compará el tiempo en la pestaña Comparar."}</div></div>`;
  return "";
}

function pintarRecorrido(p, r) {
  const s = r.resumen, total = Math.max(1, s.ms_total);
  let h = `<div class="kpis"><div class="kpi"><div class="n">LLAMADAS AL LLM</div><div class="v">${s.llamadas}</div><div class="s">${s.pasos} pasos en total</div></div>
    <div class="kpi"><div class="n">TOKENS</div><div class="v">${mil(s.tokens)}</div><div class="s">entrada + salida</div></div>
    <div class="kpi"><div class="n">TIEMPO</div><div class="v">${seg(s.ms_total)}</div><div class="s">${s.ms_llm > s.ms_total ? `${seg(s.ms_llm)} si fuera en secuencia` : "de punta a punta"}</div></div>
    <div class="kpi ${r.patron === "agente" ? "mal" : ""}"><div class="n">¿QUIÉN DECIDE?</div><div class="v" style="font-size:19px;padding:5px 0 4px">${r.patron === "agente" ? "El modelo" : "El código"}</div><div class="s">${r.patron === "agente" ? "agente" : "workflow"}</div></div></div>`;
  h += tarjetaPatron(r);
  h += `<div class="rotulo">Línea de tiempo · tocá un paso para ver su salida</div><div class="eje"><span>0 s</span><span>${seg(total)}</span></div><div class="tl">`;
  let grupo = null;
  r.pasos.forEach((x, i) => {
    if ((x.grupo || null) !== grupo) {
      grupo = x.grupo || null;
      if (grupo) h += `<div class="tl-grupo ${x.paralelo ? "par" : ""}">${x.paralelo ? ic("parallel") : ""}${esc(grupo)}</div>`;
      else h += `<div class="tl-grupo"></div>`;
    }
    const [mc, mt] = MARCA[x.marca] || [];
    const ancho = x.ms ? Math.max(.6, 100 * x.ms / total) : 0;
    h += `<div class="paso-r ${x.tipo} ${x.marca ? "m-" + x.marca : ""}" style="animation-delay:${Math.min(i, 12) * .04}s">
      <div class="fila"><span class="tipo">${ic(ICONO_PASO[x.tipo] || "steps")}</span>
        <div><div class="t">${esc(x.titulo)} ${mt ? `<span class="chip ${mc}">${mt}</span>` : ""}</div><div class="d">${esc(x.detalle)}</div></div>
        <div class="num">${x.tipo === "llm" ? `<b>${seg(x.ms)}</b><br>${mil(x.tokens)} tok` : x.tipo === "tool" ? "herramienta" : "código"}</div></div>
      <div class="gantt"><i style="left:${100 * x.inicio / total}%;width:${ancho}%"></i></div>
      <pre class="bloque">${esc(salidaTexto(x.salida) || "(sin salida)")}</pre></div>`;
  });
  h += `</div>`;
  if (!poner(p, h + glosario(r.patron))) return;
  p.querySelectorAll(".paso-r > .fila").forEach(f => f.onclick = () => f.parentElement.classList.toggle("abierto"));
}

const GLOS = {
  chaining: "<p><b>Prompt chaining:</b> pasos fijos; la salida de uno es la entrada del siguiente.</p><p><b>Gate:</b> un chequeo en código entre pasos. Si falla, la cadena se corta y no se gastan más llamadas.</p>",
  routing: "<p><b>Routing:</b> primero se clasifica, después se especializa. Cada ruta tiene su prompt, su documento y su costo.</p><p><b>Fuera de dominio</b> no gasta una segunda llamada.</p>",
  sectioning: "<p><b>Sectioning:</b> partes distintas de la tarea al mismo tiempo. En paralelo tarda lo que la más lenta; en secuencia, la suma.</p>",
  voting: "<p><b>Voting:</b> la misma pregunta varias veces, con miradas distintas, y decide la mayoría. Más confiable, pero cuesta el triple.</p>",
  orquestador: "<p><b>Orchestrator-workers:</b> las subtareas no están escritas de antemano: las decide el orquestador según el pedido.</p>",
  evaluador: "<p><b>Evaluator-optimizer:</b> genera, evalúa con criterios claros y corrige. El máximo de vueltas evita un loop infinito.</p><p>Un criterio lo chequea el <b>código</b>, no el LLM: lo que se puede contar, se cuenta.</p>",
  agente: "<p><b>Agente:</b> decide → ejecuta → observa, hasta la señal de fin. Ningún paso está escrito de antemano.</p><p><b>Freno:</b> máximo de pasos y detección de repeticiones.</p>",
};
const glosario = id => `<div class="glos">${GLOS[id] || ""}</div>`;

function pintarLlamadas(p, r) {
  let h = `<div class="aviso info">${ic("chat")}<div><b>${r.llamadas.length} ${r.llamadas.length === 1 ? "llamada" : "llamadas"} al modelo</b> en esta corrida. Tocá una para ver los mensajes que recibió y lo que devolvió.</div></div>`;
  h += r.llamadas.map((l, i) => `<div class="llamada"><div>${ic("chat")}<b>${i + 1} · ${esc(l.titulo)}</b>
      <span class="chip">${mil(l.tokens_prompt)} entrada</span><span class="chip">${mil(l.tokens_respuesta)} salida</span><span class="chip">${seg(l.ms || 0)}</span>${l.esquema ? `<span class="chip azul">JSON</span>` : ""}</div>
    <div class="cuerpo">${l.mensajes.map(m => `<div class="rol ${m.role}">${esc(m.role)}</div><pre class="bloque">${esc(m.content)}</pre>`).join("")}
      <div class="rol assistant">salida</div><pre class="bloque">${esc(l.salida)}</pre></div></div>`).join("");
  if (!poner(p, h)) return;
  p.querySelectorAll(".llamada > div:first-child").forEach(d => d.onclick = () => d.parentElement.classList.toggle("abierta"));
}

// "Parallelization · voting" → "Voting": la tabla tiene poco ancho.
const corto = id => { const n = patron(id).nombre; return n.includes(" · ") ? n.split(" · ")[1].replace(/^./, c => c.toUpperCase()) : n; };

function pintarComparar(p) {
  const C = S.corridas;
  if (!C.length) { poner(p, `<div class="panel-vacio">${ic("trend")}<p>Corré dos o más pedidos y compará acá cuántas llamadas, tokens y segundos costó cada patrón.<br><br>Probá el escenario <b>A · Workflow vs agente</b>.</p></div>`); return; }
  const mT = Math.max(...C.map(c => c.res.resumen.tokens), 1), mS = Math.max(...C.map(c => c.res.resumen.ms_total), 1), mL = Math.max(...C.map(c => c.res.resumen.llamadas), 1);
  let h = `<table class="t"><tr><th>#</th><th>Patrón</th><th>Pedido</th><th>Llamadas</th><th>Tokens</th><th>Tiempo</th></tr>${C.map(c => {
    const s = c.res.resumen;
    return `<tr class="${S.sel === c ? "sel" : ""}" data-n="${c.n}"><td>${c.n}</td><td class="pat-c">${ic(patron(c.patron).icono)} ${esc(corto(c.patron))}${c.res.config.paralelo === false ? ' <span class="chip">sin paralelo</span>' : ""}</td>
      <td class="ped"><div title="${esc(c.res.mensaje)}">${esc(c.res.mensaje)}</div></td>
      <td class="n"><span class="barra"><i style="width:${100 * s.llamadas / mL}%"></i></span>${s.llamadas}</td>
      <td class="n"><span class="barra"><i style="width:${100 * s.tokens / mT}%"></i></span>${mil(s.tokens)}</td>
      <td class="n"><span class="barra"><i style="width:${100 * s.ms_total / mS}%"></i></span>${seg(s.ms_total)}</td></tr>`;
  }).join("")}</table>`;
  if (!poner(p, h + `<div class="glos"><p><b>La autonomía se paga:</b> en tokens, en segundos y en previsibilidad.</p>
    <p><b>Regla práctica:</b> elegí el escalón más bajo que resuelva el caso. Tocá una fila para ver su recorrido.</p></div>`)) return;
  p.querySelectorAll("tr[data-n]").forEach(tr => tr.onclick = () => { S.sel = C.find(c => c.n === Number(tr.dataset.n)); S.tab = "recorrido"; pintarTabs(); pintarChat(); });
}

function pintarTodo() { pintarPatrones(); pintarEscenarios(); pintarChat(); pintarPanel(); }

// ---------- inicio ----------
(async () => {
  estado();
  S.catalogo = await (await fetch("/api/catalogo")).json();
  S.base = { ...S.catalogo.config }; S.cfg = { ...S.base };
  pintarControles();
  await cargarCorridas();
  pintarTodo();
})();
