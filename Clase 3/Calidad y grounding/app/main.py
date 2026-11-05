"""MesaYa · Laboratorio de calidad (Unidad 3: calidad, grounding y criterios de uso)."""
from __future__ import annotations

import asyncio
import json
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

import busqueda
import generacion as gen
import ruta
from escenarios import ESCENARIOS
from ollama import SinGrabacion, ollama

BASE = Path(__file__).resolve().parent
PREGUNTAS = json.loads((BASE.parent / "evaluacion" / "preguntas.json").read_text(encoding="utf-8")) \
    if (BASE.parent / "evaluacion").exists() else json.loads(Path("/app/evaluacion/preguntas.json").read_text(encoding="utf-8"))
POR_ID = {q["id"]: q for q in PREGUNTAS["preguntas"]}

app = FastAPI(title="MesaYa · Laboratorio de calidad")
app.mount("/static", StaticFiles(directory=BASE / "static"), name="static")
estado_indice = {"listo": False, "error": None}


@app.on_event("startup")
async def iniciar():
    async def preparar():
        await ollama.preparar()
        try:
            for ix in busqueda.INDICES.values():
                await ix.cargar_vectores()
            estado_indice["listo"] = True
        except Exception as e:
            estado_indice["error"] = f"No se pudo indexar el corpus: {e}"
    asyncio.create_task(preparar())


@app.get("/")
def inicio():
    return FileResponse(BASE / "static" / "index.html")


@app.get("/api/estado")
async def estado():
    if ollama.conectado:
        try:
            await ollama.listar()
        except Exception:
            ollama.conectado = False
    return {**ollama.estado(), "indice": estado_indice}


class CambioModelo(BaseModel):
    modelo: str | None = None
    modo_guion: bool | None = None


@app.post("/api/config")
def config(c: CambioModelo):
    if c.modelo:
        ollama.modelo = c.modelo
    if c.modo_guion is not None:
        ollama.modo_guion = c.modo_guion
    return ollama.estado()


@app.get("/api/catalogo")
def catalogo():
    return {"escenarios": ESCENARIOS, "preguntas": PREGUNTAS["preguntas"]}


CONFIG_BASE = {
    "perfil": "clientes", "tenant": "mesaya-ar", "chunking": "seccion", "busqueda": "hibrida",
    "k": 4, "umbral": 0.0, "solo_vigentes": False, "priorizar_oficiales": False, "rerank": False,
    "grounding": "estricto", "verificar": True, "enrutar": False,
}


class Turno(BaseModel):
    pregunta: str
    config: dict = {}
    pregunta_id: str | None = None
    historial: list[dict] = []


def metricas(candidatos: list[dict], k: int, esperados: list[dict], total_rel: int, prohibidos: list[str]) -> dict:
    topk = [f for f in candidatos if f["en_contexto"]]
    rel_k = sum(1 for f in topk if f["relevante"])
    primero = next((f["pos"] for f in candidatos if f["relevante"]), None)
    return {
        "k": k, "en_contexto": len(topk), "relevantes_en_k": rel_k, "relevantes_total": total_rel,
        "precision": round(rel_k / len(topk), 2) if topk else None,
        "recall": round(rel_k / total_rel, 2) if total_rel else None,
        "hit": rel_k > 0, "rr": round(1 / primero, 2) if primero and primero <= k else 0,
        "primer_relevante": primero,
        "violaciones": [f["document_id"] for f in topk if f["document_id"] in prohibidos],
    }


