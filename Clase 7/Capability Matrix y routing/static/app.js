// MesaYa · Capability Matrix y Model Routing — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body || {}) });
function poner(el, html) { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; }
const seg = ms => ms == null ? "—" : (ms / 1000).toFixed(1).replace(".", ",") + " s";
const mil = n => Number(n || 0).toLocaleString("es-AR");
const usd = (x, d = 3) => "USD " + Number(x ?? 0).toFixed(d).replace(".", ",");
const pct = x => Math.round(100 * x) + " %";
const json = o => esc(JSON.stringify(o, null, 2));

const TIPOS = [["clasificar", "Clasificar"], ["extraer", "Extraer a JSON"], ["herramienta", "Elegir la tool"], ["razonar", "Razonar una política"]];
const S = { cat: null, est: null, ult: {}, tab: "matriz", esc: null, hecho: {}, ocupado: false, trabajo: null,
            derivacion: 0.5, tipo: "extraer", umbral: 0.75, p95: 6, estrategia: "reglas_cascada", llamada: null };
const perfil = id => S.cat.perfiles.find(p => p.id === id);

async function llamar(url, body) {
  const r = await post(url, body), d = await r.json();
  if (!r.ok) throw new Error(d.detail || r.statusText);
  return d;
}
function toast(t) { const d = document.createElement("div"); d.className = "toast"; d.textContent = t; document.body.append(d); setTimeout(() => d.remove(), 4500); }

// ---------- estado ----------
async function estado() {
  try {
    const e = S.est = await (await fetch("/api/estado")).json();
    const dot = $("#estado .dot"), txt = $("#estado-txt");
    const desc = Object.entries(e.ollama.descargas || {}).find(([, d]) => d.estado === "descargando");
    const nv = e.proveedores.find(p => p.id === "nvidia");
    $("#guion").checked = e.modo_guion;
    if (e.modo_guion) { dot.className = "dot ok"; txt.textContent = "Modo guion"; }
    else if (desc) { const [m, d] = desc; dot.className = "dot carga"; txt.textContent = `Descargando ${m}… ${d.total ? Math.round(100 * d.completado / d.total) : 0} %`; }
    else if (!e.ollama.conectado) { dot.className = "dot mal"; txt.textContent = e.ollama.error || "Sin Ollama"; }
    else if (!e.listo) { dot.className = "dot carga"; txt.textContent = `Preparando ${e.ollama.faltan.join(", ")}…`; }
    else { dot.className = "dot ok"; txt.textContent = `Ollama listo · NVIDIA ${nv?.tiene_clave ? "con clave" : "sin clave"}`; }
    setTimeout(estado, desc || !e.listo ? 1500 : 8000);
  } catch { $("#estado .dot").className = "dot mal"; $("#estado-txt").textContent = "Sin servidor"; setTimeout(estado, 3000); }
}
$("#guion").onchange = e => post("/api/config", { modo_guion: e.target.checked }).then(estado);

// ---------- controles ----------
function pintarControles() {
  poner($("#perfiles"), S.cat.perfiles.map(p => `<div class="perfil ${p.id}"><b>${esc(p.nombre)}</b><span class="m">${esc(p.modelo)}</span>
      <small>${p.proveedor === "nvidia" ? "NVIDIA" : "Ollama local"} · USD ${String(p.precio_entrada).replace(".", ",")} / ${String(p.precio_salida).replace(".", ",")} por millón (entrada / salida)</small>
      ${p.usa_respaldo ? `<small class="resp-local">Sin clave de NVIDIA: corre su respaldo local</small>` : ""}</div>`).join(""));
  $("#derivacion").value = S.derivacion; $("#v-derivacion").textContent = usd(S.derivacion, 2);
  $("#umbral").value = S.umbral; $("#v-umbral").textContent = pct(S.umbral);
  $("#p95").value = S.p95; $("#v-p95").textContent = S.p95 + " s";
  poner($("#tipos"), TIPOS.map(([id, n]) => `<button data-v="${id}" class="${S.tipo === id ? "on" : ""}" title="${n}">${n.split(" ")[0]}</button>`).join(""));
  $$("#tipos button").forEach(b => b.onclick = () => { S.tipo = b.dataset.v; pintarControles(); pintarPanel(); });
}
$("#derivacion").oninput = e => { S.derivacion = Number(e.target.value); pintarControles(); pintarPanel(); };
$("#umbral").oninput = e => { S.umbral = Number(e.target.value); pintarControles(); pintarPanel(); };
$("#p95").oninput = e => { S.p95 = Number(e.target.value); pintarControles(); pintarPanel(); };
$("#borrar").onclick = async () => { await fetch("/api/resultados", { method: "DELETE" }); S.ult = {}; S.hecho = {}; S.llamada = null; pintarTodo(); };

