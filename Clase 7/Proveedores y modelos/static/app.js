// MesaYa · Proveedores y modelos por propósito — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
function poner(el, html) { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; }
const seg = ms => ms == null ? "—" : (ms / 1000).toFixed(1).replace(".", ",") + " s";
const mil = n => Number(n || 0).toLocaleString("es-AR");
const json = o => esc(JSON.stringify(o, null, 2));
const md = t => esc(t).replace(/\*\*(.+?)\*\*/g, "<b>$1</b>");  // las negritas de markdown que devuelven los modelos

const S = { cat: null, est: null, prov: "ollama", familia: "generalista", filtro: false, registro: [], sel: null, paso: null,
            tab: "request", esc: null, hecho: {}, ocupado: false, trabajo: null };
const FAMILIAS = [["economico", "Económico"], ["generalista", "Generalista"]];
const PANEL = { request: "request", rafaga: "limites", pipeline: "pipeline" };
const proveedor = id => S.est?.proveedores.find(p => p.id === id);
const modelo = (prov, fam) => S.cat.modelos[prov]?.[fam] ?? S.cat.modelos.ollama[fam];
const famClase = f => ({ "económico": "economico", "visión": "vision", "código": "codigo" }[f] ?? f);

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
    $("#guion").checked = e.modo_guion; $("#limite").checked = e.limite_simulado;
    const nv = proveedor("nvidia");
    if (e.modo_guion) { dot.className = "dot ok"; txt.textContent = "Modo guion"; }
    else if (desc) { const [m, d] = desc; dot.className = "dot carga"; txt.textContent = `Descargando ${m}… ${d.total ? Math.round(100 * d.completado / d.total) : 0} %`; }
    else if (!e.ollama.conectado) { dot.className = "dot mal"; txt.textContent = e.ollama.error || "Sin Ollama"; }
    else if (!e.listo) { dot.className = "dot carga"; txt.textContent = `Preparando ${e.ollama.faltan.join(", ")}…`; }
    else { dot.className = "dot ok"; txt.textContent = `Ollama listo · NVIDIA ${nv?.tiene_clave ? "con clave" : "sin clave"}`; }
    pintarControles();
    setTimeout(estado, desc || !e.listo ? 1500 : 8000);
  } catch { $("#estado .dot").className = "dot mal"; $("#estado-txt").textContent = "Sin servidor"; setTimeout(estado, 3000); }
}
$("#guion").onchange = e => post("/api/config", { modo_guion: e.target.checked }).then(estado);
$("#limite").onchange = e => post("/api/config", { limite_simulado: e.target.checked }).then(estado);

// ---------- controles ----------
function pintarControles() {
  if (!S.est || !S.cat) return;
  poner($("#proveedores"), S.est.proveedores.map(p => `<button class="prov ${S.prov === p.id ? "on" : ""}" data-p="${p.id}">
      <span class="icb">${ic(p.local ? "monitor" : "globe")}</span>
      <span><b>${esc(p.nombre)}</b><small>${esc(p.url)}</small>
      <small class="${p.tiene_clave ? "" : "mal"}">${p.local ? "sin clave: corre en esta máquina" : p.tiene_clave ? "clave: " + esc(p.origen_clave) : "sin clave configurada"}</small></span></button>`).join(""));
  $$(".prov").forEach(b => b.onclick = () => { S.prov = b.dataset.p; pintarControles(); });
  poner($("#familias"), FAMILIAS.map(([id, n]) => `<button data-f="${id}" class="${S.familia === id ? "on" : ""}">${n}</button>`).join(""));
  $$("#familias button").forEach(b => b.onclick = () => { S.familia = b.dataset.f; pintarControles(); });
  $("#nota-modelo").textContent = modelo(S.prov, S.familia);
  $("#filtro").checked = S.filtro;
}
$("#filtro").onchange = e => { S.filtro = e.target.checked; };

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
  // La ráfaga va contra NVIDIA si hay clave; si no, contra el generalista local.
  S.prov = c.proveedor || (proveedor("nvidia")?.tiene_clave ? "nvidia" : "ollama");
  if (c.familia) S.familia = c.familia;
  if (c.filtro != null) S.filtro = c.filtro;
  pintarControles();
  S.tab = e.panel; pintarTabs();
  S.hecho[k] = "corriendo"; pintarEscenarios();
  let ok;
  if (c.accion === "rafaga") ok = await lanzar("/api/rafaga", { proveedor: S.prov, modelo: modelo(S.prov, "generalista"), estrategia: c.estrategia });
  else if (c.accion === "pipeline") ok = await lanzar("/api/pipeline", { caso: c.caso, proveedor: S.prov });
  else ok = await enviar(c.mensaje);
  S.hecho[k] = ok; pintarEscenarios();
}