def diagnosticar(q: dict | None, rec: dict | None, verif: dict | None, abstuvo: bool, modo: str) -> list[dict]:
    """Árbol de diagnóstico de la slide 05: ¿dónde se rompió?"""
    nodos = []
    if not q:
        nodos.append({"id": "corpus", "pregunta": "¿La respuesta está en el corpus?", "estado": "desconocido",
                      "detalle": "Sin pregunta de evaluación no hay forma de saber qué fragmento era el correcto. Por eso hace falta un set de evaluación."})
        if verif and verif.get("fidelidad") is not None:
            ok = verif["fidelidad"] >= 0.99
            nodos.append({"id": "fiel", "pregunta": "¿La respuesta es fiel a la evidencia?", "estado": "ok" if ok else "falla",
                          "detalle": f"Fidelidad {round(verif['fidelidad'] * 100)} %."})
        return nodos
    esperados, tipo = q.get("esperados", []), q.get("tipo")
    if tipo in ("sin_acceso", "fuera_corpus"):
        nodos.append({"id": "corpus", "pregunta": "¿La respuesta está en el corpus?", "estado": "ok",
                      "detalle": "No, para este perfil. Lo correcto es decir «no sé»."})
        fuga = rec and rec["metricas"]["violaciones"]
        if fuga:
            nodos.append({"id": "permisos", "pregunta": "¿Entró algo que no debía?", "estado": "falla",
                          "detalle": f"Entró {', '.join(fuga)}.", "arreglo": "Filtrar por audiencia o vigencia antes de buscar."})
        nodos.append({"id": "abstencion", "pregunta": "¿El asistente dijo que no sabe?", "estado": "ok" if abstuvo else "falla",
                      "detalle": "Se abstuvo." if abstuvo else "Respondió igual: inventó o usó algo que no correspondía.",
                      "arreglo": None if abstuvo else "Grounding estricto con «no sé» explícito y un umbral de relevancia."})
        return nodos
    if tipo == "ruta":
        return nodos
    nodos.append({"id": "corpus", "pregunta": "¿La respuesta está en el corpus?", "estado": "ok",
                  "detalle": "Sí: " + ", ".join(e["document_id"] + (f" › {e['seccion']}" if e.get("seccion") else "") for e in esperados)})
    if modo == "sin":
        nodos.append({"id": "contexto", "pregunta": "¿Le dimos la evidencia al modelo?", "estado": "falla",
                      "detalle": "No: respondió sin documentos.", "arreglo": "Usar RAG: recuperar y pasarle los fragmentos."})
    else:
        m = rec["metricas"]
        p = m["primer_relevante"]
        nodos.append({"id": "recuperado", "pregunta": "¿Se recuperó entre los candidatos?", "estado": "ok" if p else "falla",
                      "detalle": f"Primer fragmento correcto en la posición {p}." if p else "No aparece entre los 20 candidatos.",
                      "arreglo": None if p else "Problema de recuperación: probar otro modo de búsqueda o chunking por sección."})
        if p:
            nodos.append({"id": "contexto", "pregunta": "¿Entró al contexto?", "estado": "ok" if m["hit"] else "falla",
                          "detalle": f"Entró: {m['relevantes_en_k']} de {m['relevantes_total']} fragmentos relevantes." if m["hit"]
                          else f"Quedó en la posición {p}, fuera de k = {m['k']} o bajo el umbral.",
                          "arreglo": None if m["hit"] else "Subir k, activar reranking o bajar el umbral."})
        if m["violaciones"]:
            nodos.append({"id": "permisos", "pregunta": "¿Entró algo que no debía?", "estado": "falla",
                          "detalle": f"Entró {', '.join(m['violaciones'])}.", "arreglo": "Filtrar por vigencia o país antes de buscar."})
    if verif and verif.get("fidelidad") is not None:
        ok = verif["fidelidad"] >= 0.99
        nodos.append({"id": "fiel", "pregunta": "¿La respuesta es fiel a la evidencia?", "estado": "ok" if ok else "falla",
                      "detalle": f"Fidelidad {round(verif['fidelidad'] * 100)} %.",
                      "arreglo": None if ok else "Problema de generación: grounding estricto y citas por oración."})
    elif abstuvo:
        tenia = modo != "sin" and rec["metricas"].get("hit")
        nodos.append({"id": "fiel", "pregunta": "¿Usó la evidencia que tenía?", "estado": "falla",
                      "detalle": "Dijo «no sé» con el fragmento correcto en el contexto: falló la generación, no la búsqueda." if tenia
                      else "Dijo «no sé» porque el fragmento correcto no le llegó.",
                      "arreglo": "Revisar el prompt (demasiado estricto) o probar un modelo más capaz." if tenia
                      else "Problema de recuperación: arreglarlo antes de tocar el prompt."})
    if verif and verif.get("correcta") in ("si", "parcial", "no"):
        c = verif["correcta"]
        nodos.append({"id": "correcta", "pregunta": "¿Responde lo que se preguntó?", "estado": "ok" if c == "si" else ("parcial" if c == "parcial" else "falla"),
                      "detalle": verif.get("motivo_correcta") or f"Clave: {q.get('respuesta_clave')}"})
    return nodos