// ---------- escenarios ----------
function pintarEscenarios() {
  const html = S.cat.escenarios.map(e => `<div class="esc ${S.esc === e.id ? "abierto" : ""}" data-e="${e.id}">
      <button><span class="letra">${e.id}</span><span><div class="nom">${esc(e.titulo)}</div><div class="sl">Slides ${esc(e.slides)}</div></span></button>
      <div class="cuerpo"><p class="idea">${esc(e.idea)}</p>
        <div class="vars">${e.corridas.map((c, i) => {
          const k = `${e.id}${i}`;
          return `<button class="var ${c.bien ? "bien" : ""} ${S.hecho[k] === "corriendo" ? "corriendo" : S.hecho[k] ? "hecho" : ""}" data-i="${i}">${c.bien ? ic("check") : ""}${esc(c.nombre)}</button>`;
        }).join("")}</div></div></div>`).join("");
  if (!poner($("#escenarios"), html)) return;
  $$(".esc > button").forEach(b => b.onclick = () => { const id = b.parentElement.dataset.e; S.esc = S.esc === id ? null : id; pintarEscenarios(); });
  $$(".var").forEach(b => b.onclick = () => correrEscenario(b.closest(".esc").dataset.e, Number(b.dataset.i)));
}

async function correrEscenario(id, i) {
  if (S.ocupado) return;
  const e = S.cat.escenarios.find(x => x.id === id), c = e.corridas[i], k = `${id}${i}`;
  S.tab = e.panel; pintarTabs();
  S.hecho[k] = "corriendo"; pintarEscenarios();
  let ok = true;
  if (c.accion === "matriz") ok = await lanzar("/api/matriz");
  else if (c.accion === "descarte") {
    Object.assign(S, { tipo: c.tipo, umbral: c.umbral, p95: c.p95 });
    if (!S.ult.matriz) ok = await lanzar("/api/matriz");
    S.tab = "descarte"; pintarTabs();
  }
  else if (c.accion === "router") { S.derivacion = c.derivacion; ok = await lanzar("/api/router", { derivacion: c.derivacion }); }
  else if (c.accion === "clasificar") ok = await lanzar("/api/clasificar");
  S.hecho[k] = ok; pintarTodo();
}

async function lanzar(url, body) {
  if (S.ocupado) return false;
  S.ocupado = true;
  try {
    S.trabajo = await llamar(url, body);
    pintarTodo();
    while (S.trabajo.estado === "corriendo") {
      await new Promise(r => setTimeout(r, 600));
      S.trabajo = await (await fetch("/api/trabajo")).json();
      if (S.trabajo.parcial) S.ult.matriz = S.trabajo.parcial;
      pintarTablero(); pintarPanel();
    }
    if (S.trabajo.error) toast(S.trabajo.error);
    S.ult = await (await fetch("/api/ultimos")).json();
    return S.trabajo.estado === "listo";
  } catch (e) { toast(e.message); return false; }
  finally { S.trabajo = null; S.ocupado = false; pintarTodo(); }
}

// ---------- tablero (centro) ----------
function resultado(p, t) { return (S.ult.matriz?.resultados || []).find(r => r.perfil === p && r.tarea === t && r.modelo === perfil(p).modelo); }

