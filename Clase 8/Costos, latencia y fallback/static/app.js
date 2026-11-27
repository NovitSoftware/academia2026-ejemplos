// MesaYa · Costos, latencia y eficiencia — interfaz. Los modelos de simulación están en sim.js.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body ?? {}) });
const dormir = ms => new Promise(r => setTimeout(r, ms));
const n1 = (x, d = 1) => Number(x).toLocaleString("es-AR", { minimumFractionDigits: d, maximumFractionDigits: d });
const n0 = x => Math.round(x).toLocaleString("es-AR");
const usd = (x, d = 4) => "USD " + Number(x).toLocaleString("es-AR", { minimumFractionDigits: d, maximumFractionDigits: d });
const seg = x => n1(x) + " s";
const pc = x => Math.round(x * 100) + " %";

const CAT = [["sistema", "Sistema", "var(--s1)"], ["tools", "Definición de tools", "var(--s2)"], ["historial", "Historial", "var(--s3)"], ["resultados", "Resultados de tools", "var(--s4)"], ["consulta", "Consulta y fecha", "var(--s5)"]];
const S = {
  esc: "A", tab: null, perfilLocal: null, perfil: "api", ollama: null, paso: 0, conversaciones: 10000,
  // B
  paralelo: false, streaming: true, corta: false, semilla: 1, corrida: null, corriendo: false,
  // C
  fecha: "arriba", tibia: false, recorte: false, compactar: false, ollamaCache: null, probando: false,
};
const perfil = () => S.perfil === "api" ? PERFIL_API : perfilLocal(S.perfilLocal);
// A usa la trayectoria sin optimizar; B y C, sus propios controles.
function opciones(esc = S.esc) {
  const o = { perfil: perfil(), fecha: "arriba", recorte: false, tibia: false, compactar: false, paralelo: false, corta: false, streaming: false };
  if (esc === "B") Object.assign(o, { paralelo: S.paralelo, corta: S.corta, streaming: S.streaming });
  if (esc === "C") Object.assign(o, { fecha: S.fecha, tibia: S.tibia, recorte: S.recorte, compactar: S.compactar });
  return o;
}

const ESCENARIOS = [
  { id: "A", titulo: "¿Qué se cobra en una trayectoria?", slides: "4.1 · 4.2", icono: "steps",
    sub: "“Mesa para 4 a las 22 en Palermo”: cinco llamadas al modelo, cada una relee todo lo anterior",
    idea: "En un agente manda la entrada: el historial se paga en cada paso y una tool con salida larga se relee hasta el final." },
  { id: "B", titulo: "¿Dónde se va el tiempo?", slides: "4.3", icono: "clock",
    sub: "Time-to-first-token, generación y tools en cada paso; p50 y p95 sobre muchos pedidos",
    idea: "Mejorar lo que domina. El promedio esconde los pedidos lentos: mirá el p95." },
  { id: "C", titulo: "Caché y recorte", slides: "4.4 · 4.5", icono: "layers",
    sub: "El mismo agente con la fecha arriba o abajo, la disponibilidad recortada y el historial compactado",
    idea: "Lo estable arriba, lo variable abajo. Menos tokens: menos costo y menos espera. Compactar rompe la caché." },
];
const TABS = {
  A: [["request", "Request del paso", "code"], ["cuenta", "Cuenta", "table"]],
  B: [["tiempo", "Dónde se va el tiempo", "clock"], ["percentiles", "p50 y p95", "trend"]],
  C: [["prefijo", "Prefijo", "layers"], ["recorte", "Recorte", "funnel"], ["ollama", "Ollama real", "bolt"]],
};

// ---------- tooltip ----------
const tip = $("#tip");
function mostrarTip(html, ev) { tip.innerHTML = html; tip.hidden = false; const w = tip.offsetWidth; tip.style.left = Math.min(ev.clientX + 14, innerWidth - w - 10) + "px"; tip.style.top = (ev.clientY + 14) + "px"; }
const ocultarTip = () => { tip.hidden = true; };
function conTip(sel, html) { $$(sel).forEach(el => { el.onmousemove = ev => mostrarTip(html(el), ev); el.onmouseleave = ocultarTip; }); }

// ---------- estado ----------
async function estado() {
  try {
    S.ollama = await (await fetch("/api/estado")).json();
    const dot = $("#estado .dot"), txt = $("#estado-txt");
    if (S.ollama.ollama) { dot.className = "dot ok"; txt.textContent = `Ollama · ${S.ollama.modelo}${S.ollama.instalado ? "" : " no instalado"}`; }
    else { dot.className = "dot mal"; txt.textContent = "Sin Ollama: solo simulado"; }
  } catch { $("#estado .dot").className = "dot mal"; $("#estado-txt").textContent = "Sin servidor"; }
  if (S.esc === "C" && S.tab === "ollama") pintarPanel();
  setTimeout(estado, 20000);
}

// ---------- escenarios y controles ----------
function pintarEscenarios() {
  $("#escenarios").innerHTML = ESCENARIOS.map(e => `<div class="esc ${S.esc === e.id ? "abierto" : ""}" data-e="${e.id}">
    <button><span class="letra">${e.id}</span><span><div class="nom">${esc(e.titulo)}</div><div class="sl">Slides ${esc(e.slides)}</div></span></button>
    <div class="cuerpo"><p class="idea" style="padding:0 10px 10px 12px">${esc(e.idea)}</p></div></div>`).join("");
  $$(".esc > button").forEach(b => b.onclick = () => elegir(b.parentElement.dataset.e));
}
function elegir(id) {
  S.esc = id; S.tab = TABS[id][0][0];
  const e = ESCENARIOS.find(x => x.id === id);
  $("#cab-ic").innerHTML = ic(e.icono); $("#cab-titulo").textContent = `${e.id} · ${e.titulo}`; $("#cab-sub").textContent = e.sub;
  pintarEscenarios(); pintarTodo();
}
function pintarTabs() {
  $("#tabs").innerHTML = TABS[S.esc].map(([id, n, i]) => `<button data-t="${id}" class="${S.tab === id ? "on" : ""}">${ic(i)}${n}</button>`).join("");
  $$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); pintarPanel(); });
}
function pintarTodo() { pintarControles(); pintarEscena(); pintarTabs(); pintarPanel(); }
const pintarEscena = () => ({ A: escenaA, B: escenaB, C: escenaC })[S.esc]();
const pintarPanel = () => ({ A: panelA, B: panelB, C: panelC })[S.esc]();
const tog = (id, txt, on, title = "") => `<label class="tog" title="${esc(title)}"><span>${txt}</span><input type="checkbox" id="${id}" ${on ? "checked" : ""}><span class="sw"></span></label>`;

