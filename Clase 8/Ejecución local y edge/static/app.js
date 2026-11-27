// MesaYa · Ejecución local y edge — interfaz. Los modelos de simulación están en sim.js.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const ic = (n, cls = "ic") => `<svg class="${cls}"><use href="/static/iconos.svg#i-${n}"/></svg>`;
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
const dormir = ms => new Promise(r => setTimeout(r, ms));
const n1 = (x, d = 1) => Number(x).toLocaleString("es-AR", { minimumFractionDigits: d, maximumFractionDigits: d });
const n0 = x => Math.round(x).toLocaleString("es-AR");
const usd = (x, d = 0) => "USD " + Number(x).toLocaleString("es-AR", { minimumFractionDigits: d, maximumFractionDigits: d });
const ahora = () => new Date().toLocaleTimeString("es-AR", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const SERIE = { 1.7: "var(--s1)", 4: "var(--s2)", 8: "var(--s3)", 14: "var(--s4)", 32: "var(--s5)" };
const corto = m => `${m.tam}B ${m.q.replace("_K_M", "").replace("_K_S", "").replace("_0", "")}`;

const S = {
  esc: "A", tab: null, ollama: null,
  // A
  equipo: "este", ctx: 8192, corrida: 0, sel: "qwen3:8b-q4_K_M", revelado: MODELOS.length, midiendo: -1, corriendo: false,
  medirModelo: null, medicion: null, midiendoReal: false,
  // B
  internet: true, real: false, chat: [], red: [], cola: [], pedidos: [], bateria: 86, temp: 38, ocupadoB: false,
  // C
  costoEquipo: 2400, meses: 24, energia: 20, mant: 30, precioApi: 2, volumen: 100, paralelo: 4, horas: 8, perfil: null,
};
const equipo = () => EQUIPOS.find(e => e.id === S.equipo);
const banco = () => MODELOS.map(m => simular(m, equipo(), S.ctx, S.corrida));
const elegido = () => simular(MODELOS.find(m => m.id === S.sel), equipo(), S.ctx, S.corrida);

const ESCENARIOS = [
  { id: "A", titulo: "Banco de cuantización", slides: "3.2 · 3.3 · 3.4", icono: "layers",
    sub: "El mismo pedido en cada modelo: memoria, si entra en la VRAM, tokens por segundo y aciertos",
    idea: "Más grande en Q4 antes que más chico en Q8. Si no entra en la VRAM, los tokens por segundo se caen." },
  { id: "B", titulo: "La tablet sin internet", slides: "3.5 · 3.6 · Aplicación práctica", icono: "monitor",
    sub: "Un modelo chico en la tablet del mozo; lo grande va a la nube cuando hay conexión",
    idea: "Lo simple y lo sensible en el dispositivo, lo grande en la nube. Cortá internet y mirá qué sigue funcionando." },
  { id: "C", titulo: "¿Cuándo se paga el equipo?", slides: "3.7", icono: "scale",
    sub: "Costo mensual del equipo propio contra la API, con la capacidad medida en el banco",
    idea: "Con poco volumen gana la API. Lo local se justifica por volumen alto, privacidad u offline." },
];
const TABS = {
  A: [["modelo", "Modelo", "eye"], ["medir", "Medir en este equipo", "bolt"]],
  B: [["red", "Red", "globe"], ["disp", "Dispositivo", "monitor"]],
  C: [["cuenta", "Cuenta", "table"], ["perfil", "Perfil para Costos", "link"]],
};

// ---------- tooltip ----------
const tip = $("#tip");
function mostrarTip(html, ev) { tip.innerHTML = html; tip.hidden = false; moverTip(ev); }
function moverTip(ev) { const w = tip.offsetWidth; tip.style.left = Math.min(ev.clientX + 14, innerWidth - w - 10) + "px"; tip.style.top = (ev.clientY + 14) + "px"; }
const ocultarTip = () => { tip.hidden = true; };

// ---------- estado de Ollama ----------
async function estado() {
  try {
    S.ollama = await (await fetch("/api/estado")).json();
    const o = S.ollama.ollama, dot = $("#estado .dot"), txt = $("#estado-txt");
    if (o.conectado) { dot.className = "dot ok"; txt.textContent = `Ollama · ${o.instalados.length} modelos instalados`; }
    else { dot.className = "dot mal"; txt.textContent = "Sin Ollama: solo simulado"; }
    if (o.conectado && !S.medirModelo) S.medirModelo = (o.instalados.find(m => m.nombre.startsWith("qwen3.5:4b")) ?? o.instalados[0])?.nombre ?? null;
    if (S.esc !== "C") pintarPanel();
    if (S.esc === "B") pintarControles();
  } catch { $("#estado .dot").className = "dot mal"; $("#estado-txt").textContent = "Sin servidor"; }
  setTimeout(estado, 15000);
}

// ---------- escenarios ----------
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
  pintarEscenarios(); pintarControles(); pintarEscena(); pintarTabs(); pintarPanel();
}
function pintarTabs() {
  $("#tabs").innerHTML = TABS[S.esc].map(([id, n, i]) => `<button data-t="${id}" class="${S.tab === id ? "on" : ""}">${ic(i)}${n}</button>`).join("");
  $$("#tabs button").forEach(b => b.onclick = () => { S.tab = b.dataset.t; pintarTabs(); pintarPanel(); });
}
const pintarEscena = () => ({ A: escenaA, B: escenaB, C: escenaC })[S.esc]();
const pintarPanel = () => ({ A: panelA, B: panelB, C: panelC })[S.esc]();