function pintarTablero() {
  const t = S.trabajo;
  const prog = t ? `<div class="progreso"><div class="lab"><span>${esc(t.actual || "Corriendo…")}</span><span>${t.hechas} de ${t.total}</span></div>
      <div class="barra"><i style="width:${t.total ? 100 * t.hechas / t.total : 0}%"></i></div></div>` : "";
  const corriendo = t?.actual || "";
  let grupo = "";
  const filas = S.cat.tareas.map(ta => {
    let cab = "";
    if (ta.tipo !== grupo) { grupo = ta.tipo; cab = `<tr class="grupo"><td colspan="${2 + S.cat.perfiles.length}">${esc(TIPOS.find(x => x[0] === ta.tipo)[1])}</td></tr>`; }
    const celdas = S.cat.perfiles.map(p => {
      const r = resultado(p.id, ta.id), activo = corriendo.includes(`· ${ta.id} ·`) && corriendo.startsWith(p.nombre);
      const cls = activo ? "corre" : r ? (r.correcta ? "ok" : "mal") : "";
      const val = r ? `<span class="val ${r.valida ? "" : "no"}">${r.valida ? "valida" : "no valida"}</span>` : "";
      return `<td class="celda" data-p="${p.id}" data-t="${ta.id}" title="${r ? esc(r.motivo) : ""}"><span class="res ${cls}">${activo ? "…" : r ? (r.correcta ? "✓" : "✗") : "·"}</span>${val}</td>`;
    }).join("");
    return cab + `<tr><td>${esc(ta.id)}</td><td class="enun">${esc(ta.enunciado)}</td>${celdas}</tr>`;
  }).join("");
  const html = prog + `<table class="tab"><tr><th></th><th>Tarea</th>${S.cat.perfiles.map(p => `<th class="p">${esc(p.nombre)}</th>`).join("")}</tr>${filas}</table>
    <div class="ley"><span><i style="background:var(--azul-1)"></i>respuesta correcta</span><span><i style="background:var(--violeta-3)"></i>incorrecta</span>
    <span>"valida" / "no valida": lo que ve el validador, que no conoce la respuesta</span></div>`;
  if (poner($("#tablero"), html)) $$(".celda").forEach(c => c.onclick = () => verLlamada(c.dataset.p, c.dataset.t));
}

async function verLlamada(p, t) {
  const r = resultado(p, t);
  if (!r) return;
  S.llamada = { r, request: null, cargando: true }; S.tab = "llamada"; pintarTabs(); pintarPanel();
  try { const l = await (await fetch(`/api/llamada?perfil=${p}&tarea=${t}`)).json(); if (l.request) S.llamada = { r, request: l.request, texto: l.texto }; else S.llamada = { r }; }
  catch { S.llamada = { r }; }
  pintarPanel();
}

// ---------- panel derecho ----------
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); pintarPanel(); });
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); }
const vacio = (icono, t) => `<div class="panel-vacio">${ic(icono)}<p>${t}</p></div>`;

function panelMatriz() {
  const m = S.ult.matriz;
  if (!m?.filas?.length) return vacio("table", "Corré el escenario A para medir los tres perfiles.");
  const cls = (c, t) => !t ? "" : c / t >= .99 ? "hi" : c / t >= .5 ? "mid" : "";
  const minCosto = Math.min(...m.filas.map(f => f.costo_por_1000)), maxTs = Math.max(...m.filas.map(f => f.tokens_s)), minP95 = Math.min(...m.filas.map(f => f.p95_ms));
  const cab = ["Clasificar", "Extraer JSON", "Tools", "Razonar", "JSON", "p50 / p95", "Tokens/s", "Costo × 1.000"];
  const filas = m.filas.map(f => `<div class="r">${esc(f.nombre)}<small>${esc(f.modelo)}</small></div>
      ${TIPOS.map(([t]) => { const x = f.por_tipo[t]; return `<div class="v ${cls(x.correctas, x.total)}">${x.correctas}/${x.total}</div>`; }).join("")}
      <div class="v ${f.json_garantizado ? "hi" : "mid"}">${f.json_garantizado ? "garantizado" : "en el prompt"}</div>
      <div class="v ${f.p95_ms === minP95 ? "hi" : ""}">${(f.p50_ms / 1000).toFixed(1).replace(".", ",")} / ${seg(f.p95_ms)}</div>
      <div class="v ${f.tokens_s === maxTs ? "hi" : ""}">${Math.round(f.tokens_s)}</div>
      <div class="v ${f.costo_por_1000 === minCosto ? "hi" : f.costo_por_1000 > 5 * minCosto ? "" : "mid"}">${usd(f.costo_por_1000, 2)}</div>`).join("");
  const resp = m.filas.some(f => f.usa_respaldo);
  return `<div class="card"><h4>${ic("table")} Capability matrix medida · ${S.cat.tareas.length} tareas por perfil</h4>
      <div class="mx" style="grid-template-columns: 110px repeat(4, 1fr) 1.3fr 1.5fr .9fr 1.3fr"><div></div>${cab.map(c => `<div class="h">${c}</div>`).join("")}${filas}</div>
      <div class="ley"><span><i style="background:var(--azul-1)"></i>favorable</span><span><i style="background:var(--bad-soft);border:1px solid var(--violeta-3)"></i>con reservas</span>
        <span><i style="background:var(--gris-sup);border:1px solid var(--gris-borde)"></i>débil</span></div></div>
    ${resp ? `<div class="aviso">${ic("alert")}<span>Sin clave de NVIDIA, el perfil <b>Grande</b> corre <code>${esc(perfil("grande").modelo)}</code> local: la diferencia con el generalista es menor que con un modelo de 70B.</span></div>` : ""}
    <div class="glos"><p><b>La ficha dice si una capacidad existe; la medición, si alcanza.</b> Los tres perfiles soportan tools y JSON, pero no aciertan lo mismo.</p>
      <p><b>Costo × 1.000:</b> costo de 1.000 pedidos como estos, con precios ilustrativos por millón de tokens. <b>p95:</b> el 95 % de las llamadas tardó menos.</p></div>`;
}

