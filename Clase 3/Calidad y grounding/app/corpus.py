"""Lectura del corpus de MesaYa y las dos estrategias de chunking.

- seccion: un chunk por sección (##), con título del documento y de la sección
  al principio. Es el "buen chunk" de la slide 08: se entiende solo.
- fijo: ventanas de ~150 tokens (600 caracteres) sin encabezados. Es el
  "chunk crudo": corta reglas por la mitad y pierde a qué documento pertenece.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

CORPUS = Path(__file__).resolve().parent.parent / "corpus"
if not CORPUS.exists():
    CORPUS = Path("/app/corpus")


@dataclass
class Documento:
    id: str
    titulo: str
    area: str
    version: int
    fecha: str
    estado: str
    audiencia: list[str]
    tenant: str
    ruta: str
    secciones: list[tuple[str, str]]  # (título de sección, texto)

    @property
    def autoridad(self) -> str:
        """Jerarquía de fuentes de la slide 12."""
        if self.estado != "vigente":
            return "historica"
        if self.area in ("marketing", "administracion"):
            return "comunicacion"
        return "oficial"


@dataclass
class Chunk:
    id: str
    doc: Documento
    texto: str
    secciones: list[str] = field(default_factory=list)

    def ficha(self) -> dict:
        d = self.doc
        return {
            "id": self.id, "document_id": d.id, "titulo": d.titulo,
            "seccion": " · ".join(self.secciones), "secciones": self.secciones,
            "estado": d.estado, "version": d.version, "fecha": d.fecha,
            "autoridad": d.autoridad, "audiencia": d.audiencia, "tenant": d.tenant,
            "texto": self.texto, "tokens": max(1, len(self.texto) // 4),
        }


def _front_matter(texto: str) -> tuple[dict, str]:
    m = re.match(r"---\n(.*?)\n---\n(.*)", texto, re.S)
    meta: dict = {}
    for linea in m.group(1).splitlines():
        k, _, v = linea.partition(":")
        v = v.strip()
        if v.startswith("["):
            v = [x.strip() for x in v.strip("[]").split(",") if x.strip()]
        meta[k.strip()] = v
    return meta, m.group(2)


def leer_corpus() -> list[Documento]:
    docs = []
    for f in sorted(CORPUS.rglob("*.md")):
        meta, cuerpo = _front_matter(f.read_text(encoding="utf-8"))
        secciones, actual, lineas = [], "Introducción", []
        for linea in cuerpo.splitlines():
            if linea.startswith("# "):
                continue
            if linea.startswith("## "):
                if "".join(lineas).strip():
                    secciones.append((actual, "\n".join(lineas).strip()))
                actual, lineas = linea[3:].strip(), []
            else:
                lineas.append(linea)
        if "".join(lineas).strip():
            secciones.append((actual, "\n".join(lineas).strip()))
        docs.append(Documento(
            id=meta["document_id"], titulo=meta["titulo"], area=meta.get("area", ""),
            version=int(meta.get("version", 1)), fecha=meta.get("fecha", ""),
            estado=meta.get("estado", "vigente"), audiencia=meta.get("audiencia", []),
            tenant=meta.get("tenant_id", "mesaya-ar"), ruta=str(f.relative_to(CORPUS)),
            secciones=secciones,
        ))
    return docs


def chunks_por_seccion(docs: list[Documento]) -> list[Chunk]:
    salida = []
    for d in docs:
        for i, (titulo, texto) in enumerate(d.secciones):
            salida.append(Chunk(f"{d.id}#{i + 1}", d, f"{d.titulo} › {titulo}\n{texto}", [titulo]))
    return salida


def chunks_fijos(docs: list[Documento], largo: int = 600) -> list[Chunk]:
    salida = []
    for d in docs:
        # Texto corrido, sin títulos: así llega un PDF exportado "tal cual".
        partes, mapa = [], []  # mapa: (inicio, fin, sección)
        pos = 0
        for titulo, texto in d.secciones:
            plano = re.sub(r"\s+", " ", texto).strip() + " "
            partes.append(plano)
            mapa.append((pos, pos + len(plano), titulo))
            pos += len(plano)
        corrido = "".join(partes)
        inicio, n = 0, 0
        while inicio < len(corrido):
            fin = min(len(corrido), inicio + largo)
            if fin < len(corrido):
                corte = corrido.rfind(" ", inicio, fin)
                fin = corte if corte > inicio else fin
            texto = corrido[inicio:fin].strip()
            if texto:
                n += 1
                secs = [s for a, b, s in mapa if a < fin and b > inicio]
                salida.append(Chunk(f"{d.id}~{n}", d, texto, secs))
            inicio = fin + 1
    return salida


DOCUMENTOS = leer_corpus()
CHUNKS = {"seccion": chunks_por_seccion(DOCUMENTOS), "fijo": chunks_fijos(DOCUMENTOS)}
