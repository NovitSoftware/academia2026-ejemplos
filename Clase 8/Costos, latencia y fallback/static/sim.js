// MesaYa · Costos, latencia y fallback — el modelo de simulación.
// Una trayectoria del agente de reservas: cada paso relee todo lo anterior. Los tokens salen del texto real
// de cada bloque (≈ 4 caracteres por token); los tiempos, de distribuciones con cola larga, como en producción.

function rng(semilla) {
  let s = 0; for (const c of String(semilla)) s = (s * 31 + c.charCodeAt(0)) >>> 0;
  return () => { s = (s + 0x6D2B79F5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
const normal = r => Math.sqrt(-2 * Math.log(1 - r())) * Math.cos(2 * Math.PI * r());
const lognormal = (r, mediana, sigma) => mediana * Math.exp(sigma * normal(r));
const tokens = t => Math.max(1, Math.round(String(t).length / 4));

// ---------- perfiles ----------
const PERFIL_API = {
  id: "api", nombre: "NVIDIA", modelo: "nemotron-3.5-lightning-30b-a3b",
  entrada: 3, salida: 15, cache: 0.3,           // USD por millón; la caché cobra el 10 % de la entrada
  ttftBase: 0.38, prefill: 22000, prefillCache: 200000, gen: 115, cola: 0.04,
};
function perfilLocal(p) {
  return {
    id: "local", nombre: "Local", modelo: p.modelo, equipo: p.equipo, fuente: p.fuente,
    entrada: p.usd_por_millon, salida: p.usd_por_millon, cache: p.usd_por_millon,   // el equipo cuesta lo mismo por token
    ttftBase: 0.05, prefill: p.tokens_s_lectura, prefillCache: p.tokens_s_lectura * 25, gen: p.tokens_s_generacion, cola: 0,
  };
}

// ---------- los bloques del prompt ----------
const SISTEMA = `Sos el asistente de reservas de MesaYa, una app de restaurantes con sucursales en Palermo, Belgrano y Centro.
Tu trabajo es tomar reservas, modificarlas y cancelarlas por WhatsApp. Respondé en español rioplatense, breve y cordial.
Reglas:
1. Antes de reservar, buscá disponibilidad con la herramienta buscar_disponibilidad. Nunca inventes mesas ni horarios.
2. Si el cliente ya existe, consultá su historial con consultar_cliente: los clientes frecuentes (más de 5 visitas en el año) pueden reservar sin seña una vez por mes.
3. Para grupos de 7 o más, consultá la política de señas vigente con politica_senas y explicale al cliente el monto antes de confirmar.
4. Confirmá la reserva con reservar solo cuando el cliente aceptó día, hora, sucursal y cantidad de personas.
5. Si la sucursal no tiene lugar, ofrecé el horario más cercano o la sucursal más cercana con lugar.
6. No compartas datos de otros clientes. No prometas descuentos que no figuren en la política.
7. Si el pedido no es sobre reservas, decí amablemente que solo podés ayudar con reservas.
Política de cancelación v3 (vigente desde el 01/09):
- Cancelación con 48 horas o más de anticipación: se devuelve el 100 % de la seña.
- Entre 24 y 48 horas: se devuelve el 50 %.
- Menos de 24 horas o no presentarse: no se devuelve la seña.
- Los cambios de horario en el mismo día no cuentan como cancelación si hay disponibilidad.
- Las reservas de más de 8 personas los sábados a la noche deben confirmarse por WhatsApp el día anterior antes de las 18 h; si no se confirman, se liberan.
Formato de respuesta: una o dos oraciones; al confirmar, incluí el código de reserva, la sucursal, el día, la hora y la cantidad de personas.
Horarios de las sucursales: Palermo de 12 a 16 y de 20 a 0.30; Belgrano de 12 a 15.30 y de 20 a 0; Centro de 12 a 16 y de 19.30 a 23.30.
Las mesas de vereda dependen del clima: si llueve, se reubican adentro según disponibilidad. Las mesas para personas con movilidad reducida están en planta baja en las tres sucursales.
Menús especiales: hay opciones sin TACC, vegetarianas y veganas; si el cliente lo pide, anotalo en las observaciones de la reserva para que cocina lo sepa con anticipación.
Eventos y grupos de más de 15 personas: no se reservan por este canal; derivá a eventos@mesaya.com.ar con un mensaje claro.`;

const herramienta = (nombre, desc, props, req) => ({ type: "function", function: { name: nombre, description: desc, parameters: { type: "object", properties: props, required: req } } });
const TOOLS = [
  herramienta("buscar_disponibilidad", "Busca mesas libres en una sucursal para una fecha, una hora y una cantidad de personas. Devuelve las mesas con su sector, capacidad, horarios cercanos y características. Usala siempre antes de reservar.",
    { sucursal: { type: "string", enum: ["Palermo", "Belgrano", "Centro"], description: "Sucursal pedida por el cliente" }, fecha: { type: "string", description: "Fecha en formato AAAA-MM-DD; si el cliente dice 'hoy' o 'mañana', calculala" },
      hora: { type: "string", description: "Hora en formato HH:MM, de 24 horas" }, personas: { type: "integer", minimum: 1, maximum: 20, description: "Cantidad de comensales" },
      sector: { type: "string", enum: ["salón", "vereda", "terraza", "cualquiera"], description: "Preferencia de sector, si la hay" } }, ["sucursal", "fecha", "hora", "personas"]),
  herramienta("consultar_cliente", "Devuelve el historial del cliente por su teléfono: visitas en el año, reservas activas, cancelaciones y preferencias. Sirve para saber si es frecuente.",
    { telefono: { type: "string", description: "Teléfono del cliente en formato internacional, por ejemplo +5491155550000" } }, ["telefono"]),
  herramienta("politica_senas", "Devuelve la política de señas vigente para una cantidad de personas, un día y una sucursal: porcentaje, monto mínimo y condiciones de devolución.",
    { personas: { type: "integer", description: "Cantidad de comensales" }, dia_semana: { type: "string", description: "Día de la semana de la reserva" }, sucursal: { type: "string", description: "Sucursal" } }, ["personas", "dia_semana"]),
  herramienta("reservar", "Crea la reserva y devuelve su código. Usala solo cuando el cliente confirmó todos los datos.",
    { sucursal: { type: "string" }, fecha: { type: "string" }, hora: { type: "string" }, personas: { type: "integer" }, mesa: { type: "string", description: "Identificador de la mesa elegida en buscar_disponibilidad" },
      telefono: { type: "string" }, sena: { type: "number", description: "Monto de la seña, 0 si no corresponde" }, observaciones: { type: "string", description: "Pedidos especiales: sin TACC, silla para bebé, cumpleaños" } }, ["sucursal", "fecha", "hora", "personas", "mesa", "telefono"]),
];
const TOOLS_TXT = JSON.stringify(TOOLS, null, 1);

const HISTORIAL_PREVIO = [
  { role: "user", content: "Hola! Tienen lugar este sábado?" },
  { role: "assistant", content: "¡Hola! Sí, el sábado abrimos en Palermo, Belgrano y Centro. ¿Para cuántas personas, a qué hora y en qué sucursal?" },
  { role: "user", content: "Somos 4. Hay estacionamiento en Palermo? Y se puede ir con un perro chico?" },
  { role: "assistant", content: "En Palermo no tenemos estacionamiento propio, pero hay uno a media cuadra sobre Gorriti con descuento para clientes. Las mascotas chicas son bienvenidas en la vereda y la terraza. ¿Qué horario te queda bien?" },
];
const CONSULTA = "Dale, mesa para 4 a las 22 en Palermo. Si puede ser en la terraza, mejor.";
const FECHA = "Fecha y hora actual: sábado 14/11 21:03:17";

// La disponibilidad tal como la devuelve el sistema de reservas: muchos campos que el modelo no necesita.
function disponibilidadCompleta() {
  const sectores = ["terraza", "salón", "vereda", "salón", "terraza", "salón", "vereda", "salón", "terraza", "salón", "salón", "vereda"];
  const mesas = sectores.map((s, i) => ({
    id: `PAL-${s.slice(0, 3).toUpperCase()}-${String(i + 3).padStart(2, "0")}`, sucursal_id: 1, sucursal: "Palermo", sector: s, capacidad: [2, 4, 4, 6, 4, 2, 4, 8, 4, 4, 6, 2][i],
    hora: ["21:30", "22:00", "22:00", "22:15", "22:30", "22:00", "22:45", "21:45", "22:00", "23:00", "22:30", "22:15"][i], disponible: true,
    combinable_con: i % 3 === 0 ? [`PAL-SAL-${String(i + 4).padStart(2, "0")}`] : [], accesible: i % 4 === 0, junto_ventana: i % 2 === 0, techada: s !== "vereda",
    calefaccion: s !== "salón", mascotas: s !== "salón", ruido: ["bajo", "medio", "alto"][i % 3], foto_url: `https://cdn.mesaya.com.ar/sucursales/palermo/mesas/${i + 3}.jpg`,
    descripcion: `Mesa de ${s} con vista ${i % 2 ? "a la calle" : "al patio interno"}, ideal para ${i % 2 ? "parejas" : "grupos chicos"}`,
    precio_minimo_por_persona: 0, mozo_asignado: ["Lucía", "Martín", "Sofía", "Julián"][i % 4], ultima_limpieza: "2026-11-14T19:40:00-03:00",
    reservas_previas_hoy: i % 3, tiempo_promedio_mesa_min: 95 + i * 3, rating_mesa: (4 + (i % 10) / 10).toFixed(1), creado: "2023-04-02T10:00:00-03:00", actualizado: "2026-11-10T08:12:44-03:00",
  }));
  return JSON.stringify({ ok: true, consulta: { sucursal: "Palermo", fecha: "2026-11-14", hora: "22:00", personas: 4, sector: "terraza" }, total: mesas.length, mesas,
    meta: { version_api: "3.4.1", cache: "miss", latencia_ms: 812, servidor: "reservas-2.pal.mesaya.internal", traza: "0af7651916cd43dd8448eb211c80319c" } }, null, 1);
}
// Lo mismo, recortado a los campos que usa el modelo.
function disponibilidadRecortada() {
  const d = JSON.parse(disponibilidadCompleta());
  return JSON.stringify(d.mesas.filter(m => m.capacidad >= 4 && Math.abs(Number(m.hora.slice(0, 2)) * 60 + Number(m.hora.slice(3)) - 22 * 60) <= 30)
    .map(m => ({ id: m.id, sector: m.sector, capacidad: m.capacidad, hora: m.hora, mascotas: m.mascotas })));
}
const CLIENTE = JSON.stringify({ telefono: "+54911••••4821", nombre: "Carla", visitas_anio: 7, frecuente: true, reservas_activas: [], cancelaciones_anio: 1, sin_sena_usada_mes: false,
  preferencias: ["terraza", "mesa con mascota"], alergias: [], ultima_visita: "2026-10-31", sucursal_habitual: "Palermo", observaciones: "Viene con un perro chico" }, null, 1);
const POLITICA = JSON.stringify({ personas: 4, dia: "sábado", sucursal: "Palermo", sena_porcentaje: 0, motivo: "grupos de hasta 6 personas no dejan seña", monto_minimo: 0,
  frecuente_sin_sena: "aplica una vez por mes", devolucion: { "48h_o_mas": "100 %", "24_a_48h": "50 %", "menos_24h": "0 %" }, confirmacion_previa: false }, null, 1);
const RESERVA = JSON.stringify({ ok: true, codigo: "R-4821", sucursal: "Palermo", fecha: "2026-11-14", hora: "22:00", personas: 4, mesa: "PAL-TER-04", sena: 0 }, null, 1);
const RESPUESTA = "¡Listo, Carla! Te reservé una mesa en la terraza de Palermo para 4 personas, hoy sábado a las 22:00. Código R-4821. Como son 4, no hace falta seña. El perro es bienvenido en la terraza. ¡Los esperamos!";
const RESUMEN_COMPACTO = "Resumen de lo anterior: Palermo tiene mesa en terraza para 4 a las 22:00 (PAL-TER-04, admite mascotas). Carla es clienta frecuente (7 visitas).";

// ---------- los pasos de la trayectoria ----------
// Cada paso: el modelo razona, llama una tool (o responde) y el resultado se agrega al historial.
const PASOS = [
  { tool: "buscar_disponibilidad", args: { sucursal: "Palermo", fecha: "2026-11-14", hora: "22:00", personas: 4, sector: "terraza" }, razona: 95, latencia: 1.1 },
  { tool: "consultar_cliente", args: { telefono: "+5491155554821" }, razona: 70, latencia: 0.55 },
  { tool: "politica_senas", args: { personas: 4, dia_semana: "sábado", sucursal: "Palermo" }, razona: 60, latencia: 0.25 },
  { tool: "reservar", args: { sucursal: "Palermo", fecha: "2026-11-14", hora: "22:00", personas: 4, mesa: "PAL-TER-04", telefono: "+5491155554821", sena: 0, observaciones: "perro chico" }, razona: 55, latencia: 0.9 },
  { respuesta: true, razona: 40 },
];
const RESULTADOS = { buscar_disponibilidad: o => o.recorte ? disponibilidadRecortada() : disponibilidadCompleta(), consultar_cliente: () => CLIENTE, politica_senas: () => POLITICA, reservar: () => RESERVA };

/**
 * Arma la trayectoria completa con las opciones elegidas y devuelve, por paso, los bloques del prompt con sus tokens,
 * qué parte del prefijo estaba en caché, la salida y el costo.
 * o = { perfil, recorte, fecha: "arriba"|"abajo", tibia, compactar, paralelo, corta }
 */
function trayectoria(o) {
  const P = o.perfil;
  // Con tools en paralelo, consultar_cliente y politica_senas se piden en el mismo paso.
  let pasos = PASOS.map(p => ({ ...p }));
  if (o.paralelo) pasos = [pasos[0], { tools: [pasos[1], pasos[2]], razona: 80, latencia: Math.max(pasos[1].latencia, pasos[2].latencia) }, pasos[3], pasos[4]];
  const t = { sistema: tokens(SISTEMA), tools: tokens(TOOLS_TXT), previo: tokens(JSON.stringify(HISTORIAL_PREVIO)), consulta: tokens(CONSULTA), fecha: tokens(FECHA) };
  const historial = [];          // mensajes que se agregan paso a paso: tool calls y resultados
  const salida = [];
  let prevPrompt = null;         // la secuencia de bloques del paso anterior, para calcular el prefijo común
  let compactado = false;

  for (let i = 0; i < pasos.length; i++) {
    const p = pasos[i];
    if (o.compactar && i === pasos.length - 2 && !compactado) {
      // Se reemplazan los resultados de tools viejos por un resumen: menos tokens, pero cambia el prefijo.
      const viejos = historial.filter(h => h.tipo === "resultado");
      for (const h of viejos) { h.tokens = 0; h.compactado = true; }
      historial.push({ tipo: "resumen", nombre: "resumen", tokens: tokens(RESUMEN_COMPACTO), texto: RESUMEN_COMPACTO });
      compactado = true;
    }
    // El prompt de este paso, en orden, como bloques con identidad (para saber hasta dónde coincide con el anterior).
    const bloques = [];
    if (o.fecha === "arriba") bloques.push({ k: "fecha", cat: "consulta", tokens: t.fecha, id: `fecha-${i}` });   // cambia cada segundo
    bloques.push({ k: "sistema", cat: "sistema", tokens: t.sistema, id: "sistema" }, { k: "tools", cat: "tools", tokens: t.tools, id: "tools" },
                 { k: "previo", cat: "historial", tokens: t.previo, id: "previo" }, { k: "consulta", cat: "consulta", tokens: t.consulta, id: "consulta" });
    for (const h of historial) if (h.tokens) bloques.push({ k: h.tipo, cat: h.tipo === "resultado" ? "resultados" : "historial", tokens: h.tokens, id: h.id ?? (h.id = `${h.tipo}-${h.nombre}-${historial.indexOf(h)}${h.compactado ? "c" : ""}`), nombre: h.nombre });
    if (o.fecha === "abajo") bloques.push({ k: "fecha", cat: "consulta", tokens: t.fecha, id: `fecha-${i}` });

    // Prefijo en caché: los bloques iguales desde el principio respecto del paso anterior (o de otra conversación, si la caché está tibia).
    let enCache = 0;
    const ref = prevPrompt ?? (o.tibia ? [{ id: "sistema" }, { id: "tools" }] : []);
    for (let j = 0; j < bloques.length && j < ref.length; j++) { if (bloques[j].id !== ref[j].id) break; enCache += bloques[j].tokens; }
    // Los proveedores cachean en bloques: mínimo 1.024 tokens y de a 128.
    if (P.id === "api") enCache = enCache < 1024 ? 0 : Math.floor(enCache / 128) * 128;
    const entrada = bloques.reduce((a, b) => a + b.tokens, 0);
    const porCat = { sistema: 0, tools: 0, historial: 0, resultados: 0, consulta: 0 };
    for (const b of bloques) porCat[b.cat] += b.tokens;

    // Salida: razonamiento, tool calls o la respuesta final.
    const razona = Math.round(p.razona * (o.corta ? 0.45 : 1));
    const llamadas = p.tools ?? (p.tool ? [p] : []);
    const toolCall = llamadas.reduce((a, l) => a + tokens(JSON.stringify({ name: l.tool, arguments: l.args })) + 8, 0);
    const respuesta = p.respuesta ? (o.corta ? tokens(RESPUESTA.split(". ").slice(0, 2).join(". ")) : tokens(RESPUESTA)) : 0;
    const out = razona + toolCall + respuesta;

    const costoEntrada = ((entrada - enCache) * P.entrada + enCache * P.cache) / 1e6;
    const costoSalida = out * P.salida / 1e6;
    salida.push({ n: i + 1, bloques, entrada, enCache, porCat, razona, toolCall, respuesta, out, costoEntrada, costoSalida, costo: costoEntrada + costoSalida,
                  llamadas: llamadas.map(l => l.tool), latenciaTool: p.latencia ?? 0, compactaAqui: !!o.compactar && i === pasos.length - 2 });
    prevPrompt = bloques;

    // Lo que este paso agrega al historial para el siguiente.
    for (const l of llamadas) {
      historial.push({ tipo: "llamada", nombre: l.tool, tokens: tokens(JSON.stringify({ name: l.tool, arguments: l.args })) + 8 });
      const r = RESULTADOS[l.tool](o);
      historial.push({ tipo: "resultado", nombre: l.tool, tokens: tokens(r), texto: r });
    }
  }
  const total = { entrada: 0, enCache: 0, out: 0, costo: 0, costoEntrada: 0, costoSalida: 0 };
  for (const s of salida) { total.entrada += s.entrada; total.enCache += s.enCache; total.out += s.out; total.costo += s.costo; total.costoEntrada += s.costoEntrada; total.costoSalida += s.costoSalida; }
  return { pasos: salida, total, t };
}

// Cuánto cuesta cada tool en la trayectoria: su resultado se relee en todos los pasos que siguen.
function costoPorTool(tr, perfil) {
  const filas = {};
  tr.pasos.forEach((s, i) => {
    for (const b of s.bloques.filter(b => b.cat === "resultados")) {
      const f = filas[b.nombre] ??= { nombre: b.nombre, tokens: b.tokens, relecturas: 0, tokensLeidos: 0 };
      f.relecturas++; f.tokensLeidos += b.tokens;
    }
  });
  const tot = Object.values(filas).reduce((a, f) => a + f.tokensLeidos, 0);
  return Object.values(filas).map(f => ({ ...f, costo: f.tokensLeidos * perfil.entrada / 1e6, parte: f.tokensLeidos / Math.max(1, tr.total.entrada) })).sort((a, b) => b.tokensLeidos - a.tokensLeidos);
}

/**
 * Tiempos de un pedido: TTFT, generación y tools en cada paso. `semilla` cambia el ruido.
 * Devuelve segmentos para el Gantt y totales por componente.
 */
function tiempos(tr, o, semilla) {
  const P = o.perfil, r = rng(semilla);
  let t = 0; const filas = [];
  let ttftTotal = 0, gen = 0, tools = 0, ultimoTtft = 0, ultimaGen = 0;
  for (const s of tr.pasos) {
    let ttft = P.ttftBase + (s.entrada - s.enCache) / P.prefill + s.enCache / P.prefillCache;
    ttft *= Math.exp(0.18 * normal(r));
    if (r() < P.cola) ttft += 1.2 + r() * 2.8;            // cola del proveedor en hora pico
    const g = s.out / (P.gen * Math.exp(0.08 * normal(r)));
    let tool = 0;
    if (s.latenciaTool) {
      tool = lognormal(r, s.latenciaTool, 0.3);
      if (s.llamadas.includes("buscar_disponibilidad") && r() < 0.05) tool += 2 + r() * 3;   // la base de reservas a veces tarda
    }
    filas.push({ n: s.n, ini: t, ttft, gen: g, tool, llamadas: s.llamadas });
    t += ttft + g + tool; ttftTotal += ttft; gen += g; tools += tool; ultimoTtft = ttft; ultimaGen = g;
  }
  // Con streaming, el cliente empieza a leer cuando llega el primer token de la respuesta final.
  const percibido = o.streaming ? t - ultimaGen : t;
  return { filas, total: t, ttft: ttftTotal, gen, modelo: ttftTotal + gen, tools, percibido, primerTokenFinal: t - ultimaGen };
}

const percentil = (xs, p) => { const a = [...xs].sort((x, y) => x - y); const i = (a.length - 1) * p; const lo = Math.floor(i); return a[lo] + (a[Math.ceil(i)] - a[lo]) * (i - lo); };
const promedio = xs => xs.reduce((a, b) => a + b, 0) / Math.max(1, xs.length);
