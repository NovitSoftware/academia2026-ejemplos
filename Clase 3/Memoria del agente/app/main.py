"""MesaYa · Memoria del agente (Unidad 4)."""
from __future__ import annotations

import asyncio
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

import agente
import memoria as mem
from escenarios import CONVERSACIONES, ESCENARIOS
from ollama import SinGrabacion, ollama

BASE = Path(__file__).resolve().parent
app = FastAPI(title="MesaYa · Memoria del agente")
app.mount("/static", StaticFiles(directory=BASE / "static"), name="static")
TRAZAS: dict[str, list[dict]] = {}

CONFIG_BASE = {"memoria": True, "estrategia": "resumen", "k": 4, "escritura": "segundo_plano",
               "filtrar_sensibles": True, "validar_origen": True}


@app.on_event("startup")
async def iniciar():
    asyncio.create_task(ollama.preparar())


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
    return {**ollama.estado(), "listo": ollama.listo() or ollama.modo_guion}


class Config(BaseModel):
    modelo: str | None = None
    modo_guion: bool | None = None


@app.post("/api/config")
def config(c: Config):
    if c.modelo:
        ollama.modelo = c.modelo
    if c.modo_guion is not None:
        ollama.modo_guion = c.modo_guion
    return ollama.estado()


@app.get("/api/catalogo")
def catalogo():
    return {"escenarios": ESCENARIOS, "usuarios": agente.USUARIOS, "config": CONFIG_BASE}


def vista(usuario: str) -> dict:
    s = agente.sesion(usuario)
    return {"usuario": usuario, "sesion": s["n"], "turnos": s["turnos"], "estado": s["estado"], "archivados": s["archivados"],
            "resumen": s["resumen"], "memoria": mem.listar(usuario), "pendientes": agente.PENDIENTES.get(usuario, 0),
            "reloj": {"dias": mem.RELOJ["dias"], "hoy": mem.ahora().date().isoformat()},
            "trazas": [{"i": i, "sesion": t["sesion"], "escritura": t["escritura"]} for i, t in enumerate(TRAZAS.get(usuario, []))]}


@app.get("/api/sesion/{usuario}")
def ver_sesion(usuario: str):
    return vista(usuario)


class Turno(BaseModel):
    usuario: str
    mensaje: str
    config: dict = {}


@app.post("/api/turno")
async def turno(t: Turno):
    if not (ollama.listo() or ollama.modo_guion):
        raise HTTPException(503, "Ollama todavía no está listo. Mirá el indicador de arriba a la derecha.")
    cfg = {**CONFIG_BASE, **t.config}
    try:
        traza = await agente.turno(t.usuario, t.mensaje, cfg)
    except SinGrabacion as e:
        raise HTTPException(404, str(e))
    except Exception as e:
        raise HTTPException(502, f"{type(e).__name__}: {e}")
    TRAZAS.setdefault(t.usuario, []).append(traza)
    return {"traza": traza, "indice": len(TRAZAS[t.usuario]) - 1, "vista": vista(t.usuario)}


class Usuario(BaseModel):
    usuario: str


@app.post("/api/sesion/nueva")
def nueva(u: Usuario):
    agente.nueva_sesion(u.usuario)
    return vista(u.usuario)


@app.post("/api/reiniciar")
def reiniciar(u: Usuario):
    """Borra memoria, sesiones y trazas de todos los usuarios y vuelve el reloj a hoy."""
    for usuario in agente.USUARIOS:
        agente.reiniciar(usuario)
        TRAZAS.pop(usuario, None)
    mem.RELOJ["dias"] = 0
    return vista(u.usuario)


class Reloj(BaseModel):
    usuario: str
    dias: int  # 0 = volver a hoy; otro valor = avanzar


@app.post("/api/reloj")
def reloj(r: Reloj):
    mem.RELOJ["dias"] = 0 if r.dias == 0 else mem.RELOJ["dias"] + r.dias
    vencidas = mem.vencer(r.usuario)
    return {**vista(r.usuario), "vencidas": vencidas}


@app.delete("/api/memoria/{usuario}/{mid}")
def borrar(usuario: str, mid: int):
    mem.olvidar(mid, "Borrada a mano desde el panel")
    return vista(usuario)


class Precarga(BaseModel):
    usuario: str
    conversacion: str


@app.post("/api/precargar")
def precargar(p: Precarga):
    s = agente.sesion(p.usuario)
    s["turnos"] = [{"rol": r, "texto": t} for r, t in CONVERSACIONES[p.conversacion]]
    s["resumen"], s["resumen_hasta"] = None, 0
    return vista(p.usuario)
