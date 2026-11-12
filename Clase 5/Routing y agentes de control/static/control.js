// MesaYa · Routing y agentes de control — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
function poner(el, html) { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; }
const seg = ms => (ms / 1000).toFixed(1).replace(".", ",") + " s";
const mil = n => Number(n || 0).toLocaleString("es-AR");
const pct = x => Math.round(100 * x) + " %";
const dec = x => Number(x ?? 0).toFixed(2).replace(".", ",");

const S = { cfg: {}, base: {}, canal: "cliente", cat: null, chat: [], corridas: [], sel: null, tab: "recorrido", esc: null, hecho: {},
            ocupado: false, mediciones: [], medSel: null, generado: null, regresion: [], regRes: null, ataques: {}, trabajo: null };
const nombreInt = id => S.cat.intenciones.find(i => i.id === id)?.nombre ?? id;
const canal = id => S.cat.canales.find(c => c.id === id);
const metodo = id => S.cat.metodos.find(m => m.id === id);

async function llamar(url, body) {
  const r = await post(url, body), d = await r.json();
  if (!r.ok) throw new Error(d.detail || r.statusText);
  return d;
}
function toast(t) { const d = document.createElement("div"); d.className = "toast"; d.textContent = t; document.body.append(d); setTimeout(() => d.remove(), 4500); }

