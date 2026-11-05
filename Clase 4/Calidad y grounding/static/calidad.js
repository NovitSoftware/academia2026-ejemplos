// MesaYa · Laboratorio de calidad — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const VACIO = document.querySelector("#mensajes").innerHTML;
const pct = v => v == null ? "—" : Math.round(v * 100) + " %";

const BASE = { perfil: "clientes", tenant: "mesaya-ar", chunking: "seccion", busqueda: "hibrida", k: 4, umbral: 0,
               solo_vigentes: false, priorizar_oficiales: false, rerank: false, grounding: "estricto", verificar: true, enrutar: false };
const S = { cfg: { ...BASE }, turnos: [], sel: null, tab: "recuperacion", catalogo: null, ocupado: false, eval: null, evalResp: null, esc: null };

// ---------- controles ----------
function pintarControles() {
  $$("[data-k]").forEach(el => {
    const k = el.dataset.k, v = S.cfg[k];
    if (el.classList.contains("seg")) el.querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.v === v));
    else if (el.type === "checkbox") el.checked = !!v;
    else el.value = v;
  });
  $("#v-k").textContent = S.cfg.k;
  $("#v-umbral").textContent = S.cfg.umbral > 0 ? S.cfg.umbral.toFixed(2) : "apagado";
}
$$("[data-k]").forEach(el => {
  const k = el.dataset.k;
  if (el.classList.contains("seg")) el.addEventListener("click", e => { const b = e.target.closest("button"); if (b) { S.cfg[k] = b.dataset.v; pintarControles(); } });
  else el.addEventListener("input", () => {
    S.cfg[k] = el.type === "checkbox" ? el.checked : el.type === "range" ? Number(el.value) : el.value;
    pintarControles();
  });
});
$("#reset").onclick = () => { S.cfg = { ...BASE }; pintarControles(); };

// ---------- estado de Ollama ----------
async function estado() {
  try {
    const e = await (await fetch("/api/estado")).json();
    const dot = $("#estado .dot"), txt = $("#estado-txt"), sel = $("#modelo");
    const desc = Object.entries(e.descargas || {}).find(([, d]) => d.estado === "descargando");
    $("#guion").checked = e.modo_guion;
    if (e.modo_guion) { dot.className = "dot ok"; txt.textContent = "Modo guion"; sel.hidden = true; }
    else if (desc) {
      const [m, d] = desc; dot.className = "dot carga";
      txt.textContent = `Descargando ${m}… ${d.total ? Math.round(100 * d.completado / d.total) : 0} %`; sel.hidden = true;
    } else if (!e.conectado) { dot.className = "dot mal"; txt.textContent = "Sin Ollama"; sel.hidden = true; }
    else if (!e.indice.listo) { dot.className = "dot carga"; txt.textContent = e.indice.error || "Indexando corpus…"; sel.hidden = true; }
    else {
      dot.className = "dot ok"; txt.textContent = ""; sel.hidden = false;
      if (sel.options.length !== e.instalados.length) sel.innerHTML = e.instalados.map(m => `<option>${esc(m)}</option>`).join("");
      sel.value = e.modelo;
    }
    if (e.error && !desc) { txt.textContent = e.error; dot.className = "dot mal"; }
    setTimeout(estado, (desc || !e.indice.listo) ? 1500 : 6000);
  } catch { $("#estado .dot").className = "dot mal"; $("#estado-txt").textContent = "Sin servidor"; setTimeout(estado, 3000); }
}
$("#modelo").onchange = e => fetch("/api/config", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ modelo: e.target.value }) });
$("#guion").onchange = e => fetch("/api/config", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ modo_guion: e.target.checked }) }).then(estado);

function toast(t) { const d = document.createElement("div"); d.className = "toast"; d.textContent = t; document.body.append(d); setTimeout(() => d.remove(), 4200); }