async def correr_turno(t: Turno) -> dict:
    if not estado_indice["listo"] and not ollama.modo_guion:
        raise HTTPException(503, "El índice todavía se está preparando. Mirá el estado arriba a la derecha.")
    cfg = {**CONFIG_BASE, **t.config}
    q = POR_ID.get(t.pregunta_id) if t.pregunta_id else None
    if q and q["pregunta"] != t.pregunta:
        q = None
    t0 = time.perf_counter()
    salida: dict = {"pregunta": t.pregunta, "config": cfg, "pregunta_id": q["id"] if q else None,
                    "clave": q.get("respuesta_clave") if q else None, "etapas": []}

    destino = {"ruta": "documentos", "motivo": "Router apagado: todo va a RAG."}
    if cfg["enrutar"]:
        destino = await ruta.decidir(t.pregunta)
        salida["etapas"].append({"nombre": "Decidir ruta", "ms": destino.get("ms", 0)})
    salida["ruta"] = destino

    if destino["ruta"] != "documentos":
        return await turno_sin_rag(t, destino, salida, t0)

    # Con historial, la búsqueda usa también el último mensaje del usuario ("¿Y para los contratados?").
    previo = [h["texto"] for h in t.historial if h.get("rol") == "usuario"][-1:]
    rec = await busqueda.buscar(" ".join(previo + [t.pregunta]), cfg)
    esperados = q.get("esperados", []) if q else []
    for f in rec["candidatos"]:
        f["relevante"] = busqueda.es_relevante(f, esperados) if q else None
    if q:
        rec["metricas"] = metricas(rec["candidatos"], cfg["k"], esperados, busqueda.total_relevantes(cfg, esperados), q.get("prohibidos", []))
    else:
        rec["metricas"] = {"k": cfg["k"], "en_contexto": len(rec["contexto"]), "precision": None, "recall": None,
                           "violaciones": [], "primer_relevante": None}
    salida["recuperacion"] = rec
    salida["etapas"].append({"nombre": "Recuperar" + (" + rerank" if cfg["rerank"] else ""),
                             "ms": round((time.perf_counter() - t0) * 1000) - sum(e["ms"] for e in salida["etapas"])})

    contexto = rec["contexto"] if cfg["grounding"] != "sin" else []
    if cfg["grounding"] == "estricto" and not contexto:
        # Evidencia insuficiente: ni siquiera se llama al modelo (slide 13).
        salida["respuesta"] = {"texto": gen.NO_SE, "tokens_prompt": 0, "tokens_respuesta": 0, "ms": 0,
                               "corte": "Ningún fragmento superó el umbral: se responde «no sé» sin llamar al modelo."}
        mensajes = []
    else:
        mensajes = gen.armar_mensajes(t.pregunta, contexto, cfg["grounding"], t.historial)
        salida["respuesta"] = await gen.ollama.chat(mensajes)
        salida["etapas"].append({"nombre": "Generar", "ms": salida["respuesta"]["ms"]})
    salida["mensajes"] = mensajes
    texto = salida["respuesta"]["texto"]
    abstuvo = gen.es_abstencion(texto)
    salida["abstencion"] = abstuvo

    verif = None
    if cfg["verificar"]:
        evidencia = rec["contexto"]  # en modo "sin" se verifica contra lo que el modelo NO vio
        verif = await gen.verificar(t.pregunta, texto, evidencia, q.get("respuesta_clave") if q else None)
        if cfg["grounding"] == "estricto":  # citas que no existen en el contexto
            for o in verif["oraciones"]:
                o["citas_invalidas"] = [c for c in o.get("citas", []) if c < 1 or c > len(evidencia)]
        salida["etapas"].append({"nombre": "Verificar", "ms": verif.get("ms", 0)})
    salida["verificacion"] = verif
    salida["diagnostico"] = diagnosticar(q, rec, verif, abstuvo, cfg["grounding"])
    salida["ms_total"] = round((time.perf_counter() - t0) * 1000)
    salida["grabado"] = salida["respuesta"].get("grabado")
    return salida