function pintarControles() {
  const L = perfilLocal(S.perfilLocal), P = perfil();
  let html = `<div class="rotulo">Perfil</div>
    <div class="seg" id="perfil"><button data-p="api" class="${S.perfil === "api" ? "on" : ""}">API · NVIDIA</button><button data-p="local" class="${S.perfil === "local" ? "on" : ""}">Local</button></div>
    <div class="sub-ctl">${S.perfil === "api"
      ? `${esc(P.modelo)} · USD ${n1(P.entrada, 0)} / ${n1(P.salida, 0)} por millón (entrada / salida) · caché al 10 %`
      : `${esc(L.modelo)} en ${esc(L.equipo ?? "equipo propio")} · lee ${n0(L.prefill)} t/s · genera ${n1(L.gen)} t/s · USD ${n1(L.entrada, 2)} por millón<br><i>Perfil: ${esc(L.fuente)}</i>`}</div>
    <div class="ctl" style="margin-top:10px"><div class="lab">Conversaciones por día</div><input class="inp" type="number" id="conversaciones" value="${S.conversaciones}" step="1000" min="0"></div>`;
  if (S.esc === "A") html += `<div class="rotulo">Trayectoria</div><div class="chips"><span class="chip">fecha arriba</span><span class="chip">sin caché</span><span class="chip">sin recorte</span><span class="chip">tools en secuencia</span></div>
    <div class="sub-ctl">La trayectoria sin optimizar. Las mejoras están en los escenarios B y C.</div>`;
  if (S.esc === "B") html += `<div class="rotulo">Mejoras</div>
    ${tog("paralelo", `${ic("parallel")} Tools en paralelo`, S.paralelo, "consultar_cliente y politica_senas en el mismo paso")}
    ${tog("streaming", `${ic("chat")} Streaming`, S.streaming, "El cliente lee desde el primer token de la respuesta")}
    ${tog("corta", `${ic("sliders")} Respuesta corta`, S.corta, "max_tokens y menos razonamiento")}
    <div style="display:flex;gap:8px;margin-top:12px;flex-wrap:wrap"><button class="btn sec mini" id="otro">${ic("refresh")}Otro pedido</button>
    <button class="btn mini" id="correr" ${S.corriendo ? "disabled" : ""}>${ic("trend")}${S.corriendo ? "Corriendo…" : "Correr 200 pedidos"}</button></div>`;
  if (S.esc === "C") html += `<div class="rotulo">La fecha en el prompt</div>
    <div class="seg" id="fecha"><button data-f="arriba" class="${S.fecha === "arriba" ? "on" : ""}">Arriba</button><button data-f="abajo" class="${S.fecha === "abajo" ? "on" : ""}">Abajo</button></div>
    <div class="sub-ctl">Arriba cambia el prefijo en cada segundo: no hay caché.</div>
    <div class="rotulo">Más ahorro</div>
    ${tog("tibia", `${ic("clock")} Caché tibia`, S.tibia, "Otra conversación usó el mismo prefijo hace menos de 5 minutos")}
    ${tog("recorte", `${ic("funnel")} Recortar la disponibilidad`, S.recorte, "Solo los campos que usa el modelo")}
    ${tog("compactar", `${ic("merge")} Compactar antes del paso 4`, S.compactar, "Resume los resultados viejos")}`;
  $("#controles").innerHTML = html;
  $$("#perfil button").forEach(b => b.onclick = () => { S.perfil = b.dataset.p; S.corrida = null; pintarTodo(); });
  $("#conversaciones").oninput = e => { S.conversaciones = Math.max(0, Number(e.target.value) || 0); pintarEscena(); pintarPanel(); };
  for (const k of ["paralelo", "streaming", "corta", "tibia", "recorte", "compactar"]) { const el = $("#" + k); if (el) el.onchange = e => { S[k] = e.target.checked; S.corrida = null; pintarEscena(); pintarPanel(); }; }
  $$("#fecha button").forEach(b => b.onclick = () => { S.fecha = b.dataset.f; pintarControles(); pintarEscena(); pintarPanel(); });
  if ($("#otro")) $("#otro").onclick = () => { S.semilla++; pintarEscena(); pintarPanel(); };
  if ($("#correr")) $("#correr").onclick = correr200;
}

