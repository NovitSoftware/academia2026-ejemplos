// MesaYa · Memoria del agente — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
// Redibuja solo si cambió: así las animaciones de entrada no se reinician en cada consulta.
function poner(el, html) { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; }
const espera = ms => new Promise(r => setTimeout(r, ms));
const fecha = iso => iso ? new Date(iso.length > 10 ? iso : iso + "T12:00").toLocaleDateString("es-AR", { day: "numeric", month: "short" }) : "—";

const S = { cfg: {}, base: {}, usuario: "lucia", vista: null, chat: { lucia: [], martin: [] }, sel: null, tab: "memoria",
            esc: null, corrida: null, ocupado: false, catalogo: null, nuevas: new Set() };

// ---------- controles ----------
function pintarControles() {
  $$("[data-k]").forEach(el => {
    const k = el.dataset.k, v = S.cfg[k];
    if (el.classList.contains("seg")) el.querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.v === v));
    else if (el.type === "checkbox") el.checked = !!v;
    else el.value = v;
  });
  $("#v-k").textContent = S.cfg.k;
}
$$("[data-k]").forEach(el => {
  const k = el.dataset.k;
  if (el.classList.contains("seg")) el.addEventListener("click", e => { const b = e.target.closest("button"); if (b) { S.cfg[k] = b.dataset.v; pintarControles(); } });
  else el.addEventListener("input", () => { S.cfg[k] = el.type === "checkbox" ? el.checked : el.type === "range" ? Number(el.value) : el.value; pintarControles(); });
});

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
function toast(t) { const d = document.createElement("div"); d.className = "toast"; d.textContent = t; document.body.append(d); setTimeout(() => d.remove(), 4500); }

// ---------- usuarios, sesión y reloj ----------
function pintarUsuarios() {
  const U = S.catalogo.usuarios;
  $("#usuarios").innerHTML = Object.entries(U).map(([id, u]) =>
    `<div class="usr ${S.usuario === id ? "on" : ""}" data-u="${id}"><span class="avatar ${id === "martin" ? "m" : ""}">${u.inicial}</span>${esc(u.nombre.split(" ")[0])}</div>`).join("");
  $$(".usr").forEach(el => el.onclick = () => cambiarUsuario(el.dataset.u));
  const u = U[S.usuario];
  $("#cab-avatar").textContent = u.inicial; $("#cab-avatar").className = "avatar" + (S.usuario === "martin" ? " m" : "");
  $("#cab-nombre").textContent = u.nombre;
}
async function cambiarUsuario(id) {
  S.usuario = id; S.sel = null;
  S.vista = await (await fetch(`/api/sesion/${id}`)).json();
  pintarTodo();
}
function pintarSesion() {
  const v = S.vista; if (!v) return;
  $("#ses-n").textContent = `Sesión ${v.sesion}`;
  $("#ses-msg").textContent = v.turnos.length ? `${v.turnos.length} mensajes en esta conversación` : "sin mensajes todavía";
  $("#hoy").textContent = fecha(v.reloj.hoy);
  $("#dias").textContent = v.reloj.dias ? `+${v.reloj.dias} días` : "";
  const n = v.memoria.activas.length;
  $("#cab-mem").innerHTML = `${ic("db")}${n} ${n === 1 ? "recuerdo" : "recuerdos"}${v.pendientes ? " · escribiendo…" : ""}`;
}
async function nuevaSesion() {
  S.vista = await (await post("/api/sesion/nueva", { usuario: S.usuario })).json();
  S.chat[S.usuario].push({ tipo: "sep", texto: `Sesión ${S.vista.sesion} · conversación nueva` });
  pintarTodo();
}
async function avanzarReloj(dias) {
  const r = await (await post("/api/reloj", { usuario: S.usuario, dias })).json();
  S.vista = r;
  const vencidas = r.vencidas.map(m => m.texto);
  S.chat[S.usuario].push({ tipo: "reloj", texto: dias ? `Pasaron ${dias} días${vencidas.length ? " · venció: " + vencidas.join(" · ") : ""}` : "El reloj vuelve a hoy" });
  pintarTodo();
}
$("#nueva-sesion").onclick = nuevaSesion;
$("#mas30").onclick = () => avanzarReloj(30);
$("#hoy-btn").onclick = () => avanzarReloj(0);
$("#reiniciar").onclick = async () => { await reiniciarTodo(); toast("Memoria, sesiones y reloj reiniciados."); };
async function reiniciarTodo() {
  S.vista = await (await post("/api/reiniciar", { usuario: S.usuario })).json();
  S.chat = { lucia: [], martin: [] }; S.sel = null; S.nuevas.clear();
  pintarTodo();
}