// ---------- escenarios ----------
function pintarEscenarios() {
  $("#escenarios").innerHTML = S.catalogo.escenarios.map(e => `
    <div class="esc ${S.esc === e.id ? "abierto" : ""}" data-e="${e.id}">
      <button><span class="letra">${e.id}</span><span><div class="nom">${esc(e.titulo)}</div><div class="sl">Slides ${e.slides}</div></span></button>
      <div class="cuerpo"><p class="idea">${esc(e.idea)}</p>
        ${[e.pregunta_id, ...(e.otras || [])].map(qid => `
          ${(e.otras || []).length ? `<div class="sl" style="margin:6px 0 4px">${esc(pregunta(qid).pregunta)}</div>` : ""}
          <div class="vars">${e.variantes.map((v, i) => `<button class="var ${v.bien ? "bien" : ""}" data-q="${qid}" data-i="${i}">${v.bien ? ic("check") : ""}${esc(v.nombre)}</button>`).join("")}</div>`).join("")}
      </div>
    </div>`).join("");
  $$(".esc > button").forEach(b => b.onclick = () => { const id = b.parentElement.dataset.e; S.esc = S.esc === id ? null : id; pintarEscenarios(); });
  $$(".var").forEach(b => b.onclick = () => correrVariante(b));
}
const pregunta = id => S.catalogo.preguntas.find(q => q.id === id);

async function correrVariante(b) {
  if (S.ocupado) return;
  const e = S.catalogo.escenarios.find(x => x.id === b.closest(".esc").dataset.e);
  const v = e.variantes[Number(b.dataset.i)], q = pregunta(b.dataset.q);
  S.cfg = { ...BASE, perfil: q.perfil, tenant: q.tenant_id || "mesaya-ar", ...v.config };
  pintarControles();
  b.classList.add("corriendo");
  S.tab = e.panel; pintarTabs();
  await enviar(q.pregunta, q.id, q.historial || [], `${e.id} · ${v.nombre}`);
  b.classList.remove("corriendo"); b.classList.add("hecho");
}

// ---------- chat ----------
$("#enviar").onclick = () => { const t = $("#pregunta").value.trim(); if (t) enviar(t, S.ejemplo?.pregunta === t ? S.ejemplo.id : null, S.ejemplo?.pregunta === t ? (S.ejemplo.historial || []) : []); };
$("#pregunta").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#enviar").click(); } });
$("#limpiar").onclick = () => { S.turnos = []; S.sel = null; pintarChat(); pintarPanel(); $$(".var").forEach(b => b.classList.remove("hecho")); };
$("#ejemplos").onchange = e => {
  const q = pregunta(e.target.value); if (!q) return;
  S.ejemplo = q; $("#pregunta").value = q.pregunta;
  S.cfg.perfil = q.perfil; S.cfg.tenant = q.tenant_id || "mesaya-ar"; pintarControles();
  e.target.value = "";
  $("#pregunta").focus();
};

async function enviar(texto, pregunta_id, historial, etiqueta) {
  if (S.ocupado) return;
  S.ocupado = true; $("#enviar").disabled = true; $("#pregunta").value = "";
  const turno = { pregunta: texto, etiqueta, cfg: { ...S.cfg }, historial, cargando: true };
  S.turnos.push(turno); S.sel = S.turnos.length - 1; pintarChat(); pintarPanel();
  try {
    const r = await fetch("/api/turno", { method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ pregunta: texto, pregunta_id, historial, config: S.cfg }) });
    const d = await r.json();
    if (!r.ok) throw new Error(d.detail || r.statusText);
    turno.traza = d;
  } catch (e) { turno.error = e.message; toast(e.message); }
  turno.cargando = false; S.ocupado = false; $("#enviar").disabled = false;
  pintarChat(); pintarPanel();
}

function textoConCitas(t, traza) {
  const ctx = traza?.recuperacion?.contexto || [];
  return esc(t).replace(/\[(\d+)\]/g, (_, n) => {
    const f = ctx[Number(n) - 1];
    return `<span class="cita ${f ? "" : "mala"}" title="${f ? esc(f.titulo + " › " + f.seccion) : "Cita a un fragmento que no existe"}">${n}</span>`;
  });
}