// ---------- requests y trabajos ----------
$("#enviar").onclick = () => { const t = $("#mensaje").value.trim(); if (t) enviar(t); };
$("#mensaje").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#enviar").click(); } });
$("#limpiar").onclick = async () => { await fetch("/api/registro", { method: "DELETE" }); S.registro = []; S.sel = null; S.paso = null; S.hecho = {}; pintarTodo(); };

async function enviar(texto) {
  if (S.ocupado) return false;
  S.ocupado = true; $("#enviar").disabled = true; $("#mensaje").value = "";
  const tmp = { tipo: "request", cargando: true, mensaje: texto, proveedor: S.prov, modelo: modelo(S.prov, S.familia) };
  S.registro.push(tmp); S.sel = tmp; S.paso = null;
  if (S.tab !== "request" && S.tab !== "config") { S.tab = "request"; pintarTabs(); }
  pintarTodo();
  let ok = true;
  try {
    const item = await llamar("/api/request", { mensaje: texto, proveedor: S.prov, familia: S.familia, filtro: S.filtro });
    S.registro[S.registro.indexOf(tmp)] = item; S.sel = item; ok = item.ok;
    if (!item.ok) toast(item.resultado?.error || "El proveedor respondió con error");
  } catch (e) { S.registro.splice(S.registro.indexOf(tmp), 1); toast(e.message); ok = false; }
  S.ocupado = false; $("#enviar").disabled = false;
  pintarTodo();
  return ok;
}

async function lanzar(url, body) {
  if (S.ocupado) return false;
  S.ocupado = true; $("#enviar").disabled = true;
  try {
    S.trabajo = await llamar(url, body);
    S.sel = { tipo: S.trabajo.tipo, vivo: true }; S.paso = null;
    pintarTodo();
    while (S.trabajo.estado === "corriendo") {
      await new Promise(r => setTimeout(r, 450));
      S.trabajo = await (await fetch("/api/trabajo")).json();
      pintarRegistro(); pintarPanel();
    }
    if (S.trabajo.error) toast(S.trabajo.error);
    S.registro = await (await fetch("/api/registro")).json();
    S.sel = S.registro.at(-1);
    return S.trabajo.estado === "listo";
  } catch (e) { toast(e.message); return false; }
  finally { S.trabajo = null; S.ocupado = false; $("#enviar").disabled = false; pintarTodo(); }
}

// ---------- registro (centro) ----------
function chips(r) {
  if (!r) return "";
  const est = r.estado === 200 ? `<span class="chip ok">200</span>` : r.estado ? `<span class="chip mal">${r.estado}</span>` : "";
  return `${est}<span class="chip ${r.proveedor === "nvidia" ? "grad" : "azul"}">${esc(proveedor(r.proveedor)?.nombre ?? r.proveedor)}</span>
    <span class="chip">${esc(r.modelo)}</span>
    ${r.tokens_entrada != null ? `<span class="chip">${mil(r.tokens_entrada)} → ${mil(r.tokens_salida)} tokens${r.tokens_estimados ? " (estimados)" : ""}</span>` : ""}
    ${r.ttft_ms != null ? `<span class="chip">primer token ${seg(r.ttft_ms)}</span>` : ""}
    ${r.ms != null ? `<span class="chip">${seg(r.ms)}</span>` : ""}
    ${r.grabado ? `<span class="chip vio">grabado ${esc(r.grabado)}</span>` : ""}`;
}

