using MesaYa.Rag.Busqueda;
using MesaYa.Rag.Evaluacion;
using MesaYa.Rag.Ingesta;

namespace MesaYa.Rag;

/// <summary>Comandos de consola para preparar y revisar el índice sin abrir la web.</summary>
static class Comandos
{
    public static async Task<int> Ingestar(Ingestor ingestor)
    {
        try
        {
            var r = await ingestor.Ingestar(Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine($"{"Estrategia",-15} {"Chunks",7} {"Tokens prom.",13} {"Tokens máx.",12}");
            foreach (var e in r.Estrategias)
                Console.WriteLine($"{e.Estrategia,-15} {e.Chunks,7} {e.TokensPromedio,13} {e.TokensMaximo,12}");
            Console.WriteLine();
            Console.WriteLine($"{r.Documentos} documentos · {r.TextosEmbebidos} textos embebidos · {r.Duracion.TotalSeconds:F1} s");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            Console.Error.WriteLine($"No se pudo ingestar: {ex.Message}");
            return 1;
        }
    }

    const string UsoBuscar = """
        Uso: dotnet run -- buscar "pregunta" [opciones]
          --perfil clientes|personal|gerencia      (clientes)
          --tenant mesaya-ar|mesaya-uy             (mesaya-ar)
          --estrategia fijo-150|recursivo-512|estructural|contextual|hijo   (estructural)
          --modo hibrida|semantica|palabras-clave  (hibrida)
          --permisos pre|post|apagado              (pre)
          --incluir-obsoletos
          --rerank                                 (ordena los candidatos con el cross-encoder)
          -k 5  --candidatos 50  --sql
        """;

    public static async Task<int> Buscar(Buscador buscador, Reranker reranker, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.Error.WriteLine(UsoBuscar);
            return 1;
        }

        var c = new ConsultaBusqueda(args[0], "mesaya-ar", "clientes", K: 5);
        var verSql = false;
        var rerank = false;
        try
        {
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--sql") { verSql = true; continue; }
                if (args[i] == "--rerank") { rerank = true; continue; }
                string Valor() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de {args[i]}.");
                c = args[i] switch
                {
                    "--perfil" => c with { Perfil = Valor() },
                    "--tenant" => c with { Tenant = Valor() },
                    "--estrategia" => Estrategias.Todas.Contains(Valor()) ? c with { Estrategia = args[i] }
                        : throw new ArgumentException($"Estrategia desconocida: {args[i]}."),
                    "--modo" => c with
                    {
                        Modo = Valor() switch
                        {
                            "hibrida" => ModoBusqueda.Hibrida,
                            "semantica" => ModoBusqueda.Semantica,
                            "palabras-clave" => ModoBusqueda.PalabrasClave,
                            var m => throw new ArgumentException($"Modo desconocido: {m}.")
                        }
                    },
                    "--permisos" => c with
                    {
                        Permisos = Valor() switch
                        {
                            "pre" => FiltroPermisos.Pre,
                            "post" => FiltroPermisos.Post,
                            "apagado" => FiltroPermisos.Apagado,
                            var p => throw new ArgumentException($"Filtro de permisos desconocido: {p}.")
                        }
                    },
                    "--incluir-obsoletos" => c with { SoloVigentes = false },
                    "-k" => c with { K = int.Parse(Valor()) },
                    "--candidatos" => c with { Candidatos = int.Parse(Valor()) },
                    var x => throw new ArgumentException($"Opción desconocida: {x}.")
                };
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(UsoBuscar);
            return 1;
        }

        ResultadoBusqueda r;
        RerankHecho? orden = null;
        try
        {
            // Con reranker, los 20 primeros por RRF pasan por el cross-encoder, que elige los k finales.
            r = await buscador.Buscar(rerank ? c with { K = Pipeline.OpcionesTurno.FilasRanking } : c);
            if (rerank) orden = await reranker.Ordenar(c.Texto, r.Resultados);
        }
        catch (Exception ex) when (ex is HttpRequestException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            Console.Error.WriteLine($"No se pudo buscar: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"«{c.Texto}» · {c.Perfil}@{c.Tenant} · {c.Estrategia} · {c.Modo} · permisos {c.Permisos}{(c.SoloVigentes ? "" : " · con obsoletos")}");
        Console.WriteLine($"tsquery: {r.TsQuery}");
        Console.WriteLine($"embedding {r.TiempoEmbedding.TotalMilliseconds:F0} ms · SQL {r.TiempoSql.TotalMilliseconds:F0} ms");
        Console.WriteLine();
        Console.WriteLine($"{"#",2} {"sem",4} {"kw",4} {"RRF",7}  {"documento",-27} sección");
        if (orden is null)
            foreach (var x in r.Resultados) Fila(x, "");
        else
        {
            Console.WriteLine($"   reranker {orden.Modelo}: {orden.Resultados.Count} pares en {orden.Duracion.TotalMilliseconds:F0} ms");
            foreach (var x in orden.Resultados.Take(c.K))
                Fila(x.Original with { Posicion = x.Posicion }, $"  ← #{x.Original.Posicion} RRF · relevancia {x.Puntaje:F3}");
        }
        foreach (var x in r.Descartados) Fila(x, "  ✗ descartado por permisos");
        if (r.Agrupados.Count > 0)
            Console.WriteLine($"   ({r.Agrupados.Count} hijos agrupados con un padre que ya estaba más arriba)");
        if (r.Resultados.Count == 0)
            Console.WriteLine("   (sin resultados)");
        if (verSql) Console.WriteLine($"\n{r.Sql}");
        return 0;

        static void Fila(Resultado x, string nota)
        {
            var marca = x.Chunk.Vigente ? "" : " [deprecated]";
            var seccion = x.Chunk.SeccionParaElModelo.Split(" › ").Skip(1).FirstOrDefault() ?? "(intro)";
            Console.WriteLine($"{x.Posicion,2} {x.PosSemantica?.ToString() ?? "–",4} {x.PosPalabrasClave?.ToString() ?? "–",4} {x.Rrf,7:F4}  {x.Chunk.DocumentId,-27} {seccion}{marca}{nota}");
        }
    }

    /// <summary>Corre todas las configuraciones de la evaluación y muestra métricas y detalle por pregunta.</summary>
    public static async Task<int> Evaluar(Evaluador evaluador, string[] args)
    {
        var sinRerank = args.Contains("--sin-rerank");
        var configs = Evaluador.Configuraciones.Where(c => !sinRerank || !c.Rerank).ToList();
        var resultados = new List<ResultadoConfiguracion>();
        foreach (var c in configs)
        {
            Console.Write($"{c.Nombre,-38}");
            var r = await evaluador.Evaluar(c);
            resultados.Add(r);
            Console.WriteLine(r.Error is null
                ? $" hit@{Evaluador.K} {r.HitRate,5:P0} ({r.Aciertos}/{r.Evaluadas}) · MRR {r.Mrr:F2} · violaciones {r.Violaciones} · {r.Duracion.TotalSeconds:F1} s"
                : $" ERROR: {r.Error}");
        }

        Console.WriteLine();
        Console.WriteLine("Posición del fragmento esperado en el top 5 (· = no está, ✗ = entró un documento prohibido)");
        Console.Write($"{"",-5}");
        for (var i = 0; i < resultados.Count; i++) Console.Write($"{"c" + (i + 1),4}");
        Console.WriteLine();
        foreach (var p in evaluador.Preguntas())
        {
            Console.Write($"{p.Id,-5}");
            foreach (var r in resultados)
            {
                var x = r.Preguntas.FirstOrDefault(q => q.Pregunta.Id == p.Id);
                var celda = x is null ? "?" : x.Prohibidos.Count > 0 ? "✗" : x.Rango?.ToString() ?? (x.CuentaParaHitRate ? "·" : "ok");
                Console.Write($"{celda,4}");
            }
            Console.WriteLine($"   {p.Escenario} · {p.Pregunta}");
        }
        for (var i = 0; i < resultados.Count; i++) Console.WriteLine($"c{i + 1} = {resultados[i].Config.Nombre}");
        return resultados.Any(r => r.Error is not null) ? 1 : 0;
    }

    /// <summary>Muestra cómo corta cada estrategia un documento. No usa Ollama ni la base.</summary>
    public static int MostrarChunks(Ingestor ingestor, string[] args)
    {
        var documentos = ingestor.LeerCorpus();
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Uso: dotnet run -- chunks <document_id> [estrategia]");
            Console.Error.WriteLine($"Documentos: {string.Join(", ", documentos.Select(d => d.DocumentId))}");
            return 1;
        }
        var doc = documentos.FirstOrDefault(d => d.DocumentId == args[0]);
        if (doc is null)
        {
            Console.Error.WriteLine($"No existe el documento '{args[0]}'.");
            return 1;
        }

        foreach (var grupo in Estrategias.Cortar(doc).Where(c => args.Length < 2 || c.Estrategia == args[1]).GroupBy(c => c.Estrategia))
        {
            Console.WriteLine($"══ {grupo.Key} · {grupo.Count()} chunks ══");
            foreach (var c in grupo)
            {
                var padre = c.Padre is null ? "" : $" · padre: estructural #{c.Padre.Posicion}";
                Console.WriteLine($"── #{c.Posicion} · ~{c.Tokens} tokens · {string.Join(" | ", c.Secciones.Select(s => s.Length == 0 ? "(intro)" : s))}{padre}");
                Console.WriteLine(c.TextoEmbed);
                Console.WriteLine();
            }
        }
        return 0;
    }
}
