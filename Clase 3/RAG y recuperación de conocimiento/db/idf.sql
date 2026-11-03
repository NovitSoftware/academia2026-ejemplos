-- Rareza de cada término (slide 10: «coincidencia exacta de términos, ponderada por su rareza»).
-- ts_rank_cd de PostgreSQL cuenta coincidencias y cercanía, pero no sabe que «tablet» aparece en
-- muchos chunks y «-4012» en uno solo. Esta tabla guarda el IDF de BM25 de cada lexema por estrategia
-- y el ingestor la recalcula después de cada carga.

CREATE TABLE IF NOT EXISTS lexemas_idf (
  estrategia text   NOT NULL,
  lexema     text   NOT NULL,
  df         int    NOT NULL,      -- en cuántos chunks aparece
  idf        float8 NOT NULL,      -- ln(1 + (N − df + 0,5) / (df + 0,5))
  PRIMARY KEY (estrategia, lexema)
);

TRUNCATE lexemas_idf;

INSERT INTO lexemas_idf (estrategia, lexema, df, idf)
SELECT t.estrategia, t.lexema, count(*) AS df,
       ln(1 + (n.total - count(*) + 0.5) / (count(*) + 0.5))
  FROM (SELECT estrategia, unnest(tsvector_to_array(tsv)) AS lexema FROM chunks) t
  JOIN (SELECT estrategia, count(*) AS total FROM chunks GROUP BY estrategia) n USING (estrategia)
 GROUP BY t.estrategia, t.lexema, n.total;

ANALYZE lexemas_idf;
