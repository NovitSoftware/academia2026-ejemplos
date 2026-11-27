// MesaYa · Ejecución local y edge — el modelo de simulación.
// Los números salen de las mismas cuentas que las slides, con ruido estadístico para que se vean como mediciones.
//   memoria   ≈ parámetros × bits por peso ÷ 8 + contexto (KV cache) + overhead
//   generación: limitada por el ancho de banda de memoria (cada token relee todos los pesos)
//   lectura (prompt eval): limitada por el cómputo (2 operaciones por parámetro y token, en paralelo)

// ---------- números aleatorios reproducibles ----------
function rng(semilla) {
  let s = 0; for (const c of String(semilla)) s = (s * 31 + c.charCodeAt(0)) >>> 0;
  return () => { s = (s + 0x6D2B79F5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
const normal = r => Math.sqrt(-2 * Math.log(1 - r())) * Math.cos(2 * Math.PI * r());

// ---------- equipos ----------
// vram: GB disponibles para el modelo · bw: ancho de banda GB/s · tflops: FP16 · ram/bwRam: memoria del sistema
const EQUIPOS = [
  { id: "notebook", nombre: "Notebook con GPU de 8 GB", sub: "RTX 4060 Laptop · 32 GB de RAM", vram: 8, bw: 256, tflops: 22, ram: 32, bwRam: 60, cpuTflops: 0.6 },
  { id: "este", nombre: "Este equipo · RX 6700 XT", sub: "12 GB de VRAM · 31 GB de RAM", vram: 12, bw: 384, tflops: 26, ram: 31, bwRam: 45, cpuTflops: 0.4 },
  { id: "gpu16", nombre: "GPU de 16 GB", sub: "RTX 4070 Ti Super · 64 GB de RAM", vram: 16, bw: 672, tflops: 44, ram: 64, bwRam: 70, cpuTflops: 0.8 },
  { id: "gpu24", nombre: "GPU de 24 GB", sub: "RTX 4090 · 64 GB de RAM", vram: 24, bw: 1008, tflops: 82, ram: 64, bwRam: 70, cpuTflops: 0.8 },
  { id: "mac", nombre: "Mac con 64 GB", sub: "M3 Max · memoria unificada (la GPU usa ~75 %)", vram: 48, bw: 400, tflops: 28, ram: 64, bwRam: 400, cpuTflops: 2, unificada: true },
  { id: "cpu", nombre: "Solo CPU", sub: "Mini PC sin GPU · 32 GB de RAM", vram: 0, bw: 60, tflops: 0.6, ram: 32, bwRam: 60, cpuTflops: 0.6 },
];

// ---------- cuantizaciones (bits por peso efectivos, como los reporta llama.cpp) ----------
const CUANT = {
  F16:    { bits: 16,  calidad: 1.000, nota: "sin cuantizar" },
  Q8_0:   { bits: 8.5, calidad: 0.995, nota: "prácticamente igual" },
  Q6_K:   { bits: 6.6, calidad: 0.990, nota: "pérdida mínima" },
  Q5_K_M: { bits: 5.7, calidad: 0.984, nota: "pérdida muy baja" },
  Q4_K_M: { bits: 4.85, calidad: 0.968, nota: "el equilibrio habitual" },
  Q3_K_S: { bits: 3.5, calidad: 0.885, nota: "degradación notable" },
  Q2_K:   { bits: 2.9, calidad: 0.760, nota: "solo para probar" },
};

// Calidad base (F16) sobre las 60 tareas de MesaYa, por tamaño. Ilustrativa, con la forma de las curvas publicadas.
const CALIDAD_BASE = { 1.7: 46, 4: 63, 8: 71, 14: 78, 32: 85 };

const MODELOS = [
  { id: "qwen3:1.7b-q4_K_M", tam: 1.7, q: "Q4_K_M" },
  { id: "qwen3:4b-q4_K_M", tam: 4, q: "Q4_K_M" },
  { id: "qwen3:8b-fp16", tam: 8, q: "F16" },
  { id: "qwen3:8b-q8_0", tam: 8, q: "Q8_0" },
  { id: "qwen3:8b-q5_K_M", tam: 8, q: "Q5_K_M" },
  { id: "qwen3:8b-q4_K_M", tam: 8, q: "Q4_K_M" },
  { id: "qwen3:8b-q3_K_S", tam: 8, q: "Q3_K_S" },
  { id: "qwen3:14b-q4_K_M", tam: 14, q: "Q4_K_M" },
  { id: "qwen3:32b-q4_K_M", tam: 32, q: "Q4_K_M" },
];

const TAREAS = 60;
// Para tamaños que no están en la tabla (por ejemplo un modelo instalado de 4,2B), se interpola en escala logarítmica.
function calidadBase(tam) {
  if (CALIDAD_BASE[tam]) return CALIDAD_BASE[tam];
  const ks = Object.keys(CALIDAD_BASE).map(Number).sort((a, b) => a - b);
  if (tam <= ks[0]) return CALIDAD_BASE[ks[0]] * tam / ks[0];
  if (tam >= ks.at(-1)) return CALIDAD_BASE[ks.at(-1)];
  const i = ks.findIndex(k => k > tam), a = ks[i - 1], b = ks[i];
  return CALIDAD_BASE[a] + (CALIDAD_BASE[b] - CALIDAD_BASE[a]) * Math.log(tam / a) / Math.log(b / a);
}

// KV cache: ~1 GB cada 8k de contexto para un 8B (crece con el tamaño, no proporcional por la atención agrupada).
const kvGB = (tam, ctx) => 1.0 * Math.pow(tam / 8, 0.75) * ctx / 8192;
const OVERHEAD = 0.45;   // buffers de cómputo y runtime
const RESERVA = 0.6;     // lo que usa el escritorio y el driver

function simular(m, eq, ctx, corrida = 0) {
  const c = CUANT[m.q] ?? CUANT.Q4_K_M;
  const pesos = m.tam * c.bits / 8;
  const kv = kvGB(m.tam, ctx);
  const total = pesos + kv + OVERHEAD;
  const util = Math.max(0, eq.vram - (eq.unificada ? 0 : RESERVA));
  // Ollama carga el contexto y los buffers en la GPU y reparte las capas que entran.
  let gpu = eq.vram === 0 ? 0 : total <= util ? 1 : Math.max(0, Math.min(1, (util - kv - OVERHEAD) / pesos));
  const entra = total <= util + eq.ram * 0.8 - 4;   // con el sistema operativo y el resto
  const r = rng(`${m.id}|${eq.id}|${ctx}|${corrida}`);
  const ruido = (sd) => 1 + sd * normal(r);

  // Generación: cada token lee todos los pesos; lo que está en la RAM se lee mucho más lento.
  const efG = 0.55, efC = 0.6;
  const tTok = (pesos * gpu) / (eq.bw * efG) + (pesos * (1 - gpu)) / (eq.bwRam * efC) + 0.004 + kv * 0.0012;
  const gen = (1 / tTok) * ruido(0.04);
  // Lectura: en paralelo, limitada por el cómputo. Con capas en la CPU, esas capas van a la velocidad de la CPU.
  const flops = 2 * m.tam * 1e9;
  const ppG = eq.tflops * 1e12 * 0.16 / flops;
  // llama.cpp manda a la GPU las multiplicaciones grandes aunque la capa viva en la RAM: la lectura cae, pero menos que la generación.
  const ppC = eq.vram > 0 ? ppG * 0.22 : eq.cpuTflops * 1e12 * 0.35 / flops;
  const lectura = (1 / (gpu / ppG + (1 - gpu) / ppC)) * ruido(0.05);

  // Calidad: base por tamaño × factor de la cuantización. No depende del equipo: el mismo archivo responde lo mismo.
  const p = calidadBase(m.tam) * c.calidad / 100;
  const rq = rng(`${m.id}|${corrida}`);
  const aciertos = Math.max(0, Math.min(TAREAS, Math.round(p * TAREAS + 0.4 * normal(rq))));
  const carga = (pesos / (gpu === 1 ? 2.2 : 1.4)) * ruido(0.08);   // segundos desde el disco

  return { ...m, bits: c.bits, nota: c.nota, pesos, kv, total, util, gpu, entra, gen, lectura, aciertos, esperado: p * TAREAS, carga };
}

// `ollama run --verbose` simulado para un pedido de 812 tokens de entrada y 236 de salida (slide 3.4).
function verbose(s, entrada = 812, salida = 236) {
  const pe = entrada / s.lectura, ev = salida / s.gen, load = 0.048;
  const f = (x, d = 2) => x.toFixed(d);
  return [
    `total duration:       ${f(pe + ev + load)}s`,
    `load duration:        ${Math.round(load * 1000)}ms`,
    `prompt eval count:    ${entrada} token(s)`,
    `prompt eval duration: ${f(pe)}s`,
    `prompt eval rate:     ${f(s.lectura)} tokens/s`,
    `eval count:           ${salida} token(s)`,
    `eval duration:        ${f(ev)}s`,
    `eval rate:            ${f(s.gen)} tokens/s`,
  ].join("\n");
}

function ps(s, ctx) {
  const proc = s.gpu >= 0.999 ? "100% GPU" : s.gpu <= 0.001 ? "100% CPU" : `${Math.round((1 - s.gpu) * 100)}%/${Math.round(s.gpu * 100)}% CPU/GPU`;
  const pad = (t, n) => String(t).padEnd(n);
  return `${pad("NAME", 22)}${pad("SIZE", 10)}${pad("PROCESSOR", 18)}CONTEXT\n${pad(s.id, 22)}${pad(s.total.toFixed(1) + " GB", 10)}${pad(proc, 18)}${ctx}`;
}

// ---------- la carta de la sucursal (lo que la tablet tiene guardado) ----------
const CARTA = [
  { plato: "Risotto de hongos", tipo: "principal", precio: 14500, alergenos: ["lácteos"], nota: "el caldo es casero, sin cubitos" },
  { plato: "Milanesa napolitana", tipo: "principal", precio: 13800, alergenos: ["gluten", "huevo", "lácteos"] },
  { plato: "Bife de chorizo", tipo: "principal", precio: 18900, alergenos: [], nota: "con papas fritas; la freidora es compartida con rebozados" },
  { plato: "Ñoquis de papa", tipo: "principal", precio: 11200, alergenos: ["gluten", "huevo"] },
  { plato: "Ensalada César", tipo: "entrada", precio: 9400, alergenos: ["gluten", "huevo", "pescado", "lácteos"], nota: "sin croutons queda sin gluten" },
  { plato: "Provoleta", tipo: "entrada", precio: 8200, alergenos: ["lácteos"] },
  { plato: "Empanadas de carne", tipo: "entrada", precio: 2600, alergenos: ["gluten"] },
  { plato: "Flan casero", tipo: "postre", precio: 5200, alergenos: ["huevo", "lácteos"] },
  { plato: "Tiramisú", tipo: "postre", precio: 6400, alergenos: ["gluten", "huevo", "lácteos"] },
  { plato: "Sorbete de limón", tipo: "postre", precio: 4800, alergenos: [], nota: "vegano" },
  { plato: "Ensalada de frutas", tipo: "postre", precio: 4500, alergenos: [] },
];
const cartaTexto = () => CARTA.map(c => `- ${c.plato} (${c.tipo}, $${c.precio.toLocaleString("es-AR")}): alérgenos ${c.alergenos.length ? c.alergenos.join(", ") : "ninguno"}${c.nota ? "; " + c.nota : ""}`).join("\n");

// ---------- el router de la tablet: lo simple en el dispositivo, lo grande en la nube ----------
const A_LA_NUBE = /(análisis|analiz|informe|reporte|propuesta|propon|evento|menú para|resumen del día|ventas del|tendencia|compar)/i;
const rutaMozo = t => A_LA_NUBE.test(t) ? "nube" : "local";

const ALERGENOS = { gluten: /glut|celíac|celiac|tacc/i, lácteos: /lact|lácte|leche|queso/i, huevo: /huevo/i, pescado: /pescad|anchoa/i };
const normalizar = t => t.toLowerCase().normalize("NFD").replace(/[̀-ͯ]/g, "");

// Respuestas del modelo local simulado: un modelo chico con la carta en el contexto responde así.
function respuestaLocal(t) {
  const n = normalizar(t);
  // Cuántas palabras del plato aparecen en el texto: gana el plato con más coincidencias.
  const claves = c => normalizar(c.plato).split(" ").filter(w => w.length > 3);
  const coincide = c => claves(c).filter(w => n.includes(w.replace(/s$/, ""))).length;
  const plato = CARTA.map(c => [c, coincide(c)]).filter(([, k]) => k > 0).sort((a, b) => b[1] - a[1])[0]?.[0];
  const alergeno = Object.entries(ALERGENOS).find(([, re]) => re.test(t))?.[0];
  const mesa = n.match(/mesa\s*(\d+)/);
  if (mesa && !/\?/.test(t)) {
    // En un pedido van los platos nombrados enteros ("ensalada César" no suma "ensalada de frutas").
    const cabeza = c => claves(c)[0], compartida = c => CARTA.filter(o => cabeza(o) === cabeza(c)).length > 1;
    const items = CARTA.filter(c => coincide(c) === claves(c).length || (!compartida(c) && n.includes(cabeza(c).replace(/s$/, ""))));
    return { texto: `Anotado para la mesa ${mesa[1]}: ${items.length ? items.map(i => i.plato).join(", ") : "pedido sin platos de la carta"}. Queda en la tablet y se manda a cocina por la red local.`, tipo: "pedido", mesa: mesa[1], items };
  }
  if (plato && alergeno) {
    const tiene = plato.alergenos.includes(alergeno);
    return { texto: `${plato.plato}: ${tiene ? "sí tiene" : "no tiene"} ${alergeno}.${plato.nota ? " Ojo: " + plato.nota + "." : ""}` };
  }
  if (alergeno) {
    const tipo = /postre/.test(n) ? "postre" : /entrada/.test(n) ? "entrada" : /principal|plato/.test(n) ? "principal" : null;
    const sin = CARTA.filter(c => !c.alergenos.includes(alergeno) && (!tipo || c.tipo === tipo));
    return { texto: `Sin ${alergeno}${tipo ? ` (${tipo}s)` : ""}: ${sin.map(c => c.plato).join(", ")}.` };
  }
  if (plato) return { texto: `${plato.plato}: $${plato.precio.toLocaleString("es-AR")}. Alérgenos: ${plato.alergenos.length ? plato.alergenos.join(", ") : "ninguno"}.${plato.nota ? " " + plato.nota[0].toUpperCase() + plato.nota.slice(1) + "." : ""}` };
  return { texto: "Eso no figura en la carta guardada. Consultalo en cocina." };
}

const RESPUESTAS_NUBE = [
  [/an[aá]lisis|ventas|resumen|informe|reporte/i, "Resumen del turno: 46 cubiertos, ticket promedio $21.300. Lo más pedido fue la milanesa napolitana (14) y el risotto (9). El 22 % de las mesas pidió postre, por debajo del 30 % de los sábados anteriores: conviene ofrecerlo al retirar los platos."],
  [/evento|propuesta|men[uú] para/i, "Propuesta para 40 personas: recepción con empanadas y provoleta, principal a elección entre bife de chorizo y risotto de hongos (sin gluten), y sorbete de limón como postre vegano. Costo estimado $26.500 por persona; seña del 30 % por ser más de 12."],
];
const respuestaNube = t => (RESPUESTAS_NUBE.find(([re]) => re.test(t)) ?? [null, "Listo: lo analicé con los datos agregados de la sucursal."])[1];

// tokens aproximados de un texto en español (~4 caracteres por token)
const tokens = t => Math.max(1, Math.round(String(t).length / 4));
