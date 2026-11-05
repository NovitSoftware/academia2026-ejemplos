"""Escenarios de la clase. Cada uno corre la misma pregunta con el control apagado y prendido."""

ESCENARIOS = [
    {"id": "A", "titulo": "El modelo inventa", "slides": "09–10", "pregunta_id": "q16", "panel": "grounding",
     "idea": "Sin evidencia, el modelo completa con lo que suena probable.",
     "variantes": [
         {"nombre": "Sin evidencia", "config": {"grounding": "sin"}},
         {"nombre": "Evidencia sin reglas", "config": {"grounding": "basico"}},
         {"nombre": "Grounding estricto", "config": {"grounding": "estricto"}, "bien": True}]},
    {"id": "B", "titulo": "Precision y recall según k", "slides": "02", "pregunta_id": "q01", "panel": "recuperacion",
     "idea": "Más k sube el recall y baja la precision.",
     "variantes": [
         {"nombre": "k = 1", "config": {"k": 1}},
         {"nombre": "k = 4", "config": {"k": 4}, "bien": True},
         {"nombre": "k = 10", "config": {"k": 10}}]},
    {"id": "C", "titulo": "Chunk crudo vs. buen chunk", "slides": "08", "pregunta_id": "q02", "panel": "recuperacion",
     "idea": "Cortar por tamaño separa la regla de su excepción.",
     "variantes": [
         {"nombre": "Fijo de 150 tokens", "config": {"chunking": "fijo", "k": 2}},
         {"nombre": "Por sección", "config": {"chunking": "seccion", "k": 2}, "bien": True}]},
    {"id": "D", "titulo": "Vigencia y jerarquía de fuentes", "slides": "11–12", "pregunta_id": "q17", "panel": "recuperacion",
     "idea": "La política vigente le gana al newsletter y a la versión 2021.",
     "variantes": [
         {"nombre": "Sin filtros", "config": {"k": 5, "solo_vigentes": False, "priorizar_oficiales": False}},
         {"nombre": "Solo vigentes", "config": {"k": 5, "solo_vigentes": True, "priorizar_oficiales": False}},
         {"nombre": "Vigentes + jerarquía", "config": {"k": 5, "solo_vigentes": True, "priorizar_oficiales": True}, "bien": True}]},
    {"id": "E", "titulo": "Evidencia insuficiente", "slides": "13", "pregunta_id": "q19", "panel": "grounding",
     "idea": "Que un fragmento hable del tema no significa que responda.",
     "variantes": [
         {"nombre": "Sin evidencia", "config": {"grounding": "sin"}},
         {"nombre": "Grounding estricto", "config": {"grounding": "estricto"}, "bien": True},
         {"nombre": "Estricto + umbral", "config": {"grounding": "estricto", "umbral": 0.6}, "bien": True}]},
    {"id": "F", "titulo": "¿Dónde se rompió?", "slides": "05–06", "pregunta_id": "q15", "panel": "diagnostico",
     "idea": "El árbol separa un problema de recuperación de uno de generación.",
     "variantes": [
         {"nombre": "Palabras clave", "config": {"busqueda": "palabras", "k": 2}},
         {"nombre": "Semántica", "config": {"busqueda": "semantica", "k": 2}, "bien": True}]},
    {"id": "G", "titulo": "Dos etapas: reranking", "slides": "03", "pregunta_id": "q05", "panel": "recuperacion",
     "idea": "Recuperar mucho y después ordenar fino.",
     "variantes": [
         {"nombre": "Solo etapa 1", "config": {"rerank": False, "k": 3, "perfil": "personal"}},
         {"nombre": "Con reranking", "config": {"rerank": True, "k": 3, "perfil": "personal"}, "bien": True}]},
    {"id": "H", "titulo": "¿RAG o no?", "slides": "16–18", "pregunta_id": "q20", "panel": "recuperacion",
     "idea": "Un número exacto no se busca en documentos.",
     "otras": ["q21", "q22"],
     "variantes": [
         {"nombre": "Todo a RAG", "config": {"enrutar": False}},
         {"nombre": "Con router", "config": {"enrutar": True}, "bien": True}]},
]