function pintarControles() {
  const c = $("#controles");
  if (S.esc === "A" || S.esc === "C") {
    const eq = equipo();
    let html = `<div class="rotulo">Equipo</div>
      <div class="ctl"><select class="sel" id="equipo">${EQUIPOS.map(e => `<option value="${e.id}" ${e.id === S.equipo ? "selected" : ""}>${esc(e.nombre)}</option>`).join("")}</select>
      <div class="sub-ctl">${esc(eq.sub)} · ${eq.vram ? `${eq.vram} GB para el modelo` : "sin GPU"} · ${eq.bw} GB/s</div></div>`;
    if (S.esc === "A") html += `
      <div class="ctl"><div class="lab">Contexto (num_ctx)</div><div class="seg" id="ctx">${[4096, 8192, 32768].map(v => `<button data-v="${v}" class="${S.ctx === v ? "on" : ""}">${v / 1024}k</button>`).join("")}</div>
      <div class="sub-ctl">La ventana también ocupa memoria: más contexto, menos lugar para las capas.</div></div>
      <button class="btn" id="correr" ${S.corriendo ? "disabled" : ""}>${ic("refresh")}Correr el banco de nuevo</button>`;
    else html += `
      <div class="ctl"><div class="lab">Modelo (del banco)</div><select class="sel" id="modeloC">${MODELOS.map(m => `<option value="${m.id}" ${m.id === S.sel ? "selected" : ""}>${m.id}</option>`).join("")}</select></div>
      <div class="rotulo">Costo del equipo</div>
      <div class="fila2"><div class="ctl"><div class="lab">Equipo USD</div><input class="inp" type="number" id="costoEquipo" value="${S.costoEquipo}" step="100"></div>
      <div class="ctl"><div class="lab">Meses</div><input class="inp" type="number" id="meses" value="${S.meses}" min="6" step="6"></div></div>
      <div class="fila2"><div class="ctl"><div class="lab">Energía / mes</div><input class="inp" type="number" id="energia" value="${S.energia}"></div>
      <div class="ctl"><div class="lab">Mantenimiento</div><input class="inp" type="number" id="mant" value="${S.mant}"></div></div>
      <div class="ctl"><div class="lab">Uso <b>${S.horas} h por día</b></div><div class="seg" id="horas">${[8, 16, 24].map(v => `<button data-v="${v}" class="${S.horas === v ? "on" : ""}">${v} h</button>`).join("")}</div></div>
      <div class="ctl"><div class="lab">Pedidos en paralelo</div><div class="seg" id="paralelo">${[1, 4, 8].map(v => `<button data-v="${v}" class="${S.paralelo === v ? "on" : ""}">${v}</button>`).join("")}</div>
      <div class="sub-ctl">Con varios pedidos a la vez el servidor agrupa la generación y rinde más por equipo.</div></div>
      <div class="rotulo">API</div>
      <div class="ctl"><div class="lab">USD por millón de tokens</div><input class="inp" type="number" id="precioApi" value="${S.precioApi}" step="0.1" min="0.1"></div>
      <div class="ctl"><div class="lab">Volumen <b>${n0(S.volumen)} M tokens / mes</b></div><input type="range" id="volumen" min="0" max="1000" value="${Math.round(Math.log10(S.volumen) / 3 * 1000)}"></div>`;
    c.innerHTML = html;
    $("#equipo").onchange = e => { S.equipo = e.target.value; S.revelado = MODELOS.length; pintarControles(); pintarEscena(); pintarPanel(); };
    if (S.esc === "A") {
      $$("#ctx button").forEach(b => b.onclick = () => { S.ctx = Number(b.dataset.v); pintarControles(); pintarEscena(); pintarPanel(); });
      $("#correr").onclick = correrBanco;
    } else {
      $("#modeloC").onchange = e => { S.sel = e.target.value; pintarEscena(); pintarPanel(); };
      for (const k of ["costoEquipo", "meses", "energia", "mant", "precioApi"]) $("#" + k).oninput = e => { S[k] = Math.max(0, Number(e.target.value) || 0); pintarEscena(); pintarPanel(); };
      for (const k of ["horas", "paralelo"]) $$(`#${k} button`).forEach(b => b.onclick = () => { S[k] = Number(b.dataset.v); pintarControles(); pintarEscena(); pintarPanel(); });
      $("#volumen").oninput = e => { S.volumen = Math.max(1, Math.round(Math.pow(10, Number(e.target.value) / 1000 * 3))); $("#volumen").previousElementSibling.querySelector("b").textContent = `${n0(S.volumen)} M tokens / mes`; pintarEscena(); pintarPanel(); };
    }
  } else {
    const realOk = S.ollama?.ollama.conectado && S.ollama.ollama.instalados.some(m => m.nombre === S.ollama.modelo_mozo);
    c.innerHTML = `<div class="rotulo">Sucursal Palermo</div>
      <label class="tog"><span>${ic(S.internet ? "globe" : "ban")} Internet</span><input type="checkbox" id="internet" ${S.internet ? "checked" : ""}><span class="sw"></span></label>
      <label class="tog" title="${realOk ? "La tablet usa " + esc(S.ollama.modelo_mozo) + " en este equipo" : "Necesita Ollama con " + esc(S.ollama?.modelo_mozo ?? "qwen3.5:2b")}"><span>Modelo real (${esc(S.ollama?.modelo_mozo ?? "Ollama")})</span><input type="checkbox" id="real" ${S.real && realOk ? "checked" : ""} ${realOk ? "" : "disabled"}><span class="sw"></span></label>
      <div class="sub-ctl">${realOk ? "Apagado: modelo de 3B simulado a la velocidad de una tablet." : "Sin Ollama, la tablet usa el modelo simulado."}</div>
      <div class="rotulo">El mozo pregunta</div>
      <div class="sugs">${SUGERENCIAS.map((t, i) => `<button class="sug" data-i="${i}">${esc(t)}<span class="chip ${rutaMozo(t) === "nube" ? "vio" : "azul"}">${rutaMozo(t) === "nube" ? "nube" : "tablet"}</span></button>`).join("")}</div>
      <button class="btn sec mini" id="reiniciar" style="margin-top:12px">${ic("trash")}Empezar el turno de nuevo</button>`;
    $("#internet").onchange = e => cambiarInternet(e.target.checked);
    $("#real").onchange = e => { S.real = e.target.checked; };
    $$(".sug").forEach(b => b.onclick = () => enviarMozo(SUGERENCIAS[Number(b.dataset.i)]));
    $("#reiniciar").onclick = () => { Object.assign(S, { chat: [], red: [], cola: [], pedidos: [], bateria: 86, temp: 38, internet: true }); pintarControles(); escenaB(); panelB(); };
  }
}

// =====================================================================
// A · Banco de cuantización
// =====================================================================
async function correrBanco() {
  if (S.corriendo) return;
  S.corriendo = true; S.corrida++; S.revelado = 0; pintarControles();
  for (let i = 0; i < MODELOS.length; i++) {
    S.midiendo = i; escenaA();
    const s = simular(MODELOS[i], equipo(), S.ctx, S.corrida);
    await dormir(Math.min(1400, 250 + 1100 / Math.max(s.gen, 1) * 8));   // los lentos tardan más, como en la realidad
    S.revelado = i + 1;
  }
  S.midiendo = -1; S.corriendo = false; pintarControles(); escenaA(); panelA();
}