function tarjetaRafaga(t, cfg, vivo) {
  const res = t.resultado || {}, ev = t.eventos || [];
  const n = res.cantidad || 8, porN = {};
  for (const e of ev) if (e.n) (porN[e.n] ??= []).push(e);
  const cuadros = Array.from({ length: n }, (_, i) => {
    const es = porN[i + 1] || [];
    const ok = es.find(e => e.tipo === "ok"), fallo = es.find(e => e.tipo === "fallo");
    const cls = fallo ? "fallo" : ok ? (ok.proveedor !== res.proveedor || ok.modelo !== res.modelo ? "respaldo" : es.some(e => e.tipo === "error") ? "reint" : "") : "pend";
    return `<span class="cuadro ${cls}" title="Pedido ${i + 1}">${i + 1}</span>`;
  }).join("");
  const nom = { sin: "sin reintentos", backoff: "backoff 1, 2, 4 s", retry_after: "respetando Retry-After", fallback: "con fallback" }[res.estrategia ?? cfg?.estrategia] ?? "";
  return `<div class="titulo-card">${ic("clock")} Ráfaga de ${n} pedidos · ${esc(nom)}</div>
    <div class="cuadros">${cuadros}</div>
    <div class="meta">${vivo ? `<span class="chip azul">${esc(t.actual || "corriendo…")}</span>` : `
      <span class="chip ok">${res.ok ?? 0} respondidos</span>${res.con_429 ? `<span class="chip warn">${res.con_429} con 429</span>` : ""}
      ${res.fallidos ? `<span class="chip mal">${res.fallidos} fallidos</span>` : ""}${res.por_respaldo ? `<span class="chip ok">${res.por_respaldo} por el respaldo</span>` : ""}
      <span class="chip">${seg(res.ms_total)}</span>`}
      <span class="chip ${res.proveedor === "nvidia" ? "grad" : "azul"}">${esc(res.modelo ?? "")}</span>${res.simulado ? `<span class="chip">límite simulado</span>` : ""}</div>`;
}

function tarjetaPipeline(t, cfg, vivo) {
  const ev = t.eventos || [], ult = ev.at(-1);
  const cadena = ev.map(e => `<span class="fam ${famClase(e.familia)}">${esc(e.familia)}</span>`).join(`<span class="flecha">→</span>`);
  const titulo = (cfg?.caso ?? t.resultado?.caso) === "factura" ? "Factura de un proveedor" : "Mensaje de WhatsApp";
  const final = !vivo && ult ? (typeof ult.salida === "string" ? `<div class="resp">${md(ult.salida)}</div>` : "") : "";
  return `<div class="titulo-card">${ic("chain")} Pipeline · ${titulo}</div>
    <div class="cadena">${cadena}${vivo ? `<span class="flecha">→</span><span class="pensando"><i></i><i></i><i></i></span>` : ""}</div>
    ${vivo ? `<div class="meta"><span class="chip azul">${esc(t.actual || "")}</span></div>` : `<div class="meta">
      <span class="chip">${t.resultado?.llamadas ?? 0} llamadas</span><span class="chip">${(t.resultado?.modelos || []).length} modelos</span>
      <span class="chip">${mil(t.resultado?.tokens)} tokens</span><span class="chip">${seg(t.resultado?.ms_total)}</span></div>`}${final}`;
}

