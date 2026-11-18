// MesaYa · Arquitectura, control y human-in-the-loop — interfaz.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
function poner(el, html) { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; }
const seg = ms => (ms / 1000).toFixed(1).replace(".", ",") + " s";
const mil = n => Number(n || 0).toLocaleString("es-AR");
const plata = n => "$" + mil(n);
const usd = x => "USD " + Number(x || 0).toFixed(3).replace(".", ",");
const json = o => JSON.stringify(o, null, 2);

const S = { cat: null, cfg: {}, base: {}, chat: [], sel: null, tab: "recorrido", esc: null, hecho: {}, ocupado: false, datos: null, adjunto: null };

async function llamar(url, body) {
  const r = await post(url, body), d = await r.json();
  if (!r.ok) throw new Error(d.detail || r.statusText);
  return d;
}
function toast(t) { const d = document.createElement("div"); d.className = "toast"; d.textContent = t; document.body.append(d); setTimeout(() => d.remove(), 4500); }

// ---------- controles ----------
function pintarControles() {
  $$("[data-k]").forEach(el => { const v = S.cfg[el.dataset.k]; if (el.type === "checkbox") el.checked = !!v; else el.value = v; });
  $("#v-max_pasos").textContent = S.cfg.max_pasos;
  $("#v-costo_max").textContent = usd(S.cfg.costo_max);
  $("#v-limite").textContent = plata(S.cfg.limite);
  $$("#regla button").forEach(b => b.classList.toggle("on", b.dataset.v === S.cfg.regla));
}
$$("[data-k]").forEach(el => el.addEventListener("input", () => {
  S.cfg[el.dataset.k] = el.type === "checkbox" ? el.checked : Number(el.value); pintarControles();
}));
$$("#regla button").forEach(b => b.onclick = () => { S.cfg.regla = b.dataset.v; pintarControles(); });

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
          const k = `${e.id}${i}`, t = c.accion ? "La encargada aprueba el pedido pendiente (dos clics)" : (c.mensajes || [c.mensaje]).join("\n") + (c.imagen ? `\n[${c.imagen}]` : "");
          return `<button class="var ${c.bien ? "bien" : ""} ${S.hecho[k] === "corriendo" ? "corriendo" : S.hecho[k] ? "hecho" : ""}" data-i="${i}" title="${esc(t)}">${c.bien ? ic("check") : ""}${c.imagen ? ic("file") : ""}${c.accion ? ic("person") : ""}${esc(c.nombre)}</button>`;
        }).join("")}</div></div></div>`).join("");
  if (!poner($("#escenarios"), html)) return;
  $$(".esc > button").forEach(b => b.onclick = () => { const id = b.parentElement.dataset.e; S.esc = S.esc === id ? null : id; pintarEscenarios(); });
  $$(".var").forEach(b => b.onclick = () => correrEscenario(b.closest(".esc").dataset.e, Number(b.dataset.i)));
}

async function correrEscenario(id, i) {
  if (S.ocupado) return;
  const e = S.cat.escenarios.find(x => x.id === id), c = e.corridas[i], k = `${id}${i}`;
  S.cfg = { ...S.base, ...(c.config || {}) };
  pintarControles();
  if (c.reiniciar) await reiniciar();
  S.hecho[k] = "corriendo"; pintarEscenarios();
  let ok = true;
  if (c.accion) {
    const pend = (S.datos?.aprobaciones || []).filter(a => a.estado === "pendiente").at(-1)
              || (S.datos?.aprobaciones || []).at(-1);
    if (!pend) { toast("No hay ninguna aprobación pendiente: corré primero el pedido."); ok = false; }
    else { ok = await resolver(pend.id, true); if (ok && c.accion === "aprobar_doble") ok = await resolver(pend.id, true); }
  } else {
    const msgs = c.mensajes || [c.mensaje];
    for (let j = 0; j < msgs.length && ok; j++) ok = await enviar(msgs[j], j === 0 && c.imagen ? { ejemplo: c.imagen } : null);
  }
  S.tab = c.panel || e.panel; pintarTabs(); pintarPanel();
  S.hecho[k] = ok; pintarEscenarios();
}

// ---------- adjuntar imágenes ----------
$("#btn-adjuntar").onclick = () => {
  const m = $("#menu-adj");
  m.innerHTML = S.cat.imagenes.map(i => `<button data-img="${esc(i.archivo)}"><img src="/static/imagenes/${esc(i.archivo)}" alt="">${esc(i.nombre)}</button>`).join("")
    + `<button data-subir>${ic("file")}Subir una imagen…</button>`;
  m.hidden = !m.hidden;
  $$("#menu-adj [data-img]").forEach(b => b.onclick = () => { S.adjunto = { ejemplo: b.dataset.img }; m.hidden = true; pintarAdjunto(); });
  $("#menu-adj [data-subir]").onclick = () => { m.hidden = true; $("#archivo").click(); };
};
$("#archivo").onchange = e => {
  const f = e.target.files[0]; if (!f) return;
  const r = new FileReader();
  r.onload = () => { S.adjunto = { b64: String(r.result).split(",")[1], url: r.result, nombre: f.name }; pintarAdjunto(); };
  r.readAsDataURL(f); e.target.value = "";
};
const urlAdjunto = a => a.ejemplo ? `/static/imagenes/${a.ejemplo}` : a.url;
function pintarAdjunto() {
  const el = $("#adjunto");
  el.hidden = !S.adjunto;
  if (S.adjunto) {
    el.innerHTML = `<img src="${esc(urlAdjunto(S.adjunto))}" alt=""><span>${esc(S.adjunto.ejemplo || S.adjunto.nombre)}</span><button title="Quitar">${ic("x")}</button>`;
    el.querySelector("button").onclick = () => { S.adjunto = null; pintarAdjunto(); };
  }
}

// ---------- chat ----------
$("#enviar").onclick = () => { const t = $("#mensaje").value.trim(); if (t || S.adjunto) enviar(t, S.adjunto); };
$("#mensaje").addEventListener("keydown", e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#enviar").click(); } });
$("#reiniciar").onclick = async () => { await reiniciar(); S.hecho = {}; pintarEscenarios(); };

async function reiniciar() {
  await fetch("/api/datos", { method: "DELETE" });
  S.chat = []; S.sel = null;
  await cargarDatos(); pintarTodo();
}
async function cargarDatos() { S.datos = await (await fetch("/api/datos")).json(); }

async function cargarTodo() {
  await cargarDatos();
  const turnos = S.datos.turnos;
  S.chat = [];
  if (turnos.length) S.chat.push({ tipo: "sep", texto: `${turnos.length} ${turnos.length === 1 ? "turno guardado" : "turnos guardados"} · desde ${turnos[0].fecha}` });
  for (const r of turnos) {
    const img = r.imagen && S.cat.imagenes.some(i => i.archivo === r.imagen) ? `/static/imagenes/${r.imagen}` : null;
    S.chat.push(r.accion === "mensaje" ? { tipo: "u", texto: r.mensaje, img } : { tipo: "h", texto: r.mensaje }, { tipo: "a", res: r });
  }
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

function enviar(texto, adjunto) {
  $("#mensaje").value = ""; S.adjunto = null; pintarAdjunto();
  const body = { mensaje: texto, config: S.cfg };
  if (adjunto?.ejemplo) body.imagen_ejemplo = adjunto.ejemplo;
  else if (adjunto?.b64) { body.imagen = adjunto.b64; body.imagen_nombre = adjunto.nombre; }
  return correr({ tipo: "u", texto: texto || "Te mando esta imagen.", img: adjunto ? urlAdjunto(adjunto) : null },
    () => llamar("/api/turno", body));
}
function resolver(id, aprobar) {
  const a = (S.datos?.aprobaciones || []).find(x => x.id === id);
  return correr({ tipo: "h", texto: `${S.cat.encargada}: ${aprobar ? "aprueba" : "rechaza"} · ${a?.titulo ?? id}` },
    () => llamar("/api/aprobar", { id, aprobar, config: S.cfg }));
}

const RESULTADO = { respondida: ["ok", "respondida"], pendiente: ["vio", "pendiente de aprobación"], escalada: ["mal", "escalada"], repregunta: ["warn", "repregunta"],
                    confirmacion: ["warn", "pide confirmación"], revision: ["vio", "revisión humana"], sin_validar: ["mal", "cargada sin validar"],
                    inyeccion: ["warn", "instrucciones ignoradas"], aprobada: ["ok", "aprobada"], rechazada: ["mal", "rechazada"], repetida: ["warn", "ya estaba resuelta"],
                    sin_aprobacion: ["mal", "pagado sin aprobación"] };
const RUTA = { agente: ["bot", "agente con tools"], reembolso: ["route", "workflow de reembolso"], documento: ["file", "documento"],
               confirmacion: ["check", "confirmación"], aprobacion: ["person", "aprobación humana"] };

function pintarChat() {
  const el = $("#mensajes");
  if (!S.chat.length) {
    poner(el, `<div class="vacio"><img src="/static/isotipo-blanco.png" alt=""><h2>Control y human-in-the-loop</h2>
      <p>Escribile como ${esc(S.cat.cliente)}, adjuntá una factura o elegí un escenario. Los pedidos de aprobación llegan a la pestaña <b>Aprobaciones</b>.</p></div>`);
    return;
  }
  const html = S.chat.map((m, i) => {
    if (m.tipo === "sep") return `<div class="separador">${esc(m.texto)}</div>`;
    if (m.tipo === "u") return `<div class="msg-u">${esc(m.texto)}${m.img ? `<img src="${esc(m.img)}" alt="">` : ""}</div>`;
    if (m.tipo === "h") return `<div class="msg-r">${ic("person")}${esc(m.texto)}</div>`;
    if (m.cargando) return `<div class="msg-a"><span class="pensando"><i></i><i></i><i></i></span></div>`;
    if (m.error) return `<div class="msg-a err"><div class="txt">${esc(m.error)}</div></div>`;
    const r = m.res, [cls, txt] = RESULTADO[r.resultado] ?? ["", r.resultado], ruta = RUTA[r.ruta] ?? ["bot", r.ruta];
    const meta = `<div class="meta"><span class="chip ${cls}">${esc(txt)}</span><span class="chip azul">${ic(ruta[0])}${esc(ruta[1])}</span>
      <span class="chip">${r.resumen.llamadas} LLM · ${usd(r.resumen.costo)}</span>${r.grabado ? `<span class="chip">${ic("clock")}grabado</span>` : ""}</div>`;
    const resp = r.respuestas.length ? r.respuestas : [{ texto: "Sin mensaje a la clienta: no se repite nada.", nada: true }];
    return resp.map((x, j) => `<div class="msg-a ${S.sel === r ? "sel" : ""}" data-i="${i}"><div class="txt ${x.nada ? "nada" : ""}">${esc(x.texto)}</div>${j === resp.length - 1 ? meta : ""}</div>`).join("");
  }).join("");
  if (poner(el, html)) {
    $$(".msg-a[data-i]").forEach(d => d.onclick = () => { S.sel = S.chat[Number(d.dataset.i)].res; pintarChat(); pintarPanel(); });
    el.scrollTop = el.scrollHeight;
  }
}

// ---------- panel derecho ----------
function pintarTabs() { $$("#tabs button").forEach(b => b.classList.toggle("on", b.dataset.t === S.tab)); }
$$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); pintarPanel(); });

function pintarPanel() {
  const pend = (S.datos?.aprobaciones || []).filter(a => a.estado === "pendiente").length;
  $("#badge").hidden = !pend; $("#badge").textContent = pend;
  const f = { recorrido: panelRecorrido, aprobaciones: panelAprobaciones, arquitectura: panelArquitectura, llamadas: panelLlamadas }[S.tab];
  poner($("#panel"), f());
  $$(".paso-r > .fila").forEach(x => x.onclick = () => x.parentElement.classList.toggle("abierto"));
  $$(".llamada > div:first-child").forEach(x => x.onclick = () => x.parentElement.classList.toggle("abierta"));
  $$("[data-aprobar]").forEach(b => b.onclick = () => resolver(b.dataset.aprobar, b.dataset.si === "1"));
}

const vacio = t => `<div class="panel-vacio">${t}</div>`;

function recorrido(pasos, total) {
  if (!pasos.length) return "";
  const ICONO = { llm: "bot", tool: "gear", gate: "funnel", fin: "stop", codigo: "code", checkpoint: "file", humano: "person", saltado: "refresh" };
  let grupo = null, html = "";
  for (const p of pasos) {
    if ((p.grupo || null) !== grupo) { grupo = p.grupo || null; if (grupo) html += `<div class="tl-grupo">${esc(grupo)}</div>`; }
    const iz = total ? 100 * p.inicio / total : 0, an = total ? Math.max(0.6, 100 * p.ms / total) : 0;
    const num = [p.ms ? `<b>${seg(p.ms)}</b>` : "", p.tokens ? `${mil(p.tokens)} tok` : "", p.costo ? usd(p.costo) : ""].filter(Boolean).join("<br>");
    const salida = p.salida == null ? "" : typeof p.salida === "string" ? p.salida : json(p.salida);
    html += `<div class="paso-r ${p.tipo} ${p.marca ? "m-" + p.marca : ""}"><div class="fila"><span class="tipo">${ic(ICONO[p.tipo] || "code")}</span>
      <div><div class="t">${esc(p.titulo)}</div><div class="d">${esc(p.detalle)}</div></div><div class="num">${num}</div></div>
      ${p.ms ? `<div class="gantt"><i style="left:${iz}%;width:${an}%"></i></div>` : ""}
      ${salida ? `<div class="salida-paso"><pre class="bloque">${esc(salida)}</pre></div>` : ""}</div>`;
  }
  return html + `<div class="eje"><span>0 s</span><span>${seg(total)}</span></div>`;
}

function frenos(c) {
  const fin = { finalizar: ["ok", "señal de fin: finalizar()"], max_pasos: ["mal", "cortó: máximo de pasos"], costo: ["mal", "cortó: costo"],
                timeout: ["mal", "cortó: timeout"], loop: ["mal", "cortó: loop detectado"], confirmacion: ["warn", "frenó: pide confirmación"] }[c.fin] ?? ["", c.fin];
  const b = (n, v, max, txt, lleno) => `<div class="freno ${lleno ? "lleno" : ""}"><div class="n">${n}<b>${txt}</b></div><div class="barra"><i style="width:${Math.min(100, 100 * v / max)}%"></i></div></div>`;
  return `<div class="frenos">${b("Pasos", c.pasos, c.max_pasos, `${c.pasos} de ${c.max_pasos}`, c.fin === "max_pasos")}
      ${b("Costo", c.costo, c.costo_max, `${usd(c.costo)} de ${usd(c.costo_max)}`, c.fin === "costo")}
      ${b("Tiempo", c.ms, c.timeout_ms, `${seg(c.ms)} de ${seg(c.timeout_ms)}`, c.fin === "timeout")}</div>
    <div class="senales"><span class="chip ${fin[0]}">${esc(fin[1])}</span>
      ${c.objetivo === true ? `<span class="chip ok">${ic("check")}objetivo verificado</span>` : c.objetivo === false ? `<span class="chip mal">objetivo no cumplido</span>` : ""}
      <span class="chip ${c.repeticiones >= 2 ? "warn" : ""}">misma acción seguida: ${c.repeticiones}×</span>
      <span class="chip">detector de loops ${c.detector ? "prendido" : "apagado"}</span></div>`;
}

function documento(r) {
  const d = r.extra.documento; if (!d) return "";
  const img = r.imagen && S.cat.imagenes.some(i => i.archivo === r.imagen) ? `/static/imagenes/${r.imagen}` : null;
  const cierra = d.total === d.suma;
  return `<div class="card"><h4>${ic("file")}Lo que leyó el modelo con visión</h4><div class="doc">${img ? `<img src="${img}" alt="">` : "<div></div>"}<div>
      <div style="font-size:13px;margin-bottom:6px"><b>${esc(d.tipo)}</b> · ${esc(d.emisor)}</div>
      ${d.items.map(i => `<div class="chq"><span>${esc(i.descripcion)}</span><span>${plata(i.importe)}</span></div>`).join("")}
      ${d.tipo === "factura" ? `<div class="chq tot"><span>Suma de ítems</span><span>${plata(d.suma)}</span></div>
        <div class="chq tot ${cierra ? "" : "mal"}"><span>Total leído</span><span>${plata(d.total)} ${cierra ? "✓" : "≠"}</span></div>` : ""}
      ${d.instrucciones ? `<div class="inyeccion"><b>Texto con instrucciones en la imagen:</b><br>«${esc(d.instrucciones)}»<br><i>Se registra como dato. Este camino no tiene tools de dinero.</i></div>` : ""}
    </div></div></div>`;
}

function panelRecorrido() {
  const r = S.sel;
  if (!r) return vacio("Mandá un mensaje o corré un escenario: acá se ve el camino que tomó el pedido, cada vuelta del loop y sus frenos.");
  const ruta = RUTA[r.ruta] ?? ["bot", r.ruta];
  let html = `<div class="ruta-t"><span class="chip grad">${ic(ruta[0])}${esc(ruta[1])}</span><span class="chip azul mono">${esc(r.correlation_id)}</span>
    <span class="chip">${r.resumen.llamadas} llamadas · ${mil(r.resumen.tokens)} tokens · ${usd(r.resumen.costo)}</span></div>`;
  if (r.extra.control) html += frenos(r.extra.control);
  html += documento(r);
  if (r.extra.aprobacion) html += `<div class="aviso info">${ic("person")}<span>Pedido de aprobación <b>${esc(r.extra.aprobacion.id)}</b>: ${esc(r.extra.aprobacion.titulo)}. Mirá la pestaña <b>Aprobaciones</b>.</span></div>`;
  html += `<div class="rotulo">Recorrido</div>${recorrido(r.pasos, r.resumen.ms_total)}`;
  return html + `<div class="glos"><p><b>Stopping conditions:</b> iteraciones, costo, tiempo, señal de fin y objetivo verificable. El costo se estima con el precio de un modelo grande en la nube
    (USD ${S.cat.precio.entrada} / ${S.cat.precio.salida} por millón de tokens): Ollama corre local y no cobra.</p>
    <p><b>Loop:</b> repetir lo mismo sin acercarse al objetivo. No reintentar igual: cambiar de estrategia o escalar.</p></div>`;
}

function panelAprobaciones() {
  const lista = [...(S.datos?.aprobaciones || [])].reverse();
  const sistema = S.datos?.sistema;
  let html = lista.length ? lista.map(a => `<div class="aprob ${a.estado}">
      <div class="cab">${ic("person")}${a.tipo === "factura" ? "Revisión pendiente" : "Aprobación pendiente"} · ${esc(a.id)}<span class="chip">${a.estado === "pendiente" ? `vence a las ${esc(a.vence)}` : esc(a.estado)}</span></div>
      <div class="cuerpo"><div>
        <h3>${esc(a.titulo)}</h3><div class="porque">Por qué: ${esc(a.por_que)}</div>
        <ul>${a.datos.map(d => `<li>${esc(d)}</li>`).join("")}</ul>
        <div style="font-size:12px;color:var(--muted)">Si nadie responde: ${esc(a.si_vence)} · <code>${esc(a.correlation_id)}</code></div>
        ${a.estado === "pendiente" ? `<div class="botones"><button class="btn" data-aprobar="${esc(a.id)}" data-si="1">${ic("check")}Aprobar</button>
          <button class="btn sec" data-aprobar="${esc(a.id)}" data-si="0">${ic("x")}Rechazar</button></div>`
          : `<div class="botones"><button class="btn sec mini" data-aprobar="${esc(a.id)}" data-si="1" title="Probá la idempotencia">${ic("refresh")}Aprobar otra vez</button></div>`}
      </div><div>
        <div class="cinco"><div><b>1</b>Una sola decisión, clara</div><div><b>2</b>El motivo</div><div><b>3</b>Los datos para decidir sin buscar</div><div><b>4</b>Vencimiento</div><div><b>5</b>Registro de quién y cuándo</div></div>
        <div class="reg">${a.registro.map(x => `<div>${esc(x.hora)} · <b>${esc(x.quien)}</b> ${esc(x.accion)}</div>`).join("")}</div>
      </div></div></div>`).join("")
    : vacio(`No hay pedidos de aprobación. Probá el escenario D: «Quiero que me devuelvan la seña de $15.000».`);
  if (sistema?.reembolsos?.length || sistema?.facturas?.length) {
    html += `<div class="rotulo">Lo que pasó en el sistema</div><div class="card">`;
    if (sistema.reembolsos.length) html += `<table class="t"><tr><th>Reembolso</th><th>Reserva</th><th>Monto</th><th>Clave de idempotencia</th></tr>
      ${sistema.reembolsos.map(x => `<tr><td><b>${esc(x.id)}</b></td><td>${esc(x.reserva)}</td><td>${plata(x.monto)}</td><td><code>${esc(x.clave)}</code></td></tr>`).join("")}</table>`;
    if (sistema.facturas.length) html += `<table class="t" style="margin-top:8px"><tr><th>Factura</th><th>Proveedor</th><th>Total</th><th>Suma ítems</th><th></th></tr>
      ${sistema.facturas.map(x => `<tr class="${x.total !== x.suma_items ? "dup" : ""}"><td><b>${esc(x.id)}</b></td><td>${esc(x.proveedor)}</td><td>${plata(x.total)}</td><td>${plata(x.suma_items)}</td>
        <td>${x.validada ? `<span class="chip ok">validada</span>` : `<span class="chip mal">sin validar</span>`}</td></tr>`).join("")}</table>`;
    html += `</div>`;
  }
  return html + `<div class="glos"><p><b>Human-in-the-loop:</b> una persona aprueba, corrige o decide en puntos definidos del proceso. La regla vive en el código: el modelo no puede saltearla aunque se lo pidan.</p>
    <p><b>Checkpoint:</b> mientras espera, el reembolso queda guardado en disco; al aprobar se reanuda y se paga una sola vez (idempotente por aprobación).</p></div>`;
}

function panelArquitectura() {
  const r = S.sel, ruta = r?.ruta;
  const camino = (id, tt, sub, nodos) => `<div class="camino ${ruta === id || (id === "agente" && ruta === "confirmacion") || (id === "reembolso" && ruta === "aprobacion") ? "on" : ""}">
    <div class="tt">${tt}<small>${sub}</small></div><div class="nodos">${nodos.map((n, i) => (i ? `<span class="fl">→</span>` : "") + `<span class="nodo ${n[1]}">${esc(n[0])}</span>`).join("")}</div></div>`;
  const nivel = n => S.cat.niveles.find(x => x.id === n)?.nombre ?? n;
  return `<div class="rotulo" style="margin-top:0">Workflow híbrido: control donde importa</div>
    <div class="arq"><div class="entrada-a">${ic("chat")}<b>Mensaje</b>router en código + LLM</div>
      ${camino("documento", "Documentos", "trae imagen", [["visión", "llm"], ["validar total", "cod"], ["persona si no cierra", "hum"], ["cargar_factura", "cod"]])}
      ${camino("reembolso", "Reembolso", "proceso predecible", [["clasificar y extraer", "llm"], ["lectura", "cod"], ["regla de aprobación", "cod"], ["checkpoint", "cod"], ["encargada", "hum"], ["reembolsar", "cod"]])}
      ${camino("agente", "Agente con tools", "el paso abierto", [["decidir", "llm"], ["ejecutar tool", "cod"], ["observar", "cod"], ["frenos y detector", "cod"], ["confirmación", "hum"]])}
    </div>
    <div class="rotulo">Límites de autonomía</div>
    <div class="niveles">${S.cat.niveles.map(n => `<div class="nivel ${n.id}"><b>${esc(n.nombre)}</b><span>${esc(n.detalle)}</span></div>`).join("")}</div>
    <div class="card"><h4>${ic("code")}Tools con contrato <span class="chip" style="margin-left:auto">específicas, con el rol del usuario</span></h4>
      <table class="t"><tr><th>Tool</th><th>Riesgo</th><th>Nivel</th><th>Errores</th></tr>
      ${S.cat.herramientas.map(h => `<tr><td><code>${esc(h.firma)}</code></td><td>${esc(h.riesgo)}</td><td><span class="chip niv ${h.nivel === "solo" ? "ok" : h.nivel === "confirmacion" ? "azul" : "vio"}">${esc(nivel(h.nivel))}</span></td><td style="font-size:12px;color:var(--muted)">${esc(h.errores)}</td></tr>`).join("")}
      <tr><td><code style="text-decoration:line-through">ejecutar_sql(query)</code></td><td>todo</td><td><span class="chip niv mal">nunca</span></td><td style="font-size:12px;color:var(--muted)">una tool genérica le da al agente más poder del que necesita</td></tr>
      </table></div>
    <div class="glos"><p><b>Workflow híbrido:</b> el 80 % del proceso es predecible y lo decide el código; solo el paso abierto usa un agente.</p>
      <p><b>Autonomía:</b> lo que el agente puede hacer sin pedir permiso. El límite se amplía con métricas: probá subir el reembolso sin aprobación a $20.000.</p></div>`;
}

function panelLlamadas() {
  const r = S.sel;
  if (!r) return vacio("Las llamadas al modelo del turno elegido.");
  if (!r.llamadas.length) return vacio("Este turno no llamó al modelo: la aprobación y la reanudación las hace el código.");
  return r.llamadas.map((l, i) => `<div class="llamada ${i === r.llamadas.length - 1 ? "abierta" : ""}"><div>${ic("bot")}<b>${esc(l.titulo)}</b>
      <span class="chip">${mil(l.tokens_prompt)} + ${mil(l.tokens_respuesta)} tok</span><span class="chip">${seg(l.ms)}</span>${l.esquema ? `<span class="chip azul">JSON</span>` : ""}</div>
      <div class="cuerpo">${l.mensajes.map(m => `<div class="rol ${m.role}">${esc(m.role)}${m.imagen ? " · con imagen" : ""}</div><pre class="bloque">${esc(m.content)}</pre>`).join("")}
      <div class="rol assistant">salida</div><pre class="bloque">${esc(l.salida)}</pre></div></div>`).join("");
}

function pintarTodo() { pintarChat(); pintarPanel(); pintarEscenarios(); }

// ---------- inicio ----------
(async () => {
  S.cat = await (await fetch("/api/catalogo")).json();
  S.base = { ...S.cat.config }; S.cfg = { ...S.base };
  $("#cab-quien").textContent = `Escribe ${S.cat.cliente}, clienta · las aprobaciones las resuelve ${S.cat.encargada}`;
  pintarControles();
  await cargarTodo();
  pintarTodo();
  estado();
})();