function escenaA() {
  const filas = banco(), eq = equipo();
  const max = Math.max(eq.vram, ...filas.map(f => f.total)) * 1.04;
  const maxGen = Math.max(...filas.map(f => f.gen));
  const pct = x => `${(x / max) * 100}%`;
  const tabla = filas.map((f, i) => {
    const pend = i >= S.revelado && i !== S.midiendo, mid = i === S.midiendo;
    const fuera = Math.max(0, f.total - f.util);
    const lenta = f.gen < 10;
    return `<tr data-id="${f.id}" class="${f.id === S.sel ? "sel" : ""} ${pend ? "pend" : ""} ${mid ? "midiendo" : ""}">
      <td><b>${f.tam}B</b> <span class="q">${f.q}</span></td>
      <td class="num">${n1(f.bits)}</td>
      <td><div class="mem" data-tip="mem">
        <span class="p" style="left:0;width:${pct(f.pesos)}"></span>
        <span class="k" style="left:${pct(f.pesos)};width:${pct(f.kv)}"></span>
        <span class="o" style="left:${pct(f.pesos + f.kv)};width:${pct(OVERHEAD)}"></span>
        ${fuera > 0 && eq.vram ? `<span class="fuera" style="left:${pct(f.util)};width:${pct(fuera)}"></span>` : ""}
        ${eq.vram ? `<span class="vram" style="left:${pct(f.util)}"></span>` : ""}
      </div></td>
      <td class="num">${n1(f.total)} GB</td>
      <td class="num"><span class="gpu ${f.gpu < 1 ? "mal" : ""}">${pend ? "—" : f.gpu >= 0.999 ? "100 %" : `${Math.round(f.gpu * 100)} %`}</span></td>
      <td class="num">${pend || mid ? (mid ? "midiendo…" : "—") : `<div class="velo"><span>${n1(f.gen)}</span><span class="barra ${lenta ? "lenta" : ""}"><i style="width:${f.gen / maxGen * 100}%"></i></span></div>`}</td>
      <td class="num">${pend || mid ? "—" : n0(f.lectura)}</td>
      <td class="num ok-n">${pend || mid ? "—" : `${f.aciertos}/${TAREAS}`}</td></tr>`;
  }).join("");

  const s8q8 = filas.find(f => f.id === "qwen3:8b-q8_0"), s14 = filas.find(f => f.id === "qwen3:14b-q4_K_M");
  const clave = S.revelado < MODELOS.length ? "" : `<div class="mensaje-clave">${s14.gpu >= 0.999
    ? `<b>Más grande en Q4 antes que más chico en Q8.</b> El 14B en Q4 ocupa ${n1(s14.total)} GB contra ${n1(s8q8.total)} GB del 8B en Q8 y acierta ${s14.aciertos} contra ${s8q8.aciertos}.`
    : `<b>El 14B en Q4 no entra en la VRAM</b> de este equipo con ${S.ctx / 1024}k de contexto: ${Math.round((1 - s14.gpu) * 100)} % de las capas va a la RAM y genera ${n1(s14.gen)} tokens/s. Probá con menos contexto u otro equipo.`}</div>`;

  $("#escena").innerHTML = `
    <div class="ley"><span><i style="background:var(--s1)"></i>Pesos</span><span><i style="background:var(--s2)"></i>Contexto (KV cache)</span><span><i style="background:var(--gris-4)"></i>Buffers</span>
      ${eq.vram ? `<span><i class="linea"></i>VRAM útil: ${n1(filas[0].util)} GB</span><span><i class="rayado"></i>No entra: va a la RAM</span>` : `<span>Sin GPU: todo corre en la CPU</span>`}</div>
    <table class="banco"><thead><tr><th>Modelo</th><th class="num">Bits</th><th>Memoria</th><th class="num">Total</th><th class="num">GPU</th>
      <th class="num">Genera t/s</th><th class="num">Lee t/s</th><th class="num">Aciertos</th></tr></thead><tbody>${tabla}</tbody></table>
    ${clave}
    ${dispersion(filas)}`;
  $$("table.banco tbody tr").forEach(tr => tr.onclick = () => { S.sel = tr.dataset.id; S.tab = "modelo"; escenaA(); pintarTabs(); panelA(); });
  $$(".mem").forEach(m => {
    const f = filas.find(x => x.id === m.closest("tr").dataset.id);
    m.onmousemove = ev => mostrarTip(`<b>${f.id}</b><br>Pesos ${n1(f.pesos)} GB · contexto ${n1(f.kv)} GB · buffers ${n1(OVERHEAD, 2)} GB<br>Total ${n1(f.total)} GB${eq.vram ? ` de ${n1(f.util)} GB útiles` : ""}${f.gpu < 1 && eq.vram ? `<br>${Math.round((1 - f.gpu) * 100)} % de las capas en la RAM` : ""}`, ev);
    m.onmouseleave = ocultarTip;
  });
  conectarDispersion(filas);
}

// Calidad contra memoria: un punto por modelo, color por tamaño, hueco si no entra en la VRAM.
function dispersion(filas) {
  const vis = filas.slice(0, S.revelado);
  const W = 720, H = 290, m = { l: 44, r: 16, t: 14, b: 36 };
  const xMax = Math.ceil(Math.max(...filas.map(f => f.total), equipo().vram) / 4) * 4 + 1;
  const x = v => m.l + v / xMax * (W - m.l - m.r), y = v => H - m.b - (v - 30) / 60 * (H - m.t - m.b);
  const ticksX = []; for (let v = 0; v <= xMax; v += xMax > 24 ? 8 : 4) ticksX.push(v);
  const util = filas[0].util;
  // Los 8B quedan a la misma altura: las etiquetas alternan arriba y abajo para no pisarse.
  const abajo = f => ["Q8_0", "Q4_K_M"].includes(f.q) && f.tam === 8;
  const etiqueta = f => f.tam === 8 ? f.q.replace("_K_M", "").replace("_K_S", "").replace("_0", "") : `${f.tam}B`;
  return `<div class="grafico"><div class="ttl">Calidad contra memoria</div><div class="st">Aciertos sobre ${TAREAS} tareas de MesaYa · memoria total con ${S.ctx / 1024}k de contexto</div>
    <svg viewBox="0 0 ${W} ${H}" id="disp">
      <g class="eje">${[30, 45, 60, 75, 90].map(v => `<line x1="${m.l}" x2="${W - m.r}" y1="${y(v)}" y2="${y(v)}"/><text x="${m.l - 8}" y="${y(v) + 4}" text-anchor="end">${v} %</text>`).join("")}
        ${ticksX.map(v => `<text x="${x(v)}" y="${H - m.b + 18}" text-anchor="middle">${v} GB</text>`).join("")}</g>
      ${equipo().vram ? `<line x1="${x(util)}" x2="${x(util)}" y1="${m.t}" y2="${H - m.b}" stroke="var(--azul)" stroke-width="2" stroke-dasharray="5 4"/>
        <text class="lab" x="${x(util) + 6}" y="${m.t + 12}">VRAM útil ${n1(util)} GB</text>` : ""}
      ${vis.map(f => { const fuera = f.gpu < 1; const yy = y(f.aciertos / TAREAS * 100);
        return `<g class="pt" data-id="${f.id}" style="cursor:pointer">
          <circle cx="${x(f.total)}" cy="${yy}" r="14" fill="transparent"/>
          <circle cx="${x(f.total)}" cy="${yy}" r="${f.id === S.sel ? 8 : 6}" fill="${fuera ? "#fff" : SERIE[f.tam]}" stroke="${fuera ? SERIE[f.tam] : "#fff"}" stroke-width="2"/>
          <text class="lab ${fuera ? "muted" : ""}" x="${x(f.total)}" y="${yy + (abajo(f) ? 22 : -12)}" text-anchor="middle">${etiqueta(f)}</text></g>`; }).join("")}
    </svg>
    <div class="ley">${[1.7, 4, 8, 14, 32].map(t => `<span><i style="background:${SERIE[t]}"></i>${String(t).replace(".", ",")}B</span>`).join("")}<span><i style="background:#fff;border:2px solid var(--gris-2)"></i>No entra entero en la VRAM</span></div></div>`;
}
function conectarDispersion(filas) {
  $$("#disp .pt").forEach(g => {
    const f = filas.find(x => x.id === g.dataset.id);
    g.onmousemove = ev => mostrarTip(`<b>${f.id}</b><br>${f.aciertos}/${TAREAS} aciertos · ${n1(f.total)} GB<br>${n1(f.gen)} tokens/s · ${f.gpu >= 0.999 ? "100 % GPU" : Math.round(f.gpu * 100) + " % en GPU"}`, ev);
    g.onmouseleave = ocultarTip;
    g.onclick = () => { S.sel = f.id; S.tab = "modelo"; escenaA(); pintarTabs(); panelA(); };
  });
}