function pintarRegistro() {
  const el = $("#registro");
  const items = [...S.registro];
  if (S.trabajo) items.push({ tipo: S.trabajo.tipo, trabajo: S.trabajo, vivo: true });
  if (!items.length) {
    poner(el, `<div class="vacio"><img src="/static/isotipo-blanco.png" alt=""><h2>Un pedido, cualquier proveedor</h2>
      <p>Elegí un escenario a la izquierda o escribí un pedido abajo. Cada respuesta muestra el request que salió, los tokens y los tiempos.</p></div>`);
    return;
  }
  const html = items.map((it, i) => {
    const sel = it === S.sel || (it.vivo && S.sel?.vivo) ? "sel" : "";
    if (it.tipo === "request") {
      const r = it.resultado;
      const u = `<div class="msg-u">${esc(it.mensaje)}${it.filtro && it.enviado !== it.mensaje ? `<span class="filtrado">Sale así: ${esc(it.enviado)}</span>` : ""}
        <small>→ ${esc(proveedor(it.proveedor)?.nombre ?? it.proveedor)} · ${esc(it.modelo)}</small></div>`;
      const a = it.cargando ? `<div class="msg-a ${sel}" data-i="${i}"><span class="pensando"><i></i><i></i><i></i></span></div>`
        : `<div class="msg-a ${sel} ${it.ok ? "" : "err"}" data-i="${i}"><div class="meta">${chips(r)}</div>
            ${it.ok ? `<div class="resp">${md(r.texto)}</div>` : `<div class="err">${esc(r?.error)}</div>`}</div>`;
      return u + a;
    }
    const t = it.vivo ? it.trabajo : it.trabajo, cfg = it.config;
    const cuerpo = it.tipo === "rafaga" ? tarjetaRafaga(t, cfg, it.vivo) : tarjetaPipeline(t, cfg, it.vivo);
    return `<div class="msg-a ${sel} ${t.estado === "error" ? "err" : ""}" data-i="${i}">${cuerpo}${t.error ? `<div class="err">${esc(t.error)}</div>` : ""}</div>`;
  }).join("");
  if (poner(el, html)) {
    $$("#registro .msg-a").forEach(d => d.onclick = () => {
      const it = items[Number(d.dataset.i)];
      if (it.vivo || it.cargando) return;
      S.sel = it; S.paso = null; S.tab = PANEL[it.tipo]; pintarTabs(); pintarTodo();
    });
    el.scrollTop = el.scrollHeight;
  }
}

// ---------- panel derecho ----------
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); pintarPanel(); });
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); }

function cuerpoResaltado(cuerpo, pii) {
  let txt = json(cuerpo);
  txt = txt.replace(/(&quot;(?:model|reasoning_effort|response_format|tools)&quot;: )/g, `<span class="hl">$1</span>`);
  for (const p of pii || []) if (p.viaja) txt = txt.split(esc(p.valor)).join(`<span class="pii">${esc(p.valor)}</span>`);
  return txt;
}

function verRequest(r, pii) {
  if (!r?.request) return `<div class="panel-vacio">Este paso no hace un request.</div>`;
  const q = r.request, enc = q.encabezados || {};
  return `<div class="card"><h4>${ic("code")} Request</h4>
      <div class="linea-url"><span class="m">${esc(q.metodo)}</span><span class="b">${esc(q.base)}</span><span class="r">${esc(q.ruta)}</span></div>
      <div class="enc">${Object.entries(enc).map(([k, v]) => `<div><span class="k">${esc(k)}:</span> <span class="${k === "Authorization" ? "clave" : ""}">${esc(v)}</span>
        ${k === "Authorization" ? `<span class="origen">clave leída de: ${esc(q.origen_clave)}</span>` : ""}</div>`).join("")}</div>
      <pre class="bloque">${cuerpoResaltado(q.cuerpo, pii)}</pre></div>
    <div class="card"><h4>${ic("chat")} Respuesta</h4><div class="meta" style="display:flex;flex-wrap:wrap;gap:6px;margin-bottom:8px">${chips(r)}</div>
      ${r.error ? `<div class="aviso">${ic("alert")}<span>${esc(r.error)}</span></div>` : ""}
      ${r.tool_calls?.length ? `<pre class="bloque">${json(r.tool_calls)}</pre>` : r.texto ? `<div class="resp" style="white-space:pre-wrap;line-height:1.5">${md(r.texto)}</div>` : ""}
      ${r.estructura ? `<p class="nota" style="font-size:12.5px;color:var(--muted)">Structured output: ${esc(r.estructura)}</p>` : ""}</div>`;
}