// ---------- escenarios ----------
const NOMBRES_PASO = { sesion: "Nueva sesión", reloj: d => `Pasan ${d.dias} días`, usuario: d => `Cambiar a ${S.catalogo.usuarios[d.id].nombre.split(" ")[0]}`, precargar: () => "Cargar una conversación de 14 mensajes" };
function textoPaso(p) {
  if (p.tipo === "mensaje") return `«${p.texto}»`;
  const n = NOMBRES_PASO[p.tipo]; return typeof n === "function" ? n(p) : n;
}
function pintarEscenarios() {
  const C = S.corrida;
  const html = S.catalogo.escenarios.map(e => {
    const activo = C && C.esc === e.id;
    return `<div class="esc ${S.esc === e.id ? "abierto" : ""}" data-e="${e.id}">
      <button><span class="letra">${e.id}</span><span><div class="nom">${esc(e.titulo)}</div><div class="sl">Slides ${e.slides}</div></span></button>
      <div class="cuerpo"><p class="idea">${esc(e.idea)}</p>
        <div class="vars">${e.variantes.map((v, i) => `<button class="var ${v.bien ? "bien" : ""} ${activo && C.v === i ? "hecho" : ""}" data-i="${i}">${v.bien ? ic("check") : ""}${esc(v.nombre)}</button>`).join("")}</div>
        ${activo ? `<div class="pasos">${e.pasos.map((p, i) => `<div class="paso ${i < C.paso ? "hecho" : i === C.paso ? "actual" : ""} ${i === C.paso && S.ocupado ? "corriendo" : ""} ${p.tipo !== "mensaje" ? "evento" : ""}">
            <span class="nro">${i < C.paso ? "✓" : i + 1}</span><span>${esc(textoPaso(p))}</span></div>`).join("")}</div>
          <div class="acciones-esc"><button class="btn mini" id="paso-sig" ${C.paso >= e.pasos.length || S.ocupado ? "disabled" : ""}>${ic("steps")}Siguiente paso</button>
          <button class="btn sec mini" id="paso-todos" ${C.paso >= e.pasos.length || S.ocupado ? "disabled" : ""}>Correr todos</button></div>` : ""}
      </div></div>`;
  }).join("");
  if (!poner($("#escenarios"), html)) return;
  $$(".esc > button").forEach(b => b.onclick = () => { const id = b.parentElement.dataset.e; S.esc = S.esc === id ? null : id; pintarEscenarios(); });
  $$(".var").forEach(b => b.onclick = () => empezarVariante(b.closest(".esc").dataset.e, Number(b.dataset.i)));
  $("#paso-sig") && ($("#paso-sig").onclick = () => correrPaso());
  $("#paso-todos") && ($("#paso-todos").onclick = async () => { const e = escActual(); while (S.corrida.paso < e.pasos.length) { if (!await correrPaso()) break; } });
}
const escActual = () => S.catalogo.escenarios.find(e => e.id === S.corrida.esc);

async function empezarVariante(id, v) {
  if (S.ocupado) return;
  const e = S.catalogo.escenarios.find(x => x.id === id);
  S.usuario = "lucia";
  await reiniciarTodo();
  S.cfg = { ...S.base, ...e.variantes[v].config }; pintarControles();
  S.corrida = { esc: id, v, paso: 0 };
  S.tab = e.panel; pintarTabs();
  pintarEscenarios();
}