/** Los cuatro filtros de la slide sobre la matriz medida. */
function panelDescarte() {
  const m = S.ult.matriz;
  if (!m?.filas?.length) return vacio("funnel", "Hace falta la matriz: corré el escenario A o el B.");
  const nombreTipo = TIPOS.find(x => x[0] === S.tipo)[1];
  let vivos = m.filas.map(f => f.perfil);
  const fila = id => m.filas.find(f => f.perfil === id);
  const pasos = [];
  const filtrar = (titulo, criterio, prueba, detalle) => {
    const fuera = vivos.filter(id => !prueba(fila(id)));
    pasos.push({ titulo, criterio, fuera, detalle: vivos.map(id => detalle(fila(id), !fuera.includes(id))) });
    vivos = vivos.filter(id => !fuera.includes(id));
  };
  const req = S.tipo === "herramienta" ? "tool calling" : "structured output garantizado";
  filtrar("Requisitos duros", req, f => S.tipo === "herramienta" ? f.tools : f.json_garantizado,
    (f, ok) => `<span class="${ok ? "k" : "x"}">${esc(f.nombre)}: ${ok ? "sí" : "no"}</span>`);
  filtrar("Umbral de calidad", `al menos ${pct(S.umbral)} en "${nombreTipo}"`, f => f.por_tipo[S.tipo].correctas / f.por_tipo[S.tipo].total >= S.umbral - 1e-9,
    (f, ok) => `<span class="${ok ? "k" : "x"}">${esc(f.nombre)}: ${f.por_tipo[S.tipo].correctas}/${f.por_tipo[S.tipo].total}</span>`);
  filtrar("Operación", `p95 de ${S.p95} s o menos`, f => f.p95_ms <= S.p95 * 1000,
    (f, ok) => `<span class="${ok ? "k" : "x"}">${esc(f.nombre)}: ${seg(f.p95_ms)}</span>`);
  const elegido = vivos.slice().sort((a, b) => fila(a).costo_tarea - fila(b).costo_tarea)[0];
  const quedan = vivos;
  const fueraDe = id => pasos.findIndex(p => p.fuera.includes(id));
  return `<div class="card"><h4>${ic("funnel")} Elegir un modelo para "${esc(nombreTipo)}"</h4>
      <div class="cands">${m.filas.map(f => `<div class="cand ${f.perfil === elegido ? "elegido" : fueraDe(f.perfil) >= 0 || (quedan.includes(f.perfil) && f.perfil !== elegido) ? "fuera" : ""}">${esc(f.nombre)}<small>${esc(f.modelo)}</small></div>`).join("")}</div>
      ${pasos.map((p, i) => `<div class="filtro"><span class="n">${i + 1}</span><div><b>${p.titulo}</b> · ${esc(p.criterio)}
        <div class="det">${p.detalle.join(" · ") || "ya no queda ninguno"}</div></div></div>`).join("")}
      <div class="filtro final"><span class="n">4</span><div><b>Costo</b> · el más barato por tarea entre los que quedan
        <div class="det">${quedan.length ? quedan.map(id => `<span class="${id === elegido ? "k" : ""}">${esc(fila(id).nombre)}: ${usd(fila(id).costo_tarea * 1000, 2)} × 1.000</span>`).join(" · ") : `<span class="x">Ningún perfil pasa los filtros: bajá el umbral, subí la latencia o sumá un modelo.</span>`}</div></div></div></div>
    <div class="glos"><p><b>Por descarte, no por ranking:</b> primero se eliminan los que no pueden, después los que no alcanzan y recién al final se compara precio.</p>
      <p>Mové la tarea, el umbral y la latencia en la columna izquierda: el elegido cambia.</p></div>`;
}