function chipsTurno(t) {
  const d = t.traza, c = t.cfg, out = [];
  if (t.etiqueta) out.push(`<span class="chip grad">${esc(t.etiqueta)}</span>`);
  if (!d) return out.join("");
  const r = d.ruta?.ruta;
  if (c.enrutar) out.push(`<span class="chip azul">${ic({ documentos: "file", datos: "db", en_vivo: "bolt", directo: "notes" }[r] || "file")}${{ documentos: "RAG", datos: "SQL", en_vivo: "API", directo: "Prompt" }[r]}</span>`);
  if (r === "documentos") out.push(`<span class="chip">${{ sin: "Sin evidencia", basico: "Sin reglas", estricto: "Estricto" }[c.grounding]} · k ${c.k}</span>`);
  if (d.abstencion) out.push(`<span class="chip warn">${ic("help")}Dijo «no sé»</span>`);
  const v = d.verificacion;
  if (v?.fidelidad != null) out.push(`<span class="chip ${v.fidelidad >= .99 ? "ok" : "mal"}">${ic("cite")}Fiel ${pct(v.fidelidad)}</span>`);
  if (v?.correcta) out.push(`<span class="chip ${v.correcta === "si" ? "ok" : v.correcta === "parcial" ? "warn" : "mal"}">${ic(v.correcta === "si" ? "check" : "x")}${{ si: "Correcta", parcial: "Parcial", no: "Incorrecta" }[v.correcta]}</span>`);
  if (d.recuperacion?.metricas?.violaciones?.length) out.push(`<span class="chip mal">${ic("alert")}Fuente prohibida</span>`);
  out.push(`<span class="chip">${ic("clock")}${(d.ms_total / 1000).toFixed(1)} s</span>`);
  if (d.grabado) out.push(`<span class="chip vio" title="Reproducido desde la grabación">Grabado ${esc(d.grabado)}</span>`);
  return out.join("");
}

function pintarChat() {
  const m = $("#mensajes");
  if (!S.turnos.length) { m.innerHTML = VACIO; return; }
  m.innerHTML = S.turnos.map((t, i) => `
    <div class="msg-u">${esc(t.pregunta)}</div>
    <div class="msg-a ${S.sel === i ? "sel" : ""} ${t.error ? "err" : ""}" data-i="${i}">
      <div class="meta">${chipsTurno(t)}</div>
      <div class="txt">${t.cargando ? `<span class="pensando"><i></i><i></i><i></i></span>` : t.error ? esc(t.error) : textoConCitas(t.traza.respuesta.texto, t.traza)}</div>
    </div>`).join("");
  $$(".msg-a").forEach(el => el.onclick = () => { S.sel = Number(el.dataset.i); pintarChat(); pintarPanel(); });
  m.scrollTop = m.scrollHeight;
}

// ---------- pestañas ----------
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); pintarPanel(); }
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); });

const turnoSel = () => S.turnos[S.sel];
function pintarPanel() {
  const p = $("#panel"), t = turnoSel();
  if (S.tab === "evaluacion") return pintarEvaluacion(p);
  if (!t) { p.innerHTML = `<div class="panel-vacio">${ic("search", "ic")}<p>Todavía no hay respuestas.<br>Corré un escenario para ver su recorrido.</p></div>` + glosario(S.tab); return; }
  if (t.cargando) { p.innerHTML = `<div class="panel-vacio"><span class="pensando"><i></i><i></i><i></i></span><p>Recuperando, generando y verificando…</p></div>`; return; }
  if (t.error) { p.innerHTML = `<div class="aviso">${ic("alert")}<div>${esc(t.error)}</div></div>`; return; }
  ({ recuperacion: pintarRecuperacion, grounding: pintarGrounding, diagnostico: pintarDiagnostico, contexto: pintarContexto })[S.tab](p, t.traza, t);
}

function glosario(tab) {
  const G = {
    recuperacion: `<p><b>Precision@k:</b> de lo que entró al contexto, cuánto sirve.</p><p><b>Recall@k:</b> de todo lo que servía, cuánto entró.</p><p><b>Chunk:</b> el pedazo de documento que se indexa y se recupera.</p>`,
    grounding: `<p><b>Grounding:</b> que cada afirmación esté respaldada por la evidencia que le dimos al modelo.</p><p><b>Fiel no es correcto:</b> una respuesta fiel a un documento viejo sigue estando mal.</p>`,
    diagnostico: `<p><b>Primero la recuperación:</b> si el fragmento correcto no llega, ningún prompt lo arregla.</p>`,
    contexto: `<p><b>Contexto:</b> todo lo que recibe el modelo en esta llamada. Lo que no está acá, no existe para él.</p>`,
  };
  return G[tab] ? `<div class="glos">${G[tab]}</div>` : "";
}

const autoridadChip = f => ({ oficial: `<span class="chip ok">oficial</span>`, comunicacion: `<span class="chip warn">comunicación</span>`,
                              historica: `<span class="chip mal">histórica</span>` }[f.autoridad]);