/** Compara el último request a cada proveedor: lo que cambia y lo que queda igual. */
function comparar() {
  const ult = id => [...S.registro].reverse().find(x => x.tipo === "request" && x.proveedor === id && x.resultado?.request);
  const a = ult("ollama"), b = ult("nvidia");
  if (!a || !b) return "";
  const qa = a.resultado.request, qb = b.resultado.request, ca = qa.cuerpo, cb = qb.cuerpo;
  const claves = [...new Set([...Object.keys(ca), ...Object.keys(cb)])].filter(k => k !== "messages");
  const fila = (n, va, vb) => `<tr><td><b>${esc(n)}</b></td><td class="${va === vb ? "igual" : "dif"}"><code>${esc(va ?? "—")}</code></td><td class="${va === vb ? "igual" : "dif"}"><code>${esc(vb ?? "—")}</code></td></tr>`;
  const v = x => x === undefined ? undefined : typeof x === "object" ? JSON.stringify(x) : String(x);
  return `<div class="card"><h4>${ic("split")} Mismo código, dos proveedores</h4>
    <table class="t"><tr><th></th><th>Ollama local</th><th>NVIDIA</th></tr>
      ${fila("URL base", qa.base, qb.base)}${fila("Clave", qa.encabezados.Authorization ?? "no hace falta", qb.encabezados.Authorization)}
      ${claves.map(k => fila(k, v(ca[k]), v(cb[k]))).join("")}
      ${fila("messages", JSON.stringify(ca.messages) === JSON.stringify(cb.messages) ? "iguales" : "distintos", JSON.stringify(ca.messages) === JSON.stringify(cb.messages) ? "iguales" : "distintos")}</table>
    <div class="glos"><p><b>Lo que cambia:</b> URL base, clave y modelo. El resto del cuerpo es idéntico, incluido <b>reasoning_effort</b>, que apaga el razonamiento de qwen3.5 y de Nemotron.</p></div></div>`;
}

function panelRequest() {
  const it = S.paso ? null : S.sel;
  if (S.paso) return `<div class="aviso info">${ic("chain")}<span>Request del paso <b>${esc(S.paso.titulo)}</b> del pipeline.</span></div>` + verRequest(S.paso);
  if (!it || it.tipo !== "request" || it.cargando) {
    const c = comparar();
    return c || `<div class="panel-vacio">${ic("code")}<p>Elegí un request del registro para ver qué salió hacia el proveedor.</p></div>`;
  }
  const pii = it.datos_personales || [];
  const aviso = pii.length ? `<div class="card"><h4>${ic("shield")} Datos personales en el mensaje</h4>
      <div class="pii-lista">${pii.map(p => `<div><span class="chip ${p.viaja ? "mal" : "ok"}">${p.viaja ? "viaja" : "reemplazado"}</span>${esc(p.tipo)} <code>${esc(p.valor)}</code></div>`).join("")}</div>
      ${pii.some(p => p.viaja) && it.proveedor === "nvidia" ? `<div class="aviso" style="margin:10px 0 0">${ic("alert")}<span>El endpoint público de NVIDIA es para evaluar: estos datos no deberían salir.</span></div>` : ""}</div>` : "";
  return aviso + verRequest(it.resultado, pii) + comparar();
}