function panelA() {
  if (S.esc !== "A") return;
  if (S.tab === "medir") return panelMedir();
  const s = elegido(), eq = equipo();
  const fuera = Math.max(0, s.total - s.util);
  $("#panel").innerHTML = `
    <div class="kpis"><div class="kpi"><div class="n">Memoria</div><div class="v chico">${n1(s.total)} GB</div><div class="s">${eq.vram ? `de ${n1(s.util)} útiles` : "en la RAM"}</div></div>
      <div class="kpi ${s.gpu < 1 ? "mal" : ""}"><div class="n">En GPU</div><div class="v chico">${Math.round(s.gpu * 100)} %</div><div class="s">${s.gpu < 1 ? "offloading" : "todo en VRAM"}</div></div>
      <div class="kpi ${s.gen < 10 ? "mal" : ""}"><div class="n">Generación</div><div class="v chico">${n1(s.gen)}</div><div class="s">tokens/s</div></div>
      <div class="kpi"><div class="n">Aciertos</div><div class="v chico">${s.aciertos}/${TAREAS}</div><div class="s">${esc(s.nota)}</div></div></div>
    <div class="card"><h4>${ic("layers")}¿Cuánta memoria necesita ${esc(s.id)}?</h4>
      <div class="formula">memoria ≈ parámetros × bits ÷ 8 + contexto<br>
        <b>${s.tam}</b> × <b>${n1(s.bits, 2)}</b> ÷ 8 = <b>${n1(s.pesos)} GB</b> de pesos<br>
        + contexto de ${S.ctx / 1024}k ≈ <b>${n1(s.kv)} GB</b> · buffers ≈ <b>${n1(OVERHEAD, 2)} GB</b><br>
        = <b>${n1(s.total)} GB</b> ${eq.vram ? (fuera > 0 ? `→ <b style="color:var(--bad)">${n1(fuera)} GB van a la RAM</b>` : "→ entra en la VRAM") : "→ todo en la RAM"}</div></div>
    <div class="card"><h4>${ic("code")}En la terminal</h4>
      <pre class="term"><span class="p">$</span> ollama run ${esc(s.id)} --verbose\n${verbose(s).replace(/(prompt eval rate|eval rate):(\s+)([\d.]+)/g, (_, a, b, c) => `${a}:${b}<span class="hl">${c}</span>`)}\n\n<span class="p">$</span> ollama ps\n${esc(ps(s, S.ctx))}</pre>
      <p class="sub-ctl">Simulado para 812 tokens de entrada y 236 de salida, como en la slide 3.4. La lectura y la generación son velocidades distintas.</p></div>
    <div class="card"><h4>${ic("clock")}¿Cómo se siente?</h4>
      <div class="kv"><span>Una respuesta de 250 tokens</span><b>${n1(250 / s.gen)} s</b>
        <span>Leer un contexto de 8.000 tokens</span><b>${n1(8000 / s.lectura)} s</b>
        <span>Cargar el modelo desde el disco</span><b>${n1(s.carga)} s</b></div>
      <p class="sub-ctl">${s.gen < 5 ? "Menos de 5 tokens/s: incómodo para chat." : s.gen < 10 ? "Entre 5 y 10: aceptable." : s.gen < 20 ? "Entre 10 y 20: fluido para leer." : "Más de 20: lo que pide un agente."}</p></div>
    <div class="glos"><p><b>VRAM:</b> memoria de la GPU. Define qué modelo entra.</p><p><b>Offloading:</b> mover capas a la CPU cuando no entran; la generación cae porque la RAM es mucho más lenta.</p><p><b>Valores simulados</b> con las cuentas de las slides y ruido de medición.</p></div>`;
}

