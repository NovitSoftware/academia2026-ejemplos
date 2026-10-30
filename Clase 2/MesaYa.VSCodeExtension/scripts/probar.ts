// Prueba el núcleo de la extensión sin VS Code: cliente MCP + Ollama + loop agéntico.
// Uso: npm run probar -- "Quiero una mesa para 4 el viernes a las 22:15 en Palermo, cel +5491155550123"
// Acepta automáticamente las elicitations (con una nota) e imprime cada paso.
import { Agente } from "../src/agente";
import { ClienteMcp } from "../src/mcp";
import { Ollama } from "../src/ollama";

const mcpUrl = process.env.MCP_URL ?? "http://localhost:5100/mcp";
const ollama = new Ollama(process.env.OLLAMA_URL ?? "http://localhost:11434");
const pedidos = process.argv.slice(2);
if (pedidos.length === 0) pedidos.push("¿Qué horarios hay mañana en Palermo y Belgrano?");

const mcp = new ClienteMcp(mcpUrl, {
  elicitar: async pedido => {
    console.log(`\n🙋 elicitation/create: ${pedido.mensaje}\n   campos: ${Object.keys(pedido.schema?.properties ?? {}).join(", ") || "—"} → accept`);
    return { action: "accept", content: { nota: "Prueba desde el script" } };
  },
  log: (nivel, texto) => console.log(`   📜 [${nivel}] ${texto}`)
});

await mcp.conectar();
console.log(`Conectado a ${mcp.servidor?.title} · protocolo ${mcp.protocolo} · sesión ${mcp.sesion}`);
console.log(`Tools: ${mcp.tools.map(t => t.name).join(", ")}`);

const modelos = await ollama.modelos();
const modelo = modelos.find(m => m.nombre === (process.env.OLLAMA_MODEL ?? "qwen3.5:9b")) ?? modelos.find(m => m.tools)!;
console.log(`Modelo: ${modelo.nombre}`);

const vistos = new Set<string>();
const agente = new Agente(mcp, ollama, {
  uso: u => console.log(`   🔢 ${u.entrada + u.salida} tokens (${u.entrada} entrada · ${u.salida} salida) · ⚡ ${u.ultimoTps?.toFixed(1)} tok/s (prom. ${u.promedioTps?.toFixed(1)})`),
  mensaje: m => console.log(`\n${m.rol === "user" ? "👤" : m.rol === "assistant" ? "🤖" : "⚠️"} ${m.texto}`),
  paso: p => {
    if (p.estado === "encurso") {
      if (p.progreso) console.log(`   ⏳ ${p.titulo}: ${p.progreso}`);
      else if (!vistos.has(p.id)) { vistos.add(p.id); console.log(`${p.tipo === "llm" ? "🧠" : "🛠"} ${p.titulo} ${p.tipo === "tool" ? p.entrada?.replace(/\s+/g, " ") : ""}`); }
    } else console.log(`   ${p.estado === "ok" ? "✓" : "✗"} ${p.titulo} · ${p.sub} · ${p.duracionMs} ms`);
  }
}, modelo);

for (const pedido of pedidos) await agente.enviar(pedido);
await mcp.cerrar();
