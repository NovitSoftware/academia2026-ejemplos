-- Índices de búsqueda (slide 09). El ingestor los crea después de cargar los chunks:
-- construir HNSW sobre la tabla llena es más rápido que mantenerlo fila por fila.

CREATE INDEX IF NOT EXISTS chunks_embedding_hnsw
  ON chunks USING hnsw (embedding vector_cosine_ops)
  WITH (m = 16, ef_construction = 64);

CREATE INDEX IF NOT EXISTS chunks_tsv_gin
  ON chunks USING gin (tsv);

ANALYZE chunks;

-- En la sesión de búsqueda:
--   SET hnsw.ef_search = 40;                 -- más ef_search, más recall y más latencia
--   SET hnsw.iterative_scan = relaxed_order; -- pgvector ≥ 0.8: sigue recorriendo el grafo
--                                            -- cuando el WHERE descarta candidatos, para no
--                                            -- devolver menos de k resultados con pre-filtering