function panelLimites() {
  const t = S.trabajo?.tipo === "rafaga" ? S.trabajo : S.sel?.tipo === "rafaga" ? S.sel.trabajo : null;
  const lim = S.cat.limite;
  const uso = t ? ([...(t.eventos || [])].reverse().find(e => e.uso)?.uso ?? t.resultado?.limite) : null;
  const medidor = (n, v, max, u) => { const p = Math.min(100, 100 * (v || 0) / max); return `<div class="medidor"><div class="lab"><span>${n}</span><b>${mil(v)} / ${mil(max)} ${u}</b></div>
      <div class="barra"><i class="${p >= 99 ? "lleno" : ""}" style="width:${p}%"></i></div></div>`; };
  const cab = `<div class="card"><h4>${ic("clock")} Límite ${S.est?.limite_simulado ? "simulado" : "real del proveedor"}</h4>
      ${medidor("Requests en la ventana", uso?.requests, lim.Requests, "requests")}${medidor("Tokens en la ventana", uso?.tokens, lim.Tokens, "tokens")}
      <p style="font-size:12.5px;color:var(--muted);margin:4px 0 0">Ventana de ${lim.VentanaS} s. Un proveedor real cuenta por minuto (RPM y TPM); acá la ventana es corta para que se vea en clase.</p></div>`;
  if (!t) return cab + `<div class="panel-vacio">${ic("clock")}<p>Corré el escenario B para ver una ráfaga contra el límite.</p></div>`;
  const ev = t.eventos || [], res = t.resultado || {};
  const fin = Math.max(t.ms || 0, ...ev.map(e => (e.inicio_ms ?? e.t_ms) + (e.tipo === "espera" ? e.ms : 0)), 1000);
  const x = ms => (100 * ms / fin).toFixed(2) + "%";
  const filas = Array.from({ length: res.cantidad || 8 }, (_, i) => {
    const es = ev.filter(e => e.n === i + 1);
    const marcas = es.map(e => {
      if (e.tipo === "ok") return `<i class="${e.proveedor !== res.proveedor || e.modelo !== res.modelo ? "okr" : "ok"}" style="left:${x(e.inicio_ms)};width:${x(e.t_ms - e.inicio_ms)}" title="${esc(e.modelo)} · ${seg(e.ms)}"></i>`;
      if (e.tipo === "error") return `<i class="e429" style="left:${x(e.inicio_ms)};width:9px;z-index:1" title="${esc(e.error)}"></i>`;
      if (e.tipo === "espera") return `<i class="espera" style="left:${x(e.t_ms)};width:${x(e.ms)}" title="${esc(e.nota)}"></i>`;
      return "";
    }).join("");
    return `<div class="fila"><span>${i + 1}</span><div class="pista">${marcas}</div></div>`;
  }).join("");
  const kpis = `<div class="kpis"><div class="kpi"><div class="n">Respondidos</div><div class="v">${res.ok ?? "…"}</div><div class="s">de ${res.cantidad ?? 8}</div></div>
      <div class="kpi ${res.fallidos ? "mal" : ""}"><div class="n">Fallidos</div><div class="v">${res.fallidos ?? "…"}</div><div class="s">${res.con_429 ?? 0} con 429</div></div>
      <div class="kpi"><div class="n">Por el respaldo</div><div class="v">${res.por_respaldo ?? "…"}</div><div class="s">${esc((res.respaldo || "").replace("Ollama local · ", ""))}</div></div>
      <div class="kpi"><div class="n">Tiempo total</div><div class="v">${seg(res.ms_total ?? t.ms)}</div><div class="s">${seg(res.espera_ms)} esperando</div></div></div>`;
  const log = ev.slice().reverse().map(e => `<div><span class="t">${seg(e.t_ms)}</span><span>${e.n ? "#" + e.n : ""}</span>
      <span class="${e.tipo === "error" ? "e429" : e.tipo === "ok" ? "ok" : ""}">${e.tipo === "ok" ? `200 · ${esc(e.modelo)} · ${mil(e.tokens)} tokens` : esc(e.error || e.nota)}</span></div>`).join("");
  return cab + kpis + `<div class="card"><h4>${ic("steps")} Cada pedido en el tiempo</h4><div class="crono">${filas}<div class="eje"><span>0 s</span><span>${seg(fin)}</span></div></div>
      <div class="ley"><span><i style="background:var(--azul-1)"></i>respondido</span><span><i style="background:var(--cyan)"></i>respondido por el respaldo</span>
      <span><i style="background:var(--violeta-3)"></i>429</span><span><i style="background:repeating-linear-gradient(45deg,var(--gris-4) 0 3px,var(--gris-sup) 3px 6px)"></i>espera</span></div></div>
    <div class="card"><h4>${ic("notes")} Eventos</h4><div class="eventos">${log}</div></div>
    <div class="glos"><p><b>429:</b> el proveedor rechaza el pedido por exceso. <b>Retry-After:</b> encabezado que dice cuánto esperar.</p>
      <p><b>Backoff exponencial:</b> esperas que se duplican, con algo de azar (jitter) para no reintentar todos juntos.</p></div>`;
}

