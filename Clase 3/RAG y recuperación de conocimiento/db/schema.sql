-- Esquema de la demo RAG de MesaYa (slide 09).
-- Vectores, texto y metadata en la misma tabla, con filtros SQL comunes.

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE chunks (
  id           bigserial PRIMARY KEY,

  -- Permisos (slide 07): filtros obligatorios que aplica el sistema, nunca el modelo.
  tenant_id    text    NOT NULL,                 -- 'mesaya-ar' | 'mesaya-uy'
  audiencia    text[]  NOT NULL,                 -- {clientes, personal, gerencia}

  -- Cada estrategia de chunking se indexa en paralelo para compararlas sin reingestar.
  estrategia   text    NOT NULL,                 -- 'fijo-150' | 'recursivo-512' | 'estructural' | 'contextual' | 'hijo'
  parent_id    bigint  REFERENCES chunks (id),   -- parent–child: el hijo apunta a su sección completa

  -- Origen (slide 07): para citar y rastrear cada fragmento.
  document_id  text    NOT NULL,
  titulo       text    NOT NULL,
  seccion      text,                             -- ruta: 'Política de cancelación de reservas › Plazos para cancelar sin cargo'
  fuente       text    NOT NULL,                 -- ruta del archivo en corpus/
  posicion     int     NOT NULL,                 -- orden del chunk dentro del documento

  -- Vigencia (slide 07).
  version      int     NOT NULL,
  fecha        date    NOT NULL,
  vigente      boolean NOT NULL,

  content      text         NOT NULL,          -- lo que ve el modelo
  texto_embed  text         NOT NULL,          -- lo que se embebe (en 'contextual' lleva el prefijo «Documento › Sección»)
  embedding    vector(1024) NOT NULL,          -- bge-m3
  metadata     jsonb        NOT NULL DEFAULT '{}',

  tsv tsvector GENERATED ALWAYS AS (to_tsvector('spanish', content)) STORED
);

-- Filtros que acompañan a todas las búsquedas.
CREATE INDEX chunks_filtro_idx ON chunks (estrategia, tenant_id, vigente);

-- Rareza de cada término para la búsqueda por palabras clave. Se crea vacía acá porque
-- db/funciones.sql la usa; el ingestor la llena después de cada carga (db/idf.sql).
CREATE TABLE lexemas_idf (
  estrategia text   NOT NULL,
  lexema     text   NOT NULL,
  df         int    NOT NULL,
  idf        float8 NOT NULL,
  PRIMARY KEY (estrategia, lexema)
);

-- Los índices HNSW y GIN se crean después de cargar los datos (db/indices.sql).