async def turno_sin_rag(t: Turno, destino: dict, salida: dict, t0: float) -> dict:
    r = destino["ruta"]
    if r == "datos":
        sql, rs = await ruta.generar_sql(t.pregunta)
        resultado = ruta.ejecutar_sql(sql)
        salida["herramienta"] = {"tipo": "SQL", "pedido": sql, "resultado": resultado}
        salida["etapas"].append({"nombre": "Generar SQL", "ms": rs["ms"]})
        dato = json.dumps(resultado, ensure_ascii=False)
        sistema = "Sos el asistente de MesaYa. Respondé en 1 o 2 oraciones usando solo el resultado de la consulta SQL. Si hay un error, decí que no pudiste obtener el dato."
        usuario = f"Pregunta: {t.pregunta}\nSQL ejecutado: {sql}\nResultado: {dato}"
    elif r == "en_vivo":
        suc = destino.get("sucursal") or "Palermo"
        for s in ruta.SUCURSALES:
            if busqueda._plano(s) == busqueda._plano(suc):
                suc = s
        fecha = destino.get("fecha") or ruta.hoy().isoformat()
        personas = destino.get("personas") or 2
        resultado = ruta.disponibilidad(suc, fecha, personas)
        salida["herramienta"] = {"tipo": "API", "pedido": f"GET /disponibilidad?sucursal={suc}&fecha={fecha}&personas={personas}", "resultado": resultado}
        sistema = "Sos el asistente de MesaYa. Respondé en 1 o 2 oraciones usando solo la respuesta de la API de disponibilidad. Si el horario pedido no está disponible, ofrecé el más cercano que sí."
        usuario = f"Pregunta: {t.pregunta}\nRespuesta de la API: {json.dumps(resultado, ensure_ascii=False)}"
    else:
        salida["herramienta"] = {"tipo": "Prompt", "pedido": "Ficha fija en el prompt", "resultado": ruta.FICHA}
        sistema = f"Sos el asistente de MesaYa. Respondé en 1 o 2 oraciones con esta ficha:\n{ruta.FICHA}"
        usuario = t.pregunta
    mensajes = [{"role": "system", "content": sistema}, {"role": "user", "content": usuario}]
    salida["mensajes"] = mensajes
    salida["respuesta"] = await ollama.chat(mensajes)
    salida["etapas"].append({"nombre": "Generar", "ms": salida["respuesta"]["ms"]})
    salida["abstencion"] = False
    salida["verificacion"] = None
    q = POR_ID.get(t.pregunta_id) if t.pregunta_id else None
    salida["diagnostico"] = [{"id": "ruta", "pregunta": "¿Fue a la fuente correcta?",
                              "estado": ("ok" if not q or q.get("ruta_esperada") in (None, r) else "falla"),
                              "detalle": f"Ruta: {r}. {destino.get('motivo', '')}"}]
    salida["ms_total"] = round((time.perf_counter() - t0) * 1000)
    salida["grabado"] = salida["respuesta"].get("grabado")
    return salida


@app.post("/api/turno")
async def turno(t: Turno):
    try:
        return await correr_turno(t)
    except SinGrabacion as e:
        raise HTTPException(404, str(e))
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(502, f"{type(e).__name__}: {e}")


# ---------- evaluación (slide 15) ----------
CONFIGS_EVAL = [
    {"nombre": "Semántica · fijo", "chunking": "fijo", "busqueda": "semantica"},
    {"nombre": "Semántica · sección", "chunking": "seccion", "busqueda": "semantica"},
    {"nombre": "Palabras · sección", "chunking": "seccion", "busqueda": "palabras"},
    {"nombre": "Híbrida · sección", "chunking": "seccion", "busqueda": "hibrida"},
    {"nombre": "Híbrida + vigentes", "chunking": "seccion", "busqueda": "hibrida", "solo_vigentes": True},
    {"nombre": "Híbrida + vigentes + jerarquía", "chunking": "seccion", "busqueda": "hibrida", "solo_vigentes": True, "priorizar_oficiales": True},
]