// =====================================================================
// A · ¿Qué se cobra en una trayectoria?
// =====================================================================
function escenaA() {
  const P = perfil(), tr = trayectoria(opciones("A")), tot = tr.total, p1 = tr.pasos[0];
  const porTool = costoPorTool(tr, P);
  const peor = porTool[0];
  $("#escena").innerHTML = `
    <div class="kpis"><div class="kpi"><div class="n">Por request</div><div class="v chico">${usd(p1.costo)}</div><div class="s">el primer paso: ${n0(p1.entrada)} + ${n0(p1.out)} tokens</div></div>
      <div class="kpi"><div class="n">Por trayectoria</div><div class="v chico">${usd(tot.costo)}</div><div class="s">${tr.pasos.length} llamadas al modelo</div></div>
      <div class="kpi"><div class="n">La entrada pesa</div><div class="v chico">${pc(tot.costoEntrada / tot.costo)}</div><div class="s">${n0(tot.entrada)} tokens de entrada, ${n0(tot.out)} de salida</div></div>
      <div class="kpi"><div class="n">${n0(S.conversaciones)} por día</div><div class="v chico">${usd(tot.costo * S.conversaciones, 0)}</div><div class="s">${usd(tot.costo * S.conversaciones * 30, 0)} por mes</div></div></div>
    ${columnas(tr)}
    <div class="card" style="margin-top:14px"><h4>${ic("funnel")}Costo por tool: su resultado se relee en cada paso que sigue</h4>
      <table class="t"><thead><tr><th>Tool</th><th class="num">Resultado</th><th class="num">Se relee</th><th class="num">Tokens leídos</th><th class="num">Costo</th><th>Parte de la entrada</th></tr></thead><tbody>
      ${porTool.map(f => `<tr><td><code>${f.nombre}</code></td><td class="num">${n0(f.tokens)}</td><td class="num">${f.relecturas} ${f.relecturas === 1 ? "vez" : "veces"}</td><td class="num">${n0(f.tokensLeidos)}</td><td class="num">${usd(f.costo)}</td>
        <td><span class="barrita" style="width:${Math.max(2, f.parte * 160)}px"></span>${pc(f.parte)}</td></tr>`).join("")}</tbody></table>
      <div class="mensaje-clave"><b><code>${peor.nombre}</code> devuelve ${n0(peor.tokens)} tokens y se relee ${peor.relecturas} veces:</b> es el ${pc(peor.parte)} de toda la entrada de la trayectoria. Es la primera candidata para recortar (escenario C).</div></div>
    <div class="aviso info" style="margin-top:4px">${ic("scale")}<div><b>Por resolución</b> es la única unidad que incluye la calidad: (gasto en modelos + casos derivados × costo de derivar) ÷ casos resueltos. La medimos en la Clase 7, con la capability matrix: un modelo barato que resuelve menos puede salir más caro.</div></div>`;
  $$(".paso-col").forEach(g => g.onclick = () => { S.paso = Number(g.dataset.i); S.tab = "request"; escenaA(); pintarTabs(); panelA(); });
  conTip("svg .seg", el => { const s = tr.pasos[el.dataset.i]; const c = CAT.find(x => x[0] === el.dataset.c); return `<b>Paso ${s.n} · ${c[1]}</b><br>${n0(s.porCat[c[0]])} tokens · ${usd(s.porCat[c[0]] * P.entrada / 1e6)}`; });
  conTip("svg .out", el => { const s = tr.pasos[el.dataset.i]; return `<b>Paso ${s.n} · salida</b><br>razonamiento ${s.razona} · tool call ${s.toolCall} · respuesta ${s.respuesta}<br>${n0(s.out)} tokens · ${usd(s.costoSalida)}`; });
}

// Columnas apiladas: la entrada de cada paso por bloque, y la salida debajo a la escala de su costo.
function columnas(tr) {
  const P = perfil();
  const W = 760, H = 330, m = { l: 56, r: 16, t: 26, b: 92 };
  const maxIn = Math.max(...tr.pasos.map(p => p.entrada)) * 1.12;
  const n = tr.pasos.length, ancho = (W - m.l - m.r) / n, bw = Math.min(70, ancho * 0.5);
  const y = v => m.t + (H - m.t - m.b) * (1 - v / maxIn);
  const base = H - m.b;
  const ticks = []; const paso = maxIn > 6000 ? 2000 : 1000; for (let v = 0; v <= maxIn; v += paso) ticks.push(v);
  // La salida se dibuja a la escala de su costo (un token de salida cuesta salida/entrada veces más).
  const factor = P.salida / P.entrada;
  const maxOut = Math.max(...tr.pasos.map(p => p.out * factor));
  const hOut = v => v / Math.max(maxOut, maxIn * 0.25) * 46;
  let cols = "";
  tr.pasos.forEach((s, i) => {
    const cx = m.l + ancho * i + ancho / 2, x0 = cx - bw / 2;
    let acc = 0, segs = "";
    for (const [k, , color] of CAT) {
      const v = s.porCat[k]; if (!v) continue;
      const y1 = y(acc + v), h = y(acc) - y1 - 2;
      segs += `<rect class="seg" data-i="${i}" data-c="${k}" x="${x0}" y="${y1}" width="${bw}" height="${Math.max(1, h)}" fill="${color}" ${acc === 0 ? `rx="2"` : ""}/>`;
      acc += v;
    }
    const ho = hOut(s.out * factor);
    cols += `<g class="paso-col" data-i="${i}" style="cursor:pointer">
      ${S.paso === i ? `<rect class="col-sel" x="${cx - ancho / 2 + 4}" y="${m.t - 20}" width="${ancho - 8}" height="${H - m.t - 4}" rx="2"/>` : ""}
      ${segs}
      <text class="lab" x="${cx}" y="${y(s.entrada) - 8}" text-anchor="middle">${n0(s.entrada)}</text>
      <text class="lab muted" x="${cx}" y="${base + 16}" text-anchor="middle">paso ${s.n}${s.llamadas.length ? "" : " · responde"}</text>
      <text class="lab muted" x="${cx}" y="${base + 30}" text-anchor="middle" style="font-size:10.5px">${s.llamadas.length ? s.llamadas.map(l => l.replace("_", " ")).join(" + ").slice(0, 26) : ""}</text>
      <rect class="out" data-i="${i}" x="${cx - bw / 2}" y="${base + 38}" width="${bw}" height="${Math.max(2, ho)}" fill="var(--salida)" rx="2"/>
      <text class="lab muted" x="${cx + bw / 2 + 4}" y="${base + 50}" style="font-size:10.5px">${usd(s.costo, 4).replace("USD ", "$")}</text></g>`;
  });
  return `<div class="grafico"><div class="ttl">Tokens de entrada por paso</div><div class="st">Cada paso reenvía el sistema, las tools y todo el historial. Abajo, la salida a la escala de su costo (${factor > 1.01 ? `${n0(factor)} veces el token de entrada` : "en local, igual que el token de entrada"}). Clic en un paso para ver su request.</div>
    <svg viewBox="0 0 ${W} ${H}">
      <g class="eje">${ticks.map(v => `<line x1="${m.l}" x2="${W - m.r}" y1="${y(v)}" y2="${y(v)}"/><text x="${m.l - 8}" y="${y(v) + 4}" text-anchor="end">${n0(v)}</text>`).join("")}</g>
      ${cols}
      <text class="lab muted" x="${m.l - 8}" y="${base + 52}" text-anchor="end" style="font-size:10.5px">salida</text>
    </svg>
    <div class="ley">${CAT.map(([, n, c]) => `<span><i style="background:${c}"></i>${n}</span>`).join("")}<span><i style="background:var(--salida)"></i>Salida (a escala de costo)</span></div></div>`;
}

