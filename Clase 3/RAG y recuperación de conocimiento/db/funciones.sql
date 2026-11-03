-- Consultas de búsqueda de la demo (slides 10 y 11) como funciones,
-- para usarlas desde la app y mostrarlas en vivo con psql.

-- websearch_to_tsquery une los términos con AND: una pregunta en lenguaje natural
-- («la tablet me tira E-4012, ¿qué hago?») casi nunca tiene todas sus palabras en un
-- mismo chunk. Para preguntas conversacionales se usan los mismos lexemas unidos con OR
-- y se deja que ts_rank_cd ordene.
--
-- Dato para mostrar en clase: con la configuración 'spanish', «E-4012» se indexa como
-- '-4012' porque «e» es una palabra vacía (la conjunción). Como la consulta pasa por el
-- mismo parser, igual coincide. Probalo con: SELECT to_tsvector('spanish', 'error E-4012');
CREATE OR REPLACE FUNCTION consulta_or(texto text)
RETURNS tsquery
LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE
    WHEN q::text = '' THEN q
    ELSE replace(q::text, ' & ', ' | ')::tsquery
  END
  FROM websearch_to_tsquery('spanish', texto) AS q;
$$;

-- Búsqueda semántica con pre-filtering de permisos y vigencia.
CREATE OR REPLACE FUNCTION buscar_semantica(
  p_embedding  vector(1024),
  p_tenant     text,
  p_perfil     text,
  p_estrategia text,
  p_solo_vigentes boolean DEFAULT true,
  p_k          int DEFAULT 50
)
RETURNS TABLE (id bigint, posicion bigint, distancia double precision)
LANGUAGE sql STABLE AS $$
  SELECT c.id,
         ROW_NUMBER() OVER (ORDER BY c.embedding <=> p_embedding),
         c.embedding <=> p_embedding
  FROM chunks c
  WHERE c.tenant_id = p_tenant
    AND p_perfil = ANY (c.audiencia)
    AND c.estrategia = p_estrategia
    AND (c.vigente OR NOT p_solo_vigentes)
  ORDER BY c.embedding <=> p_embedding
  LIMIT p_k;
$$;

-- Búsqueda por palabras clave con los mismos filtros (slide 10).
-- El GIN filtra los chunks que tienen algún término; el puntaje es BM25 simplificado:
-- la suma del IDF de los términos de la consulta presentes en el chunk (db/idf.sql).
-- No satura por frecuencia ni normaliza por largo: alcanza para que un código raro como
-- «E-4012» pese más que una palabra frecuente como «tablet».
DROP FUNCTION IF EXISTS buscar_palabras_clave(text, text, text, text, boolean, int);
CREATE FUNCTION buscar_palabras_clave(
  p_texto      text,
  p_tenant     text,
  p_perfil     text,
  p_estrategia text,
  p_solo_vigentes boolean DEFAULT true,
  p_k          int DEFAULT 50
)
RETURNS TABLE (id bigint, posicion bigint, puntaje double precision)
LANGUAGE sql STABLE AS $$
  WITH terminos AS (
    SELECT DISTINCT unnest(tsvector_to_array(to_tsvector('spanish', p_texto))) AS lexema
  ), puntajes AS (
    SELECT c.id, sum(i.idf) AS puntaje
      FROM chunks c
      CROSS JOIN consulta_or(p_texto) AS q
      JOIN terminos t ON t.lexema = ANY (tsvector_to_array(c.tsv))
      JOIN lexemas_idf i ON i.estrategia = c.estrategia AND i.lexema = t.lexema
     WHERE c.tenant_id = p_tenant
       AND p_perfil = ANY (c.audiencia)
       AND c.estrategia = p_estrategia
       AND (c.vigente OR NOT p_solo_vigentes)
       AND c.tsv @@ q
     GROUP BY c.id
  )
  SELECT p.id, ROW_NUMBER() OVER (ORDER BY p.puntaje DESC, p.id), p.puntaje
    FROM puntajes p
   ORDER BY p.puntaje DESC, p.id
   LIMIT p_k;
$$;

-- Búsqueda híbrida con Reciprocal Rank Fusion (slide 11):
-- RRF(d) = Σ 1 / (60 + posición). Combina posiciones, no puntajes.
-- Devuelve también las dos posiciones para la pestaña Ranking.
CREATE OR REPLACE FUNCTION buscar_hibrida(
  p_embedding  vector(1024),
  p_texto      text,
  p_tenant     text,
  p_perfil     text,
  p_estrategia text,
  p_solo_vigentes boolean DEFAULT true,
  p_k          int DEFAULT 20,
  p_candidatos int DEFAULT 50,
  p_rrf_k      int DEFAULT 60
)
RETURNS TABLE (id bigint, pos_semantica bigint, pos_palabras_clave bigint, rrf numeric)
LANGUAGE sql STABLE AS $$
  WITH sem AS (
    SELECT * FROM buscar_semantica(p_embedding, p_tenant, p_perfil, p_estrategia, p_solo_vigentes, p_candidatos)
  ), kw AS (
    SELECT * FROM buscar_palabras_clave(p_texto, p_tenant, p_perfil, p_estrategia, p_solo_vigentes, p_candidatos)
  )
  SELECT COALESCE(sem.id, kw.id),
         sem.posicion,
         kw.posicion,
         COALESCE(1.0 / (p_rrf_k + sem.posicion), 0)
       + COALESCE(1.0 / (p_rrf_k + kw.posicion), 0) AS rrf
  FROM sem FULL JOIN kw ON sem.id = kw.id
  ORDER BY rrf DESC
  LIMIT p_k;
$$;