function panelMedir() {
  const o = S.ollama?.ollama;
  if (!o?.conectado) { $("#panel").innerHTML = `<div class="aviso">${ic("alert")}<div>Ollama no responde en esta máquina. El banco simulado funciona igual; para comparar con una medición real, iniciá <code>ollama serve</code>.</div></div>`; return; }
  const inst = o.instalados;
  const elegidoReal = inst.find(m => m.nombre === S.medirModelo);
  const r = S.medicion;
  let comp = "";
  if (r && elegidoReal) {
    const tam = parseFloat(String(elegidoReal.parametros).replace(",", ".")) || 4;
    const est = simular({ id: elegidoReal.nombre, tam, q: elegidoReal.cuantizacion?.toUpperCase() }, EQUIPOS.find(e => e.id === "este"), r.contexto || 4096, 0);
    comp = `<div class="card"><h4>${ic("scale")}Estimado contra medido</h4>
      <div class="comp"><span class="h"></span><span class="h" style="text-align:right">Estimado</span><span class="h" style="text-align:right">Medido</span>
        <span>Memoria cargada</span><span class="v">${n1(est.total)} GB</span><span class="v">${n1(r.bytes / 1e9)} GB</span>
        <span>En GPU</span><span class="v">${Math.round(est.gpu * 100)} %</span><span class="v">${r.bytes ? Math.round(r.vram / r.bytes * 100) : 0} %</span>
        <span>Generación</span><span class="v">${n1(est.gen)} t/s</span><span class="v">${n1(r.eval_rate)} t/s</span>
        <span>Lectura</span><span class="v">${n0(est.lectura)} t/s</span><span class="v">${n0(r.prompt_eval_rate)} t/s</span></div>
      <p class="sub-ctl">La cuenta da el orden de magnitud. La arquitectura del modelo, el driver y el backend (CUDA, ROCm, Metal, Vulkan) mueven los números: por eso se mide.</p></div>`;
  }
  $("#panel").innerHTML = `
    <div class="aviso info">${ic("bolt")}<div>Esto sí es real: corre un pedido en un modelo ya instalado en esta máquina y muestra lo mismo que <code>ollama run --verbose</code> y <code>ollama ps</code>. No descarga nada.</div></div>
    <div class="ctl"><div class="lab">Modelo instalado</div><select class="sel" id="medirModelo">${inst.map(m => `<option value="${esc(m.nombre)}" ${m.nombre === S.medirModelo ? "selected" : ""}>${esc(m.nombre)} · ${esc(m.parametros)} · ${esc(m.cuantizacion)} · ${n1(m.bytes / 1e9)} GB</option>`).join("")}</select></div>
    <button class="btn" id="medir" ${S.midiendoReal ? "disabled" : ""}>${ic("bolt")}${S.midiendoReal ? "Midiendo… (la primera vez carga el modelo)" : "Medir en este equipo"}</button>
    ${r?.error ? `<div class="aviso" style="margin-top:12px">${ic("alert")}<div>${esc(r.error)}</div></div>` : ""}
    ${r && !r.error ? `<div class="card" style="margin-top:12px"><h4>${ic("code")}${esc(r.modelo)}</h4>
      <pre class="term"><span class="p">$</span> ollama run ${esc(r.modelo)} --verbose
total duration:       ${r.total_s.toFixed(2)}s
load duration:        ${Math.round(r.carga_s * 1000)}ms
prompt eval count:    ${r.prompt_eval_count} token(s)
prompt eval duration: ${r.prompt_eval_s.toFixed(2)}s
prompt eval rate:     <span class="hl">${r.prompt_eval_rate.toFixed(2)}</span> tokens/s
eval count:           ${r.eval_count} token(s)
eval duration:        ${r.eval_s.toFixed(2)}s
eval rate:            <span class="hl">${r.eval_rate.toFixed(2)}</span> tokens/s

<span class="p">$</span> ollama ps
${esc(r.modelo.padEnd(22))}${(n1(r.bytes / 1e9) + " GB").padEnd(10)}${(r.bytes && r.vram >= r.bytes ? "100% GPU" : `${Math.round((1 - r.vram / Math.max(r.bytes, 1)) * 100)}%/${Math.round(r.vram / Math.max(r.bytes, 1) * 100)}% CPU/GPU`).padEnd(18)}${r.contexto}</pre></div>${comp}` : ""}`;
  $("#medirModelo").onchange = e => { S.medirModelo = e.target.value; S.medicion = null; panelMedir(); };
  $("#medir").onclick = async () => {
    S.midiendoReal = true; panelMedir();
    try { const res = await post("/api/medir", { modelo: S.medirModelo }); const d = await res.json(); S.medicion = res.ok ? d : { error: d.detail }; }
    catch (e) { S.medicion = { error: String(e) }; }
    S.midiendoReal = false; if (S.esc === "A" && S.tab === "medir") panelMedir();
  };
}

// =====================================================================
// B · La tablet sin internet
// =====================================================================
const SUGERENCIAS = [
  "¿El risotto tiene gluten?",
  "Mesa 7: dos milanesas napolitanas y una ensalada César",
  "¿Qué postre no tiene lácteos?",
  "¿La provoleta tiene huevo?",
  "Armame el análisis de ventas del día",
  "Proponé un menú para un evento de 40 personas",
];
const TABLET = { modelo: "modelo de 3B en Q4", memoria: 2.0, ram: 6, tps: 14, ttft: 0.45 };
const NUBE = { host: "integrate.api.nvidia.com", modelo: "nemotron-3.5-lightning-30b-a3b", tps: 70 };

function escenaB() {
  if (!$("#escena .tablet")) {
    $("#escena").innerHTML = `<div class="tablet-wrap"><div class="tablet">
      <div class="barra-t">${ic("monitor")}<b>MesaYa · Tablet del mozo</b><span>Sucursal Palermo · sábado 21:40</span><span class="red" id="red-ind"></span></div>
      <div class="chat" id="chat"></div>
      <div class="entrada-t"><input id="msg" placeholder="Preguntá por la carta o anotá un pedido…  (Enter)"><button class="btn" id="enviar">${ic("ask")}</button></div></div></div>`;
    $("#msg").onkeydown = e => { if (e.key === "Enter" && e.target.value.trim()) { enviarMozo(e.target.value.trim()); e.target.value = ""; } };
    $("#enviar").onclick = () => { const v = $("#msg").value.trim(); if (v) { enviarMozo(v); $("#msg").value = ""; } };
  }
  $("#red-ind").className = "red " + (S.internet ? "" : "off");
  $("#red-ind").innerHTML = S.internet ? `${ic("globe")} Con internet` : `${ic("ban")} Sin internet`;
  const chat = $("#chat");
  chat.innerHTML = S.chat.length ? S.chat.map(b => b.u
    ? `<div class="burbuja u">${esc(b.texto)}</div>`
    : `<div class="burbuja a ${b.donde === "nube" ? "nube" : ""} ${b.cola ? "cola" : ""}"><div class="meta">${b.chips.map(c => `<span class="chip ${c[1] || ""}">${c[0]}</span>`).join("")}</div><span class="${b.escribiendo ? "cursor" : ""}">${esc(b.texto)}</span></div>`).join("")
    : `<div class="vacio"><img src="/static/isotipo-blanco.png" alt=""><h2>Turno de la noche</h2><p>Probá las preguntas de la izquierda. Después cortá internet y repetí.</p></div>`;
  chat.scrollTop = chat.scrollHeight;
}

function registrarRed(e) { S.red.unshift({ t: ahora(), ...e }); if (S.esc === "B" && S.tab === "red") panelB(); }

async function escribir(b, texto, tps) {
  b.escribiendo = true; b.texto = "";
  const palabras = texto.split(/(\s+)/);
  for (const p of palabras) { b.texto += p; escenaB(); if (p.trim()) await dormir(1000 / tps * Math.max(1, p.length / 4)); }
  b.escribiendo = false; escenaB();
}

async function enviarMozo(texto) {
  if (S.ocupadoB) return;
  S.ocupadoB = true;
  S.chat.push({ u: true, texto }); escenaB();
  const ruta = rutaMozo(texto);
  try {
    if (ruta === "local") await responderLocal(texto);
    else if (S.internet) await responderNube(texto);
    else {
      S.cola.push(texto);
      S.chat.push({ cola: true, texto: "Sin internet: esto necesita el modelo grande. Lo mando apenas vuelva la conexión. La carta y los pedidos siguen funcionando.", chips: [[`${ic("clock")}en cola`, "mal"]] });
      registrarRed({ destino: NUBE.host, que: `“${texto}”`, estado: "en cola", bytes: 0, sale: false });
      escenaB();
    }
  } finally { S.ocupadoB = false; }
}