function mensajesDelPaso(s) {
  return s.bloques.map(b => {
    const titulo = { fecha: "system · fecha", sistema: "system · instrucciones", tools: "tools · definiciones", previo: "historial · turnos anteriores", consulta: "user · consulta", llamada: `assistant · tool call ${b.nombre}`, resultado: `tool · resultado de ${b.nombre}`, resumen: "system · resumen (compactado)" }[b.k] ?? b.k;
    const texto = { fecha: FECHA, sistema: SISTEMA, tools: TOOLS_TXT, previo: HISTORIAL_PREVIO.map(m => `${m.role}: ${m.content}`).join("\n"), consulta: CONSULTA, resumen: RESUMEN_COMPACTO,
      resultado: b.nombre ? RESULTADOS[b.nombre](opciones()) : "", llamada: b.nombre ? JSON.stringify({ name: b.nombre, arguments: PASOS.find(p => p.tool === b.nombre)?.args }) : "" }[b.k] ?? "";
    return { titulo, texto, cat: b.cat, tokens: b.tokens };
  });
}

function panelA() {
  if (S.esc !== "A") return;
  const P = perfil(), tr = trayectoria(opciones("A"));
  S.paso = Math.min(S.paso, tr.pasos.length - 1);
  const s = tr.pasos[S.paso];
  if (S.tab === "cuenta") {
    const t = tr.total;
    $("#panel").innerHTML = `
      <div class="card"><h4>${ic("table")}La cuenta de un request · paso ${s.n}</h4>
        <div class="formula">costo = (entrada × precio entrada + salida × precio salida) ÷ 1.000.000<br>
          entrada <b>${n0(s.entrada)}</b> × ${n1(P.entrada, P.entrada < 1 ? 2 : 0)} ÷ 1.000.000 = <b>${usd(s.costoEntrada)}</b><br>
          salida <b>${n0(s.out)}</b> × ${n1(P.salida, P.salida < 1 ? 2 : 0)} ÷ 1.000.000 = <b>${usd(s.costoSalida)}</b><br>
          total = <b>${usd(s.costo)}</b> por request</div></div>
      <div class="card"><h4>${ic("steps")}Por trayectoria</h4>
        <table class="t"><thead><tr><th>Paso</th><th class="num">Entrada</th><th class="num">Salida</th><th class="num">Costo</th></tr></thead><tbody>
        ${tr.pasos.map(p => `<tr class="${p.n === s.n ? "actual" : ""}"><td>${p.n}</td><td class="num">${n0(p.entrada)}</td><td class="num">${n0(p.out)}</td><td class="num">${usd(p.costo)}</td></tr>`).join("")}
        <tr><td><b>Total</b></td><td class="num"><b>${n0(t.entrada)}</b></td><td class="num"><b>${n0(t.out)}</b></td><td class="num"><b>${usd(t.costo)}</b></td></tr></tbody></table>
        <p class="sub-ctl">La entrada pasa de ${n0(tr.pasos[0].entrada)} a ${n0(tr.pasos.at(-1).entrada)} tokens: el costo crece más rápido que la cantidad de pasos.</p></div>
      <div class="card"><h4>${ic("trend")}Proyección</h4>
        <div class="kv"><span>${n0(S.conversaciones)} conversaciones por día</span><b>${usd(t.costo * S.conversaciones, 2)}</b><span>Por mes (30 días)</span><b>${usd(t.costo * S.conversaciones * 30, 0)}</b>
          <span>Si cada conversación tuviera solo el primer request</span><b>${usd(tr.pasos[0].costo * S.conversaciones * 30, 0)}</b></div></div>
      <div class="glos"><p><b>Trayectoria:</b> secuencia completa de pasos de un agente para una tarea.</p><p><b>Precios ilustrativos:</b> ${S.perfil === "api" ? "USD 3 por millón de entrada y USD 15 de salida, como en las slides." : "el costo del equipo propio por millón de tokens, que sale de la demo de Ejecución local."}</p></div>`;
    return;
  }
  const msgs = mensajesDelPaso(s);
  $("#panel").innerHTML = `
    <div class="chips">${tr.pasos.map((p, i) => `<button class="var ${i === S.paso ? "hecho" : ""}" data-i="${i}">Paso ${p.n}</button>`).join("")}</div>
    <div class="kpis tres"><div class="kpi"><div class="n">Entrada</div><div class="v chico">${n0(s.entrada)}</div><div class="s">${usd(s.costoEntrada)}</div></div>
      <div class="kpi"><div class="n">Salida</div><div class="v chico">${n0(s.out)}</div><div class="s">${usd(s.costoSalida)}</div></div>
      <div class="kpi"><div class="n">Llama a</div><div class="v chico" style="font-size:12px;word-break:break-all;line-height:1.3">${s.llamadas.length ? s.llamadas.map(l => `<code>${l}</code>`).join("<br>") : "responde"}</div></div></div>
    <div class="card"><h4>${ic("code")}Lo que viaja en el request</h4>
      <div class="msgs">${msgs.map(m => `<div class="m ${m.cat}"><div class="cab">${esc(m.titulo)}<span class="tk">${n0(m.tokens)} tokens</span></div><pre>${esc(m.texto.slice(0, 600))}</pre></div>`).join("")}
        <div class="m" style="border-left-color:var(--salida)"><div class="cab">salida del modelo<span class="tk">${n0(s.out)} tokens</span></div>
          <div class="sub-ctl">razonamiento ${s.razona} · tool call ${s.toolCall} · respuesta ${s.respuesta}</div></div></div></div>`;
  $$("#panel .chips .var").forEach(b => b.onclick = () => { S.paso = Number(b.dataset.i); escenaA(); panelA(); });
}