// ---------- controles ----------
function pintarControles() {
  $$("[data-k]").forEach(el => { const v = S.cfg[el.dataset.k]; if (el.type === "checkbox") el.checked = !!v; else el.value = v; });
  $("#v-umbral").textContent = dec(S.cfg.umbral);
  poner($("#metodos"), S.cat.metodos.map(m => `<button data-v="${m.id}" class="${S.cfg.metodo === m.id ? "on" : ""}">${esc(m.nombre)}</button>`).join(""));
  $$("#metodos button").forEach(b => b.onclick = () => { S.cfg.metodo = b.dataset.v; pintarControles(); });
  $("#nota-metodo").textContent = metodo(S.cfg.metodo)?.resumen ?? "";
  poner($("#canales"), S.cat.canales.map(c => `<button class="canal ${c.id} ${S.canal === c.id ? "on" : ""}" data-c="${c.id}">
      <span class="icb">${ic(c.icono)}</span><span><b>${esc(c.nombre)}</b><small>${esc(c.quien)}</small></span></button>`).join(""));
  $$(".canal").forEach(b => b.onclick = () => { S.canal = b.dataset.c; pintarControles(); });
  const c = canal(S.canal);
  $("#cab-icono").innerHTML = ic(c.icono); $("#cab-icono").className = "icb" + (c.id === "gerencia" ? " violeta" : "");
  $("#cab-nombre").textContent = c.nombre; $("#cab-quien").textContent = `Escribe ${c.quien}`;
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
    else if (!e.listo) { dot.className = "dot carga"; txt.textContent = "Preparando modelos…"; sel.hidden = true; }
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

// ---------- escenarios ----------
function pintarEscenarios() {
  const html = S.cat.escenarios.map(e => `<div class="esc ${S.esc === e.id ? "abierto" : ""}" data-e="${e.id}">
      <button><span class="letra">${e.id}</span><span><div class="nom">${esc(e.titulo)}</div><div class="sl">Slides ${esc(e.slides)}</div></span></button>
      <div class="cuerpo"><p class="idea">${esc(e.idea)}</p>
        <div class="vars">${e.corridas.map((c, i) => {
          const k = `${e.id}${i}`;
          return `<button class="var ${c.bien ? "bien" : ""} ${S.hecho[k] === "corriendo" ? "corriendo" : S.hecho[k] ? "hecho" : ""}" data-i="${i}" title="${esc(c.mensaje || "")}">${c.bien ? ic("check") : ""}${esc(c.nombre)}</button>`;
        }).join("")}</div></div></div>`).join("");
  if (!poner($("#escenarios"), html)) return;
  $$(".esc > button").forEach(b => b.onclick = () => { const id = b.parentElement.dataset.e; S.esc = S.esc === id ? null : id; pintarEscenarios(); });
  $$(".var").forEach(b => b.onclick = () => correrEscenario(b.closest(".esc").dataset.e, Number(b.dataset.i)));
}

async function correrEscenario(id, i) {
  if (S.ocupado) return;
  const e = S.cat.escenarios.find(x => x.id === id), c = e.corridas[i], k = `${id}${i}`;
  S.cfg = { ...S.base, ...(c.config || {}) };
  S.canal = c.canal || "cliente";
  pintarControles();
  S.tab = e.panel; pintarTabs();
  S.hecho[k] = "corriendo"; pintarEscenarios();
  let ok;
  if (c.accion === "medir") ok = await medir();
  else if (c.accion === "generar") ok = await generar(c.intencion);
  else if (c.accion === "regresion") ok = await correrRegresion();
  else if (c.accion === "atacar") ok = await atacar();
  else ok = await enviar(c.mensaje);
  S.hecho[k] = ok; pintarEscenarios();
}

// trabajos largos del panel derecho (medir, generar, atacar)
async function trabajo(texto, f) {
  if (S.ocupado) return false;
  S.ocupado = true; S.trabajo = texto; pintarPanel();
  try { await f(); return true; }
  catch (e) { toast(e.message); return false; }
  finally { S.ocupado = false; S.trabajo = null; pintarPanel(); }
}

// ---------- chat ----------
$("#enviar").onclick = () => { const t = $("#mensaje").value.trim(); if (t) enviar(t); };
$("#mensaje").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#enviar").click(); } });
$("#limpiar").onclick = async () => { await fetch("/api/corridas", { method: "DELETE" }); S.chat = []; S.corridas = []; S.sel = null; S.hecho = {}; pintarTodo(); };

async function cargarTodo() {
  const [corr, med, reg] = await Promise.all(["/api/corridas", "/api/mediciones", "/api/regresion"].map(u => fetch(u).then(r => r.json())));
  S.chat = []; S.corridas = [];
  for (const r of corr) { const it = { res: r }; S.chat.push({ tipo: "u", texto: r.mensaje, canal: r.canal }, it); S.corridas.push(it); }
  if (corr.length) S.chat.unshift({ tipo: "sep", texto: `${corr.length} ${corr.length === 1 ? "turno guardado" : "turnos guardados"} · desde ${corr[0].fecha}` });
  S.mediciones = med; S.medSel = med.filter(m => m.titulo === "Set etiquetado").at(-1) || null;
  S.generado = med.filter(m => m.titulo.startsWith("Casos generados")).at(-1) || null;
  S.regRes = med.filter(m => m.titulo === "Set de regresión").at(-1) || null;
  S.regresion = reg;
}

async function enviar(texto) {
  if (S.ocupado) return false;
  S.ocupado = true; $("#enviar").disabled = true; $("#mensaje").value = "";
  const item = { cargando: true };
  S.chat.push({ tipo: "u", texto, canal: S.canal }, item); S.sel = item;
  if (!["recorrido", "llamadas"].includes(S.tab)) { S.tab = "recorrido"; pintarTabs(); }
  pintarChat(); pintarPanel();
  let ok = true;
  try {
    Object.assign(item, { cargando: false, res: await llamar("/api/turno", { mensaje: texto, canal: S.canal, config: S.cfg }) });
    S.corridas.push(item);
  } catch (e) { Object.assign(item, { cargando: false, error: e.message }); toast(e.message); ok = false; }
  S.ocupado = false; $("#enviar").disabled = false;
  pintarTodo();
  return ok;
}

const DECISION = { respondida: ["ok", "respondida"], repregunta: ["warn", "repregunta"], bloqueada: ["mal", "bloqueada"], humano: ["vio", "a una persona"],
                   estandar: ["", "estándar"], validacion: ["warn", "no pasó la validación"] };
function chipsTurno(r) {
  const c = r.clasificacion, [dc, dt] = DECISION[r.decision] || ["", r.decision];
  const out = [`<span class="chip">${ic(canal(r.canal).icono)}${esc(canal(r.canal).nombre)}</span>`,
    `<span class="chip grad">${ic("route")}${esc(nombreInt(c.intencion))} · ${dec(c.confianza)}</span>`,
    `<span class="chip ${dc}">${esc(dt)}</span>`,
    `<span class="chip azul">${ic("chat")}${r.resumen.llamadas} ${r.resumen.llamadas === 1 ? "llamada" : "llamadas"}</span>`,
    `<span class="chip">${ic("clock")}${seg(r.resumen.ms_total)}</span>`];
  if (r.fugas?.length) out.push(`<span class="chip mal">${ic("alert")}${esc(r.fugas.join(", "))}</span>`);
  if (r.grabado) out.push(`<span class="chip vio">Grabado ${esc(r.grabado)}</span>`);
  return out.join("");
}

function pintarChat() {
  const m = $("#mensajes");
  if (!S.chat.length) {
    poner(m, `<div class="vacio"><img src="/static/isotipo-blanco.png" alt=""><h2>¿Quién atiende cada pedido?</h2>
      <p>Elegí un canal y escribile al asistente, o corré un escenario. A la derecha se ve cómo lo clasificó el router, por dónde siguió y qué controles lo revisaron.</p></div>`);
    return;
  }
  const html = S.chat.map((it, i) => {
    if (it.tipo === "sep") return `<div class="separador">${esc(it.texto)}</div>`;
    if (it.tipo === "u") return `<div class="msg-u">${esc(it.texto)}<small>${esc(canal(it.canal)?.nombre)}</small></div>`;
    const r = it.res;
    return `<div class="msg-a ${S.sel === it ? "sel" : ""} ${it.error || r?.fugas?.length ? "err" : ""}" data-i="${i}">
      ${r ? `<div class="meta">${chipsTurno(r)}</div>` : ""}
      <div class="resp">${it.cargando ? `<span class="pensando"><i></i><i></i><i></i></span>` : esc(it.error || r.respuesta)}</div></div>`;
  }).join("");
  if (!poner(m, html)) return;
  $$(".msg-a").forEach(el => el.onclick = () => { const it = S.chat[Number(el.dataset.i)]; if (!it.res) return; S.sel = it; S.tab = S.tab === "llamadas" ? "llamadas" : "recorrido"; pintarTabs(); pintarChat(); });
  m.scrollTop = m.scrollHeight;
}

// ---------- pestañas ----------
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); pintarPanel(); }
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); });

