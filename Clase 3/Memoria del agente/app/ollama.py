"""Cliente de Ollama con descarga de modelos y modo guion.

Cada llamada que termina bien se guarda en grabaciones/llm/. Con el modo guion
prendido, la misma llamada se responde desde esa grabación sin tocar Ollama:
sirve de plan B si Ollama falla en plena clase.
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import os
import time
from pathlib import Path

import httpx

OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://ollama:11434").rstrip("/")
MODELO_PREFERIDO = os.environ.get("MODELO", "qwen3.5:9b")
MODELO_EMBEDDINGS = os.environ.get("EMBEDDINGS", "bge-m3")
GRABACIONES = Path(os.environ.get("GRABACIONES", "/app/grabaciones")) / "llm"
GRABACIONES.mkdir(parents=True, exist_ok=True)


class SinGrabacion(Exception):
    pass


class Ollama:
    def __init__(self) -> None:
        self.cliente = httpx.AsyncClient(base_url=OLLAMA_URL, timeout=httpx.Timeout(300, connect=5))
        self.modelo = MODELO_PREFERIDO
        self.embeddings = MODELO_EMBEDDINGS
        self.modo_guion = False
        self.instalados: list[str] = []
        self.conectado = False
        self.descargas: dict[str, dict] = {}  # modelo -> {estado, completado, total}
        self.error: str | None = None

    # ---------- modelos ----------
    async def listar(self) -> list[str]:
        r = await self.cliente.get("/api/tags")
        r.raise_for_status()
        self.instalados = sorted(m["name"] for m in r.json().get("models", []))
        self.conectado = True
        return self.instalados

    def _resolver(self, nombre: str) -> str | None:
        """Busca el modelo instalado: exacto, con :latest o una variante (qwen3.5:9b → qwen3.5:9b-32k)."""
        for candidato in (nombre, f"{nombre}:latest"):
            if candidato in self.instalados:
                return candidato
        variantes = [m for m in self.instalados if m.startswith(nombre)]
        return variantes[0] if variantes else None

    async def preparar(self) -> None:
        """Espera a Ollama y descarga los modelos que falten. Corre en segundo plano al iniciar."""
        while True:
            try:
                await self.listar()
                break
            except Exception as e:  # Ollama todavía no levantó
                self.conectado = False
                self.error = f"No se puede conectar a Ollama en {OLLAMA_URL}"
                await asyncio.sleep(2)
        self.error = None
        for atributo in ("embeddings", "modelo"):
            nombre = getattr(self, atributo)
            encontrado = self._resolver(nombre)
            if not encontrado:
                await self._descargar(nombre)
                await self.listar()
                encontrado = self._resolver(nombre) or nombre
            setattr(self, atributo, encontrado)

    async def _descargar(self, nombre: str) -> None:
        for intento in range(1, 6):  # el registro de Ollama a veces responde 503: se reintenta
            await self._descargar_una_vez(nombre)
            if self.descargas[nombre]["estado"] == "listo":
                self.error = None
                return
            await asyncio.sleep(5 * intento)

    async def _descargar_una_vez(self, nombre: str) -> None:
        estado = self.descargas[nombre] = {"estado": "descargando", "completado": 0, "total": 0}
        try:
            async with self.cliente.stream("POST", "/api/pull", json={"model": nombre}, timeout=None) as r:
                async for linea in r.aiter_lines():
                    if not linea:
                        continue
                    d = json.loads(linea)
                    if "total" in d:
                        estado["total"] = d["total"]
                        estado["completado"] = d.get("completed", 0)
                    if d.get("error"):
                        raise RuntimeError(d["error"])
            estado["estado"] = "listo"
        except Exception as e:
            estado["estado"] = f"error: {e}"
            self.error = f"No se pudo descargar {nombre}: {e}"

    def listo(self) -> bool:
        return self.conectado and bool(self._resolver(self.modelo)) and bool(self._resolver(self.embeddings))

    def estado(self) -> dict:
        return {
            "url": OLLAMA_URL,
            "conectado": self.conectado,
            "modelo": self.modelo,
            "embeddings": self.embeddings,
            "instalados": [m for m in self.instalados if "bge" not in m and "embed" not in m],
            "descargas": self.descargas,
            "error": self.error,
            "modo_guion": self.modo_guion,
        }

    # ---------- grabaciones ----------
    @staticmethod
    def _clave(cuerpo: dict) -> str:
        return hashlib.sha256(json.dumps(cuerpo, sort_keys=True, ensure_ascii=False).encode()).hexdigest()[:24]

    def _leer(self, clave: str):
        f = GRABACIONES / f"{clave}.json"
        if f.exists():
            return json.loads(f.read_text())
        return None

    def _guardar(self, clave: str, cuerpo: dict, respuesta) -> None:
        GRABACIONES.mkdir(parents=True, exist_ok=True)  # por si alguien borró la carpeta con la app andando
        f = GRABACIONES / f"{clave}.json"
        f.write_text(json.dumps({"pedido": cuerpo, "respuesta": respuesta,
                                 "grabado": time.strftime("%Y-%m-%d %H:%M")}, ensure_ascii=False))

    # ---------- llamadas ----------
    async def chat(self, mensajes: list[dict], formato: dict | None = None,
                   temperatura: float = 0.0, num_ctx: int = 8192, max_tokens: int | None = None) -> dict:
        """Devuelve {texto, tokens_prompt, tokens_respuesta, ms, grabado}."""
        cuerpo = {
            "model": self.modelo, "messages": mensajes, "stream": False, "think": False,
            "options": {"temperature": temperatura, "seed": 42, "num_ctx": num_ctx,
                        # Tope de salida: evita que un JSON estructurado quede en un bucle.
                        "num_predict": max_tokens or (900 if formato else 500)},
        }
        if formato:
            cuerpo["format"] = formato
        clave = self._clave(cuerpo)
        if self.modo_guion:
            g = self._leer(clave)
            if not g:
                raise SinGrabacion("No hay una grabación de esta llamada. Apagá el modo guion o corré la misma consulta una vez en vivo.")
            return {**g["respuesta"], "grabado": g["grabado"]}
        t0 = time.perf_counter()
        r = await self.cliente.post("/api/chat", json=cuerpo)
        if r.status_code >= 400:
            raise RuntimeError(f"Ollama respondió {r.status_code}: {r.text[:200]}")
        d = r.json()
        salida = {
            "texto": d["message"]["content"],
            "tokens_prompt": d.get("prompt_eval_count", 0),
            "tokens_respuesta": d.get("eval_count", 0),
            "ms": round((time.perf_counter() - t0) * 1000),
        }
        self._guardar(clave, cuerpo, salida)
        return {**salida, "grabado": None}

    async def chat_json(self, mensajes: list[dict], esquema: dict, **kw) -> tuple[dict, dict]:
        r = await self.chat(mensajes, formato=esquema, **kw)
        try:
            return json.loads(r["texto"]), r
        except json.JSONDecodeError:
            return {}, r

    async def embeber(self, textos: list[str]) -> list[list[float]]:
        cuerpo = {"model": self.embeddings, "input": textos}
        clave = "emb-" + self._clave(cuerpo)
        g = self._leer(clave)
        if g:  # los embeddings siempre se reutilizan: son deterministas
            return g["respuesta"]
        if self.modo_guion:
            raise SinGrabacion("No hay embeddings grabados para esta consulta.")
        r = await self.cliente.post("/api/embed", json=cuerpo)
        r.raise_for_status()
        vectores = r.json()["embeddings"]
        if len(textos) <= 4:  # solo se graban las consultas; el corpus va a su propio caché
            self._guardar(clave, cuerpo, vectores)
        return vectores


ollama = Ollama()