// =====================================================================
// B · ¿Dónde se va el tiempo?
// =====================================================================
function escenaB() {
  const o = opciones("B"), tr = trayectoria(o), tm = tiempos(tr, o, `${S.perfil}|${S.semilla}`);
  const max = tm.total * 1.04;
  const pct = v => `${v / max * 100}%`;
  const ticks = []; const pasoEje = max > 60 ? 20 : max > 24 ? 5 : max > 10 ? 2 : 1; for (let v = 0; v <= max; v += pasoEje) ticks.push(v);
  const filas = tm.filas.map(f => `<div class="fila"><div class="nom"><b>Paso ${f.n}</b><small>${f.llamadas.length ? f.llamadas.join(" + ") : "respuesta final"}</small></div>
    <div class="pista"><i class="ttft" data-k="ttft" data-n="${f.n}" style="left:${pct(f.ini)};width:${pct(f.ttft)}"></i><i class="gen" data-k="gen" data-n="${f.n}" style="left:${pct(f.ini + f.ttft)};width:${pct(f.gen)}"></i>
      ${f.tool ? `<i class="tool" data-k="tool" data-n="${f.n}" style="left:${pct(f.ini + f.ttft + f.gen)};width:${pct(f.tool)}"></i>` : ""}
      ${!f.llamadas.length && o.streaming ? `<span class="marca" style="left:${pct(f.ini + f.ttft)}"><span>el cliente empieza a leer</span></span>` : ""}</div>
    <div class="t">${seg(f.ttft + f.gen + f.tool)}</div></div>`).join("");
  const r = S.corrida;
  $("#escena").innerHTML = `
    <div class="kpis"><div class="kpi"><div class="n">Lo que espera el cliente</div><div class="v chico">${seg(tm.percibido)}</div><div class="s">${o.streaming ? `con streaming · total ${seg(tm.total)}` : "sin streaming: espera todo"}</div></div>
      <div class="kpi"><div class="n">Time-to-first-token</div><div class="v chico">${seg(tm.ttft)}</div><div class="s">sumado en ${tm.filas.length} pasos</div></div>
      <div class="kpi"><div class="n">Modelo</div><div class="v chico">${seg(tm.modelo)}</div><div class="s">TTFT + generación</div></div>
      <div class="kpi"><div class="n">Tools</div><div class="v chico">${seg(tm.tools)}</div><div class="s">APIs y base de datos</div></div></div>
    <div class="grafico"><div class="ttl">Un pedido del agente de reservas · ${seg(tm.total)}</div><div class="st">${esc(perfil().nombre)} · ${esc(perfil().modelo)} · pedido #${S.semilla}</div>
      <div class="gantt">${filas}<div class="eje"><span></span><div>${ticks.map(v => `<span style="left:${pct(v)}">${n0(v)} s</span>`).join("")}</div><span></span></div></div>
      <div class="ley"><span><i style="background:var(--s1)"></i>Time-to-first-token</span><span><i style="background:var(--s2)"></i>Generación</span><span><i style="background:var(--s4)"></i>Tools</span></div></div>
    ${r ? histograma(r) : `<div class="aviso info" style="margin-top:14px">${ic("trend")}<div>Un pedido solo no alcanza: <b>Correr 200 pedidos</b> muestra la distribución, el p50 y el p95.</div></div>`}`;
  conTip(".gantt .pista i", el => { const f = tm.filas.find(x => x.n === Number(el.dataset.n)); const k = el.dataset.k;
    return `<b>Paso ${f.n} · ${{ ttft: "time-to-first-token", gen: "generación", tool: f.llamadas.join(" + ") }[k]}</b><br>${seg(f[k])}`; });
}

async function correr200() {
  if (S.corriendo) return;
  S.corriendo = true; pintarControles();
  const o = opciones("B"), tr = trayectoria(o), N = 200, res = [];
  for (let i = 0; i < N; i++) {
    res.push(tiempos(tr, o, `${S.perfil}|lote|${i}`));
    if (i % 25 === 24) { S.corrida = { res: [...res], o }; escenaB(); panelB(); await dormir(60); }
  }
  S.corrida = { res, o }; S.corriendo = false; pintarControles(); escenaB(); panelB();
}

function histograma(r) {
  const xs = r.res.map(t => t.percibido), N = xs.length;
  const p50 = percentil(xs, 0.5), p95 = percentil(xs, 0.95), prom = promedio(xs);
  const maxX = Math.ceil(percentil(xs, 0.995) * 1.1), ancho = maxX > 60 ? 2.5 : maxX > 20 ? 1 : 0.5;
  // El eje arranca un poco antes del pedido más rápido: lo que interesa es la forma de la cola.
  const st = maxX > 60 ? 10 : maxX > 24 ? 5 : 2;
  const minX = Math.max(0, Math.floor(Math.min(...xs) * 0.8 / st) * st);
  const bins = Array.from({ length: Math.ceil((maxX - minX) / ancho) }, () => 0);
  for (const x of xs) bins[Math.min(bins.length - 1, Math.floor((x - minX) / ancho))]++;
  const W = 760, H = 220, m = { l: 44, r: 16, t: 26, b: 30 };
  const maxY = Math.max(...bins) * 1.15;
  const x = v => m.l + (v - minX) / (maxX - minX) * (W - m.l - m.r), y = v => H - m.b - v / maxY * (H - m.t - m.b);
  const bw = (W - m.l - m.r) / bins.length;
  // p50 a la izquierda de su línea; promedio y p95 a la derecha, en alturas distintas para no pisarse.
  const linea = (v, txt, color, dy, izq) => `<line x1="${x(v)}" x2="${x(v)}" y1="${m.t - 6}" y2="${H - m.b}" stroke="${color}" stroke-width="2" ${txt === "promedio" ? `stroke-dasharray="4 3"` : ""}/>
    <text class="lab" x="${x(v) + (izq ? -5 : 5)}" y="${m.t + dy}" text-anchor="${izq ? "end" : "start"}" style="fill:${color}">${txt} ${seg(v)}</text>`;
  const ticks = []; for (let v = minX; v <= maxX; v += st) ticks.push(v);
  return `<div class="grafico"><div class="ttl">${N} pedidos · lo que espera el cliente</div><div class="st">La mayoría tarda parecido; unos pocos, mucho más (la base de reservas lenta, la cola del proveedor).</div>
    <svg viewBox="0 0 ${W} ${H}">
      <g class="eje">${ticks.map(v => `<text x="${x(v)}" y="${H - m.b + 18}" text-anchor="middle">${v} s</text>`).join("")}<line x1="${m.l}" x2="${W - m.r}" y1="${H - m.b}" y2="${H - m.b}"/></g>
      ${bins.map((c, i) => c ? `<rect class="bin" data-i="${i}" x="${m.l + i * bw + 1}" y="${y(c)}" width="${Math.max(1, bw - 2)}" height="${H - m.b - y(c)}" fill="var(--s2)" rx="2"/>` : "").join("")}
      ${linea(p50, "p50", "var(--azul)", 0, true)}${linea(prom, "promedio", "var(--gris-1)", 16)}${linea(p95, "p95", "var(--violeta-3)", 0)}
    </svg></div>`;
}