function pintarRuta(d) {
  const r = d.ruta.ruta;
  const D = [["documentos", "file", "Documentos", "RAG"], ["datos", "db", "Datos", "SQL"], ["en_vivo", "bolt", "En vivo", "API"], ["directo", "notes", "Fijo", "Prompt"]];
  return `<div class="card"><h4>${ic("split")}¿De dónde sale la respuesta?</h4>
    <div class="destinos">${D.map(([k, i, n, s]) => `<div class="destino ${r === k ? "on" : ""}">${ic(i)}<b>${n}</b>${s}</div>`).join("")}</div>
    <div style="font-size:13px;color:var(--muted)">${d.config.enrutar ? esc(d.ruta.motivo) : "Router apagado: todo va a RAG, aunque la pregunta no sea para documentos."}</div></div>`;
}

function pintarHerramienta(h) {
  let res = "";
  if (h.tipo === "SQL") {
    const r = h.resultado;
    res = r.error ? `<div class="aviso">${esc(r.error)}</div>` :
      `<table class="t"><tr>${r.columnas.map(c => `<th>${esc(c)}</th>`).join("")}</tr>${r.filas.slice(0, 12).map(f => `<tr>${f.map(x => `<td>${esc(x)}</td>`).join("")}</tr>`).join("")}</table>`;
  } else if (h.tipo === "API") {
    const r = h.resultado;
    res = r.error ? `<div class="aviso">${esc(r.error)}</div>` : `<table class="t"><tr><th>Hora</th><th>Lugares libres</th><th></th></tr>${r.turnos.map(x =>
      `<tr><td><b>${x.hora}</b></td><td>${x.lugares_libres}</td><td>${x.disponible ? `<span class="chip ok">disponible</span>` : `<span class="chip mal">lleno</span>`}</td></tr>`).join("")}</table>
      <div style="font-size:12px;color:var(--muted);margin-top:6px">Consultado a las ${r.consultado}: el dato tiene segundos de vida.</div>`;
  } else res = `<pre class="bloque">${esc(h.resultado)}</pre>`;
  return `<div class="card"><h4>${ic(h.tipo === "SQL" ? "db" : h.tipo === "API" ? "bolt" : "notes")}${h.tipo}</h4><pre class="bloque" style="margin-bottom:10px">${esc(h.pedido)}</pre>${res}</div>`;
}