function panelRouter() {
  const r = S.ult.router;
  if (!r) return vacio("route", "Corré el escenario C para comparar las cinco estrategias.");
  const es = r.estrategias.map(e => {
    const total = e.costo_modelo + (e.total - e.resueltas) * S.derivacion;
    return { ...e, costo_total: total, costo_resolucion: e.resueltas ? total / e.resueltas : Infinity };
  });
  const mejor = es.reduce((a, b) => b.costo_resolucion < a.costo_resolucion ? b : a);
  const max = Math.max(...es.filter(e => isFinite(e.costo_resolucion)).map(e => e.costo_total));
  const sel = es.find(e => e.id === S.estrategia) || mejor;
  const tabla = `<table class="t"><tr><th>Estrategia</th><th class="num">Resueltas</th><th class="num">Llamadas</th><th class="num">Escalan</th><th class="num">Modelo × 1.000</th><th class="num">Por resolución</th></tr>
    ${es.map(e => `<tr class="clic ${e.id === sel.id ? "sel" : ""}" data-e="${e.id}"><td><b>${esc(e.nombre)}</b>${e === mejor ? `<span class="chip ok mejor-chip">mejor</span>` : ""}</td>
      <td class="num">${e.resueltas}/${e.total}</td><td class="num">${e.llamadas}</td><td class="num">${e.escalamientos}</td>
      <td class="num">${usd(e.costo_modelo_1000, 2)}</td><td class="num">${isFinite(e.costo_resolucion) ? usd(e.costo_resolucion, 3) : "—"}</td></tr>`).join("")}</table>`;
  const barras = es.map(e => `<div class="fila"><b>${esc(e.nombre)}</b><div class="pista"><i class="modelo" style="width:${100 * e.costo_modelo / max}%"></i><i class="deriv" style="width:${100 * (e.costo_total - e.costo_modelo) / max}%"></i></div>
      <span class="v">${usd(e.costo_total, 2)}</span></div>`).join("");
  const nom = id => perfil(id).nombre;
  const detalle = sel.detalle.map(d => `<tr><td><b class="mono">${esc(d.tarea)}</b></td><td><div class="camino">${d.intentos.map((x, i) =>
      `${i ? `<span class="fl">→</span>` : ""}<span class="p ${x.valida ? "ok" : "no"}" title="${esc(x.motivo)}">${esc(nom(x.perfil))}</span>`).join("")}</div></td>
      <td>${d.correcta ? `<span class="chip ok">resuelta</span>` : `<span class="chip mal">a una persona</span>`}</td><td class="num">${usd(d.costo * 1000, 2)}</td></tr>`).join("");
  return `<div class="card"><h4>${ic("route")} Cinco estrategias, ${S.cat.tareas.length} tareas</h4>${tabla}</div>
    <div class="card"><h4>${ic("trend")} Costo total de las ${S.cat.tareas.length} tareas · derivar cuesta ${usd(S.derivacion, 2)}</h4><div class="barras">${barras}</div>
      <div class="ley"><span><i style="background:var(--cyan)"></i>gasto en modelos</span><span><i style="background:var(--violeta-3)"></i>tareas que pasan a una persona</span></div></div>
    <div class="card"><h4>${ic("steps")} Camino de cada tarea · ${esc(sel.nombre)}</h4><p style="font-size:12.5px;color:var(--muted);margin:0 0 8px">${esc(sel.resumen)} Tachado: el validador lo rechazó.</p>
      <table class="t"><tr><th>Tarea</th><th>Modelos</th><th></th><th class="num">× 1.000</th></tr>${detalle}</table></div>
    <div class="glos"><p><b>Costo por resolución:</b> (gasto en modelos + tareas no resueltas × costo de derivar) ÷ tareas resueltas.</p>
      <p><b>El validador revisa la forma y las cuentas, no la regla:</b> por eso algunos errores pasan sin escalar.</p></div>`;
}