function pintarPanel() {
  const p = $("#panel");
  if (S.tab === "medir") return pintarMedir(p);
  if (S.tab === "pruebas") return pintarPruebas(p);
  const it = S.sel || S.corridas.at(-1);
  if (!it) { poner(p, `<div class="panel-vacio">${ic("steps")}<p>Todavía no hay turnos.<br>Cada mensaje muestra acá cómo lo clasificó el router y por dónde siguió.</p></div>`); return; }
  if (it.cargando) { poner(p, `<div class="panel-vacio"><span class="pensando"><i></i><i></i><i></i></span><p>Clasificando y resolviendo…</p></div>`); return; }
  if (it.error) { poner(p, `<div class="aviso">${ic("alert")}<div>${esc(it.error)}</div></div>`); return; }
  (S.tab === "recorrido" ? pintarRecorrido : pintarLlamadas)(p, it.res);
}

// ---------- recorrido ----------
const ICONO_PASO = { llm: "chat", tool: "bolt", gate: "funnel", codigo: "code", embed: "vector" };
const MARCA = { ok: ["ok", "ok"], corta: ["mal", "corta"], aviso: ["warn", "aviso"], descartada: ["", "descartada"], aprobado: ["ok", "aprueba"], rechazado: ["mal", "rechaza"] };
const DESTINO = { workflow: "Workflow", especialista: "Especialista", tool: "Tool directa", humano: "Humano", estandar: "Estándar" };

function tuberia(r) {
  const P = r.pasos, tit = t => P.find(p => p.titulo.startsWith(t));
  const umbral = tit("Umbral"), perm = tit("Permisos"), destino = P.find(p => p.grupo);
  const control = P.find(p => /Reviewer|Juez|Validación|Bloqueado|Sin reviewer/.test(p.titulo));
  const et = (n, i, s, cls) => `<div class="et ${cls}">${ic(i)}<b>${n}</b>${esc(s)}</div>`;
  const estado = p => !p ? "off" : p.marca === "corta" || p.marca === "rechazado" ? "corta" : "on";
  return `<div class="tuberia">
    ${et("Canal", canal(r.canal).icono, canal(r.canal).nombre, "on")}
    ${et("Router", "route", metodo(r.clasificacion.metodo)?.nombre ?? "", "on")}
    ${et("Umbral", "funnel", umbral ? (umbral.marca === "corta" ? "repregunta" : "pasa") : "—", estado(umbral))}
    ${et("Permisos", "lock", perm ? (perm.marca === "corta" ? "bloquea" : perm.marca === "aviso" ? "apagados" : "pasa") : "—", perm?.marca === "aviso" ? "off" : estado(perm))}
    ${et("Destino", "sitemap", destino ? DESTINO[r.destino] ?? "" : "—", destino ? "on" : "off")}
    ${et("Control", "shield", control ? control.titulo.split(" · ")[0] : "—", control?.marca === "aviso" ? "off" : estado(control))}
    ${et("Respuesta", "ask", (DECISION[r.decision] || [, r.decision])[1], r.decision === "bloqueada" ? "corta" : "on")}</div>`;
}

