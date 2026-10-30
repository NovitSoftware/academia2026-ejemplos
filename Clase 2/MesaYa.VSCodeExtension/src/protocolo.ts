// Mensajes entre la extensión (Node) y el webview del chat (navegador).

export type EstadoPaso = "encurso" | "ok" | "error";

/** Una tarjeta del chat: una decisión del modelo o una ejecución de tool por MCP. */
export interface Paso {
  id: string;
  tipo: "llm" | "tool";
  iteracion: number;
  titulo: string;
  sub?: string;
  entrada?: string;
  salida?: string;
  estado: EstadoPaso;
  inicio: number;
  duracionMs?: number;
  progreso?: string;
  /** Para tools que fallaron: el código de error que devolvió el servidor (SIN_DISPONIBILIDAD, FORBIDDEN…). */
  codigoError?: string;
}

export interface MensajeChat {
  id: string;
  rol: "user" | "assistant" | "error";
  texto: string;
}

/** Esquema de un campo pedido por el servidor en elicitation/create. */
export interface CampoElicitacion {
  type?: string;
  title?: string;
  description?: string;
  enum?: string[];
  oneOf?: { const: string; title?: string }[];
  default?: unknown;
  minimum?: number;
  maximum?: number;
  maxLength?: number;
}

export interface PedidoElicitacion {
  id: string;
  mensaje: string;
  schema?: { properties?: Record<string, CampoElicitacion>; required?: string[] };
}

export interface EstadoConexion {
  conectado: boolean;
  conectando: boolean;
  mcpUrl: string;
  servidor?: string;
  sesion?: string;
  protocolo?: string;
  tools: { nombre: string; descripcion?: string }[];
  error?: string;
  modelo: string;
  modelos: { nombre: string; tools: boolean; tamano?: string }[];
  errorOllama?: string;
  ocupado: boolean;
  /** Si es false, el chat oculta las tarjetas del loop (modelo y tools/call). */
  mostrarTools: boolean;
}

/** Consumo de tokens de la conversación actual. */
export interface UsoConversacion {
  entrada: number;
  salida: number;
  llamadas: number;
  /** Tokens por segundo de la última respuesta del modelo. */
  ultimoTps?: number;
  /** Promedio de tokens por segundo de toda la conversación (salida / tiempo generando). */
  promedioTps?: number;
}

export type HaciaWebview =
  | { tipo: "estado"; estado: EstadoConexion }
  | { tipo: "mensaje"; mensaje: MensajeChat }
  | { tipo: "paso"; paso: Paso }
  | { tipo: "elicitacion"; pedido: PedidoElicitacion }
  | { tipo: "elicitacionCerrada"; id: string; accion: string }
  | { tipo: "uso"; uso: UsoConversacion }
  | { tipo: "limpiar" };

export type DesdeWebview =
  | { tipo: "listo" }
  | { tipo: "enviar"; texto: string }
  | { tipo: "detener" }
  | { tipo: "nuevaConversacion" }
  | { tipo: "elegirModelo"; nombre: string }
  | { tipo: "responderElicitacion"; id: string; accion: "accept" | "decline" | "cancel"; contenido?: Record<string, unknown> }
  | { tipo: "abrirEnEditor"; contenido: string; lenguaje: string }
  | { tipo: "guardar"; contenido: string; nombre: string }
  | { tipo: "abrirSecuencia" }
  | { tipo: "reconectar" };
