"""Recuperación en dos etapas (slide 03).

Etapa 1: semántica (bge-m3), palabras clave (BM25) o híbrida (RRF).
Filtros: audiencia del perfil, país, vigencia y jerarquía de fuentes.
Etapa 2 opcional: reranking con el LLM sobre los mejores candidatos.
Umbral: los fragmentos con similitud menor no entran al contexto (slide 04).
"""
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import unicodedata
from collections import Counter
from pathlib import Path

import numpy as np

from corpus import CHUNKS, Chunk
from ollama import ollama

CACHE = Path(os.environ.get("CACHE", "/app/datos"))
CACHE.mkdir(parents=True, exist_ok=True)

STOP = set("""a al algo como con de del el en es esta este hay la las lo los me mi mis o para pero por que qué se si sí sin
su sus te tu un una uno y ya le les nos cuando cuándo donde dónde cual cuál cuanto cuánto cuántas cuántos puedo
hago hace ser son fue era tiene tienen tengo quiero""".split())

ORDEN_AUTORIDAD = {"oficial": 0, "comunicacion": 1, "historica": 2}


def _plano(t: str) -> str:
    t = unicodedata.normalize("NFD", t.lower())
    return "".join(c for c in t if unicodedata.category(c) != "Mn")


def tokenizar(t: str) -> list[str]:
    return [w for w in re.findall(r"[a-z0-9]+(?:-[a-z0-9]+)*", _plano(t)) if w not in STOP and len(w) > 1]


class Indice:
    def __init__(self, estrategia: str):
        self.estrategia = estrategia
        self.chunks: list[Chunk] = CHUNKS[estrategia]
        self.vectores: np.ndarray | None = None
        toks = [tokenizar(c.texto) for c in self.chunks]
        self.tf = [Counter(t) for t in toks]
        self.largos = np.array([len(t) for t in toks], dtype=float)
        self.prom = float(self.largos.mean())
        df = Counter(w for t in toks for w in set(t))
        n = len(toks)
        self.idf = {w: math.log(1 + (n - f + 0.5) / (f + 0.5)) for w, f in df.items()}

    async def cargar_vectores(self) -> None:
        firma = hashlib.sha256(("|".join(c.texto for c in self.chunks) + ollama.embeddings).encode()).hexdigest()[:16]
        f = CACHE / f"embeddings-{self.estrategia}-{firma}.npy"
        if f.exists():
            self.vectores = np.load(f)
            return
        vecs = []
        for i in range(0, len(self.chunks), 16):
            vecs += await ollama.embeber([c.texto for c in self.chunks[i:i + 16]])
        m = np.array(vecs, dtype=np.float32)
        m /= np.linalg.norm(m, axis=1, keepdims=True)
        np.save(f, m)
        self.vectores = m

    def bm25(self, consulta: str) -> np.ndarray:
        q = tokenizar(consulta)
        s = np.zeros(len(self.chunks))
        for i, tf in enumerate(self.tf):
            for w in q:
                if w in tf:
                    f = tf[w]
                    s[i] += self.idf[w] * f * 2.2 / (f + 1.2 * (0.25 + 0.75 * self.largos[i] / self.prom))
        return s


INDICES = {e: Indice(e) for e in CHUNKS}


def _rangos(puntajes: np.ndarray) -> np.ndarray:
    orden = np.argsort(-puntajes, kind="stable")
    r = np.empty(len(puntajes), dtype=int)
    r[orden] = np.arange(1, len(puntajes) + 1)
    return r


def motivo_exclusion(c: Chunk, cfg: dict) -> str | None:
    d = c.doc
    if cfg["perfil"] not in d.audiencia:
        return f"audiencia: solo {', '.join(d.audiencia)}"
    if d.tenant != cfg["tenant"]:
        return "otro país"
    if cfg["solo_vigentes"] and d.estado != "vigente":
        return f"no vigente (v{d.version}, {d.fecha[:4]})"
    return None