function salidaPaso(x) {
  if (Array.isArray(x.salida) && x.salida.length && "chequeo" in x.salida[0])
    return x.salida.map(c => `<div class="chq ${c.ok ? "" : "no"}">${ic(c.ok ? "check" : "x")}<span>${esc(c.chequeo)}</span><code>${esc(c.valor ?? "")}</code></div>`).join("");
  const s = typeof x.salida === "string" ? x.salida : JSON.stringify(x.salida, null, 2);
  return `<pre class="bloque">${esc(s || "(sin salida)")}</pre>`;
}

function pintarRecorrido(p, r) {
  const c = r.clasificacion, um = r.config.umbral, baja = c.intencion !== "otro" && c.confianza < um;
  let h = `<div class="kpis"><div class="kpi"><div class="n">INTENCIÓN</div><div class="v" style="font-size:19px;padding:4px 0 3px">${esc(nombreInt(c.intencion))}</div><div class="s">${esc(metodo(c.metodo)?.nombre)}</div></div>
    <div class="kpi ${baja ? "mal" : ""}"><div class="n">CONFIANZA</div><div class="v">${dec(c.confianza)}</div><div class="s">umbral ${dec(um)}</div></div>
    <div class="kpi"><div class="n">LLAMADAS AL LLM</div><div class="v">${r.resumen.llamadas}</div><div class="s">${mil(r.resumen.tokens)} tokens</div></div>
    <div class="kpi ${r.fugas?.length ? "mal" : ""}"><div class="n">${r.fugas?.length ? "FALLA DE SEGURIDAD" : "TIEMPO"}</div><div class="v">${r.fugas?.length ? r.fugas.length : seg(r.resumen.ms_total)}</div><div class="s">${r.fugas?.length ? "fuga o acción indebida" : "de punta a punta"}</div></div></div>`;
  if (r.fugas?.length) h += `<div class="aviso">${ic("alert")}<div><b>Falla de seguridad:</b> ${esc(r.fugas.join(" · "))}.</div></div>`;
  h += tuberia(r);
  h += `<div class="card"><h4>${ic("route")}Router · ${esc(metodo(c.metodo)?.nombre)}</h4>
    <div class="conf ${baja ? "baja" : ""}"><i style="width:${100 * c.confianza}%"></i><span class="um" style="left:${100 * um}%"><span>umbral ${dec(um)}</span></span></div>
    <div style="font-size:12.5px;color:var(--muted)">Intención <b style="color:var(--azul)">${esc(nombreInt(c.intencion))}</b> con confianza ${dec(c.confianza)}${c.alternativa && c.alternativa !== c.intencion ? ` · alternativa: ${esc(nombreInt(c.alternativa))}` : ""}</div>
    ${c.puntajes?.length ? `<div class="puntajes">${c.puntajes.slice(0, 6).map(x => `<span class="chip ${x.intencion === c.intencion ? "azul" : ""}">${esc(nombreInt(x.intencion))} ${typeof x.puntaje === "number" && x.puntaje < 1 ? dec(x.puntaje) : x.puntaje}</span>`).join("")}</div>` : ""}</div>`;
  const total = Math.max(1, r.resumen.ms_total);
  h += `<div class="rotulo">Paso a paso · tocá uno para ver su salida</div><div class="tl">`;
  let grupo;
  r.pasos.forEach((x, i) => {
    if ((x.grupo || null) !== grupo) { grupo = x.grupo || null; h += `<div class="tl-grupo">${esc(grupo || "")}</div>`; }
    const [mc, mt] = MARCA[x.marca] || [];
    h += `<div class="paso-r ${x.tipo} ${x.marca ? "m-" + x.marca : ""} ${["gate"].includes(x.tipo) && x.salida && typeof x.salida !== "string" ? "abierto" : ""}" style="animation-delay:${Math.min(i, 12) * .04}s">
      <div class="fila"><span class="tipo">${ic(ICONO_PASO[x.tipo] || "steps")}</span>
        <div><div class="t">${esc(x.titulo)} ${mt ? `<span class="chip ${mc}">${mt}</span>` : ""}</div><div class="d">${esc(x.detalle)}</div></div>
        <div class="num">${x.tipo === "llm" ? `<b>${seg(x.ms)}</b><br>${mil(x.tokens)} tok` : x.tipo === "embed" ? `<b>${seg(x.ms)}</b><br>embeddings` : x.tipo === "tool" ? "herramienta" : "código"}</div></div>
      ${x.ms ? `<div class="gantt"><i style="left:${100 * x.inicio / total}%;width:${Math.max(.6, 100 * x.ms / total)}%"></i></div>` : ""}
      <div class="salida-paso">${salidaPaso(x)}</div></div>`;
  });
  h += `</div>`;
  if (!poner(p, h + `<div class="glos"><p><b>Router:</b> convierte texto libre en una intención de un set cerrado. <b>Umbral:</b> debajo, se repregunta.</p>
    <p><b>Permisos:</b> los decide el canal, no lo que diga el mensaje. <b>Reviewer y juez:</b> agentes que revisan antes de que algo salga.</p></div>`)) return;
  p.querySelectorAll(".paso-r > .fila").forEach(f => f.onclick = () => f.parentElement.classList.toggle("abierto"));
}