function panelClasificacion() {
  const r = S.ult.clasificacion;
  if (!r) return vacio("split", "Corré el escenario D para comparar reglas y clasificador.");
  const et = (v, esp) => `<span class="et ${v} ${v !== esp ? "mal" : ""}">${esc(v)}</span>`;
  const destino = v => v === "complejo" ? perfil("grande").nombre : perfil("economico").nombre;
  return `<div class="kpis" style="grid-template-columns:repeat(3,1fr)">
      <div class="kpi"><div class="n">Reglas</div><div class="v">${r.aciertos_reglas}/${r.filas.length}</div><div class="s">gratis, sin llamadas</div></div>
      <div class="kpi"><div class="n">Clasificador</div><div class="v">${r.aciertos_clasificador}/${r.filas.length}</div><div class="s">${esc(r.modelo)}</div></div>
      <div class="kpi"><div class="n">Lo que suma el clasificador</div><div class="v">${seg(r.ms_clasificador / r.filas.length)}</div><div class="s">por mensaje · ${usd(r.costo_clasificador / r.filas.length * 1000, 2)} × 1.000</div></div></div>
    <div class="card"><h4>${ic("split")} ¿Económico o grande?</h4><table class="t"><tr><th>Mensaje</th><th>Debería</th><th>Reglas</th><th>Clasificador</th></tr>
      ${r.filas.map(f => `<tr><td>${esc(f.mensaje)}</td><td>${et(f.esperado, f.esperado)}</td>
        <td>${et(f.reglas, f.esperado)}${f.palabra ? `<span class="palabra">por "${esc(f.palabra)}"</span>` : ""}</td>
        <td>${et(f.clasificador, f.esperado)}<span class="palabra">→ ${esc(destino(f.clasificador))}</span></td></tr>`).join("")}</table></div>
    <div class="glos"><p><b>Reglas:</b> palabras clave (evento, presupuesto, devuelven, reembolso, grupo). Predecibles y gratis, pero no ven lo que nadie escribió en la lista.</p>
      <p><b>Clasificador:</b> un modelo chico etiqueta el mensaje. Se adapta al contenido, a cambio de una llamada más por pedido.</p>
      <p><b>Se equivocan en lugares distintos:</b> las reglas atrapan "evento" o "devuelven" aunque el modelo chico no; el modelo entiende lo que no está en la lista. Por eso se combinan.</p></div>`;
}

function panelLlamada() {
  const l = S.llamada;
  if (!l) return vacio("code", "Hacé clic en una celda del tablero para ver la llamada.");
  const r = l.r, ta = S.cat.tareas.find(t => t.id === r.tarea), p = perfil(r.perfil);
  return `<div class="card"><h4>${ic("target")} ${esc(ta.id)} · ${esc(p.nombre)}</h4>
      <div class="dato"><span>Tarea</span><span>${esc(ta.enunciado)}</span><span>Modelo</span><span class="mono">${esc(r.modelo)} · ${r.proveedor === "nvidia" ? "NVIDIA" : "Ollama local"}</span>
        <span>Validador</span><span>${r.valida ? `<span class="chip ok">valida</span>` : `<span class="chip mal">no valida</span>`} ${esc(r.motivo)}</span>
        <span>Corrección</span><span>${r.correcta ? `<span class="chip ok">correcta</span>` : `<span class="chip mal">incorrecta</span>`}</span>
        <span>Medidas</span><span>${mil(r.tokens_entrada)} → ${mil(r.tokens_salida)} tokens · ${seg(r.ms)} · ${usd(r.costo * 1000, 2)} × 1.000${r.grabado ? ` · grabado ${esc(r.grabado)}` : ""}</span></div>
      <div class="cols" style="display:grid;grid-template-columns:1fr 1fr;gap:10px">
        <div><div class="rotulo" style="margin:6px 0">Respuesta</div><pre class="bloque">${json(r.salida)}</pre></div>
        <div><div class="rotulo" style="margin:6px 0">Esperado</div><pre class="bloque">${json(ta.esperado)}</pre></div></div></div>
    ${l.cargando ? "" : l.request ? `<div class="card"><h4>${ic("code")} Request · ${esc(l.request.url)}</h4><pre class="bloque">${json(l.request.cuerpo)}</pre></div>`
      : `<div class="aviso info">${ic("help")}<span>El request completo se ve en las llamadas hechas en esta sesión del servidor.</span></div>`}`;
}

function pintarPanel() {
  const f = { matriz: panelMatriz, descarte: panelDescarte, router: panelRouter, clasificacion: panelClasificacion, llamada: panelLlamada }[S.tab];
  if (poner($("#panel"), f())) $$("tr[data-e]").forEach(tr => tr.onclick = () => { S.estrategia = tr.dataset.e; pintarPanel(); });
}

function pintarTodo() { pintarControles(); pintarEscenarios(); pintarTablero(); pintarPanel(); }

(async function iniciar() {
  S.cat = await (await fetch("/api/catalogo")).json();
  S.ult = await (await fetch("/api/ultimos")).json();
  await estado();
  pintarTodo();
})();