function pintarRecuperacion(p, d) {
  let h = pintarRuta(d);
  if (d.herramienta) { p.innerHTML = h + pintarHerramienta(d.herramienta) + `<div class="glos"><p><b>No todo es RAG:</b> números exactos van a SQL, datos que cambian minuto a minuto van a una API y lo corto y fijo va directo en el prompt.</p></div>`; return; }
  const rec = d.recuperacion, m = rec.metricas, gold = d.pregunta_id != null;
  h += gold ? `<div class="kpis">
      <div class="kpi"><div class="n">PRECISION@${m.k}</div><div class="v">${pct(m.precision)}</div><div class="s">${m.relevantes_en_k ?? "—"} de ${m.en_contexto} sirven</div></div>
      <div class="kpi"><div class="n">RECALL@${m.k}</div><div class="v">${pct(m.recall)}</div><div class="s">${m.relevantes_en_k ?? "—"} de ${m.relevantes_total ?? "—"} relevantes</div></div>
      <div class="kpi ${m.hit ? "" : "mal"}"><div class="n">HIT@${m.k}</div><div class="v">${m.hit ? "Sí" : "No"}</div><div class="s">${m.primer_relevante ? "1.º relevante: puesto " + m.primer_relevante : "ninguno relevante"}</div></div>
      <div class="kpi ${m.violaciones.length ? "mal" : ""}"><div class="n">FUENTES PROHIBIDAS</div><div class="v">${m.violaciones.length}</div><div class="s">${esc(m.violaciones.join(", ") || "ninguna")}</div></div>
    </div>` : `<div class="aviso info">${ic("help")}<div>Pregunta libre: sin un set de evaluación no se sabe qué fragmento era el correcto, así que no hay precision ni recall. Cargá una pregunta de evaluación para medirlos.</div></div>`;
  h += `<div class="card"><h4>${ic("columns")}Los ${rec.candidatos.length} mejores candidatos de ${rec.total_chunks} chunks</h4>
    <div class="tira">${rec.candidatos.map(f => `<div class="celda ${f.en_contexto ? "ctx" : ""} ${f.relevante ? "rel" : ""}" title="${esc(f.titulo)}">${f.pos}</div>`).join("")}</div>
    <div class="leyenda"><span><i class="celda ctx"></i>entró al contexto</span>${gold ? `<span><i class="celda rel ctx"></i>relevante y entró</span><span><i class="celda rel"></i>relevante que quedó afuera</span>` : ""}</div></div>`;
  if (d.config.grounding === "sin") h += `<div class="aviso">${ic("eye")}<div>Modo <b>sin evidencia</b>: el modelo <b>no</b> vio estos fragmentos. Se buscan igual para poder verificar la respuesta.</div></div>`;
  if (rec.rerank) h += `<div class="aviso info">${ic("layers")}<div><b>Etapa 2 · reranking:</b> el LLM puntuó de 0 a 3 los 12 primeros (${(rec.rerank.ms / 1000).toFixed(1)} s). Las flechas muestran cuánto se movió cada uno.</div></div>`;
  let corte = false;
  h += rec.candidatos.map(f => {
    let linea = "";
    if (!corte && !f.en_k) { corte = true; linea = `<div class="corte">corte en k = ${d.config.k}</div>`; }
    const mov = rec.rerank && f.pos_etapa1 ? f.pos_etapa1 - f.pos : 0;
    return linea + `<div class="cand ${f.en_contexto ? "ctx" : "fuera"}">
      <div class="fila"><span class="pos">${f.pos}</span>
        <div><div class="tit">${esc(f.titulo)} <small>› ${esc(f.seccion)}</small></div>
          <div class="badges">${f.relevante ? `<span class="chip ok">${ic("check")}relevante</span>` : ""}
            <span class="chip ${f.estado === "vigente" ? "" : "mal"}">v${f.version} · ${esc(f.fecha.slice(0, 7))}${f.estado === "vigente" ? "" : " · " + esc(f.estado)}</span>
            ${autoridadChip(f)}${f.bajo_umbral ? `<span class="chip warn">bajo el umbral</span>` : ""}<span class="chip">${f.tokens} tok</span></div></div>
        <div class="nums">sim <b>${f.sim.toFixed(2)}</b><br>bm25 ${f.rank_bm25 ? "#" + f.rank_bm25 : "—"} · sem #${f.rank_sem}
          ${f.rerank != null ? `<br>rerank <b>${f.rerank}/3</b> ${mov ? `<span class="mueve ${mov > 0 ? "sube" : "baja"}">${mov > 0 ? "▲" : "▼"}${Math.abs(mov)}</span>` : ""}` : ""}</div></div>
      <div class="texto">${esc(f.texto)}</div></div>`;
  }).join("");
  if (rec.excluidos.length) h += `<div class="rotulo" style="margin-top:16px">Excluidos por filtros antes de entrar</div>` + rec.excluidos.map(f =>
    `<div class="cand fuera"><div class="fila"><span class="pos">${ic("ban")}</span><div><div class="tit">${esc(f.titulo)} <small>› ${esc(f.seccion)}</small></div>
      <div class="badges"><span class="chip mal">${esc(f.motivo)}</span></div></div><div class="nums">sim ${f.sim.toFixed(2)}</div></div><div class="texto">${esc(f.texto)}</div></div>`).join("");
  p.innerHTML = h + glosario("recuperacion");
  p.querySelectorAll(".cand .fila").forEach(f => f.onclick = () => f.parentElement.classList.toggle("abierto"));
}