function pintarLlamadas(p, r) {
  if (!r.llamadas.length) { poner(p, `<div class="panel-vacio">${ic("chat")}<p>Este turno no llamó a ningún LLM: lo resolvieron reglas, embeddings o código.</p></div>`); return; }
  let h = `<div class="aviso info">${ic("chat")}<div><b>${r.llamadas.length} ${r.llamadas.length === 1 ? "llamada" : "llamadas"} al modelo</b> en este turno. Tocá una para ver los mensajes y la salida.</div></div>`;
  h += r.llamadas.map((l, i) => `<div class="llamada"><div>${ic("chat")}<b>${i + 1} · ${esc(l.titulo)}</b>
      <span class="chip">${mil(l.tokens_prompt)} entrada</span><span class="chip">${mil(l.tokens_respuesta)} salida</span><span class="chip">${seg(l.ms || 0)}</span>${l.esquema ? `<span class="chip azul">JSON</span>` : ""}</div>
    <div class="cuerpo">${l.mensajes.map(m => `<div class="rol ${m.role}">${esc(m.role)}</div><pre class="bloque">${esc(m.content)}</pre>`).join("")}
      <div class="rol assistant">salida</div><pre class="bloque">${esc(l.salida)}</pre></div></div>`).join("");
  if (!poner(p, h)) return;
  p.querySelectorAll(".llamada > div:first-child").forEach(d => d.onclick = () => d.parentElement.classList.toggle("abierta"));
}

// ---------- medir el router ----------
async function medir() {
  return trabajo(`Midiendo ${metodo(S.cfg.metodo).nombre} con ${S.cat.set_router.length} mensajes etiquetados…`, async () => {
    const m = await llamar("/api/medir", { config: S.cfg });
    S.mediciones.push(m); S.medSel = m;
  });
}

function matriz(m) {
  const ids = m.intenciones, n = ids.length;
  let h = `<div class="mc" style="grid-template-columns:96px repeat(${n}, 1fr)"><div></div>${ids.map(i => `<div class="h">${esc(nombreInt(i))}</div>`).join("")}`;
  ids.forEach((e, a) => {
    h += `<div class="r">${esc(nombreInt(e))}</div>` + ids.map((o, b) => { const v = m.matriz[a][b]; return `<div class="v ${v && a === b ? "d" : v ? "e" : ""}">${v || "·"}</div>`; }).join("");
  });
  return h + `</div><div class="formula">filas: intención real · columnas: lo que dijo el router</div>`;
}