async def buscar(consulta: str, cfg: dict) -> dict:
    ix = INDICES[cfg["chunking"]]
    qv = np.array((await ollama.embeber([consulta]))[0], dtype=np.float32)
    qv /= np.linalg.norm(qv)
    sem = ix.vectores @ qv
    bm = ix.bm25(consulta)
    r_sem, r_bm = _rangos(sem), _rangos(bm)
    modo = cfg["busqueda"]
    if modo == "semantica":
        fusion = sem.copy()
    elif modo == "palabras":
        fusion = bm.copy()
    else:  # RRF con k=60: no importa la escala de cada puntaje, solo la posición
        fusion = 1 / (60 + r_sem) + 1 / (60 + np.where(bm > 0, r_bm, 10_000))
    orden = list(np.argsort(-fusion, kind="stable"))

    permitidos, excluidos = [], []
    for i in orden:
        c = ix.chunks[i]
        fila = {
            **c.ficha(),
            "sim": round(float(sem[i]), 3), "bm25": round(float(bm[i]), 2),
            "rank_sem": int(r_sem[i]), "rank_bm25": int(r_bm[i]) if bm[i] > 0 else None,
            "fusion": round(float(fusion[i]), 4),
        }
        motivo = motivo_exclusion(c, cfg)
        if motivo:
            if len(excluidos) < 6 and len(permitidos) < 12:
                excluidos.append({**fila, "motivo": motivo})
            continue
        permitidos.append(fila)

    candidatos = permitidos[:20]
    if cfg["priorizar_oficiales"]:
        candidatos.sort(key=lambda f: ORDEN_AUTORIDAD[f["autoridad"]])  # estable: respeta el orden previo
    for p, f in enumerate(candidatos, 1):
        f["pos_etapa1"] = p

    rerank_info = None
    if cfg["rerank"] and candidatos:
        rerank_info = await reranquear(consulta, candidatos[:12])
        puntos = rerank_info["puntajes"]
        cabeza = sorted(candidatos[:12], key=lambda f: (-puntos.get(f["id"], 0), f["pos_etapa1"]))
        for f in cabeza:
            f["rerank"] = puntos.get(f["id"], 0)
        candidatos = cabeza + candidatos[12:]
        if cfg["priorizar_oficiales"]:
            candidatos.sort(key=lambda f: ORDEN_AUTORIDAD[f["autoridad"]])

    k, umbral = cfg["k"], cfg["umbral"]
    contexto = []
    for p, f in enumerate(candidatos, 1):
        f["pos"] = p
        f["en_k"] = p <= k
        f["bajo_umbral"] = umbral > 0 and f["sim"] < umbral
        f["en_contexto"] = f["en_k"] and not f["bajo_umbral"]
        if f["en_contexto"]:
            contexto.append(f)
    return {"candidatos": candidatos, "excluidos": excluidos, "contexto": contexto,
            "rerank": rerank_info, "total_chunks": len(ix.chunks)}


ESQUEMA_RERANK = {
    "type": "object",
    "properties": {"puntajes": {"type": "array", "items": {
        "type": "object",
        "properties": {"n": {"type": "integer"}, "relevancia": {"type": "integer", "minimum": 0, "maximum": 3}},
        "required": ["n", "relevancia"]}}},
    "required": ["puntajes"],
}


async def reranquear(consulta: str, filas: list[dict]) -> dict:
    lista = "\n\n".join(f"[{n}] {f['texto'][:700]}" for n, f in enumerate(filas, 1))
    mensajes = [
        {"role": "system", "content": "Sos un evaluador de relevancia. Para cada fragmento, puntuá de 0 a 3 cuánto ayuda a "
                                      "responder la consulta: 3 = la responde directamente, 2 = aporta parte, 1 = mismo tema "
                                      "pero no responde, 0 = no tiene relación. Evaluá todos los fragmentos."},
        {"role": "user", "content": f"Consulta: {consulta}\n\nFragmentos:\n{lista}"},
    ]
    datos, r = await ollama.chat_json(mensajes, ESQUEMA_RERANK)
    puntajes = {}
    for p in datos.get("puntajes", []):
        if 1 <= p.get("n", 0) <= len(filas):
            puntajes[filas[p["n"] - 1]["id"]] = int(p.get("relevancia", 0))
    return {"puntajes": puntajes, "ms": r["ms"], "grabado": r.get("grabado")}


def es_relevante(fila: dict, esperados: list[dict]) -> bool:
    for e in esperados:
        if fila["document_id"] == e["document_id"] and (e.get("seccion") is None or e["seccion"] in fila["secciones"]):
            return True
    return False


def total_relevantes(cfg: dict, esperados: list[dict]) -> int:
    ix = INDICES[cfg["chunking"]]
    return sum(1 for c in ix.chunks if es_relevante(c.ficha(), esperados) and not motivo_exclusion(c, cfg))