async function correrPaso() {
  const e = escActual(), p = e.pasos[S.corrida.paso];
  if (!p || S.ocupado) return false;
  S.ocupado = true; pintarEscenarios();
  let ok = true;
  if (p.tipo === "mensaje") ok = await enviar(p.texto, true);
  else if (p.tipo === "sesion") await nuevaSesion();
  else if (p.tipo === "reloj") await avanzarReloj(p.dias);
  else if (p.tipo === "usuario") { await cambiarUsuario(p.id); S.chat[p.id].push({ tipo: "sep", texto: `Ahora escribe ${S.catalogo.usuarios[p.id].nombre}` }); }
  else if (p.tipo === "precargar") {
    S.vista = await (await post("/api/precargar", { usuario: S.usuario, conversacion: p.conversacion })).json();
    S.chat[S.usuario] = [{ tipo: "sep", texto: `Conversación precargada · ${S.vista.turnos.length} mensajes` },
      ...S.vista.turnos.map(t => ({ tipo: t.rol === "cliente" ? "u" : "a", texto: t.texto, precargado: true }))];
  }
  S.ocupado = false;
  if (ok) S.corrida.paso++;
  pintarTodo();
  return ok;
}

// ---------- chat ----------
$("#enviar").onclick = () => { const t = $("#mensaje").value.trim(); if (t) enviar(t); };
$("#mensaje").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#enviar").click(); } });

async function enviar(texto, desdeEscenario = false) {
  if (S.ocupado && !desdeEscenario) return false;
  S.ocupado = true; $("#enviar").disabled = true; $("#mensaje").value = "";
  const usuario = S.usuario, chat = S.chat[usuario];
  const item = { tipo: "a", cargando: true };
  chat.push({ tipo: "u", texto }, item); S.sel = chat.length - 1; pintarChat(); pintarPanel();
  let ok = true;
  try {
    const r = await post("/api/turno", { usuario, mensaje: texto, config: S.cfg });
    const d = await r.json();
    if (!r.ok) throw new Error(d.detail || r.statusText);
    Object.assign(item, { texto: d.traza.respuesta.texto, traza: d.traza, i: d.indice, cargando: false });
    S.vista = d.vista; marcarNuevas(d.traza);
    if (d.traza.escritura.pendiente) await esperarEscritura(usuario);
  } catch (e) { Object.assign(item, { cargando: false, error: e.message }); toast(e.message); ok = false; }
  if (!desdeEscenario) S.ocupado = false;
  $("#enviar").disabled = false;
  pintarTodo();
  return ok;
}

function marcarNuevas(traza) {
  S.nuevas = new Set((traza.escritura.decisiones || []).filter(c => c.id && ["GUARDAR", "ACTUALIZAR"].includes(c.decision)).map(c => c.id));
}

async function esperarEscritura(usuario) {
  // Escritura en segundo plano: la respuesta ya llegó; la memoria se completa sola unos segundos después.
  pintarTodo();
  for (let n = 0; n < 120; n++) {
    await espera(1200);
    const v = await (await fetch(`/api/sesion/${usuario}`)).json();
    for (const it of S.chat[usuario]) if (it.traza && v.trazas[it.i]) it.traza.escritura = v.trazas[it.i].escritura;
    if (usuario === S.usuario) S.vista = v;
    const ult = S.chat[usuario].filter(x => x.traza).pop();
    if (ult) marcarNuevas(ult.traza);
    pintarTodo();
    if (!v.pendientes) return;
  }
}

function chipsTurno(t) {
  const out = [];
  const usados = (t.recuerdos || []).filter(m => m.entra);
  if (!t.config.memoria) out.push(`<span class="chip">${ic("ban")}sin memoria</span>`);
  else out.push(`<span class="chip ${usados.length ? "azul" : ""}">${ic("db")}${usados.length} ${usados.length === 1 ? "recuerdo" : "recuerdos"}</span>`);
  const w = t.escritura;
  if (w.pendiente) out.push(`<span class="chip warn">${ic("pen")}escribiendo memoria…</span>`);
  else {
    const dec = w.decisiones || [];
    const g = dec.filter(c => ["GUARDAR", "ACTUALIZAR"].includes(c.decision)).length, b = dec.filter(c => c.decision === "BLOQUEAR").length, n = dec.filter(c => c.decision === "NO GUARDAR").length;
    if (g) out.push(`<span class="chip ok">${ic("check")}guardó ${g}</span>`);
    if (n) out.push(`<span class="chip">${ic("x")}descartó ${n}</span>`);
    if (b) out.push(`<span class="chip mal">${ic("shield")}bloqueó ${b}</span>`);
  }
  if (t.olvidadas?.length) out.push(`<span class="chip warn">${ic("trash")}olvidó ${t.olvidadas.length}</span>`);
  if (t.herramienta) out.push(`<span class="chip">${ic(t.herramienta.nombre === "leer_resenas" ? "globe" : "trash")}${t.herramienta.nombre}</span>`);
  if (t.estado?.paso === "confirmada" && t.cambios_estado?.includes("paso")) out.push(`<span class="chip grad">${ic("filecheck")}reserva ${esc(t.estado.codigo)}</span>`);
  out.push(`<span class="chip">${ic("clock")}${(t.ms_total / 1000).toFixed(1)} s</span>`);
  if (t.grabado) out.push(`<span class="chip vio">Grabado ${esc(t.grabado)}</span>`);
  return out.join("");
}