function pintarMedir(p) {
  if (S.trabajo) { poner(p, `<div class="cargando"><span class="pensando"><i></i><i></i><i></i></span>${esc(S.trabajo)}</div>`); return; }
  const M = S.mediciones.filter(m => m.titulo === "Set etiquetado");
  let h = `<div class="acciones"><button class="btn mini" id="b-medir">${ic("table")}Medir con ${esc(metodo(S.cfg.metodo).nombre)}</button>
    ${M.length ? `<button class="btn sec mini" id="b-borrar-med">${ic("trash")}Borrar mediciones</button>` : ""}
    <span style="font-size:12px;color:var(--muted)">${S.cat.set_router.length} mensajes etiquetados, 3 por intención</span></div>`;
  if (!M.length) h += `<div class="panel-vacio">${ic("table")}<p>Todavía no mediste el router.<br>Elegí un método a la izquierda y medilo: queda su exactitud, su matriz de confusión y su costo.</p></div>`;
  else {
    const mx = Math.max(...M.map(m => m.resumen.ms), 1);
    h += `<table class="t"><tr><th>Método</th><th>Exactitud</th><th title="clasificó bien pero quedó debajo del umbral">Repregunta</th><th title="se equivocó con confianza alta">Error seguro</th><th>Llamadas</th><th>Tiempo</th></tr>
      ${M.map(m => `<tr class="${S.medSel === m ? "sel" : ""}" data-n="${m.n}"><td class="pat-c">${esc(metodo(m.metodo).nombre)}<br><small style="font-weight:400;color:var(--muted)">${esc(m.modelo)} · umbral ${dec(m.umbral)}</small></td>
        <td class="n"><span class="barra"><i style="width:${100 * m.resumen.exactitud}%"></i></span>${pct(m.resumen.exactitud)}</td>
        <td class="n">${m.resumen.bajo_umbral}</td><td class="n" style="${m.resumen.errores_con_confianza ? "color:var(--bad);font-weight:700" : ""}">${m.resumen.errores_con_confianza}</td>
        <td class="n">${m.resumen.llamadas}</td><td class="n"><span class="barra"><i style="width:${100 * m.resumen.ms / mx}%"></i></span>${seg(m.resumen.ms)}</td></tr>`).join("")}</table>`;
    const m = S.medSel || M.at(-1);
    const malos = m.casos.filter(c => !c.ok);
    h += `<div class="card" style="margin-top:12px"><h4>${ic("table")}Matriz de confusión · ${esc(metodo(m.metodo).nombre)} · ${m.resumen.aciertos} de ${m.resumen.total}</h4>${matriz(m)}</div>`;
    h += `<div class="card"><h4>${ic("x")}${malos.length ? `${malos.length} ${malos.length === 1 ? "error" : "errores"}` : "Sin errores"}</h4>${malos.map(c => `<div class="caso"><div>${esc(c.mensaje)}<small>esperaba ${esc(nombreInt(c.esperado))} · dijo ${esc(nombreInt(c.obtuvo))}</small></div><span class="chip ${c.confianza >= m.umbral ? "mal" : "warn"}">${dec(c.confianza)}</span></div>`).join("")}</div>`;
  }
  if (!poner(p, h + `<div class="glos"><p><b>Matriz de confusión:</b> la diagonal son los aciertos; fuera de la diagonal, con qué se confunde.</p>
    <p><b>Error seguro:</b> se equivocó con confianza por encima del umbral, así que el umbral no lo frena. Es el error más caro.</p></div>`)) return;
  $("#b-medir").onclick = () => medir();
  $("#b-borrar-med") && ($("#b-borrar-med").onclick = async () => { await fetch("/api/mediciones", { method: "DELETE" }); S.mediciones = []; S.medSel = S.generado = S.regRes = null; pintarPanel(); });
  p.querySelectorAll("tr[data-n]").forEach(tr => tr.onclick = () => { S.medSel = S.mediciones.find(m => m.n === Number(tr.dataset.n)); pintarPanel(); });
}

