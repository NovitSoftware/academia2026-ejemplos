"""¿RAG o no? (slides 16 a 18).

Antes de buscar, el router decide adónde va la pregunta:
- documentos → RAG sobre el corpus.
- datos      → SQL sobre la base de reservas (números exactos, agregados).
- en_vivo    → API de disponibilidad (cambia minuto a minuto).
- directo    → ficha corta que entra entera en el prompt.
"""
from __future__ import annotations

import datetime as dt
import random
import re
import sqlite3

from ollama import ollama

SUCURSALES = {"Palermo": 60, "Belgrano": 40, "Núñez": 45}  # cubiertos por turno de 30 minutos
HORARIOS = ["20:00", "20:30", "21:00", "21:30", "22:00", "22:30"]

FICHA = """MesaYa es una cadena de restaurantes con tres sucursales en Buenos Aires: Palermo, Belgrano y Núñez.
Teléfono central: (011) 4800-1234. Web y app: mesaya.example. Correo de ayuda: ayuda@mesaya.example.
Reservas desde la app, la web o por teléfono."""

ESQUEMA = """reservas(id TEXT, sucursal TEXT, fecha TEXT 'AAAA-MM-DD', hora TEXT 'HH:MM', personas INTEGER,
         estado TEXT -- 'confirmada' | 'cumplida' | 'cancelada' | 'no_show',
         canal TEXT -- 'app' | 'web' | 'telefono')
ventas_diarias(sucursal TEXT, fecha TEXT 'AAAA-MM-DD', cubiertos INTEGER, facturacion INTEGER -- en pesos)
Valores de sucursal: 'Palermo', 'Belgrano', 'Núñez'."""


def hoy() -> dt.date:
    return dt.date.today()


def crear_base() -> sqlite3.Connection:
    """Datos sintéticos y deterministas: 30 días hacia atrás y 14 hacia adelante."""
    db = sqlite3.connect(":memory:", check_same_thread=False)
    db.executescript("""
        CREATE TABLE reservas(id TEXT, sucursal TEXT, fecha TEXT, hora TEXT, personas INTEGER, estado TEXT, canal TEXT);
        CREATE TABLE ventas_diarias(sucursal TEXT, fecha TEXT, cubiertos INTEGER, facturacion INTEGER);""")
    rnd = random.Random(2026)
    n = 0
    for delta in range(-30, 15):
        dia = hoy() + dt.timedelta(days=delta)
        finde = dia.weekday() >= 4
        for suc in SUCURSALES:
            cubiertos = 0
            for _ in range(rnd.randint(14, 22) + (12 if finde else 0)):
                n += 1
                personas = rnd.choice([2, 2, 2, 3, 4, 4, 5, 6, 8])
                if delta < 0:
                    estado = rnd.choices(["cumplida", "cancelada", "no_show"], [82, 12, 6])[0]
                else:
                    estado = rnd.choices(["confirmada", "cancelada"], [92, 8])[0]
                if estado == "cumplida":
                    cubiertos += personas
                db.execute("INSERT INTO reservas VALUES (?,?,?,?,?,?,?)",
                           (f"RSV-{20000 + n}", suc, dia.isoformat(), rnd.choice(HORARIOS), personas, estado,
                            rnd.choice(["app", "app", "web", "telefono"])))
            if delta < 0:
                db.execute("INSERT INTO ventas_diarias VALUES (?,?,?,?)",
                           (suc, dia.isoformat(), cubiertos, cubiertos * rnd.randint(27, 34) * 1000))
    # Hoy a las 21 Belgrano está lleno: la respuesta correcta es ofrecer otro horario.
    for i in range(8):
        db.execute("INSERT INTO reservas VALUES (?,?,?,?,?,?,?)",
                   (f"RSV-{30000 + i}", "Belgrano", hoy().isoformat(), "21:00", 5, "confirmada", "app"))
    db.commit()
    return db


DB = crear_base()