function panelB() {
  if (S.esc !== "B") return;
  const o = opciones("B"), tr = trayectoria(o), tm = tiempos(tr, o, `${S.perfil}|${S.semilla}`);
  if (S.tab === "percentiles") {
    const r = S.corrida;
    if (!r) { $("#panel").innerHTML = `<div class="panel-vacio">${ic("trend")}<p>Corré 200 pedidos para ver los percentiles.</p></div>`; return; }
    const comp = [["Time-to-first-token", t => t.ttft], ["Modelo", t => t.modelo], ["Tools", t => t.tools], ["Total", t => t.total], ["Lo que espera el cliente", t => t.percibido]];
    $("#panel").innerHTML = `
      <div class="card"><h4>${ic("trend")}${r.res.length} pedidos</h4>
        <table class="t"><thead><tr><th></th><th class="num">p50</th><th class="num">Promedio</th><th class="num">p95</th></tr></thead><tbody>
        ${comp.map(([n, f]) => { const xs = r.res.map(f); return `<tr><td>${n}</td><td class="num">${seg(percentil(xs, 0.5))}</td><td class="num">${seg(promedio(xs))}</td><td class="num"><b>${seg(percentil(xs, 0.95))}</b></td></tr>`; }).join("")}</tbody></table>
        <p class="sub-ctl">El promedio queda entre los dos y no describe a nadie: la experiencia la definen los pedidos lentos.</p></div>
      <div class="glos"><p><b>p50 / p95:</b> la mitad / el 95 % de los pedidos tarda menos que ese valor.</p><p><b>Cola larga:</b> la base de reservas tarda de 2 a 5 s en el 5 % de las búsquedas; el proveedor encola el 4 % de los pedidos.</p></div>`;
    return;
  }
  const P = perfil(), fin = tr.pasos.at(-1), filaFin = tm.filas.at(-1);
  const partes = [["Time-to-first-token", tm.ttft, "var(--s1)"], ["Generación", tm.gen, "var(--s2)"], ["Tools", tm.tools, "var(--s4)"]];
  const domina = partes.reduce((a, b) => b[1] > a[1] ? b : a)[0];
  const recs = [
    ["Time-to-first-token", "Domina la lectura del prompt", "Menos contexto, caché de prefijo (escenario C), un proveedor o modelo con menor TTFT."],
    ["Generación", "Domina el modelo", "Respuestas más cortas, un modelo más rápido, menos pasos (tools en paralelo)."],
    ["Tools", "Dominan las tools", "Llamadas en paralelo, caché de resultados, timeouts."],
  ];
  $("#panel").innerHTML = `
    <div class="card"><h4>${ic("clock")}Este pedido · ${seg(tm.total)}</h4>
      <div class="reparto">${partes.map(([n, v, c]) => `<i style="width:${v / tm.total * 100}%;background:${c}" title="${n}"></i>`).join("")}</div>
      <div class="kv">${partes.map(([n, v]) => `<span>${n}</span><b>${seg(v)} · ${pc(v / tm.total)}</b>`).join("")}</div></div>
    <div class="card"><h4>${ic("bolt")}La respuesta final</h4>
      <div class="formula">tiempo ≈ TTFT + tokens de salida ÷ tokens por segundo<br>
        → <b>${n1(filaFin.ttft, 2)}</b> + ${n0(fin.out)} ÷ ${n0(P.gen)} = <b>${seg(filaFin.ttft + filaFin.gen)}</b></div>
      <p class="sub-ctl">${o.streaming ? "Con streaming el total no baja, pero el cliente empieza a leer en el primer token." : "Sin streaming, el cliente no ve nada hasta el último token."}</p></div>
    <div class="card"><h4>${ic("target")}Mejorar lo que domina</h4>
      ${recs.map(([k, t, d]) => `<div class="recom ${k === domina ? "domina" : ""}"><b>${t}${k === domina ? " ← este pedido" : ""}</b><br>${d}</div>`).join("")}</div>
    <div class="glos"><p><b>TTFT:</b> time-to-first-token, tiempo hasta que aparece el primer token.</p>${S.perfil === "local" ? "<p><b>En local</b> la lectura del prompt domina: el equipo lee cientos de tokens por segundo, no decenas de miles.</p>" : ""}</div>`;
}