class PedidoEval(BaseModel):
    k: int = 4


@app.post("/api/evaluar/recuperacion")
async def evaluar_recuperacion(p: PedidoEval):
    filas = []
    preguntas = [q for q in PREGUNTAS["preguntas"] if q["tipo"] in ("recuperacion", "sin_acceso")]
    for c in CONFIGS_EVAL:
        detalle, hits, rrs, precs, recs, viol = [], [], [], [], [], 0
        for q in preguntas:
            cfg = {**CONFIG_BASE, "k": p.k, **{k: v for k, v in c.items() if k != "nombre"},
                   "perfil": q["perfil"], "tenant": q.get("tenant_id", "mesaya-ar"), "rerank": False}
            consulta = q["pregunta"] if not q.get("historial") else " ".join(h["texto"] for h in q["historial"] if h["rol"] == "usuario") + " " + q["pregunta"]
            rec = await busqueda.buscar(consulta, cfg)
            for f in rec["candidatos"]:
                f["relevante"] = busqueda.es_relevante(f, q["esperados"])
            m = metricas(rec["candidatos"], p.k, q["esperados"], busqueda.total_relevantes(cfg, q["esperados"]), q.get("prohibidos", []))
            viol += bool(m["violaciones"])
            if q["tipo"] == "recuperacion":
                hits.append(m["hit"]); rrs.append(m["rr"]); precs.append(m["precision"] or 0); recs.append(m["recall"] or 0)
            detalle.append({"id": q["id"], "hit": m["hit"] if q["tipo"] == "recuperacion" else None,
                            "pos": m["primer_relevante"], "violacion": bool(m["violaciones"])})
        n = len(hits) or 1
        filas.append({"nombre": c["nombre"], "hit": round(sum(hits) / n, 2), "mrr": round(sum(rrs) / n, 2),
                      "precision": round(sum(precs) / n, 2), "recall": round(sum(recs) / n, 2),
                      "violaciones": viol, "detalle": detalle})
    return {"k": p.k, "filas": filas, "preguntas": [{"id": q["id"], "pregunta": q["pregunta"], "tipo": q["tipo"]} for q in preguntas]}


class PedidoEvalResp(BaseModel):
    config: dict = {}


@app.post("/api/evaluar/respuestas")
async def evaluar_respuestas(p: PedidoEvalResp):
    """Corre el set completo con la configuración actual. Devuelve una línea JSON por pregunta."""
    async def flujo():
        preguntas = [q for q in PREGUNTAS["preguntas"] if q["tipo"] != "ruta"]
        for i, q in enumerate(preguntas, 1):
            cfg = {**p.config, "perfil": q["perfil"], "tenant": q.get("tenant_id", "mesaya-ar"), "verificar": True, "enrutar": False}
            try:
                r = await correr_turno(Turno(pregunta=q["pregunta"], config=cfg, pregunta_id=q["id"], historial=q.get("historial", [])))
                v = r.get("verificacion") or {}
                fila = {"i": i, "n": len(preguntas), "id": q["id"], "tipo": q["tipo"], "pregunta": q["pregunta"],
                        "respuesta": r["respuesta"]["texto"], "abstencion": r["abstencion"],
                        "fidelidad": v.get("fidelidad"), "correcta": v.get("correcta"),
                        "abstencion_correcta": (r["abstencion"] if q["tipo"] in ("sin_acceso", "fuera_corpus") else None),
                        "hit": (r.get("recuperacion") or {}).get("metricas", {}).get("hit")}
            except Exception as e:
                fila = {"i": i, "n": len(preguntas), "id": q["id"], "error": str(e)}
            yield json.dumps(fila, ensure_ascii=False) + "\n"
    return StreamingResponse(flujo(), media_type="application/x-ndjson")
