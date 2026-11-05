"""Memoria de largo plazo del agente (slides 03 a 06 y 12 a 14).

Cada recuerdo se guarda con sus metadatos: tipo, origen, fuente, confianza,
alcance, fechas y TTL. Recuperar combina similitud, importancia y recencia.
Olvidar es parte del diseño: vence el TTL, se reemplaza o se borra a pedido.
"""
from __future__ import annotations

import datetime as dt
import json
import math
import os
import sqlite3
from pathlib import Path

import numpy as np

from ollama import ollama

DATOS = Path(os.environ.get("CACHE", "/srv/datos"))
DATOS.mkdir(parents=True, exist_ok=True)
DB = sqlite3.connect(DATOS / "memoria.db", check_same_thread=False)
DB.row_factory = sqlite3.Row
DB.executescript("""
CREATE TABLE IF NOT EXISTS memorias (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  usuario TEXT, texto TEXT,
  tipo TEXT,          -- semantica | episodica | procedimental
  categoria TEXT,     -- preferencia | restriccion | dato | hecho | instruccion
  origen TEXT,        -- explicita | inferida
  fuente TEXT,        -- usuario | contenido_externo
  confianza REAL, importancia REAL, alcance TEXT,
  creada TEXT, actualizada TEXT, ultimo_uso TEXT, usos INTEGER DEFAULT 0,
  ttl_dias INTEGER,   -- NULL = sin vencimiento
  estado TEXT DEFAULT 'activa',   -- activa | reemplazada | olvidada | pendiente
  motivo_estado TEXT, reemplaza_id INTEGER,
  embedding TEXT
);""")

# Reloj simulado: "Avanzar 45 días" sirve para mostrar TTL y decaimiento en clase.
RELOJ = {"dias": 0}
SEMIVIDA_DIAS = 60


def ahora() -> dt.datetime:
    return dt.datetime.now().replace(microsecond=0) + dt.timedelta(days=RELOJ["dias"])


def _fecha(s: str | None) -> dt.datetime | None:
    return dt.datetime.fromisoformat(s) if s else None


def ficha(r: sqlite3.Row) -> dict:
    d = {k: r[k] for k in r.keys() if k != "embedding"}
    creada = _fecha(r["creada"])
    if r["ttl_dias"] and creada:
        vence = creada + dt.timedelta(days=r["ttl_dias"])
        d["vence"] = vence.date().isoformat()
        d["dias_para_vencer"] = (vence - ahora()).days
    uso = _fecha(r["ultimo_uso"]) or creada
    d["dias_sin_uso"] = max(0, (ahora() - uso).days) if uso else 0
    d["recencia"] = round(math.exp(-d["dias_sin_uso"] * math.log(2) / SEMIVIDA_DIAS), 2)
    return d


def listar(usuario: str) -> dict:
    vencer(usuario)
    filas = DB.execute("SELECT * FROM memorias WHERE usuario=? ORDER BY id", (usuario,)).fetchall()
    todas = [ficha(r) for r in filas]
    return {"activas": [m for m in todas if m["estado"] == "activa"],
            "pendientes": [m for m in todas if m["estado"] == "pendiente"],
            "historial": [m for m in todas if m["estado"] in ("reemplazada", "olvidada")]}


def vencer(usuario: str) -> list[dict]:
    """Olvido por TTL: lo que venció deja de existir para el agente."""
    vencidas = []
    for r in DB.execute("SELECT * FROM memorias WHERE usuario=? AND estado='activa' AND ttl_dias IS NOT NULL", (usuario,)).fetchall():
        if _fecha(r["creada"]) + dt.timedelta(days=r["ttl_dias"]) <= ahora():
            DB.execute("UPDATE memorias SET estado='olvidada', motivo_estado=? WHERE id=?",
                       (f"Venció el TTL de {r['ttl_dias']} días", r["id"]))
            vencidas.append(ficha(r))
    DB.commit()
    return vencidas


async def vector(texto: str) -> np.ndarray:
    v = np.array((await ollama.embeber([texto]))[0], dtype=np.float32)
    return v / np.linalg.norm(v)


async def recuperar(usuario: str, consulta: str, k: int) -> list[dict]:
    """Puntaje = 0,6 similitud + 0,25 importancia + 0,15 recencia. Entran las k mejores con similitud ≥ 0,35."""
    vencer(usuario)
    filas = DB.execute("SELECT * FROM memorias WHERE usuario=? AND estado='activa'", (usuario,)).fetchall()
    if not filas:
        return []
    q = await vector(consulta)
    salida = []
    for r in filas:
        f = ficha(r)
        sim = float(np.dot(q, np.array(json.loads(r["embedding"]), dtype=np.float32)))
        f["sim"] = round(sim, 3)
        f["puntaje"] = round(0.6 * sim + 0.25 * (r["importancia"] or 0.5) + 0.15 * f["recencia"], 3)
        salida.append(f)
    salida.sort(key=lambda f: -f["puntaje"])
    for i, f in enumerate(salida):
        f["entra"] = i < k and f["sim"] >= 0.35
    usadas = [f["id"] for f in salida if f["entra"]]
    if usadas:
        DB.execute(f"UPDATE memorias SET ultimo_uso=?, usos=usos+1 WHERE id IN ({','.join('?' * len(usadas))})",
                   (ahora().isoformat(), *usadas))
        DB.commit()
    return salida


async def guardar(usuario: str, c: dict, estado: str = "activa") -> int:
    emb = (await vector(c["texto"])).tolist()
    t = ahora().isoformat()
    cur = DB.execute(
        """INSERT INTO memorias (usuario, texto, tipo, categoria, origen, fuente, confianza, importancia, alcance,
           creada, actualizada, ultimo_uso, usos, ttl_dias, estado, reemplaza_id, embedding)
           VALUES (?,?,?,?,?,?,?,?,?,?,?,?,0,?,?,?,?)""",
        (usuario, c["texto"], c["tipo"], c["categoria"], c["origen"], c["fuente"], c["confianza"], c["importancia"],
         c.get("alcance", "global"), t, t, None, c.get("ttl_dias"), estado, c.get("actualiza_id"), json.dumps(emb)))
    DB.commit()
    return cur.lastrowid


def activar(mid: int) -> None:
    DB.execute("UPDATE memorias SET estado='activa' WHERE id=?", (mid,))
    DB.commit()


def reemplazar(viejo: int, nuevo: int) -> None:
    DB.execute("UPDATE memorias SET estado='reemplazada', motivo_estado=? WHERE id=?", (f"Actualizada por #{nuevo}", viejo))
    DB.commit()


def reforzar(mid: int, confianza: float) -> None:
    DB.execute("UPDATE memorias SET usos=usos+1, confianza=MAX(confianza, ?), actualizada=? WHERE id=?",
               (confianza, ahora().isoformat(), mid))
    DB.commit()


def olvidar(mid: int, motivo: str) -> None:
    DB.execute("UPDATE memorias SET estado='olvidada', motivo_estado=? WHERE id=?", (motivo, mid))
    DB.commit()


def borrar_usuario(usuario: str) -> None:
    DB.execute("DELETE FROM memorias WHERE usuario=?", (usuario,))
    DB.commit()


def obtener(mid: int) -> dict | None:
    r = DB.execute("SELECT * FROM memorias WHERE id=?", (mid,)).fetchone()
    return ficha(r) if r else None


def activas_para_extractor(usuario: str) -> list[dict]:
    return [{"id": r["id"], "texto": r["texto"]} for r in
            DB.execute("SELECT id, texto FROM memorias WHERE usuario=? AND estado='activa' ORDER BY id", (usuario,)).fetchall()]