async function responderLocal(texto) {
  const b = { donde: "local", texto: "", chips: [] }; S.chat.push(b);
  const entrada = tokens(cartaTexto()) + tokens(texto) + 120;
  let resp, tps, ms, modelo;
  if (S.real) {
    b.chips = [[`${ic("monitor")}${esc(S.ollama.modelo_mozo)} · en este equipo`, "azul"]]; b.escribiendo = true; b.texto = ""; escenaB();
    try {
      const r = await post("/api/mozo", { mensaje: texto, carta: cartaTexto() }); const d = await r.json();
      if (!r.ok) throw new Error(d.detail);
      resp = { texto: d.texto }; tps = d.tokens_s || 30; ms = d.ms; modelo = d.modelo;
    } catch (e) { resp = respuestaLocal(texto); tps = TABLET.tps; ms = null; modelo = TABLET.modelo; b.chips.push(["falló el modelo real: simulado", "mal"]); }
  } else {
    resp = respuestaLocal(texto); tps = TABLET.tps * (0.9 + Math.random() * 0.2); modelo = TABLET.modelo;
    b.chips = [[`${ic("monitor")}tablet · ${TABLET.modelo}`, "azul"]]; b.escribiendo = true; escenaB();
    await dormir(TABLET.ttft * 1000);
    ms = Math.round(TABLET.ttft * 1000 + tokens(resp.texto) / tps * 1000);
  }
  b.chips.push([`${n1(tps)} t/s`, ""]);
  if (ms) b.chips.push([`${n1(ms / 1000)} s`, ""]);
  await escribir(b, resp.texto, S.real ? Math.max(tps, 40) : tps);
  S.bateria = Math.max(5, S.bateria - 0.4); S.temp = Math.min(46, S.temp + 0.6);
  registrarRed({ destino: "tablet (localhost)", que: `${resp.tipo === "pedido" ? "pedido" : "pregunta"} de la mesa · ${modelo}`, estado: "no salió del dispositivo", bytes: 0, sale: false, tokens: entrada });
  if (resp.tipo === "pedido") {
    S.pedidos.push({ mesa: resp.mesa, items: resp.items.map(i => i.plato), sincronizado: false });
    registrarRed({ destino: "cocina · red local 192.168.0.20", que: `comanda de la mesa ${resp.mesa}`, estado: "LAN de la sucursal", bytes: 180, sale: false });
  }
}

async function responderNube(texto, deCola = false) {
  const b = { donde: "nube", texto: "", chips: [[`${ic("globe")}nube · ${NUBE.modelo}`, "vio"]] }; S.chat.push(b);
  if (deCola) b.chips.push(["estaba en cola", "warn"]);
  b.escribiendo = true; escenaB();
  // A la nube viajan datos agregados del turno, no las mesas ni los clientes.
  const cuerpo = JSON.stringify({ model: NUBE.modelo, messages: [{ role: "user", content: texto }, { role: "user", content: "ventas del turno agregadas por plato" }] });
  const bytes = cuerpo.length + 2400;
  registrarRed({ destino: NUBE.host, que: `“${texto}” + ventas agregadas por plato (sin mesas ni clientes)`, estado: "enviado", bytes, sale: true });
  const ttft = 0.7 + Math.random() * 0.6; await dormir(ttft * 1000);
  const r = respuestaNube(texto);
  b.chips.push([`${n1(ttft + tokens(r) / NUBE.tps)} s`, ""]);
  await escribir(b, r, NUBE.tps);
}

async function cambiarInternet(on) {
  S.internet = on; escenaB(); pintarControles();
  registrarRed({ destino: on ? "router de la sucursal" : "router de la sucursal", que: on ? "volvió la conexión" : "se cortó internet", estado: on ? "online" : "offline", bytes: 0, sale: false, evento: true });
  if (!on) return;
  const pend = S.pedidos.filter(p => !p.sincronizado);
  if (pend.length) {
    await dormir(400);
    pend.forEach(p => p.sincronizado = true);
    registrarRed({ destino: "api.mesaya.com.ar", que: `sincroniza ${pend.length} pedido(s) del turno: totales por plato`, estado: "sincronizado", bytes: 900 + pend.length * 120, sale: true });
  }
  while (S.cola.length) { const t = S.cola.shift(); await dormir(300); S.ocupadoB = true; try { await responderNube(t, true); } finally { S.ocupadoB = false; } }
}

