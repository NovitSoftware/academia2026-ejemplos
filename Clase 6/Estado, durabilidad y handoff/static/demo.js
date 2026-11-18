// MesaYa · Estado, durabilidad y handoff — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
function poner(el, html) { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; }
const seg = ms => (ms / 1000).toFixed(1).replace(".", ",") + " s";
const mil = n => Number(n || 0).toLocaleString("es-AR");
const plata = n => "$" + mil(n);
const json = o => JSON.stringify(o, null, 2);

const S = { cat: null, cfg: {}, fallas: {}, base: {}, baseFallas: {}, chat: [], sel: null, tab: "tarea", esc: null, hecho: {},
            ocupado: false, datos: null, filtro: null };
const agente = id => S.cat.agentes.find(a => a.id === id) ?? { nombre: id, icono: "bot" };

async function llamar(url, body) {
  const r = await post(url, body), d = await r.json();
  if (!r.ok) throw new Error(d.detail || r.statusText);
  return d;
}
function toast(t) { const d = document.createElement("div"); d.className = "toast"; d.textContent = t; document.body.append(d); setTimeout(() => d.remove(), 4500); }

// ---------- controles y fallas ----------
function pintarControles() {
  $$("[data-k]").forEach(el => el.checked = !!S.cfg[el.dataset.k]);
  $$("[data-f]").forEach(el => { const v = S.fallas[el.dataset.f]; if (el.type === "checkbox") el.checked = !!v; else el.value = String(v ?? ""); });
}
$$("[data-k]").forEach(el => el.addEventListener("input", () => { S.cfg[el.dataset.k] = el.checked; }));
$$("[data-f]").forEach(el => el.addEventListener("input", () => {
  S.fallas[el.dataset.f] = el.type === "checkbox" ? el.checked : el.dataset.f === "caida_tras" ? Number(el.value) : el.value;
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
          const k = `${e.id}${i}`, t = c.accion === "reanudar" ? "Reanudar la tarea interrumpida" : (c.mensajes || [c.mensaje]).join("\n");
          return `<button class="var ${c.bien ? "bien" : ""} ${S.hecho[k] === "corriendo" ? "corriendo" : S.hecho[k] ? "hecho" : ""}" data-i="${i}" title="${esc(t)}">${c.bien ? ic("check") : ""}${c.accion === "reanudar" ? ic("refresh") : ""}${esc(c.nombre)}</button>`;
        }).join("")}</div></div></div>`).join("");
  if (!poner($("#escenarios"), html)) return;
  $$(".esc > button").forEach(b => b.onclick = () => { const id = b.parentElement.dataset.e; S.esc = S.esc === id ? null : id; pintarEscenarios(); });
  $$(".var").forEach(b => b.onclick = () => correrEscenario(b.closest(".esc").dataset.e, Number(b.dataset.i)));
}

async function correrEscenario(id, i) {
  if (S.ocupado) return;
  const e = S.cat.escenarios.find(x => x.id === id), c = e.corridas[i], k = `${id}${i}`;
  S.cfg = { ...S.base, ...(c.config || {}) };
  S.fallas = { ...S.baseFallas, ...(c.fallas || {}) };
  pintarControles();
  if (c.reiniciar) { await reiniciar(); }
  S.hecho[k] = "corriendo"; pintarEscenarios();
  let ok = true;
  if (c.accion === "reanudar") ok = await reanudar();
  else for (const m of c.mensajes || [c.mensaje]) { ok = await enviar(m); if (!ok) break; }
  S.tab = c.panel || e.panel; pintarTabs(); pintarPanel();
  S.hecho[k] = ok; pintarEscenarios();
  // Las fallas se inyectan una vez: después de la corrida vuelven a "ninguna".
  S.fallas = { ...S.baseFallas }; pintarControles();
}

// ---------- chat ----------
$("#enviar").onclick = () => { const t = $("#mensaje").value.trim(); if (t) enviar(t); };
$("#mensaje").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#enviar").click(); } });
$("#reanudar").onclick = () => reanudar();
$("#reiniciar").onclick = async () => { await reiniciar(); S.hecho = {}; pintarEscenarios(); };

async function reiniciar() {
  await fetch("/api/datos", { method: "DELETE" });
  S.chat = []; S.sel = null; S.filtro = null;
  await cargarDatos(); pintarTodo();
}

async function cargarDatos() { S.datos = await (await fetch("/api/datos")).json(); }

async function cargarTodo() {
  await cargarDatos();
  const turnos = S.datos.turnos;
  S.chat = [];
  if (turnos.length) S.chat.push({ tipo: "sep", texto: `${turnos.length} ${turnos.length === 1 ? "turno guardado" : "turnos guardados"} · desde ${turnos[0].fecha}` });
  for (const r of turnos) S.chat.push(r.accion === "reanudar" ? { tipo: "r" } : { tipo: "u", texto: r.mensaje }, { tipo: "a", res: r });
  S.sel = turnos.at(-1) || null;
}

async function correr(item, f) {
  if (S.ocupado) return false;
  S.ocupado = true; $("#enviar").disabled = true;
  const a = { tipo: "a", cargando: true };
  S.chat.push(item, a); pintarChat();
  let ok = true;
  try { a.res = await f(); S.sel = a.res; }
  catch (e) { a.error = e.message; toast(e.message); ok = false; }
  a.cargando = false;
  S.ocupado = false; $("#enviar").disabled = false;
  await cargarDatos();
  pintarTodo();
  return ok;
}

function enviar(texto) {
  $("#mensaje").value = "";
  return correr({ tipo: "u", texto }, () => llamar("/api/turno", { mensaje: texto, config: S.cfg, fallas: S.fallas }));
}
function reanudar() { return correr({ tipo: "r" }, () => llamar("/api/reanudar", { config: S.cfg })); }

const RESULTADO = { completada: ["ok", "completada"], interrumpida: ["mal", "interrumpida"], esperando: ["warn", "esperando al cliente"],
                    derivada: ["vio", "derivada"], fallida: ["mal", "falló · escalada"] };

function pintarChat() {
  const el = $("#mensajes");
  if (!S.chat.length) {
    poner(el, `<div class="vacio"><img src="/static/isotipo-blanco.png" alt=""><h2>Una reserva que sobrevive</h2>
      <p>Pedí una mesa como ${esc(S.cat.cliente)}, o elegí un escenario a la izquierda. Probá apagar los checkpoints o inyectar una falla.</p></div>`);
    $("#reanudar").hidden = true;
    return;
  }
  const html = S.chat.map((m, i) => {
    if (m.tipo === "sep") return `<div class="separador">${esc(m.texto)}</div>`;
    if (m.tipo === "u") return `<div class="msg-u">${esc(m.texto)}</div>`;
    if (m.tipo === "r") return `<div class="msg-r">${ic("refresh")}Reanudar</div>`;
    if (m.cargando) return `<div class="msg-a"><span class="pensando"><i></i><i></i><i></i></span></div>`;
    if (m.error) return `<div class="msg-a err"><div class="txt">${esc(m.error)}</div></div>`;
    const r = m.res, [cls, txt] = RESULTADO[r.resultado] ?? ["", r.resultado];
    const meta = `<div class="meta"><span class="chip ${cls}">${esc(txt)}</span><span class="chip azul mono">${esc(r.tarea.correlation_id)}</span>
      <span class="chip">${r.en_disco ? ic("file") + "en disco" : "nada en disco"}</span>
      <span class="chip">${r.resumen.llamadas} LLM · ${r.resumen.api} API</span>${r.grabado ? `<span class="chip">${ic("clock")}grabado</span>` : ""}</div>`;
    const resp = r.respuestas.length ? r.respuestas : [{ de: "sistema", texto: "(sin respuesta)" }];
    return resp.map((x, j) => `<div class="msg-a ${x.de} ${S.sel === r ? "sel" : ""}" data-i="${i}">
      <div class="de">${ic(agente(x.de).icono)}${esc(agente(x.de).nombre)}</div><div class="txt">${esc(x.texto)}</div>${j === resp.length - 1 ? meta : ""}</div>`).join("");
  }).join("");
  if (poner(el, html)) {
    $$(".msg-a[data-i]").forEach(d => d.onclick = () => { S.sel = S.chat[Number(d.dataset.i)].res; pintarChat(); pintarPanel(); });
    el.scrollTop = el.scrollHeight;
  }
  const ult = S.chat.filter(m => m.res).at(-1)?.res;
  $("#reanudar").hidden = !(ult && ult.resultado === "interrumpida");
}

// ---------- panel derecho ----------
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); }
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); pintarPanel(); });

function pintarPanel() {
  const p = $("#panel");
  const f = { tarea: panelTarea, sistema: panelSistema, handoff: panelHandoff, disco: panelDisco, llamadas: panelLlamadas }[S.tab];
  poner(p, f());
  $$(".paso-r > .fila").forEach(x => x.onclick = () => x.parentElement.classList.toggle("abierto"));
  $$(".llamada > div:first-child").forEach(x => x.onclick = () => x.parentElement.classList.toggle("abierta"));
  $$("[data-cid]").forEach(x => x.onclick = () => { S.filtro = S.filtro === x.dataset.cid ? null : x.dataset.cid; pintarPanel(); });
  $$("[data-reanudar]").forEach(x => x.onclick = () => reanudar());
  const sel = $("#filtro-cid"); if (sel) sel.onchange = () => { S.filtro = sel.value || null; pintarPanel(); };
}

const vacio = t => `<div class="panel-vacio">${t}</div>`;

// --- Tarea: los cinco pasos, el estado y el recorrido
function estadoPasos(r) {
  const t = r.tarea, n = t.paso_actual, comp = (t.compensadas || []).join(" ");
  const caida = r.pasos.find(p => p.tipo === "caida");
  const caidoTras = caida ? n - 1 : null;
  return S.cat.pasos.map((p, i) => {
    const k = i + 1, grupo = r.pasos.filter(x => x.grupo?.startsWith(`Paso ${k} `));
    let e = "";
    if (r.pasos.some(x => x.tipo === "saltado" && x.titulo.includes(p.nombre))) e = "saltado";
    else if (grupo.some(x => x.marca === "corta")) e = "fallo";
    else if (k < n || grupo.some(x => ["llm", "api"].includes(x.tipo) && x.marca !== "error")) e = "hecho";
    if ((p.id === "cobrar_sena" && comp.includes("seña")) || (p.id === "bloquear_mesa" && comp.includes("mesa"))) e = "compensado";
    const cp = (r.en_disco?.checkpoints || []).some(c => c.n === k);
    return { ...p, k, e, cp, caidaDespues: caidoTras === k };
  });
}

function tuberia(r) {
  return `<div class="tuberia">${estadoPasos(r).map(p => `<div class="et ${p.e}" title="${p.efecto ? "Acción con efecto" : "Sin efecto"}">
      ${p.cp ? `<span class="cp" title="Checkpoint en disco">${ic("check")}</span>` : ""}${ic(p.icono)}<b>${esc(p.nombre)}</b>${p.efecto ? "con efecto" : p.tipo.toUpperCase()}</div>
      ${p.caidaDespues ? `<div class="caida" title="El proceso se cayó acá">✗</div>` : ""}`).join("")}</div>
    <div class="leyenda"><span><i style="background:var(--azul-1)"></i>hecho</span><span><i style="background:var(--accent-soft)"></i>salteado al reanudar</span>
      <span><i style="background:var(--violeta-3)"></i>falla</span><span><i style="background:var(--bad-soft)"></i>compensado</span><span>${ic("check")} checkpoint en disco</span></div>`;
}

function recorrido(pasos, total) {
  if (!pasos.length) return "";
  const ICONO = { llm: "bot", api: "db", checkpoint: "file", gate: "funnel", espera: "clock", caida: "x", handoff: "users", saltado: "refresh", codigo: "code" };
  let grupo = null, html = "";
  for (const p of pasos) {
    if ((p.grupo || null) !== grupo) { grupo = p.grupo || null; if (grupo) html += `<div class="tl-grupo">${esc(grupo)}</div>`; }
    const iz = total ? 100 * p.inicio / total : 0, an = total ? Math.max(0.6, 100 * p.ms / total) : 0;
    const num = [p.ms ? `<b>${seg(p.ms)}</b>` : "", p.tokens ? `${mil(p.tokens)} tok` : ""].filter(Boolean).join("<br>");
    const salida = p.salida == null ? "" : typeof p.salida === "string" ? p.salida : json(p.salida);
    html += `<div class="paso-r ${p.tipo} ${p.marca ? "m-" + p.marca : ""}"><div class="fila"><span class="tipo">${ic(ICONO[p.tipo] || "code")}</span>
      <div><div class="t">${esc(p.titulo)}</div><div class="d">${esc(p.detalle)}</div></div><div class="num">${num}</div></div>
      ${p.ms ? `<div class="gantt"><i style="left:${iz}%;width:${an}%"></i></div>` : ""}
      ${salida ? `<div class="salida-paso"><pre class="bloque">${esc(salida)}</pre></div>` : ""}</div>`;
  }
  return html + `<div class="eje"><span>0 s</span><span>${seg(total)} (reloj simulado: incluye timeouts y esperas)</span></div>`;
}

function panelTarea() {
  const r = S.sel;
  if (!r) return vacio("Mandá un mensaje o corré un escenario: acá se ve la tarea paso a paso, su estado de ejecución y lo que quedó en disco.");
  const t = r.tarea, d = r.en_disco;
  const disco = d ? { ...d } : null;
  if (disco) delete disco.handoff;
  const ent = (t.entrada || []).map(m => `<li>${esc(m)}</li>`).join("");
  const datos = Object.entries(t.datos || {}).filter(([k]) => k !== "nombre").map(([k, v]) => `<b>${esc(k)}</b><span>${v == null ? "—" : esc(v)}</span>`).join("");
  return `${tuberia(r)}
    <div class="dos" style="margin-bottom:12px">
      <div class="card estado-card"><h4>${ic("file")}Estado de ejecución<span class="chip ${d ? "azul" : "mal"}">${d ? "en disco" : "solo en memoria"}</span></h4>
        ${d ? `<pre class="bloque">${esc(json(disco))}</pre>` : `<p style="font-size:13px;color:var(--muted);margin:0">No hay nada en <code>datos/tareas/${esc(t.correlation_id)}.json</code>: lo que estaba en memoria se perdió con la caída.</p>
          <pre class="bloque" style="margin-top:8px;opacity:.6">${esc(json({ paso_actual: t.paso_actual, acciones_hechas: t.acciones_hechas, estado: t.estado }))}</pre>`}</div>
      <div class="card"><h4>${ic("chat")}Estado conversacional</h4>
        <div class="kv">${datos || "<span>—</span>"}</div>
        <div class="rol" style="margin-top:10px">Mensajes del pedido</div><ol class="entrada-lista">${ent}</ol>
        ${r.resultado === "interrumpida" ? `<button class="btn mini" data-reanudar style="margin-top:10px">${ic("refresh")}Reanudar</button>` : ""}</div>
    </div>
    <div class="rotulo">Recorrido del turno</div>${recorrido(r.pasos, r.resumen.ms_total)}
    <div class="glos"><p><b>Checkpoint:</b> foto del estado guardada en un punto seguro. <b>Reanudar:</b> cargar el último checkpoint y correr solo lo que falta.</p>
      <p><b>Acciones con efecto</b> (mesa, seña, reserva): quedan en <code>acciones_hechas</code> para no repetirlas.</p></div>`;
}

// --- Sistema de reservas: lo que pasó de verdad, con los duplicados marcados
function duplicados(lista, activo) {
  const n = {};
  for (const x of lista.filter(activo)) { const k = `${x.cliente}|${x.fecha}|${x.hora}`; n[k] = (n[k] || 0) + 1; }
  return x => activo(x) && n[`${x.cliente}|${x.fecha}|${x.hora}`] > 1;
}

function panelSistema() {
  const s = S.datos?.sistema;
  if (!s) return vacio("Cargando…");
  const mesaAct = m => m.estado === "bloqueada", cobAct = c => c.estado === "cobrado", resAct = r => r.estado === "confirmada";
  const dm = duplicados(s.mesas, mesaAct), dc = duplicados(s.cobros, cobAct), dr = duplicados(s.reservas, resAct);
  const dups = s.mesas.filter(dm).length + s.cobros.filter(dc).length + s.reservas.filter(dr).length;
  const cobrado = s.cobros.filter(cobAct).reduce((a, c) => a + c.monto, 0);
  const chipE = e => `<span class="chip ${["bloqueada", "cobrado", "confirmada"].includes(e) ? "ok" : "mal"}">${esc(e)}</span>`;
  const tabla = (titulo, icono, lista, cols, dup, act) => `<div class="card"><h4>${ic(icono)}${titulo}</h4>${lista.length ? `<table class="t"><tr>${cols.map(c => `<th>${c[0]}</th>`).join("")}</tr>
    ${lista.map(x => `<tr class="${dup(x) ? "dup" : ""} ${act(x) ? "" : "off"}">${cols.map(c => `<td class="${c[2] || ""}">${c[1](x)}</td>`).join("")}</tr>`).join("")}</table>` : `<span style="color:var(--muted);font-size:13px">Nada todavía.</span>`}</div>`;
  const cid = x => `<code data-cid="${esc(x.correlation_id)}" style="cursor:pointer">${esc(x.correlation_id)}</code>`;
  return `<div class="kpis">
      <div class="kpi"><div class="n">Reservas</div><div class="v">${s.reservas.filter(resAct).length}</div><div class="s">confirmadas</div></div>
      <div class="kpi"><div class="n">Mesas bloqueadas</div><div class="v">${s.mesas.filter(mesaAct).length}</div><div class="s">${s.mesas.filter(m => !mesaAct(m)).length} liberadas</div></div>
      <div class="kpi"><div class="n">Señas cobradas</div><div class="v">${plata(cobrado)}</div><div class="s">${s.cobros.filter(c => !cobAct(c)).length} reembolsadas</div></div>
      <div class="kpi ${dups ? "mal" : ""}"><div class="n">Duplicados</div><div class="v">${dups}</div><div class="s">misma clienta, día y hora</div></div></div>
    ${dups ? `<div class="aviso">${ic("alert")}<span>Hay acciones repetidas: el mismo pedido bloqueó, cobró o reservó más de una vez. Es lo que pasa al reintentar o reanudar sin idempotencia ni checkpoints.</span></div>` : ""}
    ${tabla("Reservas", "calendar", s.reservas, [["Código", x => `<b>${esc(x.codigo)}</b>`], ["Sucursal", x => esc(x.sucursal)], ["Día y hora", x => `${esc(x.fecha)} ${esc(x.hora)}`],
      ["Pers.", x => esc(x.personas)], ["Correlation ID", cid], ["Estado", x => chipE(x.estado)]], dr, resAct)}
    <div class="dos" style="margin-bottom:12px">
    ${tabla("Mesas", "lock", s.mesas, [["Mesa", x => `<b>${esc(x.id)}</b>`], ["Correlation ID", cid], ["Estado", x => chipE(x.estado)]], dm, mesaAct)}
    ${tabla("Cobros de seña", "tag", s.cobros, [["Cobro", x => `<b>${esc(x.id)}</b> ${plata(x.monto)}`], ["Correlation ID", cid], ["Estado", x => chipE(x.estado)]], dc, cobAct)}
    </div>
    <div class="card"><h4>${ic("globe")}Pedidos a la API<span class="chip" style="margin-left:auto">X-Correlation-ID e Idempotency-Key</span></h4>
      ${s.pedidos.length ? `<table class="t pedidos"><tr><th>#</th><th>Operación</th><th>Correlation ID</th><th>Clave</th><th>Resultado</th></tr>
      ${s.pedidos.slice(-30).reverse().map(p => `<tr class="${p.ok ? "" : "mal"}"><td>${p.n}</td><td><b>${esc(p.operacion)}</b></td><td>${cid(p)}</td>
        <td>${p.clave ? `<code>${esc(p.clave.split(":")[1])}</code>` : `<span class="chip mal">sin clave</span>`}</td><td>${esc(p.resultado)}</td></tr>`).join("")}</table>` : `<span style="color:var(--muted);font-size:13px">Nada todavía.</span>`}</div>
    <div class="glos"><p><b>Idempotencia:</b> ejecutar dos veces da el mismo resultado que una. Con la clave, la API reconoce el pedido repetido y devuelve el original.</p>
      <p><b>Timeout:</b> se dejó de esperar la respuesta, aunque la acción quizá sí se hizo.</p></div>`;
}

// --- Handoff: el resumen que viaja y quién es el dueño
function panelHandoff() {
  const tareas = S.datos?.tareas || [];
  const r = S.sel;
  const t = r?.handoff ? r.tarea : [...tareas].reverse().find(x => x.handoff);
  const bandeja = S.datos?.bandeja || [];
  let html = "";
  if (t?.handoff) {
    const h = t.handoff, dueno = t.dueño;
    html += `<div class="duenos">${["agente_reservas", "agente_eventos", "humano_atencion"].map(a => `<div class="dueno ${dueno === a ? "on" : a === h.de ? "ex" : ""}">
        ${ic(agente(a).icono)}${esc(agente(a).nombre)}${dueno === a ? "<br>dueño de la tarea" : a === h.de ? "<br>deja de actuar" : ""}</div>`).join("")}</div>
      <div class="dos" style="margin-bottom:12px">
        <div class="card"><h4>${ic("users")}Handoff · <code>${esc(t.correlation_id)}</code></h4><pre class="bloque">${esc(json(h))}</pre></div>
        <div class="card"><h4>${ic("check")}¿Qué viaja?</h4>
          ${[["motivo", "Motivo", "el que recibe sabe qué se espera de él"], ["datos_confirmados", "Datos confirmados", "el cliente no repite fecha, hora y personas"],
             ["hecho", "Lo hecho", "no se repiten acciones"], ["pendiente", "Lo pendiente", "qué falta decidir"], ["correlation_id", "Correlation ID", "para seguir el rastro"]]
            .map(([k, n, s]) => `<div class="chk ${h[k] ? "" : "no"}">${ic(h[k] ? "check" : "x")}<span>${n}<small>${s}</small></span></div>`).join("")}
          ${h.mensaje ? `<div class="aviso" style="margin:10px 0 0">${ic("alert")}<span>Sin contexto: solo viaja el último mensaje. El agente nuevo tiene que volver a preguntar.</span></div>` : ""}</div>
      </div>`;
  } else html += vacio("Todavía no hubo ningún handoff. Probá el escenario D: «En realidad somos 40, es un cumpleaños».");
  html += `<div class="rotulo">Bandeja de atención al cliente</div>` + (bandeja.length ? [...bandeja].reverse().map(c => `<div class="caso">
      <div class="cab">${ic("person")}<b>${esc(c.cliente)}</b><code data-cid="${esc(c.correlation_id)}" style="cursor:pointer">${esc(c.correlation_id)}</code><span class="chip vio">${esc(c.estado)} · ${esc(c.recibido)}</span></div>
      <p>${esc(c.resumen)}</p><ul>${(c.mensajes || []).map(m => `<li>${esc(m)}</li>`).join("")}</ul></div>`).join("")
    : `<p style="color:var(--muted);font-size:13px">Vacía.</p>`);
  return html + `<div class="glos"><p><b>Handoff:</b> pasar la tarea a otro agente o a una persona. A otro agente, en JSON; a una persona, un resumen que se lee en 10 segundos.</p>
    <p><b>Dueño de la tarea:</b> el único que puede actuar sobre ella. Después del handoff, el agente anterior no contesta más.</p></div>`;
}

// --- Disco y log: las tareas guardadas, la cola y el log con correlation ID
function panelDisco() {
  const d = S.datos;
  if (!d) return vacio("Cargando…");
  const colgada = t => t.estado === "en_curso";
  const ids = [...new Set(d.log.map(l => l.correlation_id))];
  const log = d.log.filter(l => !S.filtro || l.correlation_id === S.filtro);
  return `<div class="card"><h4>${ic("file")}datos/tareas/</h4>${d.tareas.length ? `<table class="t"><tr><th>Correlation ID</th><th>Paso</th><th>Estado</th><th>Dueño</th><th>Actualizado</th></tr>
      ${d.tareas.map(t => `<tr><td><code data-cid="${esc(t.correlation_id)}" style="cursor:pointer">${esc(t.correlation_id)}</code></td><td>${Math.min(t.paso_actual, 5)} de 5</td>
        <td><span class="chip ${colgada(t) ? "mal" : t.estado === "completada" ? "ok" : "warn"}">${colgada(t) ? "colgada" : esc(t.estado)}</span>
        ${colgada(t) ? `<button class="btn mini" data-reanudar style="margin-left:6px">${ic("refresh")}Reanudar</button>` : ""}</td>
        <td>${esc(agente(t.dueño).nombre)}</td><td>${esc((t.actualizado || "").slice(11))}</td></tr>`).join("")}</table>` : `<span style="color:var(--muted);font-size:13px">Ninguna tarea en disco.</span>`}</div>
    <div class="card"><h4>${ic("layers")}Cola de entrada <span class="chip" style="margin-left:auto">pedidos que todavía no terminaron</span></h4>
      ${d.cola.length ? d.cola.map(c => `<div style="font-size:12.5px;margin-bottom:4px"><code>${esc(c.correlation_id)}</code> · ${(c.entrada || []).map(esc).join(" / ")}</div>`).join("") : `<span style="color:var(--muted);font-size:13px">Vacía.</span>`}</div>
    <div class="acciones"><b style="font-size:13px;color:var(--azul)">Log</b><select id="filtro-cid"><option value="">Todos los pedidos</option>
      ${ids.map(i => `<option ${S.filtro === i ? "selected" : ""}>${esc(i)}</option>`).join("")}</select>
      <span style="font-size:12px;color:var(--muted)">Tocá un correlation ID para filtrar.</span></div>
    <div class="log">${log.slice(-200).map(l => `<div class="${S.filtro === l.correlation_id ? "on" : ""}"><span class="h">${esc(l.hora)}</span>
      <span class="cid" data-cid="${esc(l.correlation_id)}">${esc(l.correlation_id)}</span><span class="comp">${esc(l.componente)}</span><span>${esc(l.texto)}</span></div>`).join("") || `<span style="color:var(--muted)">Vacío.</span>`}</div>
    <div class="glos"><p><b>Tarea colgada:</b> la que quedó "en_curso" en disco pero ya nadie la está corriendo: hay que retomarla o cancelarla.</p>
      <p><b>Correlation ID:</b> un ID único que acompaña al pedido por agentes, tools, logs y APIs.</p></div>`;
}

function panelLlamadas() {
  const r = S.sel;
  if (!r) return vacio("Las llamadas al modelo del turno elegido.");
  if (!r.llamadas.length) return vacio("Este turno no llamó al modelo: todo lo resolvió el código (checkpoints, API, reintentos y compensaciones).");
  return r.llamadas.map((l, i) => `<div class="llamada ${i === r.llamadas.length - 1 ? "abierta" : ""}"><div>${ic("bot")}<b>${esc(l.titulo)}</b>
      <span class="chip">${mil(l.tokens_prompt)} + ${mil(l.tokens_respuesta)} tok</span><span class="chip">${seg(l.ms)}</span>${l.esquema ? `<span class="chip azul">JSON</span>` : ""}</div>
      <div class="cuerpo">${l.mensajes.map(m => `<div class="rol ${m.role}">${esc(m.role)}</div><pre class="bloque">${esc(m.content)}</pre>`).join("")}
      <div class="rol assistant">salida</div><pre class="bloque">${esc(l.salida)}</pre></div></div>`).join("");
}

function pintarTodo() { pintarChat(); pintarPanel(); pintarEscenarios(); }

// ---------- inicio ----------
(async () => {
  S.cat = await (await fetch("/api/catalogo")).json();
  S.base = { ...S.cat.config }; S.baseFallas = { ...S.cat.fallas };
  S.cfg = { ...S.base }; S.fallas = { ...S.baseFallas };
  $("#cab-quien").textContent = `Escribe ${S.cat.cliente}, clienta`;
  pintarControles();
  await cargarTodo();
  pintarTodo();
  estado();
})();