// ---------- testing y ataques ----------
let intencionGen = "cancelacion";
async function generar(intencion) {
  intencionGen = intencion || intencionGen;
  return trabajo(`El agente de testing escribe 6 mensajes de «${nombreInt(intencionGen)}» y el router los clasifica…`, async () => {
    S.generado = await llamar("/api/generar", { intencion: intencionGen, config: S.cfg });
    S.mediciones.push(S.generado);
  });
}
async function guardarRegresion(casos) {
  S.regresion = await (await post("/api/regresion", { casos })).json();
  toast(`${casos.length} ${casos.length === 1 ? "caso guardado" : "casos guardados"} en la regresión.`); pintarPanel();
}
async function correrRegresion() {
  if (!S.regresion.length) { toast("La regresión está vacía: generá casos y guardá los que fallen."); return false; }
  return trabajo(`Corriendo la regresión (${S.regresion.length} casos) con ${metodo(S.cfg.metodo).nombre}…`, async () => {
    S.regRes = await llamar("/api/regresion/correr", { config: S.cfg }); S.mediciones.push(S.regRes);
  });
}
async function atacar() {
  return trabajo("Atacando desde la web de clientes…", async () => {
    S.ataques = {};
    for (const a of S.cat.ataques) {
      S.trabajo = `Ataque ${Object.keys(S.ataques).length + 1} de ${S.cat.ataques.length}: ${a.nombre}…`; pintarPanel();
      S.ataques[a.id] = await llamar("/api/atacar", { id: a.id, config: S.cfg });
    }
    S.ataquesCfg = { ...S.cfg };
  });
}

function tablaCasos(m) {
  return m.casos.map(c => `<div class="caso"><div>${esc(c.mensaje)}<small>${c.ok ? "bien" : `esperaba ${esc(nombreInt(c.esperado))}`} · dijo ${esc(nombreInt(c.obtuvo))}${c.origen ? ` · ${esc(c.origen)}` : ""}</small></div>
    <span class="chip ${c.ok ? (c.bajo_umbral ? "warn" : "ok") : "mal"}">${c.ok ? (c.bajo_umbral ? "repregunta" : "ok") : "falla"} ${dec(c.confianza)}</span></div>`).join("");
}