function pintarGrounding(p, d) {
  const v = d.verificacion;
  if (d.herramienta) { p.innerHTML = `<div class="aviso info">${ic("split")}<div>Esta respuesta no salió de documentos sino de <b>${d.herramienta.tipo}</b>: se apoya en el resultado de la herramienta, que está en la pestaña Recuperación.</div></div>`; return; }
  if (!v) { p.innerHTML = `<div class="aviso info">${ic("help")}<div>La verificación está apagada. Prendé <b>Verificar cada oración</b> para ver el respaldo de cada afirmación.</div></div>` + glosario("grounding"); return; }
  const fid = v.fidelidad;
  let h = d.respuesta.corte ? `<div class="aviso info">${ic("filecheck")}<div><b>Evidencia insuficiente.</b> ${esc(d.respuesta.corte)}</div></div>` : "";
  if (d.config.grounding === "sin") h += `<div class="aviso">${ic("eye")}<div>El modelo respondió <b>sin ver documentos</b>. Cada oración se contrasta con lo que habría encontrado la búsqueda.</div></div>`;
  h += `<div class="medidor"><div class="anillo ${fid != null && fid < .99 ? "mal" : ""}" style="--p:${fid == null ? 0 : fid * 100}"><span>${fid == null ? "—" : pct(fid)}</span></div>
    <div><h3 style="font-size:16px;margin-bottom:4px">Fidelidad a la evidencia</h3>
    <div style="font-size:13px;color:var(--muted)">${fid == null ? "El asistente dijo que no sabe: no hay afirmaciones que verificar." : `${v.oraciones.filter(o => o.respaldo === "respaldada").length} de ${v.oraciones.length} oraciones respaldadas.`}</div>
    ${v.correcta ? `<div style="margin-top:6px">${{ si: `<span class="chip ok">${ic("check")}Correcta</span>`, parcial: `<span class="chip warn">Parcial</span>`, no: `<span class="chip mal">${ic("x")}Incorrecta</span>` }[v.correcta]}
      <span style="font-size:12.5px;color:var(--muted)"> ${esc(v.motivo_correcta)}</span></div>` : ""}</div></div>`;
  if (d.clave) h += `<div class="card" style="font-size:13px"><b style="color:var(--azul-1)">Respuesta de referencia:</b> ${esc(d.clave)}</div>`;
  const ctx = d.recuperacion?.contexto || [];
  h += v.oraciones.map((o, i) => {
    const iconos = { respaldada: "check", parcial: "help", sin_respaldo: "x", abstencion: "help" };
    const nombres = { respaldada: "Respaldada", parcial: "Parcial", sin_respaldo: "Sin respaldo", abstencion: "Abstención" };
    const frs = (o.fragmentos || []).map(n => ctx[n - 1]).filter(Boolean);
    return `<div class="orac ${o.respaldo}" style="animation-delay:${i * .08}s"><span class="marca">${ic(iconos[o.respaldo])}</span>
      <div><div class="o">${textoConCitas(o.texto, d)}</div>
      <div class="cual"><b>${nombres[o.respaldo]}</b> · ${esc(o.motivo)}${frs.length ? " · " + frs.map(f => esc(f.document_id)).join(", ") : ""}
        ${o.citas_invalidas?.length ? ` · <span style="color:var(--bad)">cita [${o.citas_invalidas.join("], [")}] inexistente</span>` : ""}</div>
      ${o.cita_textual ? `<blockquote class="${o.cita_encontrada ? "" : "no"}">«${esc(o.cita_textual)}»</blockquote>` : ""}</div></div>`;
  }).join("");
  p.innerHTML = h + `<div class="glos"><p><b>Provenance:</b> el verificador copia la frase textual que respalda cada oración y el código comprueba que exista en la evidencia. Si no aparece, la oración queda sin respaldo.</p>
    <p><b>Fiel no es correcto:</b> una respuesta fiel a un documento viejo sigue estando mal.</p></div>`;
}

function pintarDiagnostico(p, d) {
  const nodos = d.diagnostico || [];
  if (!nodos.length) { p.innerHTML = `<div class="panel-vacio">Sin diagnóstico para esta pregunta.</div>`; return; }
  const iFalla = nodos.findIndex(n => n.estado === "falla");
  const f = nodos[iFalla];
  let h = iFalla < 0 ? `<div class="veredicto">${ic("filecheck")}<div><b>Todo el recorrido funcionó.</b><br><span style="font-size:13px">Recuperación, contexto y respuesta pasan los controles.</span></div></div>`
                     : `<div class="veredicto mal">${ic("alert")}<div><b>Se rompió en: ${esc(f.pregunta.replace(/[¿?]/g, ""))}</b><br><span style="font-size:13px">${esc(f.arreglo || f.detalle)}</span></div></div>`;
  h += `<div class="arbol">` + nodos.map((n, i) => `${i ? `<div class="flecha"></div>` : ""}
    <div class="nodo ${n.estado} ${iFalla >= 0 && i > iFalla ? "despues" : ""}" style="animation-delay:${i * .12}s">
      <span class="estado">${ic(n.estado === "ok" ? "check" : n.estado === "falla" ? "x" : "help")}</span>
      <div><h5>${esc(n.pregunta)}</h5><p>${esc(n.detalle)}</p>${n.arreglo ? `<div class="arreglo">${ic("pen")} ${esc(n.arreglo)}</div>` : ""}</div></div>`).join("") + `</div>`;
  p.innerHTML = h + glosario("diagnostico");
}