// =====================================================================
// C · Caché y recorte
// =====================================================================
const CONFIGS = [
  ["Sin optimizar", { fecha: "arriba" }],
  ["Fecha abajo", { fecha: "abajo" }],
  ["+ caché tibia", { fecha: "abajo", tibia: true }],
  ["+ recorte", { fecha: "abajo", tibia: true, recorte: true }],
  ["+ compactar", { fecha: "abajo", tibia: true, recorte: true, compactar: true }],
];
function escenaC() {
  const o = opciones("C"), P = perfil(), tr = trayectoria(o), base = trayectoria({ ...o, fecha: "arriba", tibia: false, recorte: false, compactar: false });
  const tm = tiempos(tr, o, `${S.perfil}|c`);
  const maxIn = Math.max(...base.pasos.map(p => p.entrada), ...tr.pasos.map(p => p.entrada));
  const capas = tr.pasos.map(s => `<div class="capa"><span><b>Paso ${s.n}</b></span>
    <div class="pista" style="width:${s.entrada / maxIn * 100}%"><i class="cache" style="width:${s.enCache / s.entrada * 100}%"></i><i class="nuevo" style="flex:1"></i></div>
    <span class="v">${s.compactaAqui ? `<span class="rompe">compacta: queda solo el prefijo estable</span><br>` : ""}${n0(s.enCache)} / ${n0(s.entrada)}</span></div>`).join("");
  const filas = CONFIGS.map(([n, c]) => {
    const t = trayectoria({ ...o, tibia: false, recorte: false, compactar: false, ...c }), x = tiempos(t, { ...o, ...c }, `${S.perfil}|c`);
    const actual = c.fecha === o.fecha && !!c.tibia === o.tibia && !!c.recorte === o.recorte && !!c.compactar === o.compactar;
    return `<tr class="${actual ? "actual" : ""}"><td>${n}</td><td class="num">${n0(t.total.entrada)}</td><td class="num">${pc(t.total.enCache / t.total.entrada)}</td><td class="num">${usd(t.total.costo)}</td>
      <td class="num">${usd(t.total.costo * S.conversaciones * 30, 0)}</td><td class="num">${seg(x.ttft)}</td></tr>`;
  }).join("");
  const ahorro = 1 - tr.total.costo / base.total.costo;
  $("#escena").innerHTML = `
    <div class="kpis"><div class="kpi"><div class="n">Por trayectoria</div><div class="v chico">${usd(tr.total.costo)}</div><div class="s">${ahorro > 0.005 ? `${pc(ahorro)} menos que sin optimizar` : "sin optimizar"}</div></div>
      <div class="kpi"><div class="n">Entrada en caché</div><div class="v chico">${pc(tr.total.enCache / tr.total.entrada)}</div><div class="s">${n0(tr.total.enCache)} de ${n0(tr.total.entrada)} tokens</div></div>
      <div class="kpi"><div class="n">Time-to-first-token</div><div class="v chico">${seg(tm.ttft)}</div><div class="s">sumado en los ${tr.pasos.length} pasos</div></div>
      <div class="kpi"><div class="n">Por mes</div><div class="v chico">${usd(tr.total.costo * S.conversaciones * 30, 0)}</div><div class="s">${n0(S.conversaciones)} conversaciones por día</div></div></div>
    <div class="grafico"><div class="ttl">Qué parte de cada request ya estaba en la caché</div><div class="st">${S.perfil === "api" ? "El proveedor cobra el prefijo en caché al 10 % y lo lee mucho más rápido. Cachea en bloques: mínimo 1.024 tokens." : "En local no cambia el costo: cambia cuánto tiene que leer el equipo antes de responder."}</div>
      <div class="capas">${capas}</div>
      <div class="ley"><span><i style="background:var(--s1)"></i>En caché</span><span><i style="background:var(--s3)"></i>Se procesa y se cobra entero</span></div></div>
    <div class="card" style="margin-top:14px"><h4>${ic("table")}Una mejora tras otra</h4>
      <table class="t"><thead><tr><th></th><th class="num">Entrada</th><th class="num">En caché</th><th class="num">Trayectoria</th><th class="num">Por mes</th><th class="num">TTFT</th></tr></thead><tbody>${filas}</tbody></table>
      <p class="sub-ctl">${S.perfil === "api" ? "Sin contar el recargo por escribir en caché que cobran algunos proveedores." : "Con el perfil Local, la columna que importa es el TTFT."}</p></div>
    <div class="mensaje-clave">${o.fecha === "arriba"
      ? `<b>Con la fecha arriba no hay caché:</b> el prefijo cambia cada segundo y el proveedor procesa todo de nuevo en cada paso. Pasala abajo.`
      : o.compactar ? `<b>Compactar cambia el prefijo y rompe la caché</b> en el paso ${tr.pasos.find(p => p.compactaAqui)?.n}: ahorra tokens hacia adelante, pero conviene hacerlo pocas veces, no en cada turno.`
      : `<b>Lo estable arriba, lo variable abajo:</b> desde el paso 2, cada request reutiliza lo que ya procesó el anterior.${o.recorte ? "" : " Probá recortar la disponibilidad."}`}</div>`;
}