function salidaPaso(p) {
  const s = p.salida;
  if (p.familia === "embeddings" && Array.isArray(s))
    return s.map(x => `<div class="sim"><span>${esc(x.titulo)}<div class="barra"><i style="width:${Math.max(0, (x.similitud - .3) / .7 * 100)}%"></i></div></span><b class="mono">${x.similitud.toFixed(3)}</b></div>`).join("");
  if (s && typeof s === "object" && "cierra" in s)
    return `<pre class="bloque">${json(s)}</pre><div class="decision ${s.cierra ? "bien" : "mal"}">${s.cierra ? "✓ El total cierra: se carga" : `✗ $${mil(s.suma_items)} ≠ $${mil(s.total_leido)}: revisión humana`}</div>`;
  if (typeof s === "string") return `<div class="salida-txt">${md(s)}</div>`;
  return `<pre class="bloque">${json(s)}</pre>`;
}

function panelPipeline() {
  const t = S.trabajo?.tipo === "pipeline" ? S.trabajo : S.sel?.tipo === "pipeline" ? S.sel.trabajo : null;
  if (!t) return `<div class="panel-vacio">${ic("chain")}<p>Corré el escenario C para ver cada paso con su familia de modelo.</p></div>`;
  const pasos = (t.eventos || []).map((p, i) => `<div class="paso ${famClase(p.familia)} ${p.omitido ? "omitido" : ""}">
      <div class="cab"><span class="fam ${famClase(p.familia)}">${esc(p.familia)}</span><b>${esc(p.titulo)}</b>
        <span class="num">${p.modelo && p.modelo !== "—" ? `<b>${esc(p.modelo)}</b><br>` : ""}${p.tokens_entrada != null ? `${mil(p.tokens_entrada)}${p.tokens_salida != null ? " → " + mil(p.tokens_salida) : ""} tokens · ` : ""}${p.ms != null ? seg(p.ms) : ""}</span></div>
      ${p.nota ? `<p class="nota">${esc(p.nota)}</p>` : ""}${p.imagen ? `<img src="${esc(p.imagen)}" alt="Factura">` : ""}
      ${p.salida != null ? salidaPaso(p) : ""}
      ${p.request ? `<button class="btn sec mini" data-paso="${i}" style="margin-top:8px">${ic("code")}Ver request</button>` : ""}</div>`).join("");
  const vivo = t.estado === "corriendo" ? `<div class="paso corriendo"><div class="cab"><span class="pensando"><i></i><i></i><i></i></span><b>${esc(t.actual || "")}</b></div></div>` : "";
  return pasos + vivo + `<div class="glos"><p><b>Un modelo por tarea:</b> el económico clasifica, los embeddings comparan significados, el generalista usa tools y la visión lee imágenes.</p>
    <p><b>Lo que se extrae, se valida:</b> la suma de la factura se controla en código, no con otro modelo.</p></div>`;
}