function pintarContexto(p, d) {
  const total = d.etapas.reduce((a, e) => a + e.ms, 0) || 1;
  const colores = ["#0D0B92", "#1B4BB9", "#4F8ED5", "#3398DC", "#510371"];
  let h = `<div class="card"><h4>${ic("clock")}Tiempo por etapa · ${(d.ms_total / 1000).toFixed(1)} s</h4>
    <div class="etapas">${d.etapas.map((e, i) => `<i style="flex:${Math.max(e.ms, total * .06)};background:${colores[i % 5]}">${esc(e.nombre)}</i>`).join("")}</div>
    <div style="font-size:12px;color:var(--muted)">${d.etapas.map(e => `${esc(e.nombre)} ${(e.ms / 1000).toFixed(1)} s`).join(" · ")} · tokens del prompt: <b>${d.respuesta.tokens_prompt || "—"}</b> · de la respuesta: <b>${d.respuesta.tokens_respuesta || "—"}</b></div></div>`;
  if (!d.mensajes.length) h += `<div class="aviso info">${ic("ban")}<div>No se llamó al modelo: ${esc(d.respuesta.corte || "")}</div></div>`;
  h += d.mensajes.map(m => `<div class="msj"><div class="rol">${{ system: "Sistema", user: "Usuario", assistant: "Asistente" }[m.role]}</div><pre class="bloque">${esc(m.content)}</pre></div>`).join("");
  p.innerHTML = h + glosario("contexto");
}

// ---------- evaluación (slide 15) ----------
async function correrEvalRecuperacion(k) {
  S.eval = { cargando: true }; pintarPanel();
  const r = await fetch("/api/evaluar/recuperacion", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ k }) });
  S.eval = r.ok ? await r.json() : { error: (await r.json()).detail };
  pintarPanel();
}
async function correrEvalRespuestas() {
  S.evalResp = { filas: [], cfg: { ...S.cfg } }; pintarPanel();
  const r = await fetch("/api/evaluar/respuestas", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ config: S.cfg }) });
  const lector = r.body.getReader(), dec = new TextDecoder(); let buf = "";
  for (;;) {
    const { value, done } = await lector.read(); if (done) break;
    buf += dec.decode(value, { stream: true });
    let i; while ((i = buf.indexOf("\n")) >= 0) { S.evalResp.filas.push(JSON.parse(buf.slice(0, i))); buf = buf.slice(i + 1); if (S.tab === "evaluacion") pintarPanel(); }
  }
  S.evalResp.fin = true; pintarPanel();
}

function barra(v) { return `<div class="num-bar"><div class="barra"><i style="width:${(v ?? 0) * 100}%"></i></div><b>${pct(v)}</b></div>`; }