function panelC() {
  if (S.esc !== "C") return;
  if (S.tab === "recorte") {
    const completa = disponibilidadCompleta(), recortada = disponibilidadRecortada();
    $("#panel").innerHTML = `
      <div class="kpis tres"><div class="kpi"><div class="n">Completa</div><div class="v chico">${n0(tokens(completa))}</div><div class="s">tokens</div></div>
        <div class="kpi"><div class="n">Recortada</div><div class="v chico">${n0(tokens(recortada))}</div><div class="s">tokens</div></div>
        <div class="kpi"><div class="n">Menos</div><div class="v chico">${pc(1 - tokens(recortada) / tokens(completa))}</div><div class="s">en cada relectura</div></div></div>
      <div class="card"><h4>${ic("funnel")}Solo los campos que usa el modelo</h4>
        <div class="json-comp"><pre class="term" style="white-space:pre-wrap">${esc(completa.slice(0, 1400))}\n…</pre><pre class="term" style="white-space:pre-wrap">${esc(JSON.stringify(JSON.parse(recortada), null, 1))}</pre></div>
        <p class="sub-ctl">Las mesas que no sirven (otra capacidad, otro horario) y los campos que el modelo no usa (fotos, trazas, fechas de limpieza) se filtran en código antes de devolver el resultado. La respuesta es la misma: PAL-TER-04, terraza, 22:00.</p></div>
      <div class="card"><h4>${ic("layers")}Otras formas de reducir el contexto</h4>
        <div class="kv"><span>Recuperar fragmentos, no el documento</span><b>RAG</b><span>Compactar: resumir lo viejo, literal lo reciente</span><b>historial</b>
          <span>Menos tools: cada definición se paga en cada request</span><b>${n0(tokens(TOOLS_TXT))} t</b><span>Subagentes que devuelven la conclusión</span><b>contexto aparte</b><span>Limitar la salida</span><b>max_tokens</b></div></div>`;
    return;
  }
  if (S.tab === "ollama") return panelOllama();
  const o = opciones("C");
  const fechaTxt = `<span class="var">${esc(FECHA)}</span>`;
  const est = t => `<span class="estable">${esc(t)}</span>`;
  const prompt = [
    o.fecha === "arriba" ? fechaTxt : null,
    est("Sistema: Sos el asistente de reservas de MesaYa…") + ` <span class="rot">(${n0(tokens(SISTEMA))} t)</span>`,
    est("Herramientas: buscar_disponibilidad, consultar_cliente, politica_senas, reservar") + ` <span class="rot">(${n0(tokens(TOOLS_TXT))} t)</span>`,
    est("Historial: “Hola! Tienen lugar este sábado?” …") + ` <span class="rot">(${n0(tokens(JSON.stringify(HISTORIAL_PREVIO)))} t)</span>`,
    esc("Consulta: " + CONSULTA),
    `<span class="rot">… tool calls y resultados de los pasos anteriores …</span>`,
    o.fecha === "abajo" ? fechaTxt : null,
  ].filter(Boolean).join("\n");
  $("#panel").innerHTML = `
    <div class="card"><h4>${ic("layers")}El prompt de cada paso · fecha ${o.fecha}</h4><pre class="prompt">${prompt}</pre>
      <p class="sub-ctl">${o.fecha === "arriba" ? "La fecha con segundos está al principio: el prefijo nunca es igual al del request anterior." : "Todo lo que está arriba de la fecha es idéntico entre un paso y el siguiente: el proveedor lo encuentra en caché."}</p></div>
    <div class="card"><h4>${ic("db")}Tres cachés, tres ahorros</h4>
      <div class="kv"><span><b style="color:var(--azul);font-family:Lato">Prompt caching</b>: el prefijo ya procesado</span><b>tokens al 10 %</b>
        <span><b style="color:var(--azul);font-family:Lato">Respuestas</b>: un pedido ya visto</span><b>evita la llamada</b>
        <span><b style="color:var(--azul);font-family:Lato">Tools</b>: el resultado de una herramienta</span><b>evita la ejecución</b></div>
      <p class="sub-ctl">La caché de prefijo vence en minutos: la "caché tibia" supone que otra conversación usó el mismo sistema hace poco.</p></div>
    <div class="glos"><p><b>Prefijo:</b> la parte inicial del prompt, igual entre un request y el siguiente.</p><p><b>Cache hit:</b> el proveedor encontró el prefijo y no lo vuelve a procesar.</p></div>`;
}

function panelOllama() {
  const e = S.ollama, r = S.ollamaCache;
  if (!e?.ollama || !e.instalado) {
    $("#panel").innerHTML = `<div class="aviso">${ic("alert")}<div>${!e?.ollama ? "Ollama no responde en esta máquina." : `Falta <code>${esc(e.modelo)}</code> en Ollama.`} La simulación funciona igual; esta pestaña prueba la caché de prefijo de verdad.</div></div>`;
    return;
  }
  let res = "";
  if (r?.error) res = `<div class="aviso">${ic("alert")}<div>${esc(r.error)}</div></div>`;
  else if (r) {
    const max = Math.max(...r.resultados.map(x => x.lectura_s));
    res = `<div class="card"><h4>${ic("bolt")}${esc(r.modelo)} · lectura del prompt</h4>
      <div class="capas">${r.resultados.map(x => `<div class="capa"><span>${x.orden} ${x.n}</span><div class="pista" style="width:100%"><i style="width:${x.lectura_s / max * 100}%;background:${x.orden === "abajo" && x.n === 2 ? "var(--s1)" : "var(--s3)"}"></i></div><span class="v">${n1(x.lectura_s, 2)} s · ${n0(x.tokens)} t</span></div>`).join("")}</div>
      <p class="sub-ctl">Con la fecha arriba, los dos pedidos leen todo. Con la fecha abajo, el segundo reutiliza el prefijo del primero. <code>prompt_eval_count</code> informa los mismos tokens: el ahorro se ve en el tiempo.</p>
      <p class="sub-ctl">Con un modelo de arquitectura híbrida (qwen3.5, granite4) la reutilización es parcial: llama.cpp guarda puntos de control del estado y no cada token. Se cambia en <code>appsettings.json</code> (<code>Ollama:ModeloCache</code>).</p></div>`;
  }
  $("#panel").innerHTML = `
    <div class="aviso info">${ic("bolt")}<div>Esto es real: dos pedidos con la fecha arriba y dos con la fecha abajo contra <code>${esc(e.modelo)}</code> en esta máquina. llama.cpp guarda el prefijo procesado y lo reutiliza si el siguiente pedido empieza igual.</div></div>
    <button class="btn" id="probar" ${S.probando ? "disabled" : ""}>${ic("bolt")}${S.probando ? "Probando… (unos 30 s)" : "Probar en Ollama"}</button>
    <div style="margin-top:12px">${res}</div>`;
  $("#probar").onclick = async () => {
    S.probando = true; panelOllama();
    try { const x = await post("/api/cache-ollama"); const d = await x.json(); S.ollamaCache = x.ok ? d : { error: d.detail }; }
    catch (err) { S.ollamaCache = { error: String(err) }; }
    S.probando = false; if (S.esc === "C" && S.tab === "ollama") panelOllama();
  };
}

// ---------- inicio ----------
(async () => {
  try { S.perfilLocal = await (await fetch("/api/perfil-local")).json(); }
  catch { S.perfilLocal = { modelo: "qwen3:8b-q4_K_M", tokens_s_lectura: 257, tokens_s_generacion: 34.3, usd_por_millon: 1.5, fuente: "valores de respaldo" }; }
  elegir("A");
  estado();
})();