function panelB() {
  if (S.esc !== "B") return;
  if (S.tab === "disp") {
    $("#panel").innerHTML = `
      <div class="card"><h4>${ic("monitor")}La tablet</h4>
        <div class="kv"><span>Modelo</span><b>${S.real ? esc(S.ollama.modelo_mozo) + " (este equipo)" : TABLET.modelo}</b><span>Generación</span><b>${TABLET.tps} t/s</b>
          <span>Carta en el contexto</span><b>${n0(tokens(cartaTexto()))} tokens</b><span>Funciona sin red</span><b>sí</b></div></div>
      <div class="card"><h4>${ic("alert")}Los límites del edge</h4>
        <div class="medidor"><div class="lab">Memoria<b>${n1(TABLET.memoria)} de ${TABLET.ram} GB</b></div><div class="barra"><i style="width:${TABLET.memoria / TABLET.ram * 100}%"></i></div></div>
        <div class="medidor"><div class="lab">Batería<b>${n0(S.bateria)} %</b></div><div class="barra"><i style="width:${S.bateria}%"></i></div></div>
        <div class="medidor"><div class="lab">Temperatura<b>${n0(S.temp)} °C</b></div><div class="barra"><i style="width:${(S.temp - 25) / 25 * 100}%"></i></div></div>
        <p class="sub-ctl">Cada respuesta local gasta batería y calienta el equipo: por eso en el dispositivo van modelos chicos, muy cuantizados y de una sola tarea.</p></div>
      <div class="card"><h4>${ic("route")}La regla del router</h4>
        <pre class="term">${esc(`// lo que excede a la tablet va a la nube
const A_LA_NUBE = ${A_LA_NUBE};
ruta = A_LA_NUBE.test(pedido) ? "nube" : "local";
// sin internet: lo de la nube queda en cola`)}</pre></div>
      <div class="glos"><p><b>Edge:</b> ejecutar cerca del usuario o del dato, no en un centro de datos.</p><p><b>Patrón híbrido:</b> el dispositivo resuelve lo simple y deriva el resto a la nube.</p></div>`;
    return;
  }
  const local = S.red.filter(r => r.destino.startsWith("tablet")).length;
  const salieron = S.red.filter(r => r.sale);
  const bytes = salieron.reduce((a, r) => a + r.bytes, 0);
  $("#panel").innerHTML = `
    <div class="kpis tres"><div class="kpi"><div class="n">En la tablet</div><div class="v">${local}</div><div class="s">sin salir del dispositivo</div></div>
      <div class="kpi"><div class="n">A la nube</div><div class="v">${salieron.length}</div><div class="s">${n1(bytes / 1024)} KB, solo agregados</div></div>
      <div class="kpi ${S.cola.length ? "mal" : ""}"><div class="n">En cola</div><div class="v">${S.cola.length}</div><div class="s">${S.internet ? "con conexión" : "esperando la red"}</div></div></div>
    <div class="aviso info">${ic("lock")}<div><b>Datos de mesas y clientes que salieron de la sucursal: 0.</b> Privacidad simplifica el compliance, no lo resuelve: siguen haciendo falta accesos, logs y resguardo del equipo.</div></div>
    <div class="card"><h4>${ic("globe")}Cada request y su destino</h4>
      <div class="red-log">${S.red.length ? S.red.map(r => `<div class="it"><span class="t">${r.t}</span><span><span class="d">${esc(r.destino)}</span><br><span class="q">${esc(r.que)}</span></span>
        <span class="b"><span class="chip ${r.evento ? (r.estado === "offline" ? "mal" : "ok") : r.sale ? "vio" : r.estado === "en cola" ? "mal" : "azul"}">${esc(r.estado)}</span>${r.bytes ? `<br>${n1(r.bytes / 1024)} KB` : ""}</span></div>`).join("") : `<p class="panel-vacio">Todavía no hay requests.</p>`}</div></div>`;
}

// =====================================================================
// C · ¿Cuándo se paga el equipo?
// =====================================================================
// Mezcla típica de un asistente: 80 % de tokens de entrada (lectura) y 20 % de salida (generación).
const EF_PARALELO = { 1: 1, 4: 2.7, 8: 4.3 };
function cuenta() {
  const s = elegido();
  const mensualEquipo = S.costoEquipo / Math.max(1, S.meses) + S.energia + S.mant;
  const segPorToken = 0.8 / s.lectura + 0.2 / s.gen;
  const tps = (1 / segPorToken) * EF_PARALELO[S.paralelo];
  const capacidad = tps * 3600 * S.horas * 30 / 1e6;     // M tokens por mes por equipo
  const equipos = v => Math.max(1, Math.ceil(v / capacidad));
  const local = v => equipos(v) * mensualEquipo;
  const api = v => v * S.precioApi;
  const equilibrio = mensualEquipo / S.precioApi;
  return { s, mensualEquipo, tps, capacidad, equipos, local, api, equilibrio, entraEnUno: equilibrio <= capacidad };
}

function escenaC() {
  const c = cuenta(), v = S.volumen;
  const gana = c.local(v) < c.api(v) ? "local" : "api";
  const vols = [10, 50, 100, 300];
  $("#escena").innerHTML = `
    <div class="kpis"><div class="kpi"><div class="n">Equipo propio</div><div class="v chico">${usd(c.local(v))}</div><div class="s">${c.equipos(v)} equipo${c.equipos(v) > 1 ? "s" : ""} por mes</div></div>
      <div class="kpi"><div class="n">API</div><div class="v chico">${usd(c.api(v))}</div><div class="s">${n0(v)} M × ${usd(S.precioApi, 2)}</div></div>
      <div class="kpi"><div class="n">Punto de equilibrio</div><div class="v chico">${n0(c.equilibrio)} M</div><div class="s">tokens por mes</div></div>
      <div class="kpi ${c.capacidad < v ? "mal" : ""}"><div class="n">Capacidad por equipo</div><div class="v chico">${n0(c.capacidad)} M</div><div class="s">${esc(c.s.id)} · ${S.horas} h/día</div></div></div>
    ${curvaC(c)}
    <table class="vol"><thead><tr><th>Volumen por mes</th>${vols.map(x => `<th>${x} M</th>`).join("")}<th>${n0(v)} M (elegido)</th></tr></thead><tbody>
      <tr><td>API</td>${[...vols, v].map(x => `<td class="${c.api(x) <= c.local(x) ? "gana" : ""}">${usd(c.api(x))}</td>`).join("")}</tr>
      <tr><td>Equipo propio</td>${[...vols, v].map(x => `<td class="${c.local(x) < c.api(x) ? "gana" : ""}">${usd(c.local(x))}${c.equipos(x) > 1 ? ` · ${c.equipos(x)} eq.` : ""}</td>`).join("")}</tr></tbody></table>
    <div class="mensaje-clave">${gana === "api"
      ? `<b>Con ${n0(v)} M tokens por mes gana la API</b> por ${usd(c.local(v) - c.api(v))} al mes. Lo local se justificaría por privacidad u offline, si el modelo chico alcanza.`
      : `<b>Con ${n0(v)} M tokens por mes gana el equipo propio</b> por ${usd(c.api(v) - c.local(v))} al mes${c.equipos(v) > 1 ? `, pero hacen falta ${c.equipos(v)} equipos y alguien que los mantenga` : ""}.`}
      ${c.entraEnUno ? "" : ` Ojo: un equipo no llega al punto de equilibrio (${n0(c.capacidad)} M de capacidad); con este modelo, lo local nunca gana por costo.`}</div>`;
  const svg = $("#curva"); if (!svg) return;
  svg.onmousemove = ev => {
    const r = svg.getBoundingClientRect(), W = 720, m = 54;
    const px = (ev.clientX - r.left) / r.width * W; if (px < m || px > W - 16) return ocultarTip();
    const x = (px - m) / (W - m - 16) * svg._xMax;
    $("#cruz").setAttribute("x1", px); $("#cruz").setAttribute("x2", px); $("#cruz").style.opacity = 1;
    mostrarTip(`<b>${n0(x)} M tokens / mes</b><br>API ${usd(c.api(x))}<br>Equipo propio ${usd(c.local(x))} (${c.equipos(x)} eq.)`, ev);
  };
  svg.onmouseleave = () => { ocultarTip(); $("#cruz").style.opacity = 0; };
  svg._xMax = Math.max(400, S.volumen * 1.25, c.equilibrio * 1.6);
}

function curvaC(c) {
  const W = 720, H = 280, m = { l: 54, r: 16, t: 16, b: 34 };
  const xMax = Math.max(400, S.volumen * 1.25, c.equilibrio * 1.6);
  const yMax = Math.max(c.api(xMax), c.local(xMax)) * 1.08;
  const x = v => m.l + v / xMax * (W - m.l - m.r), y = v => H - m.b - v / yMax * (H - m.t - m.b);
  const pasos = 160, ptsL = [];
  for (let i = 0; i <= pasos; i++) { const v = xMax * i / pasos; ptsL.push(`${x(v)},${y(c.local(v))}`); }
  const ticks = n => { const p = Math.pow(10, Math.floor(Math.log10(n / 4))), st = [1, 2, 5, 10].map(k => k * p).find(k => n / k <= 6); const r = []; for (let v = 0; v <= n; v += st) r.push(v); return r; };
  const eq = c.equilibrio;
  return `<div class="grafico"><div class="ttl">Costo por mes según el volumen</div><div class="st">API a ${usd(S.precioApi, 2)} por millón · equipo propio: ${usd(c.mensualEquipo)} por mes por equipo</div>
    <svg viewBox="0 0 ${W} ${H}" id="curva">
      <g class="eje">${ticks(yMax).map(v => `<line x1="${m.l}" x2="${W - m.r}" y1="${y(v)}" y2="${y(v)}"/><text x="${m.l - 8}" y="${y(v) + 4}" text-anchor="end">${n0(v)}</text>`).join("")}
        ${ticks(xMax).map(v => `<text x="${x(v)}" y="${H - m.b + 18}" text-anchor="middle">${n0(v)} M</text>`).join("")}</g>
      <line x1="${x(S.volumen)}" x2="${x(S.volumen)}" y1="${m.t}" y2="${H - m.b}" stroke="var(--gris-3)" stroke-width="1.5" stroke-dasharray="3 3"/>
      <text class="lab muted" x="${x(S.volumen) + 5}" y="${H - m.b - 6}">elegido</text>
      <polyline points="${x(0)},${y(0)} ${x(xMax)},${y(c.api(xMax))}" fill="none" stroke="var(--s1)" stroke-width="2"/>
      <polyline points="${ptsL.join(" ")}" fill="none" stroke="var(--s3)" stroke-width="2"/>
      ${eq <= xMax && c.entraEnUno ? `<circle cx="${x(eq)}" cy="${y(c.api(eq))}" r="6" fill="var(--azul)" stroke="#fff" stroke-width="2"/>
        <text class="lab" x="${x(eq) + 9}" y="${y(c.api(eq)) - 9}">equilibrio: ${n0(eq)} M</text>` : ""}
      <text class="lab" x="${x(xMax) - 4}" y="${y(c.api(xMax)) + 16}" text-anchor="end">API</text>
      <text class="lab" x="${x(xMax) - 4}" y="${y(c.local(xMax)) - 8}" text-anchor="end">Equipo propio</text>
      <line id="cruz" x1="0" x2="0" y1="${m.t}" y2="${H - m.b}" stroke="var(--azul-2)" stroke-width="1" style="opacity:0"/>
    </svg>
    <div class="ley"><span><i style="background:var(--s1)"></i>API (lineal)</span><span><i style="background:var(--s3)"></i>Equipo propio (escalones: un equipo más cuando se llena)</span></div></div>`;
}

function perfilLocal() {
  const c = cuenta(), v = Math.min(S.volumen, c.capacidad);
  return { modelo: c.s.id, cuantizacion: c.s.q, equipo: equipo().nombre, tokens_s_lectura: Math.round(c.s.lectura), tokens_s_generacion: Number(c.s.gen.toFixed(1)),
           usd_por_millon: Number((c.local(v) / Math.max(v, 1)).toFixed(2)), volumen_m: Math.round(v), origen: "simulado en la demo de Ejecución local y edge" };
}

async function panelC() {
  if (S.esc !== "C") return;
  const c = cuenta();
  if (S.tab === "perfil") {
    if (S.perfil === null) { try { S.perfil = await (await fetch("/api/perfil-local")).json(); } catch { S.perfil = {}; } }
    const p = perfilLocal();
    $("#panel").innerHTML = `
      <div class="aviso info">${ic("link")}<div>La demo de <b>Costos, latencia y fallback</b> compara la API con un perfil <b>Local</b>. Ese perfil sale de acá: el modelo, sus tokens por segundo y el costo por millón de tokens del equipo al volumen elegido.</div></div>
      <div class="card"><h4>${ic("code")}Lo que se guarda</h4><pre class="term">${esc(JSON.stringify(p, null, 2))}</pre>
        <button class="btn" id="guardar" style="margin-top:10px">${ic("check")}Usar como perfil Local</button></div>
      ${S.perfil?.modelo ? `<div class="card"><h4>${ic("filecheck")}Perfil guardado</h4><div class="kv"><span>Modelo</span><b>${esc(S.perfil.modelo)}</b><span>USD por millón</span><b>${S.perfil.usd_por_millon}</b>
        <span>Generación</span><b>${S.perfil.tokens_s_generacion} t/s</b><span>Guardado</span><b>${esc(String(S.perfil.guardado).replace("T", " "))}</b></div></div>` : ""}
      <p class="sub-ctl">Se guarda en <code>datos/perfil-local.json</code>. La demo de Costos lo lee al iniciar; si no existe, usa un perfil de ejemplo.</p>`;
    $("#guardar").onclick = async () => { const r = await post("/api/perfil-local", p); S.perfil = await r.json(); panelC(); };
    return;
  }
  $("#panel").innerHTML = `
    <div class="card"><h4>${ic("table")}Costo local por mes</h4>
      <div class="kv"><span>Equipo ${usd(S.costoEquipo)} en ${S.meses} meses</span><b>${usd(S.costoEquipo / Math.max(1, S.meses))}</b><span>Energía</span><b>${usd(S.energia)}</b>
        <span>Mantenimiento</span><b>${usd(S.mant)}</b><span><b style="color:var(--azul);font-family:Lato">Por equipo</b></span><b>${usd(c.mensualEquipo)}</b></div></div>
    <div class="card"><h4>${ic("bolt")}Capacidad de un equipo</h4>
      <div class="formula">${esc(c.s.id)} en ${esc(equipo().nombre)}<br>
        lectura <b>${n0(c.s.lectura)}</b> t/s · generación <b>${n1(c.s.gen)}</b> t/s<br>
        mezcla 80 % entrada / 20 % salida → <b>${n0(c.tps / EF_PARALELO[S.paralelo])}</b> t/s<br>
        × ${n1(EF_PARALELO[S.paralelo])} por ${S.paralelo} pedido${S.paralelo > 1 ? "s" : ""} en paralelo → <b>${n0(c.tps)}</b> t/s<br>
        × 3.600 s × ${S.horas} h × 30 días = <b>${n0(c.capacidad)} M tokens/mes</b></div>
      <p class="sub-ctl">El modelo y el equipo vienen del escenario A. Un modelo más chico atiende más tokens, pero ¿alcanza la calidad? (${c.s.aciertos}/${TAREAS} aciertos)</p></div>
    <div class="card"><h4>${ic("scale")}Punto de equilibrio</h4>
      <div class="formula">${usd(c.mensualEquipo)} ÷ ${usd(S.precioApi, 2)} por millón = <b>${n0(c.equilibrio)} M tokens por mes</b></div>
      <p class="sub-ctl">${c.entraEnUno ? "Por debajo, la API es más barata." : "Un solo equipo no llega a ese volumen: con más equipos, el costo crece en escalones y el equilibrio no llega."}</p></div>
    <div class="glos"><p><b>Volumen:</b> cantidad de tokens que el sistema procesa por mes.</p><p><b>Valores ilustrativos,</b> como en la slide 3.7. Falta sumar lo que no está en la cuenta: el tiempo de quien lo opera.</p></div>`;
}

// ---------- inicio ----------
elegir("A");
estado();