function pintarEvaluacion(p) {
  const E = S.eval, R = S.evalResp;
  let h = `<div class="card"><h4>${ic("target")}Recuperación: todas las configuraciones contra el set de evaluación</h4>
    <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap"><span style="font-size:13px">k =</span>
      <select class="sel" id="ek" style="width:70px">${[1, 2, 3, 4, 5, 8].map(k => `<option ${k === (E?.k || S.cfg.k) ? "selected" : ""}>${k}</option>`).join("")}</select>
      <button class="btn mini" id="eval-rec">${ic("bolt")}Evaluar recuperación</button>
      <span style="font-size:12px;color:var(--muted)">Sin LLM: segundos.</span></div></div>`;
  if (E?.cargando) h += `<div class="panel-vacio"><span class="pensando"><i></i><i></i><i></i></span></div>`;
  else if (E?.error) h += `<div class="aviso">${esc(E.error)}</div>`;
  else if (E) {
    const mejor = Math.max(...E.filas.map(f => f.hit + f.mrr));
    h += `<table class="t"><tr><th>Configuración</th><th>Hit@${E.k}</th><th>MRR</th><th>Recall</th><th>Precision</th><th>Prohib.</th></tr>` +
      E.filas.map(f => `<tr class="${f.hit + f.mrr === mejor ? "mejor" : ""}"><td><b>${esc(f.nombre)}</b></td><td>${barra(f.hit)}</td><td>${barra(f.mrr)}</td><td>${barra(f.recall)}</td><td>${barra(f.precision)}</td>
        <td>${f.violaciones ? `<span class="chip mal">${f.violaciones}</span>` : `<span class="chip ok">0</span>`}</td></tr>`).join("") + `</table>`;
    h += `<div class="rotulo" style="margin-top:16px">Puesto del primer fragmento relevante</div><div style="overflow-x:auto"><table class="t mat"><tr><th></th>${E.filas.map((f, i) => `<th title="${esc(f.nombre)}">C${i + 1}</th>`).join("")}</tr>` +
      E.preguntas.map((q, qi) => `<tr><td title="${esc(q.pregunta)}" style="font-size:12px;max-width:220px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis"><b>${q.id}</b> ${esc(q.pregunta)}</td>${E.filas.map(f => {
        const x = f.detalle[qi]; const cls = x.hit === null ? "" : x.hit ? "h" : "m";
        return `<td class="c ${cls} ${x.violacion ? "v" : ""}" title="${x.violacion ? "Entró una fuente prohibida" : ""}">${x.hit === null ? (x.violacion ? "fuga" : "ok") : (x.pos ?? "—")}</td>`;
      }).join("")}</tr>`).join("") + `</table></div><div style="font-size:12px;color:var(--muted);margin-top:6px">${E.filas.map((f, i) => `C${i + 1} = ${esc(f.nombre)}`).join(" · ")}. Borde violeta: entró una fuente prohibida.</div>`;
  }
  h += `<div class="card" style="margin-top:16px"><h4>${ic("cite")}Respuestas: fidelidad, corrección y «no sé» con la configuración actual</h4>
    <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap"><button class="btn mini" id="eval-resp" ${R && !R.fin ? "disabled" : ""}>${ic("bolt")}Evaluar respuestas</button>
    <span style="font-size:12px;color:var(--muted)">Usa el LLM como juez: varios minutos.</span></div></div>`;
  if (R) {
    const ok = R.filas.filter(f => !f.error);
    const fids = ok.filter(f => f.fidelidad != null).map(f => f.fidelidad);
    const corr = ok.filter(f => f.correcta);
    const abst = ok.filter(f => f.abstencion_correcta !== null && f.abstencion_correcta !== undefined);
    const n = R.filas[0]?.n || "…";
    h += `<div class="kpis"><div class="kpi"><div class="n">AVANCE</div><div class="v">${R.filas.length}/${n}</div></div>
      <div class="kpi"><div class="n">FIDELIDAD</div><div class="v">${fids.length ? pct(fids.reduce((a, b) => a + b, 0) / fids.length) : "—"}</div></div>
      <div class="kpi"><div class="n">CORRECTAS</div><div class="v">${corr.length ? pct(corr.filter(f => f.correcta === "si").length / corr.length) : "—"}</div></div>
      <div class="kpi"><div class="n">«NO SÉ» BIEN DICHO</div><div class="v">${abst.length ? pct(abst.filter(f => f.abstencion_correcta).length / abst.length) : "—"}</div></div></div>
      <table class="t"><tr><th>Pregunta</th><th>Fiel</th><th>Correcta</th></tr>${R.filas.map(f => f.error ? `<tr><td colspan="3">${f.id}: ${esc(f.error)}</td></tr>` :
        `<tr><td style="font-size:12.5px"><b>${f.id}</b> ${esc(f.pregunta)}<div style="color:var(--muted);margin-top:3px">${esc(f.respuesta.slice(0, 150))}</div></td>
        <td>${f.fidelidad == null ? (f.abstencion ? `<span class="chip warn">no sé</span>` : "—") : `<span class="chip ${f.fidelidad >= .99 ? "ok" : "mal"}">${pct(f.fidelidad)}</span>`}</td>
        <td>${f.abstencion_correcta != null ? `<span class="chip ${f.abstencion_correcta ? "ok" : "mal"}">${f.abstencion_correcta ? "se abstuvo" : "inventó"}</span>` :
          f.correcta ? `<span class="chip ${f.correcta === "si" ? "ok" : f.correcta === "parcial" ? "warn" : "mal"}">${f.correcta}</span>` : "—"}</td></tr>`).join("")}</table>`;
  }
  p.innerHTML = h + `<div class="glos"><p><b>Hit@k:</b> en cuántas preguntas entró al menos un fragmento correcto. <b>MRR:</b> qué tan arriba quedó el primero.</p>
    <p><b>Medí en tu corpus:</b> no hay configuración que gane siempre; el set de evaluación es lo que decide.</p></div>`;
  $("#eval-rec").onclick = () => correrEvalRecuperacion(Number($("#ek").value));
  $("#eval-resp").onclick = correrEvalRespuestas;
}

// ---------- inicio ----------
(async () => {
  pintarControles(); estado();
  S.catalogo = await (await fetch("/api/catalogo")).json();
  $("#ejemplos").innerHTML += S.catalogo.preguntas.map(q => `<option value="${q.id}">${q.id} · ${esc(q.pregunta)} (${q.perfil})</option>`).join("");
  pintarEscenarios(); pintarPanel();
})();