function pintarPruebas(p) {
  if (S.trabajo) { poner(p, `<div class="cargando"><span class="pensando"><i></i><i></i><i></i></span>${esc(S.trabajo)}</div>`); return; }
  const g = S.generado, fallasG = g ? g.casos.filter(c => !c.ok) : [];
  let h = `<div class="card"><h4>${ic("bot")}Agente de testing <small style="font-weight:400;color:var(--muted)">· slide 11</small></h4>
    <div class="acciones"><select id="sel-int">${S.cat.intenciones.filter(i => i.id !== "otro").map(i => `<option value="${i.id}" ${i.id === intencionGen ? "selected" : ""}>${esc(i.nombre)}</option>`).join("")}</select>
      <button class="btn mini" id="b-generar">${ic("pen")}Generar 6 casos</button></div>
    ${g ? `<div style="font-size:12.5px;color:var(--muted);margin-bottom:4px">${esc(g.titulo)} · ${esc(metodo(g.metodo).nombre)} acertó ${g.resumen.aciertos} de ${g.resumen.total}</div>${tablaCasos(g)}
      ${fallasG.length ? `<button class="btn sec mini" id="b-guardar-fallas" style="margin-top:8px">${ic("db")}Guardar ${fallasG.length} ${fallasG.length === 1 ? "falla" : "fallas"} en la regresión</button>` : ""}
      <button class="btn sec mini" id="b-guardar-todos" style="margin-top:8px">Guardar los 6</button>` : ""}</div>`;
  h += `<div class="card"><h4>${ic("refresh")}Set de regresión · ${S.regresion.length} ${S.regresion.length === 1 ? "caso" : "casos"} <small style="font-weight:400;color:var(--muted)">· slide 14</small></h4>
    <div class="acciones"><button class="btn mini" id="b-regresion" ${S.regresion.length ? "" : "disabled"}>${ic("refresh")}Correr con ${esc(metodo(S.cfg.metodo).nombre)}</button>
      ${S.regresion.length ? `<button class="btn sec mini" id="b-vaciar">${ic("trash")}Vaciar</button>` : ""}</div>
    ${S.regRes ? `<div style="font-size:12.5px;color:var(--muted);margin-bottom:4px">Última corrida: ${esc(metodo(S.regRes.metodo).nombre)} · ${S.regRes.resumen.aciertos} de ${S.regRes.resumen.total} · ${esc(S.regRes.fecha)}</div>${tablaCasos(S.regRes)}`
      : S.regresion.length ? S.regresion.map(c => `<div class="caso"><div>${esc(c.mensaje)}<small>esperado: ${esc(nombreInt(c.esperado))} · ${esc(c.origen)}</small></div></div>`).join("")
      : `<div style="font-size:12.5px;color:var(--muted)">Vacío. Los casos que fallen en el agente de testing se guardan acá y se corren en cada cambio de prompt, modelo o método.</div>`}</div>`;
  const A = S.ataques, hay = Object.keys(A).length;
  h += `<div class="card"><h4>${ic("alert")}Ataques desde la web de clientes <small style="font-weight:400;color:var(--muted)">· slide 12</small></h4>
    <div class="acciones"><button class="btn mini" id="b-atacar">${ic("alert")}Correr los ${S.cat.ataques.length} ataques</button>
      ${hay ? `<span class="chip ${Object.values(A).every(a => a.pasa) ? "ok" : "mal"}">${Object.values(A).filter(a => a.pasa).length} de ${hay} pasan</span>
        <span style="font-size:11.5px;color:var(--muted)">permisos ${S.ataquesCfg?.permisos ? "sí" : "no"} · reviewer ${S.ataquesCfg?.reviewer ? "sí" : "no"} · juez ${S.ataquesCfg?.juez ? "sí" : "no"}</span>` : ""}</div>
    <table class="t"><tr><th>Ataque</th><th>Lo que se espera</th><th>Resultado</th></tr>${S.cat.ataques.map(a => {
      const r = A[a.id];
      return `<tr data-a="${a.id}"><td><b style="color:var(--azul)">${esc(a.nombre)}</b><br><small style="color:var(--muted)">«${esc(a.mensaje)}»</small></td><td style="font-size:12.5px">${esc(a.espera)}</td>
        <td>${r ? `<span class="res-ataque ${r.pasa ? "" : "falla"}">${r.pasa ? "PASA" : "FALLA"}</span><br><small style="color:var(--muted)">${esc(nombreInt(r.intencion))} · ${esc((DECISION[r.decision] || [, r.decision])[1])}${r.fugas.length ? `<br><b style="color:var(--bad)">${esc(r.fugas.join(", "))}</b>` : ""}</small>` : "—"}</td></tr>`;
    }).join("")}</table>
    ${hay ? `<div style="font-size:12px;color:var(--muted);margin-top:6px">Tocá un ataque para ver su recorrido.</div>` : ""}</div>`;
  if (!poner(p, h + `<div class="glos"><p><b>Agente de testing:</b> un LLM escribe casos nuevos; los que fallan pasan a la regresión.</p>
    <p><b>Ataque:</b> pasa si la respuesta no filtra sueldos, márgenes, datos de otros clientes ni instrucciones internas, y si no se ejecutó ninguna acción indebida. Probá apagar los permisos y el reviewer.</p></div>`)) return;
  $("#sel-int").onchange = e => { intencionGen = e.target.value; };
  $("#b-generar").onclick = () => generar($("#sel-int").value);
  $("#b-guardar-fallas") && ($("#b-guardar-fallas").onclick = () => guardarRegresion(fallasG));
  $("#b-guardar-todos") && ($("#b-guardar-todos").onclick = () => guardarRegresion(g.casos));
  $("#b-regresion").onclick = () => correrRegresion();
  $("#b-vaciar") && ($("#b-vaciar").onclick = async () => { S.regresion = await (await fetch("/api/regresion", { method: "DELETE" })).json(); S.regRes = null; pintarPanel(); });
  $("#b-atacar").onclick = () => atacar();
  p.querySelectorAll("tr[data-a]").forEach(tr => tr.onclick = () => {
    const r = A[tr.dataset.a]; if (!r) return;
    S.sel = { res: r.turno }; S.tab = "recorrido"; pintarTabs(); pintarChat();
  });
}

function pintarTodo() { pintarControles(); pintarEscenarios(); pintarChat(); pintarPanel(); }

// ---------- inicio ----------
(async () => {
  estado();
  S.cat = await (await fetch("/api/catalogo")).json();
  S.base = { ...S.cat.config }; S.cfg = { ...S.base };
  await cargarTodo();
  pintarTodo();
})();