function panelConfig() {
  const filas = S.est.proveedores.map(p => `<tr><td><b>${esc(p.nombre)}</b></td><td><code>${esc(p.url)}</code></td>
      <td>${p.local ? "no hace falta" : p.tiene_clave ? `<span class="chip ok">${esc(p.origen_clave)}</span>` : `<span class="chip mal">sin clave</span>`}</td>
      <td>${Object.entries(S.cat.modelos[p.id] || {}).map(([f, m]) => `<div><span style="color:var(--muted)">${esc(f)}:</span> <code>${esc(m)}</code></div>`).join("")}</td></tr>`).join("");
  return `<div class="card"><h4>${ic("globe")} Proveedores</h4><table class="t"><tr><th>Proveedor</th><th>URL base</th><th>Clave</th><th>Modelos por familia</th></tr>${filas}</table></div>
    <div class="card"><h4>${ic("lock")} De dónde sale la clave de NVIDIA</h4>
<pre class="bloque"><span class="c">// 1. la variable de entorno, si está definida</span>
var clave = Environment.<span class="k">GetEnvironmentVariable</span>("NVIDIA_API_KEY")
<span class="c">// 2. si no, appsettings: appsettings.Local.json pisa a appsettings.json</span>
         ?? config["Proveedores:nvidia:ApiKey"];</pre>
      <p style="font-size:13px;line-height:1.45;margin:10px 0 0"><b>appsettings.Local.json</b> está en el <code>.gitignore</code>: la clave real no va al repositorio.
      La interfaz muestra de dónde salió la clave, nunca su valor. Tampoco se guarda en <code>grabaciones/</code> ni en <code>datos/</code>.</p></div>
    <div class="card"><h4>${ic("clock")} Límite simulado</h4><p style="font-size:13px;margin:0">${S.cat.limite.Requests} requests y ${mil(S.cat.limite.Tokens)} tokens cada ${S.cat.limite.VentanaS} s, por proveedor y modelo. Se cambia en <code>appsettings.json</code> (sección LimiteSimulado). Apagado, la ráfaga va contra el límite real del proveedor.</p></div>
    <div class="glos"><p><b>Compatible con OpenAI:</b> Ollama y NVIDIA exponen el mismo <code>/v1/chat/completions</code>. Cambiar de proveedor es cambiar URL base, clave y modelo.</p>
      <p><b>Lo que puede no ser portable:</b> el structured output y el control del razonamiento. Hoy Ollama y NVIDIA aceptan los dos en el formato de OpenAI (<code>response_format</code> y <code>reasoning_effort</code>); si un proveedor rechaza el esquema, el cliente pide el JSON en el prompt. Todo eso vive en una sola clase: <code>Comun/Proveedores.cs</code>.</p></div>`;
}

function pintarPanel() {
  const f = { request: panelRequest, limites: panelLimites, pipeline: panelPipeline, config: panelConfig }[S.tab];
  if (!S.cat || !S.est) return;
  if (poner($("#panel"), f())) $$("[data-paso]").forEach(b => b.onclick = () => {
    const t = S.trabajo?.tipo === "pipeline" ? S.trabajo : S.sel.trabajo;
    S.paso = t.eventos[Number(b.dataset.paso)]; S.tab = "request"; pintarTabs(); pintarPanel();
  });
}

function pintarTodo() { pintarControles(); pintarEscenarios(); pintarRegistro(); pintarPanel(); }

(async function iniciar() {
  S.cat = await (await fetch("/api/catalogo")).json();
  S.registro = await (await fetch("/api/registro")).json();
  await estado();
  pintarTodo();
})();