function pintarChat() {
  const m = $("#mensajes"), chat = S.chat[S.usuario];
  if (!chat.length) {
    poner(m, `<div class="vacio"><img src="/static/isotipo-blanco.png" alt=""><h2>Recordar es decidir</h2>
      <p>Elegí un escenario y corré sus pasos, o escribile al asistente. A la derecha se ve qué recuerda, qué decide guardar y qué olvida.</p></div>`);
    return;
  }
  const html = chat.map((it, i) => {
    if (it.tipo === "sep") return `<div class="separador">${esc(it.texto)}</div>`;
    if (it.tipo === "reloj") return `<div class="separador reloj-sep">${esc(it.texto)}</div>`;
    if (it.tipo === "u") return `<div class="msg-u" ${it.precargado ? 'style="opacity:.75"' : ""}>${esc(it.texto)}</div>`;
    const t = it.traza;
    return `<div class="msg-a ${S.sel === i ? "sel" : ""} ${it.error ? "err" : ""}" data-i="${i}" ${it.precargado ? 'style="opacity:.75;cursor:default"' : ""}>
      ${t ? `<div class="meta">${chipsTurno(t)}</div>` : ""}
      <div class="txt">${it.cargando ? `<span class="pensando"><i></i><i></i><i></i></span>` : esc(it.error || it.texto)}</div>
      ${t && t.recuerdos.some(r => r.entra) ? `<div class="usados">${t.recuerdos.filter(r => r.entra).map(r => `<span class="chip ${r.fuente !== "usuario" ? "mal" : "ok"}" title="similitud ${r.sim}">${esc(r.texto)}</span>`).join("")}</div>` : ""}
    </div>`;
  }).join("");
  if (!poner(m, html)) return;
  $$(".msg-a").forEach(el => el.onclick = () => { const i = Number(el.dataset.i); if (!S.chat[S.usuario][i].traza) return; S.sel = i; pintarChat(); pintarPanel(); });
  m.scrollTop = m.scrollHeight;
}

// ---------- pestañas ----------
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); pintarPanel(); }
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); });
function trazaSel() {
  const chat = S.chat[S.usuario];
  const it = (S.sel != null && chat[S.sel]?.traza) ? chat[S.sel] : chat.filter(x => x.traza).pop();
  return it;
}
function pintarPanel() {
  const p = $("#panel"); if (!S.vista) return;
  const it = trazaSel();
  if (S.tab === "memoria") return pintarMemoria(p);
  if (S.tab === "estado") return pintarEstado(p, it?.traza);
  if (!it) { poner(p, `<div class="panel-vacio">Todavía no hay turnos de ${esc(S.catalogo.usuarios[S.usuario].nombre.split(" ")[0])}.</div>`); return; }
  if (it.cargando) { poner(p, `<div class="panel-vacio"><span class="pensando"><i></i><i></i><i></i></span><p>Recuperando, analizando y respondiendo…</p></div>`); return; }
  if (it.error) { poner(p, `<div class="aviso">${ic("alert")}<div>${esc(it.error)}</div></div>`); return; }
  (S.tab === "turno" ? pintarTurno : pintarContexto)(p, it.traza);
}

const TIPOS = { semantica: ["Semántica", "qué sé del cliente", "notes"], episodica: ["Episódica", "qué pasó", "calendar"], procedimental: ["Procedimental", "cómo lo atiendo", "steps"] };