def disponibilidad(sucursal: str, fecha: str, personas: int) -> dict:
    """La "API en vivo". Responde lo que hay en este momento."""
    cap = SUCURSALES.get(sucursal)
    if not cap:
        return {"error": f"Sucursal desconocida: {sucursal}"}
    turnos = []
    for h in HORARIOS:
        ocupado = DB.execute("SELECT COALESCE(SUM(personas),0) FROM reservas WHERE sucursal=? AND fecha=? AND hora=? AND estado='confirmada'",
                             (sucursal, fecha, h)).fetchone()[0]
        libre = max(0, cap - ocupado)
        turnos.append({"hora": h, "lugares_libres": libre, "disponible": libre >= personas})
    return {"sucursal": sucursal, "fecha": fecha, "personas": personas, "turnos": turnos,
            "consultado": dt.datetime.now().strftime("%H:%M:%S")}


ESQUEMA_RUTA = {
    "type": "object",
    "properties": {
        "ruta": {"type": "string", "enum": ["documentos", "datos", "en_vivo", "directo"]},
        "motivo": {"type": "string"},
        "sucursal": {"type": "string"}, "fecha": {"type": "string"}, "personas": {"type": "integer"},
    },
    "required": ["ruta", "motivo"],
}


async def decidir(pregunta: str) -> dict:
    hoy_txt = f"{hoy().isoformat()} ({['lunes','martes','miércoles','jueves','viernes','sábado','domingo'][hoy().weekday()]})"
    mensajes = [
        {"role": "system", "content": f"""Clasificá la pregunta de un usuario de MesaYa (cadena de restaurantes) según de dónde sale la respuesta:
- documentos: políticas, reglas, procedimientos, menú, accesibilidad, manuales del personal. Texto que cambia poco.
- datos: números exactos o agregados sobre reservas o ventas pasadas (cuántas, total, promedio, ranking). Va a SQL.
- en_vivo: disponibilidad de mesas o lugares libres para una fecha y hora. Cambia minuto a minuto. Va a la API.
- directo: saludo, teléfono, web, mail de contacto o qué es MesaYa. Está en una ficha corta.
Si es en_vivo, completá sucursal (Palermo, Belgrano o Núñez), fecha (AAAA-MM-DD) y personas. Hoy es {hoy_txt}."""},
        {"role": "user", "content": pregunta},
    ]
    datos, r = await ollama.chat_json(mensajes, ESQUEMA_RUTA)
    datos.setdefault("ruta", "documentos")
    datos["ms"] = r["ms"]
    return datos


async def generar_sql(pregunta: str) -> tuple[str, dict]:
    hoy_txt = f"{hoy().isoformat()} ({['lunes','martes','miércoles','jueves','viernes','sábado','domingo'][hoy().weekday()]})"
    mensajes = [
        {"role": "system", "content": f"Escribí una sola consulta SQLite (solo SELECT) que responda la pregunta. Esquema:\n{ESQUEMA}\n"
                                      f"Hoy es {hoy_txt}. Usá date('now', ...) o fechas literales. Las reservas pasadas que se concretaron tienen estado 'cumplida'."},
        {"role": "user", "content": pregunta},
    ]
    datos, r = await ollama.chat_json(mensajes, {"type": "object", "properties": {"sql": {"type": "string"}}, "required": ["sql"]})
    return datos.get("sql", "").strip().rstrip(";"), r


def ejecutar_sql(sql: str) -> dict:
    if not re.match(r"^\s*(select|with)\b", sql, re.I) or re.search(r"\b(insert|update|delete|drop|alter|attach|pragma)\b", sql, re.I):
        return {"error": "Solo se permiten consultas de lectura (SELECT)."}
    try:
        cur = DB.execute(sql)
        columnas = [c[0] for c in cur.description]
        filas = cur.fetchmany(30)
        return {"columnas": columnas, "filas": filas}
    except Exception as e:
        return {"error": str(e)}