function tarjetaMemoria(m, pend = false) {
  const ext = m.fuente !== "usuario";
  const vence = m.ttl_dias ? (m.dias_para_vencer <= 0 ? "vencida" : `vence en ${m.dias_para_vencer} días`) : "sin vencimiento";
  const json = { texto: m.texto, tipo: m.tipo, categoria: m.categoria, origen: m.origen, fuente: m.fuente, confianza: m.confianza,
                 importancia: m.importancia, alcance: m.alcance, creada: m.creada?.slice(0, 10), ultimo_uso: m.ultimo_uso?.slice(0, 10) ?? null,
                 usos: m.usos, ttl_dias: m.ttl_dias, version: m.reemplaza_id ? `reemplaza a #${m.reemplaza_id}` : 1 };
  return `<div class="mem ${ext ? "externa" : ""} ${pend ? "pend" : ""} ${S.nuevas.has(m.id) ? "nueva" : ""}" data-id="${m.id}">
    <div class="botones"><button data-json title="Ver metadatos">${ic("code")}</button>${pend ? "" : `<button data-borrar title="Olvidar">${ic("trash")}</button>`}</div>
    <div class="txt">${esc(m.texto)}</div>
    <div class="chips"><span class="chip">${esc(m.categoria)}</span><span class="chip ${m.origen === "explicita" ? "ok" : "warn"}">${esc(m.origen)}</span>
      ${ext ? `<span class="chip mal">${ic("globe")}fuente externa</span>` : `<span class="chip">${ic("person")}lo dijo el cliente</span>`}
      ${m.reemplaza_id ? `<span class="chip azul">${ic("merge")}v2 de #${m.reemplaza_id}</span>` : ""}${S.nuevas.has(m.id) ? `<span class="chip grad">nueva</span>` : ""}</div>
    <div class="meta"><span class="conf">confianza <span class="barra"><i style="width:${(m.confianza || 0) * 100}%"></i></span> ${(m.confianza || 0).toFixed(2)}</span>
      <span>${ic("clock")} ${vence}</span><span>usada ${m.usos} ${m.usos === 1 ? "vez" : "veces"}</span><span>recencia ${m.recencia}</span></div>
    <pre class="bloque" hidden>${esc(JSON.stringify(json, null, 2))}</pre></div>`;
}

function pintarMemoria(p) {
  const M = S.vista.memoria, act = M.activas;
  const ext = act.filter(m => m.fuente !== "usuario").length;
  let h = `<div class="kpis"><div class="kpi"><div class="n">RECUERDOS ACTIVOS</div><div class="v">${act.length}</div><div class="s">${esc(S.catalogo.usuarios[S.usuario].nombre.split(" ")[0])}</div></div>
    <div class="kpi"><div class="n">CON VENCIMIENTO</div><div class="v">${act.filter(m => m.ttl_dias).length}</div><div class="s">TTL de episodios</div></div>
    <div class="kpi"><div class="n">OLVIDADOS</div><div class="v">${M.historial.length}</div><div class="s">vencidos, borrados o reemplazados</div></div>
    <div class="kpi ${ext ? "mal" : ""}"><div class="n">DE FUENTE EXTERNA</div><div class="v">${ext}</div><div class="s">${ext ? "memoria envenenada" : "ninguno"}</div></div></div>`;
  if (S.vista.pendientes) h += `<div class="aviso info">${ic("pen")}<div><b>Escribiendo en segundo plano.</b> La respuesta ya llegó; los recuerdos de este turno aparecen en unos segundos.</div></div>`;
  if (!act.length && !M.historial.length) h += `<div class="panel-vacio">${ic("db")}<p>La memoria de largo plazo está vacía.<br>Lo que se guarde va a aparecer acá, con sus metadatos.</p></div>`;
  for (const [k, [nom, sub, i]] of Object.entries(TIPOS)) {
    const lista = act.filter(m => m.tipo === k);
    if (!lista.length) continue;
    h += `<div class="grupo"><h4><span class="icb">${ic(i)}</span>${nom} <small>· ${sub}</small></h4>${lista.map(m => tarjetaMemoria(m)).join("")}</div>`;
  }
  if (M.historial.length) h += `<div class="rotulo">Historial · lo que se olvidó o se reemplazó</div>` +
    M.historial.map(m => `<div class="hist">${ic(m.estado === "reemplazada" ? "merge" : "trash")}<div><s>${esc(m.texto)}</s><br>${esc(m.motivo_estado)}</div></div>`).join("");
  if (!poner(p, h + `<div class="glos"><p><b>Semántica:</b> lo que sé. <b>Episódica:</b> lo que pasó. <b>Procedimental:</b> cómo lo hago.</p>
    <p><b>Cada recuerdo tiene su ficha:</b> origen, fuente, confianza, fechas y vencimiento. Tocá ${ic("code")} para ver el JSON.</p></div>`)) return;
  p.querySelectorAll("[data-json]").forEach(b => b.onclick = () => { const pre = b.closest(".mem").querySelector("pre"); pre.hidden = !pre.hidden; });
  p.querySelectorAll("[data-borrar]").forEach(b => b.onclick = async () => {
    S.vista = await (await fetch(`/api/memoria/${S.usuario}/${b.closest(".mem").dataset.id}`, { method: "DELETE" })).json(); pintarTodo();
  });
}

function pintarTurno(p, t) {
  const et = n => t.etapas.find(e => e.nombre.startsWith(n));
  const w = t.escritura;
  const pasos = [
    ["Recuperar", "search", t.config.memoria ? `${t.recuerdos.filter(r => r.entra).length} de ${t.recuerdos.length}` : "apagado", t.config.memoria, et("Recuperar")],
    ["Analizar", "kv", t.analisis.intencion, true, et("Analizar")],
    ["Herramienta", "bolt", t.herramienta ? t.herramienta.nombre.replace("_", " ") : "ninguna", !!t.herramienta, null],
    ["Responder", "chat", `${t.respuesta.tokens_prompt || "—"} tok`, true, et("Responder")],
    ["Escribir", "pen", { caliente: "en caliente", segundo_plano: w.pendiente ? "en curso…" : "2.º plano", apagada: "apagado", olvido: "no aplica" }[w.modo], !["apagada", "olvido"].includes(w.modo), et("Escribir")],
  ];
  let h = `<div class="pipe">${pasos.map(([n, i, s, on, e], k) => `<div class="et ${on ? "on" : "off"}" style="animation-delay:${k * .07}s">${ic(i)}<b>${n}</b>${esc(s)}${e ? `<br>${(e.ms / 1000).toFixed(1)} s` : ""}</div>`).join("")}</div>`;
  // 1. recuperación
  h += `<div class="card"><h4>${ic("search")}Memorias candidatas <small style="font-weight:400;color:var(--muted)">· puntaje = 0,6 similitud + 0,25 importancia + 0,15 recencia</small></h4>`;
  if (!t.config.memoria) h += `<div style="font-size:13px;color:var(--muted)">Memoria de largo plazo apagada: el agente solo ve esta conversación.</div>`;
  else if (!t.recuerdos.length) h += `<div style="font-size:13px;color:var(--muted)">No hay recuerdos guardados de este cliente.</div>`;
  else h += `<table class="t"><tr><th>Recuerdo</th><th>Similitud</th><th>Recencia</th><th>Puntaje</th><th></th></tr>${t.recuerdos.map(r => `<tr class="${r.entra ? "" : "fuera"}">
      <td>${esc(r.texto)} ${r.fuente !== "usuario" ? `<span class="chip mal">externa</span>` : ""}</td><td>${r.sim.toFixed(2)}</td><td>${r.recencia}</td>
      <td><span class="barra"><i style="width:${Math.min(100, r.puntaje * 100)}%"></i></span> ${r.puntaje.toFixed(2)}</td>
      <td>${r.entra ? `<span class="chip ok">entra</span>` : `<span class="chip">afuera</span>`}</td></tr>`).join("")}</table>`;
  h += `</div>`;
  // 3. herramienta
  if (t.herramienta?.nombre === "leer_resenas") {
    h += `<div class="card"><h4>${ic("globe")}El agente leyó reseñas de ${esc(t.herramienta.args.sucursal)}</h4><div class="resenas">${t.herramienta.texto.split("\n").map(l =>
      `<p>${esc(l).replace(/(NOTA PARA EL ASISTENTE DE IA:.*?)(?=»)/, "<mark>$1</mark>")}</p>`).join("")}</div>
      <div class="aviso" style="margin:8px 0 0">${ic("alert")}<div>Una reseña trae <b>instrucciones escondidas</b>. El texto llega al extractor de memoria marcado como <b>contenido externo</b>.</div></div></div>`;
  }
  if (t.olvidadas?.length || t.herramienta?.nombre === "olvidar_memoria")
    h += `<div class="card"><h4>${ic("trash")}Olvido a pedido</h4>${t.olvidadas.length ? t.olvidadas.map(o => `<div class="hist">${ic("trash")}<div><s>${esc(o.texto)}</s> · similitud ${o.sim}</div></div>`).join("") : `<div style="font-size:13px;color:var(--muted)">No había recuerdos parecidos a «${esc(t.analisis.olvidar)}».</div>`}</div>`;
  // 5. escritura
  h += `<div class="card"><h4>${ic("pen")}¿Vale la pena recordarlo?</h4>`;
  if (w.modo === "apagada") h += `<div style="font-size:13px;color:var(--muted)">La memoria está apagada: no se escribe nada.</div>`;
  else if (w.modo === "olvido") h += `<div style="font-size:13px;color:var(--muted)">${esc(w.nota)}</div>`;
  else if (w.pendiente) h += `<div style="font-size:13px;color:var(--muted)"><span class="pensando"><i></i><i></i><i></i></span> Escribiendo en segundo plano: el cliente ya tiene su respuesta.</div>`;
  else if (!w.decisiones.length) h += `<div style="font-size:13px;color:var(--muted)">El extractor no encontró nada para recordar en este mensaje.</div>`;
  else h += w.decisiones.map((c, i) => `<div class="dec ${c.decision.replace(" ", "_")}" style="animation-delay:${i * .08}s">
      <span class="tag">${ic({ GUARDAR: "check", ACTUALIZAR: "merge", REFORZAR: "refresh", "NO GUARDAR": "x", BLOQUEAR: "shield" }[c.decision])}${c.decision}</span>
      <div><div class="t">${esc(c.texto)}</div>
        <div class="chips" style="display:flex;gap:4px;flex-wrap:wrap;margin-top:4px"><span class="chip">${esc(c.tipo)}</span><span class="chip">${esc(c.origen)}</span>
          <span class="chip ${c.fuente === "usuario" ? "" : "mal"}">${c.por_host ? "cierre de tarea" : c.fuente === "usuario" ? "lo dijo el cliente" : "contenido externo"}</span>
          ${c.confianza != null ? `<span class="chip">confianza ${Number(c.confianza).toFixed(2)}</span>` : ""}</div>
        <div class="por">${esc(c.motivo_politica)}</div></div></div>`).join("");
  h += `</div>`;
  poner(p, h + `<div class="glos"><p><b>Estado vs. memoria:</b> lo de esta conversación va al estado; a la memoria solo pasa lo estable y útil después.</p>
    <p><b>La procedencia la pone el host:</b> lo que viene de una reseña no puede escribir como si lo hubiera dicho el cliente.</p></div>`);
}

const COLORES = { sistema: "#0D0B92", memoria: "#3398DC", estado: "#1B4BB9", herramienta: "#85067B", resumen: "#510371", historial: "#4F8ED5", mensaje: "#2C037B" };
function pintarContexto(p, t) {
  const partes = t.partes, total = partes.reduce((a, x) => a + x.tokens, 0) || 1, c = t.conversacional;
  let h = `<div class="card"><h4>${ic("layers")}La ventana de este turno · ~${total} tokens estimados · ${t.respuesta.tokens_prompt || "—"} reales</h4>
    <div class="apilado">${partes.filter(x => x.tokens).map(x => `<i style="flex:${x.tokens};background:${COLORES[x.tipo]}" title="${esc(x.titulo)}">${x.tokens > total * .08 ? x.tokens : ""}</i>`).join("")}</div>
    <div style="font-size:12.5px;color:var(--muted)">Memoria conversacional: <b>${{ completa: "conversación completa", ventana: "ventana de 4 mensajes", resumen: "resumen + ventana de 4" }[c.estrategia]}</b>
      · ${c.turnos_literales} de ${c.turnos_totales} mensajes literales${c.descartados ? ` · <b style="color:var(--bad)">${c.descartados} mensajes fuera de la ventana</b>` : ""}</div></div>`;
  if (c.resumen) h += `<div class="aviso info">${ic("notes")}<div><b>Resumen de lo anterior:</b> ${esc(c.resumen)}</div></div>`;
  h += partes.map(x => `<div class="comp ${x.tokens ? "" : "vacio"}"><div><span class="sw-col" style="background:${COLORES[x.tipo]}"></span><b>${esc(x.titulo)}</b><span class="chip">${x.tokens} tok</span></div>
    <pre class="bloque">${esc(x.texto || "(vacío)")}</pre></div>`).join("");
  if (!poner(p, h + `<div class="glos"><p><b>Ventana:</b> barata, pero olvida el principio. <b>Resumen + ventana:</b> conserva los datos clave con pocos tokens.</p>
    <p><b>La memoria también ocupa contexto:</b> por eso entran solo los mejores k recuerdos.</p></div>`)) return;
  p.querySelectorAll(".comp > div").forEach(d => d.onclick = () => d.parentElement.classList.toggle("abierto"));
}

function pintarEstado(p, t) {
  const v = S.vista, e = v.estado;
  const fases = [["inicio", "Inicio"], ["datos", "Pide datos"], ["confirmar", "Confirma"], ["confirmada", "Cerrada"]];
  const iFase = fases.findIndex(f => f[0] === e.paso);
  let h = `<div class="dos"><div class="card" style="margin:0"><h4>${ic("kv")}Estado de la tarea</h4><div style="font-size:12px;color:var(--muted)">Corto plazo · sesión ${v.sesion}</div></div>
    <div class="card" style="margin:0"><h4>${ic("db")}Memoria</h4><div style="font-size:12px;color:var(--muted)">Largo plazo · ${v.memoria.activas.length} recuerdos</div></div></div>`;
  h += `<div class="card" style="margin-top:10px"><h4>${e.tarea ? "Reserva en curso" : "Sin tarea en curso"}${e.paso === "confirmada" ? ` <span class="sello">${ic("filecheck")}${esc(e.codigo)}</span>` : ""}</h4>
    <div class="fase">${fases.map(([k, n], i) => `<span class="${i <= iFase && e.tarea ? "on" : ""}">${n}</span>`).join("")}</div>
    <div class="slots">${[["sucursal", "Sucursal"], ["fecha", "Fecha"], ["hora", "Hora"], ["personas", "Personas"], ["sector", "Sector"]].map(([k, n]) =>
      `<div class="slot ${e[k] ? "lleno" : ""}">${n}<b>${esc(k === "fecha" && e[k] ? fecha(e[k]) : e[k] ?? "—")}</b></div>`).join("")}</div>
    <pre class="bloque">${esc(JSON.stringify(e, null, 2))}</pre></div>`;
  if (t && t.cambios_estado?.length) h += `<div class="aviso info">${ic("refresh")}<div>Este turno cambió: <b>${t.cambios_estado.join(", ")}</b>. ${t.estado.sector && t.config.memoria && t.recuerdos.some(r => r.entra && /terraza|interior|adentro/i.test(r.texto)) ? "El pedido de hoy pisa el estado, no la preferencia guardada." : ""}</div></div>`;
  if (v.archivados.length) h += `<div class="rotulo">Estados archivados al cerrar cada sesión</div>` + v.archivados.slice().reverse().map(a => `<div class="hist">${ic("filecheck")}<div>
      <b>Sesión ${a.sesion}</b> · ${a.turnos} mensajes · ${a.estado.tarea ? `reserva ${esc(a.estado.paso)}${a.estado.codigo ? " " + esc(a.estado.codigo) : ""} · ${esc([a.estado.sucursal, a.estado.fecha && fecha(a.estado.fecha), a.estado.hora, a.estado.personas && a.estado.personas + " personas", a.estado.sector].filter(Boolean).join(", "))}` : "sin tarea"}</div></div>`).join("");
  poner(p, h + `<div class="glos"><p><b>Estado:</b> se pisa, se completa y se archiva al cerrar la sesión.</p>
    <p><b>Memoria:</b> se consolida, se versiona y se olvida. Separarlos evita que un pedido de hoy se vuelva una preferencia para siempre.</p></div>`);
}

function pintarTodo() { pintarUsuarios(); pintarSesion(); pintarEscenarios(); pintarChat(); pintarPanel(); }

// ---------- inicio ----------
(async () => {
  estado();
  S.catalogo = await (await fetch("/api/catalogo")).json();
  S.base = { ...S.catalogo.config }; S.cfg = { ...S.base };
  pintarControles();
  S.vista = await (await fetch(`/api/sesion/${S.usuario}`)).json();
  pintarTodo();
})();
